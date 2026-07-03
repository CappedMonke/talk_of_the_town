using System.Collections.Generic;
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
            LogEvent("resource_exhausted",
                $"{{\"resourceType\":\"{node.resourceType}\",\"x\":{pos.x:F1},\"z\":{pos.z:F1},\"isMineShaft\":{(node.isMineShaft ? "true" : "false")}}}");
        }

        public void OnNodeRegrown(Environment.Resources.ResourceNode node)
        {
            if (node == null) return;
            var pos = node.transform.position;
            LogEvent("resource_regrown",
                $"{{\"resourceType\":\"{node.resourceType}\",\"x\":{pos.x:F1},\"z\":{pos.z:F1}}}");
        }

        public void OnGoalCompleted(GlobalGoal goal)
        {
            LogEvent("goal_completed",
                $"{{\"description\":\"{EscapeJson(goal.Description)}\",\"completionTime\":{goal.completionTime:F1}}}");
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

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        }
    }
}
