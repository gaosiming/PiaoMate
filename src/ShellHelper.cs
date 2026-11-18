using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace PiaoMate
{
    /// <summary>资源管理器相关操作。</summary>
    public static class ShellHelper
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
        [DllImport("shell32.dll")]
        static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint flags);
        [DllImport("ole32.dll")]
        static extern void CoTaskMemFree(IntPtr pv);

        /// <summary>打开文件所在目录并选中这些文件（同一目录的多个文件一起选中；最多打开 5 个目录）。返回实际处理的文件数。</summary>
        public static int ShowInFolder(IEnumerable<string> paths)
        {
            var files = paths.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int n = 0;
            foreach (var g in files.GroupBy(p => Path.GetDirectoryName(p), StringComparer.OrdinalIgnoreCase).Take(5))
            {
                if (SelectIn(g.Key, g.ToList())) { n += g.Count(); continue; }
                try { Process.Start("explorer.exe", "/select,\"" + g.First() + "\""); n++; } catch { }
            }
            return n;
        }

        static bool SelectIn(string dir, List<string> files)
        {
            IntPtr folder = IntPtr.Zero;
            var items = new List<IntPtr>();
            try
            {
                uint o;
                if (SHParseDisplayName(dir, IntPtr.Zero, out folder, 0, out o) != 0) return false;
                foreach (var f in files) { IntPtr p; if (SHParseDisplayName(f, IntPtr.Zero, out p, 0, out o) == 0) items.Add(p); }
                if (items.Count == 0) return false;
                return SHOpenFolderAndSelectItems(folder, (uint)items.Count, items.ToArray(), 0) == 0;
            }
            catch { return false; }
            finally
            {
                if (folder != IntPtr.Zero) CoTaskMemFree(folder);
                foreach (var p in items) CoTaskMemFree(p);
            }
        }
    }
}
