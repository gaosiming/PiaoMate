using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace PiaoMate
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // 从 exe 内嵌资源加载依赖程序集（必须在引用 PdfPig 的代码运行前注册）
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string name = new AssemblyName(e.Name).Name;
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("lib." + name + ".dll"))
                {
                    if (st == null) return null;
                    byte[] buf = new byte[st.Length];
                    int off = 0, n;
                    while (off < buf.Length && (n = st.Read(buf, off, buf.Length - off)) > 0) off += n;
                    return Assembly.Load(buf);
                }
            };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 首次启动（或免责声明版本更新后）须同意免责声明，不同意则退出
            if (!DisclaimerForm.EnsureAccepted()) return;
            Application.Run(new MainForm());
        }
    }
}
