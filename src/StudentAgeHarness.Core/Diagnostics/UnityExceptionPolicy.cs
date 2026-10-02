using System;

namespace StudentAgeHarness.Diagnostics
{
    /// <summary>
    /// Unity 异常日志的纯文本处理（不依赖 UnityEngine，可以直接单测）。
    /// 签名 = 异常类型 + 第一条非框架栈帧，用来在同一场景内合并重复的异常。
    /// </summary>
    internal static class UnityExceptionPolicy
    {
        internal const int MaxStackChars = 1600;
        internal const string SelfCheckMarker = "[StudentAgeHarness log monitor self-check; expected, not a fault]";

        private static readonly string[] FrameworkPrefixes =
        {
            "System.", "UnityEngine.", "Cysharp.", "DG.Tweening.", "Mono.", "(wrapper", "---"
        };

        internal static bool IsSelfCheck(string condition)
        {
            return condition != null && condition.IndexOf(SelfCheckMarker, StringComparison.Ordinal) >= 0;
        }

        internal static string ExceptionType(string condition)
        {
            string head = FirstLine(condition);
            int colon = head.IndexOf(':');
            return (colon > 0 ? head.Substring(0, colon) : head).Trim();
        }

        internal static string Signature(string condition, string stackTrace)
        {
            string frame = FirstRelevantFrame(stackTrace);
            string type = ExceptionType(condition);
            if (type.Length == 0) type = "(unknown exception)";
            return frame.Length == 0 ? type : type + " @ " + frame;
        }

        internal static string FirstRelevantFrame(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return string.Empty;
            string fallback = string.Empty;
            foreach (string raw in stackTrace.Split('\n'))
            {
                string frame = NormalizeFrame(raw);
                if (frame.Length == 0) continue;
                if (fallback.Length == 0 && !frame.StartsWith("---", StringComparison.Ordinal)) fallback = frame;
                if (!IsFrameworkFrame(frame)) return frame;
            }
            return fallback;
        }

        internal static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars) return value ?? string.Empty;
            return value.Substring(0, maxChars) + "…";
        }

        internal static string DescribeCondition(string condition)
        {
            string head = FirstLine(condition).Trim();
            return Truncate(head.Length == 0 ? "(空异常消息)" : head, 400);
        }

        // Unity 回调里的栈是 "Type.Method () (at <mvid>:0)"；Player.log 和 Exception.ToString 是
        // "  at Type.Method () [0x00133] in <mvid>:0"。两种都归一成 "Type.Method ()"。
        private static string NormalizeFrame(string raw)
        {
            string frame = (raw ?? string.Empty).Trim();
            if (frame.StartsWith("at ", StringComparison.Ordinal)) frame = frame.Substring(3).TrimStart();
            int cut = frame.IndexOf(" (at ", StringComparison.Ordinal);
            if (cut < 0) cut = frame.IndexOf(" [0x", StringComparison.Ordinal);
            if (cut >= 0) frame = frame.Substring(0, cut);
            return frame.TrimEnd();
        }

        private static bool IsFrameworkFrame(string frame)
        {
            foreach (string prefix in FrameworkPrefixes)
                if (frame.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        private static string FirstLine(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            int end = value.IndexOfAny(new[] { '\r', '\n' });
            return end < 0 ? value : value.Substring(0, end);
        }
    }
}
