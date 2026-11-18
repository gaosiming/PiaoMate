using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PiaoMate
{
    /// <summary>
    /// 扫码开票：在内置浏览器（WebView2）中打开开票页面，自动填写抬头/税号/手机/邮箱并用红框标出提交按钮。
    /// 提交开票由用户本人核对后点击（不会自动提交）。页面中出现的 PDF（下载、预览、链接）会自动保存并整理。
    /// </summary>
    public class IssueForm : Form
    {
        readonly Settings cfg;
        readonly string startUrl, archiveDir;
        readonly Action<List<OrganizeItem>> onOrganized;
        readonly HashSet<string> seen = new HashSet<string>();
        readonly HashSet<string> fetchedLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly string tempDir = Path.Combine(Path.GetTempPath(), "PiaoMate_issue", Guid.NewGuid().ToString("N"));
        WebView2 web;
        ComboBox cmbCompany;
        TextBox txtPhone, txtEmail, txtUrl;
        CheckBox chkAuto;
        Label lblStatus, lblFill;
        Timer timer;
        bool forceNext;
        int captured;
        /// <summary>测试钩子：WebView2 就绪后、导航前调用。</summary>
        public Action<CoreWebView2> OnReady;
        public int CapturedCount { get { return captured; } }

        public IssueForm(Settings cfg, string url, string archiveDir, Action<List<OrganizeItem>> onOrganized)
        {
            this.cfg = cfg; startUrl = url; this.archiveDir = archiveDir; this.onOrganized = onOrganized;
            Text = "扫码开票（内置浏览器）";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(560, 860);
            var wa = Screen.PrimaryScreen.WorkingArea;
            if (ClientSize.Height > wa.Height - 60) ClientSize = new Size(560, wa.Height - 60);
            MinimumSize = new Size(420, 500);
            StartPosition = FormStartPosition.CenterScreen;
            ShowIcon = false;
            BuildUi();
            Load += async (s, e) => await InitWeb();
            FormClosed += (s, e) =>
            {
                if (timer != null) timer.Stop();
                SaveFields();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            };
        }

        static Label L(string t) { return new Label { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 0, 3) }; }

        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var f1 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(4, 4, 4, 0) };
            cmbCompany = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
            foreach (var c in Companies(cfg)) cmbCompany.Items.Add(c.Key + "（" + c.Value + "）");
            int sel = Companies(cfg).FindIndex(c => c.Value == cfg.IssueCompany);
            if (cmbCompany.Items.Count > 0) cmbCompany.SelectedIndex = Math.Max(0, sel);
            cmbCompany.SelectedIndexChanged += (s, e) => { SaveFields(); Refill(); };
            txtPhone = new TextBox { Width = 120, Text = cfg.Phone };
            txtEmail = new TextBox { Width = 190, Text = cfg.Email };
            var bRefill = new Button { Text = "重新填写", AutoSize = true };
            bRefill.Click += (s, e) => { SaveFields(); Refill(); };
            chkAuto = new CheckBox { Text = "自动填写", Checked = true, AutoSize = true, Margin = new Padding(6, 6, 3, 3) };
            f1.Controls.AddRange(new Control[] { L("开票抬头："), cmbCompany, L("手机："), txtPhone, L("邮箱："), txtEmail, bRefill, chkAuto });
            root.Controls.Add(f1, 0, 0);

            var t2 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true, Padding = new Padding(4, 0, 4, 0) };
            t2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var bBack = new Button { Text = "后退", AutoSize = true };
            var bReload = new Button { Text = "刷新", AutoSize = true };
            txtUrl = new TextBox { Dock = DockStyle.Fill, ReadOnly = true };
            var bExt = new Button { Text = "用系统浏览器打开", AutoSize = true };
            bBack.Click += (s, e) => { if (web != null && web.CoreWebView2 != null && web.CanGoBack) web.GoBack(); };
            bReload.Click += (s, e) => { if (web != null && web.CoreWebView2 != null) web.Reload(); };
            bExt.Click += (s, e) => { try { Process.Start(txtUrl.Text.Length > 0 ? txtUrl.Text : startUrl); } catch { } };
            t2.Controls.Add(bBack, 0, 0); t2.Controls.Add(bReload, 1, 0); t2.Controls.Add(txtUrl, 2, 0); t2.Controls.Add(bExt, 3, 0);
            root.Controls.Add(t2, 0, 1);

            lblFill = new Label
            {
                AutoSize = true, ForeColor = Color.FromArgb(0xB7, 0x1C, 0x1C), Margin = new Padding(6, 4, 6, 4),
                Text = "已自动填写的输入框显示为黄色。请核对信息无误后，自己点击页面上红框标出的按钮提交开票（软件不会替你提交）。"
            };
            root.Controls.Add(lblFill, 0, 2);

            web = new WebView2 { Dock = DockStyle.Fill };
            root.Controls.Add(web, 0, 3);

            lblStatus = new Label { AutoSize = true, Margin = new Padding(6, 4, 6, 6), Font = new Font(Font, FontStyle.Bold), Text = "正在打开…" };
            root.Controls.Add(lblStatus, 0, 4);
            Resize += (s, e) => { lblFill.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 16), 0); lblStatus.MaximumSize = lblFill.MaximumSize; };
        }

        /// <summary>可选的开票公司：备选公司 + 自定义（若填写过）。</summary>
        public static List<KeyValuePair<string, string>> Companies(Settings cfg)
        {
            var l = cfg.Companies.Where(c => c.Key.Length > 0).ToList();
            if (cfg.CustomName.Trim().Length > 0 && !l.Any(c => c.Key == cfg.CustomName.Trim()))
                l.Add(new KeyValuePair<string, string>(cfg.CustomName.Trim(), cfg.CustomTaxId.Trim()));
            return l;
        }

        KeyValuePair<string, string> Company()
        {
            var l = Companies(cfg);
            int i = cmbCompany.SelectedIndex;
            return i >= 0 && i < l.Count ? l[i] : new KeyValuePair<string, string>("", "");
        }

        void SaveFields()
        {
            cfg.Phone = txtPhone.Text.Trim(); cfg.Email = txtEmail.Text.Trim();
            var c = Company(); if (c.Value.Length > 0) cfg.IssueCompany = c.Value;
        }

        // ---------- WebView2 ----------
        static bool loaderSet;

        /// <summary>从 exe 内嵌资源释放与进程架构匹配的 WebView2Loader.dll。</summary>
        static void EnsureLoader()
        {
            if (loaderSet) return;
            string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : (Environment.Is64BitProcess ? "x64" : "x86");
            var asm = Assembly.GetExecutingAssembly();
            using (var st = asm.GetManifestResourceStream("native." + arch + ".WebView2Loader.dll"))
            {
                if (st == null) { loaderSet = true; return; } // 未内嵌（如开发调试），使用输出目录中的 runtimes\
                var buf = new byte[st.Length]; int off = 0, n;
                while (off < buf.Length && (n = st.Read(buf, off, buf.Length - off)) > 0) off += n;
                string hash = BitConverter.ToString(System.Security.Cryptography.SHA1.Create().ComputeHash(buf)).Replace("-", "").Substring(0, 12);
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiaoMate", "WebView2Loader", arch + "_" + hash);
                string file = Path.Combine(dir, "WebView2Loader.dll");
                if (!File.Exists(file)) { Directory.CreateDirectory(dir); File.WriteAllBytes(file, buf); }
                CoreWebView2Environment.SetLoaderDllFolderPath(dir);
            }
            loaderSet = true;
        }

        async Task InitWeb()
        {
            try
            {
                EnsureLoader();
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiaoMate", "WebView2Data");
                var env = await CoreWebView2Environment.CreateAsync(null, data);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                Fail("本机未安装 Microsoft Edge WebView2 运行时，无法使用内置浏览器。请安装后重试（微软官网搜索“WebView2 Runtime”），或点「用系统浏览器打开」手动开票。");
                return;
            }
            catch (Exception ex) { Fail("内置浏览器启动失败：" + ex.Message + "。可点「用系统浏览器打开」手动开票。"); return; }

            var core = web.CoreWebView2;
            // 发票 H5 页面多为手机版
            core.Settings.UserAgent = "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36";
            core.Settings.AreDevToolsEnabled = false;
            core.NavigationStarting += (s, e) => { txtUrl.Text = e.Uri; lblStatus.Text = "正在加载…"; };
            core.NavigationCompleted += (s, e) =>
            {
                txtUrl.Text = core.Source;
                if (!e.IsSuccess) lblStatus.Text = "页面加载失败（" + e.WebErrorStatus + "）。";
                else if (captured == 0) lblStatus.Text = "请核对自动填写的信息，确认无误后在页面上点击提交开票。";
                RunFill();
            };
            core.NewWindowRequested += (s, e) => { e.Handled = true; core.Navigate(e.Uri); };
            core.WebResourceResponseReceived += OnResponse;
            core.DownloadStarting += OnDownload;
            core.WebMessageReceived += OnMessage;
            if (OnReady != null) OnReady(core);

            timer = new Timer { Interval = 1500 };
            timer.Tick += (s, e) => RunFill();
            timer.Start();
            core.Navigate(startUrl);
        }

        void Fail(string msg)
        {
            lblStatus.Text = msg; lblStatus.ForeColor = Color.Firebrick;
            MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void Refill() { forceNext = true; RunFill(); }

        async void RunFill()
        {
            var core = web != null ? web.CoreWebView2 : null;
            if (core == null) return;
            bool force = forceNext; forceNext = false;
            try
            {
                if (chkAuto.Checked || force)
                {
                    var c = Company();
                    string arg = Json(new[] { "name", c.Key, "tax", c.Value, "phone", txtPhone.Text.Trim(), "email", txtEmail.Text.Trim(), "force", force ? "1" : "" });
                    string r = await core.ExecuteScriptAsync(FillScript + "(" + arg + ")");
                    r = Unquote(r);
                    if (r.Length > 0) lblFill.Text = r;
                }
                string links = Unquote(await core.ExecuteScriptAsync(LinkScript));
                foreach (var u in links.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(5))
                    if (fetchedLinks.Add(u)) await core.ExecuteScriptAsync(FetchScript + "(" + Json(new[] { u }, true) + ")");
            }
            catch { }
        }

        // ---------- PDF 捕获 ----------
        async void OnResponse(object sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                string ct = "";
                try { ct = e.Response.Headers.GetHeader("Content-Type") ?? ""; } catch { }
                string uri = e.Request.Uri ?? "";
                if (ct.IndexOf("pdf", StringComparison.OrdinalIgnoreCase) < 0 && ct.IndexOf("octet-stream", StringComparison.OrdinalIgnoreCase) < 0 &&
                    !uri.Split('?')[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return;
                using (var st = await e.Response.GetContentAsync())
                {
                    if (st == null) return;
                    var ms = new MemoryStream(); await st.CopyToAsync(ms);
                    HandlePdf(ms.ToArray(), NameFromUri(uri));
                }
            }
            catch { }
        }

        void OnDownload(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            Directory.CreateDirectory(tempDir);
            string name = Organizer.Sanitize(Path.GetFileName(e.ResultFilePath));
            if (name.Length == 0) name = "download.pdf";
            string path = Path.Combine(tempDir, Guid.NewGuid().ToString("N").Substring(0, 8) + "_" + name);
            e.ResultFilePath = path;
            e.Handled = true; // 不弹出浏览器自带的下载提示
            var op = e.DownloadOperation;
            lblStatus.Text = "正在下载：" + name;
            op.StateChanged += (s, a) =>
            {
                if (op.State == CoreWebView2DownloadState.Completed)
                {
                    try { HandlePdf(File.ReadAllBytes(path), name); } catch (Exception ex) { lblStatus.Text = "读取下载文件失败：" + ex.Message; }
                }
                else if (op.State == CoreWebView2DownloadState.Interrupted) lblStatus.Text = "下载中断：" + op.InterruptReason;
            };
        }

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string m;
            try { m = e.TryGetWebMessageAsString(); } catch { return; }
            if (m == null || !m.StartsWith("FPPDF|")) return;
            int k = m.IndexOf('|', 6);
            if (k < 0) return;
            try { HandlePdf(Convert.FromBase64String(m.Substring(k + 1)), NameFromUri(m.Substring(6, k - 6))); } catch { }
        }

        static string NameFromUri(string uri)
        {
            try
            {
                string n = Organizer.Sanitize(Uri.UnescapeDataString(new Uri(uri).Segments.Last()));
                return n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? n : "发票_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pdf";
            }
            catch { return "发票_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pdf"; }
        }

        /// <summary>保存 PDF → 识别 → 整理。同一文件只处理一次。</summary>
        void HandlePdf(byte[] data, string name)
        {
            if (!Downloader.IsPdf(data)) return;
            string h;
            using (var sha = System.Security.Cryptography.SHA256.Create()) h = BitConverter.ToString(sha.ComputeHash(data));
            if (!seen.Add(h)) return;
            Directory.CreateDirectory(tempDir);
            string file = Path.Combine(tempDir, Guid.NewGuid().ToString("N").Substring(0, 8) + "_" + name);
            File.WriteAllBytes(file, data);
            List<OrganizeItem> res;
            try
            {
                res = Organizer.Organize(new[] { InvoiceParser.Parse(file) }, archiveDir, includeUnknown: true);
                Organizer.WriteIndex(res, DateTime.Now);
            }
            catch (Exception ex) { lblStatus.Text = "整理失败：" + ex.Message; return; }
            finally { try { File.Delete(file); } catch { } }
            captured++;
            var r = res[0];
            lblStatus.ForeColor = r.Verified ? Color.DarkGreen : Color.Firebrick;
            lblStatus.Text = r.Verified
                ? (r.Invoice.Ok ? "已获取发票并整理：" : "已获取 PDF（未识别为发票，放入 " + Organizer.UnknownFolder + "）：") + Path.GetFileName(r.Target)
                : "保存失败：" + r.Status;
            if (onOrganized != null) onOrganized(res);
        }

        // ---------- 脚本 ----------
        static string Unquote(string jsonStr)
        {
            if (string.IsNullOrEmpty(jsonStr) || jsonStr == "null") return "";
            if (jsonStr.Length >= 2 && jsonStr[0] == '"')
            {
                try { return System.Text.RegularExpressions.Regex.Unescape(jsonStr.Substring(1, jsonStr.Length - 2)); } catch { }
            }
            return jsonStr;
        }

        /// <summary>键值对数组 → JSON 对象（或 list=true 时第一个元素 → JSON 字符串）。</summary>
        static string Json(string[] kv, bool single = false)
        {
            Func<string, string> q = v =>
            {
                var sb = new System.Text.StringBuilder("\"");
                foreach (char c in v ?? "")
                {
                    if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                    else if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029 || c == '<') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                }
                return sb.Append('"').ToString();
            };
            if (single) return q(kv[0]);
            var parts = new List<string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) parts.Add(q(kv[i]) + ":" + q(kv[i + 1]));
            return "{" + string.Join(",", parts) + "}";
        }

        /// <summary>
        /// 自动填写：按输入框的 placeholder/name/标签文字识别 税号/邮箱/手机/抬头；只填空白框（重新填写时覆盖）；
        /// 若页面有“个人/企业”切换则选企业；用红框标出提交/开票按钮（不点击）。返回填写情况说明。
        /// </summary>
        public const string FillScript = @"(function(p){
