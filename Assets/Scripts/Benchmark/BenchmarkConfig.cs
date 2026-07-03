using System;
using System.Collections.Generic;
using Tiles;

namespace Benchmark
{
    public enum RunStatus
    {
        Pending,
        Running,
        Completed,
        Failed
    }

    /// <summary>
    /// A named set of goals that can be configured in the Inspector.
    /// E.g. "1 Goal" = just Wood 100, "3 Goals" = Wood 100 + Stone 80 + Food 60.
    /// </summary>
    [Serializable]
    public class GoalPreset
    {
        public string label;                      // e.g. "1goal", "3goals", "5goals"
        public List<GoalConfig> goals = new();
    }

    [Serializable]
    public class GoalConfig
    {
        public GlobalGoalType type;
        public string targetResource; // ResourceType name, e.g. "Wood"
        public int targetAmount;

        public GlobalGoal ToGlobalGoal()
        {
            var goal = new GlobalGoal
            {
                type = type,
                targetAmount = targetAmount,
                isCompleted = false,
                completionTime = 0f
            };

            if (type == GlobalGoalType.ResourceAmount)
            {
                if (Enum.TryParse<ResourceType>(targetResource, true, out var rt))
                    goal.targetResource = rt;
            }

            return goal;
        }
    }

    [Serializable]
    public class BenchmarkRunConfig
    {
        public string runId;
        public string modelName;
        public string mapFile;       // .twcmap filename
        public string mapSize;       // "small" or "large" (metadata label)
        public List<GoalConfig> goals = new();
        public int repetition;       // 1 or 2
        public long cutoffTicks;     // max sim-ticks before forced abort
        public RunStatus status = RunStatus.Pending;
        public string completedAt;   // ISO 8601 timestamp
        public string abortReason;   // "goals_reached", "cutoff", "error:...", "skipped"
    }

    [Serializable]
    public class BenchmarkManifest
    {
        public string createdAt;
        public List<BenchmarkRunConfig> runs = new();
    }

    // ── Lightweight snapshot of dynamic state at time of LLM call ────────

    [Serializable]
    public class InputStateSnapshot
    {
        public int wood;
        public int stone;
        public int seeds;
        public int food;
        public int capacity;
        public int villagerCount;
        public List<string> idleVillagers = new();
        public List<string> activeGoals = new();
        public int buildingCount;
    }

    [Serializable]
    public class ParsedAssignment
    {
        public string villager;
        public string job;
        public string buildingType;
        public int targetX;
        public int targetY;
        public int gatherAmount;
        public int restUntilEnergy;
        public string reason;
    }

    [Serializable]
    public class ParsedGoal
    {
        public string type;
        public string resource;
        public int amount;
        public string priority;
        public string description;
    }

    [Serializable]
    public class TokenCount
    {
        public int prompt;
        public int response;
        public int total;
    }

    [Serializable]
    public class LLMDecisionLogEntry
    {
        public long simTick;
        public string triggerReason;
        public InputStateSnapshot inputState;
        public string rawResponse;
        public List<ParsedAssignment> parsedAssignments = new();
        public List<ParsedGoal> parsedGoals = new();
        public TokenCount tokenCount;
        public double responseTimeSeconds;
        public bool success;
        public string errorMessage;
    }

    // ── Data passed via the new LLMController event ──────────────────────

    public class BatchDecisionLog
    {
        public long simTick;
        public string triggerReason;
        public InputStateSnapshot inputState;
        public string rawResponse;
        public Dictionary<string, JobDecision> parsedDecisions;
        public List<RawGoalDecision> parsedGoals;
        public LLMMetrics metrics;
    }

    // ── World event entry ────────────────────────────────────────────────

    [Serializable]
    public class WorldEventEntry
    {
        public long simTick;
        public string eventType;
        public string details; // JSON sub-object as string for flexibility
    }

    // ── Run metadata ─────────────────────────────────────────────────────

    [Serializable]
    public class MapStatistics
    {
        public int totalTiles;
        public int grassTiles;
        public int forestTiles;
        public int mountainTiles;
        public int waterTiles;
        public int coastTiles;
        public int treeCount;
        public int stoneCount;
        public int seedCount;
        public int mineCount;
        public int cropCount;
    }

    [Serializable]
    public class RunMetadata
    {
        public string runId;
        public string modelName;
        public string mapFile;
        public string mapSize;
        public int mapSeed;
        public List<GoalConfig> goals = new();
        public int repetition;
        public long cutoffTicks;
        public string startTime;
        public string endTime;
        public string abortReason;
        public long finalTick;
        public float elapsedGameTimeSeconds;
        public float elapsedRealTimeSeconds;
        public MapStatistics mapStats;
        public LLMSessionStats sessionStats;
    }
}
