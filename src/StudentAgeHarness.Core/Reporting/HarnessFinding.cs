using System;
using System.Collections.Generic;

namespace StudentAgeHarness
{
    /// <summary>严重程度。error 会让这次运行判为失败；warning 在 -Strict 下判失败；info 只留档。</summary>
    public static class HarnessSeverity
    {
        public const string Error = "error";
        public const string Warning = "warning";
        public const string Info = "info";

        public static int Rank(string severity)
        {
            if (string.Equals(severity, Error, StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(severity, Warning, StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        /// <summary>把 severity 封顶到 max（例如把 warning 降为 info）。</summary>
        public static string Cap(string severity, string max)
        {
            return Rank(severity) > Rank(max) ? max : severity;
        }
    }

    /// <summary>报告里的一条发现。</summary>
    public sealed class HarnessFinding
    {
        public string Severity = HarnessSeverity.Info;

        /// <summary>来源分类：harness、scenario、layout、unity-log、bepinex-log、isolation 等。</summary>
        public string Category;

        /// <summary>稳定的规则名，便于脚本按规则过滤。</summary>
        public string Rule;

        /// <summary>所属场景；为空时由报告自动填当前场景。</summary>
        public string Scenario;

        /// <summary>所属步骤；为空时由报告自动填当前步骤。</summary>
        public string Step;

        /// <summary>出问题的对象：界面节点路径、文件、方法名等。</summary>
        public string Path;

        public string Message;

        public Dictionary<string, object> Metrics;

        public static HarnessFinding Create(string severity, string category, string rule, string message,
            string path = null)
        {
            return new HarnessFinding
            {
                Severity = severity ?? HarnessSeverity.Info,
                Category = category ?? string.Empty,
                Rule = rule ?? string.Empty,
                Message = message ?? string.Empty,
                Path = path
            };
        }

        public HarnessFinding WithMetric(string key, object value)
        {
            if (string.IsNullOrEmpty(key)) return this;
            if (Metrics == null) Metrics = new Dictionary<string, object>(StringComparer.Ordinal);
            Metrics[key] = value;
            return this;
        }
    }
}