var R={name:0,tax:0,phone:0,email:0},sub=0;
function vis(e){var r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>0&&r.height>0&&s.visibility!='hidden'&&s.display!='none';}
function txt(e){return ((e&&(e.innerText||e.textContent))||'').replace(/\s+/g,'');}
function hint(e){var h=[e.placeholder,e.name,e.id,e.getAttribute('aria-label'),e.title];
 if(e.id){try{var l=document.querySelector('label[for=""'+CSS.escape(e.id)+'""]');if(l)h.push(txt(l));}catch(x){}}
 var c=e;for(var i=0;i<4&&c;i++){var ps=c.previousElementSibling;if(ps){var pt=txt(ps);if(pt&&pt.length<=20){h.push(pt);break;}}c=c.parentElement;if(c){var t=txt(c);if(t&&t.length<=24){h.push(t);break;}}}
 return h.filter(Boolean).join(' ');}
var bad=/地址|开户|银行|账号|帐号|备注|金额|验证码|校验码|密码|数量|单价|搜索|search|code|captcha|企业电话|单位电话|公司电话|注册电话|座机|固定电话|(^|\s)电话(\s|$)/i;
function kind(h,t){if(/验证码|校验码|短信码|captcha|verify/i.test(h))return null;
 if(/税号|纳税人|识别号|信用代码|taxno|taxnum|tax_?id/i.test(h))return 'tax';
 if(t=='email'||/邮箱|电子邮件|e-?mail/i.test(h))return 'email';
 if(bad.test(h))return null;
 if(t=='tel'||/手机|电话|联系方式|mobile|phone|tel/i.test(h))return 'phone';
 if(/抬头|名称|单位|公司|购方|购买方|title|company|buyer/i.test(h))return 'name';return null;}
