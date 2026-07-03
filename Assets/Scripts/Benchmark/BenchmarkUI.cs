using UnityEngine;
using TMPro;
using UnityEngine.UI;

namespace Benchmark
{
    /// <summary>
    /// Minimal overlay UI for monitoring benchmark progress.
    /// Attach to a Canvas that is NOT part of the main menu toggle system.
    /// This Canvas should be on the BenchmarkRunner's DontDestroyOnLoad GameObject.
    /// </summary>
    public class BenchmarkUI : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private GameObject panel;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private TextMeshProUGUI progressText;
        [SerializeField] private Button startButton;
        [SerializeField] private Button pauseButton;
        [SerializeField] private Button skipButton;

        void Start()
        {
            if (startButton != null)
                startButton.onClick.AddListener(OnStartClicked);
            if (pauseButton != null)
                pauseButton.onClick.AddListener(OnPauseClicked);
            if (skipButton != null)
                skipButton.onClick.AddListener(OnSkipClicked);

            UpdateUI();
        }

        void Update()
        {
            UpdateUI();
        }

        private void UpdateUI()
        {
            var runner = BenchmarkRunner.Instance;
            if (runner == null)
            {
                if (statusText != null) statusText.text = "No BenchmarkRunner found";
                return;
            }

            // Toggle button visibility
            if (startButton != null)
                startButton.gameObject.SetActive(!runner.IsRunning);
            if (pauseButton != null)
                pauseButton.gameObject.SetActive(runner.IsRunning);
            if (skipButton != null)
                skipButton.gameObject.SetActive(runner.IsRunning);

            // Status
            if (statusText != null)
            {
                if (!runner.IsRunning && runner.CurrentRun == null)
                {
                    statusText.text = $"Benchmark: {runner.CompletedRunCount}/{runner.TotalRunCount} runs completed. Press Start.";
                }
                else if (runner.CurrentRun != null)
                {
                    var run = runner.CurrentRun;
                    statusText.text = $"Run {runner.CompletedRunCount + 1}/{runner.TotalRunCount}: {run.modelName} | {run.goals.Count} goals | {run.mapSize} map | rep {run.repetition}";
                }
            }

            // Progress
            if (progressText != null && runner.IsRunning && runner.CurrentRun != null)
            {
                long tick = SimTickTracker.CurrentTick;
                long cutoff = runner.CurrentRun.cutoffTicks;
                float pct = cutoff > 0 ? (tick * 100f / cutoff) : 0f;
                bool paused = Time.timeScale == 0f;
                progressText.text = $"Tick: {tick} / {cutoff} ({pct:F0}%){(paused ? " [PAUSED]" : "")}";
            }
            else if (progressText != null)
            {
                progressText.text = "";
            }
        }

        private void OnStartClicked()
        {
            BenchmarkRunner.Instance?.StartBenchmark();
        }

        private void OnPauseClicked()
        {
            BenchmarkRunner.Instance?.TogglePause();
        }

        private void OnSkipClicked()
        {
            BenchmarkRunner.Instance?.SkipCurrentRun();
        }
    }
}
