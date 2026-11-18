using System; using System.Linq; using PiaoMate; using UglyToad.PdfPig;
class P{ static void Main(string[] a){
  bool dump = a.Length > 0 && a[0] == "--dump";
  foreach(var f in a.Where(x => x != "--dump")){
    if (dump) { using(var d=PdfDocument.Open(f)){ Console.WriteLine(d.NumberOfPages+" pages"); foreach(var p in d.GetPages()){ Console.WriteLine(p.Width+"x"+p.Height); foreach(var w in p.GetWords()) Console.WriteLine($"{w.BoundingBox.Left:F0},{w.BoundingBox.Bottom:F0}-{w.BoundingBox.Right:F0} {w.Text}"); } } }
    else { var i=InvoiceParser.Parse(f); Console.WriteLine($"ok={i.Ok} err={i.Error} date={i.Date} buyer=[{i.BuyerName}] tax=[{i.BuyerTaxId}] seller=[{i.SellerName}] item=[{i.Item}] amt={i.Amount}"); }
  } } }
