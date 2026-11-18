using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;

namespace PiaoMate
{
    public class MainForm : Form
    {

        readonly Settings cfg;
        readonly List<Invoice> all = new List<Invoice>();
        readonly List<Invoice> shown = new List<Invoice>();
        bool suppress;
        int sortCol = 1; bool sortAsc = true;

        Label lblCName, lblCTax;
        Action fitFilter;
        bool prevCustom;
        TextBox txtDir, txtOut, txtTarget, txtCName, txtCTax;
        CheckBox chkRecurse, chkDate;
        DateTimePicker dtFrom, dtTo;
        ComboBox cmbCompany;
        ListView lv;
        Label lblStatus;
        Button btnScan, btnMatch, btnExport;
        ProgressBar bar;
        CancellationTokenSource scanCts;

        public MainForm()
        {
            cfg = Settings.Load();
            Text = "票小秘 v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + (Settings.IsInternalEdition ? "（内部版）" : "");
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9f);
            var wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(cfg.WinW, wa.Width - 40), Math.Min(cfg.WinH, wa.Height - 80));
            MinimumSize = new Size(480, 420);
            StartPosition = FormStartPosition.CenterScreen;
            BuildUi();
            LoadToUi();
            Shown += (s, e) => { if (Directory.Exists(txtDir.Text)) Scan(); };
            FormClosing += (s, e) => { SaveFromUi(); if (WindowState == FormWindowState.Normal) { cfg.WinW = ClientSize.Width; cfg.WinH = ClientSize.Height; } cfg.Save(); };
        }

        // ---------- UI ----------
        Label L(string t) { return new Label { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }; }

        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8), AutoScroll = true };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // 功能入口：整理发票 / 下载发票（独立窗口）
            var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2) };
            var tbOrganize = new ToolStripButton("整理发票") { ToolTipText = "按 抬头_税号 分类整理发票目录中的 PDF" };
            var tbDownload = new ToolStripButton("下载发票") { ToolTipText = "通过链接 / 二维码截图下载发票，并自动整理" };
            tbOrganize.Click += (s, e) => OpenTool(new OrganizeForm(cfg, txtDir.Text.Trim()));
            tbDownload.Click += (s, e) => OpenTool(new DownloadForm(cfg));
            tools.Items.Add(new ToolStripLabel("凑金额导出（本页）"));
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(tbOrganize);
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(tbDownload);
            var tbLog = new ToolStripButton("更新日志") { Alignment = ToolStripItemAlignment.Right, ToolTipText = "查看各版本的改动记录" };
            tbLog.Click += (s, e) => ChangelogForm.ShowLog(this);
            tools.Items.Add(tbLog);
            var tbDisclaimer = new ToolStripButton("免责声明") { Alignment = ToolStripItemAlignment.Right, ToolTipText = "查看免责声明" };
            tbDisclaimer.Click += (s, e) => DisclaimerForm.Show(this, false);
            tools.Items.Add(tbDisclaimer);
            Controls.Add(tools);

            // 目录
            var g1 = new GroupBox { Text = "目录", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            var t1 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2, AutoSize = true };
            t1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            txtDir = new TextBox { Dock = DockStyle.Fill };
            txtOut = new TextBox { Dock = DockStyle.Fill };
            var b1 = new Button { Text = "浏览…", AutoSize = true };
            var b2 = new Button { Text = "浏览…", AutoSize = true };
            btnScan = new Button { Text = "扫描/刷新", AutoSize = true };
            chkRecurse = new CheckBox { Text = "含子文件夹", AutoSize = true, Anchor = AnchorStyles.Left };
            b1.Click += (s, e) => Browse(txtDir);
            b2.Click += (s, e) => Browse(txtOut);
            btnScan.Click += (s, e) => Scan();
            t1.Controls.Add(L("发票目录："), 0, 0); t1.Controls.Add(txtDir, 1, 0); t1.Controls.Add(b1, 2, 0); t1.Controls.Add(btnScan, 3, 0); t1.Controls.Add(chkRecurse, 4, 0);
            t1.Controls.Add(L("导出目录："), 0, 1); t1.Controls.Add(txtOut, 1, 1); t1.Controls.Add(b2, 2, 1);
            g1.Controls.Add(t1);
            root.Controls.Add(g1, 0, 0);

            // 筛选
            var g2 = new GroupBox { Text = "筛选", Dock = DockStyle.Fill, Height = 80 };
            var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, WrapContents = true, Height = 32 };
            var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, WrapContents = true, Height = 32 };
            chkDate = new CheckBox { Text = "开票日期", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            dtFrom = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 105 };
            dtTo = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 105 };
            cmbCompany = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270 };
            cmbCompany.Items.Add("不筛选（全部）");
            foreach (var c in cfg.Companies) cmbCompany.Items.Add(c.Key);
            cmbCompany.Items.Add("自定义…");
            txtCName = new TextBox { Width = 220 };
            txtCTax = new TextBox { Width = 170 };
            row1.Controls.Add(chkDate); row1.Controls.Add(dtFrom); row1.Controls.Add(L("至")); row1.Controls.Add(dtTo);
            row2.Controls.Add(L("公司抬头："));  row2.Controls.Add(cmbCompany);
            lblCName = L("公司名称："); lblCTax = L("税号：");
            row2.Controls.Add(lblCName); row2.Controls.Add(txtCName);
            row2.Controls.Add(lblCTax); row2.Controls.Add(txtCTax);
            g2.Controls.Add(row2); g2.Controls.Add(row1);
            fitFilter = () =>
            {
                int w = Math.Max(100, g2.ClientSize.Width - 8);
                row1.Height = row1.GetPreferredSize(new Size(w, 0)).Height;
                row2.Height = row2.GetPreferredSize(new Size(w, 0)).Height;
                g2.Height = row1.Height + row2.Height + 28;
            };
            g2.Resize += (s, e) => fitFilter();
            root.Controls.Add(g2, 0, 1);
            EventHandler refilter = (s, e) => { if (!suppress) { UpdateCustomEnabled(); Refilter(); } };
            chkDate.CheckedChanged += refilter; dtFrom.ValueChanged += refilter; dtTo.ValueChanged += refilter;
            cmbCompany.SelectedIndexChanged += refilter; txtCName.TextChanged += refilter; txtCTax.TextChanged += refilter;

            // 凑金额
            var g3 = new GroupBox { Text = "凑金额", Dock = DockStyle.Fill, Height = 60 };
            var f3 = new FlowLayoutPanel { Dock = DockStyle.Fill };
            g3.Resize += (s, e) => g3.Height = f3.GetPreferredSize(new Size(Math.Max(100, g3.ClientSize.Width), 0)).Height + g3.Padding.Vertical + 24;
            txtTarget = new TextBox { Width = 110 };
                        btnMatch = new Button { Text = "匹配最接近的发票", AutoSize = true };
            var bAll = new Button { Text = "全选", AutoSize = true };
            var bNone = new Button { Text = "清空选择", AutoSize = true };
            f3.Controls.Add(L("目标总金额(元)：")); f3.Controls.Add(txtTarget); f3.Controls.Add(L("（所选发票合计不少于目标，且尽量接近）")); f3.Controls.Add(btnMatch); f3.Controls.Add(bAll); f3.Controls.Add(bNone);
            g3.Controls.Add(f3);
            root.Controls.Add(g3, 0, 2);
            btnMatch.Click += (s, e) => MatchNow();
            bAll.Click += (s, e) => SetAll(true);
            bNone.Click += (s, e) => SetAll(false);
            txtTarget.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { MatchNow(); e.SuppressKeyPress = true; } };

            // 列表
            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, CheckBoxes = true, HideSelection = false, Scrollable = true, ShowItemToolTips = true };
            lv.Columns.Add("", 24);
            lv.Columns.Add("开票日期", 85);
            lv.Columns.Add("公司抬头(购买方)", 230);
            lv.Columns.Add("税号", 150);
            lv.Columns.Add("项目名称", 200);
            lv.Columns.Add("价税合计", 90, HorizontalAlignment.Right);
            lv.Columns.Add("文件名 / 状态", 330);
            lv.ItemChecked += (s, e) => { if (suppress) return; var inv = e.Item.Tag as Invoice; if (inv != null) inv.Checked = e.Item.Checked; UpdateStatus(); };
            lv.ColumnClick += (s, e) => { if (e.Column == 0) return; if (sortCol == e.Column) sortAsc = !sortAsc; else { sortCol = e.Column; sortAsc = true; } Refilter(); };
            lv.DoubleClick += (s, e) => { if (lv.SelectedItems.Count > 0) { var inv = (Invoice)lv.SelectedItems[0].Tag; try { Process.Start(inv.Path); } catch { } } };
            var menu = new ContextMenuStrip();
            var miCopy = new ToolStripMenuItem("复制文件地址");
            miCopy.Click += (s, e) => CopyPaths();
            menu.Items.Add(miCopy);
            menu.Items.Add(new ToolStripMenuItem("打开所在目录并选中", null, (s, e) =>
            {
                var paths = lv.SelectedItems.Cast<ListViewItem>().Select(i => ((Invoice)i.Tag).Path);
                if (ShellHelper.ShowInFolder(paths) == 0) MessageBox.Show(this, "文件已不存在，请点「扫描/刷新」。", Text);
            }));
            menu.Opening += (s, e) => { if (lv.SelectedItems.Count == 0) e.Cancel = true; };
            lv.ContextMenuStrip = menu;
            root.Controls.Add(lv, 0, 3);

            // 底部
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lblStatus = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) };
            bar = new ProgressBar { Dock = DockStyle.Fill, Visible = false };
            btnExport = new Button { Text = "导出所选并打开目录", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
            btnExport.Click += (s, e) => DoExport();
            bottom.Controls.Add(lblStatus, 0, 0); bottom.Controls.Add(bar, 1, 0); bottom.Controls.Add(btnExport, 2, 0);
            root.Controls.Add(bottom, 0, 4);
        }

        void OpenTool(Form f)
        {
            SaveFromUi();
            using (f)
            {
                f.ShowDialog(this);
                cfg.Save();
                bool changed = (f is OrganizeForm && ((OrganizeForm)f).Changed) || (f is DownloadForm && ((DownloadForm)f).Changed);
                if (changed && Directory.Exists(txtDir.Text.Trim())) Scan();
            }
        }

        void CopyPaths()
        {
            var paths = lv.SelectedItems.Cast<ListViewItem>().Select(i => ((Invoice)i.Tag).Path).ToArray();
            if (paths.Length == 0) return;
            for (int k = 0; k < 5; k++)
            {
                try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); lblStatus.Text = "已复制 " + paths.Length + " 个文件地址"; return; }
                catch (System.Runtime.InteropServices.ExternalException) { System.Threading.Thread.Sleep(80); }
            }
            MessageBox.Show(this, "复制失败，剪贴板被其他程序占用。", Text);
        }

        void Browse(TextBox box)
        {
            string p = FolderPicker.Pick(this, box.Text.Trim(), box == txtDir ? "选择发票目录" : "选择导出目录");
            if (p == null) return;
            box.Text = p;
            if (box == txtDir) Scan();
        }

        void LoadToUi()
        {
            suppress = true;
            txtDir.Text = cfg.InvoiceDir; txtOut.Text = cfg.ExportDir; chkRecurse.Checked = cfg.Recurse;
            chkDate.Checked = cfg.DateEnabled; dtFrom.Value = cfg.DateFrom; dtTo.Value = cfg.DateTo;
            int ci = 0;
            if (cfg.CompanyChoice == "custom") ci = cfg.Companies.Count + 1;
            else for (int k = 0; k < cfg.Companies.Count; k++) if (cfg.Companies[k].Value == cfg.CompanyChoice) ci = k + 1;
            cmbCompany.SelectedIndex = ci;
            txtTarget.Text = cfg.Target;
            suppress = false;
            UpdateCustomEnabled();
        }

        void SaveFromUi()
        {
            cfg.InvoiceDir = txtDir.Text.Trim(); cfg.ExportDir = txtOut.Text.Trim(); cfg.Recurse = chkRecurse.Checked;
            cfg.DateEnabled = chkDate.Checked; cfg.DateFrom = dtFrom.Value.Date; cfg.DateTo = dtTo.Value.Date;
            int sc = cmbCompany.SelectedIndex;
            cfg.CompanyChoice = sc <= 0 ? "" : (sc > cfg.Companies.Count ? "custom" : cfg.Companies[sc - 1].Value); if (prevCustom) { cfg.CustomName = txtCName.Text.Trim(); cfg.CustomTaxId = txtCTax.Text.Trim(); }
            cfg.Target = txtTarget.Text.Trim();
        }

        /// <summary>公司名/税号框：自定义时可编辑；备选公司显示其名称税号(只读)；不筛选时为空(只读)。</summary>
        void UpdateCustomEnabled()
        {
            int idx = cmbCompany.SelectedIndex;
            bool custom = idx == cfg.Companies.Count + 1;
            bool was = suppress; suppress = true;
            if (prevCustom && !custom) { cfg.CustomName = txtCName.Text.Trim(); cfg.CustomTaxId = txtCTax.Text.Trim(); }
            if (custom) { if (!prevCustom) { txtCName.Text = cfg.CustomName; txtCTax.Text = cfg.CustomTaxId; } }
            else if (idx >= 1) { txtCName.Text = cfg.Companies[idx - 1].Key; txtCTax.Text = cfg.Companies[idx - 1].Value; }
            else { txtCName.Text = ""; txtCTax.Text = ""; }
            txtCName.ReadOnly = txtCTax.ReadOnly = !custom;
            prevCustom = custom;
            suppress = was;
        }

        // ---------- 扫描 ----------
        async void Scan()
        {
            string dir = txtDir.Text.Trim();
            if (!Directory.Exists(dir)) { MessageBox.Show(this, "请先选择有效的发票目录。", Text); return; }
            if (scanCts != null) scanCts.Cancel();
            var cts = scanCts = new CancellationTokenSource();
            bool recurse = chkRecurse.Checked;
            btnScan.Enabled = false; bar.Visible = true; bar.Value = 0; lblStatus.Text = "正在扫描…";
            var keep = new HashSet<string>(all.Where(i => i.Checked).Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            var results = new List<Invoice>();
            try
            {
                var files = await Task.Run(() => EnumeratePdfs(dir, recurse));
                bar.Maximum = Math.Max(1, files.Count);
                int done = 0;
                var bag = new System.Collections.Concurrent.ConcurrentBag<Invoice>();
                await Task.Run(() =>
                {
                    Parallel.ForEach(files, new ParallelOptions { CancellationToken = cts.Token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, f =>
                    {
                        bag.Add(InvoiceParser.Parse(f));
                        int d = Interlocked.Increment(ref done);
                        if (d % 5 == 0 || d == files.Count) BeginInvoke((Action)(() => { if (!cts.IsCancellationRequested) bar.Value = Math.Min(bar.Maximum, d); }));
                    });
                });
                results = bag.ToList();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { MessageBox.Show(this, "扫描失败：" + ex.Message, Text); }
            if (cts.IsCancellationRequested) return;
            foreach (var r in results) r.Checked = keep.Contains(r.Path);
            all.Clear(); all.AddRange(results);
            bar.Visible = false; btnScan.Enabled = true;
            Refilter();
            int bad = all.Count(i => !i.Ok);
            if (bad > 0) lblStatus.Text += "    （" + bad + " 个 PDF 无法识别，已灰色显示）";
        }

        internal static List<string> EnumeratePdfs(string dir, bool recurse)
        {
            var list = new List<string>();
            var opt = SearchOption.TopDirectoryOnly;
            if (recurse) opt = SearchOption.AllDirectories;
            try
            {
                if (!recurse) list.AddRange(Directory.GetFiles(dir, "*.pdf", opt));
                else
                {
                    var stack = new Stack<string>(); stack.Push(dir);
                    while (stack.Count > 0)
                    {
                        string d = stack.Pop();
                        try { list.AddRange(Directory.GetFiles(d, "*.pdf")); } catch { }
                        try { foreach (var s in Directory.GetDirectories(d)) stack.Push(s); } catch { }
                    }
                }
            }
            catch { }
            return list.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // ---------- 筛选 / 显示 ----------
        bool Pass(Invoice i)
        {
            if (!i.Ok) return false;
            if (chkDate.Checked)
            {
                if (!i.Date.HasValue) return false;
                if (i.Date.Value.Date < dtFrom.Value.Date || i.Date.Value.Date > dtTo.Value.Date) return false;
            }
            int m = cmbCompany.SelectedIndex;
            if (m >= 1 && m <= cfg.Companies.Count)
            {
                var c = cfg.Companies[m - 1];
                return string.Equals(i.BuyerTaxId, c.Value, StringComparison.OrdinalIgnoreCase) || i.BuyerName == c.Key;
            }
            if (m == cfg.Companies.Count + 1)
            {
                string n = txtCName.Text.Trim(), t = txtCTax.Text.Trim();
                if (n.Length == 0 && t.Length == 0) return true;
                bool okN = n.Length == 0 || i.BuyerName.Contains(n);
                bool okT = t.Length == 0 || string.Equals(i.BuyerTaxId, t, StringComparison.OrdinalIgnoreCase);
                return okN && okT;
            }
            return true;
        }

        void Refilter()
        {
            suppress = true;
            lv.BeginUpdate();
            lv.Items.Clear();
            shown.Clear();
            var rows = new List<Invoice>(all.Where(i => Pass(i)));
            rows.Sort(Compare);
            // 无法识别的放最后（灰色）
            var bad = all.Where(i => !i.Ok).OrderBy(i => i.FileName).ToList();
            foreach (var i in rows) { shown.Add(i); lv.Items.Add(MakeItem(i)); }
            foreach (var i in bad) lv.Items.Add(MakeItem(i));
            lv.EndUpdate();
            suppress = false;
            UpdateStatus();
        }

        ListViewItem MakeItem(Invoice i)
        {
            var it = new ListViewItem("");
            it.Tag = i;
            if (i.Ok)
            {
                it.SubItems.Add(i.Date.HasValue ? i.Date.Value.ToString("yyyy-MM-dd") : "");
                it.SubItems.Add(i.BuyerName); it.SubItems.Add(i.BuyerTaxId); it.SubItems.Add(i.Item);
                it.SubItems.Add(i.Amount.ToString("F2", CultureInfo.InvariantCulture));
                it.SubItems.Add(i.FileName);
                it.ToolTipText = i.Summary();
                it.Checked = i.Checked;
            }
            else
            {
                it.SubItems.Add(""); it.SubItems.Add("（无法识别）"); it.SubItems.Add(""); it.SubItems.Add(""); it.SubItems.Add("");
                it.SubItems.Add(i.FileName + " — " + i.Error);
                it.ForeColor = SystemColors.GrayText;
                it.ToolTipText = i.Summary();
            }
            return it;
        }

        int Compare(Invoice a, Invoice b)
        {
            int c;
            switch (sortCol)
            {
                case 1: c = Nullable.Compare(a.Date, b.Date); break;
                case 2: c = string.Compare(a.BuyerName, b.BuyerName, StringComparison.CurrentCulture); break;
                case 3: c = string.CompareOrdinal(a.BuyerTaxId, b.BuyerTaxId); break;
                case 4: c = string.Compare(a.Item, b.Item, StringComparison.CurrentCulture); break;
                case 5: c = a.Amount.CompareTo(b.Amount); break;
                default: c = string.Compare(a.FileName, b.FileName, StringComparison.CurrentCulture); break;
            }
            if (c == 0) c = string.Compare(a.FileName, b.FileName, StringComparison.CurrentCulture);
            return sortAsc ? c : -c;
        }

        List<Invoice> Selected() { return shown.Where(i => i.Checked).ToList(); }

        bool TryTarget(out long cents)
        {
            cents = 0;
            decimal d;
            string t = txtTarget.Text.Trim().Replace(",", "").Replace("，", "").Replace("¥", "").Replace("￥", "");
            if (!decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out d) || d <= 0) return false;
            cents = (long)Math.Round(d * 100m);
            return true;
        }

        void UpdateStatus()
        {
            var sel = Selected();
            decimal sum = sel.Sum(i => i.Amount);
            string s = "显示 " + shown.Count + " / 共 " + all.Count(i => i.Ok) + " 张；已选 " + sel.Count + " 张，合计 " + sum.ToString("F2", CultureInfo.InvariantCulture) + " 元";
            long t;
            if (TryTarget(out t))
            {
                decimal diff = sum - t / 100m;
                s += "；与目标相差 " + (diff >= 0 ? "+" : "") + diff.ToString("F2", CultureInfo.InvariantCulture);
            }
            lblStatus.Text = s;
        }

        void SetAll(bool v)
        {
            suppress = true;
            foreach (ListViewItem it in lv.Items) { var inv = (Invoice)it.Tag; if (inv.Ok) { inv.Checked = v; it.Checked = v; } }
            suppress = false;
            UpdateStatus();
        }

        // ---------- 匹配 ----------
        void MatchNow()
        {
            long target;
            if (!TryTarget(out target)) { MessageBox.Show(this, "请输入大于 0 的目标金额，例如 1234.56", Text); txtTarget.Focus(); return; }
            if (shown.Count == 0) { MessageBox.Show(this, "当前列表没有可选发票。", Text); return; }
            Cursor = Cursors.WaitCursor;
            List<int> pick;
            try { pick = Matcher.Find(shown.Select(i => i.Cents).ToList(), target); }
            finally { Cursor = Cursors.Default; }
            var set = new HashSet<Invoice>(pick.Select(i => shown[i]));
            suppress = true;
            foreach (ListViewItem it in lv.Items)
            {
                var inv = (Invoice)it.Tag;
                if (!inv.Ok) continue;
                inv.Checked = set.Contains(inv); it.Checked = inv.Checked;
            }
            suppress = false;
            UpdateStatus();
            if (pick.Count == 0)
                MessageBox.Show(this, "当前列表中全部发票合计 " + (shown.Where(i => i.Cents > 0).Sum(i => i.Cents) / 100m).ToString("F2", CultureInfo.InvariantCulture) + " 元，不足目标金额 " + (target / 100m).ToString("F2", CultureInfo.InvariantCulture) + " 元，无法凑够。", Text);
        }

        // ---------- 导出 ----------
        void DoExport()
        {
            var sel = Selected();
            if (sel.Count == 0) { MessageBox.Show(this, "请先勾选要导出的发票。", Text); return; }
            string outDir = txtOut.Text.Trim();
            if (outDir.Length == 0) { MessageBox.Show(this, "请先设置导出目录。", Text); return; }
            decimal sum = sel.Sum(i => i.Amount);
            if (MessageBox.Show(this, "将把 " + sel.Count + " 张发票（合计 " + sum.ToString("F2", CultureInfo.InvariantCulture) + " 元）移动到导出目录，是否继续？",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try
            {
                Directory.CreateDirectory(outDir);
                string dest = Exporter.Export(sel, outDir, DateTime.Now);
                foreach (var i in sel) all.Remove(i);
                SaveFromUi(); cfg.Save();
                Refilter();
                Process.Start("explorer.exe", "\"" + dest + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "导出失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Scan();
            }
        }
    }
}










