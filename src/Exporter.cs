using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PiaoMate
{
    public static class Exporter
    {
        /// <summary>导出文件名：在原文件名后追加金额（原名已含该金额则不重复）。</summary>
        public static string NewFileName(Invoice inv)
        {
            string stem = Path.GetFileNameWithoutExtension(inv.FileName), ext = Path.GetExtension(inv.FileName);
            string amt = inv.Amount.ToString("F2", CultureInfo.InvariantCulture);
            if (System.Text.RegularExpressions.Regex.IsMatch(stem, @"(?<![\d.])" + System.Text.RegularExpressions.Regex.Escape(amt) + @"(?!\d)")) return inv.FileName;
            return stem + "_" + amt + ext;
        }

        /// <summary>在 exportDir 下创建日期时间子文件夹，移动发票并写入清单.txt，返回子文件夹路径。</summary>
        public static string Export(IList<Invoice> invoices, string exportDir, DateTime now)
        {
            if (invoices.Count == 0) throw new InvalidOperationException("没有选中任何发票");
            string dest = Path.Combine(exportDir, now.ToString("yyyyMMdd_HHmmss"));
            for (int k = 1; Directory.Exists(dest); k++) dest = Path.Combine(exportDir, now.ToString("yyyyMMdd_HHmmss") + "_" + k);
            Directory.CreateDirectory(dest);

            var sb = new StringBuilder();
            sb.AppendLine("发票清单    导出时间：" + now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(new string('=', 60));
            decimal total = 0;
            int n = 0;
            foreach (var inv in invoices)
            {
                string newName = NewFileName(inv);
                string target = Path.Combine(dest, newName);
                for (int k = 1; File.Exists(target); k++)
                    target = Path.Combine(dest, Path.GetFileNameWithoutExtension(newName) + "(" + k + ")" + Path.GetExtension(newName));
                File.Move(inv.Path, target);
                inv.Path = target;
                n++;
                total += inv.Amount;
                sb.AppendLine(n + ". 文件名：" + Path.GetFileName(target));
                sb.AppendLine("   抬头：" + inv.BuyerName + "    税号：" + inv.BuyerTaxId + "    金额：" + inv.Amount.ToString("F2", CultureInfo.InvariantCulture) + " 元");
                sb.AppendLine();
            }
            sb.AppendLine(new string('=', 60));
            sb.AppendLine("共 " + n + " 张    总计金额：" + total.ToString("F2", CultureInfo.InvariantCulture) + " 元");
            File.WriteAllText(Path.Combine(dest, "清单.txt"), sb.ToString(), new UTF8Encoding(true));
            return dest;
        }
    }
}


