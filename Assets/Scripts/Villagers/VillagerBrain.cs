using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Bridges LLM batch decisions to JobHandler.
/// Listens for batch decisions instead of requesting individually.
/// </summary>
[RequireComponent(typeof(Villager))]
[RequireComponent(typeof(JobHandler))]
public class VillagerBrain : MonoBehaviour
{
    [Header("Decision Timing")]
    [SerializeField] private float checkInterval = 1f;
    [SerializeField] private float idleTimeout = 5f;

    [Header("Available Jobs")]
    [SerializeField] private List<JobType> availableJobTypes = new List<JobType>();

    [Header("Debug")]
    public JobDecision lastDecision;
    public string currentState = "Initializing";

    private const string LogCategory = "VillagerBrain";
    void LogError(string msg)   => GameLog.LogError(LogCategory, msg, this);
    void LogWarning(string msg) => GameLog.LogWarning(LogCategory, msg, this);
    void LogEvent(string msg)   => GameLog.LogEvent(LogCategory, msg, this);
    void LogInfo(string msg)    => GameLog.LogInfo(LogCategory, msg, this);
    void LogVerbose(string msg) => GameLog.LogVerbose(LogCategory, msg, this);

    private Villager _villager;
    private JobHandler _jobHandler;
    private float _idleTime;
    private float _lastAppliedDecisionTime;
    private bool _waitingForBatch;
    private int _restUntilEnergy; // 0 = no target; >0 = wait until villager reaches this % before requesting
    public bool IsResting => _restUntilEnergy > 0;

    // Mini-goal tracking
    private int _gatherGoalAmount;
    private int _personalGathered;

    void Awake()
    {
        _villager = GetComponent<Villager>();
        _jobHandler = GetComponent<JobHandler>();
    }

    void Start()
    {
        if (availableJobTypes.Count == 0)
        {
            availableJobTypes.AddRange(Resources.LoadAll<JobType>(""));
            LogInfo($"Loaded {availableJobTypes.Count} job types");
        }

        // Subscribe to batch decisions
        if (LLMController.Instance != null)
        {
            LLMController.Instance.OnBatchDecisionMade += OnBatchDecisionReceived;
        }

        _jobHandler.OnResourceDeposited += OnResourceDeposited;

        StartCoroutine(WaitForLLMAndSubscribe());
        StartCoroutine(MonitorLoop());
    }

    void OnDestroy()
    {
        if (LLMController.Instance != null)
        {
            LLMController.Instance.OnBatchDecisionMade -= OnBatchDecisionReceived;
        }

        _jobHandler.OnResourceDeposited -= OnResourceDeposited;
    }

    private IEnumerator WaitForLLMAndSubscribe()
    {
        while (LLMController.Instance == null)
        {
            currentState = "Waiting for LLM...";
            yield return new WaitForSeconds(0.5f);
        }

        // Subscribe if not already
        LLMController.Instance.OnBatchDecisionMade -= OnBatchDecisionReceived;
        LLMController.Instance.OnBatchDecisionMade += OnBatchDecisionReceived;

        LogInfo($"{_villager.villagerName} subscribed to batch decisions");
    }

    private void OnResourceDeposited(int amount)
    {
        if (_gatherGoalAmount <= 0) return;
        _personalGathered += amount;
    }

    private void OnBatchDecisionReceived(Dictionary<string, JobDecision> decisions)
    {
        if (decisions.TryGetValue(_villager.villagerName, out var decision))
        {
            ApplyDecision(decision);
        }
        else
        {
            // Not included — likely spawned after the batch was built. Fast-track the idle
            // timer so MonitorLoop requests a new batch on the very next tick.
            LogVerbose($"{_villager.villagerName} not in batch decision (newly spawned), requesting next batch");
            if (_jobHandler.currentJob == null)
                _idleTime = idleTimeout;
        }
    }

    private IEnumerator MonitorLoop()
    {
        // Wait for systems
        while (LLMController.Instance == null || !LLMController.Instance.IsReady)
        {
            yield return new WaitForSeconds(0.5f);
        }

        while (true)
        {
            yield return new WaitForSeconds(checkInterval);

            // Check if we need to request a decision
            if (!_waitingForBatch && ShouldRequestDecision())
            {
                if (LLMController.Instance.UseBatchDecisions)
                {
                    _waitingForBatch = true;
                    LLMController.Instance.TriggerDecision($"{_villager.villagerName} needs assignment");
                }
                else
                {
                    // Fallback to individual decision
                    yield return RequestIndividualDecision();
                }
            }

            UpdateState();
        }
    }

    private bool ShouldRequestDecision()
    {
        // Check if villager is exhausted (energy < 5) — force idle
        if (_villager.Energy < 5f && _jobHandler.currentJob != null)
        {
            LogEvent($"{_villager.villagerName} exhausted (energy {_villager.EnergyPercent}%) — forcing idle to rest");
            _jobHandler.AssignJob(null);
            currentState = "Exhausted — resting";
            _idleTime = 0f;
            return false; // Don't request a new decision yet, let them rest
        }

        // Check if an energy rest target has been reached
        if (_restUntilEnergy > 0)
        {
            if (_villager.EnergyPercent >= _restUntilEnergy)
            {
                LogEvent($"{_villager.villagerName} energy recovered to {_villager.EnergyPercent}% (target was {_restUntilEnergy}%) — requesting new assignment");
                _restUntilEnergy = 0;
                _idleTime = 0f;
                return true;
            }
            return false; // Still resting — suppress all other triggers
        }

        // Check if a gather mini-goal has been met
        if (_gatherGoalAmount > 0 && _personalGathered >= _gatherGoalAmount)
        {
            LogEvent($"{_villager.villagerName} mini-goal met: deposited {_personalGathered}/{_gatherGoalAmount}");
            ClearGatherGoal();
            _jobHandler.AssignJob(null);
            currentState = "Mini-goal complete";
            _idleTime = 0f;
            return true;
        }

        // No job assigned
        if (_jobHandler.currentJob == null)
        {
            _idleTime += checkInterval;
            if (_idleTime >= idleTimeout)
            {
                _idleTime = 0f;
                return true;
            }
            return false;
        }

        // Check job status
        string status = _jobHandler.ActiveJobLogic?.GetCurrentStatus() ?? "Idle";

        // Job is waiting/stuck
        if (status.Contains("Waiting") || status.Contains("No ") || status.Contains("not found") || status.Contains("Storage full"))
        {
            _idleTime += checkInterval;
            if (_idleTime >= idleTimeout)
            {
                _idleTime = 0f;
                return true;
            }
        }
        else
        {
            _idleTime = 0f;
        }

        return false;
    }

