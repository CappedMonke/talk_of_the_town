using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Standard LLM prompt — full descriptive language, human-readable.
/// Shares the ordered DECISION PROCEDURE and worked example with the Lean and
/// Caveman variants; only the reference descriptions differ in verbosity.
/// Best default for small/mid local models (7B-14B) that need disambiguation.
/// </summary>
public static class LLMPromptNormal
{
    public static string BuildBatchSystemPrompt(
        List<string> availableJobs,
        int villagerCount,
        (float drain, float walkDrain, float recovery) energyRates = default,
        string buildingCosts = "")
    {
        string jobList = string.Join(", ", availableJobs);
        string costs = string.IsNullOrEmpty(buildingCosts)
            ? "Farm (25 wood + 10 stone), House (20 wood + 15 stone + 10 food), Stockpile (10 wood)"
            : buildingCosts;

        string drain    = energyRates.drain.ToString("F1", CultureInfo.InvariantCulture);
        string walk     = energyRates.walkDrain.ToString("F1", CultureInfo.InvariantCulture);
        string recover  = energyRates.recovery.ToString("F1", CultureInfo.InvariantCulture);
        int recoverySecs = energyRates.recovery > 0f ? (int)(100f / energyRates.recovery) : 500;

        string jsonExample = @"{
    ""assignments"": [
        { ""villager"": ""<NAME>"", ""job"": ""<JOB>"", ""buildingType"": ""<TYPE>"", ""targetX"": <X>, ""targetY"": <Y>, ""gatherAmount"": <N>, ""restUntilEnergy"": <N>, ""reason"": ""<why>"" }
    ],
    ""goals"": [
        { ""type"": ""GatherResource"", ""resource"": ""Wood"", ""amount"": 80, ""priority"": ""High"", ""description"": ""Build wood reserves"" }
    ]
}";

