using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using AnimState = Villagers.Jobs.AnimationState;

namespace Benchmark.Loggers
{
    /// <summary>
    /// Periodically samples all villagers and writes villager_timeseries.csv.
    /// </summary>
    public class VillagerTimeSeriesLogger
    {
        private const string Header = "sim_tick,villager_id,villager_name,pos_x,pos_y,grid_x,grid_y,job,energy_pct,status";

        private readonly string _filePath;
        private readonly int _flushThreshold;
        private readonly List<string> _buffer = new();
        private long _lastSampleTick = -1;

        public VillagerTimeSeriesLogger(string outputDir, int flushThreshold)
        {
            _filePath = Path.Combine(outputDir, "villager_timeseries.csv");
            _flushThreshold = flushThreshold;
            File.WriteAllText(_filePath, Header + "\n");
        }

        /// <summary>
        /// Checks if a sample is due and captures villager data.
        /// Called every frame from BenchmarkLogger.Update().
        /// </summary>
        public void SampleIfDue(long currentTick, int intervalTicks)
        {
            if (currentTick <= _lastSampleTick) return;
            if ((currentTick - _lastSampleTick) < intervalTicks && _lastSampleTick >= 0) return;

            _lastSampleTick = currentTick;
            Sample(currentTick);
        }

        private void Sample(long tick)
        {
            if (VillageState.Instance == null) return;

            foreach (var v in VillageState.Instance.Villagers)
            {
                if (v == null) continue;

                var pos = v.transform.position;
                var gridPos = v.GridPosition;
                var jh = v.GetComponent<JobHandler>();

                string jobName = "Idle";
                string status = "idle";

                if (jh != null && jh.currentJob != null)
                {
                    jobName = jh.currentJob.JobName;
                    var logic = jh.ActiveJobLogic;
                    if (logic != null)
                    {
                        var state = logic.GetCurrentState();
                        status = state switch
                        {
                            AnimState.MovingToTarget or AnimState.Carrying => "walking",
                            AnimState.Idle => "idle",
                            _ => "working"
                        };
                    }
                }

                _buffer.Add($"{tick},{v.VillagerId},{CsvEscape(v.villagerName)},{pos.x:F1},{pos.z:F1},{gridPos.x},{gridPos.y},{CsvEscape(jobName)},{v.EnergyPercent},{status}");
            }

            if (_buffer.Count >= _flushThreshold)
                Flush();
        }

        public void Flush()
        {
            if (_buffer.Count == 0) return;

            var sb = new StringBuilder();
            foreach (var line in _buffer)
                sb.AppendLine(line);

            File.AppendAllText(_filePath, sb.ToString());
            _buffer.Clear();
        }

        private static string CsvEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Contains(',') || s.Contains('"'))
                return $"\"{s.Replace("\"", "\"\"")}\"";
            return s;
        }
    }
}