function setv(e,v){var pr=e.tagName=='TEXTAREA'?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;
 Object.getOwnPropertyDescriptor(pr,'value').set.call(e,v);
 ['input','change','blur'].forEach(function(n){e.dispatchEvent(new Event(n,{bubbles:true}));});}
if(p.name&&p.tax&&!window.__fpType){
 var cs=[].slice.call(document.querySelectorAll('label,span,div,button,a,li,p,[role=radio],[role=tab]')).filter(function(e){var t=txt(e);return /^(企业|单位|企业单位|企业\/单位|单位\/企业|公司|非个人)$/.test(t)&&vis(e)&&![].some.call(e.children,function(c){return txt(c)==t;});});
 for(var i=0;i<cs.length;i++){var e=cs[i],up=e.parentElement&&e.parentElement.parentElement;if(up&&/个人/.test(txt(up))){e.click();window.__fpType=1;break;}}}
var vals={name:p.name,tax:p.tax,phone:p.phone,email:p.email};
[].forEach.call(document.querySelectorAll('input,textarea'),function(e){
 var t=(e.getAttribute('type')||'text').toLowerCase();
 if(e.tagName=='INPUT'&&['text','tel','email','number','search'].indexOf(t)<0)return;
 if(e.readOnly||e.disabled||!vis(e))return;
 var im=((e.getAttribute('inputmode')||'')+' '+(e.getAttribute('autocomplete')||'')).toLowerCase();
 var k=kind(hint(e),/email/.test(im)?'email':(/\btel\b/.test(im)?'tel':t));
 if(!k||!vals[k])return;
 var mine=e.dataset.fpFilled&&e.dataset.fpFilled!='user';
 if(!p.force){if(e.dataset.fpFilled){if(mine)R[k]=1;return;}if(e.value&&e.value.trim()){e.dataset.fpFilled='user';return;}}
 setv(e,vals[k]);e.dataset.fpFilled=k;e.style.backgroundColor='#fff59d';R[k]=1;});
