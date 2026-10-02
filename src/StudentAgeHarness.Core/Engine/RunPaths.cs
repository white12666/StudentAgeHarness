using System;
using System.IO;

namespace StudentAgeHarness.Engine
{
    internal static class RunPaths
    {
        internal static bool IsInside(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
            string full = Path.GetFullPath(path).TrimEnd('\\', '/');
            string parent = Path.GetFullPath(root).TrimEnd('\\', '/');
            return string.Equals(full, parent, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
