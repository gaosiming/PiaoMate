using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PiaoMate
{
    /// <summary>简单的 key=value 配置，保存在 exe 同目录（便携）；目录不可写时退回 %APPDATA%。</summary>
    public class Settings
    {
        public string InvoiceDir = "";
        public string ExportDir = "";
        /// <summary>发票整理（归档）目录；为空时使用 导出目录\发票整理</summary>
        public string ArchiveDir = "";
        /// <summary>扫码开票时自动填写的手机号、邮箱，以及上次选择的开票公司（税号）</summary>
        public string Phone = "", Email = "", IssueCompany = "";
        /// <summary>已同意的免责声明版本（见 DisclaimerForm.Version），为空表示尚未同意。</summary>
        public string DisclaimerAccepted = "";
        public string EffectiveArchiveDir
        {
            get { return ArchiveDir.Length > 0 ? ArchiveDir : (ExportDir.Length > 0 ? Path.Combine(ExportDir, "发票整理") : ""); }
        }
        public bool Recurse = true;
        public bool DateEnabled = false;
        public DateTime DateFrom = new DateTime(DateTime.Today.Year, 1, 1);
        public DateTime DateTo = DateTime.Today;
        /// <summary>备选公司（名称, 税号），可在配置文件中用 CompanyN=名称|税号 修改或新增</summary>
        public List<KeyValuePair<string, string>> Companies = DefaultCompanies();
        /// <summary>"" = 不筛选；"custom" = 自定义；否则为所选备选公司的税号</summary>
        public string CompanyChoice = "";
        public string CustomName = "";
        public string CustomTaxId = "";
        public string Target = "";
        public int WinW = 1000, WinH = 640;

        /// <summary>
        /// 内置备选公司。开源（公开）版没有内置公司；内部版构建时（-p:Edition=Internal）把 internal\companies.txt
        /// 嵌入为资源 builtin.companies.txt，每行「名称|税号」，# 开头为注释。
        /// </summary>
        public static List<KeyValuePair<string, string>> DefaultCompanies()
        {
            return ParseCompanies(BuiltinText());
        }

        /// <summary>是否为内置了公司信息的内部版。</summary>
        public static bool IsInternalEdition { get { return BuiltinText() != null; } }

        static string builtin; static bool builtinLoaded;
        static string BuiltinText()
        {
            if (!builtinLoaded)
            {
                using (var s = typeof(Settings).Assembly.GetManifestResourceStream("builtin.companies.txt"))
                    if (s != null) using (var r = new StreamReader(s, Encoding.UTF8)) builtin = r.ReadToEnd();
                builtinLoaded = true;
            }
            return builtin;
        }

        public static List<KeyValuePair<string, string>> ParseCompanies(string text)
        {
            var l = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(text)) return l;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int p = line.LastIndexOf('|');
                string name = (p < 0 ? line : line.Substring(0, p)).Trim(), tax = p < 0 ? "" : line.Substring(p + 1).Trim();
                if (name.Length > 0) l.Add(new KeyValuePair<string, string>(name, tax));
            }
            return l;
        }

        public const string FileName = "PiaoMate.config";

        static string PortablePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName); } }
        static string RoamingPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PiaoMate", FileName); }
        }

        /// <summary>按优先级查找配置：exe 旁（便携）→ %APPDATA%\PiaoMate。</summary>
        public static string FindConfig(string exeDir, string appData)
        {
            foreach (var p in new[] { Path.Combine(exeDir, FileName), Path.Combine(appData, "PiaoMate", FileName) })
                if (File.Exists(p)) return p;
            return null;
        }

        public static Settings Load(string path = null)
        {
            var s = new Settings();
            try
            {
                if (path == null) path = FindConfig(AppDomain.CurrentDomain.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
                if (path == null || !File.Exists(path)) return s;
                var d = new Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                string v;
                if (d.TryGetValue("InvoiceDir", out v)) s.InvoiceDir = v;
                if (d.TryGetValue("ExportDir", out v)) s.ExportDir = v;
                if (d.TryGetValue("ArchiveDir", out v)) s.ArchiveDir = v;
                if (d.TryGetValue("Phone", out v)) s.Phone = v;
                if (d.TryGetValue("Email", out v)) s.Email = v;
                if (d.TryGetValue("IssueCompany", out v)) s.IssueCompany = v;
                if (d.TryGetValue("DisclaimerAccepted", out v)) s.DisclaimerAccepted = v;
                if (d.TryGetValue("Recurse", out v)) s.Recurse = v == "1";
                if (d.TryGetValue("DateEnabled", out v)) s.DateEnabled = v == "1";
                DateTime dt;
                if (d.TryGetValue("DateFrom", out v) && DateTime.TryParse(v, out dt)) s.DateFrom = dt;
                if (d.TryGetValue("DateTo", out v) && DateTime.TryParse(v, out dt)) s.DateTo = dt;
                int n;
                if (d.TryGetValue("CompanyChoice", out v)) s.CompanyChoice = v;
                var comps = new List<KeyValuePair<int, KeyValuePair<string, string>>>();
                foreach (var kv in d)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(kv.Key, @"^Company(\d+)$");
                    int k = kv.Value.LastIndexOf('|');
                    if (!m.Success || k < 0) continue;
                    comps.Add(new KeyValuePair<int, KeyValuePair<string, string>>(int.Parse(m.Groups[1].Value),
                        new KeyValuePair<string, string>(kv.Value.Substring(0, k).Trim(), kv.Value.Substring(k + 1).Trim())));
                }
                if (comps.Count > 0) s.Companies = comps.OrderBy(c => c.Key).Select(c => c.Value).ToList();
                if (d.TryGetValue("CustomName", out v)) s.CustomName = v;
                if (d.TryGetValue("CustomTaxId", out v)) s.CustomTaxId = v;
                if (d.TryGetValue("Target", out v)) s.Target = v;
                if (d.TryGetValue("WinW", out v) && int.TryParse(v, out n) && n >= 600) s.WinW = n;
                if (d.TryGetValue("WinH", out v) && int.TryParse(v, out n) && n >= 400) s.WinH = n;
            }
            catch { }
            return s;
        }

        public void Save(string path = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("InvoiceDir=" + InvoiceDir);
            sb.AppendLine("ExportDir=" + ExportDir);
            sb.AppendLine("ArchiveDir=" + ArchiveDir);
            sb.AppendLine("Phone=" + Phone);
            sb.AppendLine("Email=" + Email);
            sb.AppendLine("IssueCompany=" + IssueCompany);
            sb.AppendLine("DisclaimerAccepted=" + DisclaimerAccepted);
            sb.AppendLine("Recurse=" + (Recurse ? "1" : "0"));
            sb.AppendLine("DateEnabled=" + (DateEnabled ? "1" : "0"));
            sb.AppendLine("DateFrom=" + DateFrom.ToString("yyyy-MM-dd"));
            sb.AppendLine("DateTo=" + DateTo.ToString("yyyy-MM-dd"));
            sb.AppendLine("CompanyChoice=" + CompanyChoice);
            sb.AppendLine("# 备选公司，格式：CompanyN=公司名称|纳税人识别号，可修改或新增（N 为序号）");
            for (int i = 0; i < Companies.Count; i++) sb.AppendLine("Company" + (i + 1) + "=" + Companies[i].Key + "|" + Companies[i].Value);
            sb.AppendLine("CustomName=" + CustomName);
            sb.AppendLine("CustomTaxId=" + CustomTaxId);
            sb.AppendLine("Target=" + Target);
            sb.AppendLine("WinW=" + WinW);
            sb.AppendLine("WinH=" + WinH);
            if (path != null) { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); return; }
            try { File.WriteAllText(PortablePath, sb.ToString(), new UTF8Encoding(false)); return; }
            catch { }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RoamingPath));
                File.WriteAllText(RoamingPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}



