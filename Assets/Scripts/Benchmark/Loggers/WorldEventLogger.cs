using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Benchmark.Loggers
{
    /// <summary>
    /// Logs world events (buildings, resources, goals) to world_events.jsonl.
    /// </summary>
    public class WorldEventLogger
    {
        private readonly string _filePath;
        private readonly int _flushThreshold;
        private readonly List<string> _buffer = new();

        public WorldEventLogger(string outputDir, int flushThreshold)
        {
            _filePath = Path.Combine(outputDir, "world_events.jsonl");
            _flushThreshold = flushThreshold;
            File.WriteAllText(_filePath, "");
        }

        // ── Event handlers (subscribed by BenchmarkLogger) ──────────────

        public void OnBuildingPlaced(Buildings.Building building)
        {
            if (building == null || building.buildingData == null) return;
            var tile = building.GetComponentInParent<Tiles.Tile>();
            var pos = tile != null ? tile.GridPos : Vector2Int.zero;
            LogEvent("building_placed",
                $"{{\"buildingType\":\"{building.buildingData.buildingType}\",\"x\":{pos.x},\"y\":{pos.y}}}");
        }

        public void OnBuildingCompleted(Buildings.Building building)
        {
            if (building == null || building.buildingData == null) return;
            var tile = building.GetComponentInParent<Tiles.Tile>();
            var pos = tile != null ? tile.GridPos : Vector2Int.zero;
            LogEvent("building_completed",
                $"{{\"buildingType\":\"{building.buildingData.buildingType}\",\"x\":{pos.x},\"y\":{pos.y},\"level\":{building.currentLevel}}}");
        }

        public void OnNodeExhausted(Environment.Resources.ResourceNode node)
        {
            if (node == null) return;
            var pos = node.transform.position;
            var x = pos.x.ToString("F1", CultureInfo.InvariantCulture);
            var z = pos.z.ToString("F1", CultureInfo.InvariantCulture);
            LogEvent("resource_exhausted",
                $"{{\"resourceType\":\"{node.resourceType}\",\"x\":{x},\"z\":{z},\"isMineShaft\":{(node.isMineShaft ? "true" : "false")}}}");
        }

        public void OnNodeRegrown(Environment.Resources.ResourceNode node)
        {
            if (node == null) return;
            var pos = node.transform.position;
            var x = pos.x.ToString("F1", CultureInfo.InvariantCulture);
            var z = pos.z.ToString("F1", CultureInfo.InvariantCulture);
            LogEvent("resource_regrown",
                $"{{\"resourceType\":\"{node.resourceType}\",\"x\":{x},\"z\":{z}}}");
        }

        public void OnGoalCompleted(GlobalGoal goal)
        {
            var time = goal.completionTime.ToString("F1", CultureInfo.InvariantCulture);
            LogEvent("goal_completed",
                $"{{\"description\":\"{EscapeJson(goal.Description)}\",\"completionTime\":{time}}}");
        }

        public void OnAllGoalsCompleted()
        {
            LogEvent("all_goals_completed", "{}");
        }

        // ── Internal ────────────────────────────────────────────────────

        private void LogEvent(string eventType, string detailsJson)
        {
            var entry = new WorldEventEntry
            {
                simTick = SimTickTracker.CurrentTick,
                eventType = eventType,
                details = detailsJson
            };

            // Build JSONL line manually for nested JSON in details
            _buffer.Add($"{{\"simTick\":{entry.simTick},\"eventType\":\"{entry.eventType}\",\"details\":{entry.details}}}");

            // World events are rare — flush immediately so data is never lost
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

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        }
    }
}
