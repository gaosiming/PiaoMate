# 参与贡献

欢迎提交问题反馈、开票平台适配和代码改进。

## 隐私第一

发票包含个人和公司信息。无论是 Issue、PR 还是测试数据，都**不要**提交：

- 真实发票 PDF / OFD、发票截图、完整的发票链接或二维码
- 公司名称与税号、手机号、邮箱
- 个人配置 `PiaoMate.config`、内部版公司列表 `internal/companies.txt`

测试与文档只用虚构数据，例如 `示例科技有限公司`、`91110000MA00000001`。

## 开发环境

- Windows 10/11
- [.NET SDK](https://dotnet.microsoft.com/download) 6.0 或更高（目标框架 .NET Framework 4.8）
- 测试需要系统字体「等线」`Deng.ttf` 或「黑体」`simhei.ttf`（用于运行时生成虚构发票 PDF）；没有时用环境变量 `PIAOMATE_TEST_FONT` 指定任意中文 `.ttf`

```powershell
dotnet build src -c Release -o build\out          # 构建，得到 build\out\PiaoMate.exe
dotnet run --project tests\PiaoMate.Tests          # 运行测试
```

## 代码结构

| 位置 | 内容 |
|---|---|
| `src/InvoiceParser.cs` | 按文字坐标解析全电发票 PDF |
| `src/Matcher.cs` | 凑金额（子集和） |
| `src/Organizer.cs` | 按 `抬头_税号` 分类整理 |
| `src/Downloader.cs` | 链接 / 二维码下载 |
| `src/SiteAdapters.cs` | 开票平台适配器 |
| `src/IssueForm.cs` | 扫码开票（WebView2 自动填写） |
| `tests/PiaoMate.Tests/` | 控制台测试；`SampleInvoice.cs` 生成虚构发票 |
| `tools/release.ps1` | 版本号、更新日志与打包 |

## 适配新的开票平台

1. 在 `src/SiteAdapters.cs` 中继承 `SiteAdapter`：
   - `CanHandle(Uri)`：根据**跟随跳转后的最终网址**判断是否为该平台的发票页；
   - `Resolve(Uri, SiteContext)`：调用平台接口，把 PDF 地址放进 `SiteResult.Files`；拿不到时填写 `Message`，需要用户在浏览器中继续（验证码等）时设 `NeedsBrowser = true`。
2. 把新适配器加入 `SiteAdapter.All`。
3. 在 `tests/PiaoMate.Tests/Program.cs` 中用注入的 GET / POST 函数模拟平台接口（参考 `NuonuoTests`、`BaiwangTests`），数据全部虚构，测试不联网。
4. 手动联网验证：`dotnet run --project tests\PiaoMate.Tests -- --live <网址>`。

扫码开票只做**自动填写**，任何情况下都不要自动点击提交按钮。

## 提交规范

- 提交信息使用简短的祈使句，例如「适配某某平台发票链接」。
- 用户可见的改动请记录到更新日志：`.\tools\release.ps1 -AddNote "说明" -Type 新增|改进|修复`。
- 版本发布由维护者通过 `.\tools\release.ps1` 完成。
