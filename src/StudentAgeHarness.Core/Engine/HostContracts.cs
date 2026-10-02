using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StudentAgeHarness.Engine
{
    /// <summary>日志出口，由插件接到 BepInEx 日志。</summary>
    internal interface IHarnessLogSink
    {
        void Info(string message);
        void Warning(string message);
        void Error(string message);
    }

    /// <summary>
    /// 游戏适配层：引擎通过它判断游戏状态、在场景之间恢复现场、识别弹窗。学生时代的实现在插件里。
    /// </summary>
    internal interface IHarnessHostAdapter
    {
        IHarnessGame Game { get; }

        IHarnessInputBinding InputBinding { get; }

        /// <summary>游戏已经可以开始跑场景（主菜单就绪）。轮询调用，异常按 false 处理。</summary>
        bool IsStartupReady();

        /// <summary>启动就绪后调用一次：记录基线、检查被测插件是否加载。</summary>
        void OnStartupReady(HarnessReport report);

        /// <summary>场景之间回到基线状态。轮询调用，可以执行操作，返回 true 表示已经回到主菜单。</summary>
        bool TryRecoverToBaseline();

        /// <summary>屏幕上有挡住流程的弹窗时返回描述，否则返回 null。</summary>
        string DescribeUnexpectedModal();

        /// <summary>游戏自身已知、无害的异常日志（不计入报告的 error）。</summary>
        bool IsKnownLogNoise(string condition);

        bool IsPluginLoaded(string guid);

        /// <summary>每帧调用（窗口停靠等）。</summary>
        void Tick();

        /// <summary>写报告前填充环境信息。</summary>
        void DescribeEnvironment(JObject environment);

        /// <summary>写报告前填充隔离信息等。</summary>
        void OnRunFinished(HarnessReport report);
    }

    /// <summary>
    /// 游戏自己缓存的输入设备。学生时代的 Control 类在启动时缓存了键盘和鼠标，虚拟设备要临时接进去，
    /// 游戏自己的快捷键和鼠标位置查询才能读到真实输入。
    /// </summary>
    internal interface IHarnessInputBinding
    {
        IDisposable BindKeyboard(Keyboard keyboard);
        IDisposable BindMouse(Mouse mouse);

        /// <summary>游戏自己读到的按键状态；不支持时返回 null。</summary>
        bool? GameSeesKey(Key key);

        /// <summary>游戏自己读到的鼠标位置；不支持时返回 null。</summary>
        Vector2? GameMousePosition { get; }
    }

    internal sealed class NullInputBinding : IHarnessInputBinding
    {
        internal static readonly NullInputBinding Instance = new NullInputBinding();

        public IDisposable BindKeyboard(Keyboard keyboard)
        {
            return EmptyDisposable.Instance;
        }

        public IDisposable BindMouse(Mouse mouse)
        {
            return EmptyDisposable.Instance;
        }

        public bool? GameSeesKey(Key key)
        {
            return null;
        }

        public Vector2? GameMousePosition => null;
    }

    internal sealed class EmptyDisposable : IDisposable
    {
        internal static readonly EmptyDisposable Instance = new EmptyDisposable();

        public void Dispose()
        {
        }
    }

    internal sealed class ActionDisposable : IDisposable
    {
        private Action action;

        internal ActionDisposable(Action action)
        {
            this.action = action;
        }

        public void Dispose()
        {
            Action pending = action;
            action = null;
            pending?.Invoke();
        }
    }
}
