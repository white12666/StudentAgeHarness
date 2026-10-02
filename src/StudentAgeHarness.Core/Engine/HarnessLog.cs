using UnityEngine;

namespace StudentAgeHarness.Engine
{
    internal static class HarnessLog
    {
        private static IHarnessLogSink sink;

        internal static void SetSink(IHarnessLogSink value)
        {
            sink = value;
        }

        internal static void Info(string message)
        {
            if (sink != null) sink.Info(message);
            else Debug.Log("[StudentAge Harness] " + message);
        }

        internal static void Warning(string message)
        {
            if (sink != null) sink.Warning(message);
            else Debug.LogWarning("[StudentAge Harness] " + message);
        }

        internal static void Error(string message)
        {
            if (sink != null) sink.Error(message);
            else Debug.LogError("[StudentAge Harness] " + message);
        }
    }
}
