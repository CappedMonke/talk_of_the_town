using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Benchmark.Loggers
{
    /// <summary>
    /// Logs each LLM batch decision call to llm_decisions.jsonl (JSON Lines format).
    /// </summary>
    public class LLMDecisionLogger
    {
        private readonly string _filePath;
        private readonly int _flushThreshold;
        private readonly List<string> _buffer = new();

        public LLMDecisionLogger(string outputDir, int flushThreshold)
        {
            _filePath = Path.Combine(outputDir, "llm_decisions.jsonl");
            _flushThreshold = flushThreshold;
            // Write empty file to ensure it exists
            File.WriteAllText(_filePath, "");
        }

        public void LogDecision(BatchDecisionLog log)
        {
            var entry = new LLMDecisionLogEntry
            {
                simTick = log.simTick,
                triggerReason = log.triggerReason,
                inputState = log.inputState,
                rawResponse = log.rawResponse,
                tokenCount = new TokenCount
                {
                    prompt = log.metrics.promptEvalCount,
                    response = log.metrics.evalCount,
                    total = log.metrics.totalTokens
                },
                responseTimeSeconds = log.metrics.responseTime,
                success = log.metrics.success,
                errorMessage = log.metrics.errorMessage
            };

            // Convert parsed decisions to assignment list
            if (log.parsedDecisions != null)
            {
                foreach (var kv in log.parsedDecisions)
                {
                    var d = kv.Value;
                    entry.parsedAssignments.Add(new ParsedAssignment
                    {
                        villager = kv.Key,
                        job = d.jobName,
                        buildingType = d.buildingType,
                        targetX = d.targetX,
                        targetY = d.targetY,
                        gatherAmount = d.gatherAmount,
                        restUntilEnergy = d.restUntilEnergy,
                        reason = d.reason
                    });
                }
            }

            // Convert parsed goals
            if (log.parsedGoals != null)
            {
                foreach (var g in log.parsedGoals)
                {
                    entry.parsedGoals.Add(new ParsedGoal
                    {
                        type = g.type,
                        resource = g.resource,
                        amount = g.amount,
                        priority = g.priority,
                        description = g.description
                    });
                }
            }

            _buffer.Add(JsonUtility.ToJson(entry));

            // LLM decisions are rare — flush immediately so data is never lost
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
