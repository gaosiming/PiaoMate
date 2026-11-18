using System;
using System.Globalization;
using System.IO;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

/// <summary>
/// 生成虚构的全电发票 PDF（版式与真实发票的关键文字位置一致），供测试使用，避免仓库中出现真实发票。
/// 使用系统自带中文字体（等线 Deng.ttf，退回黑体 simhei.ttf）。
/// </summary>
class SampleInvoice
{
    public string Number = "", BuyerName = "", BuyerTaxId = "", SellerName = "", SellerTaxId = "", Item = "";
    public DateTime Date;
    public decimal Amount;

    static readonly string[] Fonts = { "Deng.ttf", "simhei.ttf" };

    /// <summary>可用的中文 TrueType 字体；没有时返回 null（相关测试跳过）。</summary>
    public static string FontPath()
    {
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var f in Fonts) { string p = Path.Combine(dir, f); if (File.Exists(p)) return p; }
        return null;
    }

    public byte[] Build()
    {
        string fontPath = FontPath();
        if (fontPath == null) throw new InvalidOperationException("未找到中文字体");
        // 黑体没有半角 ¥，用全角 ￥（解析器两者都支持）
        string yen = Path.GetFileName(fontPath).Equals("Deng.ttf", StringComparison.OrdinalIgnoreCase) ? "¥" : "￥";
        var b = new PdfDocumentBuilder();
        var font = b.AddTrueTypeFont(File.ReadAllBytes(fontPath));
        var page = b.AddPage(UglyToad.PdfPig.Content.PageSize.A4, true); // 纵向，宽 595
        Action<string, double, double, double> T = (s, x, y, size) => page.AddText(s, size, new PdfPoint(x, y), font);
        string amt = Amount.ToString("F2", CultureInfo.InvariantCulture);

        T("电子发票（普通发票）", 210, 800, 14);
        T("发票号码：", 400, 780, 8); T(Number, 450, 780, 8);
        T("开票日期：", 400, 768, 8); T(Date.ToString("yyyy年MM月dd日"), 450, 768, 8);
        // 购买方（左半页）/ 销售方（右半页）
        T("购", 20, 730, 8); T("销", 300, 730, 8);
        T("名称：" + BuyerName, 40, 720, 8); T("名称：" + SellerName, 320, 720, 8);
        T("统一社会信用代码/纳税人识别号：", 40, 705, 8); T(BuyerTaxId, 175, 705, 8);
        T("统一社会信用代码/纳税人识别号：", 320, 705, 8); T(SellerTaxId, 455, 705, 8);
        // 项目表格
        T("项目名称", 40, 650, 8); T("单", 200, 650, 8); T("数量", 240, 650, 8); T("金额", 400, 650, 8);
        T(Item, 40, 635, 8); T("1", 240, 635, 8); T(amt, 400, 635, 8);
        T("合", 40, 600, 8); T("计", 80, 600, 8); T(yen + amt, 400, 600, 8);
        T("价税合计（大写）", 40, 585, 8); T("（小写）", 380, 585, 8); T(yen + amt, 420, 585, 8);
        T("开票人：测试", 40, 560, 8);
        return b.Build();
    }

    public string Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, Build());
        return path;
    }

    /// <summary>测试用的两张虚构发票。</summary>
    public static readonly SampleInvoice A = new SampleInvoice
    {
        Number = "26110000000000000001", Date = new DateTime(2026, 8, 27), BuyerName = "示例科技有限公司", BuyerTaxId = "91110000MA00000001",
        SellerName = "某某餐饮有限公司", SellerTaxId = "91110000MA0000000X", Item = "*餐饮服务*餐饮服务", Amount = 34.92m
    };
    public static readonly SampleInvoice B = new SampleInvoice
    {
        Number = "26110000000000000002", Date = new DateTime(2026, 11, 11), BuyerName = "示例（北京）贸易有限公司", BuyerTaxId = "91110000MA00000002",
        SellerName = "某某超市有限公司", SellerTaxId = "91110000MA0000000Y", Item = "*食品*矿泉水", Amount = 33.08m
    };
}