    private void UpdateState()
    {
        if (_jobHandler.currentJob == null)
        {
            if (_villager.Energy < 30f)
                currentState = $"Resting (energy {_villager.EnergyPercent}%)";
            else
                currentState = $"Idle ({_idleTime:F1}s)";
        }
        else
        {
            string status = _jobHandler.ActiveJobLogic?.GetCurrentStatus() ?? "Working";
            if (_villager.Energy < 30f)
                status += $" [tired {_villager.EnergyPercent}%]";
            currentState = status;
        }
    }

    private IEnumerator RequestIndividualDecision()
    {
        currentState = "Thinking...";

        var jobNames = new List<string>();
        foreach (var jt in availableJobTypes)
        {
            if (jt != null)
                jobNames.Add(jt.JobName);
        }

        var task = LLMController.Instance.RequestJobDecision(_villager, jobNames);

        while (!task.IsCompleted)
            yield return null;

        ApplyDecision(task.Result);
    }

    private void ApplyDecision(JobDecision decision)
    {
        if (decision == null) return;

        lastDecision = decision;
        _lastAppliedDecisionTime = Time.time;
        _idleTime = 0f;
        _waitingForBatch = false;

        string targetInfo = decision.hasTargetArea ? $" at ({decision.targetX},{decision.targetY})" : "";
        string goalInfo = decision.gatherAmount > 0 ? $" [goal: {decision.gatherAmount}]" : "";
        LogEvent($"{_villager.villagerName} -> {decision.jobName}{targetInfo}{goalInfo}: {decision.reason}");

        ClearGatherGoal();
        _restUntilEnergy = 0;

        if (decision.IsIdle || !decision.success)
        {
            _jobHandler.AssignJob(null);
            if (decision.restUntilEnergy > 0)
            {
                _restUntilEnergy = Mathf.Clamp(decision.restUntilEnergy, 1, 100);
                currentState = $"Resting until {_restUntilEnergy}% energy";
                LogEvent($"{_villager.villagerName} rest target set: {_restUntilEnergy}%");
            }
            else
            {
                currentState = "Idle";
            }
            return;
        }

        JobType matchedJob = FindJobType(decision.jobName);

        if (matchedJob != null)
        {
            bool jobChanged = _jobHandler.currentJob != matchedJob;
            bool targetChanged = decision.hasTargetArea && _jobHandler.HasDifferentTargetArea(decision.TargetPosition);

            bool buildingTypeChanged = !string.IsNullOrEmpty(decision.buildingType)
                && !decision.buildingType.Equals(_jobHandler.PreferredBuildingType, System.StringComparison.OrdinalIgnoreCase);
            if (jobChanged || targetChanged || buildingTypeChanged)
            {
                if (!string.IsNullOrEmpty(decision.buildingType))
                {
                    _jobHandler.AssignJobWithBuilding(matchedJob, decision.TargetPosition, decision.buildingType);
                }
                else if (decision.hasTargetArea)
                {
                    _jobHandler.AssignJobWithTarget(matchedJob, decision.TargetPosition);
                }
                else
                {
                    _jobHandler.AssignJob(matchedJob);
                }

                LogInfo($"{_villager.villagerName} assigned {matchedJob.JobName}");
            }

            if (decision.gatherAmount > 0 && IsGatheringJob(matchedJob.JobName))
            {
                _gatherGoalAmount = decision.gatherAmount;
                _personalGathered = 0;
                LogEvent($"{_villager.villagerName} mini-goal set: personally gather {_gatherGoalAmount} via {matchedJob.JobName}");
            }

            currentState = $"{matchedJob.JobName}";
        }
        else
        {
            LogWarning($"Unknown job: {decision.jobName}");
            _jobHandler.AssignJob(null);
            currentState = "Unknown job";
        }
    }

    private void ClearGatherGoal()
    {
        _gatherGoalAmount = 0;
        _personalGathered = 0;
    }

    private static bool IsGatheringJob(string jobName) =>
        jobName == "Lumberjack" || jobName == "Miner" || jobName == "SeedGatherer" || jobName == "Farmer";

    private JobType FindJobType(string jobName)
    {
        foreach (var jt in availableJobTypes)
        {
            if (jt != null && jt.JobName.Equals(jobName, System.StringComparison.OrdinalIgnoreCase))
                return jt;
        }
        return null;
    }

    public void ForceDecision()
    {
        _idleTime = idleTimeout;
        if (LLMController.Instance != null && LLMController.Instance.UseBatchDecisions)
        {
            LLMController.Instance.RequestImmediateBatchDecision();
        }
        else
        {
            StartCoroutine(RequestIndividualDecision());
        }
    }

#if UNITY_EDITOR
    [ContextMenu("Force Decision Now")]
    private void EditorForceDecision()
    {
        if (Application.isPlaying)
            ForceDecision();
    }
#endif
}