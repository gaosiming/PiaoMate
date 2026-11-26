using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PiaoMate;

static class T
{
    static int fail, pass;
    static void Eq<X>(X exp, X act, string name)
    {
        if (Equals(exp, act)) pass++; else { fail++; Console.WriteLine("FAIL " + name + ": 期望 [" + exp + "] 实际 [" + act + "]"); }
    }

    static long Sum(long[] a, List<int> p) { return p.Sum(i => a[i]); }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--live")
        {
            // 手动联网检查：dotnet run --project tests\PiaoMate.Tests -- --live <网址>
            var o = new Downloader().FromText(args[1], Path.Combine(Path.GetTempPath(), "fp_live"), System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine((o.Ok ? "OK " + string.Join(";", o.Pdfs) : "FAIL") + " " + o.Message);
            return o.Ok ? 0 : 1;
        }
        try { return Run(); } catch (Exception ex) { Console.WriteLine("EXC " + ex.GetType().FullName + ": " + ex.Message + ex.StackTrace); return 2; }
    }
    static int Run()
    {
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\.."));
        string root = Path.Combine(repo, "mock");
        // 测试用发票：运行时生成的虚构发票（仓库中不放真实发票）
        if (SampleInvoice.FontPath() == null) { Console.WriteLine("FAIL 未找到中文字体（Deng.ttf / simhei.ttf，或用环境变量 " + SampleInvoice.FontEnv + " 指定 .ttf），无法生成测试发票"); return 1; }
        string samples = Path.Combine(Path.GetTempPath(), "fp_samples_" + Guid.NewGuid().ToString("N"));
        var A = SampleInvoice.A; var B = SampleInvoice.B;
        var f1 = A.Save(Path.Combine(samples, A.Number + "_" + A.BuyerName + "_34.92.pdf"));
        var f2 = B.Save(Path.Combine(samples, B.Number + "_33.08_" + B.BuyerName + ".pdf"));
        try { return RunAll(repo, root, f1, f2); }
        finally { try { Directory.Delete(samples, true); } catch { } }
    }

