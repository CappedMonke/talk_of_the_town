using System;
using System.IO;
using System.Linq;
using Environment.Resources;
using Tiles;
using UnityEngine;

namespace Benchmark.Loggers
{
    /// <summary>
    /// Captures run metadata at start and writes run_metadata.json at finalization.
    /// </summary>
    public class RunMetadataLogger
    {
        private readonly string _outputDir;
        private readonly RunMetadata _metadata;
        private readonly float _startRealTime;

        public RunMetadataLogger(string outputDir, BenchmarkRunConfig config)
        {
            _outputDir = outputDir;
            _startRealTime = Time.realtimeSinceStartup;

            _metadata = new RunMetadata
            {
                runId = config.runId,
                modelName = config.modelName,
                mapFile = config.mapFile,
                mapSize = config.mapSize,
                goals = config.goals,
                repetition = config.repetition,
                cutoffTicks = config.cutoffTicks,
                startTime = DateTime.Now.ToString("o")
            };
        }

        /// <summary>
        /// Scans the loaded map to collect tile and resource statistics.
        /// Call after the map is fully loaded and resources are spawned.
        /// </summary>
        public void CaptureMapStatistics()
        {
            var stats = new MapStatistics();

            // Count tiles by type
            var tileGrid = UnityEngine.Object.FindFirstObjectByType<TileGrid>();
            if (tileGrid != null)
            {
                // FindAllTiles with always-true predicate to get all tiles
                var allTiles = tileGrid.FindAllTiles(_ => true);
                stats.totalTiles = allTiles.Count;
                foreach (var tile in allTiles)
                {
                    var style = tile.Archetype?.Style ?? TileStyle.Grass;
                    switch (style)
                    {
                        case TileStyle.Grass:    stats.grassTiles++; break;
                        case TileStyle.Forest:   stats.forestTiles++; break;
                        case TileStyle.Mountain: stats.mountainTiles++; break;
                        case TileStyle.Water:    stats.waterTiles++; break;
                        case TileStyle.Coast:    stats.coastTiles++; break;
                    }
                }
            }

            // Count resource nodes by type
            var nodes = UnityEngine.Object.FindObjectsByType<ResourceNode>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var node in nodes)
            {
                if (node == null) continue;
                switch (node.resourceType)
                {
                    case ResourceNode.ResourceType.Tree:  stats.treeCount++; break;
                    case ResourceNode.ResourceType.Stone:
                        if (node.isMineShaft) stats.mineCount++;
                        else stats.stoneCount++;
                        break;
                    case ResourceNode.ResourceType.Seed:  stats.seedCount++; break;
                    case ResourceNode.ResourceType.Crop:  stats.cropCount++; break;
                }
            }

            _metadata.mapStats = stats;

            // Capture map seed from TWCBridge
            var bridge = UnityEngine.Object.FindFirstObjectByType<TWCBridge>();
            if (bridge != null)
                _metadata.mapSeed = bridge.MapSeed;
        }

        /// <summary>
        /// Writes the final metadata file with run results.
        /// </summary>
        public void FinalizeAndWrite(string abortReason, LLMSessionStats sessionStats)
        {
            _metadata.endTime = DateTime.Now.ToString("o");
            _metadata.abortReason = abortReason;
            _metadata.finalTick = SimTickTracker.CurrentTick;
            _metadata.elapsedGameTimeSeconds = Time.time;
            _metadata.elapsedRealTimeSeconds = Time.realtimeSinceStartup - _startRealTime;
            _metadata.sessionStats = sessionStats;

            string json = JsonUtility.ToJson(_metadata, true);
            string path = Path.Combine(_outputDir, "run_metadata.json");
            File.WriteAllText(path, json);
        }
    }
}