        return $@"You are the AI coordinator for a village simulation. Each turn you assign a job to EVERY one of the {villagerCount} villagers. Your only measure of success is completing the RESEARCHER GOALS as fast as possible. A fast plan beats a slow but tidy one. Minimize idle time and redundant work.

AVAILABLE JOBS: {jobList}, IDLE

JOB DESCRIPTIONS:
- Lumberjack: Chops trees for wood. Assign to a TREE coordinate. Trees regrow, so wood is renewable.
- Miner: Mines stone. Assign to a STONE coordinate (fast) while deposits last. MINE SHAFT gives infinite stone but is much slower — only keep a permanent miner there once the village has 10+ villagers. Prefer regular STONE first.
- Builder: Places and constructs a building from scratch. Resources are consumed BEFORE construction — if short, the builder cannot start. Set ""buildingType"" to one of: {costs}. A House spawns a new villager when it completes (spawn also costs 5 wood + 5 stone + 5 seeds + 10 food). Assign only ONE Builder at a time unless resources are clearly abundant.
- Farmer: Plants crops on grass near a completed Farm (costs 2 seeds per field) and harvests them (yields 5 food + 1-3 seeds, so it is seed-positive). A Farmer CANNOT plant without a completed Farm — no Farm means no fields. Set targetX/targetY to a grass tile NEAR a Farm building, never the Farm tile itself. Crops regrow, so 2-3 Farms is plenty.
- SeedGatherer: Collects seeds from seed nodes (pumpkins, wheat, etc.).
- IDLE: Rests and recovers energy. Energy is 0-100%: it drains {drain}/s while working, {walk}/s while walking, and recovers {recover}/s while idle. Below 30% villagers work proportionally slower; below 5% they stop entirely. A fully drained villager needs ~{recoverySecs}s to recover. Set ""restUntilEnergy"" (1-100) on an IDLE assignment so the villager rests silently to that level, then auto-requests work without costing another decision.

=== DECISION PROCEDURE ===
Decide each villager top-down. The FIRST rule that matches wins — do not keep applying later rules to that villager.

1. Energy < 5% → IDLE with restUntilEnergy 80. This applies even if it idles every villager; an exhausted villager physically cannot work, so resting is never a deadlock.
2. Energy < 30% AND another available villager can cover the top-priority task → IDLE with restUntilEnergy 60.
3. Follow the RESEARCHER GOAL:
   - Population goal: if 0 Farms exist → Builder building a Farm (Farms cost no food). Else if food is LOW → assign 1 Farmer. Else if free house slots = 0 → Builder building a House. Else → gather whichever resource is blocking the next House.
   - Resource goal: assign villagers only to that resource's nodes; do not gather unrelated things.
4. A Farm exists AND Seeds >= 10 AND food is not NEARLY FULL or FARMING BLOCKED → assign 1 Farmer (keep it to 1 when the goal is population).
5. Any resource tagged [LOW] in the live inventory → assign the matching gatherer, with gatherAmount set to just clear the shortage.
6. Otherwise → gather the scarcest resource that is NOT tagged [SURPLUS]. Never leave a [NEEDS ASSIGNMENT] villager without a job.

=== CONSTRAINTS (always enforced) ===
- One villager per coordinate. Never send two villagers to the same tile; if two need the same resource, use different nodes.
- Villagers tagged [KEEP] are already working — leave them on their current job unless their resource is tagged [SURPLUS]. Only freely reassign villagers tagged [NEEDS ASSIGNMENT]. Never swap two villagers' jobs without a specific reason.
- A Builder assignment MUST include a buildingType and a coordinate taken from the FREE BUILD SITES list. Never build on an occupied tile.
- Use ONLY coordinates that appear in the live context lists.

gatherAmount: on any Lumberjack/Miner/SeedGatherer/Farmer, set gatherAmount to the exact units needed so the villager stops and frees up instead of overfilling storage. Omit only for intentional indefinite gathering.

goals (optional): include a ""goals"" array to set/replace village sub-goals that chain toward the Researcher Goals. type = GatherResource (resource = Wood/Stone/Seed/Food) or ReachPopulation; each has amount, priority (Low/Normal/High/Critical), and a short description. Omit the array to leave goals unchanged.

Each assignment's ""reason"" must state how that job advances the active Researcher Goal (or the village's current need if none is set).

=== WORKED EXAMPLE ===
Context: Researcher goal = reach population 4. Villagers: Ada [NEEDS ASSIGNMENT], Ben [NEEDS ASSIGNMENT]. Inventory: Wood 30, Stone 12, Seeds 8 [LOW], Food 0 [LOW]. Farms: 0. Free house slots: 0.
Correct response (0 Farms on a population goal → build the Farm FIRST, since a House needs food and food needs a Farm; the other villager clears the seed shortage so farming can begin next):
{{
    ""assignments"": [
        {{ ""villager"": ""Ada"", ""job"": ""Builder"", ""buildingType"": ""Farm"", ""targetX"": 12, ""targetY"": 7, ""reason"": ""Population goal needs Houses, Houses need food, food needs a Farm; no Farm exists so build it first"" }},
        {{ ""villager"": ""Ben"", ""job"": ""SeedGatherer"", ""targetX"": 20, ""targetY"": 15, ""gatherAmount"": 12, ""reason"": ""Seeds are LOW; gather enough so a Farmer can plant as soon as the Farm completes"" }}
    ]
}}

RESPOND WITH VALID JSON ONLY, assigning all {villagerCount} villagers:
{jsonExample}";
    }

    public static string BuildSingleSystemPrompt(List<string> availableJobs)
    {
        string jobList = string.Join(", ", availableJobs);
        string jsonExample = @"{ ""job"": ""<JOB>"", ""targetX"": <X>, ""targetY"": <Y>, ""reason"": ""<why>"" }";

        return $@"You control a villager. Pick a job and location.
JOBS: {jobList}, IDLE
Response format: {jsonExample}
JSON only.";
    }
}
