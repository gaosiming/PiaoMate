using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PiaoMate
{
    /// <summary>单条发票导入或导出记录。</summary>
    public class HistoryRecord
    {
        public string Action = "";
        public DateTime Time;
        public string Number = "";
        public decimal Amount;
        public string InvoiceDate = "";
        public string BuyerTaxId = "";
        public string BuyerName = "";
        public string Note = "";
        public string Sha256 = "";

        public bool IsExport { get { return string.Equals(Action, InvoiceHistory.ActionExport, StringComparison.OrdinalIgnoreCase); } }
        public bool IsImport { get { return string.Equals(Action, InvoiceHistory.ActionImport, StringComparison.OrdinalIgnoreCase); } }
    }

    /// <summary>
    /// 发票导入/导出历史记录：
    /// - 以 TSV 追加写入（O(k) 写入），启动/加载时建立内存哈希索引（O(1) 查重），大数量发票下读写可控；
    /// - 最多保留 1 年零 2 个月（14 个月），过期记录在加载或清理时自动剔除并压缩文件；
    /// - 查重核心为发票号（无发票号时退回文件 SHA-256）。
    /// </summary>
    public class InvoiceHistory
    {
        public const string FileName = "PiaoMate.history.tsv";
        public const int RetentionMonths = 14;
        public const string ActionImport = "import";
        public const string ActionExport = "export";

        static readonly object defaultLock = new object();
        static InvoiceHistory defaultInstance;

        readonly string path;
        readonly object sync = new object();
        readonly Dictionary<string, HistoryRecord> importsByNum = new Dictionary<string, HistoryRecord>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, HistoryRecord> exportsByNum = new Dictionary<string, HistoryRecord>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, HistoryRecord> importsByHash = new Dictionary<string, HistoryRecord>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, HistoryRecord> exportsByHash = new Dictionary<string, HistoryRecord>(StringComparer.OrdinalIgnoreCase);
        readonly List<HistoryRecord> records = new List<HistoryRecord>();
        DateTime lastWriteUtc;
        DateTime lastLoadDate;

        public string FilePath { get { return path; } }
        internal object SyncRoot { get { return sync; } }

        public static InvoiceHistory Default
        {
            get
            {
                if (defaultInstance != null) return defaultInstance;
                lock (defaultLock)
                {
                    if (defaultInstance == null) defaultInstance = new InvoiceHistory(ResolvePath());
                    return defaultInstance;
                }
            }
        }

        /// <summary>按与 PiaoMate.config 一致的便携策略确定历史文件路径。</summary>
        public static string ResolvePath()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string portable = Path.Combine(exeDir, FileName);
            string roaming = Path.Combine(appData, "PiaoMate", FileName);
            if (File.Exists(portable)) return portable;
            if (File.Exists(roaming)) return roaming;
            // 若便携目录可写（或便携配置就在 exe 旁），优先放 exe 旁
            try
            {
                string probe = Path.Combine(exeDir, ".pm_write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return portable;
            }
            catch { return roaming; }
        }

        public InvoiceHistory(string path, DateTime? now = null)
        {
            this.path = path;
            Load(now ?? DateTime.Now);
        }

        public int Count { get { lock (sync) return records.Count; } }

        public void ReloadIfChanged(DateTime? now = null)
        {
            lock (sync)
            {
                if (!File.Exists(path)) return;
                DateTime mtime;
                try { mtime = File.GetLastWriteTimeUtc(path); } catch { return; }
                DateTime time = now ?? DateTime.Now;
                if (mtime != lastWriteUtc || time.Date != lastLoadDate) LoadLocked(time);
            }
        }

        void Load(DateTime now)
        {
            lock (sync) LoadLocked(now);
        }

        void LoadLocked(DateTime now)
        {
            importsByNum.Clear();
            exportsByNum.Clear();
            importsByHash.Clear();
            exportsByHash.Clear();
            records.Clear();
            if (!File.Exists(path)) { lastWriteUtc = DateTime.MinValue; return; }

            DateTime cutoff = now.AddMonths(-RetentionMonths);
            int totalLines = 0, dropped = 0;
            try
            {
                using (var sr = new StreamReader(path, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.Length == 0 || line[0] == '#') continue;
                        totalLines++;
                        var rec = ParseLine(line);
                        if (rec == null) { dropped++; continue; }
                        if (rec.Time < cutoff) { dropped++; continue; }
                        AddInMemory(rec);
                    }
                }
                lastWriteUtc = File.GetLastWriteTimeUtc(path);
                lastLoadDate = now.Date;
            }
            catch { return; }

            // 有过期记录或冗余行过多时压缩重写
            if (dropped > 0 || totalLines > records.Count * 2 + 200)
                CompactLocked();
        }

        void AddInMemory(HistoryRecord rec)
        {
            records.Add(rec);
            string num = Norm(rec.Number);
            string hash = NormHash(rec.Sha256);
            if (rec.IsExport)
            {
                if (num.Length > 0) exportsByNum[num] = rec;
                if (hash.Length > 0) exportsByHash[hash] = rec;
            }
            else if (rec.IsImport)
            {
                if (num.Length > 0) importsByNum[num] = rec;
                if (hash.Length > 0) importsByHash[hash] = rec;
            }
        }

        public HistoryRecord FindExport(Invoice inv, string sha256 = null)
        {
            if (inv == null && string.IsNullOrEmpty(sha256)) return null;
            if (sha256 == null) sha256 = FallbackHash(inv);
            lock (sync)
            {
                HistoryRecord r;
                string num = inv != null ? Norm(inv.Number) : "";
                if (num.Length > 0 && exportsByNum.TryGetValue(num, out r)) return r;
                string h = NormHash(sha256);
                if (h.Length > 0 && exportsByHash.TryGetValue(h, out r)) return r;
                return null;
            }
        }

        internal static string FallbackHash(Invoice inv)
        {
            if (inv == null || !string.IsNullOrWhiteSpace(inv.Number) || string.IsNullOrEmpty(inv.Path)) return "";
            try { return Organizer.Hash(inv.Path); }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        public HistoryRecord FindImport(Invoice inv, string sha256 = null)
        {
            if (inv == null && string.IsNullOrEmpty(sha256)) return null;
            lock (sync)
            {
                HistoryRecord r;
                string num = inv != null ? Norm(inv.Number) : "";
                if (num.Length > 0 && importsByNum.TryGetValue(num, out r)) return r;
                string h = NormHash(sha256);
                if (h.Length > 0 && importsByHash.TryGetValue(h, out r)) return r;
                return null;
            }
        }

        public HistoryRecord FindExportByNumber(string number)
        {
            string num = Norm(number);
            if (num.Length == 0) return null;
            lock (sync)
            {
                HistoryRecord r;
                return exportsByNum.TryGetValue(num, out r) ? r : null;
            }
        }

        public HistoryRecord FindImportByNumber(string number)
        {
            string num = Norm(number);
            if (num.Length == 0) return null;
            lock (sync)
            {
                HistoryRecord r;
                return importsByNum.TryGetValue(num, out r) ? r : null;
            }
        }

        public void RecordImport(Invoice inv, string note, string sha256 = null, DateTime? now = null)
        {
            if (inv == null) return;
            RecordBatch(new[] { MakeRecord(ActionImport, inv, note, sha256, now ?? DateTime.Now) });
        }

        public void RecordImports(IEnumerable<Tuple<Invoice, string>> items, string note, DateTime? now = null)
        {
            DateTime t = now ?? DateTime.Now;
            var list = new List<HistoryRecord>();
            foreach (var x in items)
                if (x != null && x.Item1 != null) list.Add(MakeRecord(ActionImport, x.Item1, note, x.Item2, t));
            if (list.Count > 0) RecordBatch(list);
        }

        public void RecordExport(IEnumerable<Invoice> invoices, string note, DateTime? now = null)
        {
            DateTime t = now ?? DateTime.Now;
            var list = new List<HistoryRecord>();
            foreach (var inv in invoices)
            {
                if (inv == null) continue;
                string hash = "";
                try { if (!string.IsNullOrEmpty(inv.Path) && File.Exists(inv.Path)) hash = Organizer.Hash(inv.Path); } catch { }
                list.Add(MakeRecord(ActionExport, inv, note, hash, t));
            }
            if (list.Count > 0) RecordBatch(list);
        }

        static HistoryRecord MakeRecord(string action, Invoice inv, string note, string sha256, DateTime time)
        {
            return new HistoryRecord
            {
                Action = action,
                Time = time,
                Number = Norm(inv.Number),
                Amount = inv.Amount,
                InvoiceDate = inv.Date.HasValue ? inv.Date.Value.ToString("yyyy-MM-dd") : "",
                BuyerTaxId = CleanField(inv.BuyerTaxId),
                BuyerName = CleanField(inv.BuyerName),
                Note = CleanField(note),
                Sha256 = NormHash(sha256)
            };
        }

        void RecordBatch(IList<HistoryRecord> batch)
        {
            if (batch.Count == 0) return;
            lock (sync)
            {
                var sb = new StringBuilder();
                foreach (var r in batch)
                {
                    AddInMemory(r);
                    sb.AppendLine(FormatLine(r));
                }
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                    lastWriteUtc = File.GetLastWriteTimeUtc(path);
                }
                catch (IOException ex) { throw new IOException("无法保存发票历史记录：" + path, ex); }
                catch (UnauthorizedAccessException ex) { throw new IOException("无法保存发票历史记录：" + path, ex); }
            }
        }

        /// <summary>清理超过保留期（14 个月）的记录并压缩文件。</summary>
        public int Prune(DateTime? now = null)
        {
            DateTime cutoff = (now ?? DateTime.Now).AddMonths(-RetentionMonths);
            lock (sync)
            {
                int before = records.Count;
                var kept = records.Where(r => r.Time >= cutoff).ToList();
                int removed = before - kept.Count;
                if (removed == 0) return 0;
                importsByNum.Clear();
                exportsByNum.Clear();
                importsByHash.Clear();
                exportsByHash.Clear();
                records.Clear();
                foreach (var r in kept) AddInMemory(r);
                CompactLocked();
                return removed;
            }
        }

        public List<HistoryRecord> GetAll()
        {
            lock (sync)
            {
                var list = new List<HistoryRecord>(records);
                list.Reverse();
                return list;
            }
        }

        void CompactLocked()
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                using (var sw = new StreamWriter(tmp, false, new UTF8Encoding(false)))
                {
                    foreach (var r in records) sw.WriteLine(FormatLine(r));
                }
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                lastWriteUtc = File.GetLastWriteTimeUtc(path);
            }
            catch { }
        }

        static string FormatLine(HistoryRecord r)
        {
            return string.Join("\t", new[]
            {
                CleanField(r.Action),
                r.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                CleanField(r.Number),
                r.Amount.ToString("F2", CultureInfo.InvariantCulture),
                CleanField(r.InvoiceDate),
                CleanField(r.BuyerTaxId),
                CleanField(r.BuyerName),
                CleanField(r.Note),
                NormHash(r.Sha256)
            });
        }

        static HistoryRecord ParseLine(string line)
        {
            var p = line.Split('\t');
            if (p.Length < 4) return null;
            DateTime t;
            if (!DateTime.TryParseExact(p[1], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                if (!DateTime.TryParse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return null;
            decimal amt;
            decimal.TryParse(p[3], NumberStyles.Number, CultureInfo.InvariantCulture, out amt);
            return new HistoryRecord
            {
                Action = p[0],
                Time = t,
                Number = p[2],
                Amount = amt,
                InvoiceDate = p.Length > 4 ? p[4] : "",
                BuyerTaxId = p.Length > 5 ? p[5] : "",
                BuyerName = p.Length > 6 ? p[6] : "",
                Note = p.Length > 7 ? p[7] : "",
                Sha256 = p.Length > 8 ? p[8] : ""
            };
        }

        static string Norm(string s) { return string.IsNullOrWhiteSpace(s) ? "" : s.Trim(); }
        static string NormHash(string s) { return string.IsNullOrWhiteSpace(s) ? "" : s.Replace("-", "").Trim().ToUpperInvariant(); }
        static string CleanField(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }
}
