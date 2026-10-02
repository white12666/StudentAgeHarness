using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace StudentAgeHarness.Engine
{
    /// <summary>一个提供场景的程序集：插件自带的工具场景，或 run.json 里列出的场景包。</summary>
    internal sealed class ScenarioSource
    {
        internal Assembly Assembly;
        internal string Name;
        internal string File;
        internal bool AllowExtensions = true;
    }

    internal sealed class ScenarioEntry
    {
        internal string Name;
        internal HarnessScenarioAttribute Attribute;
        internal Type Type;
        internal string Pack;
        internal int PackIndex;

        internal string[] Tags => Attribute.Tags ?? new string[0];

        internal bool HasTag(string tag)
        {
            foreach (string item in Tags)
                if (string.Equals(item, tag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal IHarnessScenario Create()
        {
            return (IHarnessScenario)Activator.CreateInstance(Type);
        }
    }

    internal static class ScenarioCatalog
    {
        internal const string ManualTag = "manual";

        internal static List<ScenarioEntry> Discover(IList<ScenarioSource> sources, HarnessReport report,
            List<IHarnessExtension> extensions, JArray packsJson)
        {
            var all = new List<ScenarioEntry>();
            var byName = new Dictionary<string, ScenarioEntry>(StringComparer.OrdinalIgnoreCase);
            for (int packIndex = 0; packIndex < sources.Count; packIndex++)
            {
                ScenarioSource source = sources[packIndex];
                Type[] types;
                try
                {
                    types = source.Assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(type => type != null).ToArray();
                    string loader = string.Join("；", ex.LoaderExceptions.Where(item => item != null)
                        .Select(item => item.Message).Distinct().Take(3).ToArray());
                    report.AddFinding(HarnessSeverity.Warning, "harness", "pack-types-partially-loaded",
                        "场景包 " + source.Name + " 有部分类型加载失败（通常是缺少它引用的 mod 程序集）：" + loader,
                        source.File);
                }
                catch (Exception ex)
                {
                    report.AddFinding(HarnessSeverity.Error, "harness", "pack-load-failed",
                        "无法读取场景包 " + source.Name + " 的类型：" + ex.Message, source.File);
                    continue;
                }

                var packScenarios = new List<ScenarioEntry>();
                int extensionCount = 0;
                foreach (Type type in types)
                {
                    if (type == null || !type.IsClass || type.IsAbstract) continue;
                    HarnessScenarioAttribute attribute = null;
                    try
                    {
                        attribute = (HarnessScenarioAttribute)System.Attribute.GetCustomAttribute(type,
                            typeof(HarnessScenarioAttribute), false);
                    }
                    catch
                    {
                    }

                    if (attribute != null)
                    {
                        string problem = Validate(type, attribute, byName);
                        if (problem != null)
                        {
                            report.AddFinding(HarnessSeverity.Error, "harness", "scenario-invalid", problem,
                                type.FullName);
                            continue;
                        }
                        var entry = new ScenarioEntry
                        {
                            Name = attribute.Name.Trim(),
                            Attribute = attribute,
                            Type = type,
                            Pack = source.Name,
                            PackIndex = packIndex
                        };
                        byName[entry.Name] = entry;
                        packScenarios.Add(entry);
                    }
                    else if (source.AllowExtensions && typeof(IHarnessExtension).IsAssignableFrom(type) &&
                             type.GetConstructor(Type.EmptyTypes) != null)
                    {
                        try
                        {
                            extensions.Add((IHarnessExtension)Activator.CreateInstance(type));
                            extensionCount++;
                        }
                        catch (Exception ex)
                        {
                            report.AddFinding(HarnessSeverity.Error, "harness", "extension-create-failed",
                                "无法创建扩展 " + type.FullName + "：" + Unwrap(ex).Message, source.File);
                        }
                    }
                }

                packScenarios.Sort(CompareInPack);
                all.AddRange(packScenarios);
                packsJson?.Add(new JObject
                {
                    ["name"] = source.Name,
                    ["file"] = source.File,
                    ["assembly"] = source.Assembly.GetName().Name,
                    ["version"] = source.Assembly.GetName().Version?.ToString(),
                    ["scenarios"] = new JArray(packScenarios.Select(item => item.Name)),
                    ["extensions"] = extensionCount
                });
            }
            return all;
        }

        private static string Validate(Type type, HarnessScenarioAttribute attribute,
            Dictionary<string, ScenarioEntry> byName)
        {
            if (string.IsNullOrWhiteSpace(attribute.Name))
                return "场景 " + type.FullName + " 的 HarnessScenario 名字为空。";
            if (!typeof(IHarnessScenario).IsAssignableFrom(type))
                return "场景 " + attribute.Name + "（" + type.FullName + "）没有实现 IHarnessScenario。";
            if (type.GetConstructor(Type.EmptyTypes) == null)
                return "场景 " + attribute.Name + "（" + type.FullName + "）缺少公开的无参构造函数。";
            if (byName.TryGetValue(attribute.Name.Trim(), out ScenarioEntry existing))
                return "场景名 " + attribute.Name + " 重复：" + existing.Type.FullName + " 和 " + type.FullName +
                    "，只保留前者。";
            return null;
        }

        private static int CompareInPack(ScenarioEntry left, ScenarioEntry right)
        {
            int order = left.Attribute.Order.CompareTo(right.Attribute.Order);
            return order != 0 ? order : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 按 run.json 的选择项挑场景。选择项：场景名、tag:标签、* （全部，不含 manual 标签的场景）。
        /// 结果按选择项的书写顺序排列并去重；explicitSelection 记录被直接点名的场景。
        /// </summary>
        internal static List<ScenarioEntry> Select(List<ScenarioEntry> all, IList<string> tokens,
            HarnessReport report, out HashSet<ScenarioEntry> explicitSelection)
        {
            explicitSelection = new HashSet<ScenarioEntry>();
            var selected = new List<ScenarioEntry>();
            var seen = new HashSet<ScenarioEntry>();
            IList<string> effective = tokens == null || tokens.Count == 0 ? new[] { "*" } : tokens;
            foreach (string raw in effective)
            {
                string token = (raw ?? string.Empty).Trim();
                if (token.Length == 0) continue;
                List<ScenarioEntry> matches;
                if (token == "*" || string.Equals(token, "all", StringComparison.OrdinalIgnoreCase))
                {
                    matches = all.Where(item => !item.HasTag(ManualTag)).ToList();
                }
                else if (token.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
                {
                    string tag = token.Substring(4).Trim();
                    matches = all.Where(item => item.HasTag(tag)).ToList();
                    if (matches.Count == 0)
                        report.AddFinding(HarnessSeverity.Error, "harness", "unknown-scenario-tag",
                            "没有场景带标签 " + tag + "。", token);
                }
                else
                {
                    ScenarioEntry match = all.FirstOrDefault(item =>
                        string.Equals(item.Name, token, StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                    {
                        report.AddFinding(HarnessSeverity.Error, "harness", "unknown-scenario",
                            "找不到场景 " + token + "，已忽略。可用的场景：" +
                            string.Join(", ", all.Select(item => item.Name).ToArray()), token);
                        continue;
                    }
                    explicitSelection.Add(match);
                    matches = new List<ScenarioEntry> { match };
                }
                foreach (ScenarioEntry entry in matches)
                    if (seen.Add(entry)) selected.Add(entry);
            }

            ScenarioEntry alone = selected.FirstOrDefault(item => item.Attribute.RunAlone);
            if (alone != null && selected.Count > 1)
            {
                report.AddFinding(HarnessSeverity.Error, "harness", "scenario-must-run-alone",
                    "场景 " + alone.Name + " 要求单独运行，本次只保留它，其它 " + (selected.Count - 1) + " 个场景没有运行。",
                    alone.Name);
                selected = new List<ScenarioEntry> { alone };
            }
            return selected;
        }

        internal static Exception Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
    }
}
