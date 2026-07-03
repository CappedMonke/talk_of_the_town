using System.IO;
using Benchmark.Loggers;
using UnityEngine;

namespace Benchmark
{
    /// <summary>
    /// Central logging coordinator for benchmark runs.
    /// Manages sub-loggers and their lifecycle per run.
    /// Lives on the BenchmarkRunner GameObject (DontDestroyOnLoad).
    /// </summary>
    public class BenchmarkLogger : MonoBehaviour
    {
        [Header("Sampling")]
        [Tooltip("Villager/resource time series sample interval in sim-ticks (1 tick = 0.5 game-seconds)")]
        [SerializeField] private int sampleIntervalTicks = 10;

        [Tooltip("Buffer entries before flushing to disk")]
        [SerializeField] private int flushThreshold = 50;

        public static BenchmarkLogger Instance { get; private set; }

        private string _runOutputDir;
        private bool _isLogging;
        private bool _subscribedToLLM;

        // Sub-loggers
        private RunMetadataLogger _metadataLogger;
        private LLMDecisionLogger _decisionLogger;
        private VillagerTimeSeriesLogger _villagerLogger;
        private ResourceTimeSeriesLogger _resourceLogger;
        private WorldEventLogger _worldEventLogger;

        public bool IsLogging => _isLogging;
        public string RunOutputDir => _runOutputDir;

        void Awake()
        {
            if (Instance == null)
                Instance = this;
            else
            {
                Destroy(this);
                return;
            }
        }

        /// <summary>
        /// Begins logging for a new benchmark run. Creates the output directory and initializes all sub-loggers.
        /// </summary>
        public void BeginRun(BenchmarkRunConfig config)
        {
            _runOutputDir = Path.Combine(Application.persistentDataPath, "BenchmarkRuns", config.runId);
            Directory.CreateDirectory(_runOutputDir);

            _metadataLogger = new RunMetadataLogger(_runOutputDir, config);
            _decisionLogger = new LLMDecisionLogger(_runOutputDir, flushThreshold);
            _villagerLogger = new VillagerTimeSeriesLogger(_runOutputDir, flushThreshold);
            _resourceLogger = new ResourceTimeSeriesLogger(_runOutputDir, flushThreshold);
            _worldEventLogger = new WorldEventLogger(_runOutputDir, flushThreshold);

            // Subscribe to LLM decisions
            _subscribedToLLM = false;
            TrySubscribeToLLM();

            // Subscribe to world events
            Buildings.Building.OnBuildingPlaced += _worldEventLogger.OnBuildingPlaced;
            Buildings.Building.OnBuildingCompleted += _worldEventLogger.OnBuildingCompleted;
            Environment.Resources.ResourceNode.OnNodeExhausted += _worldEventLogger.OnNodeExhausted;
            Environment.Resources.ResourceNode.OnNodeRegrown += _worldEventLogger.OnNodeRegrown;

            if (GlobalGoals.Instance != null)
            {
                GlobalGoals.Instance.OnGlobalGoalCompleted += _worldEventLogger.OnGoalCompleted;
                GlobalGoals.Instance.OnAllGlobalGoalsCompleted += _worldEventLogger.OnAllGoalsCompleted;
            }

            // Capture map statistics now that the map is loaded
            _metadataLogger.CaptureMapStatistics();

            _isLogging = true;
            Debug.Log($"[BenchmarkLogger] Logging started for run: {config.runId} -> {_runOutputDir}");
        }

        void Update()
        {
            if (!_isLogging) return;

            // Retry LLM subscription if it wasn't ready at BeginRun time
            if (!_subscribedToLLM)
                TrySubscribeToLLM();

            long currentTick = SimTickTracker.CurrentTick;

            // Periodic sampling
            _villagerLogger?.SampleIfDue(currentTick, sampleIntervalTicks);
            _resourceLogger?.SampleIfDue(currentTick, sampleIntervalTicks);
        }

        private void TrySubscribeToLLM()
        {
            if (_subscribedToLLM) return;
            if (LLMController.Instance == null) return;

            LLMController.Instance.OnBatchDecisionLogged += OnBatchDecisionLogged;
            _subscribedToLLM = true;
            Debug.Log("[BenchmarkLogger] Subscribed to LLMController.OnBatchDecisionLogged");
        }

        private void OnBatchDecisionLogged(BatchDecisionLog log)
        {
            _decisionLogger?.LogDecision(log);
            _resourceLogger?.LogAtDecision(log.simTick);
        }

        /// <summary>
        /// Finalizes the current run: flushes all buffers, writes metadata, unsubscribes from events.
        /// </summary>
        public void FinalizeRun(string abortReason, LLMSessionStats sessionStats)
        {
            if (!_isLogging) return;
            _isLogging = false;

            // Unsubscribe events
            if (LLMController.Instance != null && _subscribedToLLM)
                LLMController.Instance.OnBatchDecisionLogged -= OnBatchDecisionLogged;
            _subscribedToLLM = false;

            Buildings.Building.OnBuildingPlaced -= _worldEventLogger.OnBuildingPlaced;
            Buildings.Building.OnBuildingCompleted -= _worldEventLogger.OnBuildingCompleted;
            Environment.Resources.ResourceNode.OnNodeExhausted -= _worldEventLogger.OnNodeExhausted;
            Environment.Resources.ResourceNode.OnNodeRegrown -= _worldEventLogger.OnNodeRegrown;

            if (GlobalGoals.Instance != null)
            {
                GlobalGoals.Instance.OnGlobalGoalCompleted -= _worldEventLogger.OnGoalCompleted;
                GlobalGoals.Instance.OnAllGlobalGoalsCompleted -= _worldEventLogger.OnAllGoalsCompleted;
            }

            // Flush all loggers
            _decisionLogger?.Flush();
            _villagerLogger?.Flush();
            _resourceLogger?.Flush();
            _worldEventLogger?.Flush();
            _metadataLogger?.FinalizeAndWrite(abortReason, sessionStats);

            Debug.Log($"[BenchmarkLogger] Run finalized. Logs written to: {_runOutputDir}");
        }
    }
}
