using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PiaoMate
{
    /// <summary>发票整理：扫描目录 → 按 抬头_税号 分类复制到整理目录 → 用户确认后删除原始文件（移入回收站）。</summary>
    public class OrganizeForm : Form
    {
        readonly Settings cfg;
        TextBox txtSrc, txtDst;
        CheckBox chkRecurse;
        ListView lv;
        Label lblStatus;
        Button btnScan, btnRun, btnDelete;
        ProgressBar bar;
        List<Invoice> invoices = new List<Invoice>();
        List<OrganizeItem> lastResult = new List<OrganizeItem>();
        bool suppress, busy;
        /// <summary>是否复制/删除过文件（主窗口据此刷新）。</summary>
        public bool Changed { get; private set; }

        public OrganizeForm(Settings cfg, string invoiceDir)
        {
            this.cfg = cfg;
            Text = "整理发票";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(1000, 600);
            MinimumSize = new Size(640, 420);
            StartPosition = FormStartPosition.CenterParent;
            ShowIcon = false;
            BuildUi();
            txtSrc.Text = invoiceDir;
            txtDst.Text = cfg.EffectiveArchiveDir;
            chkRecurse.Checked = cfg.Recurse;
            Shown += (s, e) => { if (Directory.Exists(txtSrc.Text.Trim())) Scan(); };
            FormClosing += (s, e) =>
            {
                if (busy) { e.Cancel = true; return; }
                string d = txtDst.Text.Trim();
                cfg.ArchiveDir = string.Equals(d, Path.Combine(cfg.ExportDir, "发票整理"), StringComparison.OrdinalIgnoreCase) && cfg.ExportDir.Length > 0 ? "" : d;
            };
        }

        static Label L(string t) { return new Label { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }; }

        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2, AutoSize = true };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            txtSrc = new TextBox { Dock = DockStyle.Fill };
            txtDst = new TextBox { Dock = DockStyle.Fill };
            var b1 = new Button { Text = "浏览…", AutoSize = true };
            var b2 = new Button { Text = "浏览…", AutoSize = true };
            var bOpen = new Button { Text = "打开", AutoSize = true };
            btnScan = new Button { Text = "扫描", AutoSize = true };
            chkRecurse = new CheckBox { Text = "含子文件夹", AutoSize = true, Anchor = AnchorStyles.Left };
            b1.Click += (s, e) => { var p = FolderPicker.Pick(this, txtSrc.Text.Trim(), "选择要整理的发票目录"); if (p != null) { txtSrc.Text = p; Scan(); } };
            b2.Click += (s, e) => { var p = FolderPicker.Pick(this, txtDst.Text.Trim(), "选择整理后存放的目录"); if (p != null) { txtDst.Text = p; RefreshPreview(); } };
            bOpen.Click += (s, e) => OpenDir(txtDst.Text.Trim());
            btnScan.Click += (s, e) => Scan();
            txtDst.Leave += (s, e) => RefreshPreview();
            t.Controls.Add(L("发票目录："), 0, 0); t.Controls.Add(txtSrc, 1, 0); t.Controls.Add(b1, 2, 0); t.Controls.Add(btnScan, 3, 0); t.Controls.Add(chkRecurse, 4, 0);
            t.Controls.Add(L("整理到："), 0, 1); t.Controls.Add(txtDst, 1, 1); t.Controls.Add(b2, 2, 1); t.Controls.Add(bOpen, 3, 1);
            root.Controls.Add(t, 0, 0);

            root.Controls.Add(new Label
            {
                AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 6),
                Text = "按「抬头_税号」建子文件夹，文件命名为「金额_抬头_税号_发票号_开票日期.pdf」。整理时只复制、不改动原文件；整理完成后可在确认后把原文件移入回收站。"
            }, 0, 1);

            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, CheckBoxes = true, HideSelection = false, ShowItemToolTips = true };
            lv.Columns.Add("", 24);
            lv.Columns.Add("状态", 120);
            lv.Columns.Add("抬头(购买方)", 210);
            lv.Columns.Add("税号", 150);
            lv.Columns.Add("金额", 80, HorizontalAlignment.Right);
            lv.Columns.Add("开票日期", 85);
            lv.Columns.Add("原文件", 220);
            lv.Columns.Add("整理后", 380);
            lv.ItemChecked += (s, e) => { if (suppress) return; var inv = e.Item.Tag as Invoice; if (inv != null) inv.Checked = e.Item.Checked; UpdateStatus(); };
            lv.DoubleClick += (s, e) => { if (lv.SelectedItems.Count > 0) { try { Process.Start(((Invoice)lv.SelectedItems[0].Tag).Path); } catch { } } };
            var menu = new ContextMenuStrip();
            var miSrc = new ToolStripMenuItem("打开原文件所在目录并选中", null, (s, e) => ShowSelected(false));
            var miDst = new ToolStripMenuItem("打开整理后所在目录并选中", null, (s, e) => ShowSelected(true));
            var miCopy = new ToolStripMenuItem("复制文件地址", null, (s, e) => CopySelected());
            menu.Items.AddRange(new ToolStripItem[] { miSrc, miDst, miCopy });
            menu.Opening += (s, e) =>
            {
                if (lv.SelectedItems.Count == 0) { e.Cancel = true; return; }
                var sel = SelectedInvoices();
                miSrc.Enabled = sel.Any(i => File.Exists(i.Path));
                miDst.Enabled = sel.Any(i => TargetOf(i) != null);
            };
            lv.ContextMenuStrip = menu;
            root.Controls.Add(lv, 0, 2);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, AutoSize = true };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            for (int i = 0; i < 3; i++) bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lblStatus = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) };
            bar = new ProgressBar { Dock = DockStyle.Fill, Visible = false };
            var bAll = new Button { Text = "全选/全不选", AutoSize = true };
            btnRun = new Button { Text = "开始整理（复制）", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
            btnDelete = new Button { Text = "删除原始文件…", AutoSize = true, Enabled = false, ForeColor = Color.Firebrick };
            bAll.Click += (s, e) => ToggleAll();
            btnRun.Click += (s, e) => RunOrganize();
            btnDelete.Click += (s, e) => DeleteOriginals();
            bottom.Controls.Add(lblStatus, 0, 0); bottom.Controls.Add(bar, 1, 0); bottom.Controls.Add(bAll, 2, 0); bottom.Controls.Add(btnRun, 3, 0); bottom.Controls.Add(btnDelete, 4, 0);
            root.Controls.Add(bottom, 0, 3);
        }

        static void OpenDir(string d)
        {
            if (Directory.Exists(d)) Process.Start("explorer.exe", "\"" + d + "\"");
        }

        static bool IsUnder(string path, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                string p = Path.GetFullPath(path), d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        async void Scan()
        {
            if (busy) return;
            string dir = txtSrc.Text.Trim(), dst = txtDst.Text.Trim();
            if (!Directory.Exists(dir)) { MessageBox.Show(this, "请先选择有效的发票目录。", Text); return; }
            SetBusy(true, "正在扫描…");
            try
            {
                bool rec = chkRecurse.Checked;
                // 整理目录本身不参与扫描，避免重复整理
                var files = await Task.Run(() => MainForm.EnumeratePdfs(dir, rec).Where(f => !IsUnder(f, dst)).ToList());
                bar.Maximum = Math.Max(1, files.Count); bar.Value = 0;
                int done = 0;
                var bag = new System.Collections.Concurrent.ConcurrentBag<Invoice>();
                await Task.Run(() => Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, f =>
                {
                    bag.Add(InvoiceParser.Parse(f));
                    int d = Interlocked.Increment(ref done);
                    if (d % 5 == 0 || d == files.Count) BeginInvoke((Action)(() => bar.Value = Math.Min(bar.Maximum, d)));
                }));
                invoices = bag.OrderBy(i => i.Ok ? 0 : 1).ThenBy(i => i.BuyerName).ThenBy(i => i.Date).ThenBy(i => i.FileName).ToList();
                foreach (var i in invoices) i.Checked = i.Ok;
                lastResult = new List<OrganizeItem>();
                btnDelete.Enabled = false;
            }
            catch (Exception ex) { MessageBox.Show(this, "扫描失败：" + ex.Message, Text); }
            finally { SetBusy(false, null); }
            Fill(null);
        }

        void Fill(Dictionary<Invoice, OrganizeItem> res)
        {
            suppress = true;
            lv.BeginUpdate(); lv.Items.Clear();
            string dst = txtDst.Text.Trim();
            foreach (var i in invoices)
            {
                OrganizeItem r = null;
                if (res != null) res.TryGetValue(i, out r);
                var it = new ListViewItem("") { Tag = i, Checked = i.Checked };
                it.SubItems.Add(r != null ? r.Status : (i.Ok ? "待整理" : "无法识别"));
                it.SubItems.Add(i.Ok ? i.BuyerName : "");
                it.SubItems.Add(i.BuyerTaxId);
                it.SubItems.Add(i.Ok ? i.Amount.ToString("F2", CultureInfo.InvariantCulture) : "");
                it.SubItems.Add(i.Date.HasValue ? i.Date.Value.ToString("yyyy-MM-dd") : "");
                it.SubItems.Add(r != null ? r.Source : i.Path);
                string target = r != null && r.Target.Length > 0 ? r.Target : (i.Ok ? Path.Combine(dst, Organizer.RelativeTarget(i)) : i.Error);
                it.SubItems.Add(target);
                it.ToolTipText = i.Summary() + (r != null && r.Target.Length > 0 ? "\n整理后：" + r.Target : "") + (r != null ? "\n状态：" + r.Status : "");
                if (!i.Ok) it.ForeColor = SystemColors.GrayText;
                else if (r != null && r.Status.StartsWith("失败")) it.ForeColor = Color.Firebrick;
                else if (r != null && r.Verified) it.ForeColor = Color.DarkGreen;
                lv.Items.Add(it);
            }
            lv.EndUpdate();
            suppress = false;
            UpdateStatus();
        }

        List<Invoice> SelectedInvoices() { return lv.SelectedItems.Cast<ListViewItem>().Select(it => (Invoice)it.Tag).ToList(); }

        string TargetOf(Invoice i)
        {
            var r = lastResult.FirstOrDefault(x => x.Invoice == i);
            return r != null && r.Target.Length > 0 && File.Exists(r.Target) ? r.Target : null;
        }

        void ShowSelected(bool target)
        {
            var paths = SelectedInvoices().Select(i => target ? TargetOf(i) : i.Path).ToList();
            if (ShellHelper.ShowInFolder(paths) == 0) MessageBox.Show(this, target ? "所选发票还没有整理后的文件。" : "原文件已不存在（可能已删除）。", Text);
        }

        void CopySelected()
        {
            var paths = SelectedInvoices().Select(i => TargetOf(i) ?? i.Path).ToArray();
            try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); lblStatus.Text = "已复制 " + paths.Length + " 个文件地址"; }
            catch { MessageBox.Show(this, "复制失败，剪贴板被其他程序占用。", Text); }
        }

        void RefreshPreview()
        {
            if (lastResult.Count == 0) Fill(null);
        }

        void ToggleAll()
        {
            bool v = invoices.Any(i => i.Ok && !i.Checked);
            foreach (var i in invoices) i.Checked = v && i.Ok;
            Fill(lastResult.Count > 0 ? lastResult.ToDictionary(r => r.Invoice) : null);
        }

        void UpdateStatus()
        {
            int ok = invoices.Count(i => i.Ok), sel = invoices.Count(i => i.Checked && i.Ok);
            string s = "共 " + invoices.Count + " 个 PDF，可识别 " + ok + " 张，已选 " + sel + " 张";
            int bad = invoices.Count - ok;
            if (bad > 0) s += "（" + bad + " 个无法识别，不参与整理）";
            if (lastResult.Count > 0)
            {
                int v = lastResult.Count(r => r.Verified), del = lastResult.Count(r => r.SourceDeleted);
                s = "已整理 " + v + " 张" + (del > 0 ? "，已删除原文件 " + del + " 个" : "") + "；" + s;
            }
            lblStatus.Text = s;
        }

        void SetBusy(bool b, string msg)
        {
            busy = b;
            btnScan.Enabled = btnRun.Enabled = !b;
            if (b) btnDelete.Enabled = false;
            bar.Visible = b;
            if (msg != null) lblStatus.Text = msg;
            Cursor = b ? Cursors.WaitCursor : Cursors.Default;
        }

        async void RunOrganize()
        {
            var sel = invoices.Where(i => i.Checked && i.Ok).ToList();
            if (sel.Count == 0) { MessageBox.Show(this, "请先勾选要整理的发票。", Text); return; }
            string dst = txtDst.Text.Trim();
            if (dst.Length == 0) { MessageBox.Show(this, "请先设置「整理到」目录。", Text); return; }
            if (MessageBox.Show(this, "将把 " + sel.Count + " 张发票按抬头分类【复制】到：\n" + dst + "\n\n原文件不会被改动。是否继续？", Text,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            SetBusy(true, "正在整理…");
            List<OrganizeItem> res = null;
            try
            {
                res = await Task.Run(() => { var r = Organizer.Organize(sel, dst); Organizer.WriteIndex(r, DateTime.Now); return r; });
                Changed = true;
            }
            catch (Exception ex) { MessageBox.Show(this, "整理失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { SetBusy(false, null); }
            if (res == null) return;
            lastResult = res;
            Fill(res.ToDictionary(r => r.Invoice));
            int ok = res.Count(r => r.Verified), failed = res.Count - ok;
            btnDelete.Enabled = res.Any(r => r.Verified && !r.SourceDeleted && !r.Status.StartsWith("已在归档位置"));
            string msg = "整理完成：" + ok + " 张已归档（其中 " + res.Count(r => r.Status == "已存在相同文件") + " 张此前已归档）" + (failed > 0 ? "，" + failed + " 张失败" : "") + "。\n\n" +
                         "原始文件仍保留在原处。如需删除，请核对整理结果后点击「删除原始文件…」。\n\n是否打开整理目录？";
            if (MessageBox.Show(this, msg, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) OpenDir(dst);
        }

        void DeleteOriginals()
        {
            var cand = lastResult.Where(r => r.Verified && !r.SourceDeleted && File.Exists(r.Source) &&
                                             !string.Equals(Path.GetFullPath(r.Source), Path.GetFullPath(r.Target), StringComparison.OrdinalIgnoreCase)).ToList();
            if (cand.Count == 0) { MessageBox.Show(this, "没有可删除的原始文件。", Text); return; }
            var preview = string.Join("\n", cand.Take(8).Select(r => "  " + r.Source));
            if (cand.Count > 8) preview += "\n  ……等共 " + cand.Count + " 个";
            string msg = "即将删除以下 " + cand.Count + " 个原始发票文件：\n\n" + preview +
                         "\n\n这些文件均已核对：整理目录中存在内容完全一致的副本。\n文件将移入【回收站】，必要时可从回收站恢复。\n\n确定删除吗？";
            if (MessageBox.Show(this, msg, "确认删除原始文件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            int n;
            Cursor = Cursors.WaitCursor;
            try { n = Organizer.DeleteSources(cand); }
            finally { Cursor = Cursors.Default; }
            Changed = true;
            btnDelete.Enabled = lastResult.Any(r => r.Verified && !r.SourceDeleted && File.Exists(r.Source));
            Fill(lastResult.ToDictionary(r => r.Invoice));
            MessageBox.Show(this, "已将 " + n + " 个原始文件移入回收站。" + (n < cand.Count ? "\n" + (cand.Count - n) + " 个未删除，详见列表状态。" : ""), Text);
        }
    }
}
