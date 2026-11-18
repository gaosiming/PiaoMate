using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PiaoMate
{
    /// <summary>
    /// 免责声明（嵌入 exe 的 docs/免责声明.md）。首次启动时必须同意才能使用；工具栏「免责声明」可随时查看。
    /// </summary>
    public static class DisclaimerForm
    {
        /// <summary>免责声明版本。声明内容有实质修改时加 1，已同意旧版本的用户下次启动会再次确认。</summary>
        public const string Version = "1";

        /// <summary>读取嵌入的免责声明原文；没有时返回空串。</summary>
        public static string Raw()
        {
            using (var s = typeof(DisclaimerForm).Assembly.GetManifestResourceStream("免责声明.md"))
            {
                if (s == null) return "";
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        /// <summary>转成便于阅读的纯文本：去掉标题行和 Markdown 标记（** 和 `），条目之间空一行。</summary>
        public static string Text(string md)
        {
            var sb = new StringBuilder();
            foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                line = line.Replace("**", "").Replace("`", "");
                if (sb.Length > 0) sb.AppendLine().AppendLine();
                sb.Append(line);
            }
            return sb.ToString();
        }

        /// <summary>是否需要弹出确认：配置里记录的已同意版本与当前版本不同。</summary>
        public static bool NeedsConfirm(Settings cfg) { return cfg.DisclaimerAccepted != Version; }

        /// <summary>
        /// 首次启动确认：已同意当前版本时直接返回 true；否则弹窗，同意后写入配置并返回 true，不同意返回 false（调用方退出程序）。
        /// </summary>
        public static bool EnsureAccepted()
        {
            var cfg = Settings.Load();
            if (!NeedsConfirm(cfg)) return true;
            if (!Show(null, true)) return false;
            cfg.DisclaimerAccepted = Version;
            cfg.Save();
            return true;
        }

        /// <summary>显示免责声明。confirm=true 时为首次确认（需勾选同意），返回是否同意；否则只读查看，返回 true。</summary>
        public static bool Show(IWin32Window owner, bool confirm)
        {
            string text = Text(Raw());
            if (text.Length == 0) text = "测试生成的虚构发票仅用于测试数据格式解析，不得当作正式发票提交；使用假发票是违法行为，滥用行为与本软件作者无关。";
            using (var f = new Form
            {
                Text = "票小秘 — 免责声明",
                StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
                Size = new Size(640, 520),
                MinimumSize = new Size(480, 380),
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = owner == null,
                Font = new Font("Microsoft YaHei UI", 9F)
            })
            {
                var head = new Label
                {
                    Text = confirm ? "使用本软件前，请仔细阅读并同意以下免责声明：" : "免责声明",
                    Dock = DockStyle.Top, Height = 32, Padding = new Padding(8, 8, 8, 0),
                    Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold)
                };
                var box = new TextBox
                {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                    BackColor = SystemColors.Window, Text = text
                };
                var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
                if (confirm)
                {
                    var agree = new Button { Text = "同意并继续", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };
                    var refuse = new Button { Text = "不同意并退出", AutoSize = true, DialogResult = DialogResult.Cancel };
                    var chk = new CheckBox { Text = "我已阅读并同意上述免责声明", AutoSize = true, Margin = new Padding(3, 8, 24, 3) };
                    chk.CheckedChanged += (s, e) => agree.Enabled = chk.Checked;
                    bottom.Controls.Add(refuse); bottom.Controls.Add(agree); bottom.Controls.Add(chk);
                    f.AcceptButton = agree; f.CancelButton = refuse;
                }
                else
                {
                    var ok = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.OK };
                    bottom.Controls.Add(ok);
                    f.AcceptButton = ok; f.CancelButton = ok;
                }
                f.Controls.Add(box); f.Controls.Add(bottom); f.Controls.Add(head);
                f.Shown += (s, e) => { box.SelectionStart = 0; box.SelectionLength = 0; };
                var r = f.ShowDialog(owner);
                return !confirm || r == DialogResult.OK;
            }
        }
    }
}
