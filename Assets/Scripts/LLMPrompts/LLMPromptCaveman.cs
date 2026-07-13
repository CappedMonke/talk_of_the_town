using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Caveman LLM prompt — maximally token-efficient telegraphic reference. Keeps
/// the SAME ordered DECISION PROCEDURE, CONSTRAINTS, and worked example as the
/// Normal and Lean variants so the benchmark isolates compression level only.
/// Intended for high-capability cloud models (e.g. gpt-oss:20b/120b) that can
/// infer the gaps and where token cost matters more than disambiguation.
/// </summary>
public static class LLMPromptCaveman
{
    public static string BuildBatchSystemPrompt(
        List<string> availableJobs,
        int villagerCount,
        (float drain, float walkDrain, float recovery) energyRates = default,
        string buildingCosts = "")
    {
        string jobList = string.Join(", ", availableJobs);
        string costs = string.IsNullOrEmpty(buildingCosts)
            ? "Farm=25w10s House=20w15s10food Stockpile=10w"
            : buildingCosts;

        string drain   = energyRates.drain.ToString("F1", CultureInfo.InvariantCulture);
        string walk    = energyRates.walkDrain.ToString("F1", CultureInfo.InvariantCulture);
        string recover = energyRates.recovery.ToString("F1", CultureInfo.InvariantCulture);
        int recoverySecs = energyRates.recovery > 0f ? (int)(100f / energyRates.recovery) : 500;

        string jsonExample = @"{""assignments"":[{""villager"":""<NAME>"",""job"":""<JOB>"",""buildingType"":""<TYPE>"",""targetX"":<X>,""targetY"":<Y>,""gatherAmount"":<N>,""restUntilEnergy"":<N>,""reason"":""<why>""}],""goals"":[{""type"":""GatherResource"",""resource"":""Wood"",""amount"":80,""priority"":""High"",""description"":""wood""}]}";

        return $@"Assign job to ALL {villagerCount} villagers. Goal=finish RESEARCHER GOALS fast. Fast>tidy. No idle waste.
JOBS: {jobList}, IDLE

JOBS REF:
Lumberjack: TREE->wood (regrows)
Miner: STONE(fast) or MINE SHAFT(infinite/slow). STONE first. permanent shaft miner only @10+ villagers
Builder: FREE BUILD SITE only. cost consumed first. {costs}. House->spawns villager(+5w5s5seed10food). 1 builder unless rich
Farmer: grass NEAR done Farm. 2seed/field->5food+1-3seed. NO Farm=NO fields(blocked). not on Farm tile. 2-3 farms enough
SeedGatherer: node->seeds
IDLE: rest. energy0-100 -{drain}/s work -{walk}/s walk +{recover}/s idle. <30%slow <5%stop. full~{recoverySecs}s. set restUntilEnergy to auto-resume

DECISION (each villager top-down, FIRST match wins, stop):
1. energy<5% ->IDLE restUntilEnergy80. ok even if all idle (cant work=not deadlock)
2. energy<30% AND someone else covers top task ->IDLE restUntilEnergy60
3. RESEARCHER GOAL:
   pop goal: 0 Farms->Builder Farm(no food cost). elif food LOW->1 Farmer. elif free house slots=0->Builder House. else->gather House bottleneck
   resource goal: only that resource's nodes
4. Farm exists AND seeds>=10 AND food not NEARLY FULL/BLOCKED ->1 Farmer(max1 on pop goal)
5. any [LOW] resource ->matching gatherer, gatherAmount=clear shortage
6. else->gather scarcest non-[SURPLUS]. never leave [NEEDS ASSIGNMENT] unassigned

CONSTRAINTS:
- 1 villager/coord. never 2 same tile. same resource->diff nodes
- [KEEP]=stay unless resource [SURPLUS]. only reassign [NEEDS ASSIGNMENT]. no job swaps w/o reason
- Builder needs buildingType + FREE BUILD SITE coord. never on occupied tile
- failed build coord -> never reuse. pick different FREE BUILD SITE
- only coords from live context lists

gatherAmount: set exact units so villager stops+frees up (no overfill). omit=indefinite.
goals(opt): ""goals"" replaces existing. type=GatherResource(Wood/Stone/Seed/Food)/ReachPopulation, amount, priority Low/Normal/High/Critical, description.
reason=how job advances researcher goal (or village need).

EXAMPLE: goal=pop4. Ada[NEEDS ASSIGNMENT] Ben[NEEDS ASSIGNMENT]. Wood30 Stone12 Seeds8[LOW] Food0[LOW]. Farms0. free slots0.
correct(0 farms+pop goal->Farm FIRST: house needs food needs farm; other clears seeds for next-turn planting):
{{""assignments"":[{{""villager"":""Ada"",""job"":""Builder"",""buildingType"":""Farm"",""targetX"":12,""targetY"":7,""reason"":""pop->house->food->farm; none exists build first""}},{{""villager"":""Ben"",""job"":""SeedGatherer"",""targetX"":20,""targetY"":15,""gatherAmount"":12,""reason"":""seeds LOW; stock for farmer after farm done""}}]}}

JSON ONLY, all {villagerCount} villagers:
{jsonExample}";
    }

    public static string BuildSingleSystemPrompt(List<string> availableJobs)
    {
        string jobList = string.Join(", ", availableJobs);
        string jsonExample = @"{""job"":""<JOB>"",""targetX"":<X>,""targetY"":<Y>,""reason"":""<why>""}";
        return $@"Pick job+location. JOBS:{jobList},IDLE. JSON:{jsonExample}";
    }
}
