using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace StudentAgeHarness
{
    public sealed class RegistryValue
    {
        public string name;
        public uint type;
        public string data;
    }

    // Unity sometimes stores eight bytes under REG_DWORD. Preserve the raw type and
    // bytes instead of using RegistryKey.GetValue/SetValue's lossy conversion.
    public static class RawRegistry
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegQueryValueEx(SafeRegistryHandle key, string name,
            IntPtr reserved, out uint type, byte[] data, ref uint length);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegSetValueEx(SafeRegistryHandle key, string name,
            uint reserved, uint type, byte[] data, uint length);

        public static RegistryValue Read(RegistryKey key, string name)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint type, length = 0;
                int code = RegQueryValueEx(key.Handle, name, IntPtr.Zero, out type, null, ref length);
                if (code != 0) throw new Win32Exception(code);
                var bytes = new byte[length];
                code = RegQueryValueEx(key.Handle, name, IntPtr.Zero, out type, bytes, ref length);
                if (code == 234) continue;
                if (code != 0) throw new Win32Exception(code);
                return new RegistryValue { name = name, type = type, data = Convert.ToBase64String(bytes, 0, (int)length) };
            }
            throw new InvalidOperationException("Registry value changed while taking snapshot.");
        }

        public static void Write(RegistryKey key, string name, uint type, string data)
        {
            byte[] bytes = Convert.FromBase64String(data);
            int code = RegSetValueEx(key.Handle, name, 0, type, bytes, (uint)bytes.Length);
            if (code != 0) throw new Win32Exception(code);
        }
    }
}
