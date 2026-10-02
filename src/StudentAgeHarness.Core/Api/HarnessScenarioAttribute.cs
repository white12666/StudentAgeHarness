using System;

namespace StudentAgeHarness
{
    /// <summary>
    /// 标记一个场景。被标记的类必须实现 <see cref="IHarnessScenario"/>，并且有公开的无参构造函数。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class HarnessScenarioAttribute : Attribute
    {
        public HarnessScenarioAttribute(string name)
        {
            Name = name;
        }

        /// <summary>场景名。配置文件和命令行用它选择场景，建议只用小写字母、数字和连字符。</summary>
        public string Name { get; }

        /// <summary>一句话说明这个场景验证什么，会写进报告。</summary>
        public string Description { get; set; }

        /// <summary>标签。可以用 <c>tag:名字</c> 选择；带 <c>manual</c> 标签的场景只在被点名时运行。</summary>
        public string[] Tags { get; set; }

        /// <summary>依赖的 BepInEx 插件 GUID。缺任何一个时这个场景不运行，并在报告里写明原因。</summary>
        public string[] RequiresPlugins { get; set; }

        /// <summary>这个场景最多运行多少秒；0 表示只受整次运行的看门狗限制。</summary>
        public float TimeoutSec { get; set; }

        /// <summary>true 时这个场景只能单独运行，和其它场景一起选中时只保留它。</summary>
        public bool RunAlone { get; set; }

        /// <summary>true 时需要游戏窗口真正获得焦点；后台模式下会跳过。</summary>
        public bool RequiresFocus { get; set; }

        /// <summary>同一个场景包里的执行顺序，数字小的先运行。</summary>
        public int Order { get; set; }
    }
}