    static int RunAll(string repo, string root, string f1, string f2)
    {
        var A = SampleInvoice.A; var B = SampleInvoice.B;
        var a = InvoiceParser.Parse(f1);
        Eq(true, a.Ok, "a.Ok"); Eq(34.92m, a.Amount, "a.Amount"); Eq(A.BuyerTaxId, a.BuyerTaxId, "a.tax");
        Eq(A.BuyerName, a.BuyerName, "a.buyer"); Eq(A.SellerName, a.SellerName, "a.seller"); Eq(A.Number, a.Number, "a.number");
        Eq(new DateTime(2026, 8, 27), a.Date.Value, "a.date"); Eq(A.Item, a.Item, "a.item");
        // 测试发票必须标明「非真实发票」（见 docs/免责声明.md）
        using (var doc = UglyToad.PdfPig.PdfDocument.Open(f1))
            Eq(true, string.Concat(doc.GetPage(1).Letters.Select(l => l.Value)).Contains("非真实发票"), "测试发票标注非真实发票");
        var b = InvoiceParser.Parse(f2);
        Eq(33.08m, b.Amount, "b.Amount"); Eq(B.BuyerTaxId, b.BuyerTaxId, "b.tax"); Eq(B.BuyerName, b.BuyerName, "b.buyer");
        Eq(new DateTime(2026, 11, 11), b.Date.Value, "b.date"); Eq(B.Item, b.Item, "b.item");
        LocalSampleCheck(root);

        // 异常输入
        var miss = InvoiceParser.Parse(Path.Combine(root, "不存在.pdf")); Eq(false, miss.Ok, "missing");
        string bad = Path.Combine(Path.GetTempPath(), "fp_bad.pdf"); File.WriteAllText(bad, "not a pdf");
        Eq(false, InvoiceParser.Parse(bad).Ok, "malformed"); File.Delete(bad);

        // 匹配：合计必须 >= 目标，且最接近
        var am = new long[] { 3492, 3308, 1000, 2500, 10000 };
        Eq(6800L, Sum(am, Matcher.Find(am, 6800)), "恰好凑齐");
        Eq(6801L < Sum(am, Matcher.Find(am, 6801)), true, "不能少");
        Eq(6808L, Sum(am, Matcher.Find(am, 6801)), "6801 的最小够数组合");
        Eq(1000L, Sum(am, Matcher.Find(am, 100)), "目标很小取最小够数");
        Eq(0, Matcher.Find(am, 0).Count, "目标0");
        Eq(0, Matcher.Find(new long[0], 100).Count, "空列表");
        Eq(0, Matcher.Find(am, 100000).Count, "总额不足返回空");
        var neg = new long[] { -500, 1000 };
        Eq(1000L, Sum(neg, Matcher.Find(neg, 900)), "忽略负数");
        var rnd = new Random(1);
        for (int t = 0; t < 200; t++)
        {
            var arr = Enumerable.Range(0, rnd.Next(1, 12)).Select(_ => (long)rnd.Next(100, 5000)).ToArray();
            long tg = rnd.Next(1, (int)arr.Sum() + 1);
            long bf = long.MaxValue;
            for (int mask = 1; mask < (1 << arr.Length); mask++) { long sm = 0; for (int j = 0; j < arr.Length; j++) if ((mask >> j & 1) == 1) sm += arr[j]; if (sm >= tg && sm < bf) bf = sm; }
            var r = Matcher.Find(arr, tg);
            if (Sum(arr, r) != bf || r.Distinct().Count() != r.Count) { fail++; Console.WriteLine("FAIL 随机对拍 " + string.Join(",", arr) + " 目标 " + tg + " 得 " + Sum(arr, r) + " 期望 " + bf); break; } else pass++;
        }

        // 导出
        string tmp = Path.Combine(Path.GetTempPath(), "fp_test_" + Guid.NewGuid().ToString("N"));
        string src = Path.Combine(tmp, "src"), outd = Path.Combine(tmp, "out"); Directory.CreateDirectory(src);
        File.Copy(f1, Path.Combine(src, Path.GetFileName(f1))); File.Copy(f2, Path.Combine(src, Path.GetFileName(f2)));
        var l = Directory.GetFiles(src).Select(InvoiceParser.Parse).ToList();
        string dest = Exporter.Export(l, outd, new DateTime(2026, 11, 17, 9, 8, 7));
        Eq("20261117_090807", Path.GetFileName(dest), "子文件夹");
        Eq(0, Directory.GetFiles(src).Length, "源已移动"); Eq(3, Directory.GetFiles(dest).Length, "目标含2PDF+清单");
        string txt = File.ReadAllText(Path.Combine(dest, "清单.txt"));
        Eq(true, txt.Contains("总计金额：68.00 元") && txt.Contains("金额：34.92 元") && txt.Contains("税号：" + A.BuyerTaxId) && txt.Contains(A.BuyerName), "清单内容");
        Eq(false, txt.Contains("餐饮有限公司"), "清单不含销售方");
        Eq(true, File.Exists(Path.Combine(dest, Path.GetFileName(f1))) && txt.Contains("文件名：" + Path.GetFileName(f1)), "已含金额的文件名不变");
        Eq(A.Number + "_" + A.BuyerName + "_34.92.pdf", Exporter.NewFileName(new Invoice { FileName = A.Number + "_" + A.BuyerName + ".pdf", Amount = 34.92m }), "追加金额的新文件名");
        Eq(true, File.Exists(Path.Combine(dest, Path.GetFileName(f2))), "已含金额不重复追加");
        Console.WriteLine(txt);
        Directory.Delete(tmp, true);

        Eq("某公司_发票金额39.00元.pdf", Exporter.NewFileName(new Invoice { FileName = "某公司_发票金额39.00元.pdf", Amount = 39m }), "名中已含金额不追加");
        Eq("某公司_139.00.pdf", Exporter.NewFileName(new Invoice { FileName = "某公司.pdf", Amount = 139m }).Replace("某公司_139.00.pdf","某公司_139.00.pdf"), "追加");
        Eq("某公司_1239.00_39.00.pdf", Exporter.NewFileName(new Invoice { FileName = "某公司_1239.00.pdf", Amount = 39m }), "数字子串不误判");

        Eq("2026年08月26日 某某麦香食品 生产生活服务", InvoiceParser.Clean("2026年08⽉26⽇ 某某⻨香⻝品 ⽣产⽣活服务"), "康熙部首还原");
        Eq("示例（北京）贸易", InvoiceParser.Clean("示例（北京）贸易"), "正常字符不变");

        // 设置往返
        string cp = Path.Combine(Path.GetTempPath(), "fp_cfg.txt");
        var s = new Settings { InvoiceDir = @"D:\a=b", Target = "12.5", CompanyChoice = "custom", Companies = new List<System.Collections.Generic.KeyValuePair<string,string>>{ new System.Collections.Generic.KeyValuePair<string,string>("甲公司","TAX1"), new System.Collections.Generic.KeyValuePair<string,string>("乙|公司","TAX2") }, CustomName = "某公司", DateEnabled = true, MainColOrder = "amount,number,date,buyer,tax,dir,item,file", AutoDeleteAfterOrganize = false, AutoDeletePromptYear = 2026 };
        s.Save(cp); var s2 = Settings.Load(cp);
        Eq(@"D:\a=b", s2.InvoiceDir, "cfg dir"); Eq("custom", s2.CompanyChoice, "cfg choice"); Eq(2, s2.Companies.Count, "cfg comps"); Eq("乙|公司", s2.Companies[1].Key, "cfg comp name"); Eq("TAX2", s2.Companies[1].Value, "cfg comp tax"); Eq(0, new Settings().Companies.Count, "公开版无内置公司"); Eq(false, Settings.IsInternalEdition, "测试为公开版"); Eq("某公司", s2.CustomName, "cfg name"); Eq(true, s2.DateEnabled, "cfg date");
        Eq("amount,number,date,buyer,tax,dir,item,file", s2.MainColOrder, "cfg colOrder");
        Eq(false, s2.AutoDeleteAfterOrganize, "cfg autoDelete");
        Eq(2026, s2.AutoDeletePromptYear, "cfg autoDeleteYear");
        Eq(true, new Settings().AutoDeleteAfterOrganize, "默认整理后删除原文件");
        Eq(false, OrganizeForm.ShouldPromptAutoDelete(s2, 2026), "本年度不再提示");
        Eq(true, OrganizeForm.ShouldPromptAutoDelete(s2, 2027), "跨年重新提示");
        File.Delete(cp);

        ColumnOrderTests();
        HistoryAndDedupTests(f1, f2);
        OrganizerTests(f1, f2);
        DownloadListUiTests(f1);
        DownloaderTests(f1).GetAwaiter().GetResult();
        NuonuoTests(f1).GetAwaiter().GetResult();
        BaiwangTests(f1).GetAwaiter().GetResult();
        ChangelogTests(repo);
        CompanyListTests();

        Console.WriteLine("通过 " + pass + "，失败 " + fail);
        return fail == 0 ? 0 : 1;
    }

    /// <summary>本地真实样例（mock/*.pdf，不提交 Git）：存在时逐个确认能识别，不校验具体内容。</summary>
    static void LocalSampleCheck(string mockDir)
    {
        var files = Directory.Exists(mockDir) ? Directory.GetFiles(mockDir, "*.pdf") : new string[0];
        if (files.Length == 0) { Console.WriteLine("（mock/ 中没有本地真实样例，跳过真实样例检查）"); return; }
        foreach (var f in files)
        {
            var inv = InvoiceParser.Parse(f);
            Eq(true, inv.Ok && inv.Amount > 0 && inv.BuyerName.Length > 0 && inv.BuyerTaxId.Length > 0 && inv.Date.HasValue,
               "本地真实样例可识别：" + Path.GetFileName(f) + (inv.Ok ? "" : "（" + inv.Error + "）"));
        }
    }

