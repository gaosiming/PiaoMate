using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace PiaoMate
{
    /// <summary>查看发票导入/导出历史记录（最多保留 1 年零 2 个月）。</summary>
    public class HistoryForm : Form
    {
        readonly InvoiceHistory history;
        ComboBox cmbType;
        TextBox txtSearch;
        ListView lv;
        Label lblStatus;
        List<HistoryRecord> all = new List<HistoryRecord>();

        public HistoryForm(InvoiceHistory history)
        {
            this.history = history;
            Text = "导入导出记录";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(980, 560);
            MinimumSize = new Size(640, 380);
            StartPosition = FormStartPosition.CenterParent;
            ShowIcon = false;
            BuildUi();
            Reload();
        }

        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            cmbType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            cmbType.Items.AddRange(new object[] { "全部", "导出", "导入/整理" });
            cmbType.SelectedIndex = 0;
            cmbType.SelectedIndexChanged += (s, e) => RefreshList();
            txtSearch = new TextBox { Width = 260 };
            txtSearch.TextChanged += (s, e) => RefreshList();
            var btnRefresh = new Button { Text = "刷新", AutoSize = true };
            btnRefresh.Click += (s, e) => Reload();
            top.Controls.Add(new Label { Text = "类型：", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
            top.Controls.Add(cmbType);
            top.Controls.Add(new Label { Text = "搜索（发票号/抬头/税号/说明）：", AutoSize = true, Margin = new Padding(10, 6, 3, 3) });
            top.Controls.Add(txtSearch);
            top.Controls.Add(btnRefresh);
            root.Controls.Add(top, 0, 0);

            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
            lv.Columns.Add("记录时间", 135);
            lv.Columns.Add("类型", 65);
            lv.Columns.Add("发票号", 160);
            lv.Columns.Add("发票金额", 85, HorizontalAlignment.Right);
            lv.Columns.Add("开票日期", 85);
            lv.Columns.Add("公司抬头", 200);
            lv.Columns.Add("税号", 145);
            lv.Columns.Add("说明", 180);
            var menu = new ContextMenuStrip();
            menu.Items.Add("复制发票号", null, (s, e) =>
            {
                var nums = lv.SelectedItems.Cast<ListViewItem>().Select(i => i.SubItems[2].Text).Where(x => x.Length > 0).ToArray();
                if (nums.Length > 0) try { Clipboard.SetText(string.Join(Environment.NewLine, nums)); } catch { }
            });
            menu.Opening += (s, e) => { if (lv.SelectedItems.Count == 0) e.Cancel = true; };
            lv.ContextMenuStrip = menu;
            root.Controls.Add(lv, 0, 1);

            lblStatus = new Label { AutoSize = true, Margin = new Padding(3, 6, 3, 3), ForeColor = SystemColors.GrayText };
            root.Controls.Add(lblStatus, 0, 2);
        }

        void Reload()
        {
            history.ReloadIfChanged();
            all = history.GetAll();
            RefreshList();
        }

        void RefreshList()
        {
            string kw = txtSearch.Text.Trim();
            int mode = cmbType.SelectedIndex;
            var filtered = all.Where(r =>
            {
                if (mode == 1 && !r.IsExport) return false;
                if (mode == 2 && !r.IsImport) return false;
                if (kw.Length == 0) return true;
                return r.Number.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       r.BuyerName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       r.BuyerTaxId.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       r.Note.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;
            }).ToList();

            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var r in filtered)
            {
                var it = new ListViewItem(r.Time.ToString("yyyy-MM-dd HH:mm:ss"));
                it.SubItems.Add(r.IsExport ? "导出" : "导入");
                it.SubItems.Add(r.Number);
                it.SubItems.Add(r.Amount.ToString("F2", CultureInfo.InvariantCulture));
                it.SubItems.Add(r.InvoiceDate);
                it.SubItems.Add(r.BuyerName);
                it.SubItems.Add(r.BuyerTaxId);
                it.SubItems.Add(r.Note);
                if (r.IsExport) it.ForeColor = Color.DarkBlue;
                lv.Items.Add(it);
            }
            lv.EndUpdate();
            lblStatus.Text = "显示 " + filtered.Count + " / 共 " + all.Count + " 条记录（自动保留最近 " + InvoiceHistory.RetentionMonths + " 个月）    存储位置：" + history.FilePath;
        }
    }
}
