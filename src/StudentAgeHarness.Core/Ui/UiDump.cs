using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StudentAgeHarness
{
    /// <summary>
    /// 把当前界面导出成 JSON：可见文字、可点击目标、输入框、打开的视图和最上层视图的节点树。
    /// 写场景时用它查按钮文字和节点路径，不需要反编译游戏。
    /// </summary>
    internal static class UiDump
    {
        private const int MaxDepth = 10;
        private const int MaxChildren = 60;
        private const int MaxNodes = 1500;

        internal static JObject Capture(IHarnessGame game)
        {
            var dump = new JObject
            {
                ["capturedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["screen"] = new JObject { ["width"] = Screen.width, ["height"] = Screen.height }
            };

            GameObject topRoot = null;
            if (game != null)
            {
                try
                {
                    dump["topView"] = game.TopViewName();
                    dump["openViews"] = new JArray(game.OpenViewNames().ToArray());
                    topRoot = game.TopViewRoot();
                }
                catch (Exception ex)
                {
                    dump["viewError"] = ex.Message;
                }
            }

            dump["visibleTexts"] = new JArray(HarnessUi.Instance.VisibleTexts(300).ToArray());
            dump["clickables"] = new JArray(HarnessUi.Instance.ClickableTargets(300).Select(TargetJson));
            dump["inputs"] = new JArray(HarnessUi.Instance.InputTargets().Select(TargetJson));

            if (topRoot != null)
            {
                int budget = MaxNodes;
                dump["topViewTree"] = Node(topRoot.transform, 0, ref budget);
                if (budget <= 0) dump["topViewTreeTruncated"] = true;
            }
            return dump;
        }

        private static JObject TargetJson(HarnessUiTarget target)
        {
            return new JObject
            {
                ["text"] = target.Text,
                ["kind"] = target.Kind,
                ["path"] = target.Path,
                ["rect"] = RectJson(target.ScreenRect)
            };
        }

        private static JObject RectJson(Rect rect)
        {
            return new JObject
            {
                ["x"] = Mathf.Round(rect.x),
                ["y"] = Mathf.Round(rect.y),
                ["width"] = Mathf.Round(rect.width),
                ["height"] = Mathf.Round(rect.height)
            };
        }

        private static JObject Node(Transform transform, int depth, ref int budget)
        {
            budget--;
            var node = new JObject
            {
                ["name"] = transform.name,
                ["active"] = transform.gameObject.activeSelf
            };
            var components = new JArray();
            foreach (Component component in transform.GetComponents<Component>())
            {
                if (component == null || component is Transform || component is CanvasRenderer) continue;
                components.Add(component.GetType().Name);
            }
            if (components.Count > 0) node["components"] = components;

            string text = ReadOwnText(transform);
            if (!string.IsNullOrEmpty(text)) node["text"] = text;
            if (transform is RectTransform rect && transform.gameObject.activeInHierarchy &&
                HarnessUi.TryGetScreenRect(rect, out Rect screenRect))
                node["rect"] = RectJson(screenRect);

            if (transform.childCount == 0) return node;
            if (depth >= MaxDepth || budget <= 0)
            {
                node["childrenOmitted"] = transform.childCount;
                return node;
            }
            var children = new JArray();
            int count = Math.Min(transform.childCount, MaxChildren);
            for (int index = 0; index < count && budget > 0; index++)
                children.Add(Node(transform.GetChild(index), depth + 1, ref budget));
            node["children"] = children;
            if (transform.childCount > children.Count) node["childrenOmitted"] = transform.childCount - children.Count;
            return node;
        }

        private static string ReadOwnText(Transform transform)
        {
            Text text = transform.GetComponent<Text>();
            if (text != null) return Clip(HarnessUi.Normalize(HarnessUi.StripRichText(text.text)));
            TMP_Text tmp = transform.GetComponent<TMP_Text>();
            if (tmp != null) return Clip(HarnessUi.Normalize(HarnessUi.StripRichText(tmp.text)));
            return null;
        }

        private static string Clip(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return value.Length > 120 ? value.Substring(0, 120) + "…" : value;
        }
    }
}