    /// <summary>内置公司列表（内部版 internal\companies.txt 的格式）。</summary>
    static void CompanyListTests()
    {
        var l = Settings.ParseCompanies("﻿# 注释\r\n甲公司|TAX1\r\n\r\n乙|公司|TAX2\n 丙公司 \n");
        Eq(3, l.Count, "内置公司条数");
        Eq("甲公司", l[0].Key, "内置公司名"); Eq("TAX1", l[0].Value, "内置公司税号");
        Eq("乙|公司", l[1].Key, "名称含竖线"); Eq("TAX2", l[1].Value, "名称含竖线的税号");
        Eq("丙公司", l[2].Key, "无税号"); Eq("", l[2].Value, "无税号为空");
        Eq(0, Settings.ParseCompanies(null).Count, "无内置公司");

        // 配置查找：exe 旁的 PiaoMate.config 优先于 %APPDATA%\PiaoMate\PiaoMate.config
        string tmp = Path.Combine(Path.GetTempPath(), "pm_cfgname_" + Guid.NewGuid().ToString("N"));
        string exeDir = Path.Combine(tmp, "exe"), appData = Path.Combine(tmp, "appdata");
        Directory.CreateDirectory(exeDir); Directory.CreateDirectory(Path.Combine(appData, "PiaoMate"));
        Eq(null, Settings.FindConfig(exeDir, appData), "没有配置");
        string roam = Path.Combine(appData, "PiaoMate", "PiaoMate.config"); File.WriteAllText(roam, "Phone=1");
        Eq(roam, Settings.FindConfig(exeDir, appData), "读取 %APPDATA% 配置");
        string portable = Path.Combine(exeDir, "PiaoMate.config"); File.WriteAllText(portable, "Phone=2");
        Eq(portable, Settings.FindConfig(exeDir, appData), "便携配置优先");
        Eq("2", Settings.Load(portable).Phone, "配置内容可读");
        Directory.Delete(tmp, true);
    }

