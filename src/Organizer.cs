using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PiaoMate
{
    /// <summary>一张发票的整理结果。</summary>
    public class OrganizeItem
    {
        public Invoice Invoice;
        public string Source, Target = "";
        /// <summary>已复制 / 目标已存在相同文件 / 拦截：… / 失败</summary>
        public string Status = "";
        /// <summary>归档目录中已有与源文件内容完全一致的副本（只有此时才允许删除原文件）。</summary>
        public bool Verified;
        /// <summary>因导入/导出记录冲突或发票号重复而被拦截。</summary>
        public bool Blocked;
        public bool SourceDeleted;
    }

    /// <summary>
    /// 发票整理：按「抬头_税号」分子文件夹，文件名为「金额_抬头_税号_发票号_开票日期.pdf」。
    /// 只复制，不改动原文件；原文件须由用户确认后另行调用 DeleteSources 删除。
    /// </summary>
    public static class Organizer
    {
        public const string UnknownFolder = "_未识别";

        public static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var bad = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new StringBuilder();
            foreach (char c in s.Trim()) sb.Append(bad.Contains(c) ? '_' : c);
            return sb.ToString().Trim(' ', '.');
        }

        /// <summary>分类文件夹名：抬头_税号（无税号时只用抬头）。</summary>
        public static string FolderName(Invoice inv)
        {
            string name = Sanitize(inv.BuyerName), tax = Sanitize(inv.BuyerTaxId);
            if (name.Length == 0) name = "未知抬头";
            return tax.Length > 0 ? name + "_" + tax : name;
        }

        /// <summary>整理后的文件名：金额_抬头_税号_发票号_开票日期(yyyyMMdd).pdf（无税号/发票号/日期时省略该项）</summary>
        public static string FileName(Invoice inv)
        {
            string num = Sanitize(inv.Number);
            string s = inv.Amount.ToString("F2", CultureInfo.InvariantCulture) + "_" + FolderName(inv);
            if (num.Length > 0) s += "_" + num;
            if (inv.Date.HasValue) s += "_" + inv.Date.Value.ToString("yyyyMMdd");
            return s + ".pdf";
        }

        /// <summary>预览目标相对路径（不考虑重名）。</summary>
        public static string RelativeTarget(Invoice inv)
        {
            return inv.Ok ? Path.Combine(FolderName(inv), FileName(inv)) : Path.Combine(UnknownFolder, Sanitize(inv.FileName));
        }

        public static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs));
        }

        /// <summary>
        /// 把发票复制到 archiveDir 下的分类子文件夹。includeUnknown 为 true 时，无法识别的 PDF 原名复制到 _未识别。
        /// 传入 history 时会按发票号（无发票号时按 SHA-256）拦截已导出或已导入的重复发票，并在成功后追加导入记录。
        /// </summary>
        public static List<OrganizeItem> Organize(IEnumerable<Invoice> invoices, string archiveDir, bool includeUnknown = false, InvoiceHistory history = null, string historyNote = "整理")
        {
            if (history == null) return OrganizeCore(invoices, archiveDir, includeUnknown, null, historyNote);
            lock (history.SyncRoot) return OrganizeCore(invoices, archiveDir, includeUnknown, history, historyNote);
        }

        static List<OrganizeItem> OrganizeCore(IEnumerable<Invoice> invoices, string archiveDir, bool includeUnknown, InvoiceHistory history, string historyNote)
        {
            Directory.CreateDirectory(archiveDir);
            if (history != null) history.ReloadIfChanged();
            var results = new List<OrganizeItem>();
            var seenNums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newlyImported = new List<Tuple<Invoice, string>>();
            foreach (var inv in invoices)
            {
                var it = new OrganizeItem { Invoice = inv, Source = inv.Path };
                results.Add(it);
                if (!inv.Ok && !includeUnknown) { it.Status = "跳过：无法识别"; continue; }
                try
                {
                    string rel = RelativeTarget(inv);
                    string dir = Path.Combine(archiveDir, Path.GetDirectoryName(rel));
                    string stem = Path.GetFileNameWithoutExtension(rel), ext = Path.GetExtension(rel);
                    if (ext.Length == 0) ext = ".pdf";
                    string srcHash = Hash(inv.Path);
                    string target = Path.Combine(dir, stem + ext);
                    string num = inv.Ok && !string.IsNullOrWhiteSpace(inv.Number) ? inv.Number.Trim() : "";

                    if (history != null)
                    {
                        var expRec = history.FindExport(inv, srcHash);
                        if (expRec != null)
                        {
                            it.Blocked = true;
                            it.Status = "拦截：已于 " + expRec.Time.ToString("yyyy-MM-dd") + " 导出过";
                            continue;
                        }
                        var impRec = history.FindImport(inv, srcHash);
                        if (impRec != null)
                        {
                            if (File.Exists(target) && Hash(target) == srcHash)
                            {
                                it.Target = target;
                                it.Verified = true;
                                it.Status = "已存在相同文件";
                                continue;
                            }
                            it.Blocked = true;
                            it.Status = "拦截：已于 " + impRec.Time.ToString("yyyy-MM-dd") + " 导入过";
                            continue;
                        }
                        if (num.Length > 0 ? seenNums.Contains(num) : seenHashes.Contains(srcHash))
                        {
                            if (File.Exists(target) && Hash(target) == srcHash)
                            {
                                it.Target = target;
                                it.Verified = true;
                                it.Status = "已存在相同文件";
                                continue;
                            }
                            it.Blocked = true;
                            it.Status = "拦截：本次列表中发票重复";
                            continue;
                        }
                    }

                    Directory.CreateDirectory(dir);
                    for (int k = 2; ; k++)
                    {
                        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(inv.Path), StringComparison.OrdinalIgnoreCase))
                        { it.Target = target; it.Status = "已在归档位置"; break; }
                        if (!File.Exists(target))
                        {
                            File.Copy(inv.Path, target);
                            it.Target = target;
                            it.Verified = Hash(target) == srcHash;
                            it.Status = it.Verified ? "已复制" : "失败：复制后校验不一致";
                            break;
                        }
                        if (Hash(target) == srcHash)
                        {
                            it.Target = target; it.Verified = true; it.Status = "已存在相同文件";
                            break;
                        }
                        if (history != null && num.Length > 0)
                        {
                            it.Blocked = true;
                            it.Status = "拦截：目标目录已存在同发票号文件";
                            break;
                        }
                        target = Path.Combine(dir, stem + "(" + k + ")" + ext);
                    }
                    if (it.Verified && history != null)
                    {
                        if (num.Length > 0) seenNums.Add(num);
                        else seenHashes.Add(srcHash);
                        newlyImported.Add(Tuple.Create(inv, srcHash));
                    }
                }
                catch (Exception ex) { it.Status = "失败：" + ex.Message; it.Verified = false; }
            }
            if (history != null && newlyImported.Count > 0)
                history.RecordImports(newlyImported, historyNote);
            return results;
        }

        /// <summary>在每个分类子文件夹写入/追加 清单.txt。</summary>
        public static void WriteIndex(IEnumerable<OrganizeItem> items, DateTime now)
        {
            foreach (var g in items.Where(i => i.Verified && i.Status.StartsWith("已复制")).GroupBy(i => Path.GetDirectoryName(i.Target), StringComparer.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                sb.AppendLine("整理时间：" + now.ToString("yyyy-MM-dd HH:mm:ss"));
                foreach (var i in g)
                    sb.AppendLine("  " + Path.GetFileName(i.Target) + "    发票号码：" + i.Invoice.Number + "    原文件：" + i.Source);
                sb.AppendLine();
                File.AppendAllText(Path.Combine(g.Key, "清单.txt"), sb.ToString(), new UTF8Encoding(true));
            }
        }

        /// <summary>
        /// 删除原始文件：仅删除已校验归档副本、且源文件内容未变化的项。delete 默认移入回收站。
        /// 返回删除数量。
        /// </summary>
        public static int DeleteSources(IEnumerable<OrganizeItem> items, Action<string> delete = null)
        {
            if (delete == null) delete = RecycleBin;
            int n = 0;
            foreach (var it in items)
            {
                if (!it.Verified || it.SourceDeleted || string.IsNullOrEmpty(it.Target)) continue;
                try
                {
                    if (string.Equals(Path.GetFullPath(it.Source), Path.GetFullPath(it.Target), StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(it.Source) || !File.Exists(it.Target)) continue;
                    if (Hash(it.Source) != Hash(it.Target)) { it.Status = "未删除：原文件与归档副本不一致"; continue; }
                    delete(it.Source);
                    it.SourceDeleted = true; it.Status += "，原文件已删除";
                    n++;
                }
                catch (Exception ex) { it.Status += "，删除原文件失败：" + ex.Message; }
            }
            return n;
        }

        public static void RecycleBin(string path)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
    }
}
