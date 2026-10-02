using System;
using System.Collections;
using System.Collections.Generic;

namespace StudentAgeHarness.Engine
{
    /// <summary>把嵌套的 IEnumerator 展平，让嵌套协程里抛出的异常也落在引擎的捕获范围内。</summary>
    internal static class RoutineDriver
    {
        internal static IEnumerator Flatten(IEnumerator root)
        {
            var stack = new Stack<IEnumerator>();
            if (root != null) stack.Push(root);
            try
            {
                while (stack.Count > 0)
                {
                    IEnumerator current = stack.Peek();
                    if (!current.MoveNext())
                    {
                        stack.Pop();
                        (current as IDisposable)?.Dispose();
                    }
                    else if (current.Current is IEnumerator child) stack.Push(child);
                    else yield return current.Current;
                }
            }
            finally
            {
                while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            }
        }
    }
}
