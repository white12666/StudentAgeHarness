using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudentAgeHarness.Plugin.Adapter
{
    /// <summary>
    /// 后台模式：把游戏窗口停在整个桌面的下边缘之外（不最小化，最小化后 Unity 不再渲染，截图会是黑的），
    /// 把焦点还给启动时的前台窗口，并限制帧率。静音在两种模式下都按配置生效。
    /// 游戏设置分辨率时会把窗口挪回屏幕中央，所以每秒检查一次。
    /// </summary>
    internal sealed class GameWindow
    {
        private const int SwpNoSize = 0x0001;
        private const int SwpNoActivate = 0x0010;
        private const int BackgroundFrameRate = 60;
        private static readonly IntPtr BottomOfZOrder = new IntPtr(1);

        private readonly bool background;
        private readonly bool mute;
        private readonly string focusFile;
        private IntPtr window;
        private bool focusHandled;
        private float nextCheck;
        private int parkCount;

        internal GameWindow(string runRoot, bool background, bool mute)
        {
            this.background = background;
            this.mute = mute;
            focusFile = Path.Combine(runRoot, "launch-focus.txt");
        }

        internal void Tick()
        {
            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 1f;
            if (mute) AudioListener.volume = 0f;
            if (!background) return;
            // 窗口在屏幕外时垂直同步不起作用，不限帧会让显卡空转。游戏启动时会应用自己的帧率设置，所以反复设置。
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = BackgroundFrameRate;
            if (!Find()) return;
            if (IsOnDesktop()) Park();
            if (!focusHandled) GiveFocusBack();
        }

        internal JObject Describe()
        {
            var result = new JObject
            {
                ["mode"] = background ? "background" : "visible",
                ["muted"] = mute,
                ["focused"] = Application.isFocused,
                ["parkCount"] = parkCount
            };
            if (Find() && GetWindowRect(window, out Rect rect))
                result["rect"] = new JObject
                {
                    ["x"] = rect.Left,
                    ["y"] = rect.Top,
                    ["width"] = rect.Right - rect.Left,
                    ["height"] = rect.Bottom - rect.Top
                };
            return result;
        }

        private bool Find()
        {
            if (window != IntPtr.Zero && IsWindow(window)) return true;
            window = IntPtr.Zero;
            uint pid = (uint)Process.GetCurrentProcess().Id;
            var name = new StringBuilder(64);
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out uint owner);
                if (owner != pid || !IsWindowVisible(handle)) return true;
                name.Length = 0;
                GetClassName(handle, name, name.Capacity);
                if (name.ToString() != "UnityWndClass") return true;
                window = handle;
                return false;
            }, IntPtr.Zero);
            return window != IntPtr.Zero;
        }

        private bool IsOnDesktop()
        {
            if (!GetWindowRect(window, out Rect rect)) return false;
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
            int right = left + GetSystemMetrics(78), bottom = top + GetSystemMetrics(79);
            return rect.Left < right && rect.Right > left && rect.Top < bottom && rect.Bottom > top;
        }

        /// <summary>保持水平位置不变（还算在同一个显示器上，不会因为 DPI 不同被系统缩放），只移到桌面下方。</summary>
        private void Park()
        {
            if (!GetWindowRect(window, out Rect rect)) return;
            int below = GetSystemMetrics(77) + GetSystemMetrics(79) + 40;
            if (SetWindowPos(window, BottomOfZOrder, rect.Left, below, 0, 0, SwpNoSize | SwpNoActivate)) parkCount++;
        }

        /// <summary>只有前台进程能把焦点交给别的窗口，所以由游戏自己交还；用户已经点到别处时不动。</summary>
        private void GiveFocusBack()
        {
            focusHandled = true;
            IntPtr restore = IntPtr.Zero;
            try
            {
                if (File.Exists(focusFile) && long.TryParse(File.ReadAllText(focusFile).Trim(), out long value))
                    restore = new IntPtr(value);
            }
            catch
            {
            }
            if (restore == IntPtr.Zero || !IsWindow(restore)) return;
            GetWindowThreadProcessId(GetForegroundWindow(), out uint owner);
            if (owner == (uint)Process.GetCurrentProcess().Id) SetForegroundWindow(restore);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int capacity);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out Rect rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, int flags);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    }
}
