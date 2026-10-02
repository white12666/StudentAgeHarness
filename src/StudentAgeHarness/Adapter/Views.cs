using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sdk;

namespace StudentAgeHarness.Plugin.Adapter
{
    /// <summary>读取 UIMgr 里登记的视图。UIMgr.Init 之前视图表还不存在，这里一律返回空。</summary>
    internal static class Views
    {
        private static readonly FieldInfo InstanceField = AccessTools.Field(typeof(UIMgr), "ins");
        private static readonly FieldInfo DictionaryField = AccessTools.Field(typeof(UIMgr), "viewDict");

        internal static List<BaseView> All()
        {
            try
            {
                object instance = InstanceField?.GetValue(null);
                if (!(DictionaryField?.GetValue(instance) is Dictionary<string, BaseView> dictionary)) return new List<BaseView>();
                return dictionary.Values.Where(view => view != null).ToList();
            }
            catch
            {
                return new List<BaseView>();
            }
        }

        /// <summary>按完整类型名或类名查找视图（不会创建实例）。</summary>
        internal static BaseView Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string trimmed = name.Trim();
            List<BaseView> views = All();
            BaseView exact = views.FirstOrDefault(view => string.Equals(view.Name, trimmed, StringComparison.Ordinal));
            if (exact != null) return exact;
            List<BaseView> bySimpleName = views.Where(view =>
                string.Equals(view.GetType().Name, trimmed, StringComparison.Ordinal)).ToList();
            if (bySimpleName.Count > 1)
                throw new InvalidOperationException("视图名 " + trimmed + " 不唯一，请写完整类型名：" +
                    string.Join(", ", bySimpleName.Select(view => view.Name).ToArray()));
            return bySimpleName.FirstOrDefault();
        }

        internal static bool IsOpened(BaseView view)
        {
            return view != null && view.viewState == ViewState.Opened && view.gameObject != null;
        }

        internal static bool IsOpening(BaseView view)
        {
            return view != null && (view.viewState == ViewState.Loading || view.viewState == ViewState.Loaded);
        }

        internal static BaseView Top()
        {
            try { return UIMgr.GetTopView(); }
            catch { return null; }
        }

        internal static IList<string> OpenedNames()
        {
            return All().Where(IsOpened).Select(view => view.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
        }
    }
}
