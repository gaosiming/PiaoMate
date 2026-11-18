# Repository Guidelines

## Project Structure & Module Organization

`PiaoMate` is intended to be published as open source. Real invoices, personal configuration and company names/tax IDs must never be committed; see `.gitignore` and the section below.

Keep local real sample documents in `mock/` (git-ignored; only `mock/README.md` is tracked). When implementation begins, group application code in `src/` and automated tests in `tests/`, unless the selected framework requires another layout. Document new directories and entry points here when they are introduced.

## Build, Test, and Development Commands

No build, run, lint, or test commands are configured yet. Do not assume that `npm test`, `pytest`, or another framework command is available.

From the repository root, use these PowerShell commands to inspect the workspace:

- `Get-ChildItem -Force` — list top-level entries, including hidden files.
- `Get-ChildItem mock -File` — list available sample documents.

When adding a toolchain, document its installation steps and exact build, run, and test commands alongside its dependency manifest.

## Coding Style & Naming Conventions

No language-specific style or formatter is established. Follow the chosen language’s standard conventions, use consistent indentation, and add formatter configuration with the first implementation. Prefer descriptive module and function names. Avoid unrelated formatting changes and preserve existing sample filenames unless a change is necessary and documented.

## Testing Guidelines

No testing framework or coverage threshold exists yet. Add tests alongside new behavior and document how to execute them. If PDF processing is implemented, cover valid documents, malformed input, and missing files. Use synthetic or anonymized fixtures, with names that describe the scenario.

## Commit & Pull Request Guidelines

No Git metadata or commit history is available in this workspace. Until conventions are established, use short, imperative commit subjects, such as `Add PDF input validation`.

Pull requests should describe the change, explain validation performed, link relevant issues when available, and include screenshots for visible interface changes. Explicitly note checks that could not be run.

## Sample Data & Security

Treat invoice PDFs as potentially sensitive. Do not commit credentials, personal information, or real invoice data without appropriate authorization. Prefer anonymized samples and exclude generated exports and local configuration from version control when Git is initialized.

## 当前实现（v1.0.0）

