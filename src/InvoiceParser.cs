using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PiaoMate
{
    public class Invoice
    {
        public string Path, FileName, Number = "", BuyerName = "", BuyerTaxId = "", SellerName = "", Item = "";
        public DateTime? Date;
        public decimal Amount;
        public bool Ok;
        public string Error = "";
        public bool Checked;
        public long Cents { get { return (long)Math.Round(Amount * 100m); } }

        /// <summary>鼠标悬停时显示的概要信息。</summary>
        public string Summary()
        {
            var sb = new System.Text.StringBuilder();
            if (Ok)
            {
                sb.AppendLine("发票金额：" + Amount.ToString("F2", CultureInfo.InvariantCulture) + " 元");
                sb.AppendLine("抬头：" + BuyerName);
                sb.AppendLine("税号：" + (BuyerTaxId.Length > 0 ? BuyerTaxId : "（无）"));
                if (Number.Length > 0) sb.AppendLine("发票号码：" + Number);
                if (Date.HasValue) sb.AppendLine("开票日期：" + Date.Value.ToString("yyyy-MM-dd"));
                if (SellerName.Length > 0) sb.AppendLine("销售方：" + SellerName);
                if (Item.Length > 0) sb.AppendLine("项目：" + Item);
            }
            else sb.AppendLine("无法识别：" + Error);
            sb.Append("文件：" + Path);
            return sb.ToString();
        }
    }

    /// <summary>从电子发票 PDF 中提取开票信息（基于文字坐标，适配全电发票版式）。</summary>
    public static class InvoiceParser
    {
        static readonly Regex TaxRe = new Regex(@"^[0-9A-Z]{15,20}$");
        static readonly Regex MoneyRe = new Regex(@"^[¥￥]\s*(-?[\d,]+(\.\d+)?)$");
        static readonly Regex DateRe2 = new Regex(@"(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})");
        static readonly Regex DateRe = new Regex(@"(\d{4})\s*年\s*(\d{1,2})\s*月\s*(\d{1,2})\s*日");

        public static Invoice Parse(string path)
        {
            var inv = new Invoice { Path = path, FileName = System.IO.Path.GetFileName(path) };
            try
            {
                using (var doc = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true }))
                {
                    if (doc.NumberOfPages < 1) throw new Exception("PDF 没有页面");
                    Page page = doc.GetPage(1);
                    Fill(inv, page.GetWords().ToList(), page.Width);
                }
            }
            catch (Exception ex)
            {
                inv.Ok = false;
                inv.Error = ex.Message;
            }
            return inv;
        }

        // 部首补充区(2E80-2EFF)字符没有兼容分解，手工映射到对应常用汉字（按 Unicode 码位）
        static readonly Dictionary<char, char> Radicals = BuildRadicals();
        static Dictionary<char, char> BuildRadicals()
        {
            var d = new Dictionary<char, char>();
            Action<string, char> add = (cps, to) => { foreach (var h in cps.Split(',')) d[(char)Convert.ToInt32(h, 16)] = to; };
            add("2E85", '人'); add("2E96,2E97", '心'); add("2E98", '手'); add("2E9C", '日'); add("2E9D", '月'); add("2E9F", '母');
            add("2EA1,2EA2", '水'); add("2EA3", '火'); add("2EA7", '牛'); add("2EA8", '犬'); add("2EA9", '玉'); add("2EAB", '目');
            add("2EAE", '竹'); add("2EAF,2EB0", '丝'); add("2EB6,2EB7,2EB8", '羊'); add("2EB9", '老'); add("2EBC", '肉');
            add("2EC1", '虎'); add("2EC2", '衣'); add("2EC3,2EC4", '西'); add("2EC5", '见'); add("2EC6,2EC7", '角');
            add("2EC8", '言'); add("2EC9", '贝'); add("2ECA", '足'); add("2ECB", '车'); add("2ED0", '金');
            add("2ED1,2ED2,2ED3", '长'); add("2ED4", '门'); add("2ED7", '雨'); add("2ED8", '青'); add("2ED9", '韦');
            add("2EDA", '页'); add("2EDB", '风'); add("2EDC", '飞'); add("2EDD,2EDE,2EDF,2EE0", '食'); add("2EE1", '首');
            add("2EE2", '马'); add("2EE3", '骨'); add("2EE4", '鬼'); add("2EE5", '鱼'); add("2EE6", '鸟'); add("2EE7", '盐');
            add("2EE8", '麦'); add("2EE9", '黄'); add("2EEB,2EEC", '齐'); add("2EED,2EEE", '齿'); add("2EEF,2EF0", '龙');
            add("2EF1,2EF2,2EF3", '龟');
            return d;
        }

        /// <summary>把 PDF 中常见的康熙部首(2F00-2FDF)/部首补充(2E80-2EFF)字符（如 ⽉ ⽇ ⽣ ⽅ ⻨ ⻝）还原为常用汉字。</summary>
        public static string Clean(string s)
        {
            if (s == null) return s;
            System.Text.StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i], o = c;
                if (c >= '\u2F00' && c <= '\u2FDF') o = c.ToString().Normalize(System.Text.NormalizationForm.FormKC)[0];
                else if (c >= '\u2E80' && c <= '\u2EFF') { char m; if (Radicals.TryGetValue(c, out m)) o = m; }
                if (o != c && sb == null) { sb = new System.Text.StringBuilder(s.Length); sb.Append(s, 0, i); }
                if (sb != null) sb.Append(o);
            }
            return sb == null ? s : sb.ToString();
        }

        struct W { public string T; public double L, R, B; }

        static void Fill(Invoice inv, List<Word> words, double pageW)
        {
            var ws = words.Select(w => new W { T = Clean(w.Text.Trim()), L = w.BoundingBox.Left, R = w.BoundingBox.Right, B = w.BoundingBox.Bottom })
                          .Where(w => w.T.Length > 0).ToList();
            string all = string.Join("", ws.Select(w => w.T));
            double half = pageW / 2;

            // 发票号码：20 位数字
            var num = ws.FirstOrDefault(w => Regex.IsMatch(w.T, @"^\d{20}$"));
            inv.Number = num.T ?? "";

            var dm = DateRe.Match(all);
            if (!dm.Success) dm = DateRe2.Match(all);
            if (dm.Success)
            {
                try { inv.Date = new DateTime(int.Parse(dm.Groups[1].Value), int.Parse(dm.Groups[2].Value), int.Parse(dm.Groups[3].Value)); } catch { }
            }

            // 购买方(左半页) / 销售方(右半页)
            inv.BuyerName = NameIn(ws, 0, half);
            inv.SellerName = NameIn(ws, half, double.MaxValue);
            var ids = ws.Where(w => TaxRe.IsMatch(w.T) && !Regex.IsMatch(w.T, @"^\d{20}$")).ToList();
            var buyerId = ids.Where(w => w.L < half).OrderBy(w => w.L).FirstOrDefault();
            inv.BuyerTaxId = buyerId.T ?? "";

            // 项目名称：位于表头"项目名称"之下、"合 计"行之上、"单位"列左侧，以 * 开头的行，续行拼接
            inv.Item = ItemNames(ws);

            // 价税合计（小写）
            inv.Amount = TotalAmount(ws);
            if (ws.Count == 0) { inv.Ok = false; inv.Error = "PDF 中没有可提取的文字（可能是扫描件/图片）"; }
            else if (inv.Amount == 0) { inv.Ok = false; inv.Error = "未识别到价税合计金额（可能不是发票）"; }
            else inv.Ok = true;
        }

        static string NameIn(List<W> ws, double lo, double hi)
        {
            var label = ws.Where(w => w.T.StartsWith("名称") && w.L >= lo && w.L < hi).OrderByDescending(w => w.B).FirstOrDefault();
            if (label.T == null) return "";
            // 标签与名称可能连成一个词（"名称：某某公司"），也可能分开
            string inline = label.T.Substring(2).TrimStart('：', ':', ' ');
            var parts = ws.Where(w => Math.Abs(w.B - label.B) < 3 && w.L > label.L && w.L < hi && w.L >= lo && !w.T.StartsWith("名称"))
                          .OrderBy(w => w.L).Select(w => w.T);
            return inline + string.Concat(parts);
        }

        static string ItemNames(List<W> ws)
        {
            var head = ws.FirstOrDefault(w => w.T == "项目名称");
            if (head.T == null) return "";
            var colEnd = ws.Where(w => w.T == "单" && Math.Abs(w.B - head.B) < 3).OrderBy(w => w.L).Select(w => w.L).DefaultIfEmpty(190).First();
            var sum = ws.Where(w => (w.T == "合" || w.T == "合计") && w.B < head.B).OrderByDescending(w => w.B).FirstOrDefault();
            double floor = sum.T != null ? sum.B + 4 : 0;
            var rows = ws.Where(w => w.B < head.B - 3 && w.B > floor && w.R <= colEnd + 2 && w.L < colEnd)
                         .OrderByDescending(w => Math.Round(w.B)).ThenBy(w => w.L).ToList();
            var items = new List<string>();
            foreach (var w in rows)
            {
                if (w.T.StartsWith("*") || items.Count == 0) items.Add(w.T);
                else items[items.Count - 1] += w.T;
            }
            return string.Join("；", items.Distinct());
        }

        static decimal TotalAmount(List<W> ws)
        {
            var label = ws.FirstOrDefault(w => w.T.Contains("（小写）") || w.T.Contains("(小写)"));
            // 合并单独成词的 "¥" 与其右侧数字
            var merged = new List<W>();
            for (int i = 0; i < ws.Count; i++)
            {
                var w = ws[i];
                if ((w.T == "¥" || w.T == "￥") )
                {
                    var nx = ws.Where(x => x.T.Length > 0 && Regex.IsMatch(x.T, @"^-?[\d,]+(\.\d+)?$") && Math.Abs(x.B - w.B) < 2 && x.L >= w.R - 1 && x.L - w.R < 12).OrderBy(x => x.L).FirstOrDefault();
                    if (nx.T != null) { merged.Add(new W { T = "¥" + nx.T, L = w.L, R = nx.R, B = w.B }); continue; }
                }
                merged.Add(w);
            }
            ws = merged;
            var cands = new List<Tuple<double, decimal>>();
            foreach (var w in ws)
            {
                var m = MoneyRe.Match(w.T);
                if (!m.Success) continue;
                decimal v;
                if (!decimal.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out v)) continue;
                cands.Add(Tuple.Create(w.B, v));
                if (label.T != null && Math.Abs(w.B - label.B) < 4 && w.L >= label.L - 2) return v;
            }
            // 兜底：最底部的 ¥ 金额（价税合计行位于合计行之下）
            if (cands.Count > 0) return cands.OrderBy(c => c.Item1).Select(c => c.Item2).First();
            var tm = Regex.Match(string.Concat(ws.Select(w => w.T)), @"小写[）)]?[¥￥]?\s*(\d[\d,]*\.\d{1,2})");
            decimal tv;
            if (tm.Success && decimal.TryParse(tm.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out tv)) return tv;
            return 0;
        }
    }
}





