namespace StudentAgeHarness
{
    /// <summary>
    /// 场景包可以提供扩展，参与整次运行的生命周期。实现类放在场景包里，有公开无参构造函数即可被自动发现。
    /// 一般继承 <see cref="HarnessExtension"/>，只重写需要的方法。
    /// </summary>
    public interface IHarnessExtension
    {
        /// <summary>游戏就绪、第一个场景开始前调用一次。</summary>
        void OnRunStart(HarnessRunContext run);

        /// <summary>
        /// 场景之间回主菜单时反复调用（每 0.25 秒一次），直到返回 true。
        /// 用来关掉自己 mod 打开的界面、等自己的异步收尾完成。可以在这里执行操作，但要能重复调用。
        /// </summary>
        bool TryRecover();

        /// <summary>
        /// 如果屏幕上有本 mod 的弹窗正在挡住流程，返回一句描述；否则返回 null。
        /// 步骤没有声明 ExpectModal 时，出现这种弹窗会记一条 error。
        /// </summary>
        string DescribeUnexpectedModal();

        /// <summary>写报告前调用，可以用 report.SetExtension 写入自己的数据。</summary>
        void OnRunEnd(HarnessReport report);
    }

    /// <summary><see cref="IHarnessExtension"/> 的空实现，按需重写。</summary>
    public abstract class HarnessExtension : IHarnessExtension
    {
        public virtual void OnRunStart(HarnessRunContext run)
        {
        }

        public virtual bool TryRecover()
        {
            return true;
        }

        public virtual string DescribeUnexpectedModal()
        {
            return null;
        }

        public virtual void OnRunEnd(HarnessReport report)
        {
        }
    }
}
