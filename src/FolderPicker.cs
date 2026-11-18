using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PiaoMate
{
    /// <summary>Windows Vista+ 风格的文件夹选择对话框（带地址栏，可直接输入/粘贴路径）。</summary>
    public static class FolderPicker
    {
        public static string Pick(IWin32Window owner, string initial, string title)
        {
            try
            {
                var dlg = (IFileOpenDialog)new FileOpenDialogCom();
                uint opts; dlg.GetOptions(out opts);
                dlg.SetOptions(opts | 0x20 /*PICKFOLDERS*/ | 0x40 /*FORCEFILESYSTEM*/ | 0x1000 /*FILEMUSTEXIST? PATHMUSTEXIST*/);
                if (!string.IsNullOrEmpty(title)) dlg.SetTitle(title);
                if (!string.IsNullOrEmpty(initial) && System.IO.Directory.Exists(initial))
                {
                    IShellItem item;
                    if (SHCreateItemFromParsingName(initial, IntPtr.Zero, typeof(IShellItem).GUID, out item) == 0)
                    {
                        dlg.SetFolder(item);
                        dlg.SetFileName(System.IO.Path.GetFileName(initial.TrimEnd('\\')));
                    }
                }
                int hr = dlg.Show(owner == null ? IntPtr.Zero : owner.Handle);
                if (hr != 0) return null; // 取消 (0x800704C7) 或其他
                IShellItem res; dlg.GetResult(out res);
                string path; res.GetDisplayName(0x80058000 /*FILESYSPATH*/, out path);
                return path;
            }
            catch (COMException) { return Fallback(owner, initial); }
            catch (InvalidCastException) { return Fallback(owner, initial); }
        }

        static string Fallback(IWin32Window owner, string initial)
        {
            using (var d = new FolderBrowserDialog())
            {
                if (System.IO.Directory.Exists(initial)) d.SelectedPath = initial;
                return d.ShowDialog(owner) == DialogResult.OK ? d.SelectedPath : null;
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        class FileOpenDialogCom { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint c, IntPtr p);
            void SetFileTypeIndex(uint i);
            void GetFileTypeIndex(out uint i);
            void Advise(IntPtr e, out uint c);
            void Unadvise(uint c);
            void SetOptions(uint o);
            void GetOptions(out uint o);
            void SetDefaultFolder(IShellItem i);
            void SetFolder(IShellItem i);
            void GetFolder(out IShellItem i);
            void GetCurrentSelection(out IShellItem i);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string n);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string n);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string t);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string t);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string t);
            void GetResult(out IShellItem i);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem i);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint a);
            void Compare(IShellItem o, uint hint, out int order);
        }
    }
}
