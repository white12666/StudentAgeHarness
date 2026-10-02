using System.Collections.Generic;

namespace StudentAgeHarness
{
    /// <summary>
    /// 一个场景：返回按顺序执行的步骤。<see cref="Build"/> 只负责描述步骤，不要在里面直接操作界面；
    /// 真正的操作写进步骤的 Act/ActRoutine，这样失败时报告能定位到具体步骤。
    /// </summary>
    public interface IHarnessScenario
    {
        IEnumerable<HarnessStep> Build(HarnessContext context);
    }
}