- 技术栈：C# WinForms，目标 .NET Framework 4.8（Windows 10/11 自带，无需安装运行时）；PDF 解析使用 PdfPig，依赖 DLL 已嵌入 exe，单文件便携。
- 二维码识别使用 ZXing.Net（同样嵌入 exe）；下载用 `System.Net.Http`，删除原文件用 `Microsoft.VisualBasic` 移入回收站。
- `src/`：`Program.cs`（入口、嵌入 DLL 加载）、`InvoiceParser.cs`（按文字坐标解析发票）、`Matcher.cs`（子集和，最接近目标金额）、`Exporter.cs`（移动文件 + 清单.txt）、`Settings.cs`（备选公司可在配置文件中用 `CompanyN=名称|税号` 修改/新增；源码中没有内置公司，内置公司来自嵌入资源 `builtin.companies.txt`，仅内部版构建 `-p:Edition=Internal` 时由 `internal\companies.txt` 嵌入，`Settings.IsInternalEdition` 判断，标题栏显示「（内部版）」；配置保存于 exe 同目录 `PiaoMate.config`，不可写时退回 `%APPDATA%`）、`MainForm.cs`（主界面：凑金额导出；顶部工具栏为「整理发票」「下载发票」入口）、`Organizer.cs`（按 抬头_税号 分类复制、SHA256 校验、确认后删除原文件）、`OrganizeForm.cs`（整理界面）、`Downloader.cs`（链接/二维码下载：跟随跳转、从页面提取 PDF 链接、查验二维码识别；`QrDecoder` 基于 ZXing；网络访问可注入以便离线测试）、`DownloadForm.cs`（下载界面，下载后自动整理）、`IssueForm.cs`（扫码开票：内置 WebView2 浏览器，自动填写抬头/税号/手机/邮箱并红框标出提交按钮，**不自动提交**；捕获页面中的 PDF 后自动整理；`IssuePicker` 选择抬头）、`ShellHelper.cs`（资源管理器中打开目录并选中文件）、`ChangelogForm.cs`（工具栏「更新日志」窗口，显示嵌入 exe 的 `CHANGELOG.md`）、`SiteAdapters.cs`（开票平台适配器：`SiteAdapter` 基类 + `SiteAdapter.All` 注册表；`Downloader` 跟随跳转后若最终网址被某适配器 `CanHandle`，就调用其 `Resolve` 通过平台接口取得 PDF 地址；已适配：`NuonuoAdapter` 诺诺网——`printQrcode?paramList=` 页 → POST `/scan2/getIvcDetailShow.do` → `data.invoiceSimpleVo.url`。`BaiwangAdapter` 百望云——`pis.baiwang.com/smkp-vue/…?param=加密参数`（或 `state` 中的 `f`/`param`）→ 以 param 为 JSON 请求体 POST `/bwmg/mix/bw/previewInvoiceQd` → `data[].eInvoiceUrl`，无地址时用 `/bwmg/mix/bw/downloadFormat?param=…&formatType=PDF`（`natural=00` 走 `/bwmg/nature-dlg/web/dlg/invoice/`）。适配器需要原始 JSON 请求体时，在表单中放 `SiteContext.RawBodyKey`。新增平台时继承 `SiteAdapter` 并加入 `All`，在测试中用注入的 GET/POST 模拟接口，数据用虚构样例）。JSON 解析用 `System.Web.Extensions` 的 `JavaScriptSerializer`。WebView2 托管 DLL 嵌入 exe，原生 `WebView2Loader.dll` 按架构嵌入为 `native.<arch>.WebView2Loader.dll`，运行时释放到 `%LOCALAPPDATA%\PiaoMate\WebView2Loader\`；配置新增 `Phone`、`Email`、`IssueCompany`。整理目录配置项 `ArchiveDir`，为空时用 `导出目录\发票整理`。
- `tests/PiaoMate.Tests/`：控制台测试，运行 `dotnet run --project tests\PiaoMate.Tests`。测试发票由 `SampleInvoice.cs` 在运行时用 PdfPig 生成（虚构数据，需系统字体 Deng.ttf 或 simhei.ttf，或用环境变量 `PIAOMATE_TEST_FONT` 指定 .ttf）；`mock/` 中若有本地真实样例，只做「能识别」检查，不在源码中写入任何真实数据。下载测试用模拟网络，不联网。手动联网检查：`dotnet run --project tests\PiaoMate.Tests -- --live <网址>`。
- `tools/probe/`：PDF 文字坐标探查小工具，仅开发调试用。
- 版本与更新日志：版本号在 `src/PiaoMate.csproj` 的 `<Version>`；每次改动记到根目录 `CHANGELOG.md` 的 `## [未发布]`（分组 `### 新增/改进/修复`），可用 `.\tools\release.ps1 -AddNote "说明" [-Type 修复]` 记录，`-Changes` 查看未发布改动，`-History` 列出已发布版本。
- 构建发布：`powershell -NoProfile -ExecutionPolicy Bypass -File tools\release.ps1 [-Bump minor|major | -Version x.y.z] [-Notes "…"] [-Edition All|Public|Internal]`——升版本号、把「未发布」改为 `## [版本] - 日期`、跑测试、编译、生成 `build/dist/` 下的目录与 zip（exe、使用说明、快速使用手册、更新日志.md；zip 不含个人配置），失败时还原版本号和日志；默认（All）同时生成公开版 `PiaoMate-v<版本>`（开源发布用）和内部版 `PiaoMate-v<版本>-内部版`（存在 `internal\companies.txt` 时；个人配置只带到内部版旁）。「未发布」为空时拒绝发版（`-AllowEmpty` 可强制）。默认只保留最近 2 个版本号的目录（`-Keep`）。`-Rebuild` 重新生成当前版本。测试会校验 `CHANGELOG.md` 含当前版本。

## 仓库文件

- `.github/`：Issue 模板（问题反馈、开票平台适配请求——提醒不要公开发票链接）、PR 模板、CI（`workflows/ci.yml`：windows-latest 下载霞鹜文楷字体（SHA256 校验）作为 `PIAOMATE_TEST_FONT` 运行测试，用 `release.ps1 -Rebuild -Edition Public -SkipTests` 生成公开版并上传为构建物料；推送 `v*` 标签时校验与 csproj 版本一致，并把公开版 zip 发布到 GitHub Release，已存在则覆盖附件）。
- `CONTRIBUTING.md`（开发与适配指南）、`SECURITY.md`（隐私问题私下报告）。
- `docs/images/main.png`：README 截图，只用虚构发票生成。

## 名称

- 软件名「票小秘」，英文/程序集/仓库名 `PiaoMate`。配置文件 `PiaoMate.config`（exe 旁，不可写时 `%APPDATA%\PiaoMate\`，见 `Settings.FindConfig`）；数据目录 `%LOCALAPPDATA%\PiaoMate\`。

## 开源与内部版

- 公开版（默认构建）不含任何公司信息；内部版的公司列表在 `internal\companies.txt`（git-ignored，示例 `internal\companies.example.txt`）。
- 文档、测试、示例只用虚构数据（如 `示例科技有限公司`、`91110000MA00000001`）。
