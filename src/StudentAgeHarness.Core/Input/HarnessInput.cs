using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StudentAgeHarness.Engine;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace StudentAgeHarness
{
    /// <summary>
    /// 通过 Unity InputSystem 发送真实的鼠标和键盘输入：每次手势临时接入一个虚拟设备，
    /// 输入经过游戏正常的 Update 和 UI 输入模块，和玩家操作走同一条路。手势结束后设备和设置全部还原。
    /// 窗口没有焦点时，手势期间会暂时停用物理键鼠，你在别的窗口里的操作不会漏进游戏。
    /// 所有方法都返回 IEnumerator，放进步骤的 ActRoutine 里执行。
    /// </summary>
    public sealed class HarnessInput
    {
        private const string MouseLayout = "StudentAgeHarnessMouse";
        private const string KeyboardLayout = "StudentAgeHarnessKeyboard";
        private const int MinHoverFrames = 60;

        private readonly IHarnessInputBinding binding;
        private int mouseGestures;
        private int backgroundMouseGestures;
        private int keyGestures;

        internal HarnessInput(IHarnessInputBinding binding)
        {
            this.binding = binding ?? NullInputBinding.Instance;
        }

        /// <summary>鼠标移到目标中心，等悬停稳定后按下、抬起左键，并确认目标真的收到了点击。</summary>
        public IEnumerator Click(Component target, float hoverSettleSec = 0f)
        {
            RectTransform rect = RequireTarget(target);
            GameObject clickRoot = ExecuteEvents.GetEventHandler<IPointerClickHandler>(rect.gameObject);
            if (clickRoot == null)
                throw new InvalidOperationException("鼠标目标及其父节点都不接受点击：" + HarnessUi.PathOf(rect));
            return VerifiedClick(rect, clickRoot, hoverSettleSec);
        }

        /// <summary>右键点击目标中心（不验证送达，右键通常由目标自己处理）。</summary>
        public IEnumerator RightClick(Component target)
        {
            RectTransform rect = RequireTarget(target);
            return MouseSession(mouse => PressAt(mouse, rect, null, MouseButton.Right, Vector2.zero, 0f));
        }

        /// <summary>在目标中心按下左键，拖动 delta 像素（屏幕坐标）后松开。</summary>
        public IEnumerator Drag(Component target, Vector2 delta)
        {
            RectTransform rect = RequireTarget(target);
            return MouseSession(mouse => PressAt(mouse, rect, null, MouseButton.Left, delta, 0f));
        }

        /// <summary>鼠标移到目标上停留一段时间（用来触发悬停提示），不点击。</summary>
        public IEnumerator Hover(Component target, float seconds)
        {
            RectTransform rect = RequireTarget(target);
            return MouseSession(mouse => HoverRoutine(mouse, rect, Mathf.Max(0f, seconds)));
        }

        /// <summary>按屏幕比例坐标（左下角为 0,0）点击左键，不检查点到了什么。</summary>
        public IEnumerator ClickAtNormalized(float normalizedX, float normalizedY)
        {
            if (float.IsNaN(normalizedX) || float.IsNaN(normalizedY) ||
                normalizedX < 0f || normalizedX > 1f || normalizedY < 0f || normalizedY > 1f)
                throw new ArgumentOutOfRangeException(nameof(normalizedX), "比例坐标必须在 0 到 1 之间。");
            var point = new Vector2(normalizedX * Screen.width, normalizedY * Screen.height);
            return MouseSession(mouse => PressAt(mouse, null, point, MouseButton.Left, Vector2.zero, 0f));
        }

        /// <summary>鼠标停在目标上滚动滚轮。delta 为正向上滚，单位和鼠标滚轮一致（一格约 120）。</summary>
        public IEnumerator Scroll(Component target, float delta)
        {
            RectTransform rect = RequireTarget(target);
            return MouseSession(mouse => ScrollRoutine(mouse, rect, delta));
        }

        /// <summary>同时按下这些键再一起松开，例如 <c>Press(Key.Escape)</c>。</summary>
        public IEnumerator Press(params Key[] keys)
        {
            return KeyRoutine(keys, null);
        }

        /// <summary>组合键：先按住修饰键再按主键，按真实顺序松开，例如 <c>Chord(Key.S, Key.LeftCtrl)</c>。</summary>
        public IEnumerator Chord(Key key, params Key[] modifiers)
        {
            Key[] held = modifiers ?? new Key[0];
            var keys = new Key[held.Length + 1];
            Array.Copy(held, keys, held.Length);
            keys[held.Length] = key;
            return KeyRoutine(keys, held);
        }

        internal JObject Describe()
        {
            return new JObject
            {
                ["mouseGestures"] = mouseGestures,
                ["backgroundMouseGestures"] = backgroundMouseGestures,
                ["keyGestures"] = keyGestures
            };
        }

        // ======================== 鼠标 ========================

        private static RectTransform RequireTarget(Component target)
        {
            RectTransform rect = target == null ? null : target as RectTransform ?? target.transform as RectTransform;
            if (rect == null) throw new InvalidOperationException("鼠标目标为空或不是 UI 节点。");
            if (!rect.gameObject.activeInHierarchy)
                throw new InvalidOperationException("鼠标目标没有显示：" + HarnessUi.PathOf(rect));
            if (target is Selectable selectable && !selectable.IsInteractable())
                throw new InvalidOperationException("鼠标目标当前不可交互：" + HarnessUi.PathOf(rect));
            return rect;
        }

        private IEnumerator VerifiedClick(RectTransform rect, GameObject clickRoot, float hoverSettleSec)
        {
            // 观察器挂在本来就会处理点击的节点上，不改变事件的派发路线。
            ClickObserver observer = clickRoot.AddComponent<ClickObserver>();
            try
            {
                yield return MouseSession(mouse => PressAt(mouse, rect, null, MouseButton.Left, Vector2.zero, hoverSettleSec));
                if (!observer.Clicked)
                    throw new InvalidOperationException("真实鼠标点击没有送达目标：" + HarnessUi.PathOf(rect) +
                        "（负责点击的节点是 " + HarnessUi.PathOf(clickRoot.transform) + "）。");
            }
            finally
            {
                if (observer != null) UnityEngine.Object.Destroy(observer);
            }
        }

        private IEnumerator MouseSession(Func<Mouse, IEnumerator> body)
        {
            if (EventSystem.current == null)
                throw new InvalidOperationException("场景里没有 EventSystem，无法发送鼠标输入。");

            bool background = !Application.isFocused;
            InputSettings settings = InputSystem.settings;
            InputSettings.BackgroundBehavior previousBehavior = settings.backgroundBehavior;
            Dictionary<InputDevice, bool> originalStates =
                InputSystem.devices.ToDictionary(device => device, device => device.enabled);
            Mouse previousMouse = Mouse.current;
            Mouse mouse = null;
            IDisposable gameBinding = null;
            bool layoutRegistered = false;
            try
            {
                InputSystem.RegisterLayout("{\"name\":\"" + MouseLayout +
                    "\",\"extend\":\"Mouse\",\"runInBackground\":\"enabled\"}");
                layoutRegistered = true;
                // 窗口没有焦点时 UI 输入模块默认不处理指针：手势期间忽略焦点，同时停用物理设备。
                settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                foreach (InputDevice device in InputSystem.devices.ToArray())
                    if (device.enabled && (device is Mouse || (background && device.native)))
                        InputSystem.DisableDevice(device);

                mouse = (Mouse)InputSystem.AddDevice(MouseLayout);
                mouse.MakeCurrent();
                gameBinding = binding.BindMouse(mouse);
                InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(-100f, -100f) });
                yield return null;
                yield return null;

                mouseGestures++;
                if (background) backgroundMouseGestures++;
                yield return body(mouse);
            }
            finally
            {
                try { gameBinding?.Dispose(); }
                catch (Exception ex) { HarnessLog.Warning("还原游戏的鼠标绑定失败：" + ex.Message); }
                if (mouse != null && mouse.added) InputSystem.RemoveDevice(mouse);
                foreach (KeyValuePair<InputDevice, bool> pair in originalStates)
                {
                    if (!pair.Key.added || pair.Key.enabled == pair.Value) continue;
                    if (pair.Value) InputSystem.EnableDevice(pair.Key);
                    else InputSystem.DisableDevice(pair.Key);
                }
                settings.backgroundBehavior = previousBehavior;
                if (layoutRegistered) InputSystem.RemoveLayout(MouseLayout);
                if (previousMouse != null && previousMouse.added) previousMouse.MakeCurrent();
            }
        }

        private sealed class PointHolder
        {
            internal Vector2 Point;
        }

        /// <summary>把鼠标移到目标中心，等目标稳定地处在最上层（入场动画结束）。</summary>
        private IEnumerator MoveOver(Mouse mouse, RectTransform rect, float hoverSettleSec, PointHolder result)
        {
            GameObject top = null;
            Vector2 point = Vector2.zero;
            bool owned = false;
            float stableSince = Time.realtimeSinceStartup;
            float deadline = stableSince + Mathf.Max(2f, hoverSettleSec + 1f);
            for (int frame = 0; frame < MinHoverFrames || Time.realtimeSinceStartup < deadline; frame++)
            {
                if (!HarnessUi.TryGetScreenCenter(rect, out point))
                    throw new InvalidOperationException("鼠标目标不在屏幕内：" + HarnessUi.PathOf(rect));
                InputSystem.QueueStateEvent(mouse, new MouseState { position = point });
                yield return null;
                owned = HarnessUi.TryGetTopHit(point, out top, out _) && HarnessUi.IsSameOrChild(top.transform, rect);
                if (!owned)
                {
                    stableSince = Time.realtimeSinceStartup;
                    continue;
                }
                if (Time.realtimeSinceStartup - stableSince >= hoverSettleSec) break;
            }
            if (!owned)
                throw new InvalidOperationException("鼠标目标被挡住了：" + HarnessUi.PathOf(rect) + "；最上层是 " +
                    HarnessUi.PathOf(top == null ? null : top.transform));
            if (Time.realtimeSinceStartup - stableSince < hoverSettleSec)
                throw new InvalidOperationException("Pointer hover did not settle before the deadline.");
            Vector2? gameMouse = binding.GameMousePosition;
            if (gameMouse.HasValue && (gameMouse.Value - point).sqrMagnitude > 1f)
                throw new InvalidOperationException("游戏读到的鼠标位置 " + gameMouse.Value + " 和虚拟鼠标 " + point + " 不一致。");
            result.Point = point;
        }

        private IEnumerator PressAt(Mouse mouse, RectTransform rect, Vector2? fixedPoint, MouseButton button,
            Vector2 delta, float hoverSettleSec)
        {
            var holder = new PointHolder { Point = fixedPoint ?? Vector2.zero };
            if (rect != null)
            {
                yield return MoveOver(mouse, rect, hoverSettleSec, holder);
            }
            else
            {
                InputSystem.QueueStateEvent(mouse, new MouseState { position = holder.Point });
                yield return null;
                yield return null;
            }

            Vector2 point = holder.Point;
            InputSystem.QueueStateEvent(mouse, new MouseState { position = point }.WithButton(button));
            yield return null;
            yield return null;
            if (delta != Vector2.zero)
            {
                const int segments = 6;
                for (int index = 1; index <= segments; index++)
                {
                    InputSystem.QueueStateEvent(mouse,
                        new MouseState { position = point + delta * index / segments }.WithButton(button));
                    yield return null;
                }
                yield return null;
            }
            InputSystem.QueueStateEvent(mouse, new MouseState { position = point + delta });
            yield return null;
            yield return null;
        }

        private IEnumerator HoverRoutine(Mouse mouse, RectTransform rect, float seconds)
        {
            var holder = new PointHolder();
            yield return MoveOver(mouse, rect, 0f, holder);
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                InputSystem.QueueStateEvent(mouse, new MouseState { position = holder.Point });
                yield return null;
            }
        }

        private IEnumerator ScrollRoutine(Mouse mouse, RectTransform rect, float delta)
        {
            var holder = new PointHolder();
            yield return MoveOver(mouse, rect, 0f, holder);
            InputSystem.QueueStateEvent(mouse, new MouseState { position = holder.Point, scroll = new Vector2(0f, delta) });
            yield return null;
            InputSystem.QueueStateEvent(mouse, new MouseState { position = holder.Point });
            yield return null;
            yield return null;
        }

        // ======================== 键盘 ========================

        private IEnumerator KeyRoutine(Key[] keys, Key[] heldOnRelease)
        {
            if (keys == null || keys.Length == 0) throw new ArgumentException("至少要按一个键。", nameof(keys));
            Keyboard previous = Keyboard.current;
            Keyboard keyboard = null;
            IDisposable gameBinding = null;
            bool layoutRegistered = false;
            try
            {
                InputSystem.RegisterLayout("{\"name\":\"" + KeyboardLayout +
                    "\",\"extend\":\"Keyboard\",\"runInBackground\":\"enabled\"}");
                layoutRegistered = true;
                keyboard = (Keyboard)InputSystem.AddDevice(KeyboardLayout);
                keyboard.MakeCurrent();
                // 游戏的快捷键读取的是启动时缓存的键盘，手势期间把它接到虚拟键盘上。
                gameBinding = binding.BindKeyboard(keyboard);
                keyGestures++;

                if (heldOnRelease != null && heldOnRelease.Length > 0)
                {
                    InputSystem.QueueStateEvent(keyboard, new KeyboardState(heldOnRelease));
                    yield return null;
                }
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
                yield return null;
                foreach (Key key in keys)
                {
                    if (!keyboard[key].isPressed)
                        throw new InvalidOperationException("虚拟键盘的按键没有生效：" + key);
                    if (binding.GameSeesKey(key) == false)
                        throw new InvalidOperationException("游戏没有读到虚拟键盘的按键：" + key);
                }
                yield return null;
                // 游戏的快捷键在松开时触发：先松主键再松修饰键，和真实操作一致。
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(heldOnRelease ?? new Key[0]));
                yield return null;
                yield return null;
                if (heldOnRelease != null && heldOnRelease.Length > 0)
                {
                    InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                    yield return null;
                    yield return null;
                }
            }
            finally
            {
                try { gameBinding?.Dispose(); }
                catch (Exception ex) { HarnessLog.Warning("还原游戏的键盘绑定失败：" + ex.Message); }
                if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
                if (layoutRegistered) InputSystem.RemoveLayout(KeyboardLayout);
                if (previous != null && previous.added) previous.MakeCurrent();
            }
        }
    }

    internal sealed class ClickObserver : MonoBehaviour, IPointerClickHandler
    {
        internal bool Clicked;

        public void OnPointerClick(PointerEventData eventData)
        {
            Clicked = true;
        }
    }
}
