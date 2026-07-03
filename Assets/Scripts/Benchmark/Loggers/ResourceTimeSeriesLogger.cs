using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Benchmark.Loggers
{
    /// <summary>
    /// Periodically samples resource/inventory state and writes resource_timeseries.csv.
    /// Also samples at each LLM decision.
    /// </summary>
    public class ResourceTimeSeriesLogger
    {
        private const string Header = "sim_tick,trigger,wood,stone,seeds,food,capacity,villager_count,building_count";

        private readonly string _filePath;
        private readonly int _flushThreshold;
        private readonly List<string> _buffer = new();
        private long _lastSampleTick = -1;

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
            WriteSample(currentTick, "periodic");
        }

        /// <summary>Captures a snapshot triggered by an LLM decision.</summary>
        public void LogAtDecision(long simTick)
        {
            WriteSample(simTick, "llm_decision");
        }

        private void WriteSample(long tick, string trigger)
        {
            var vs = VillageState.Instance;
            if (vs == null) return;

            int buildingCount = 0;
            var buildings = Object.FindObjectsByType<Buildings.Building>(FindObjectsSortMode.None);
            foreach (var b in buildings)
                if (b != null && b.IsFinished()) buildingCount++;

            _buffer.Add($"{tick},{trigger},{vs.Wood},{vs.Stone},{vs.Seeds},{vs.Food},{vs.InventoryCapacity},{vs.Villagers.Count},{buildingCount}");

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
