using System;
using System.Reflection;
using HarmonyLib;
using StudentAgeHarness.Engine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StudentAgeHarness.Plugin.Adapter
{
    /// <summary>
    /// 游戏的 Control 在启用时缓存了 Keyboard.current 和 Mouse.current，快捷键和鼠标位置都从缓存读。
    /// 手势期间把缓存换成虚拟设备，结束后换回来，游戏自己的输入代码才能读到测试输入。
    /// </summary>
    internal sealed class ControlInputBinding : IHarnessInputBinding
    {
        private static readonly FieldInfo InstanceField = AccessTools.Field(typeof(Control), "ins");
        private static readonly FieldInfo KeyboardField = AccessTools.Field(typeof(Control), "keyboard");
        private static readonly FieldInfo MouseField = AccessTools.Field(typeof(Control), "mouse");

        public IDisposable BindKeyboard(Keyboard keyboard)
        {
            return Bind(KeyboardField, keyboard);
        }

        public IDisposable BindMouse(Mouse mouse)
        {
            return Bind(MouseField, mouse);
        }

        public bool? GameSeesKey(Key key)
        {
            if (Current() == null) return null;
            try { return Control.GetKey(key); }
            catch { return null; }
        }

        public Vector2? GameMousePosition
        {
            get
            {
                if (Current() == null) return null;
                try { return Control.mousePosition; }
                catch { return null; }
            }
        }

        private static Control Current()
        {
            try { return InstanceField?.GetValue(null) as Control; }
            catch { return null; }
        }

        private static IDisposable Bind(FieldInfo field, object device)
        {
            Control control = Current();
            if (control == null || field == null) return EmptyDisposable.Instance;
            object previous = field.GetValue(control);
            field.SetValue(control, device);
            return new ActionDisposable(() =>
            {
                // 游戏在禁用/启用 Control 时会重新缓存设备；只有缓存仍是我们的虚拟设备时才还原。
                if (ReferenceEquals(field.GetValue(control), device)) field.SetValue(control, previous);
            });
        }
    }
}