[].forEach.call(document.querySelectorAll('button,a,[role=button],input[type=submit],input[type=button],div,span'),function(e){
 var t=txt(e)||e.value||'';var leaf=['BUTTON','A','INPUT'].indexOf(e.tagName)>=0||e.getAttribute('role')=='button'||e.children.length==0;
 if(leaf&&t.length>0&&t.length<=8&&/提交|开票|申请|确认/.test(t)&&!/查看|历史|记录|说明|须知|规则|已开/.test(t)&&vis(e)){e.style.outline='3px solid #e53935';e.style.outlineOffset='2px';sub++;}});
var n={name:'抬头',tax:'税号',phone:'手机',email:'邮箱'},done=[],miss=[];
for(var k in n){if(R[k])done.push(n[k]);else if(vals[k])miss.push(n[k]);}
if(!done.length&&!sub)return '';
return (done.length?'已自动填写（黄色）：'+done.join('、')+'。':'')+(miss.length&&done.length?'未找到：'+miss.join('、')+'，如需要请手动填写。':'')+(sub?'请核对后自己点击红框按钮提交开票（软件不会替你提交）。':'');
})";

        /// <summary>收集页面中指向 PDF 的链接（换行分隔）。</summary>
        public const string LinkScript = @"(function(){var s={};[].forEach.call(document.querySelectorAll('a[href],iframe[src],embed[src],object[data]'),function(e){var u=e.href||e.src||e.data;if(u&&/^https?:/i.test(u)&&/\.pdf(\?|#|$)|[?&](type|format|filetype|fileType)=pdf|downloadpdf|pdfurl|pdf_url/i.test(u))s[u]=1;});return Object.keys(s).join('\n');})()";

        /// <summary>在页面内（带 Cookie）下载链接，通过 postMessage 回传 Base64。</summary>
        public const string FetchScript = @"(function(u){fetch(u,{credentials:'include'}).then(function(r){return r.arrayBuffer();}).then(function(a){var b=new Uint8Array(a),s='';for(var i=0;i<b.length;i+=32768)s+=String.fromCharCode.apply(null,b.subarray(i,i+32768));window.chrome.webview.postMessage('FPPDF|'+u+'|'+btoa(s));}).catch(function(){});})";
    }

    /// <summary>打开内置浏览器前选择开票抬头、确认手机/邮箱。</summary>
    public static class IssuePicker
    {
        public static bool Pick(IWin32Window owner, Settings cfg)
        {
            var comps = IssueForm.Companies(cfg);
            using (var f = new Form { Text = "选择开票抬头", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowIcon = false, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = new Font("Microsoft YaHei UI", 9f), Padding = new Padding(10) })
            {
                var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
                var lst = new ListBox { Width = 420, Height = Math.Min(8, Math.Max(2, comps.Count)) * 22 + 6 };
                foreach (var c in comps) lst.Items.Add(c.Key + "    " + c.Value);
                int sel = comps.FindIndex(c => c.Value == cfg.IssueCompany);
                if (lst.Items.Count > 0) lst.SelectedIndex = Math.Max(0, sel);
                var phone = new TextBox { Width = 200, Text = cfg.Phone };
                var email = new TextBox { Width = 260, Text = cfg.Email };
                var ok = new Button { Text = "打开开票页面", AutoSize = true, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
                Func<string, Label> L = s => new Label { Text = s, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
                t.Controls.Add(L("开票抬头："), 0, 0); t.Controls.Add(lst, 1, 0);
                t.Controls.Add(L("手机号："), 0, 1); t.Controls.Add(phone, 1, 1);
                t.Controls.Add(L("邮箱："), 0, 2); t.Controls.Add(email, 1, 2);
                var tip = new Label { AutoSize = true, MaximumSize = new Size(480, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3),
                    Text = "备选公司可在主窗口筛选区「自定义」或配置文件中维护。手机号、邮箱会记住，下次自动带出。" };
                t.Controls.Add(tip, 0, 3); t.SetColumnSpan(tip, 2);
                var bf = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
                bf.Controls.Add(cancel); bf.Controls.Add(ok);
                t.Controls.Add(bf, 0, 4); t.SetColumnSpan(bf, 2);
                f.Controls.Add(t);
                f.AcceptButton = ok; f.CancelButton = cancel;
                lst.DoubleClick += (s, e) => { f.DialogResult = DialogResult.OK; };
                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                if (lst.SelectedIndex >= 0) cfg.IssueCompany = comps[lst.SelectedIndex].Value;
                cfg.Phone = phone.Text.Trim(); cfg.Email = email.Text.Trim();
                return true;
            }
        }
    }
}
