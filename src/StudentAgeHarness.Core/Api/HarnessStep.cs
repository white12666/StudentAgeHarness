using System;
using System.Collections;

namespace StudentAgeHarness
{
    /// <summary>
    /// 场景里的一步。执行顺序：SkipWhen → Pre（轮询等待）→ Act → ActRoutine（可以跨帧）→ Post（轮询等待）→ 截图。
    /// Pre/Post 里抛出的异常按"条件还不满足"处理，超时后报告会带上最后一次异常；
    /// Act/ActRoutine 抛出的异常直接让这一步失败。
    /// </summary>
    public sealed class HarnessStep
    {
        /// <summary>步骤名，同一场景内唯一，会出现在报告和截图文件名里。</summary>
        public string Name;

        public Func<bool> Pre;
        public float PreTimeoutSec = 15f;
        public string PreTimeoutMessage;

        public Action Act;

        /// <summary>需要跨帧完成的操作（真实输入、等待动画等）。可以 yield 嵌套的 IEnumerator。</summary>
        public Func<IEnumerator> ActRoutine;

        /// <summary>ActRoutine 的耗时上限；小于等于 0 表示不限制（仍受场景和整次运行的上限约束）。</summary>
        public float ActTimeoutSec = 60f;

        public Func<bool> Post;
        public float PostTimeoutSec = 15f;
        public string PostTimeoutMessage;

        /// <summary>步骤成功结束后截一张图。</summary>
        public bool CaptureAfter;
        public float SettleBeforeCaptureSec;

        /// <summary>true：失败 finding 记 warning，后面的步骤照常执行；失败步骤仍影响场景结果。</summary>
        public bool ContinueOnFailure;

        /// <summary>true：前面的步骤失败后仍然执行（关闭界面、清理数据类收尾步骤）。</summary>
        public bool AlwaysRun;

        /// <summary>true：这一步本来就会出现确认框之类的弹窗，不把它当成意外弹窗。</summary>
        public bool ExpectModal;

        /// <summary>执行前求值，为 true 时整步跳过。</summary>
        public Func<bool> SkipWhen;
        public string SkipReason;

        /// <summary>true：SkipWhen 命中属于正常分支，记为 skipped-allowed，不算覆盖缺失。</summary>
        public bool AllowSkip;

        internal bool IsRecovery;

        public static HarnessStep Do(string name, Action act)
        {
            return new HarnessStep { Name = name, Act = act };
        }

        public static HarnessStep Routine(string name, Func<IEnumerator> routine, float timeoutSec = 60f)
        {
            return new HarnessStep { Name = name, ActRoutine = routine, ActTimeoutSec = timeoutSec };
        }

        public static HarnessStep WaitUntil(string name, Func<bool> condition, float timeoutSec = 15f,
            string timeoutMessage = null)
        {
            return new HarnessStep
            {
                Name = name,
                Pre = condition,
                PreTimeoutSec = timeoutSec,
                PreTimeoutMessage = timeoutMessage
            };
        }

        public static HarnessStep Screenshot(string name, float settleSec = 0.3f)
        {
            return new HarnessStep { Name = name, CaptureAfter = true, SettleBeforeCaptureSec = settleSec };
        }

        /// <summary>执行前先等这个条件成立。</summary>
        public HarnessStep After(Func<bool> condition, float timeoutSec = 15f, string timeoutMessage = null)
        {
            Pre = condition;
            PreTimeoutSec = timeoutSec;
            PreTimeoutMessage = timeoutMessage;
            return this;
        }

        /// <summary>执行后等这个条件成立，超时算失败。</summary>
        public HarnessStep Then(Func<bool> condition, float timeoutSec = 15f, string timeoutMessage = null)
        {
            Post = condition;
            PostTimeoutSec = timeoutSec;
            PostTimeoutMessage = timeoutMessage;
            return this;
        }

        public HarnessStep WithCapture(float settleSec = 0f)
        {
            CaptureAfter = true;
            SettleBeforeCaptureSec = settleSec;
            return this;
        }

        public HarnessStep Soft()
        {
            ContinueOnFailure = true;
            return this;
        }

        /// <summary>收尾步骤：前面失败也执行，自己失败不阻断后续。</summary>
        public HarnessStep Cleanup()
        {
            AlwaysRun = true;
            ContinueOnFailure = true;
            return this;
        }

        public HarnessStep ExpectingModal()
        {
            ExpectModal = true;
            return this;
        }

        public HarnessStep SkipIf(Func<bool> condition, string reason, bool allowed = false)
        {
            SkipWhen = condition;
            SkipReason = reason;
            AllowSkip = allowed;
            return this;
        }
    }
}
