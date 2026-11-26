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
        readonly InvoiceHistory history;
        readonly List<Invoice> all = new List<Invoice>();
        readonly List<Invoice> shown = new List<Invoice>();
        bool suppress;
        int sortCol = 3; bool sortAsc = true;

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

        public MainForm() : this(InvoiceHistory.Default) { }

        public MainForm(InvoiceHistory history)
        {
            this.history = history ?? InvoiceHistory.Default;
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

            // 功能入口：整理发票 / 下载发票 / 导入导出记录（独立窗口）
            var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2) };
            var tbOrganize = new ToolStripButton("整理发票") { ToolTipText = "按 抬头_税号 分类整理发票目录中的 PDF" };
            var tbDownload = new ToolStripButton("下载发票") { ToolTipText = "通过链接 / 二维码截图下载发票，并自动整理" };
            var tbHistory = new ToolStripButton("导入导出记录") { ToolTipText = "查看最近 14 个月的发票导入与导出记录" };
            tbOrganize.Click += (s, e) => OpenTool(new OrganizeForm(cfg, txtDir.Text.Trim(), history));
            tbDownload.Click += (s, e) => OpenTool(new DownloadForm(cfg, history));
            tbHistory.Click += (s, e) => { using (var hf = new HistoryForm(history)) hf.ShowDialog(this); };
            tools.Items.Add(new ToolStripLabel("凑金额导出（本页）"));
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(tbOrganize);
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(tbDownload);
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(tbHistory);
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
            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, CheckBoxes = true, HideSelection = false, Scrollable = true, ShowItemToolTips = true, AllowColumnReorder = true };
            lv.Columns.Add(new ColumnHeader { Name = "chk", Text = "", Width = 24 });
            lv.Columns.Add(new ColumnHeader { Name = "number", Text = "发票号", Width = 165 });
            lv.Columns.Add(new ColumnHeader { Name = "amount", Text = "发票金额", Width = 90, TextAlign = HorizontalAlignment.Right });
            lv.Columns.Add(new ColumnHeader { Name = "date", Text = "开票日期", Width = 85 });
            lv.Columns.Add(new ColumnHeader { Name = "buyer", Text = "公司抬头(购买方)", Width = 210 });
            lv.Columns.Add(new ColumnHeader { Name = "tax", Text = "税号", Width = 150 });
            lv.Columns.Add(new ColumnHeader { Name = "dir", Text = "所在目录", Width = 180 });
            lv.Columns.Add(new ColumnHeader { Name = "item", Text = "项目名称", Width = 180 });
            lv.Columns.Add(new ColumnHeader { Name = "file", Text = "文件名 / 状态", Width = 280 });
            ApplyColumnOrder(lv, cfg.MainColOrder);
            lv.ColumnReordered += (s, e) =>
            {
                if (e.OldDisplayIndex == 0 || e.NewDisplayIndex == 0) { e.Cancel = true; return; }
                BeginInvoke((Action)(() => { cfg.MainColOrder = GetColumnOrder(lv); }));
            };
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
            if (lv != null && lv.Columns.Count > 1) cfg.MainColOrder = GetColumnOrder(lv);
        }

        /// <summary>获取列表当前列显示顺序（不含第 0 勾选列），用逗号分隔列名。</summary>
        public static string GetColumnOrder(ListView listView)
        {
            return string.Join(",", listView.Columns.Cast<ColumnHeader>()
                .Where(c => c.Index > 0)
                .OrderBy(c => c.DisplayIndex)
                .Select(c => c.Name));
        }

        /// <summary>按保存的列名顺序恢复 ListView 列显示顺序（第 0 勾选列始终固定在首位）。</summary>
        public static void ApplyColumnOrder(ListView listView, string order)
        {
            if (listView == null || listView.Columns.Count <= 1 || string.IsNullOrWhiteSpace(order)) return;
            var byName = new Dictionary<string, ColumnHeader>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < listView.Columns.Count; i++)
                if (!string.IsNullOrEmpty(listView.Columns[i].Name)) byName[listView.Columns[i].Name] = listView.Columns[i];
            var ordered = new List<ColumnHeader>();
            var used = new HashSet<ColumnHeader>();
            foreach (var raw in order.Split(','))
            {
                string k = raw.Trim();
                ColumnHeader col;
                if (k.Length > 0 && byName.TryGetValue(k, out col) && used.Add(col)) ordered.Add(col);
            }
            for (int i = 1; i < listView.Columns.Count; i++)
                if (used.Add(listView.Columns[i])) ordered.Add(listView.Columns[i]);
            listView.Columns[0].DisplayIndex = 0;
            for (int i = 0; i < ordered.Count; i++) ordered[i].DisplayIndex = i + 1;
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
            history.ReloadIfChanged();
            suppress = true;
            lv.BeginUpdate();
            lv.Items.Clear();
            shown.Clear();
            var rows = new List<Invoice>(all.Where(i => Pass(i)));
            rows.Sort(Compare);
            var numCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in all)
            {
                if (!i.Ok || string.IsNullOrWhiteSpace(i.Number)) continue;
                string k = i.Number.Trim();
                int cnt; numCounts.TryGetValue(k, out cnt); numCounts[k] = cnt + 1;
            }
            // 无法识别的放最后（灰色）
            var bad = all.Where(i => !i.Ok).OrderBy(i => i.FileName).ToList();
            foreach (var i in rows)
            {
                var expRec = history.FindExport(i);
                int cnt = 0;
                if (!string.IsNullOrWhiteSpace(i.Number)) numCounts.TryGetValue(i.Number.Trim(), out cnt);
                shown.Add(i);
                lv.Items.Add(MakeItem(i, expRec, cnt > 1));
            }
            foreach (var i in bad) lv.Items.Add(MakeItem(i, null, false));
            lv.EndUpdate();
            suppress = false;
            UpdateStatus();
        }

        ListViewItem MakeItem(Invoice i, HistoryRecord expRec, bool dupInList)
        {
            var it = new ListViewItem("");
            it.Tag = i;
            string dir = "";
            try { dir = Path.GetDirectoryName(i.Path) ?? ""; } catch { }
            if (i.Ok)
            {
                it.SubItems.Add(i.Number);
                it.SubItems.Add(i.Amount.ToString("F2", CultureInfo.InvariantCulture));
                it.SubItems.Add(i.Date.HasValue ? i.Date.Value.ToString("yyyy-MM-dd") : "");
                it.SubItems.Add(i.BuyerName);
                it.SubItems.Add(i.BuyerTaxId);
                it.SubItems.Add(dir);
                it.SubItems.Add(i.Item);
                string fileStatus = i.FileName;
                if (expRec != null) { fileStatus += " 【已导出 " + expRec.Time.ToString("yyyy-MM-dd") + "】"; it.ForeColor = Color.Firebrick; }
                else if (dupInList) { fileStatus += " 【发票号重复】"; it.ForeColor = Color.DarkOrange; }
                it.SubItems.Add(fileStatus);
                string tip = i.Summary();
                if (expRec != null) tip += "\n[拦截提示] 该发票已于 " + expRec.Time.ToString("yyyy-MM-dd HH:mm") + " 导出过" + (expRec.Note.Length > 0 ? "（" + expRec.Note + "）" : "") + "，不可重复导出。";
                else if (dupInList) tip += "\n[提示] 列表中存在相同发票号的重复文件。";
                it.ToolTipText = tip;
                it.Checked = i.Checked;
            }
            else
            {
                it.SubItems.Add(""); it.SubItems.Add(""); it.SubItems.Add(""); it.SubItems.Add("（无法识别）");
                it.SubItems.Add(""); it.SubItems.Add(dir); it.SubItems.Add("");
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
                case 1: c = string.CompareOrdinal(a.Number, b.Number); break;
                case 2: c = a.Amount.CompareTo(b.Amount); break;
                case 3: c = Nullable.Compare(a.Date, b.Date); break;
                case 4: c = string.Compare(a.BuyerName, b.BuyerName, StringComparison.CurrentCulture); break;
                case 5: c = string.CompareOrdinal(a.BuyerTaxId, b.BuyerTaxId); break;
                case 6: c = string.Compare(Path.GetDirectoryName(a.Path) ?? "", Path.GetDirectoryName(b.Path) ?? "", StringComparison.CurrentCultureIgnoreCase); break;
                case 7: c = string.Compare(a.Item, b.Item, StringComparison.CurrentCulture); break;
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
            history.ReloadIfChanged();
            var seenNums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            suppress = true;
            foreach (ListViewItem it in lv.Items)
            {
                var inv = (Invoice)it.Tag;
                if (!inv.Ok) continue;
                bool check = v;
                if (v)
                {
                    if (history.FindExport(inv) != null) check = false;
                    else if (!string.IsNullOrWhiteSpace(inv.Number) && !seenNums.Add(inv.Number.Trim())) check = false;
                }
                inv.Checked = check; it.Checked = check;
            }
            suppress = false;
            UpdateStatus();
        }

        /// <summary>过滤出可用于凑金额匹配的候选发票：剔除已导出记录，并对同发票号去重。</summary>
        public static List<Invoice> FilterMatchCandidates(IList<Invoice> source, InvoiceHistory hist, out int skippedExported, out int skippedDup)
        {
            skippedExported = 0; skippedDup = 0;
            var result = new List<Invoice>();
            var seenNums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var inv in source)
            {
                if (!inv.Ok) continue;
                string hash = InvoiceHistory.FallbackHash(inv);
                if (hist != null && hist.FindExport(inv, hash) != null) { skippedExported++; continue; }
                string num = string.IsNullOrWhiteSpace(inv.Number) ? "" : inv.Number.Trim();
                if ((num.Length > 0 && !seenNums.Add(num)) || (hash.Length > 0 && !seenHashes.Add(hash)))
                { skippedDup++; continue; }
                result.Add(inv);
            }
            return result;
        }

        // ---------- 匹配 ----------
        void MatchNow()
        {
            long target;
            if (!TryTarget(out target)) { MessageBox.Show(this, "请输入大于 0 的目标金额，例如 1234.56", Text); txtTarget.Focus(); return; }
            if (shown.Count == 0) { MessageBox.Show(this, "当前列表没有可选发票。", Text); return; }
            history.ReloadIfChanged();
            int skippedExp, skippedDup;
            var candidates = FilterMatchCandidates(shown, history, out skippedExp, out skippedDup);
            if (candidates.Count == 0)
            {
                MessageBox.Show(this, "当前列表中没有未导出的可用发票（" + skippedExp + " 张已有导出记录）。", Text);
                return;
            }
            Cursor = Cursors.WaitCursor;
            List<int> pick;
            try { pick = Matcher.Find(candidates.Select(i => i.Cents).ToList(), target); }
            finally { Cursor = Cursors.Default; }
            var set = new HashSet<Invoice>(pick.Select(i => candidates[i]));
            suppress = true;
            foreach (ListViewItem it in lv.Items)
            {
                var inv = (Invoice)it.Tag;
                if (!inv.Ok) continue;
                inv.Checked = set.Contains(inv); it.Checked = inv.Checked;
            }
            suppress = false;
            UpdateStatus();
            if (skippedExp > 0 || skippedDup > 0)
            {
                var notes = new List<string>();
                if (skippedExp > 0) notes.Add("已排除 " + skippedExp + " 张已导出发票");
                if (skippedDup > 0) notes.Add("去重 " + skippedDup + " 张重复发票号");
                lblStatus.Text += "    （" + string.Join("，", notes) + "）";
            }
            if (pick.Count == 0)
                MessageBox.Show(this, "当前列表中可用发票合计 " + (candidates.Where(i => i.Cents > 0).Sum(i => i.Cents) / 100m).ToString("F2", CultureInfo.InvariantCulture) + " 元，不足目标金额 " + (target / 100m).ToString("F2", CultureInfo.InvariantCulture) + " 元，无法凑够。", Text);
        }

        // ---------- 导出 ----------
        void DoExport()
        {
            var sel = Selected();
            if (sel.Count == 0) { MessageBox.Show(this, "请先勾选要导出的发票。", Text); return; }
            string outDir = txtOut.Text.Trim();
            if (outDir.Length == 0) { MessageBox.Show(this, "请先设置导出目录。", Text); return; }

            history.ReloadIfChanged();
            // 1. 拦截已有导出记录的发票
            var exportedConflicts = new List<string>();
            var exportedInvs = new HashSet<Invoice>();
            foreach (var inv in sel)
            {
                var rec = history.FindExport(inv);
                if (rec != null)
                {
                    exportedInvs.Add(inv);
                    exportedConflicts.Add("  · " + (inv.Number.Length > 0 ? inv.Number : inv.FileName) + "（金额 " + inv.Amount.ToString("F2", CultureInfo.InvariantCulture) + "，已于 " + rec.Time.ToString("yyyy-MM-dd") + " 导出）");
                }
            }
            if (exportedConflicts.Count > 0)
            {
                // 自动取消勾选冲突发票，避免误导出
                suppress = true;
                foreach (ListViewItem it in lv.Items)
                {
                    var inv = it.Tag as Invoice;
                    if (inv != null && exportedInvs.Contains(inv)) { inv.Checked = false; it.Checked = false; }
                }
                suppress = false;
                UpdateStatus();
                string preview = string.Join("\n", exportedConflicts.Take(10)) + (exportedConflicts.Count > 10 ? "\n  ……等共 " + exportedConflicts.Count + " 张" : "");
                MessageBox.Show(this, "以下发票在导出记录中已存在，已拦截并取消勾选，避免重复导出：\n\n" + preview, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 2. 拦截本次勾选中的重复发票号
            var dupGroups = sel.Select(i => new { Invoice = i, Key = string.IsNullOrWhiteSpace(i.Number)
                                                   ? InvoiceHistory.FallbackHash(i) : i.Number.Trim() })
                               .Where(i => i.Key.Length > 0)
                               .GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
                               .Where(g => g.Count() > 1).ToList();
            if (dupGroups.Count > 0)
            {
                string preview = string.Join("\n", dupGroups.Take(10).Select(g => "  · " +
                    (string.IsNullOrWhiteSpace(g.First().Invoice.Number) ? g.First().Invoice.FileName : "发票号 " + g.Key) +
                    "（勾选了 " + g.Count() + " 份）"));
                MessageBox.Show(this, "所选发票中存在重复项，请取消重复项后再导出：\n\n" + preview, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            decimal sum = sel.Sum(i => i.Amount);
            if (MessageBox.Show(this, "将把 " + sel.Count + " 张发票（合计 " + sum.ToString("F2", CultureInfo.InvariantCulture) + " 元）移动到导出目录，是否继续？",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try
            {
                Directory.CreateDirectory(outDir);
                DateTime now = DateTime.Now;
                var originalPaths = sel.ToDictionary(i => i, i => i.Path);
                string dest;
                lock (history.SyncRoot)
                {
                    history.ReloadIfChanged();
                    if (sel.Any(i => history.FindExport(i) != null))
                        throw new InvalidOperationException("所选发票已有导出记录，请重新选择。");
                    try { dest = Exporter.Export(sel, outDir, now); }
                    finally
                    {
                        var moved = sel.Where(i => i.Path != originalPaths[i]).ToList();
                        if (moved.Count > 0)
                            history.RecordExport(moved, Path.GetFileName(Path.GetDirectoryName(moved[0].Path)), now);
                    }
                }
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










