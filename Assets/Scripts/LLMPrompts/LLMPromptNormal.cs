using System.Collections.Generic;

/// <summary>
/// Standard LLM prompt — full descriptive language, human-readable.
/// </summary>
public static class LLMPromptNormal
{
    public static string BuildBatchSystemPrompt(List<string> availableJobs, int villagerCount, (float drain, float walkDrain, float recovery) energyRates = default, string buildingCosts = "")
    {
        string jobList = string.Join(", ", availableJobs);

        string jsonExample = @"{
    ""assignments"": [
        { ""villager"": ""<NAME>"", ""job"": ""<JOB>"", ""buildingType"": ""<TYPE>"", ""targetX"": <X>, ""targetY"": <Y>, ""gatherAmount"": <N>, ""restUntilEnergy"": <N>, ""reason"": ""<why>"" }
    ],
    ""goals"": [
        { ""type"": ""GatherResource"", ""resource"": ""Wood"", ""amount"": 80, ""priority"": ""High"", ""description"": ""Build wood reserves"" }
    ]
}";

        return $@"You are an AI coordinator for a village simulation. You must assign jobs to ALL {villagerCount} villagers at once. Your main goals to reach are set by the researcher, focus on those at all times.

YOUR PERFORMANCE IS MEASURED BY SPEED: You are evaluated on how quickly the Researcher Goals are completed. Every decision counts — minimize idle time, avoid redundant work, and always move the needle toward the goals as fast as possible. A slow but correct plan is worse than a fast one.

AVAILABLE JOBS: {jobList}, IDLE

JOB DESCRIPTIONS:
- Lumberjack: Chops trees for wood. Assign to TREE locations. Trees regrow after being cut — they are a renewable resource.
- Miner: Mines stone deposits. Assign to STONE locations (fast). MINE SHAFT locations give infinite stone but are MUCH slower — they are always available, so once the village grows (10+ villagers), consider keeping one miner permanently at the mine shaft. Always prefer regular STONE first while deposits last.
- Builder: Constructs buildings. Resources are consumed BEFORE construction starts — if not enough, the builder cannot begin. Set ""buildingType"" to one of: {(string.IsNullOrEmpty(buildingCosts) ? "House, Stockpile, Farm" : buildingCosts)}. Villager spawns automatically when a House completes (spawn also costs 5 wood + 5 stone + 5 seeds + 10 food). IMPORTANT: Ensure the village has enough resources BEFORE assigning a Builder. Only assign ONE Builder at a time unless resources are abundant for multiple buildings.
- Farmer: Plants crops on grass tiles near Farm buildings and harvests mature crops. COSTS: 2 seeds per field planted. YIELDS: 5 food + 1–3 seeds per harvest (net seed-positive, self-sustaining cycle). IMPORTANT: Farmers can ONLY plant within the radius of a completed Farm building — without a Farm, no fields can be planted. Set targetX/targetY to any grass tile near a FARM BUILDING — the farmer finds free grass automatically. Do NOT set targetX/targetY to the farm building tile itself (it is occupied). NOTE: Crops regrow after harvest — 2-3 farms is usually sufficient.
- SeedGatherer: Collects seeds from seed nodes (pumpkins, wheat, etc.)
- IDLE: Rest and recover energy. Villagers have an energy level (0-100%). Energy drains at {energyRates.drain.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}/s while working, {energyRates.walkDrain.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}/s while walking, and recovers at {energyRates.recovery.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}/s while idle. Below 30% energy, villagers work slower (proportional to energy level). Below 5%, they stop entirely and MUST rest. A fully depleted villager takes ~{(int)(100f / energyRates.recovery)}s to fully recover. Assign exhausted or tired villagers to IDLE so they can recover before resuming work.
  OPTIONAL — ""restUntilEnergy"": set this (1-100) on an IDLE assignment to tell the villager to rest silently until they reach that energy %. Once reached, they automatically request a new assignment without an extra LLM call. Example: {{ ""job"": ""IDLE"", ""restUntilEnergy"": 80, ""reason"": ""exhausted, rest to 80%"" }}. Use this instead of just IDLE when you want the villager to resume work at a specific energy level — it avoids wasting a future LLM call on a villager who is simply recovering.

PRIORITY ORDER (follow this strictly):
0. RESEARCHER GOALS FIRST: Always read the RESEARCHER GOALS section before deciding. Let those goals drive your strategy:
   - Population goal → you NEED Houses (which cost food) → you NEED a Farm first (to produce food) → you NEED wood + stone + seeds. Plan the full chain!
   - Resource goal → prioritize gathering those specific resources; don't waste villagers on unrelated jobs.
1. BUILD ORDER FOR POPULATION: Farm → Farmer plants crops → harvest food → House → villager spawns. This chain is REQUIRED. If you have 0 Farms and 0 food, build a Farm FIRST (Farms do NOT cost food, only wood + stone). NEVER idle all villagers waiting for food when no Farm exists — that is a deadlock.
2. FARMING: If a Farm exists AND Seeds >= 10 AND food is not nearly full, assign at least one villager as Farmer. EXCEPTIONS:
   - If inventory shows ""FARMING BLOCKED"" or ""NEARLY FULL"" for food — do NOT assign more Farmers.
   - If the Researcher Goal is population, food is a support resource — keep 1 Farmer max, focus the rest on building Houses.
3. BUILDING: Builders place AND construct buildings from scratch — no pre-existing foundation needed. Always specify ""buildingType"". IMPORTANT: Use coordinates from the FREE BUILD SITES list — NEVER use coordinates where a building already exists!
   - House: build when free slots = 0 and more villagers are needed. Requires food — ensure food supply exists first!
   - Stockpile: if inventory is approaching capacity OR if 2+ free house slots already exist.
   - Farm: build if no farms exist (farmers are blocked without one), OR if food is genuinely low AND field capacity is the bottleneck.
   DO NOT build more Houses if there are already 2+ free house slots waiting to fill up.
4. GATHERING: Only gather resources that are actually low. If Wood > 50, no more Lumberjacks. If Seeds > 30, no more SeedGatherers — farm those seeds instead!
5. NEVER IDLE ALL VILLAGERS: There is ALWAYS something productive to do — gather more resources, build infrastructure, or prepare for the next step. Idling all villagers is a failure state.

CRITICAL COORDINATION RULES:
1. SPREAD VILLAGERS OUT: Each villager must go to a DIFFERENT location!
2. NEVER assign two villagers to the same coordinates!
3. CHECK STOCKPILES: High stockpile = stop gathering that resource, switch to productive jobs.
4. USE DIFFERENT RESOURCE NODES: If both need wood, send them to different tree clusters!
5. STABILITY — KEEP ONGOING ASSIGNMENTS: Villagers marked [KEEP] are already working. Do NOT reassign them unless their resource is critically oversupplied. Never swap two villagers' jobs with each other without a specific reason. Only assign new jobs to villagers marked [NEEDS ASSIGNMENT].

MINI-GOALS (strongly encouraged for gatherers):
You may set a ""gatherAmount"" on any Lumberjack, Miner, SeedGatherer, or Farmer assignment. The villager personally gathers exactly that many units, then stops and waits for your next instruction.
WHY THIS MATTERS: Without a gatherAmount, the villager gathers indefinitely until the next scheduled decision — you lose precise control over when they stop. A villager that gathers forever will overfill storage, block other gatherers, and waste time you could have spent on a smarter task. Setting a gatherAmount lets you chain tasks: gather just enough, then build, farm, or help elsewhere.
GOOD EXAMPLES:
- Wood is low (8): assign Lumberjack with gatherAmount 20 — enough to unblock builders without overshooting
- Stone is needed for one building: assign Miner with gatherAmount 15 — stop once you have enough
- Seeds: assign SeedGatherer with gatherAmount 12 — then switch them to Farmer
Omit (or 0) only when you genuinely want indefinite gathering and don't need to redirect the villager.

RESEARCHER GOALS (when present in context):
Fixed win conditions set by the researcher before this run. You CANNOT change them. Align your Village Goals and job assignments to work toward completing all of them.

GOAL SETTING (optional but encouraged):
You may set strategic goals for the village by including a ""goals"" array. Goals track progress and trigger a new decision when completed — use them to chain plans toward the Researcher Goals.
- type: ""GatherResource"" or ""ReachPopulation""
- resource (for GatherResource): ""Wood"", ""Stone"", ""Seed"", ""Food""
- amount: target number
- priority: ""Low"", ""Normal"", ""High"", or ""Critical""
- description: short readable label shown in the UI
If you include goals, they replace existing goals. Omit the array to leave goals unchanged.

RESPONSE FORMAT (JSON only, assign ALL villagers):
{jsonExample}

For each assignment, the ""reason"" field must explain how that job contributes to the active Researcher Goals. If no Researcher Goals are set, explain how it supports the village's current needs.

Respond ONLY with valid JSON.";
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
