using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PiaoMate
{
    /// <summary>发票下载：链接 / 二维码截图 / 图片链接 → 下载 PDF → 自动按抬头整理。</summary>
    public class DownloadForm : Form
    {
        class Job
        {
            public string Source;
            public Bitmap Image;
            public string Text;
            public string LocalPdf;
            public string OpenUrl = "";
            public ListViewItem Item;
            public bool Removed, Running, NeedsBrowser;
            /// <summary>已整理的发票（Invoice.Path 指向整理后的文件）。</summary>
            public readonly List<OrganizeItem> Done = new List<OrganizeItem>();
        }

        readonly Settings cfg;
        readonly Downloader downloader = new Downloader();
        readonly Queue<Job> queue = new Queue<Job>();
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        TextBox txtUrls, txtDst;
        ListView lv;
        Label lblStatus;
        bool running;
        public bool Changed { get; private set; }

        public DownloadForm(Settings cfg)
        {
            this.cfg = cfg;
            Text = "下载发票";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(900, 580);
            MinimumSize = new Size(600, 420);
            StartPosition = FormStartPosition.CenterParent;
            ShowIcon = false;
            KeyPreview = true;
            AllowDrop = true;
            BuildUi();
            txtDst.Text = cfg.EffectiveArchiveDir;
            DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap) || e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (s, e) => AddFromData(e.Data);
            FormClosing += (s, e) =>
            {
                if (running && MessageBox.Show(this, "仍有下载在进行，确定关闭吗？", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) { e.Cancel = true; return; }
                cts.Cancel();
                string d = txtDst.Text.Trim();
                cfg.ArchiveDir = cfg.ExportDir.Length > 0 && string.Equals(d, Path.Combine(cfg.ExportDir, "发票整理"), StringComparison.OrdinalIgnoreCase) ? "" : d;
            };
        }

        static Label L(string t) { return new Label { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }; }

        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            txtDst = new TextBox { Dock = DockStyle.Fill };
            var b1 = new Button { Text = "浏览…", AutoSize = true };
            var b2 = new Button { Text = "打开", AutoSize = true };
            b1.Click += (s, e) => { var p = FolderPicker.Pick(this, txtDst.Text.Trim(), "选择整理后存放的目录"); if (p != null) txtDst.Text = p; };
            b2.Click += (s, e) => OpenDir(txtDst.Text.Trim());
            t.Controls.Add(L("整理到："), 0, 0); t.Controls.Add(txtDst, 1, 0); t.Controls.Add(b1, 2, 0); t.Controls.Add(b2, 3, 0);
            root.Controls.Add(t, 0, 0);

            root.Controls.Add(new Label
            {
                AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 4),
                Text = "在下框输入发票链接（每行一个）；也可按 Ctrl+V 粘贴二维码截图或图片链接，或把二维码图片 / 已下载的 PDF 拖入本窗口。下载后自动按抬头整理。"
            }, 0, 1);

            txtUrls = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
            root.Controls.Add(txtUrls, 0, 2);

            var f = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            var bDl = new Button { Text = "下载链接", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
            var bPaste = new Button { Text = "粘贴二维码/链接 (Ctrl+V)", AutoSize = true };
            var bImg = new Button { Text = "选择二维码图片…", AutoSize = true };
            var bPdf = new Button { Text = "导入已下载的 PDF…", AutoSize = true };
            var bIssue = new Button { Text = "内置浏览器开票", AutoSize = true, ForeColor = Color.DarkBlue };
            bIssue.Click += (s, e) => OpenIssueForSelected();
            var bOpenUrl = new Button { Text = "在浏览器打开所选", AutoSize = true };
            var bClear = new Button { Text = "清除已完成", AutoSize = true };
            var bDel = new Button { Text = "删除所选", AutoSize = true };
            var bDelAll = new Button { Text = "清空列表", AutoSize = true };
            bDel.Click += (s, e) => RemoveJobs(lv.SelectedItems.Cast<ListViewItem>());
            bDelAll.Click += (s, e) => RemoveJobs(lv.Items.Cast<ListViewItem>());
            bDl.Click += (s, e) => AddUrlsFromBox();
            bPaste.Click += (s, e) => PasteClipboard();
            bImg.Click += (s, e) => PickFiles("图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*");
            bPdf.Click += (s, e) => PickFiles("PDF 发票|*.pdf");
            bOpenUrl.Click += (s, e) => OpenSelectedUrl();
            bClear.Click += (s, e) => { foreach (ListViewItem it in lv.Items.Cast<ListViewItem>().ToList()) if (it.SubItems[1].Text.StartsWith("已整理")) lv.Items.Remove(it); };
            f.Controls.AddRange(new Control[] { bDl, bPaste, bImg, bPdf, bIssue, bOpenUrl, bClear, bDel, bDelAll });
            root.Controls.Add(f, 0, 3);

            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, ShowItemToolTips = true };
            lv.Columns.Add("来源", 260);
            lv.Columns.Add("状态", 90);
            lv.Columns.Add("结果 / 提示", 520);
            lv.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) { RemoveJobs(lv.SelectedItems.Cast<ListViewItem>()); e.Handled = true; } };
            lv.DoubleClick += (s, e) =>
            {
                if (lv.SelectedItems.Count == 0) return;
                var j = (Job)lv.SelectedItems[0].Tag;
                string res = DoneFiles(new[] { j }).FirstOrDefault();
                if (res != null) { try { Process.Start(res); } catch { } }
                else if (j.NeedsBrowser) OpenIssue(j);
                else if (j.OpenUrl.Length > 0) OpenUrl(j.OpenUrl);
            };
            var menu = new ContextMenuStrip();
            var miShow = new ToolStripMenuItem("打开所在目录并选中", null, (s, e) => ShellHelper.ShowInFolder(DoneFiles(SelectedJobs())));
            var miCopyPath = new ToolStripMenuItem("复制文件地址", null, (s, e) =>
            {
                var paths = DoneFiles(SelectedJobs()).ToArray();
                try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); lblStatus.Text = "已复制 " + paths.Length + " 个文件地址"; }
                catch { MessageBox.Show(this, "复制失败，剪贴板被其他程序占用。", Text); }
            });
            menu.Items.Add(miShow);
            menu.Items.Add(miCopyPath);
            menu.Items.Add(new ToolStripSeparator());
            menu.Opening += (s, e) =>
            {
                if (lv.SelectedItems.Count == 0) { e.Cancel = true; return; }
                miShow.Enabled = miCopyPath.Enabled = DoneFiles(SelectedJobs()).Any();
            };
            menu.Items.Add("在内置浏览器中开票", null, (s, e) => OpenIssueForSelected());
            menu.Items.Add("在浏览器打开链接", null, (s, e) => OpenSelectedUrl());
            menu.Items.Add("删除所选", null, (s, e) => RemoveJobs(lv.SelectedItems.Cast<ListViewItem>()));
            menu.Items.Add("复制提示文字", null, (s, e) => { if (lv.SelectedItems.Count > 0) try { Clipboard.SetText(lv.SelectedItems[0].SubItems[2].Text); } catch { } });
            lv.ContextMenuStrip = menu;
            root.Controls.Add(lv, 0, 4);

            lblStatus = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 6, 3, 3) };
            root.Controls.Add(lblStatus, 0, 5);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // 输入框内粘贴纯文字时交给文本框；有图片/文件时由本窗口处理
            if (keyData == (Keys.Control | Keys.V))
            {
                bool hasNonText = false;
                try { hasNonText = Clipboard.ContainsImage() || Clipboard.ContainsFileDropList(); } catch { }
                if (!txtUrls.Focused || hasNonText) { PasteClipboard(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        static void OpenDir(string d) { if (Directory.Exists(d)) Process.Start("explorer.exe", "\"" + d + "\""); }
        static void OpenUrl(string u) { try { Process.Start(u); } catch { } }

        void OpenSelectedUrl()
        {
            foreach (ListViewItem it in lv.SelectedItems)
            {
                var j = (Job)it.Tag;
                if (j.OpenUrl.Length > 0) OpenUrl(j.OpenUrl);
            }
        }

        void PickFiles(string filter)
        {
            using (var d = new OpenFileDialog { Filter = filter, Multiselect = true })
                if (d.ShowDialog(this) == DialogResult.OK) AddFiles(d.FileNames);
        }

        void AddUrlsFromBox()
        {
            string text = txtUrls.Text;
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) { MessageBox.Show(this, "请先在输入框中填写发票链接，每行一个。", Text); return; }
            foreach (var l in lines) Enqueue(new Job { Source = l, Text = l });
            txtUrls.Clear();
        }

        void PasteClipboard()
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null || !AddFromData(data)) MessageBox.Show(this, "剪贴板中没有图片、链接或文件。", Text);
            }
            catch (Exception ex) { MessageBox.Show(this, "读取剪贴板失败：" + ex.Message, Text); }
        }

        bool AddFromData(IDataObject data)
        {
            if (data.GetDataPresent(DataFormats.FileDrop)) { var files = data.GetData(DataFormats.FileDrop) as string[]; if (files != null && files.Length > 0) { AddFiles(files); return true; } }
            if (data.GetDataPresent(DataFormats.Bitmap))
            {
                var img = data.GetData(DataFormats.Bitmap) as Image;
                if (img != null) { Enqueue(new Job { Source = "粘贴的图片 " + DateTime.Now.ToString("HH:mm:ss"), Image = new Bitmap(img) }); return true; }
            }
            // 从浏览器复制图片时可能只有 HTML（含 <img src>）
            if (data.GetDataPresent(DataFormats.Html))
            {
                var html = data.GetData(DataFormats.Html) as string ?? "";
                var m = Regex.Match(html, @"<img[^>]+src\s*=\s*[""'](https?://[^""']+)[""']", RegexOptions.IgnoreCase);
                if (m.Success) { Enqueue(new Job { Source = m.Groups[1].Value, Text = m.Groups[1].Value }); return true; }
            }
            if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
            {
                string text = (data.GetData(DataFormats.UnicodeText) ?? data.GetData(DataFormats.Text)) as string ?? "";
                var urls = Downloader.ExtractUrls(text);
                if (urls.Count > 0) { foreach (var u in urls) Enqueue(new Job { Source = u, Text = u }); return true; }
                if (text.Trim().Length > 0) { Enqueue(new Job { Source = text.Trim(), Text = text.Trim() }); return true; }
            }
            return false;
        }

        void AddFiles(IEnumerable<string> files)
        {
            foreach (var f in files)
            {
                if (Directory.Exists(f)) continue;
                if (f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) Enqueue(new Job { Source = f, LocalPdf = f });
                else
                {
                    try
                    {
                        Bitmap b;
                        using (var img = Image.FromFile(f)) b = new Bitmap(img);
                        Enqueue(new Job { Source = f, Image = b });
                    }
                    catch { AddRow(new Job { Source = f }, "失败", "不是可识别的图片或 PDF 文件"); }
                }
            }
        }

        void OpenIssueForSelected()
        {
            if (lv.SelectedItems.Count == 0) { MessageBox.Show(this, "请先选中一行（需有链接）。", Text); return; }
            var j = (Job)lv.SelectedItems[0].Tag;
            if (j.OpenUrl.Length == 0) { MessageBox.Show(this, "该行没有可打开的链接。", Text); return; }
            OpenIssue(j);
        }

        void OpenIssue(Job j)
        {
            if (IsDisposed || j.Removed) return;
            string dst = txtDst.Text.Trim();
            if (dst.Length == 0) { MessageBox.Show(this, "请先设置「整理到」目录。", Text); return; }
            if (IssueForm.Companies(cfg).Count == 0) { MessageBox.Show(this, "还没有可用的开票抬头。请在主窗口筛选区选「自定义…」填写公司名称和税号，或在配置文件 PiaoMate.config 中用 CompanyN=名称|税号 添加备选公司。", Text); return; }
            if (!IssuePicker.Pick(this, cfg)) return;
            var f = new IssueForm(cfg, j.OpenUrl, dst, res =>
            {
                Changed = true;
                if (j.Removed || j.Item.ListView == null) return;
                var r = res[0];
                string prev = j.Item.SubItems[1].Text.StartsWith("已整理") ? j.Item.SubItems[2].Text + "；" : "";
                if (r.Verified) { r.Invoice.Path = r.Target; j.Done.Add(r); }
                if (r.Verified) SetRow(j, r.Invoice.Ok ? "已整理" : "已整理(未识别)", prev + r.Target, r.Invoice.Ok ? Color.DarkGreen : Color.DarkOrange);
                else SetRow(j, "失败", r.Status, Color.Firebrick);
            });
            f.Show(this);
        }

        /// <summary>从列表移除（未开始的同时取消；正在处理的保留）。只移除列表行，不删除任何已下载/整理的文件。</summary>
        void RemoveJobs(IEnumerable<ListViewItem> items)
        {
            var list = items.ToList();
            if (list.Count == 0) { MessageBox.Show(this, "请先选中要删除的行。", Text); return; }
            int busy = 0;
            lv.BeginUpdate();
            foreach (var it in list)
            {
                var j = (Job)it.Tag;
                if (j.Running) { busy++; continue; }
                j.Removed = true;
                if (j.Image != null) { j.Image.Dispose(); j.Image = null; }
                lv.Items.Remove(it);
            }
            lv.EndUpdate();
            if (busy > 0) lblStatus.Text = "有 " + busy + " 项正在处理，完成后才能删除。";
        }

        ListViewItem AddRow(Job j, string status, string result)
        {
            var it = new ListViewItem(j.Source) { Tag = j };
            it.SubItems.Add(status); it.SubItems.Add(result);
            lv.Items.Add(it);
            it.EnsureVisible();
            j.Item = it;
            it.ToolTipText = Tip(j);
            return it;
        }

        void Enqueue(Job j)
        {
            AddRow(j, "等待", "");
            queue.Enqueue(j);
            if (!running) RunQueue();
        }

        void SetRow(Job j, string status, string result, Color color)
        {
            j.Item.SubItems[1].Text = status;
            j.Item.SubItems[2].Text = result;
            j.Item.ForeColor = color;
            j.Item.ToolTipText = Tip(j);
        }

        List<Job> SelectedJobs() { return lv.SelectedItems.Cast<ListViewItem>().Select(i => (Job)i.Tag).ToList(); }

        static IEnumerable<string> DoneFiles(IEnumerable<Job> jobs)
        {
            return jobs.SelectMany(j => j.Done).Select(r => r.Target).Where(File.Exists);
        }

        /// <summary>悬停信息：来源、状态；已整理的显示每张发票的概要，否则显示完整提示文字。</summary>
        static string Tip(Job j)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("来源：").Append(Wrap(j.Source, 70));
            if (j.Item != null) sb.Append("\n状态：").Append(j.Item.SubItems[1].Text);
            if (j.LocalPdf != null) sb.Append("\n原文件：").Append(j.LocalPdf).Append("（保留）");
            for (int k = 0; k < j.Done.Count; k++)
            {
                sb.Append("\n\n");
                if (j.Done.Count > 1) sb.Append("【第 ").Append(k + 1).Append(" 张】\n");
                sb.Append(j.Done[k].Invoice.Summary());
            }
            if (j.Done.Count == 0)
            {
                if (j.Item != null && j.Item.SubItems[2].Text.Length > 0) sb.Append("\n").Append(Wrap(j.Item.SubItems[2].Text, 40));
                if (j.OpenUrl.Length > 0) sb.Append("\n链接：").Append(Wrap(j.OpenUrl, 70));
            }
            return sb.ToString();
        }

        static string Wrap(string s, int width)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= width) return s ?? "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i += width) { if (i > 0) sb.Append('\n'); sb.Append(s, i, Math.Min(width, s.Length - i)); }
            return sb.ToString();
        }

        async void RunQueue()
        {
            running = true;
            try
            {
                while (queue.Count > 0)
                {
                    var j = queue.Dequeue();
                    if (j.Removed) continue;
                    string dst = txtDst.Text.Trim();
                    if (dst.Length == 0) { SetRow(j, "失败", "请先设置「整理到」目录后重新添加", Color.Firebrick); continue; }
                    lblStatus.Text = "正在处理：" + j.Source + (queue.Count > 0 ? "（还有 " + queue.Count + " 项）" : "");
                    SetRow(j, "处理中…", "", SystemColors.WindowText);
                    j.Running = true;
                    try { await RunJob(j, dst); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { SetRow(j, "失败", ex.Message, Color.Firebrick); }
                    finally { j.Running = false; }
                }
                lblStatus.Text = "全部完成。成功的发票已整理到：" + txtDst.Text.Trim();
            }
            finally { running = false; }
        }

        async Task RunJob(Job j, string dst)
        {
            var ct = cts.Token;
            var pdfs = new List<string>();
            bool temp = j.LocalPdf == null;
            string tempDir = Path.Combine(Path.GetTempPath(), "PiaoMate_dl", Guid.NewGuid().ToString("N"));
            DownloadOutcome o = null;
            if (j.LocalPdf != null) pdfs.Add(j.LocalPdf);
            else
            {
                SetRow(j, "下载中…", "", SystemColors.WindowText);
                if (j.Image != null) { var img = j.Image; o = await Task.Run(() => downloader.FromImage(img, tempDir, ct)); }
                else o = await Task.Run(() => downloader.FromText(j.Text, tempDir, ct));
                j.OpenUrl = o.OpenUrl;
                pdfs.AddRange(o.Pdfs);
            }
            if (pdfs.Count == 0)
            {
                if (o != null && o.NeedsBrowser && j.OpenUrl.Length > 0)
                {
                    j.NeedsBrowser = true;
                    SetRow(j, "需开票", "打开的是开票/下载页面，需要填写抬头等信息。已在内置浏览器中打开（也可双击此行），自动填写后请核对并提交，生成的发票会自动整理。", Color.DarkBlue);
                    // 队列中没有其他待处理项时自动打开，避免批量时弹出多个窗口
                    if (queue.Count == 0) BeginInvoke((Action)(() => OpenIssue(j)));
                }
                else SetRow(j, "需手动下载", o != null ? o.Message : "未能下载", Color.DarkOrange);
                return;
            }
            try
            {
                var res = await Task.Run(() =>
                {
                    var invs = pdfs.Select(InvoiceParser.Parse).ToList();
                    var r = Organizer.Organize(invs, dst, includeUnknown: true);
                    Organizer.WriteIndex(r, DateTime.Now);
                    return r;
                });
                Changed = true;
                var good = res.Where(r => r.Verified).ToList();
                if (good.Count == 0) { SetRow(j, "失败", string.Join("；", res.Select(r => r.Status)), Color.Firebrick); return; }
                foreach (var r in good) r.Invoice.Path = r.Target;
                j.Done.AddRange(good);
                bool unknown = good.Any(r => !r.Invoice.Ok);
                string text = string.Join("；", good.Select(r => r.Target));
                if (unknown) text = "（未识别为发票，已放入 " + Organizer.UnknownFolder + "）" + text;
                if (j.LocalPdf != null) text += "（原文件保留）";
                SetRow(j, unknown ? "已整理(未识别)" : "已整理", text, unknown ? Color.DarkOrange : Color.DarkGreen);
            }
            finally
            {
                if (temp) { try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { } }
                if (j.Image != null) { j.Image.Dispose(); j.Image = null; }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) cts.Dispose();
            base.Dispose(disposing);
        }
    }
}
