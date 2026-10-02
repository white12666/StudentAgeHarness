using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;

namespace StudentAgeHarness.Plugin.Adapter
{
    /// <summary>
    /// 运行结束时扫描隔离目录里的 BepInEx/LogOutput.log，把 Error/Fatal 记进报告。
    /// 依赖缺失之类的加载错误发生在本插件启用之前，只能从日志文件里拿到。
    /// BepInEx 自己和被测插件的错误记 error，其它插件的错误记 warning；Unity 日志由异常监听单独处理。
    /// </summary>
    internal static class BepInExLogScan
    {
        private const int MaxFindings = 40;
        private static readonly Regex Header = new Regex(@"^\[(Error|Fatal)\s*:\s*(.+?)\]\s?(.*)$", RegexOptions.Compiled);
        private static readonly Regex AnyHeader = new Regex(@"^\[(Debug|Info|Message|Warning|Error|Fatal)\s*:", RegexOptions.Compiled);

        internal static void AddFindings(HarnessReport report, IEnumerable<string> pluginUnderTestNames)
        {
            string path = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
            string[] lines;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    lines = reader.ReadToEnd().Split('\n');
            }
            catch (Exception ex)
            {
                report.AddFinding(HarnessSeverity.Info, "bepinex-log", "log-unreadable", "无法读取 BepInEx 日志：" + ex.Message, path);
                return;
            }

            var underTest = new HashSet<string>(pluginUnderTestNames ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int dropped = 0;
            for (int index = 0; index < lines.Length; index++)
            {
                Match match = Header.Match(lines[index].TrimEnd('\r'));
                if (!match.Success) continue;
                string source = match.Groups[2].Value.Trim();
                if (source == "Unity Log" || source == HarnessPlugin.PluginName) continue;
                var message = new StringBuilder(match.Groups[3].Value.Trim());
                for (int next = index + 1; next < lines.Length && next <= index + 4; next++)
                {
                    string continuation = lines[next].TrimEnd('\r');
                    if (AnyHeader.IsMatch(continuation)) break;
                    if (continuation.Trim().Length > 0) message.Append('\n').Append(continuation.Trim());
                }
                string text = message.ToString();
                if (!seen.Add(source + "\n" + text)) continue;
                if (seen.Count > MaxFindings)
                {
                    dropped++;
                    continue;
                }
                bool serious = match.Groups[1].Value == "Fatal" || source == "BepInEx" || underTest.Contains(source);
                report.AddFinding(new HarnessFinding
                {
                    Severity = serious ? HarnessSeverity.Error : HarnessSeverity.Warning,
                    Category = "bepinex-log",
                    Rule = source == "BepInEx" ? "bepinex-error" : "plugin-log-error",
                    Scenario = string.Empty,
                    Step = string.Empty,
                    Path = source,
                    Message = "[" + source + "] " + (text.Length > 600 ? text.Substring(0, 600) + "…" : text)
                });
            }
            if (dropped > 0)
                report.AddFinding(HarnessSeverity.Warning, "bepinex-log", "log-errors-truncated",
                    "BepInEx 日志里还有 " + dropped + " 条不同的错误没有列出，请直接查看 LogOutput.log。", path);
        }
    }
}
