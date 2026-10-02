using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StudentAgeHarness
{
    /// <summary>屏幕上一个可以点击的目标（UI 导出用）。</summary>
    public sealed class HarnessUiTarget
    {
        public string Text;
        public string Path;
        public string Kind;
        public Rect ScreenRect;
    }

    /// <summary>
    /// 只操作当前真正渲染在屏幕上、并且能被普通指针命中的界面。查找依据是可见文字和几何位置，
    /// 点击会先做射线检测：目标被别的界面挡住时直接报错，而不是"点穿"过去。
    /// 点击通过 EventSystem 发送 pointerDown/pointerUp/pointerClick，不需要窗口焦点，后台模式也能用；
    /// 要模拟真实鼠标设备请用 <see cref="HarnessInput"/>。
    /// </summary>
    public sealed class HarnessUi
    {
        public static readonly HarnessUi Instance = new HarnessUi();

        private static readonly Vector3[] WorldCorners = new Vector3[4];

        private HarnessUi()
        {
        }

        private sealed class LabelCandidate
        {
            internal Component Component;
            internal RectTransform Rect;
            internal string Text;
            internal int Depth;
        }

        private sealed class InputCandidate
        {
            internal RectTransform Rect;
            internal Transform Transform;
            internal Func<string> Read;
            internal Action Focus;
            internal Func<bool> IsFocused;
            internal Action<Event> ProcessEvent;
            internal Action ForceLabelUpdate;
            internal Action Blur;
            internal Func<int> CharacterLimit;
            internal string Kind;
            internal string Placeholder;
        }

        // ======================== 文字查询与点击 ========================

        /// <summary>屏幕上有一段完全等于 expected 的可见文字（空白会被归一）。</summary>
        public bool HasText(string expected)
        {
            string normalized = Normalize(expected);
            return FindLabels(text => string.Equals(text, normalized, StringComparison.Ordinal)).Count > 0;
        }

        /// <summary>屏幕上有一段同时包含所有片段的可见文字。</summary>
        public bool HasTextContaining(params string[] parts)
        {
            string[] normalized = NormalizeParts(parts);
            return FindLabels(text => ContainsAll(text, normalized)).Count > 0;
        }

        /// <summary>有且只有一个可点击目标显示这段文字，并且没有被遮挡。</summary>
        public bool IsClickable(string expected)
        {
            string normalized = Normalize(expected);
            return FindClickableLabel(text => string.Equals(text, normalized, StringComparison.Ordinal),
                out _, out _, out _);
        }

        public bool IsClickableContaining(params string[] parts)
        {
            string[] normalized = NormalizeParts(parts);
            return FindClickableLabel(text => ContainsAll(text, normalized), out _, out _, out _);
        }

        /// <summary>点击显示这段文字的唯一可点击目标；找不到、不唯一或被遮挡时抛异常。返回点到的文字。</summary>
        public string ClickText(string expected)
        {
            string normalized = Normalize(expected);
            return ClickMatching(text => string.Equals(text, normalized, StringComparison.Ordinal), expected, false);
        }

        public string ClickTextContaining(params string[] parts)
        {
            string[] normalized = NormalizeParts(parts);
            return ClickMatching(text => ContainsAll(text, normalized),
                string.Join(" + ", parts ?? new string[0]), false);
        }

        /// <summary>有多个匹配时点最上层的第一个（列表里任意一行都可以的情况）。</summary>
        public string ClickFirstTextContaining(params string[] parts)
        {
            string[] normalized = NormalizeParts(parts);
            return ClickMatching(text => ContainsAll(text, normalized),
                string.Join(" + ", parts ?? new string[0]), true);
        }

        // ======================== 按对象点击 ========================

        /// <summary>目标中心点的最上层射线命中是它自己或它的子节点（没有被挡住、在屏幕内）。</summary>
        public bool IsReachable(Component target)
        {
            RectTransform rect = RectOf(target);
            if (rect == null || !rect.gameObject.activeInHierarchy) return false;
            if (!TryGetScreenCenter(rect, out Vector2 point)) return false;
            return TryGetTopHit(point, out GameObject hit, out _) && IsSameOrChild(hit.transform, rect);
        }

        /// <summary>对目标发送一次指针点击；目标不可见、被遮挡或不接受点击时抛异常。</summary>
        public void Click(Component target)
        {
            RectTransform rect = RectOf(target);
            if (rect == null) throw new InvalidOperationException("点击目标为空或不是 UI 节点。");
            if (!rect.gameObject.activeInHierarchy)
                throw new InvalidOperationException("点击目标没有显示：" + PathOf(rect));
            if (target is Selectable selectable && !selectable.IsInteractable())
                throw new InvalidOperationException("点击目标当前不可交互：" + PathOf(rect));
            if (!TryGetScreenCenter(rect, out Vector2 point))
                throw new InvalidOperationException("点击目标不在屏幕内：" + PathOf(rect));
            if (!TryGetTopHit(point, out GameObject hit, out PointerEventData data))
                throw new InvalidOperationException("点击位置没有任何可命中的界面：" + PathOf(rect));
            if (!IsSameOrChild(hit.transform, rect))
                throw new InvalidOperationException("点击目标被挡住了：" + PathOf(rect) + "；最上层是 " +
                    PathOf(hit.transform));
            ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerUpHandler);
            if (ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerClickHandler) == null)
                throw new InvalidOperationException("点击目标不接受指针点击：" + PathOf(rect));
        }

        /// <summary>能点就点并返回 true；不可见、被挡住或不可交互时返回 false。</summary>
        public bool TryClick(Component target)
        {
            try
            {
                if (target is Selectable selectable && (!selectable.IsActive() || !selectable.IsInteractable()))
                    return false;
                if (!IsReachable(target)) return false;
                Click(target);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>等目标出现、可交互并且没被挡住（例如等动画结束），然后点一次。</summary>
        public IEnumerator ClickWhenReachable(Component target, float timeoutSec = 5f)
        {
            float deadline = Time.realtimeSinceStartup + Mathf.Max(0.1f, timeoutSec);
            while (true)
            {
                bool interactable = !(target is Selectable selectable) ||
                    (selectable.IsActive() && selectable.IsInteractable());
                if (target != null && interactable && IsReachable(target)) break;
                if (Time.realtimeSinceStartup > deadline)
                    throw new InvalidOperationException("等待目标可点击超时：" + PathOf(RectOf(target)) +
                        "（interactable=" + interactable + "）；可见文字：" + VisibleTextSummary(20));
                yield return null;
            }
            Click(target);
            yield return null;
        }

        /// <summary>按屏幕比例坐标（左下角为 0,0）点击，只在最上层命中接受点击时才发送。</summary>
        public void ClickAtNormalized(float normalizedX, float normalizedY)
        {
            if (!TryGetNormalizedScreenPoint(normalizedX, normalizedY, out Vector2 point) ||
                !TryGetTopHit(point, out GameObject hit, out PointerEventData data))
                throw new InvalidOperationException("比例坐标 " + normalizedX + "," + normalizedY + " 处没有可命中的界面。");
            if (ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit) == null)
                throw new InvalidOperationException("比例坐标 " + normalizedX + "," + normalizedY + " 处的界面不接受点击。");
            ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerClickHandler);
        }

        public bool IsClickableAtNormalized(float normalizedX, float normalizedY)
        {
            return TryGetNormalizedScreenPoint(normalizedX, normalizedY, out Vector2 point) &&
                TryGetTopHit(point, out GameObject hit, out _) &&
                ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit) != null;
        }

        // ======================== 输入框 ========================

        /// <summary>
        /// 在标签文字旁边的输入框里用键盘事件输入。只支持 ASCII 字母、数字、空格和 , ; - . _（不经过输入法）。
        /// </summary>
        public IEnumerator TypeIntoInputByLabel(string label, string value)
        {
            if (!TryFindInputByLabel(label, out InputCandidate input))
                throw new InvalidOperationException("标签 " + label + " 旁边没有唯一的可见输入框。可见文字：" +
                    VisibleTextSummary(20));
            return EditWithKeyboard(input, value, true);
        }

        public IEnumerator TypeIntoInputByPlaceholder(string value, params string[] placeholderParts)
        {
            return EditWithKeyboard(FindUniqueInputByPlaceholder(placeholderParts), value, true);
        }

        /// <summary>在指定输入框里输入。commit=false 时保持焦点，留给下一次点击去提交。</summary>
        public IEnumerator TypeIntoInput(Component input, string value, bool commit = true)
        {
            Transform target = input == null ? null : input.transform;
            InputCandidate selected = FindInputs().FirstOrDefault(candidate => candidate.Transform == target);
            if (selected == null) throw new InvalidOperationException("目标不是可见、可编辑的输入框。");
            return EditWithKeyboard(selected, value, commit);
        }

        public bool InputByLabelEquals(string label, string expected)
        {
            return TryFindInputByLabel(label, out InputCandidate input) &&
                string.Equals(input.Read() ?? string.Empty, expected ?? string.Empty, StringComparison.Ordinal);
        }

        // ======================== 导出 ========================

        /// <summary>屏幕上可见文字的摘要（去重，按渲染顺序），用于失败消息。</summary>
        public string VisibleTextSummary(int maximumItems = 40)
        {
            IList<string> values = VisibleTexts(maximumItems);
            return values.Count == 0 ? "(没有可见文字)" : string.Join(" | ", values.ToArray());
        }

        public IList<string> VisibleTexts(int maximumItems = 200)
        {
            var values = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (LabelCandidate label in FindLabels(_ => true))
                {
                    if (label.Text.Length == 0 || !seen.Add(label.Text)) continue;
                    values.Add(label.Text);
                    if (values.Count >= maximumItems) break;
                }
            }
            catch
            {
            }
            return values;
        }

        /// <summary>屏幕上所有能被指针命中的可点击目标（按钮、开关、下拉框等）。</summary>
        public IList<HarnessUiTarget> ClickableTargets(int maximumItems = 200)
        {
            var result = new List<HarnessUiTarget>();
            var seen = new HashSet<Transform>();
            foreach (Selectable selectable in UnityEngine.Object.FindObjectsOfType<Selectable>())
            {
                if (result.Count >= maximumItems) break;
                if (selectable == null || !selectable.IsActive() || !selectable.IsInteractable()) continue;
                var rect = selectable.transform as RectTransform;
                if (rect == null || !seen.Add(rect) || !IsRendered(rect) || !IsReachable(selectable)) continue;
                TryGetScreenRect(rect, out Rect screenRect);
                result.Add(new HarnessUiTarget
                {
                    Text = ReadText(selectable.gameObject),
                    Path = PathOf(rect),
                    Kind = selectable.GetType().Name,
                    ScreenRect = screenRect
                });
            }
            return result;
        }

        internal IList<HarnessUiTarget> InputTargets()
        {
            return FindInputs().Select(input =>
            {
                TryGetScreenRect(input.Rect, out Rect screenRect);
                return new HarnessUiTarget
                {
                    Text = input.Read() ?? string.Empty,
                    Path = PathOf(input.Rect),
                    Kind = input.Kind + (string.IsNullOrEmpty(input.Placeholder) ? string.Empty : "（占位：" + input.Placeholder + "）"),
                    ScreenRect = screenRect
                };
            }).ToList();
        }

        /// <summary>节点的层级路径（从场景根开始）。</summary>
        public static string PathOf(Transform target)
        {
            if (target == null) return "(无)";
            string path = target.name;
            for (Transform parent = target.parent; parent != null; parent = parent.parent)
                path = parent.name + "/" + path;
            return path;
        }

        // ======================== 内部实现 ========================

        private static RectTransform RectOf(Component target)
        {
            if (target == null) return null;
            return target as RectTransform ?? target.transform as RectTransform;
        }

        private string ClickMatching(Func<string, bool> predicate, string description, bool firstMatch)
        {
            if (!FindClickableLabel(predicate, out GameObject hit, out PointerEventData data,
                    out LabelCandidate label, firstMatch))
                throw new InvalidOperationException("找不到" + (firstMatch ? "" : "唯一的") + "可点击文字：" +
                    description + "。可见文字：" + VisibleTextSummary(30));
            ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerUpHandler);
            if (ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerClickHandler) == null)
                throw new InvalidOperationException("文字所在的目标不接受指针点击：" + label.Text);
            return label.Text;
        }

        private static bool FindClickableLabel(Func<string, bool> predicate, out GameObject hit,
            out PointerEventData data, out LabelCandidate chosen, bool firstMatch = false)
        {
            hit = null;
            data = null;
            chosen = null;
            List<LabelCandidate> matches = FindLabels(predicate);
            matches.Sort((left, right) => right.Depth.CompareTo(left.Depth));
            foreach (LabelCandidate label in matches)
            {
                Transform actionRoot = FindPointerActionRoot(label.Component.transform);
                if (actionRoot == null) continue;
                if (!TryGetScreenCenter(label.Rect, out Vector2 point)) continue;
                if (!TryGetTopHit(point, out GameObject candidateHit, out PointerEventData candidateData)) continue;
                if (!IsSameOrChild(candidateHit.transform, actionRoot)) continue;
                // 有多个可点目标显示同样的文字时视为不明确，避免点错对象。
                if (chosen != null) return false;
                chosen = label;
                hit = candidateHit;
                data = candidateData;
                if (firstMatch) return true;
            }
            return chosen != null;
        }

        private static Transform FindPointerActionRoot(Transform start)
        {
            for (Transform cursor = start; cursor != null; cursor = cursor.parent)
            {
                Selectable selectable = cursor.GetComponent<Selectable>();
                if (selectable != null && (!selectable.IsActive() || !selectable.IsInteractable())) return null;
                if (selectable != null &&
                    (selectable is Button || selectable is Toggle || selectable is Dropdown ||
                     selectable is TMP_Dropdown))
                    return cursor;
                foreach (Component component in cursor.GetComponents<Component>())
                    if (component is IPointerClickHandler && component is Behaviour behaviour &&
                        behaviour.isActiveAndEnabled)
                        return cursor;
            }
            return null;
        }

        private static List<LabelCandidate> FindLabels(Func<string, bool> predicate)
        {
            var result = new List<LabelCandidate>();
            foreach (Text label in UnityEngine.Object.FindObjectsOfType<Text>())
            {
                if (label == null || !label.enabled || string.IsNullOrWhiteSpace(label.text)) continue;
                AddLabel(label, label.rectTransform, label.text, predicate, result);
            }
            foreach (TMP_Text label in UnityEngine.Object.FindObjectsOfType<TMP_Text>())
            {
                if (label == null || !label.enabled || string.IsNullOrWhiteSpace(label.text)) continue;
                AddLabel(label, label.rectTransform, label.text, predicate, result);
            }
            return result;
        }

        private static void AddLabel(Component component, RectTransform rect, string raw,
            Func<string, bool> predicate, List<LabelCandidate> result)
        {
            string normalized = Normalize(StripRichText(raw));
            if (normalized.Length == 0 || !predicate(normalized) || !IsRendered(rect)) return;
            CanvasRenderer renderer = component.GetComponent<CanvasRenderer>();
            result.Add(new LabelCandidate
            {
                Component = component,
                Rect = rect,
                Text = normalized,
                Depth = renderer == null ? 0 : renderer.absoluteDepth
            });
        }

        private static bool TryFindInputByLabel(string expected, out InputCandidate selected)
        {
            selected = null;
            string normalized = Normalize(expected);
            List<LabelCandidate> labels = FindLabels(text => string.Equals(text, normalized, StringComparison.Ordinal));
            List<InputCandidate> inputs = FindInputs();
            float bestScore = float.PositiveInfinity;
            foreach (LabelCandidate label in labels)
            {
                if (!TryGetScreenRect(label.Rect, out Rect labelRect)) continue;
                foreach (InputCandidate input in inputs)
                {
                    if (!TryGetScreenRect(input.Rect, out Rect inputRect)) continue;
                    float vertical = Mathf.Abs(inputRect.center.y - labelRect.center.y);
                    float verticalLimit = Mathf.Max(48f, Mathf.Max(inputRect.height, labelRect.height) * 2.5f);
                    if (vertical > verticalLimit) continue;
                    float horizontal = inputRect.xMin - labelRect.xMax;
                    if (inputRect.center.x < labelRect.center.x) horizontal += Screen.width;
                    float score = vertical * 12f + Mathf.Abs(horizontal);
                    if (score >= bestScore) continue;
                    bestScore = score;
                    selected = input;
                }
            }
            return selected != null;
        }

        private static InputCandidate FindUniqueInputByPlaceholder(string[] placeholderParts)
        {
            string[] normalized = NormalizeParts(placeholderParts);
            InputCandidate match = null;
            foreach (InputCandidate input in FindInputs())
            {
                if (!ContainsAll(Normalize(input.Placeholder), normalized)) continue;
                if (match != null)
                    throw new InvalidOperationException("有多个输入框的占位文字匹配：" +
                        string.Join(" + ", placeholderParts ?? new string[0]));
                match = input;
            }
            if (match == null)
                throw new InvalidOperationException("没有占位文字匹配的可见输入框：" +
                    string.Join(" + ", placeholderParts ?? new string[0]));
            return match;
        }

        private static List<InputCandidate> FindInputs()
        {
            var result = new List<InputCandidate>();
            foreach (InputField field in UnityEngine.Object.FindObjectsOfType<InputField>())
            {
                if (field == null || !field.IsActive() || !field.interactable || field.readOnly ||
                    !IsRendered(field.transform as RectTransform)) continue;
                InputField captured = field;
                result.Add(new InputCandidate
                {
                    Rect = captured.transform as RectTransform,
                    Transform = captured.transform,
                    Read = () => captured.text,
                    Focus = () =>
                    {
                        captured.Select();
                        captured.ActivateInputField();
                    },
                    IsFocused = () => captured.isFocused,
                    ProcessEvent = captured.ProcessEvent,
                    ForceLabelUpdate = captured.ForceLabelUpdate,
                    Blur = captured.DeactivateInputField,
                    CharacterLimit = () => captured.characterLimit,
                    Kind = "InputField",
                    Placeholder = captured.placeholder is Text placeholder ? placeholder.text : string.Empty
                });
            }
            foreach (TMP_InputField field in UnityEngine.Object.FindObjectsOfType<TMP_InputField>())
            {
                if (field == null || !field.IsActive() || !field.interactable || field.readOnly ||
                    !IsRendered(field.transform as RectTransform)) continue;
                TMP_InputField captured = field;
                result.Add(new InputCandidate
                {
                    Rect = captured.transform as RectTransform,
                    Transform = captured.transform,
                    Read = () => captured.text,
                    Focus = () =>
                    {
                        captured.Select();
                        captured.ActivateInputField();
                    },
                    IsFocused = () => captured.isFocused,
                    ProcessEvent = captured.ProcessEvent,
                    ForceLabelUpdate = captured.ForceLabelUpdate,
                    Blur = () => captured.DeactivateInputField(false),
                    CharacterLimit = () => captured.characterLimit,
                    Kind = "TMP_InputField",
                    Placeholder = captured.placeholder is TMP_Text placeholder ? placeholder.text : string.Empty
                });
            }
            return result;
        }

        private IEnumerator EditWithKeyboard(InputCandidate input, string value, bool commit)
        {
            if (input == null || !TryGetScreenCenter(input.Rect, out Vector2 point))
                throw new InvalidOperationException("输入框不在屏幕内。");
            if (!TryGetTopHit(point, out GameObject hit, out PointerEventData data) ||
                !IsSameOrChild(hit.transform, input.Transform))
                throw new InvalidOperationException("输入框被挡住，指针点不到：" + PathOf(input.Transform));

            ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerClickHandler);

            input.Focus();
            float focusDeadline = Time.realtimeSinceStartup + 1f;
            while (!input.IsFocused() && Time.realtimeSinceStartup < focusDeadline) yield return null;
            if (!input.IsFocused())
                throw InputMismatch(input, "获取焦点", '\0', input.Read(), input.Read(), "输入框没有拿到键盘焦点");

            input.ProcessEvent(KeyEvent(EventType.KeyDown, KeyCode.A, '\0', EventModifiers.Control));
            yield return null;
            input.ProcessEvent(KeyEvent(EventType.KeyUp, KeyCode.A, '\0', EventModifiers.Control));
            yield return null;

            string replacement = value ?? string.Empty;
            int characterLimit = input.CharacterLimit == null ? 0 : input.CharacterLimit();
            if (characterLimit > 0 && replacement.Length > characterLimit)
                throw InputMismatch(input, "长度检查", '\0', replacement, input.Read(), "内容超过 characterLimit");

            if (replacement.Length == 0)
            {
                input.ProcessEvent(KeyEvent(EventType.KeyDown, KeyCode.Backspace, '\0', EventModifiers.None));
                yield return null;
                input.ProcessEvent(KeyEvent(EventType.KeyUp, KeyCode.Backspace, '\0', EventModifiers.None));
                yield return null;
                if (!string.IsNullOrEmpty(input.Read()))
                    throw InputMismatch(input, "清空", '\0', "", input.Read(), "Ctrl+A 加退格没有清空输入框");
            }

            var expected = new StringBuilder(replacement.Length);
            foreach (char character in replacement)
            {
                if (!input.IsFocused())
                    throw InputMismatch(input, "按键前", character, expected.ToString(), input.Read(), "输入框失去了焦点");
                if (!TryMapKey(character, out KeyCode keyCode, out EventModifiers modifiers))
                    throw InputMismatch(input, "按键映射", character, expected.ToString(), input.Read(),
                        "不支持的字符（只支持 ASCII 字母、数字、空格和 , ; - . _）");
                input.ProcessEvent(KeyEvent(EventType.KeyDown, keyCode, character, modifiers));
                yield return null;
                expected.Append(character);
                string actual = input.Read() ?? string.Empty;
                if (!string.Equals(actual, expected.ToString(), StringComparison.Ordinal))
                    throw InputMismatch(input, "按下后", character, expected.ToString(), actual, "输入框内容和预期前缀不一致");
                input.ForceLabelUpdate();
                input.ProcessEvent(KeyEvent(EventType.KeyUp, keyCode, '\0', modifiers));
                yield return null;
            }

            if (!commit) yield break;
            EventSystem system = EventSystem.current;
            if (system == null) throw new InvalidOperationException("输入过程中 EventSystem 消失了。");
            system.SetSelectedGameObject(null);
            yield return null;
            if (input.IsFocused())
            {
                input.Blur();
                yield return null;
            }
            if (input.IsFocused())
                throw InputMismatch(input, "提交", '\0', replacement, input.Read(), "输入框没有释放焦点");
            string committed = input.Read() ?? string.Empty;
            if (!string.Equals(committed, replacement, StringComparison.Ordinal))
                throw InputMismatch(input, "提交后", '\0', replacement, committed, "提交时内容被改变");
            input.ForceLabelUpdate();
        }

        private static bool TryMapKey(char character, out KeyCode keyCode, out EventModifiers modifiers)
        {
            keyCode = KeyCode.None;
            modifiers = EventModifiers.None;
            if (character >= 'A' && character <= 'Z')
            {
                keyCode = (KeyCode)((int)KeyCode.A + character - 'A');
                modifiers = EventModifiers.Shift;
                return true;
            }
            if (character >= 'a' && character <= 'z')
            {
                keyCode = (KeyCode)((int)KeyCode.A + character - 'a');
                return true;
            }
            if (character >= '0' && character <= '9')
            {
                keyCode = (KeyCode)((int)KeyCode.Alpha0 + character - '0');
                return true;
            }
            switch (character)
            {
                case ',': keyCode = KeyCode.Comma; return true;
                case ';': keyCode = KeyCode.Semicolon; return true;
                case '-': keyCode = KeyCode.Minus; return true;
                case '.': keyCode = KeyCode.Period; return true;
                case ' ': keyCode = KeyCode.Space; return true;
                case '_':
                    keyCode = KeyCode.Minus;
                    modifiers = EventModifiers.Shift;
                    return true;
            }
            return false;
        }

        private static Event KeyEvent(EventType eventType, KeyCode keyCode, char character, EventModifiers modifiers)
        {
            return new Event { type = eventType, keyCode = keyCode, character = character, modifiers = modifiers };
        }

        private static InvalidOperationException InputMismatch(InputCandidate input, string phase, char character,
            string expected, string actual, string reason)
        {
            return new InvalidOperationException("输入框键盘输入不一致：阶段=" + phase + "，原因=" + reason +
                "，字符=" + Escape(character.ToString()) + "，预期=" + Escape(expected) + "，实际=" + Escape(actual) +
                "，类型=" + (input == null ? "null" : input.Kind));
        }

        private static string Escape(string value)
        {
            if (value == null) return "(null)";
            if (value.Length == 0 || value == "\0") return "(空)";
            return value;
        }

        internal static bool TryGetTopHit(Vector2 point, out GameObject hit, out PointerEventData data)
        {
            hit = null;
            data = null;
            EventSystem system = EventSystem.current;
            if (system == null) return false;
            data = new PointerEventData(system)
            {
                position = point,
                pressPosition = point,
                button = PointerEventData.InputButton.Left,
                clickCount = 1,
                eligibleForClick = true
            };
            var results = new List<RaycastResult>();
            system.RaycastAll(data, results);
            foreach (RaycastResult result in results)
            {
                if (result.gameObject == null || !result.gameObject.activeInHierarchy) continue;
                data.pointerCurrentRaycast = result;
                data.pointerPressRaycast = result;
                data.pointerPress = result.gameObject;
                data.rawPointerPress = result.gameObject;
                hit = result.gameObject;
                return true;
            }
            return false;
        }

        private static bool TryGetNormalizedScreenPoint(float x, float y, out Vector2 point)
        {
            point = Vector2.zero;
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y) ||
                x < 0f || x > 1f || y < 0f || y > 1f || Screen.width <= 0 || Screen.height <= 0) return false;
            point = new Vector2(x * Screen.width, y * Screen.height);
            return true;
        }

        private static bool IsRendered(RectTransform rect)
        {
            if (rect == null || !rect.gameObject.activeInHierarchy ||
                !TryGetScreenRect(rect, out Rect screenRect) || screenRect.width < 2f || screenRect.height < 2f)
                return false;
            if (screenRect.xMax <= 0f || screenRect.yMax <= 0f || screenRect.xMin >= Screen.width ||
                screenRect.yMin >= Screen.height) return false;

            float alpha = 1f;
            for (Transform cursor = rect; cursor != null; cursor = cursor.parent)
            {
                CanvasGroup group = cursor.GetComponent<CanvasGroup>();
                if (group == null) continue;
                alpha *= group.alpha;
                if (alpha <= 0.01f) return false;
                if (group.ignoreParentGroups) break;
            }

            if (!TryGetScreenCenter(rect, out Vector2 point)) return false;
            Camera camera = ResolveCamera(rect);
            for (Transform cursor = rect; cursor != null; cursor = cursor.parent)
            {
                foreach (Component component in cursor.GetComponents<Component>())
                {
                    if (!(component is ICanvasRaycastFilter filter)) continue;
                    try
                    {
                        if (!filter.IsRaycastLocationValid(point, camera)) return false;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        internal static bool TryGetScreenCenter(RectTransform rect, out Vector2 point)
        {
            point = Vector2.zero;
            if (!TryGetScreenRect(rect, out Rect screenRect)) return false;
            point = screenRect.center;
            return point.x >= 0f && point.y >= 0f && point.x <= Screen.width && point.y <= Screen.height;
        }

        internal static bool TryGetScreenRect(RectTransform rect, out Rect screenRect)
        {
            screenRect = default;
            if (rect == null) return false;
            rect.GetWorldCorners(WorldCorners);
            Camera camera = ResolveCamera(rect);
            Vector2 first = RectTransformUtility.WorldToScreenPoint(camera, WorldCorners[0]);
            float minX = first.x, maxX = first.x, minY = first.y, maxY = first.y;
            for (int index = 1; index < WorldCorners.Length; index++)
            {
                Vector2 value = RectTransformUtility.WorldToScreenPoint(camera, WorldCorners[index]);
                minX = Mathf.Min(minX, value.x);
                maxX = Mathf.Max(maxX, value.x);
                minY = Mathf.Min(minY, value.y);
                maxY = Mathf.Max(maxY, value.y);
            }
            screenRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return IsFinite(minX) && IsFinite(maxX) && IsFinite(minY) && IsFinite(maxY);
        }

        internal static Camera ResolveCamera(RectTransform rect)
        {
            Canvas canvas = rect == null ? null : rect.GetComponentInParent<Canvas>();
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
            return canvas.worldCamera;
        }

        internal static bool IsSameOrChild(Transform candidate, Transform root)
        {
            if (candidate == null || root == null) return false;
            for (Transform cursor = candidate; cursor != null; cursor = cursor.parent)
                if (ReferenceEquals(cursor, root)) return true;
            return false;
        }

        private static string ReadText(GameObject root)
        {
            if (root == null) return string.Empty;
            var builder = new StringBuilder();
            foreach (Text text in root.GetComponentsInChildren<Text>(false))
                Append(builder, text == null ? null : text.text);
            foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(false))
                Append(builder, text == null ? null : text.text);
            return builder.ToString();
        }

        private static void Append(StringBuilder builder, string value)
        {
            string normalized = Normalize(StripRichText(value));
            if (normalized.Length == 0) return;
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(normalized);
        }

        private static bool ContainsAll(string value, string[] parts)
        {
            if (string.IsNullOrEmpty(value) || parts == null || parts.Length == 0) return false;
            foreach (string part in parts)
                if (part.Length > 0 && value.IndexOf(part, StringComparison.Ordinal) < 0) return false;
            return true;
        }

        private static string[] NormalizeParts(string[] values)
        {
            if (values == null) return new string[0];
            return values.Select(Normalize).ToArray();
        }

        /// <summary>去掉 &lt;color=...&gt; 这类富文本标签，按玩家看到的文字匹配。</summary>
        internal static string StripRichText(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('<') < 0) return value ?? string.Empty;
            var builder = new StringBuilder(value.Length);
            int index = 0;
            while (index < value.Length)
            {
                char c = value[index];
                if (c == '<')
                {
                    int close = value.IndexOf('>', index + 1);
                    if (close > index && close - index <= 64 && LooksLikeTag(value, index + 1, close))
                    {
                        index = close + 1;
                        continue;
                    }
                }
                builder.Append(c);
                index++;
            }
            return builder.ToString();
        }

        private static bool LooksLikeTag(string value, int start, int end)
        {
            if (start >= end) return false;
            char first = value[start];
            return first == '/' || char.IsLetter(first) || first == '#';
        }

        internal static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var builder = new StringBuilder(value.Length);
            bool pendingSpace = false;
            foreach (char character in value.Trim())
            {
                if (char.IsWhiteSpace(character))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }
                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }
                builder.Append(character);
            }
            return builder.ToString();
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
