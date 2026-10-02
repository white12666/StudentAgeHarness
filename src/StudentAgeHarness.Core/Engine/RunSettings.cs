using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StudentAgeHarness.Engine
{
    /// <summary>run.json 里的一项被测插件：复制进隔离 BepInEx 的一个 DLL 或一个目录（路径相对运行目录）。</summary>
    internal sealed class PluginUnderTest
    {
        internal string Path;
        internal bool IsFolder;
        internal string Name;
    }

    /// <summary>运行目录里的 run.json（由 sah 启动器生成，格式 studentage-harness-run/1）。</summary>
    internal sealed class RunSettings
    {
        internal const string FormatId = "studentage-harness-run/1";

        internal string RunId;
        internal string RunDirectory;
        internal List<string> Scenarios = new List<string>();
        internal float TimeoutSec = 600f;
        internal float StartupTimeoutSec = 180f;
        internal int Width;
        internal int Height;
        internal string Window = "visible";
        internal bool Mute = true;
        internal bool RedactPaths = true;
        internal List<PluginUnderTest> PluginsUnderTest = new List<PluginUnderTest>();
        internal List<string> ExpectPlugins = new List<string>();
        internal List<string> Packs = new List<string>();

        /// <summary>本次启用的创意工坊 mod ID；InheritWorkshopMods 为 true 时沿用玩家自己的启用列表。</summary>
        internal List<string> WorkshopMods = new List<string>();
        internal bool InheritWorkshopMods;
        internal JObject Source;

        internal bool IsBackground => string.Equals(Window, "background", StringComparison.OrdinalIgnoreCase);
        internal bool HasExpectedResolution => Width > 0 && Height > 0;
        internal string ScreenshotDirectory => Path.Combine(RunDirectory, "screenshots");
        internal string ProfileDirectory => Path.Combine(RunDirectory, "profile");

        internal static RunSettings Load(string path)
        {
            string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            return Parse(json, Path.GetDirectoryName(Path.GetFullPath(path)));
        }

        internal static JObject ParseObject(string json)
        {
            // 关掉日期自动解析：否则 ISO 时间字符串会按当前区域格式回写，报告和比较都会走样。
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        internal static RunSettings Parse(string json, string runDirectory)
        {
            JObject root = ParseObject(json);
            string format = (string)root["format"];
            if (!string.Equals(format, FormatId, StringComparison.Ordinal))
                throw new InvalidDataException("run.json 的 format 应为 " + FormatId + "，实际为 " + (format ?? "(空)"));

            var settings = new RunSettings
            {
                RunDirectory = runDirectory,
                RunId = RequireString(root, "runId"),
                Window = (string)root["window"] ?? "visible",
                Mute = (bool?)root["mute"] ?? true,
                RedactPaths = (bool?)root["redactPaths"] ?? true,
                TimeoutSec = Positive((float?)root["timeoutSec"], 600f),
                StartupTimeoutSec = Positive((float?)root["startupTimeoutSec"], 180f),
                Source = root["source"] as JObject
            };

            if (settings.Window != "visible" && settings.Window != "background")
                throw new InvalidDataException("window must be visible or background.");

            if (root["resolution"] is JObject resolution)
            {
                settings.Width = (int?)resolution["width"] ?? 0;
                settings.Height = (int?)resolution["height"] ?? 0;
                if (settings.Width < 320 || settings.Width > 8192 || settings.Height < 200 || settings.Height > 8192)
                    throw new InvalidDataException("Resolution is outside the supported range.");
            }

            settings.Scenarios = Strings(root["scenarios"]);
            settings.ExpectPlugins = Strings(root["expectPlugins"]);
            settings.Packs = Strings(root["packs"]);
            if (settings.Scenarios.Count == 0) settings.Scenarios.Add("startup");

            if (root["pluginsUnderTest"] is JArray plugins)
            {
                foreach (JToken item in plugins)
                {
                    if (!(item is JObject entry)) continue;
                    string path = (string)entry["path"];
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    settings.PluginsUnderTest.Add(new PluginUnderTest
                    {
                        Path = path.Trim(),
                        IsFolder = string.Equals((string)entry["kind"], "folder", StringComparison.OrdinalIgnoreCase),
                        Name = (string)entry["name"] ?? System.IO.Path.GetFileName(path.Trim().TrimEnd('/', '\\'))
                    });
                }
            }

            JToken workshop = root["workshopMods"];
            if (workshop != null && workshop.Type == JTokenType.String &&
                string.Equals((string)workshop, "inherit", StringComparison.OrdinalIgnoreCase))
                settings.InheritWorkshopMods = true;
            else
                foreach (string id in Strings(workshop))
                    if (ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed) && parsed > 0)
                        settings.WorkshopMods.Add(id);
                    else
                        throw new InvalidDataException("Invalid Workshop item ID.");
            return settings;
        }

        private static string RequireString(JObject root, string name)
        {
            string value = (string)root[name];
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("run.json 缺少 " + name);
            return value;
        }

        private static float Positive(float? value, float fallback)
        {
            if (!value.HasValue) return fallback;
            if (float.IsInfinity(value.Value) || float.IsNaN(value.Value) || value.Value <= 0f)
                throw new InvalidDataException("Timeout must be finite and positive.");
            return value.Value;
        }

        private static List<string> Strings(JToken token)
        {
            var result = new List<string>();
            if (token is JArray array)
            {
                foreach (JToken item in array)
                {
                    string text = item.Type == JTokenType.String || item.Type == JTokenType.Integer
                        ? Convert.ToString(((JValue)item).Value, CultureInfo.InvariantCulture)
                        : null;
                    if (!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
                }
            }
            else if (token != null && token.Type == JTokenType.String)
            {
                foreach (string part in ((string)token).Split(','))
                    if (!string.IsNullOrWhiteSpace(part)) result.Add(part.Trim());
            }
            return result;
        }
    }
}
