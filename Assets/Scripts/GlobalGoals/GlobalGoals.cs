using System;
using System.Collections.Generic;
using System.Text;
using Tiles;
using UnityEngine;

/// <summary>
/// Manages researcher-defined win conditions for a simulation run.
/// Global Goals are set by the player before the run starts and cannot be changed
/// by the LLM. When all goals are met the simulation stops.
/// </summary>
public class GlobalGoals : MonoBehaviour
{
    // ── Balancing constants — adjust these during playtesting ─────────────
    public const int MaxResourceAmount = 500;
    public const int MaxBuildingCount  = 20;
    public const int MaxPopulation     = 20;
    // ─────────────────────────────────────────────────────────────────────

    private const string LogCategory = "GlobalGoals";
    void LogInfo(string msg)    => GameLog.LogInfo(LogCategory, msg, this);
    void LogWarning(string msg) => GameLog.LogWarning(LogCategory, msg, this);

    public static GlobalGoals Instance { get; private set; }

    private readonly List<GlobalGoal> _goals = new();

    public IReadOnlyList<GlobalGoal> Goals => _goals;
    public bool HasGoals => _goals.Count > 0;

    /// <summary>Fired when a single global goal is met.</summary>
    public event Action<GlobalGoal> OnGlobalGoalCompleted;

    /// <summary>Fired when every global goal has been met — stops the simulation.</summary>
    public event Action OnAllGlobalGoalsCompleted;

    void Awake()
    {
        if (Instance == null)
            Instance = this;
        else
        {
            Destroy(gameObject);
        }
    }

    void OnEnable()
    {
        OnAllGlobalGoalsCompleted += HandleAllGoalsCompleted;
    }

    void OnDisable()
    {
        OnAllGlobalGoalsCompleted -= HandleAllGoalsCompleted;
    }

    void Update()
    {
        if (_goals.Count == 0) return;

        bool anyNewlyCompleted = false;
        foreach (var goal in _goals)
        {
            if (goal.isCompleted) continue;
            if (!IsGoalComplete(goal)) continue;

            goal.isCompleted    = true;
            goal.completionTime = Time.time;
            LogInfo($"Global goal reached: {goal.Description} at t={goal.completionTime:F1}s");
            OnGlobalGoalCompleted?.Invoke(goal);
            anyNewlyCompleted = true;
        }

        if (anyNewlyCompleted && AllGoalsMet())
        {
            LogInfo("All global goals completed — stopping simulation.");
            OnAllGlobalGoalsCompleted?.Invoke();
        }
    }

    /// <summary>
    /// Called from GoalsMenu before a run starts. Replaces any previously set goals.
    /// </summary>
    public void SetGoals(List<GlobalGoal> goals)
    {
        _goals.Clear();
        _goals.AddRange(goals);
        LogInfo($"Global goals set: {goals.Count} goal(s) — {string.Join(", ", goals.ConvertAll(g => g.Description))}");
    }

    /// <summary>
    /// Clears all global goals (called when returning to main menu / resetting).
    /// </summary>
    public void ClearGoals()
    {
        _goals.Clear();
    }

    /// <summary>
    /// Returns a formatted string injected into every LLM prompt as RESEARCHER GOALS.
    /// </summary>
    public string GetGoalsForPrompt()
    {
        if (_goals.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("These are fixed objectives set by the researcher. You CANNOT change them. Work toward ALL of them:");
        foreach (var goal in _goals)
        {
            string status   = goal.isCompleted ? "[DONE] " : "[ ] ";
            string progress = GetGoalProgress(goal);
            sb.AppendLine($"- {status}{goal.Description} {progress}");
        }
        return sb.ToString().TrimEnd();
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private bool AllGoalsMet() => _goals.TrueForAll(g => g.isCompleted);

    private bool IsGoalComplete(GlobalGoal goal)
    {
        if (VillageState.Instance == null) return false;

        switch (goal.type)
        {
            case GlobalGoalType.ResourceAmount:
                return VillageState.Instance.GetResource(goal.targetResource) >= goal.targetAmount;

            case GlobalGoalType.BuildingCount:
                return CountAllCompletedBuildings() >= goal.targetAmount;

            case GlobalGoalType.PopulationCount:
                return VillageState.Instance.Villagers.Count >= goal.targetAmount;

            default:
                return false;
        }
    }

    private string GetGoalProgress(GlobalGoal goal)
    {
        if (VillageState.Instance == null) return "";

        switch (goal.type)
        {
            case GlobalGoalType.ResourceAmount:
                int res = VillageState.Instance.GetResource(goal.targetResource);
                return $"({res}/{goal.targetAmount})";

            case GlobalGoalType.BuildingCount:
                int built = CountAllCompletedBuildings();
                return $"({built}/{goal.targetAmount})";

            case GlobalGoalType.PopulationCount:
                return $"({VillageState.Instance.Villagers.Count}/{goal.targetAmount})";

            default:
                return "";
        }
    }

    private static int CountAllCompletedBuildings()
    {
        int count = 0;
        var buildings = UnityEngine.Object.FindObjectsByType<Buildings.Building>(FindObjectsSortMode.None);
        foreach (var b in buildings)
        {
            if (b != null && b.IsFinished())
                count++;
        }
        return count;
    }

    private void HandleAllGoalsCompleted()
    {
        // Stop the simulation by setting game speed to 0.
        // The logging system (next sprint) can hook into OnAllGlobalGoalsCompleted
        // and OnGlobalGoalCompleted for structured run data.
        if (VillageState.Instance != null)
            VillageState.Instance.SetGameSpeed(0f);
    }
}

// ── Data types ────────────────────────────────────────────────────────────────

public enum GlobalGoalType
{
    ResourceAmount,
    BuildingCount,
    PopulationCount
}

[Serializable]
public class GlobalGoal
{
    public GlobalGoalType type;
    public ResourceType   targetResource; // used when type == ResourceAmount
    public int            targetAmount;
    public bool                 isCompleted;
    public float                completionTime;   // Time.time when completed

    public string Description => type switch
    {
        GlobalGoalType.ResourceAmount  => $"Gather {targetAmount} {targetResource}",
        GlobalGoalType.BuildingCount   => $"Build {targetAmount} building(s)",
        GlobalGoalType.PopulationCount => $"Reach {targetAmount} villagers",
        _                              => "Unknown goal"
    };
}
