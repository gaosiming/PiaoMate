using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PiaoMate
{
    /// <summary>适配器可用的网络能力（与下载器共用 Cookie）。</summary>
    public class SiteContext
    {
        public Func<Uri, IDictionary<string, string>, Uri, CancellationToken, Task<FetchResult>> Post;
        public CancellationToken Ct;
        /// <summary>表单中含此键时，以其值作为原始 JSON 请求体发送（而不是表单编码）。</summary>
        public const string RawBodyKey = "\0raw";
    }

    public class SiteResult
    {
        /// <summary>要下载的发票 PDF 地址。</summary>
        public readonly List<Uri> Files = new List<Uri>();
        /// <summary>未拿到 PDF 时的说明。</summary>
        public string Message = "";
        /// <summary>需要在浏览器中继续操作（输入验证码、填写开票信息等）。</summary>
        public bool NeedsBrowser;
    }

    /// <summary>
    /// 开票平台适配器：识别某家平台的发票页面网址，调用其接口直接取得 PDF 地址。
    /// 新增平台：继承本类并加入 <see cref="All"/>。
    /// </summary>
    public abstract class SiteAdapter
    {
        public abstract string Name { get; }
        /// <summary>是否为本平台的发票页面（参数为跟随跳转后的最终网址）。</summary>
        public abstract bool CanHandle(Uri page);
        public abstract Task<SiteResult> Resolve(Uri page, SiteContext ctx);

        public static readonly List<SiteAdapter> All = new List<SiteAdapter> { new NuonuoAdapter(), new BaiwangAdapter() };

        public static SiteAdapter Find(Uri page) { return All.FirstOrDefault(a => a.CanHandle(page)); }

        // ---------- 工具 ----------
        public static Dictionary<string, string> Query(Uri u)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in u.Query.TrimStart('?').Split('&'))
            {
                if (part.Length == 0) continue;
                int i = part.IndexOf('=');
                string k = i < 0 ? part : part.Substring(0, i), v = i < 0 ? "" : part.Substring(i + 1);
                try { d[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' ')); } catch { }
            }
            return d;
        }

        public static object ParseJson(byte[] body)
        {
            string s = Encoding.UTF8.GetString(body).TrimStart('﻿');
            return new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(s);
        }

        /// <summary>按 a.b.c 路径取 JSON 中的值。</summary>
        public static object Get(object json, string path)
        {
            object cur = json;
            foreach (var k in path.Split('.'))
            {
                var d = cur as IDictionary<string, object>;
                if (d == null || !d.TryGetValue(k, out cur)) return null;
            }
            return cur;
        }

        public static string Str(object json, string path)
        {
            var v = Get(json, path);
            return v == null ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        static readonly Regex PdfUrl = new Regex(@"^https?://[^\s?#]+\.pdf([?#].*)?$", RegexOptions.IgnoreCase);

        /// <summary>递归收集 JSON 中所有指向 .pdf 的地址（兜底，兼容一单多票等结构）。</summary>
        public static List<string> FindPdfUrls(object json)
        {
            var res = new List<string>();
            Action<object> walk = null;
            walk = o =>
            {
                var d = o as IDictionary<string, object>;
                if (d != null) { foreach (var v in d.Values) walk(v); return; }
                var s = o as string;
                if (s != null) { if (PdfUrl.IsMatch(s) && !res.Contains(s)) res.Add(s); return; }
                var e = o as IEnumerable;
                if (e != null) foreach (var v in e) walk(v);
            };
            walk(json);
            return res;
        }
    }

    /// <summary>
    /// 诺诺网（nnfp.jss.com.cn）：短链 → /scan-invoice/printQrcode?paramList=销方税号!!!单号!false，
    /// 页面通过 POST /scan2/getIvcDetailShow.do 取得发票信息，其中 data.invoiceSimpleVo.url 为 PDF 地址。
    /// </summary>
    public class NuonuoAdapter : SiteAdapter
    {
        public override string Name { get { return "诺诺网"; } }

        public override bool CanHandle(Uri page)
        {
            string h = page.Host.ToLowerInvariant();
            if (!(h.EndsWith("jss.com.cn") || h.EndsWith("nuonuo.com"))) return false;
            return page.AbsolutePath.IndexOf("printQrcode", StringComparison.OrdinalIgnoreCase) >= 0 && Query(page).ContainsKey("paramList");
        }

        public override async Task<SiteResult> Resolve(Uri page, SiteContext ctx)
        {
            var q = Query(page);
            var res = new SiteResult();
            string api = q.ContainsKey("isOuterPageReq") && q["isOuterPageReq"] == "true" ? "/invoice/scan/IvcDetail.do" : "/scan2/getIvcDetailShow.do";
            var form = new Dictionary<string, string> { { "paramList", q["paramList"] } };
            foreach (var k in new[] { "code", "aliView", "shortLinkSource" })
                if (q.ContainsKey(k)) form[k] = q[k];
            var r = await ctx.Post(new Uri(page, api), form, page, ctx.Ct);
            object json;
            try { json = ParseJson(r.Body); }
            catch { res.Message = "平台接口返回的不是有效数据"; res.NeedsBrowser = true; return res; }

            if (Str(json, "status") != "0000")
            {
                string msg = Str(json, "msg");
                res.Message = "平台提示：" + (msg.Length > 0 ? msg : "查询失败（" + Str(json, "status") + "）");
                res.NeedsBrowser = true;
                return res;
            }
            switch (Str(json, "routeView"))
            {
                case "INVOICE_TEMPLATE_VIEW":
                case "DELIVERY_CHANNEL_MIDDLE_VIEW":
                case "":
                    var urls = new List<string>();
                    foreach (var p in new[] { "data.invoiceSimpleVo.url", "data.medicalInvociePdfUrl" })
                    {
                        string u = Str(json, p);
                        if (u.Length > 0 && !urls.Contains(u)) urls.Add(u);
                    }
                    foreach (var u in FindPdfUrls(Get(json, "data"))) if (!urls.Contains(u)) urls.Add(u);
                    foreach (var u in urls) { Uri x; if (Uri.TryCreate(u, UriKind.Absolute, out x)) res.Files.Add(x); }
                    if (res.Files.Count == 0)
                    {
                        res.Message = Str(json, "data.invoiceSimpleVo.ofdDownloadUrl").Length > 0
                            ? "该发票只提供 OFD 格式，未提供 PDF" : "发票信息中没有 PDF 地址";
                        res.NeedsBrowser = true;
                    }
                    break;
                case "VALID_CODE_VIEW":
                    res.Message = "需要输入提取码/验证码才能查看发票"; res.NeedsBrowser = true; break;
                case "VALID_BUYER_NAME_VIEW":
                    res.Message = "需要验证购买方名称才能查看发票"; res.NeedsBrowser = true; break;
                case "INVOICE_STATUS_VIEW":
                    res.Message = "发票尚未开具完成（开具中、待开票或开票失败）"; res.NeedsBrowser = true; break;
                default:
                    res.Message = "未识别的页面类型（" + Str(json, "routeView") + "）"; res.NeedsBrowser = true; break;
            }
            return res;
        }
    }

    /// <summary>
    /// 百望云（pis.baiwang.com）：扫码开票 H5 页 /smkp-vue/previewInvoice…?param=加密参数，
    /// 页面以 param 为 JSON 请求体 POST /bwmg/mix/bw/previewInvoiceQd，返回 data[].eInvoiceUrl 为 PDF 地址；
    /// 下载按钮为 GET /bwmg/mix/bw/downloadFormat?param=…&amp;formatType=PDF。natural=00（自然人代开）走 /bwmg/nature-dlg/web/dlg/invoice/。
    /// </summary>
    public class BaiwangAdapter : SiteAdapter
    {
        public override string Name { get { return "百望云"; } }

        public override bool CanHandle(Uri page)
        {
            string h = page.Host.ToLowerInvariant();
            if (!h.EndsWith("baiwang.com")) return false;
            return page.AbsolutePath.IndexOf("/smkp-vue/", StringComparison.OrdinalIgnoreCase) >= 0 && Param(page).Length > 0;
        }

        /// <summary>发票参数：查询串 param，或微信授权回跳时 state 中的 f / param。</summary>
        public static string Param(Uri page)
        {
            var q = Query(page);
            string state;
            if (q.TryGetValue("state", out state) && state.Length > 0)
            {
                var s = Query(new Uri("http://x/?" + state));
                foreach (var k in new[] { "f", "param" })
                    if (s.ContainsKey(k) && s[k].Length > 0) return s[k];
            }
            return q.ContainsKey("param") ? q["param"] : "";
        }

        public override async Task<SiteResult> Resolve(Uri page, SiteContext ctx)
        {
            var q = Query(page);
            string param = Param(page);
            bool natural = q.ContainsKey("natural") && q["natural"] == "00";
            string basePath = natural ? "/bwmg/nature-dlg/web/dlg/invoice/" : "/bwmg/mix/bw/";
            var res = new SiteResult();
            var download = new Uri(page, basePath + "downloadFormat?param=" + Uri.EscapeDataString(param) + "&formatType=PDF");

            object json = null;
            try
            {
                var r = await ctx.Post(new Uri(page, basePath + "previewInvoiceQd"),
                    new Dictionary<string, string> { { SiteContext.RawBodyKey, param } }, page, ctx.Ct);
                json = ParseJson(r.Body);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            // 预览接口不可用时，仍尝试页面上的「下载PDF文件」
            if (json == null) { res.Files.Add(download); res.Message = "平台接口不可用"; res.NeedsBrowser = true; return res; }

            if (!(Get(json, "success") is bool && (bool)Get(json, "success")))
            {
                string msg = Str(json, "message");
                if (msg.Contains("解密") || msg.Contains("AESUtils")) msg = "发票链接参数无效（链接不完整或已失效）";
                res.Message = "平台提示：" + (msg.Length > 0 ? msg : "查询发票失败");
                res.NeedsBrowser = true;
                return res;
            }
            var urls = new List<string>();
            var list = Get(json, "data") as IEnumerable;
            if (list != null && !(list is IDictionary<string, object>))
                foreach (var item in list)
                    foreach (var k in new[] { "eInvoiceUrl", "einvoiceUrl" })
                    {
                        string u = Str(item, k);
                        if (u.Length > 0 && !urls.Contains(u)) urls.Add(u);
                    }
            foreach (var u in urls) { Uri x; if (Uri.TryCreate(page, u, out x) && (x.Scheme == "http" || x.Scheme == "https")) res.Files.Add(x); }
            if (res.Files.Count == 0) res.Files.Add(download);
            res.Message = "未能下载发票 PDF（可能需要先同意授权或发票仍在开具中）";
            res.NeedsBrowser = true;
            return res;
        }
    }
}
