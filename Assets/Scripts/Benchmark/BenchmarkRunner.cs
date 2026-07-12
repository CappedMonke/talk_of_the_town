using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Tiles;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Benchmark
{
    /// <summary>
    /// Orchestrates the full benchmark run matrix.
    /// Survives scene reloads (DontDestroyOnLoad) to manage runs across resets.
    /// </summary>
    public class BenchmarkRunner : MonoBehaviour
    {
        [Header("Benchmark Configuration")]
        [Tooltip("LLM models to benchmark, each with its own think mode")]
        [SerializeField] private ModelConfig[] models = {
            new() { modelName = "gemma3:12b", thinkMode = ThinkMode.ModelDefault, contextSize = 32768 },
            new() { modelName = "qwen3:8b", thinkMode = ThinkMode.Low },
            new() { modelName = "gpt-oss:20b-cloud", thinkMode = ThinkMode.Low },
            new() { modelName = "nemotron-3-super:cloud", thinkMode = ThinkMode.ModelDefault }
        };

        [Tooltip("Map filenames (.twcmap) in persistentDataPath")]
        [SerializeField] private string[] mapFiles = { "small_map.twcmap", "large_map.twcmap" };

        [Tooltip("Labels for the maps (for metadata)")]
        [SerializeField] private string[] mapSizeLabels = { "small", "large" };

        [Tooltip("Number of repetitions per configuration")]
        [SerializeField] private int repetitions = 2;

        [Tooltip("Maximum sim-ticks before a run is aborted")]
        [SerializeField] private long cutoffTicks = 20000;

        [Tooltip("Game speed during benchmark runs")]
        [SerializeField] private float benchmarkGameSpeed = 50f;

        [Header("Goal Sets (configure exact goals per preset in Inspector)")]
        [SerializeField] private GoalPreset[] goalPresets = {
            new() { label = "1goal", goals = {
                new() { type = GlobalGoalType.PopulationCount, targetResource = "", targetAmount = 5 }
            } },
            new() { label = "3goals", goals = {
                new() { type = GlobalGoalType.PopulationCount, targetResource = "", targetAmount = 5 },
                new() { type = GlobalGoalType.ResourceAmount, targetResource = "Wood", targetAmount = 100 },
                new() { type = GlobalGoalType.ResourceAmount, targetResource = "Stone", targetAmount = 80 }
            } },
            new() { label = "5goals", goals = {
                new() { type = GlobalGoalType.PopulationCount, targetResource = "", targetAmount = 5 },
                new() { type = GlobalGoalType.ResourceAmount, targetResource = "Wood", targetAmount = 100 },
                new() { type = GlobalGoalType.ResourceAmount, targetResource = "Stone", targetAmount = 80 },
                new() { type = GlobalGoalType.ResourceAmount, targetResource = "Food", targetAmount = 60 },
                new() { type = GlobalGoalType.BuildingCount, targetResource = "", targetAmount = 3 }
            } }
        };

        [Header("Test Mode")]
        [Tooltip("Run a quick test with minimal config instead of the full 48-run matrix")]
        [SerializeField] private bool testMode = false;

        [Tooltip("Override model index for test runs (0 = first model)")]
        [SerializeField] private int testModelIndex = 0;

        [Tooltip("Override goal preset index for test runs (0 = first preset)")]
        [SerializeField] private int testGoalPresetIndex = 0;

        [Tooltip("Override map index for test runs (0 = first map)")]
        [SerializeField] private int testMapIndex = 0;

        [Tooltip("Override cutoff for test runs (short for quick validation)")]
        [SerializeField] private long testCutoffTicks = 500;

        [Header("Auto Start")]
        [Tooltip("Automatically start the benchmark when entering play mode")]
        [SerializeField] private bool autoStart = false;

        public static BenchmarkRunner Instance { get; private set; }

        private BenchmarkManifest _manifest;
        private BenchmarkRunConfig _currentRun;
        private bool _isRunning;
        private bool _waitingForSceneReload;
        private float _runStartRealTime;

        // Status for UI
        public bool IsRunning => _isRunning;
        public BenchmarkRunConfig CurrentRun => _currentRun;
        public BenchmarkManifest Manifest => _manifest;
        public int CompletedRunCount => _manifest?.runs.FindAll(r => r.status == RunStatus.Completed).Count ?? 0;
        public int TotalRunCount => _manifest?.runs.Count ?? 0;

        private string ManifestPath => Path.Combine(Application.persistentDataPath, "BenchmarkRuns",
            testMode ? "_test" : "", "manifest.json");

        void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else
            {
                Destroy(gameObject);
                return;
            }
        }

        void Start()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;

            if (autoStart)
                StartBenchmark();
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_waitingForSceneReload)
            {
                _waitingForSceneReload = false;
                StartCoroutine(DelayedStartNextRun());
            }
        }

        private IEnumerator DelayedStartNextRun()
        {
            // Wait for scene to fully initialize — map loading, NavMesh baking, etc.
            yield return new WaitForSecondsRealtime(5f);
            StartCoroutine(ConfigureAndStartNextRun());
        }

        // Idle detection
        private bool _waitingForDecision;
        private float _lastIdleDetectionTime;

        void Update()
        {
            // ESC quits the application in builds
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Debug.Log("[BenchmarkRunner] ESC pressed — quitting application");
                Application.Quit();
                return;
            }

            if (!_isRunning || _currentRun == null) return;

            // Check tick cutoff
            if (SimTickTracker.CurrentTick >= _currentRun.cutoffTicks)
            {
                Debug.Log($"[BenchmarkRunner] Cutoff reached at tick {SimTickTracker.CurrentTick}");
                CompleteCurrentRun("cutoff");
                return;
            }

            // When all villagers are idle and we're running at speed, pause and force a new LLM decision
            // But NOT if there are growing crops — villagers may be intentionally waiting for harvest
            // Cooldown of 30 game-seconds between idle detections to avoid spamming LLM when
            // builders are stuck waiting for resources (LLM keeps assigning the same thing)
            if (!_waitingForDecision && Time.timeScale > 0f && AllVillagersIdle()
                && !HasGrowingCrops() && !HasUnfinishedBuildings()
                && Time.time - _lastIdleDetectionTime > 30f)
            {
                _lastIdleDetectionTime = Time.time;
                _waitingForDecision = true;
                VillageState.Instance?.SetGameSpeed(0f);

                if (LLMController.Instance != null)
                {
                    LLMController.Instance.OnBatchDecisionMade += OnDecisionResumesSpeed;
                    LLMController.Instance.RequestImmediateBatchDecision();
                }
            }
        }

        private void OnDecisionResumesSpeed(Dictionary<string, JobDecision> _)
        {
            if (LLMController.Instance != null)
                LLMController.Instance.OnBatchDecisionMade -= OnDecisionResumesSpeed;

            _waitingForDecision = false;

            if (_isRunning && VillageState.Instance != null)
                VillageState.Instance.SetGameSpeed(benchmarkGameSpeed);
        }

        private bool HasUnfinishedBuildings()
        {
            var buildings = FindObjectsByType<Buildings.Building>(FindObjectsSortMode.None);
            foreach (var b in buildings)
            {
                if (b != null && !b.IsFinished())
                    return true;
            }
            return false;
        }

        private bool HasGrowingCrops()
        {
            var nodes = FindObjectsByType<Environment.Resources.ResourceNode>(FindObjectsSortMode.None);
            foreach (var node in nodes)
            {
                if (node != null
                    && node.resourceType == Environment.Resources.ResourceNode.ResourceType.Crop
                    && !node.IsMature)
                    return true;
            }
            return false;
        }

        private bool AllVillagersIdle()
        {
            if (VillageState.Instance == null) return false;
            var villagers = VillageState.Instance.Villagers;
            if (villagers.Count == 0) return false;

            foreach (var v in villagers)
            {
                if (v == null) continue;

                var brain = v.GetComponent<VillagerBrain>();

                // LLM explicitly assigned IDLE — intentional, not "needs orders"
                if (brain != null && brain.IsLLMAssignedIdle)
                    return false;

                // Villager is resting: either LLM-set rest target or exhausted (energy < 5%)
                if (brain != null && brain.IsResting)
                    return false;
                if (v.EnergyPercent < 5)
                    return false;

                // Check if villager has an active job that's actually doing work
                var jh = v.GetComponent<JobHandler>();
                if (jh != null && jh.currentJob != null && jh.ActiveJobLogic != null)
                {
                    var state = jh.ActiveJobLogic.GetCurrentState();
                    // Only treat job-internal Idle as effectively idle
                    // (e.g. builder waiting for resources, farmer waiting for farm)
                    // FindingTarget is active work (searching for a node to harvest)
                    if (state != Villagers.Jobs.AnimationState.Idle)
                        return false;
                }
            }
            return true;
        }

        // ── Public API ──────────────────────────────────────────────────

        /// <summary>Start the full benchmark (or test run). Generates manifest if needed.</summary>
        public void StartBenchmark()
        {
            if (_isRunning) return; // Prevent double-start
            LoadOrGenerateManifest();
            StartCoroutine(ConfigureAndStartNextRun());
        }

        /// <summary>Skip the current run and move to the next.</summary>
        public void SkipCurrentRun()
        {
            if (_currentRun != null)
                CompleteCurrentRun("skipped");
        }

        /// <summary>Pause/unpause the current run.</summary>
        public void TogglePause()
        {
            if (!_isRunning) return;
            if (Time.timeScale > 0f)
            {
                Time.timeScale = 0f;
            }
            else
            {
                VillageState.Instance?.SetGameSpeed(benchmarkGameSpeed);
            }
        }

        /// <summary>Reset a specific run to Pending so it will be re-run.</summary>
        public void ResetRun(string runId)
        {
            if (_manifest == null) return;
            var run = _manifest.runs.Find(r => r.runId == runId);
            if (run != null)
            {
                run.status = RunStatus.Pending;
                run.completedAt = null;
                run.abortReason = null;
                SaveManifest();
                Debug.Log($"[BenchmarkRunner] Run {runId} reset to Pending");
            }
        }

        /// <summary>Force regenerate the manifest (clears all progress!).</summary>
        public void RegenerateManifest()
        {
            _manifest = GenerateManifest();
            SaveManifest();
            Debug.Log($"[BenchmarkRunner] Manifest regenerated with {_manifest.runs.Count} runs");
        }

        // ── Manifest management ─────────────────────────────────────────

        private void LoadOrGenerateManifest()
        {
            // In test mode, always regenerate so changing testModelIndex/testGoalPresetIndex takes effect
            if (testMode)
            {
                _manifest = GenerateManifest();
                SaveManifest();
                Debug.Log($"[BenchmarkRunner] Test mode: generated fresh manifest with {_manifest.runs.Count} run(s)");
                return;
            }

            if (File.Exists(ManifestPath))
            {
                string json = File.ReadAllText(ManifestPath);
                _manifest = JsonUtility.FromJson<BenchmarkManifest>(json);

                // Reset any runs left in Running state (interrupted by crash/restart) back to Pending
                int reset = 0;
                foreach (var run in _manifest.runs)
                {
                    if (run.status == RunStatus.Running)
                    {
                        run.status = RunStatus.Pending;
                        run.completedAt = null;
                        run.abortReason = null;
                        reset++;
                    }
                }
                if (reset > 0)
                {
                    SaveManifest();
                    Debug.Log($"[BenchmarkRunner] Reset {reset} interrupted run(s) back to Pending");
                }

                Debug.Log($"[BenchmarkRunner] Loaded manifest: {_manifest.runs.Count} runs ({CompletedRunCount} completed)");
            }
            else
            {
                _manifest = GenerateManifest();
                SaveManifest();
                Debug.Log($"[BenchmarkRunner] Generated new manifest: {_manifest.runs.Count} runs");
            }
        }

        private void SaveManifest()
        {
            string dir = Path.GetDirectoryName(ManifestPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            string json = JsonUtility.ToJson(_manifest, true);
            File.WriteAllText(ManifestPath, json);
        }

        private BenchmarkManifest GenerateManifest()
        {
            var manifest = new BenchmarkManifest
            {
                createdAt = DateTime.Now.ToString("o"),
                runs = new List<BenchmarkRunConfig>()
            };

            if (testMode)
            {
                var mc = models[Mathf.Clamp(testModelIndex, 0, models.Length - 1)];
                int mapIdx = Mathf.Clamp(testMapIndex, 0, mapFiles.Length - 1);
                var preset = testGoalPresetIndex < goalPresets.Length ? goalPresets[testGoalPresetIndex] : goalPresets[0];

                manifest.runs.Add(new BenchmarkRunConfig
                {
                    runId = $"test_{SanitizeModelName(mc.modelName)}_{preset.label}_{mapSizeLabels[mapIdx]}_rep1",
                    modelName = mc.modelName,
                    thinkMode = mc.thinkMode.ToString(),
                    promptStyle = mc.promptStyle.ToString(),
                    forceJsonFormat = mc.forceJsonFormat,
                    maxOutputTokens = mc.maxOutputTokens,
                    contextSize = mc.contextSize,
                    mapFile = mapFiles[mapIdx],
                    mapSize = mapSizeLabels[mapIdx],
                    goals = new List<GoalConfig>(preset.goals),
                    repetition = 1,
                    cutoffTicks = testCutoffTicks
                });

                return manifest;
            }

            // Full matrix: goal presets × models × maps × repetitions
            // Goal presets first so all 1-goal runs complete before 3-goal, etc.
            foreach (var preset in goalPresets)
            {
                foreach (var mc in models)
                {
                    for (int mapIdx = 0; mapIdx < mapFiles.Length; mapIdx++)
                    {
                        for (int rep = 1; rep <= repetitions; rep++)
                        {
                            string sanitizedModel = SanitizeModelName(mc.modelName);
                            manifest.runs.Add(new BenchmarkRunConfig
                            {
                                runId = $"{sanitizedModel}_{preset.label}_{mapSizeLabels[mapIdx]}_rep{rep}",
                                modelName = mc.modelName,
                                thinkMode = mc.thinkMode.ToString(),
                                promptStyle = mc.promptStyle.ToString(),
                                forceJsonFormat = mc.forceJsonFormat,
                                maxOutputTokens = mc.maxOutputTokens,
                                contextSize = mc.contextSize,
                                mapFile = mapFiles[mapIdx],
                                mapSize = mapSizeLabels[mapIdx],
                                goals = new List<GoalConfig>(preset.goals),
                                repetition = rep,
                                cutoffTicks = cutoffTicks
                            });
                        }
                    }
                }
            }

            return manifest;
        }

        // ── Run lifecycle ───────────────────────────────────────────────

        private IEnumerator ConfigureAndStartNextRun()
        {
            // Wait for scene singletons to Awake — yield end-of-frame then one more frame
            yield return new WaitForEndOfFrame();
            yield return null;

            _currentRun = FindNextPendingRun();
            if (_currentRun == null)
            {
                Debug.Log("[BenchmarkRunner] All runs completed!");
                _isRunning = false;
                yield break;
            }

            Debug.Log($"[BenchmarkRunner] Starting run: {_currentRun.runId} ({CompletedRunCount + 1}/{TotalRunCount})");

            _currentRun.status = RunStatus.Running;
            SaveManifest();

            // Configure model and think mode — retry briefly if singletons aren't ready yet
            for (int i = 0; i < 60; i++) // up to ~1 second
            {
                if (GlobalSettings.Instance != null && LLMController.Instance != null) break;
                yield return null;
            }

            // Configure goals (before map load, GlobalGoals should be ready)
            if (GlobalGoals.Instance != null)
            {
                GlobalGoals.Instance.ClearGoals();
                var globalGoals = new List<GlobalGoal>();
                foreach (var gc in _currentRun.goals)
                    globalGoals.Add(gc.ToGlobalGoal());
                GlobalGoals.Instance.SetGoals(globalGoals);
            }

            // Load map and wait for NavMesh BEFORE activating game objects
            var bridge = FindFirstObjectByType<TWCBridge>();
            if (bridge != null)
            {
                bridge.LoadFromFile(_currentRun.mapFile);

                // Wait for NavMesh to be ready
                bool navMeshReady = false;
                Action onReady = () => navMeshReady = true;
                TWCBridge.OnNavMeshReady += onReady;

                float navTimeout = 60f;
                float navWaited = 0f;
                while (!navMeshReady && navWaited < navTimeout)
                {
                    navWaited += Time.unscaledDeltaTime;
                    yield return null;
                }
                TWCBridge.OnNavMeshReady -= onReady;

                if (!navMeshReady)
                {
                    Debug.LogError("[BenchmarkRunner] NavMesh timeout! Skipping run.");
                    _currentRun.status = RunStatus.Failed;
                    _currentRun.abortReason = "error:navmesh_timeout";
                    SaveManifest();
                    StartCoroutine(ConfigureAndStartNextRun());
                    yield break;
                }
            }

            // NOW activate game objects (villagers need NavMesh to exist)
            var mainMenu = FindFirstObjectByType<MainMenu.MainMenu>();
            if (mainMenu != null)
                mainMenu.OnStartPressed();

            // Wait a frame for Awake() to run on newly activated objects
            yield return null;

            // Configure model and think mode
            if (GlobalSettings.Instance != null)
            {
                GlobalSettings.Instance.LLMModel = _currentRun.modelName;
                if (Enum.TryParse<PromptStyle>(_currentRun.promptStyle, out var ps))
                    GlobalSettings.Instance.PromptStyle = ps;
            }

            if (LLMController.Instance != null)
            {
                if (Enum.TryParse<ThinkMode>(_currentRun.thinkMode, out var tm))
                    LLMController.Instance.CurrentThinkMode = tm;
                LLMController.Instance.ForceJsonFormat = _currentRun.forceJsonFormat;
                LLMController.Instance.MaxOutputTokens = _currentRun.maxOutputTokens;
                LLMController.Instance.ContextSize = _currentRun.contextSize;
                Debug.Log($"[BenchmarkRunner] Config applied: model={_currentRun.modelName}, prompt={_currentRun.promptStyle}, think={_currentRun.thinkMode}, jsonFormat={_currentRun.forceJsonFormat}, maxTokens={_currentRun.maxOutputTokens}, ctx={_currentRun.contextSize}");
            }
            else
            {
                Debug.LogWarning("[BenchmarkRunner] LLMController not found — settings will use Inspector defaults");
            }

            // Subscribe to goal completion
            if (GlobalGoals.Instance != null)
                GlobalGoals.Instance.OnAllGlobalGoalsCompleted += OnAllGoalsCompleted;

            // Reset tick counter so each run starts at tick 0
            SimTickTracker.ResetEpoch();

            // Start logging
            if (BenchmarkLogger.Instance != null)
                BenchmarkLogger.Instance.BeginRun(_currentRun);

            // Start paused — speed 0 until first LLM decision arrives
            yield return null;
            if (VillageState.Instance != null)
                VillageState.Instance.SetGameSpeed(0f);

            // Set benchmark speed after first LLM decision
            if (LLMController.Instance != null)
                LLMController.Instance.OnBatchDecisionMade += OnFirstDecisionSetSpeed;

            _isRunning = true;
            _runStartRealTime = Time.realtimeSinceStartup;
            Debug.Log($"[BenchmarkRunner] Run {_currentRun.runId} started (paused until first LLM decision)");
        }

        private void OnFirstDecisionSetSpeed(Dictionary<string, JobDecision> _)
        {
            // Unsubscribe immediately — only fires once
            if (LLMController.Instance != null)
                LLMController.Instance.OnBatchDecisionMade -= OnFirstDecisionSetSpeed;

            if (VillageState.Instance != null)
                VillageState.Instance.SetGameSpeed(benchmarkGameSpeed);

            Debug.Log($"[BenchmarkRunner] First LLM decision received — setting speed to {benchmarkGameSpeed}x");
        }

        private void OnAllGoalsCompleted()
        {
            Debug.Log($"[BenchmarkRunner] All goals completed at tick {SimTickTracker.CurrentTick}");
            CompleteCurrentRun("goals_reached");
        }

        private void CompleteCurrentRun(string abortReason)
        {
            if (_currentRun == null) return;
            _isRunning = false;

            // Unsubscribe
            _waitingForDecision = false;
            if (LLMController.Instance != null)
            {
                LLMController.Instance.OnBatchDecisionMade -= OnFirstDecisionSetSpeed;
                LLMController.Instance.OnBatchDecisionMade -= OnDecisionResumesSpeed;
            }
            if (GlobalGoals.Instance != null)
                GlobalGoals.Instance.OnAllGlobalGoalsCompleted -= OnAllGoalsCompleted;

            // Pause
            Time.timeScale = 0f;

            // Finalize logging
            var sessionStats = LLMController.Instance != null ? LLMController.Instance.SessionStats : new LLMSessionStats();
            if (BenchmarkLogger.Instance != null)
                BenchmarkLogger.Instance.FinalizeRun(abortReason, sessionStats);

            // Update manifest
            _currentRun.status = RunStatus.Completed;
            _currentRun.completedAt = DateTime.Now.ToString("o");
            _currentRun.abortReason = abortReason;
            SaveManifest();

            float elapsed = Time.realtimeSinceStartup - _runStartRealTime;
            Debug.Log($"[BenchmarkRunner] Run {_currentRun.runId} completed: {abortReason} (real time: {elapsed:F1}s)");

            // Check if there are more runs
            if (FindNextPendingRun() != null)
            {
                // Reload scene for clean state, then start next run
                _waitingForSceneReload = true;
                Time.timeScale = 1f; // Must be >0 for scene load to work
                SceneManager.LoadScene("CombinedScene");
            }
            else
            {
                Debug.Log("[BenchmarkRunner] All benchmark runs completed!");
            }
        }

        private BenchmarkRunConfig FindNextPendingRun()
        {
            return _manifest?.runs.Find(r => r.status == RunStatus.Pending);
        }

        [ContextMenu("Reset Models to Defaults")]
        private void ResetModelsToDefaults()
        {
            models = new[]
            {
                new ModelConfig { modelName = "gpt-oss:20b-cloud", thinkMode = ThinkMode.Low, forceJsonFormat = false, maxOutputTokens = 0 },
                new ModelConfig { modelName = "nemotron-3-super:cloud", thinkMode = ThinkMode.Off, forceJsonFormat = true, maxOutputTokens = 0 },
                new ModelConfig { modelName = "gemma3:12b", thinkMode = ThinkMode.ModelDefault, forceJsonFormat = true, maxOutputTokens = 0, contextSize = 32768 },
                new ModelConfig { modelName = "qwen3:8b", thinkMode = ThinkMode.Off, forceJsonFormat = false, maxOutputTokens = 0 }
            };
            Debug.Log("[BenchmarkRunner] Models reset to defaults");
        }

        private static string SanitizeModelName(string model)
        {
            // Replace characters that aren't valid in filenames/folder names
            return model.Replace(":", "-").Replace("/", "-").Replace("\\", "-");
        }
    }
}
