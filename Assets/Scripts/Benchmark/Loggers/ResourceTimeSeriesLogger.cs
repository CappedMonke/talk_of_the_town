using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Benchmark.Loggers
{
    /// <summary>
    /// Periodically samples resource/inventory state and writes resource_timeseries.csv.
    /// Skips duplicate periodic samples when nothing has changed.
    /// Always writes at LLM decisions regardless.
    /// </summary>
    public class ResourceTimeSeriesLogger
    {
        private const string Header = "sim_tick,trigger,wood,stone,seeds,food,capacity,villager_count,building_count";

        private readonly string _filePath;
        private readonly int _flushThreshold;
        private readonly List<string> _buffer = new();
        private long _lastSampleTick = -1;

        // Change detection for periodic samples
        private string _lastPeriodicHash = "";

        public ResourceTimeSeriesLogger(string outputDir, int flushThreshold)
        {
            _filePath = Path.Combine(outputDir, "resource_timeseries.csv");
            _flushThreshold = flushThreshold;
            File.WriteAllText(_filePath, Header + "\n");
        }

        public void SampleIfDue(long currentTick, int intervalTicks)
        {
            if (currentTick <= _lastSampleTick) return;
            if ((currentTick - _lastSampleTick) < intervalTicks && _lastSampleTick >= 0) return;

            _lastSampleTick = currentTick;
            WriteSample(currentTick, "periodic", skipIfUnchanged: true);
        }

        /// <summary>Captures a snapshot triggered by an LLM decision (always written).</summary>
        public void LogAtDecision(long simTick)
        {
            WriteSample(simTick, "llm_decision", skipIfUnchanged: false);
        }

        private void WriteSample(long tick, string trigger, bool skipIfUnchanged)
        {
            var vs = VillageState.Instance;
            if (vs == null) return;

            int buildingCount = 0;
            var buildings = Object.FindObjectsByType<Buildings.Building>(FindObjectsSortMode.None);
            foreach (var b in buildings)
                if (b != null && b.IsFinished()) buildingCount++;

            string hash = $"{vs.Wood}:{vs.Stone}:{vs.Seeds}:{vs.Food}:{vs.InventoryCapacity}:{vs.Villagers.Count}:{buildingCount}";

            if (skipIfUnchanged && hash == _lastPeriodicHash)
                return;

            _lastPeriodicHash = hash;

            _buffer.Add(string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4},{5},{6},{7},{8}",
                tick, trigger, vs.Wood, vs.Stone, vs.Seeds, vs.Food,
                vs.InventoryCapacity, vs.Villagers.Count, buildingCount));

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
    }
}
