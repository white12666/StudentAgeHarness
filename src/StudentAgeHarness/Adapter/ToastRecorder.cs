using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace StudentAgeHarness.Plugin.Adapter
{
    /// <summary>
    /// 记录游戏要弹的每一条 Toast。ToastHelper 的三个重载最后都调用 ToastCtrl.Toast(ToastData)，
    /// 在那里记录就能覆盖全部（包括泛型重载）。记录的是"游戏要提示什么"，即使 ToastCtrl 按状态决定不显示也会记下。
    /// </summary>
    internal sealed class ToastRecorder
    {
        private const int MaxMessages = 500;
        private static ToastRecorder instance;

        private readonly object gate = new object();
        private readonly List<string> messages = new List<string>();
        private int total;

        internal bool Installed { get; private set; }

        internal string InstallError { get; private set; }

        internal void Install(Harmony harmony)
        {
            instance = this;
            try
            {
                MethodInfo method = AccessTools.Method(typeof(ToastCtrl), nameof(ToastCtrl.Toast), new[] { typeof(ToastData) });
                if (method == null) throw new MissingMethodException("ToastCtrl", "Toast(ToastData)");
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ToastRecorder), nameof(BeforeToast)));
                Installed = true;
            }
            catch (Exception ex)
            {
                InstallError = ex.Message;
            }
        }

        private static void BeforeToast(ToastData _data)
        {
            ToastRecorder recorder = instance;
            string text = _data?.desc;
            if (recorder == null || string.IsNullOrEmpty(text)) return;
            lock (recorder.gate)
            {
                recorder.total++;
                if (recorder.messages.Count < MaxMessages) recorder.messages.Add(HarnessUi.StripRichText(text));
            }
        }

        internal IList<string> Messages
        {
            get { lock (gate) return messages.ToArray(); }
        }

        internal int Total
        {
            get { lock (gate) return total; }
        }

        internal void Clear()
        {
            lock (gate) messages.Clear();
        }

        internal bool AnyContains(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            lock (gate)
            {
                foreach (string message in messages)
                    if (message.IndexOf(text, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }
    }
}
