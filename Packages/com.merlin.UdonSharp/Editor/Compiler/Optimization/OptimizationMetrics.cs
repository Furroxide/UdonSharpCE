using System.Collections.Generic;

namespace UdonSharp.Compiler.Optimization
{
    /// <summary>
    /// Tracks counts collected while optimizing. Used for debugging and future telemetry.
    /// </summary>
    internal class OptimizationMetrics
    {
        public int InstructionsRemoved { get; set; }
        public int InstructionsReplaced { get; set; }
        public int PassesRun { get; set; }
        public int ValuesCoalesced { get; set; }
        public int JumpsThreaded { get; set; }
        public int DeadBlocksRemoved { get; set; }

        public Dictionary<string, int> PassSpecificMetrics { get; } = new Dictionary<string, int>();

        public void RecordPassMetric(string passName, string metric, int count)
        {
            string key = $"{passName}.{metric}";

            if (!PassSpecificMetrics.ContainsKey(key))
                PassSpecificMetrics[key] = 0;

            PassSpecificMetrics[key] += count;
        }
    }
}
