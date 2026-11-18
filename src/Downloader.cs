using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PiaoMate
{
    public class FetchResult
    {
        public Uri FinalUri;
        public string ContentType = "";
        public string FileName = "";
        public byte[] Body = new byte[0];
    }

    public class DownloadOutcome
    {
        public readonly List<string> Pdfs = new List<string>();
        public string Message = "";
        /// <summary>无法自动下载时，供用户在浏览器中手动打开的链接。</summary>
        public string OpenUrl = "";
        public bool Ok { get { return Pdfs.Count > 0; } }
        /// <summary>链接打开的是网页但未找到 PDF（多为需要填写抬头等信息的扫码开票页），可在内置浏览器中继续。</summary>
        public bool NeedsBrowser;
    }

    /// <summary>
    /// 发票下载：输入网址或二维码内容，自动跟随重定向/页面中的 PDF 链接下载发票。
    /// 网络访问通过 fetch 注入，便于离线测试。
    /// </summary>
    public class Downloader
    {
        const int MaxDepth = 4, MaxFetches = 16;
        const long MaxBytes = 40L * 1024 * 1024;
        readonly Func<Uri, CancellationToken, Task<FetchResult>> fetch;
        readonly Func<Uri, IDictionary<string, string>, Uri, CancellationToken, Task<FetchResult>> post;

        /// <param name="fetch">GET 请求（测试时注入）。</param>
        /// <param name="post">表单 POST 请求，供平台适配器调用开票平台接口（测试时注入）。</param>
        public Downloader(Func<Uri, CancellationToken, Task<FetchResult>> fetch = null,
                          Func<Uri, IDictionary<string, string>, Uri, CancellationToken, Task<FetchResult>> post = null)
        {
            HttpFetcher http = null;
            if (fetch == null || post == null) http = new HttpFetcher();
            this.fetch = fetch ?? http.Fetch;
            this.post = post ?? http.Post;
        }

        static readonly Regex UrlRe = new Regex(@"https?://[^\s""'<>，。；、）)\]]+", RegexOptions.IgnoreCase);
        // 全电/增值税发票查验二维码：01,发票种类,代码,号码,金额,日期,校验码,...
        static readonly Regex VerifyQrRe = new Regex(@"^\s*01,(\d{2}),([^,]*),(\w+),([\d.]*),(\d{8})");

        public static List<string> ExtractUrls(string text)
        {
            if (string.IsNullOrEmpty(text)) return new List<string>();
            return UrlRe.Matches(text).Cast<Match>().Select(m => m.Value.TrimEnd('.', ',', ';')).Distinct().ToList();
        }

        /// <summary>识别发票查验二维码内容，返回说明文字；不是则返回 null。</summary>
        public static string DescribeVerifyQr(string text)
        {
            var m = VerifyQrRe.Match(text ?? "");
            if (!m.Success) return null;
            string date = m.Groups[5].Value;
            try { date = DateTime.ParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture).ToString("yyyy-MM-dd"); } catch { }
            return "这是发票查验二维码（发票号码 " + m.Groups[3].Value + "，金额 " + m.Groups[4].Value + " 元，开票日期 " + date +
                   "），其中不含下载地址，无法自动下载。请向开票方索取 PDF，或登录电子税务局/个人所得税App等渠道下载后拖入本窗口。";
        }

        /// <summary>处理一段文字（网址或二维码内容）。</summary>
        public async Task<DownloadOutcome> FromText(string text, string saveDir, CancellationToken ct)
        {
            var o = new DownloadOutcome();
            var urls = ExtractUrls(text);
            if (urls.Count == 0)
            {
                o.Message = DescribeVerifyQr(text) ?? ("内容不是网址，无法下载：" + Shorten(text, 80));
                return o;
            }
            var st = new State { SaveDir = saveDir, Ct = ct };
            foreach (var u in urls)
            {
                Uri uri;
                if (!Uri.TryCreate(u, UriKind.Absolute, out uri)) continue;
                if (o.OpenUrl.Length == 0) o.OpenUrl = u;
                try { await Visit(uri, 0, st, o, true); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { st.Errors.Add(ex.Message); }
            }
            if (!o.Ok) { o.Message = FailMessage(st); o.NeedsBrowser = st.Site != null ? st.SiteNeedsBrowser : (st.SawHtml && !st.SawOfd); }
            else o.Message = "已下载 " + o.Pdfs.Count + " 个 PDF";
            return o;
        }

        /// <summary>识别图片中的二维码并下载。</summary>
        public async Task<DownloadOutcome> FromImage(Bitmap bmp, string saveDir, CancellationToken ct)
        {
            var codes = QrDecoder.Decode(bmp);
            if (codes.Count == 0) return new DownloadOutcome { Message = "图片中未识别到二维码，请截取更清晰、完整的二维码后重试。" };
            var o = new DownloadOutcome();
            var msgs = new List<string>();
            foreach (var c in codes)
            {
                var r = await FromText(c, saveDir, ct);
                o.Pdfs.AddRange(r.Pdfs);
                if (o.OpenUrl.Length == 0 || (r.NeedsBrowser && !o.NeedsBrowser)) o.OpenUrl = r.OpenUrl;
                if (r.NeedsBrowser) o.NeedsBrowser = true;
                if (!r.Ok) msgs.Add(r.Message);
            }
            o.Message = o.Ok ? "已下载 " + o.Pdfs.Count + " 个 PDF" : string.Join("；", msgs.Distinct());
            return o;
        }

        class State
        {
            public string SaveDir; public CancellationToken Ct;
            public int Fetches; public bool SawOfd, SawHtml, SawImageNoQr;
            /// <summary>命中的平台适配器名称及其结果说明。</summary>
            public string Site, SiteMessage = "", HtmlHost = "";
            public bool SiteNeedsBrowser;
            public readonly HashSet<string> Visited = new HashSet<string>();
            public readonly List<string> Errors = new List<string>();
        }

        static string FailMessage(State st)
        {
            string m;
            if (st.Site != null)
                return "【" + st.Site + "】" + st.SiteMessage.TrimEnd('。') + "。" +
                       (st.SiteNeedsBrowser ? "可双击此行在内置浏览器中继续，或点「在浏览器打开」手动下载。" : "请点「在浏览器打开」手动下载，下载后把 PDF 拖入本窗口即可自动整理。");
            if (st.SawOfd) m = "页面只提供了 OFD/XML 等格式，未找到 PDF。";
            else if (st.SawImageNoQr) m = "链接是图片，但其中未识别到二维码。";
            else if (st.SawHtml) m = "未能在页面中自动找到 PDF 下载地址（可能需要手动点击、输入验证码或在微信/支付宝中打开）。" +
                                     (st.HtmlHost.Length > 0 ? "该开票平台（" + st.HtmlHost + "）尚未专门适配，可把链接发给开发者适配。" : "");
            else if (st.Errors.Count > 0) m = "访问失败：" + st.Errors[0].TrimEnd('。') + "。";
            else m = "未能下载。";
            return m + "请点「在浏览器打开」手动下载，下载后把 PDF 拖入本窗口即可自动整理。";
        }

        async Task Visit(Uri uri, int depth, State st, DownloadOutcome o, bool top)
        {
            st.Ct.ThrowIfCancellationRequested();
            if (depth > MaxDepth || st.Fetches >= MaxFetches) return;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
            if (!st.Visited.Add(uri.AbsoluteUri)) return;
            st.Fetches++;
            FetchResult r;
            try { r = await fetch(uri, st.Ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { st.Errors.Add(Unwrap(ex)); return; }
            if (r == null || r.Body == null) return;
            if (r.FinalUri != null) st.Visited.Add(r.FinalUri.AbsoluteUri);
            Uri baseUri = r.FinalUri ?? uri;

            if (IsPdf(r.Body))
            {
                o.Pdfs.Add(Save(r, baseUri, st.SaveDir));
                return;
            }
            if (IsImage(r))
            {
                List<string> codes;
                try { using (var ms = new MemoryStream(r.Body)) using (var bmp = new Bitmap(ms)) codes = QrDecoder.Decode(bmp); }
                catch { codes = new List<string>(); }
                if (codes.Count == 0) { st.SawImageNoQr = true; return; }
                foreach (var c in codes)
                {
                    var urls = ExtractUrls(c);
                    if (urls.Count == 0) { string d = DescribeVerifyQr(c); if (d != null) st.Errors.Insert(0, d); continue; }
                    foreach (var u in urls) { Uri nu; if (Uri.TryCreate(u, UriKind.Absolute, out nu)) await Visit(nu, depth + 1, st, o, false); }
                }
                return;
            }
            string ct = r.ContentType.ToLowerInvariant();
            if (Regex.IsMatch(ct, "ofd|xml") && !ct.Contains("html")) { st.SawOfd = true; return; }
            if (r.Body.Length > 4 * 1024 * 1024) return;
            st.SawHtml = true;
            if (st.HtmlHost.Length == 0) st.HtmlHost = baseUri.Host;

            // 已适配的开票平台：调用其接口获取 PDF 地址
            var site = SiteAdapter.Find(baseUri);
            if (site != null)
            {
                st.Site = site.Name;
                SiteResult sr;
                try { sr = await site.Resolve(baseUri, new SiteContext { Post = post, Ct = st.Ct }); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { st.SiteMessage = "解析开票平台数据失败：" + Unwrap(ex); st.SiteNeedsBrowser = true; return; }
                int got = o.Pdfs.Count;
                foreach (var f in sr.Files) await Visit(f, depth + 1, st, o, false);
                if (o.Pdfs.Count > got) return;
                st.SiteMessage = sr.Message.Length > 0 ? sr.Message : "平台返回的 PDF 地址无法下载";
                st.SiteNeedsBrowser = sr.NeedsBrowser;
                return;
            }
            string html = DecodeText(r.Body, r.ContentType);
            int before = o.Pdfs.Count;
            foreach (var c in ExtractCandidates(html, baseUri))
            {
                if (IsOfd(c.AbsoluteUri)) { st.SawOfd = true; continue; }
                await Visit(c, depth + 1, st, o, false);
                // 已从此页面拿到 PDF 就不再尝试其余候选，避免下载重复/无关文件
                if (o.Pdfs.Count > before) return;
            }
        }

        static string Unwrap(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }

        public static bool IsPdf(byte[] b)
        {
            if (b == null || b.Length < 5) return false;
            int lim = Math.Min(b.Length - 4, 1024);
            for (int i = 0; i < lim; i++)
                if (b[i] == '%' && b[i + 1] == 'P' && b[i + 2] == 'D' && b[i + 3] == 'F' && b[i + 4] == '-') return true;
            return false;
        }

        static bool IsImage(FetchResult r)
        {
            var b = r.Body;
            if (r.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return true;
            if (b.Length < 4) return false;
            return (b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') || (b[0] == 0xFF && b[1] == 0xD8) ||
                   (b[0] == 'G' && b[1] == 'I' && b[2] == 'F') || (b[0] == 'B' && b[1] == 'M');
        }

        static bool IsOfd(string u) { return Regex.IsMatch(u, @"\.(ofd|xml)(\?|$)|[?&](type|format|fileType)=(ofd|xml)\b", RegexOptions.IgnoreCase); }

        static readonly Regex SkipRe = new Regex(@"\.(css|js|png|jpe?g|gif|ico|svg|webp|woff2?|ttf|eot|map|mp3|mp4)(\?|#|$)", RegexOptions.IgnoreCase);

        /// <summary>从 HTML/JSON 文本中提取可能指向 PDF 的链接，按可能性排序。</summary>
        public static List<Uri> ExtractCandidates(string html, Uri baseUri)
        {
            var scored = new List<KeyValuePair<Uri, int>>();
            var seen = new HashSet<string>();
            string text = (html ?? "").Replace("\\/", "/").Replace("\\u002F", "/").Replace("\\u002f", "/").Replace("&amp;", "&");
            Action<string, int> add = (raw, bonus) =>
            {
                raw = (raw ?? "").Trim();
                if (raw.Length == 0 || raw.StartsWith("#") || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
                if (raw.Contains("{{") || raw.Contains("${") || raw.Contains("+")) { if (!raw.StartsWith("http")) return; }
                Uri u;
                if (!Uri.TryCreate(baseUri, raw, out u)) return;
                if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return;
                if (SkipRe.IsMatch(u.AbsolutePath)) return;
                string s = u.AbsoluteUri.ToLowerInvariant();
                int score = bonus;
                if (Regex.IsMatch(u.AbsolutePath, @"\.pdf$", RegexOptions.IgnoreCase)) score += 100;
                else if (s.Contains("pdf")) score += 60;
                if (Regex.IsMatch(s, "download|xiazai|/down|getfile|fileurl")) score += 30;
                if (score <= 0) return;
                if (!seen.Add(u.AbsoluteUri)) return;
                scored.Add(new KeyValuePair<Uri, int>(u, score));
            };
            // <meta http-equiv="refresh" content="0;url=...">
            foreach (Match m in Regex.Matches(text, @"<meta[^>]+http-equiv\s*=\s*[""']?refresh[^>]*content\s*=\s*[""'][^""']*url\s*=\s*([^""'>]+)", RegexOptions.IgnoreCase))
                add(m.Groups[1].Value, 80);
            // location.href = '...' / location.replace('...') / window.open('...')
            foreach (Match m in Regex.Matches(text, @"(?:location(?:\.href)?\s*=|location\.(?:replace|assign)\s*\(|window\.open\s*\()\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase))
                add(m.Groups[1].Value, 70);
            foreach (Match m in Regex.Matches(text, @"(?:href|src|data-url|data-src|data-href)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase))
                add(m.Groups[1].Value, 0);
            foreach (Match m in Regex.Matches(text, @"[""']((?:https?:)?//[^""'\s<>]+|/[^""'\s<>]*pdf[^""'\s<>]*)[""']", RegexOptions.IgnoreCase))
                add(m.Groups[1].Value, 0);
            foreach (Match m in UrlRe.Matches(text)) add(m.Value, 0);
            return scored.OrderByDescending(k => k.Value).Select(k => k.Key).Take(8).ToList();
        }

        static string DecodeText(byte[] body, string contentType)
        {
            Encoding enc = null;
            var m = Regex.Match(contentType ?? "", @"charset\s*=\s*""?([\w-]+)", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 2048)), @"charset\s*=\s*[""']?([\w-]+)", RegexOptions.IgnoreCase);
            if (m.Success) { try { enc = Encoding.GetEncoding(m.Groups[1].Value); } catch { } }
            return (enc ?? Encoding.UTF8).GetString(body);
        }

        static string Save(FetchResult r, Uri uri, string dir)
        {
            Directory.CreateDirectory(dir);
            string name = Organizer.Sanitize(r.FileName);
            if (name.Length == 0)
            {
                string last = "";
                try { last = Organizer.Sanitize(Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "")); } catch { }
                name = last.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? last : "发票_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pdf";
            }
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
            string target = Path.Combine(dir, name);
            for (int k = 2; File.Exists(target); k++) target = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + "(" + k + ").pdf");
            File.WriteAllBytes(target, r.Body);
            return target;
        }

        static string Shorten(string s, int n) { s = (s ?? "").Replace("\r", " ").Replace("\n", " "); return s.Length > n ? s.Substring(0, n) + "…" : s; }

        /// <summary>真实网络访问：自动跟随重定向、保留 Cookie、模拟手机浏览器。</summary>
        class HttpFetcher
        {
            readonly HttpClient client;
            public HttpFetcher()
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                var h = new HttpClientHandler
                {
                    AllowAutoRedirect = true, MaxAutomaticRedirections = 10, UseCookies = true, CookieContainer = new CookieContainer(),
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                };
                client = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(30) };
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/pdf,application/json,image/*,*/*;q=0.8");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            }

            // 先模拟手机浏览器（发票 H5 页面多为移动端）；被拒绝(4xx)时换普通客户端标识重试
            static readonly string[] Agents = {
                "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36",
                "PiaoMate/1.0" };

            public async Task<FetchResult> Fetch(Uri uri, CancellationToken ct)
            {
                for (int i = 0; ; i++)
                {
                    try { return await FetchOnce(uri, Agents[i], ct).ConfigureAwait(false); }
                    catch (HttpStatusException ex) when (i + 1 < Agents.Length && ex.Code >= 400 && ex.Code < 500 && ex.Code != 404) { }
                }
            }

            class HttpStatusException : Exception
            {
                public readonly int Code;
                public HttpStatusException(int code, string msg) : base(msg) { Code = code; }
            }

            /// <summary>表单 POST（与 GET 共用 Cookie）。</summary>
            public Task<FetchResult> Post(Uri uri, IDictionary<string, string> form, Uri referer, CancellationToken ct)
            {
                string raw;
                HttpContent content = form.TryGetValue(SiteContext.RawBodyKey, out raw)
                    ? (HttpContent)new StringContent(raw, Encoding.UTF8, "application/json")
                    : new FormUrlEncodedContent(form);
                var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
                req.Headers.TryAddWithoutValidation("User-Agent", Agents[0]);
                req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                if (referer != null) req.Headers.Referrer = referer;
                return Send(req, uri, ct);
            }

            Task<FetchResult> FetchOnce(Uri uri, string agent, CancellationToken ct)
            {
                var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.TryAddWithoutValidation("User-Agent", agent);
                return Send(req, uri, ct);
            }

            async Task<FetchResult> Send(HttpRequestMessage req, Uri uri, CancellationToken ct)
            {
                using (req)
                using (var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) throw new HttpStatusException((int)resp.StatusCode, "服务器返回 " + (int)resp.StatusCode + " " + resp.ReasonPhrase);
                    var len = resp.Content.Headers.ContentLength;
                    if (len.HasValue && len.Value > MaxBytes) throw new Exception("文件过大");
                    var ms = new MemoryStream();
                    using (var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        var buf = new byte[81920]; int n;
                        while ((n = await s.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                        {
                            ms.Write(buf, 0, n);
                            if (ms.Length > MaxBytes) throw new Exception("文件过大");
                        }
                    }
                    var cd = resp.Content.Headers.ContentDisposition;
                    string fn = "";
                    if (cd != null) fn = (cd.FileNameStar ?? cd.FileName ?? "").Trim('"');
                    if (fn.Length > 0) { try { fn = Uri.UnescapeDataString(fn); } catch { } }
                    return new FetchResult
                    {
                        FinalUri = resp.RequestMessage != null ? resp.RequestMessage.RequestUri : uri,
                        ContentType = resp.Content.Headers.ContentType != null ? resp.Content.Headers.ContentType.ToString() : "",
                        FileName = fn,
                        Body = ms.ToArray()
                    };
                }
            }
        }
    }

    /// <summary>二维码识别（ZXing）。对截图中较小/较大的二维码做缩放重试。</summary>
    public static class QrDecoder
    {
        public static List<string> Decode(Bitmap bmp)
        {
            var res = new List<string>();
            if (bmp == null) return res;
            var reader = new ZXing.BarcodeReader
            {
                AutoRotate = true,
                Options = new ZXing.Common.DecodingOptions
                {
                    TryHarder = true, TryInverted = true,
                    PossibleFormats = new List<ZXing.BarcodeFormat> { ZXing.BarcodeFormat.QR_CODE }
                }
            };
            foreach (double scale in new[] { 1.0, 2.0, 0.5, 3.0 })
            {
                if (scale > 1 && Math.Max(bmp.Width, bmp.Height) * scale > 4000) continue;
                if (scale < 1 && Math.Max(bmp.Width, bmp.Height) < 1200) continue;
                Bitmap b = scale == 1.0 ? bmp : Resize(bmp, scale);
                try
                {
                    var rs = reader.DecodeMultiple(b);
                    if (rs != null) foreach (var r in rs) if (!string.IsNullOrEmpty(r.Text) && !res.Contains(r.Text)) res.Add(r.Text);
                }
                catch { }
                finally { if (b != bmp) b.Dispose(); }
                if (res.Count > 0) break;
            }
            return res;
        }

        static Bitmap Resize(Bitmap src, double scale)
        {
            int w = Math.Max(1, (int)(src.Width * scale)), h = Math.Max(1, (int)(src.Height * scale));
            var b = new Bitmap(w, h);
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(Color.White);
                g.InterpolationMode = scale > 1 ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor : System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            return b;
        }
    }
}