    /// <summary>下载窗口：导入本地 PDF 后，行的悬停信息含发票概要，且能取到整理后的文件（打开所在目录用）。</summary>
    static void DownloadListUiTests(string f1)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "fp_dlui_" + Guid.NewGuid().ToString("N"));
        string src = Path.Combine(tmp, "原始.pdf"); Directory.CreateDirectory(tmp); File.Copy(f1, src);
        var cfg = new Settings { ArchiveDir = Path.Combine(tmp, "归档") };
        var hist = new InvoiceHistory(Path.Combine(tmp, "history.tsv"));
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
        using (var form = new DownloadForm(cfg, hist))
        {
            form.Show();
            typeof(DownloadForm).GetMethod("AddFiles", flags).Invoke(form, new object[] { new[] { src } });
            var lv = (System.Windows.Forms.ListView)typeof(DownloadForm).GetField("lv", flags).GetValue(form);
            var until = DateTime.Now.AddSeconds(20);
            while (DateTime.Now < until && !lv.Items[0].SubItems[1].Text.StartsWith("已整理")) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(20); }
            Eq("已整理", lv.Items[0].SubItems[1].Text, "下载列表整理状态");
            string tip = lv.Items[0].ToolTipText;
            Eq(true, lv.ShowItemToolTips, "下载列表启用悬停");
            Eq(true, tip.Contains("发票金额：34.92 元") && tip.Contains("税号：" + SampleInvoice.A.BuyerTaxId) && tip.Contains("发票号码：" + SampleInvoice.A.Number), "悬停含金额税号发票号");
            Eq(true, tip.Contains("文件：" + cfg.ArchiveDir) && tip.Contains("原文件：" + src), "悬停含整理后路径和原文件");
            var job = lv.Items[0].Tag;
            var arr = Array.CreateInstance(job.GetType(), 1); arr.SetValue(job, 0);
            var files = ((IEnumerable<string>)typeof(DownloadForm).GetMethod("DoneFiles", flags).Invoke(null, new object[] { arr })).ToList();
            Eq(1, files.Count, "可打开所在目录的文件数");
            Eq(true, files.Count == 1 && files[0].StartsWith(cfg.ArchiveDir) && File.Exists(files[0]), "所在目录指向整理后文件");
            Eq(true, File.Exists(src), "导入的原文件保留");
            var menu = lv.ContextMenuStrip.Items.Cast<System.Windows.Forms.ToolStripItem>().Select(i => i.Text).ToList();
            Eq(true, menu.Contains("打开所在目录并选中") && menu.Contains("复制文件地址"), "下载列表右键菜单");
            form.Close();
        }
        Directory.Delete(tmp, true);
    }

    /// <summary>诺诺网适配：短链跳转到 printQrcode 页，POST 接口取 PDF 地址。数据为虚构样例。</summary>
    static async System.Threading.Tasks.Task NuonuoTests(string f1)
    {
        var none = System.Threading.CancellationToken.None;
        var page = new Uri("https://nnfp.jss.com.cn/scan-invoice/printQrcode?paramList=91110000TEST0000X!!!1234567890!false&aliView=true&shortLinkSource=1&wxApplet=0");
        Eq(true, SiteAdapter.Find(page) is NuonuoAdapter, "识别诺诺网页面");
        Eq(null, SiteAdapter.Find(new Uri("https://nnfp.jss.com.cn/about")), "诺诺网其他页面不匹配");
        Eq(null, SiteAdapter.Find(new Uri("https://example.com/printQrcode?paramList=1")), "其他域名不匹配");

        byte[] pdf = File.ReadAllBytes(f1);
        string route = "INVOICE_TEMPLATE_VIEW", status = "0000";
        var posts = new List<string>();
        Func<Uri, System.Threading.CancellationToken, System.Threading.Tasks.Task<FetchResult>> get = (u, ct) =>
        {
            FetchResult r;
            if (u.AbsoluteUri == "https://nnfp.jss.com.cn/a8nTEST")
                r = new FetchResult { FinalUri = page, ContentType = "text/html; charset=utf-8", Body = System.Text.Encoding.UTF8.GetBytes("<!doctype html><div id=app></div><script src=/scan-invoice/js/index.js></script>") };
            else if (u.AbsoluteUri == "https://inv.jss.com.cn/fp2/TESTTOKEN.pdf")
                r = new FetchResult { FinalUri = u, ContentType = "application/pdf", Body = pdf };
            else throw new Exception("404");
            return System.Threading.Tasks.Task.FromResult(r);
        };
        Func<Uri, IDictionary<string, string>, Uri, System.Threading.CancellationToken, System.Threading.Tasks.Task<FetchResult>> post = (u, form, referer, ct) =>
        {
            posts.Add(u.AbsoluteUri + "|" + form["paramList"] + "|" + (referer != null ? referer.AbsolutePath : ""));
            string json = "{\"status\":\"" + status + "\",\"msg\":" + (status == "0000" ? "null" : "\"订单不存在\"") + ",\"routeView\":\"" + route + "\",\"data\":{\"invoiceSimpleVo\":{\"buyername\":\"测试公司\",\"fphm\":\"00000000000000000001\"," +
                          "\"url\":\"https://inv.jss.com.cn/fp2/TESTTOKEN.pdf\",\"imgUrl\":\"https://inv.jss.com.cn/fp2/TESTTOKEN.jpg\",\"ofdDownloadUrl\":\"https://inv.jss.com.cn/fp2/TESTTOKEN.ofd\"}}}";
            return System.Threading.Tasks.Task.FromResult(new FetchResult { FinalUri = u, ContentType = "application/json;charset=utf-8", Body = System.Text.Encoding.UTF8.GetBytes(json) });
        };
        string dir = Path.Combine(Path.GetTempPath(), "fp_nn_" + Guid.NewGuid().ToString("N"));
        var o = await new Downloader(get, post).FromText("https://nnfp.jss.com.cn/a8nTEST", dir, none);
        Eq(1, o.Pdfs.Count, "诺诺网短链自动下载PDF");
        Eq(true, o.Pdfs.Count == 1 && Downloader.IsPdf(File.ReadAllBytes(o.Pdfs[0])) && o.Pdfs[0].EndsWith("TESTTOKEN.pdf"), "诺诺网PDF保存");
        Eq("https://nnfp.jss.com.cn/scan2/getIvcDetailShow.do|91110000TEST0000X!!!1234567890!false|/scan-invoice/printQrcode", posts.FirstOrDefault(), "诺诺网接口与参数");

        route = "VALID_CODE_VIEW"; posts.Clear();
        o = await new Downloader(get, post).FromText("https://nnfp.jss.com.cn/a8nTEST", dir, none);
        Eq(true, !o.Ok && o.NeedsBrowser && o.Message.StartsWith("【诺诺网】") && o.Message.Contains("验证码"), "诺诺网需验证码");

        route = "INVOICE_STATUS_VIEW";
        o = await new Downloader(get, post).FromText("https://nnfp.jss.com.cn/a8nTEST", dir, none);
        Eq(true, !o.Ok && o.NeedsBrowser && o.Message.Contains("尚未开具完成"), "诺诺网未开具");

        status = "9999"; route = "";
        o = await new Downloader(get, post).FromText("https://nnfp.jss.com.cn/a8nTEST", dir, none);
        Eq(true, !o.Ok && o.Message.Contains("订单不存在"), "诺诺网平台错误提示");

        // 未适配平台的提示带域名
        Func<Uri, System.Threading.CancellationToken, System.Threading.Tasks.Task<FetchResult>> other = (u, ct) =>
            System.Threading.Tasks.Task.FromResult(new FetchResult { FinalUri = u, ContentType = "text/html", Body = System.Text.Encoding.UTF8.GetBytes("<div id=app></div>") });
        o = await new Downloader(other, post).FromText("https://fp.unknown-platform.cn/s/abc", dir, none);
        Eq(true, !o.Ok && o.Message.Contains("fp.unknown-platform.cn") && o.Message.Contains("尚未专门适配"), "未适配平台提示");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    /// <summary>更新日志：仓库 CHANGELOG.md 格式正确，当前版本已记录；软件内显示的文本转换正确。</summary>
    static void ChangelogTests(string repo)
    {
        string md = File.ReadAllText(Path.Combine(repo, "CHANGELOG.md"));
        var vers = System.Text.RegularExpressions.Regex.Matches(md, @"(?m)^## \[(\d+\.\d+\.\d+)\] - \d{4}-\d{2}-\d{2}\s*$").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToList();
        Eq(vers.Count, vers.Distinct().Count(), "更新日志版本号不重复");
        string csproj = File.ReadAllText(Path.Combine(repo, "src", "PiaoMate.csproj"));
        string cur = System.Text.RegularExpressions.Regex.Match(csproj, "<Version>([\\d.]+)</Version>").Groups[1].Value;
        Eq(true, vers.Contains(cur), "更新日志含当前版本 v" + cur);
        Eq(true, vers.Select(v => new Version(v)).SequenceEqual(vers.Select(v => new Version(v)).OrderByDescending(v => v)), "更新日志按版本从新到旧");
        Eq(1, System.Text.RegularExpressions.Regex.Matches(md, @"(?m)^## \[未发布\]").Count, "更新日志有一个「未发布」");

        string sample = "# 更新日志\n\n说明\n\n## [未发布]\n\n## [1.2.0] - 2026-01-02\n\n### 新增\n- 支持 `A`\n\n## [1.1.0] - 2026-01-01\n- 旧\n";
        Eq("v1.2.0 - 2026-01-02\r\n【新增】\r\n  · 支持 A\r\n\r\nv1.1.0 - 2026-01-01\r\n  · 旧", ChangelogForm.Text(sample), "更新日志显示文本");
        Eq(true, ChangelogForm.Text("## [未发布]\n### 修复\n- x\n").StartsWith("未发布"), "未发布有内容时显示");

        // 免责声明：嵌入 exe，首次启动须同意；同意的版本保存在配置中
        string dis = DisclaimerForm.Raw();
        Eq(File.ReadAllText(Path.Combine(repo, "docs", "免责声明.md"), System.Text.Encoding.UTF8).Replace("\r\n", "\n").TrimStart('﻿'), dis.Replace("\r\n", "\n").TrimStart('﻿'), "免责声明已嵌入 exe 且与 docs 一致");
        string disText = DisclaimerForm.Text(dis);
        Eq(true, disText.Contains("非真实发票") && disText.Contains("与本软件作者及贡献者无关"), "免责声明含关键内容");
        Eq(false, disText.Contains("**") || disText.Contains("`") || disText.Contains("# "), "免责声明去掉 Markdown 标记");
        Eq("1. 甲 乙\r\n\r\n2. 丙", DisclaimerForm.Text("# 标题\n\n1. **甲** `乙`\n2. 丙\n"), "免责声明显示文本");
        Eq(true, DisclaimerForm.NeedsConfirm(new Settings()), "未同意时需确认");
        string dcp = Path.Combine(Path.GetTempPath(), "fp_cfg_dis_" + Guid.NewGuid().ToString("N") + ".txt");
        new Settings { DisclaimerAccepted = DisclaimerForm.Version }.Save(dcp);
        Eq(false, DisclaimerForm.NeedsConfirm(Settings.Load(dcp)), "同意后不再确认");
        new Settings { DisclaimerAccepted = "0" }.Save(dcp);
        Eq(true, DisclaimerForm.NeedsConfirm(Settings.Load(dcp)), "免责声明版本更新后重新确认");
        File.Delete(dcp);
    }

    /// <summary>百望云：用虚构参数与模拟接口验证（不联网）。</summary>
    static async System.Threading.Tasks.Task BaiwangTests(string f1)
    {
        var none = System.Threading.CancellationToken.None;
        const string P = "0A1B2C3D4E5F60718293A4B5C6D7E8F9";
        var page = new Uri("https://pis.baiwang.com/smkp-vue/previewInvoiceAllEle?param=" + P);
        Eq(true, SiteAdapter.Find(page) is BaiwangAdapter, "识别百望云页面");
        Eq(null, SiteAdapter.Find(new Uri("https://pis.baiwang.com/smkp-vue/previewInvoiceAllEle")), "百望云无参数不匹配");
        Eq(P, BaiwangAdapter.Param(new Uri("https://pis.baiwang.com/smkp-vue/previewInvoiceAllEle?code=x&state=" + Uri.EscapeDataString("f=" + P + "&from=weChatCardPackage"))), "百望云从 state 取参数");

        byte[] pdf = File.ReadAllBytes(f1);
        string mode = "ok";
        var posts = new List<string>(); var gets = new List<string>();
        Func<Uri, System.Threading.CancellationToken, System.Threading.Tasks.Task<FetchResult>> get = (u, ct) =>
        {
            gets.Add(u.AbsoluteUri);
            FetchResult r;
            if (u.AbsoluteUri == "https://u.baiwang.com/sTEST")
                r = new FetchResult { FinalUri = page, ContentType = "text/html", Body = System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE html><title>扫码开票</title><div id=app></div>") };
            else if (u.AbsoluteUri == "https://fp.baiwang.com/format/d?d=TESTPDF" || (mode == "nourl" && u.AbsolutePath == "/bwmg/mix/bw/downloadFormat"))
                r = new FetchResult { FinalUri = u, ContentType = "application/pdf", Body = pdf };
            else throw new Exception("404");
            return System.Threading.Tasks.Task.FromResult(r);
        };
        Func<Uri, IDictionary<string, string>, Uri, System.Threading.CancellationToken, System.Threading.Tasks.Task<FetchResult>> post = (u, form, referer, ct) =>
        {
            posts.Add(u.AbsoluteUri + "|" + form[SiteContext.RawBodyKey]);
            string json = mode == "fail" ? "{\"success\":false,\"errorCode\":\"0\",\"message\":\"发票不存在\",\"data\":[null]}"
                : "{\"success\":true,\"total\":\"0\",\"data\":[{\"invoiceID\":\"X1\",\"buyerName\":\"测试公司\",\"invoiceTypeCode\":\"02\",\"eInvoiceUrl\":" +
                  (mode == "nourl" ? "null" : "\"https://fp.baiwang.com/format/d?d=TESTPDF\"") + "}]}";
            return System.Threading.Tasks.Task.FromResult(new FetchResult { FinalUri = u, ContentType = "application/json", Body = System.Text.Encoding.UTF8.GetBytes(json) });
        };
        string dir = Path.Combine(Path.GetTempPath(), "fp_bw_" + Guid.NewGuid().ToString("N"));
        var o = await new Downloader(get, post).FromText("https://u.baiwang.com/sTEST", dir, none);
        Eq(1, o.Pdfs.Count, "百望云自动下载PDF");
        Eq("https://pis.baiwang.com/bwmg/mix/bw/previewInvoiceQd|" + P, posts.FirstOrDefault(), "百望云接口与参数");

        mode = "nourl"; gets.Clear();
        o = await new Downloader(get, post).FromText("https://u.baiwang.com/sTEST", dir, none);
        Eq(1, o.Pdfs.Count, "百望云无地址时走下载按钮接口");
        Eq(true, gets.Any(g => g.Contains("downloadFormat?param=" + P + "&formatType=PDF")), "百望云下载接口参数");

        mode = "fail";
        o = await new Downloader(get, post).FromText("https://u.baiwang.com/sTEST", dir, none);
        Eq(true, !o.Ok && o.NeedsBrowser && o.Message.StartsWith("【百望云】") && o.Message.Contains("发票不存在"), "百望云平台错误提示");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    static void OrganizerTests(string f1, string f2)
    {
        var a = InvoiceParser.Parse(f1);
        Eq("示例科技有限公司_91110000MA00000001", Organizer.FolderName(a), "整理文件夹名");
        Eq("34.92_示例科技有限公司_91110000MA00000001_26110000000000000001_20260827.pdf", Organizer.FileName(a), "整理文件名");
        Eq("个人_张三", Organizer.FolderName(new Invoice { BuyerName = "个人/张三", Ok = true }), "无税号+非法字符");
        Eq("100.00_某公司_123.pdf", Organizer.FileName(new Invoice { Number = "123", BuyerName = "某公司", Amount = 100m, Ok = true }), "含发票号");
        Eq("100.00_某公司.pdf", Organizer.FileName(new Invoice { BuyerName = "某公司", Amount = 100m, Ok = true }), "无日期");

        string tmp = Path.Combine(Path.GetTempPath(), "fp_org_" + Guid.NewGuid().ToString("N"));
        string src = Path.Combine(tmp, "src"), arc = Path.Combine(tmp, "归档");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        string s1 = Path.Combine(src, "a.pdf"), s2 = Path.Combine(src, "sub", "b.pdf"), s3 = Path.Combine(src, "a_copy.pdf"), bad = Path.Combine(src, "坏.pdf");
        File.Copy(f1, s1); File.Copy(f2, s2); File.Copy(f1, s3); File.WriteAllText(bad, "not a pdf");
        var invs = new[] { s1, s2, s3, bad }.Select(InvoiceParser.Parse).ToList();
        var res = Organizer.Organize(invs, arc);
        string t1 = Path.Combine(arc, "示例科技有限公司_91110000MA00000001", "34.92_示例科技有限公司_91110000MA00000001_26110000000000000001_20260827.pdf");
        string t2 = Path.Combine(arc, "示例（北京）贸易有限公司_91110000MA00000002", "33.08_示例（北京）贸易有限公司_91110000MA00000002_26110000000000000002_20261111.pdf");
        Eq(true, File.Exists(t1) && File.Exists(t2), "整理后文件存在");
        Eq("已复制", res[0].Status, "状态已复制"); Eq("已存在相同文件", res[2].Status, "重复发票不再复制");
        Eq(true, res[2].Verified, "重复发票视为已归档");
        Eq(false, res[3].Verified, "无法识别不整理"); Eq(true, res[3].Status.StartsWith("跳过"), "无法识别跳过");
        Eq(1, Directory.GetFiles(Path.GetDirectoryName(t1), "*.pdf").Length, "重复只存一份");
        Eq(true, File.Exists(s1) && File.Exists(s2) && File.Exists(s3), "整理不改动原文件");

        // 同名但内容不同 → 加序号
        string other = Path.Combine(src, "other.pdf"); File.Copy(f1, other);
        File.AppendAllText(other, "\n% changed");
        var inv4 = InvoiceParser.Parse(other);
        var r4 = Organizer.Organize(new[] { inv4 }, arc);
        Eq(true, r4[0].Target.EndsWith("_20260827(2).pdf"), "同名不同内容加序号");

        // 源文件在整理后被修改 → 不删除
        File.AppendAllText(s3, "\n% modified after organize");
        int n = Organizer.DeleteSources(res, p => File.Delete(p));
        Eq(2, n, "只删除已校验的原文件");
        Eq(false, File.Exists(s1) || File.Exists(s2), "原文件已删除");
        Eq(true, File.Exists(s3) && File.Exists(bad), "被修改/未识别的不删");
        Eq(true, File.Exists(t1) && File.Exists(t2), "归档副本保留");
        Eq(0, Organizer.DeleteSources(res, p => File.Delete(p)), "重复删除为0");

        // 未识别放入 _未识别
        var r5 = Organizer.Organize(new[] { InvoiceParser.Parse(bad) }, arc, includeUnknown: true);
        Eq(true, r5[0].Verified && File.Exists(Path.Combine(arc, Organizer.UnknownFolder, "坏.pdf")), "未识别放入_未识别");
        Organizer.WriteIndex(res, DateTime.Now);
        Eq(true, File.ReadAllText(Path.Combine(Path.GetDirectoryName(t1), "清单.txt")).Contains("26110000000000000001"), "分类清单");
        Directory.Delete(tmp, true);
    }

    static System.Drawing.Bitmap Qr(string text, int size, int canvas = 0)
    {
        var w = new ZXing.BarcodeWriter { Format = ZXing.BarcodeFormat.QR_CODE, Options = new ZXing.Common.EncodingOptions { Width = size, Height = size, Margin = 2 } };
        var q = w.Write(text);
        if (canvas == 0) return q;
        var b = new System.Drawing.Bitmap(canvas, canvas * 2 / 3);
        using (var g = System.Drawing.Graphics.FromImage(b))
        {
            g.Clear(System.Drawing.Color.WhiteSmoke);
            g.DrawString("截图里的其他文字 发票信息", new System.Drawing.Font("Microsoft YaHei", 18), System.Drawing.Brushes.Black, 10, 10);
            g.DrawImage(q, canvas / 2, canvas / 4);
        }
        q.Dispose();
        return b;
    }

    static byte[] Png(System.Drawing.Bitmap b) { using (var ms = new MemoryStream()) { b.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ms.ToArray(); } }

    static async System.Threading.Tasks.Task DownloaderTests(string f1)
    {
        var none = System.Threading.CancellationToken.None;
        Eq(2, Downloader.ExtractUrls("链接：https://a.com/x?id=1，另一个 http://b.cn/p.pdf。").Count, "提取网址");
        Eq("https://a.com/x?id=1", Downloader.ExtractUrls("链接：https://a.com/x?id=1，另一个")[0], "网址去掉中文标点");
        string vq = Downloader.DescribeVerifyQr("01,32,,26110000000000000001,34.92,20260827,,1A2B,");
        Eq(true, vq != null && vq.Contains("26110000000000000001") && vq.Contains("34.92") && vq.Contains("2026-08-27"), "查验二维码说明");
        Eq(null, Downloader.DescribeVerifyQr("https://a.com"), "网址不是查验码");

        var baseUri = new Uri("https://fp.example.com/h5/view?id=9");
        var c = Downloader.ExtractCandidates("<link href='/s.css'><a href=\"/api/download?id=9&amp;type=pdf\">下载PDF</a><a href='/about'>关于</a>", baseUri);
        Eq(1, c.Count, "候选只含下载链接");
        Eq("https://fp.example.com/api/download?id=9&type=pdf", c.Count > 0 ? c[0].AbsoluteUri : "", "相对链接+&amp;");
        c = Downloader.ExtractCandidates("<meta http-equiv=\"refresh\" content=\"0;url=https://x.cn/inv/abc\">", baseUri);
        Eq("https://x.cn/inv/abc", c.Count > 0 ? c[0].AbsoluteUri : "", "meta refresh");
        c = Downloader.ExtractCandidates("<script>window.location.href='/m/inv?c=1';var u=\"https:\\/\\/cdn.x.cn\\/f\\/1.pdf\";</script>", baseUri);
        Eq("https://cdn.x.cn/f/1.pdf", c.Count > 0 ? c[0].AbsoluteUri : "", "JSON转义的pdf优先");
        Eq(true, c.Any(u => u.AbsoluteUri == "https://fp.example.com/m/inv?c=1"), "location.href");

        // 二维码识别
        using (var b = Qr("https://fp.example.com/s/AbC123", 300)) Eq("https://fp.example.com/s/AbC123", QrDecoder.Decode(b).FirstOrDefault(), "二维码识别");
        using (var b = Qr("https://fp.example.com/s/small", 120, 1500)) Eq("https://fp.example.com/s/small", QrDecoder.Decode(b).FirstOrDefault(), "截图中的小二维码");
        using (var b = new System.Drawing.Bitmap(200, 200)) Eq(0, QrDecoder.Decode(b).Count, "空白图无二维码");

        // 模拟网络：短链 → 跳转后的 H5 页面 → pdf 链接
        byte[] pdf = File.ReadAllBytes(f1);
        byte[] qrPng; using (var b = Qr("https://s.example.com/q/1", 240)) qrPng = Png(b);
        var calls = new List<string>();
        Func<Uri, System.Threading.CancellationToken, System.Threading.Tasks.Task<FetchResult>> fake = (u, ct) =>
        {
            calls.Add(u.AbsoluteUri);
            FetchResult r;
            switch (u.AbsoluteUri)
            {
                case "https://s.example.com/q/1":
                    r = new FetchResult { FinalUri = new Uri("https://fp.example.com/h5/view?id=9"), ContentType = "text/html; charset=utf-8",
                        Body = System.Text.Encoding.UTF8.GetBytes("<html><a href='/css/a.css'></a><a class='btn' href=\"/api/file?id=9&fmt=pdf\">下载</a><a href='/api/file?id=9&fmt=ofd'>OFD</a></html>") }; break;
                case "https://fp.example.com/api/file?id=9&fmt=pdf":
                    r = new FetchResult { FinalUri = u, ContentType = "application/octet-stream", FileName = "发票9.pdf", Body = pdf }; break;
                case "https://img.example.com/qr.png":
                    r = new FetchResult { FinalUri = u, ContentType = "image/png", Body = qrPng }; break;
                case "https://wx.example.com/need-wechat":
                    r = new FetchResult { FinalUri = u, ContentType = "text/html", Body = System.Text.Encoding.UTF8.GetBytes("<html>请在微信客户端打开链接</html>") }; break;
                default: throw new Exception("404");
            }
            return System.Threading.Tasks.Task.FromResult(r);
        };
        var dl = new Downloader(fake);
        string dir = Path.Combine(Path.GetTempPath(), "fp_dl_" + Guid.NewGuid().ToString("N"));
        var o = await dl.FromText("https://s.example.com/q/1", dir, none);
        Eq(1, o.Pdfs.Count, "跟随页面下载pdf");
        Eq(true, o.Pdfs.Count == 1 && Path.GetFileName(o.Pdfs[0]) == "发票9.pdf" && File.ReadAllBytes(o.Pdfs[0]).Length == pdf.Length, "pdf保存");
        Eq(false, calls.Any(x => x.Contains(".css") || x.Contains("ofd")), "不访问css/ofd");

        o = await new Downloader(fake).FromText("看图 https://img.example.com/qr.png", dir, none);
        Eq(1, o.Pdfs.Count, "图片链接→二维码→pdf");

        using (var b = Qr("https://s.example.com/q/1", 200, 1200)) o = await new Downloader(fake).FromImage(b, dir, none);
        Eq(1, o.Pdfs.Count, "粘贴截图→下载");

        o = await dl.FromText("https://wx.example.com/need-wechat", dir, none);
        Eq(false, o.Ok, "需手动下载"); Eq("https://wx.example.com/need-wechat", o.OpenUrl, "提供浏览器链接"); Eq(true, o.Message.Contains("手动"), "手动提示");
        o = await dl.FromText("https://none.example.com/x", dir, none);
        Eq(true, !o.Ok && o.Message.Contains("404"), "访问失败提示");
        using (var b = Qr("01,32,,26110000000000000001,34.92,20260827,,1A2B,", 250)) o = await dl.FromImage(b, dir, none);
        Eq(true, !o.Ok && o.Message.Contains("查验二维码"), "查验二维码提示");

        // 下载后自动整理
        var res = Organizer.Organize(new[] { InvoiceParser.Parse(Directory.GetFiles(dir, "发票9.pdf")[0]) }, Path.Combine(dir, "归档"), true);
        Eq(true, res[0].Verified && res[0].Target.EndsWith("34.92_示例科技有限公司_91110000MA00000001_26110000000000000001_20260827.pdf"), "下载后整理");
        Directory.Delete(dir, true);
    }

    /// <summary>主窗口列顺序：默认顺序、名称、调整与持久化。</summary>
    static void ColumnOrderTests()
    {
        var lv = new System.Windows.Forms.ListView { View = System.Windows.Forms.View.Details, AllowColumnReorder = true };
        lv.Columns.Add(new System.Windows.Forms.ColumnHeader { Name = "chk", Text = "" });
        lv.Columns.Add(new System.Windows.Forms.ColumnHeader { Name = "number", Text = "发票号" });
        lv.Columns.Add(new System.Windows.Forms.ColumnHeader { Name = "amount", Text = "发票金额" });
        lv.Columns.Add(new System.Windows.Forms.ColumnHeader { Name = "date", Text = "开票日期" });
        lv.Columns.Add(new System.Windows.Forms.ColumnHeader { Name = "buyer", Text = "公司抬头(购买方)" });
        lv.Columns.Add(new System.Windows.Forms.ColumnHeader { Name = "tax", Text = "税号" });
        lv.Columns.Add(new System.Windows.Forms.ColumnHeader { Name = "dir", Text = "所在目录" });
        lv.Columns.Add(new System.Windows.Forms.ColumnHeader { Name = "item", Text = "项目名称" });
        lv.Columns.Add(new System.Windows.Forms.ColumnHeader { Name = "file", Text = "文件名 / 状态" });
        Eq("number,amount,date,buyer,tax,dir,item,file", MainForm.GetColumnOrder(lv), "默认列顺序");
        MainForm.ApplyColumnOrder(lv, "date,number,amount,unknown_col");
        Eq(0, lv.Columns[0].DisplayIndex, "勾选列固定首位");
        Eq("date,number,amount,buyer,tax,dir,item,file", MainForm.GetColumnOrder(lv), "恢复列顺序并补齐剩余列");
        lv.Dispose();
    }

    /// <summary>导入导出记录、14个月保留期、性能与去重拦截。</summary>
    static void HistoryAndDedupTests(string f1, string f2)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "fp_hist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        string hp = Path.Combine(tmp, "history.tsv");
        DateTime now = new DateTime(2026, 11, 26, 10, 0, 0);
        var hist = new InvoiceHistory(hp, now);
        var a = InvoiceParser.Parse(f1);
        var b = InvoiceParser.Parse(f2);

        // 1. 记录导入与过期清理（14 个月）
        hist.RecordImport(a, "整理", Organizer.Hash(f1), now.AddMonths(-15)); // 超过 14 个月
        hist.RecordImport(b, "整理", Organizer.Hash(f2), now.AddMonths(-13)); // 14 个月内
        var hist2 = new InvoiceHistory(hp, now);
        Eq(null, hist2.FindImport(a), "超过14个月的记录自动清理");
        Eq(true, hist2.FindImport(b) != null, "14个月内的记录保留");
        Eq(1, hist2.Count, "过期清理后条数");
        hist2.ReloadIfChanged(now.AddMonths(2));
        Eq(0, hist2.Count, "运行期间跨过保留期后清理记录");

        // 2. 整理去重与已导出拦截（即使本地已无归档文件）
        string src = Path.Combine(tmp, "src"), arc = Path.Combine(tmp, "arc");
        Directory.CreateDirectory(src);
        string s1 = Path.Combine(src, "a1.pdf"), s1dup = Path.Combine(src, "a1_dup.pdf");
        File.Copy(f1, s1); File.Copy(f1, s1dup);
        var h3 = new InvoiceHistory(Path.Combine(tmp, "h3.tsv"), now);
        var r1 = Organizer.Organize(new[] { InvoiceParser.Parse(s1) }, arc, false, h3, "整理");
        Eq("已复制", r1[0].Status, "首次整理成功");
        Eq(true, h3.FindImport(a) != null, "整理后写入导入记录");

        // 删除归档文件，再次导入同发票号应被拦截
        File.Delete(r1[0].Target);
        var r2 = Organizer.Organize(new[] { InvoiceParser.Parse(s1dup) }, arc, false, h3, "整理");
        Eq(true, r2[0].Blocked && !r2[0].Verified && r2[0].Status.Contains("导入过"), "重复导入拦截");

        // 导出记录拦截再次导入
        h3.RecordExport(new[] { b }, "20261126_100000", now);
        string s2 = Path.Combine(src, "b1.pdf"); File.Copy(f2, s2);
        var r3 = Organizer.Organize(new[] { InvoiceParser.Parse(s2) }, arc, false, h3, "下载");
        Eq(true, r3[0].Blocked && !r3[0].Verified && r3[0].Status.Contains("导出过"), "已导出发票禁止再次导入");

        // 3. 匹配候选过滤：排除已导出 & 列表内同发票号去重
        var aDup = InvoiceParser.Parse(s1dup);
        int skExp, skDup;
        var cands = MainForm.FilterMatchCandidates(new[] { a, aDup, b }, h3, out skExp, out skDup);
        Eq(1, cands.Count, "匹配候选去重并排除已导出");
        Eq(1, skExp, "排除已导出计数");
        Eq(1, skDup, "列表重复去重计数");

        var noNumber = new Invoice { Path = s1, FileName = "a1.pdf", Ok = true };
        var noNumberCopy = new Invoice { Path = s1dup, FileName = "a1_dup.pdf", Ok = true };
        var hashHistory = new InvoiceHistory(Path.Combine(tmp, "hash.tsv"), now);
        cands = MainForm.FilterMatchCandidates(new[] { noNumber, noNumberCopy }, hashHistory, out skExp, out skDup);
        Eq(1, cands.Count, "无发票号的相同文件只匹配一次");
        hashHistory.RecordExport(new[] { noNumber }, "导出", now);
        cands = MainForm.FilterMatchCandidates(new[] { noNumberCopy }, hashHistory, out skExp, out skDup);
        Eq(0, cands.Count, "无发票号的已导出文件按哈希排除");

        // 4. 性能：5000 条记录写入、加载与查重应在 1 秒内
        string perfPath = Path.Combine(tmp, "perf.tsv");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var perfHist = new InvoiceHistory(perfPath, now);
        var batch = new List<Tuple<Invoice, string>>();
        for (int i = 0; i < 5000; i++)
            batch.Add(Tuple.Create(new Invoice { Number = "261100000000000" + i.ToString("D5"), Amount = 10m + i * 0.01m, BuyerName = "示例公司", BuyerTaxId = "91110000MA00000001", Date = now.Date, Ok = true }, ""));
        perfHist.RecordImports(batch, "批量", now);
        var perfLoad = new InvoiceHistory(perfPath, now);
        Eq(5000, perfLoad.Count, "5000条记录加载完整");
        Eq(true, perfLoad.FindImportByNumber("26110000000000004999") != null, "5000条O(1)查重命中");
        sw.Stop();
        Eq(true, sw.ElapsedMilliseconds < 2000, "5000条读写查重耗时可控(" + sw.ElapsedMilliseconds + "ms)");

        Directory.Delete(tmp, true);
    }
}








