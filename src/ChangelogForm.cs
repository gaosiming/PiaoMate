using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PiaoMate
{
    /// <summary>显示嵌入 exe 的更新日志（仓库根目录 CHANGELOG.md，发版时由 tools\release.ps1 维护）。</summary>
    public static class ChangelogForm
    {
        /// <summary>读取嵌入的更新日志原文；没有时返回空串。</summary>
        public static string Raw()
        {
            using (var s = typeof(ChangelogForm).Assembly.GetManifestResourceStream("CHANGELOG.md"))
            {
                if (s == null) return "";
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        /// <summary>转成便于阅读的纯文本：去掉说明头和空的「未发布」，标题去掉 # 号。</summary>
        public static string Text(string md)
        {
            md = md.Replace("\r\n", "\n");
            int first = Regex.Match(md, @"(?m)^## ").Index;
            if (first > 0) md = md.Substring(first);
            md = Regex.Replace(md, @"(?m)^## \[未发布\]\s*(?=^## |\z)", "");
            var sb = new StringBuilder();
            foreach (var line in md.Split('\n'))
            {
                var m = Regex.Match(line, @"^## \[(.+?)\](.*)$");
                if (m.Success) { if (sb.Length > 0) sb.AppendLine(); sb.AppendLine((m.Groups[1].Value == "未发布" ? "未发布" : "v" + m.Groups[1].Value) + m.Groups[2].Value); continue; }
                if (line.StartsWith("### ")) { sb.AppendLine("【" + line.Substring(4).Trim() + "】"); continue; }
                if (line.StartsWith("- ")) { sb.AppendLine("  · " + line.Substring(2).Replace("`", "")); continue; }
                if (line.Trim().Length > 0) sb.AppendLine(line.Replace("`", ""));
            }
            return sb.ToString().TrimEnd();
        }

        public static void ShowLog(IWin32Window owner)
        {
            string text = Text(Raw());
            using (var f = new Form
            {
                Text = "更新日志 — 当前版本 v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3),
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(640, 560),
                MinimizeBox = false,
                ShowInTaskbar = false,
                Font = new Font("Microsoft YaHei UI", 9F)
            })
            {
                var box = new TextBox
                {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                    BackColor = SystemColors.Window, Text = text.Length > 0 ? text : "（没有更新日志）"
                };
                var ok = new Button { Text = "关闭", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 32 };
                f.Controls.Add(box); f.Controls.Add(ok);
                f.AcceptButton = ok; f.CancelButton = ok;
                f.Shown += (s, e) => { box.SelectionStart = 0; box.SelectionLength = 0; };
                f.ShowDialog(owner);
            }
        }
    }
}
