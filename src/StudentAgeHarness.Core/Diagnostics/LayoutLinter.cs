using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace StudentAgeHarness.Diagnostics
{
    /// <summary>
    /// UGUI 布局检查：遍历激活的 RectTransform 树，报告 (a) 文字溢出/截断 (b) 子节点越界
    /// (c) 布局组里的兄弟重叠 (d) 可点击区域过小。原则是宁可漏报也不乱报，容差和白名单都集中在常量里。
    /// TextMeshPro 通过反射探测，目标界面没用 TMP 时不会出错。
    /// </summary>
    public static class LayoutLinter
    {
        private const string Category = "layout";

        public const float TextTolerance = 2f;
        public const float ChildBoundsTolerance = 2f;
        public const float SiblingOverlapMaxArea = 4f;
        public const float MinTapTargetSize = 24f;

        /// <summary>名字里带这些词的节点按设计会伸出父节点（端口、浮层、气泡），跳过越界检查。</summary>
        private static readonly string[] IntentionalOverflowNameTokens =
        {
            "Port", "FlowIn", "FlowOut", "Overlay", "Tooltip"
        };

        private static bool tmpProbed;
        private static Type tmpTextType;
        private static PropertyInfo tmpTextProp;
        private static PropertyInfo tmpTruncatedProp;
        private static PropertyInfo tmpPreferredHeightProp;
        private static PropertyInfo tmpOverflowProp;

        /// <summary>对 root 及其所有激活的子节点做四类检查。</summary>
        public static List<HarnessFinding> Run(GameObject root, string viewName)
        {
            var findings = new List<HarnessFinding>();
            if (root == null)
            {
                findings.Add(HarnessFinding.Create(HarnessSeverity.Warning, Category, "root-null",
                    "布局检查的根节点为空（界面没有打开？）", viewName));
                return findings;
            }
            Transform rootTransform = root.transform;
            CheckUguiTexts(root, rootTransform, viewName, findings);
            CheckTmpTexts(root, rootTransform, viewName, findings);
            CheckChildBounds(root, rootTransform, viewName, findings);
            CheckSiblingOverlap(root, rootTransform, viewName, findings);
            CheckTapTargets(root, rootTransform, viewName, findings);
            return findings;
        }

        private static void CheckUguiTexts(GameObject root, Transform rootTransform, string viewName,
            List<HarnessFinding> findings)
        {
            foreach (Text text in root.GetComponentsInChildren<Text>(false))
            {
                if (text == null || string.IsNullOrEmpty(text.text)) continue;
                Rect rect = text.rectTransform.rect;
                if (rect.width <= 0f || rect.height <= 0f) continue;
                string path = BuildPath(text.transform, rootTransform, viewName);

                // 主信号：截断模式下可见字符少于正文（尾部空白不计）。
                TextGenerator generator = text.cachedTextGenerator;
                int budget = text.text.TrimEnd().Length;
                if (text.verticalOverflow == VerticalWrapMode.Truncate && generator != null &&
                    generator.characterCountVisible > 0 && generator.characterCountVisible < budget)
                {
                    findings.Add(HarnessFinding.Create(HarnessSeverity.Warning, Category, "text-truncated",
                            "文字被截断：可见字符少于正文", path)
                        .WithMetric("visibleChars", generator.characterCountVisible)
                        .WithMetric("totalChars", budget)
                        .WithMetric("rectWidth", rect.width)
                        .WithMetric("rectHeight", rect.height));
                    continue;
                }

                // 次信号：换行后首选宽高都超框，但实际没被裁掉，只留档。
                if (text.horizontalOverflow == HorizontalWrapMode.Wrap &&
                    text.verticalOverflow == VerticalWrapMode.Truncate &&
                    text.preferredWidth > rect.width + TextTolerance &&
                    text.preferredHeight > rect.height + TextTolerance)
                    findings.Add(HarnessFinding.Create(HarnessSeverity.Info, Category, "text-truncated",
                            "文字换行后余量不足（没有实际裁切）", path)
                        .WithMetric("preferredWidth", text.preferredWidth)
                        .WithMetric("preferredHeight", text.preferredHeight)
                        .WithMetric("rectWidth", rect.width)
                        .WithMetric("rectHeight", rect.height));
            }
        }

        private static void CheckTmpTexts(GameObject root, Transform rootTransform, string viewName,
            List<HarnessFinding> findings)
        {
            EnsureTmpProbe();
            if (tmpTextType == null) return;
            Component[] components;
            try { components = root.GetComponentsInChildren(tmpTextType, false); }
            catch (Exception) { return; }
            foreach (Component component in components)
            {
                if (component == null) continue;
                try
                {
                    string content = tmpTextProp != null ? tmpTextProp.GetValue(component, null) as string : null;
                    if (string.IsNullOrEmpty(content)) continue;
                    var rectTransform = component.transform as RectTransform;
                    if (rectTransform == null) continue;
                    Rect rect = rectTransform.rect;
                    if (rect.width <= 0f || rect.height <= 0f) continue;
                    bool truncated = tmpTruncatedProp != null && (bool)tmpTruncatedProp.GetValue(component, null);
                    float preferredHeight = tmpPreferredHeightProp != null
                        ? (float)tmpPreferredHeightProp.GetValue(component, null)
                        : 0f;
                    bool intendedEllipsis = false;
                    if (truncated && tmpOverflowProp != null)
                    {
                        object mode = tmpOverflowProp.GetValue(component, null);
                        intendedEllipsis = mode != null &&
                            string.Equals(mode.ToString(), "Ellipsis", StringComparison.Ordinal);
                    }
                    if (truncated || preferredHeight > rect.height + TextTolerance)
                        findings.Add(HarnessFinding.Create(
                                intendedEllipsis ? HarnessSeverity.Info : HarnessSeverity.Warning, Category,
                                "text-truncated",
                                intendedEllipsis ? "TMP 省略号截断（有意设计）"
                                    : truncated ? "TMP 文字被截断" : "TMP 文字高度超出文本框",
                                BuildPath(component.transform, rootTransform, viewName))
                            .WithMetric("isTextTruncated", truncated)
                            .WithMetric("preferredHeight", preferredHeight)
                            .WithMetric("rectHeight", rect.height));
                }
                catch (Exception)
                {
                    // 读 TMP 属性失败就当没有发现，检查器不能把场景带崩。
                }
            }
        }

        private static void EnsureTmpProbe()
        {
            if (tmpProbed) return;
            tmpProbed = true;
            try
            {
                tmpTextType = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro", false);
                if (tmpTextType == null)
                {
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        tmpTextType = assembly.GetType("TMPro.TMP_Text", false);
                        if (tmpTextType != null) break;
                    }
                }
                if (tmpTextType == null) return;
                tmpTextProp = tmpTextType.GetProperty("text");
                tmpTruncatedProp = tmpTextType.GetProperty("isTextTruncated");
                tmpPreferredHeightProp = tmpTextType.GetProperty("preferredHeight");
                tmpOverflowProp = tmpTextType.GetProperty("overflowMode");
            }
            catch (Exception)
            {
                tmpTextType = null;
            }
        }

        private static void CheckChildBounds(GameObject root, Transform rootTransform, string viewName,
            List<HarnessFinding> findings)
        {
            RectTransform[] rects = root.GetComponentsInChildren<RectTransform>(false);
            var corners = new Vector3[4];
            var local = new Vector2[4];
            foreach (RectTransform child in rects)
            {
                if (child == null || child.transform == rootTransform) continue;
                var parent = child.parent as RectTransform;
                if (parent == null) continue;
                if (IsBoundsWhitelisted(child, parent)) continue;
                // 没有可见背景、也没有布局组的父节点通常只是定位锚点，子节点挂在外面是常见写法。
                if (parent.GetComponent<Graphic>() == null && parent.GetComponent<LayoutGroup>() == null) continue;
                Rect parentRect = parent.rect;
                if (parentRect.width <= 0f || parentRect.height <= 0f) continue;

                child.GetWorldCorners(corners);
                for (int i = 0; i < 4; i++)
                {
                    Vector3 point = parent.InverseTransformPoint(corners[i]);
                    local[i] = new Vector2(point.x, point.y);
                }
                Vector4 overflow = ComputeOverflow(local, parentRect);
                float worst = Mathf.Max(Mathf.Max(overflow.x, overflow.y), Mathf.Max(overflow.z, overflow.w));
                bool conventionRow = parent.name.StartsWith("group_", StringComparison.Ordinal) &&
                    child.GetComponent<Selectable>() != null;
                if (worst > ChildBoundsTolerance)
                    findings.Add(HarnessFinding.Create(
                            conventionRow ? HarnessSeverity.Info : HarnessSeverity.Warning, Category,
                            "child-out-of-bounds",
                            conventionRow ? "控件挂在标签容器外（原版表单常见写法）" : "子节点超出父节点矩形",
                            BuildPath(child, rootTransform, viewName))
                        .WithMetric("overLeft", overflow.x)
                        .WithMetric("overRight", overflow.y)
                        .WithMetric("overBottom", overflow.z)
                        .WithMetric("overTop", overflow.w)
                        .WithMetric("parentWidth", parentRect.width)
                        .WithMetric("parentHeight", parentRect.height));
            }
        }

        private static bool IsBoundsWhitelisted(RectTransform child, RectTransform parent)
        {
            string name = child.name ?? string.Empty;
            // 原版对话姓名标签下的 _line 装饰线按设计向两侧伸出，不是内容裁切。
            if (string.Equals(name, "_line", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string token in IntentionalOverflowNameTokens)
                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (child.GetComponent<Canvas>() != null) return true;
            ScrollRect scrollRect = parent.GetComponent<ScrollRect>();
            if (scrollRect != null && scrollRect.content == child) return true;
            // 被遮罩裁掉的部分玩家看不到，属于滚动设计。
            return parent.GetComponent<RectMask2D>() != null || parent.GetComponent<Mask>() != null;
        }

        private static void CheckSiblingOverlap(GameObject root, Transform rootTransform, string viewName,
            List<HarnessFinding> findings)
        {
            var corners = new Vector3[4];
            foreach (HorizontalOrVerticalLayoutGroup group in
                     root.GetComponentsInChildren<HorizontalOrVerticalLayoutGroup>(false))
            {
                if (group == null) continue;
                var groupRect = group.transform as RectTransform;
                if (groupRect == null) continue;
                var children = new List<RectTransform>();
                var boxes = new List<Rect>();
                for (int i = 0; i < groupRect.childCount; i++)
                {
                    var child = groupRect.GetChild(i) as RectTransform;
                    if (child == null || !child.gameObject.activeInHierarchy) continue;
                    LayoutElement element = child.GetComponent<LayoutElement>();
                    if (element != null && element.ignoreLayout) continue;
                    children.Add(child);
                    boxes.Add(LocalAabb(child, groupRect, corners));
                }
                for (int i = 0; i < children.Count; i++)
                    for (int j = i + 1; j < children.Count; j++)
                    {
                        float area = OverlapArea(boxes[i], boxes[j]);
                        if (area > SiblingOverlapMaxArea)
                            findings.Add(HarnessFinding.Create(HarnessSeverity.Warning, Category, "sibling-overlap",
                                    "布局组里的子项互相重叠", BuildPath(groupRect, rootTransform, viewName))
                                .WithMetric("childA", children[i].name)
                                .WithMetric("childB", children[j].name)
                                .WithMetric("overlapArea", area));
                    }
            }
        }

        private static void CheckTapTargets(GameObject root, Transform rootTransform, string viewName,
            List<HarnessFinding> findings)
        {
            var selectables = new List<Selectable>();
            selectables.AddRange(root.GetComponentsInChildren<Button>(false));
            selectables.AddRange(root.GetComponentsInChildren<Toggle>(false));
            foreach (Selectable selectable in selectables)
            {
                if (selectable == null) continue;
                var rectTransform = selectable.transform as RectTransform;
                if (rectTransform == null) continue;
                Rect rect = rectTransform.rect;
                float shortSide = Mathf.Min(rect.width, rect.height);
                if (shortSide <= 0f || shortSide >= MinTapTargetSize) continue;
                findings.Add(HarnessFinding.Create(HarnessSeverity.Info, Category, "tap-target-too-small",
                        "可点击区域的短边小于 " + MinTapTargetSize + "px",
                        BuildPath(selectable.transform, rootTransform, viewName))
                    .WithMetric("width", rect.width)
                    .WithMetric("height", rect.height));
            }
        }

        /// <summary>四个角点（父节点局部坐标）相对父矩形的越界量：x=左 y=右 z=下 w=上，都不小于 0。</summary>
        public static Vector4 ComputeOverflow(Vector2[] cornersInParent, Rect parentRect)
        {
            float overLeft = 0f, overRight = 0f, overBottom = 0f, overTop = 0f;
            if (cornersInParent != null)
                foreach (Vector2 p in cornersInParent)
                {
                    overLeft = Mathf.Max(overLeft, parentRect.xMin - p.x);
                    overRight = Mathf.Max(overRight, p.x - parentRect.xMax);
                    overBottom = Mathf.Max(overBottom, parentRect.yMin - p.y);
                    overTop = Mathf.Max(overTop, p.y - parentRect.yMax);
                }
            return new Vector4(overLeft, overRight, overBottom, overTop);
        }

        /// <summary>两个矩形的重叠面积，不相交时为 0。</summary>
        public static float OverlapArea(Rect a, Rect b)
        {
            float w = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
            float h = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
            return w > 0f && h > 0f ? w * h : 0f;
        }

        private static Rect LocalAabb(RectTransform child, RectTransform parent, Vector3[] cornerBuffer)
        {
            child.GetWorldCorners(cornerBuffer);
            float xMin = float.MaxValue, xMax = float.MinValue, yMin = float.MaxValue, yMax = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 local = parent.InverseTransformPoint(cornerBuffer[i]);
                xMin = Mathf.Min(xMin, local.x);
                xMax = Mathf.Max(xMax, local.x);
                yMin = Mathf.Min(yMin, local.y);
                yMax = Mathf.Max(yMax, local.y);
            }
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private static string BuildPath(Transform target, Transform root, string viewName)
        {
            var parts = new List<string>();
            for (Transform cursor = target; cursor != null; cursor = cursor.parent)
            {
                parts.Add(cursor.name);
                if (cursor == root) break;
            }
            parts.Reverse();
            string relative = string.Join("/", parts.ToArray());
            return string.IsNullOrEmpty(viewName) ? relative : viewName + "/" + relative;
        }
    }
}
