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
        [Tooltip("LLM model names to benchmark (Ollama model identifiers)")]
        [SerializeField] private string[] models = { "gemma3:12b", "qwen3:14b", "gpt-oss:120b-cloud", "gpt-oss:70b-cloud" };

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

        [Tooltip("Override model for test runs (empty = use first model)")]
        [SerializeField] private string testModel = "";

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
            if (_waitingForSceneReload)
            {
                _waitingForSceneReload = false;
                StartCoroutine(ConfigureAndStartNextRun());
                return;
            }

            if (autoStart)
                StartBenchmark();
        }

        // Idle detection
        private bool _waitingForDecision;

        void Update()
        {
            if (!_isRunning || _currentRun == null) return;

            // Check tick cutoff
            if (SimTickTracker.CurrentTick >= _currentRun.cutoffTicks)
            {
                Debug.Log($"[BenchmarkRunner] Cutoff reached at tick {SimTickTracker.CurrentTick}");
                CompleteCurrentRun("cutoff");
                return;
            }

            // When all villagers are idle and we're running at speed, pause and force a new LLM decision
            if (!_waitingForDecision && Time.timeScale > 0f && AllVillagersIdle())
            {
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

        private bool AllVillagersIdle()
        {
            if (VillageState.Instance == null) return false;
            var villagers = VillageState.Instance.Villagers;
            if (villagers.Count == 0) return false;

            foreach (var v in villagers)
            {
                if (v == null) continue;

                // Villager has an active job — not idle
                var jh = v.GetComponent<JobHandler>();
                if (jh != null && jh.currentJob != null && jh.ActiveJobLogic != null)
                    return false;

                // Villager is resting: either LLM-set rest target or exhausted (energy < 5%)
                var brain = v.GetComponent<VillagerBrain>();
                if (brain != null && brain.IsResting)
                    return false;
                if (v.EnergyPercent < 5)
                    return false;
            }
            return true;
        }

        // ── Public API ──────────────────────────────────────────────────

        /// <summary>Start the full benchmark (or test run). Generates manifest if needed.</summary>
        public void StartBenchmark()
        {
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
            if (File.Exists(ManifestPath))
            {
                string json = File.ReadAllText(ManifestPath);
                _manifest = JsonUtility.FromJson<BenchmarkManifest>(json);
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
                string model = string.IsNullOrEmpty(testModel) ? models[0] : testModel;
                int mapIdx = Mathf.Clamp(testMapIndex, 0, mapFiles.Length - 1);
                var preset = testGoalPresetIndex < goalPresets.Length ? goalPresets[testGoalPresetIndex] : goalPresets[0];

                manifest.runs.Add(new BenchmarkRunConfig
                {
                    runId = $"test_{SanitizeModelName(model)}_{preset.label}_{mapSizeLabels[mapIdx]}_rep1",
                    modelName = model,
                    mapFile = mapFiles[mapIdx],
                    mapSize = mapSizeLabels[mapIdx],
                    goals = new List<GoalConfig>(preset.goals),
                    repetition = 1,
                    cutoffTicks = testCutoffTicks
                });

                return manifest;
            }

            // Full matrix: models × goal presets × maps × repetitions
            foreach (var model in models)
            {
                foreach (var preset in goalPresets)
                {
                    for (int mapIdx = 0; mapIdx < mapFiles.Length; mapIdx++)
                    {
                        for (int rep = 1; rep <= repetitions; rep++)
                        {
                            string sanitizedModel = SanitizeModelName(model);
                            manifest.runs.Add(new BenchmarkRunConfig
                            {
                                runId = $"{sanitizedModel}_{preset.label}_{mapSizeLabels[mapIdx]}_rep{rep}",
                                modelName = model,
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
            // Wait a frame for scene objects to initialize after reload
            yield return null;
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

            // Configure model
            if (GlobalSettings.Instance != null)
                GlobalSettings.Instance.LLMModel = _currentRun.modelName;

            // Configure goals
            if (GlobalGoals.Instance != null)
            {
                GlobalGoals.Instance.ClearGoals();
                var globalGoals = new List<GlobalGoal>();
                foreach (var gc in _currentRun.goals)
                    globalGoals.Add(gc.ToGlobalGoal());
                GlobalGoals.Instance.SetGoals(globalGoals);
            }

            // Load map
            var bridge = FindFirstObjectByType<TWCBridge>();
            if (bridge != null)
            {
                bridge.LoadFromFile(_currentRun.mapFile);

                // Wait for NavMesh to be ready
                bool navMeshReady = false;
                Action onReady = () => navMeshReady = true;
                TWCBridge.OnNavMeshReady += onReady;

                float timeout = 60f;
                float waited = 0f;
                while (!navMeshReady && waited < timeout)
                {
                    waited += Time.unscaledDeltaTime;
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

            // Subscribe to goal completion
            if (GlobalGoals.Instance != null)
                GlobalGoals.Instance.OnAllGlobalGoalsCompleted += OnAllGoalsCompleted;

            // Start logging
            if (BenchmarkLogger.Instance != null)
                BenchmarkLogger.Instance.BeginRun(_currentRun);

            // Start the simulation (press the start button programmatically)
            var mainMenu = FindFirstObjectByType<MainMenu.MainMenu>();
            if (mainMenu != null)
                mainMenu.OnStartPressed();

            // Start paused — speed 0 until first LLM decision arrives
            yield return null; // Wait a frame for VillageState to exist
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

        private static string SanitizeModelName(string model)
        {
            // Replace characters that aren't valid in filenames/folder names
            return model.Replace(":", "-").Replace("/", "-").Replace("\\", "-");
        }
    }
}
