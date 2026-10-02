using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace StudentAgeHarness
{
    /// <summary>
    /// 把报告里的本机绝对路径换成占位符（&lt;run&gt;、&lt;game&gt;、&lt;localLow&gt;、%USERPROFILE%），
    /// 这样报告可以直接分享，不会带出用户名和目录结构。
    /// </summary>
    internal sealed class PathRedactor
    {
        internal static readonly PathRedactor None = new PathRedactor(new List<KeyValuePair<string, string>>());

        private readonly List<KeyValuePair<string, string>> rules;

        private PathRedactor(List<KeyValuePair<string, string>> rules)
        {
            this.rules = rules;
        }

        internal bool IsEnabled => rules.Count > 0;

        internal static PathRedactor Create(string runDirectory, string gameRoot, string localLow, string userProfile)
        {
            var rules = new List<KeyValuePair<string, string>>();
            Add(rules, runDirectory, "<run>");
            Add(rules, gameRoot, "<game>");
            Add(rules, localLow, "<localLow>");
            Add(rules, userProfile, "%USERPROFILE%");
            // 先替换更长（更具体）的前缀：运行目录常常位于游戏目录或用户目录之下。
            rules.Sort((left, right) => right.Key.Length.CompareTo(left.Key.Length));
            return new PathRedactor(rules);
        }

        private static void Add(List<KeyValuePair<string, string>> rules, string path, string token)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { full = path; }
            full = full.TrimEnd('\\', '/');
            if (full.Length < 3) return;
            string backslash = full.Replace('/', '\\');
            string slash = full.Replace('\\', '/');
            rules.Add(new KeyValuePair<string, string>(backslash, token));
            if (!string.Equals(slash, backslash, StringComparison.Ordinal))
                rules.Add(new KeyValuePair<string, string>(slash, token));
        }

        internal string Apply(string value)
        {
            if (string.IsNullOrEmpty(value) || rules.Count == 0) return value;
            foreach (KeyValuePair<string, string> rule in rules)
                value = ReplaceIgnoreCase(value, rule.Key, rule.Value);
            return value;
        }

        internal JToken Apply(JToken token)
        {
            if (token == null || rules.Count == 0) return token;
            switch (token.Type)
            {
                case JTokenType.Object:
                    foreach (JProperty property in ((JObject)token).Properties())
                        property.Value = Apply(property.Value);
                    return token;
                case JTokenType.Array:
                    var array = (JArray)token;
                    for (int index = 0; index < array.Count; index++)
                        array[index] = Apply(array[index]);
                    return token;
                case JTokenType.String:
                    return new JValue(Apply((string)token));
                default:
                    return token;
            }
        }

        internal static string ReplaceIgnoreCase(string value, string oldValue, string newValue)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(oldValue)) return value;
            int index = value.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return value;
            var builder = new StringBuilder(value.Length);
            int start = 0;
            while (index >= 0)
            {
                builder.Append(value, start, index - start).Append(newValue);
                start = index + oldValue.Length;
                index = value.IndexOf(oldValue, start, StringComparison.OrdinalIgnoreCase);
            }
            builder.Append(value, start, value.Length - start);
            return builder.ToString();
        }
    }
}
