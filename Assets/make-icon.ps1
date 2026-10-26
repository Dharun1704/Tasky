# Builds Tasky.ico and Tasky.png from the two source images: crops each to a square, renders every
# icon size with stepwise high-quality downscaling, and writes a preview sheet.
# Run: powershell -ExecutionPolicy Bypass -File Assets\make-icon.ps1
param(
  [string]$Main = "$PSScriptRoot\source-main.webp",    # the full icon, 32 px and up
  [string]$Small = "$PSScriptRoot\source-small.webp",  # the flat version, for 16-24 px
  [string]$Out = $PSScriptRoot,
  [string]$Preview = "$env:TEMP\tasky-icon-preview.png",  # every size, both versions, on light and dark
  [int]$SmallUpTo = 24
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase, System.Xaml
Add-Type -ReferencedAssemblies PresentationCore, WindowsBase, System.Xaml -TypeDefinition @'
using System; using System.IO; using System.Collections.Generic;
using System.Windows; using System.Windows.Media; using System.Windows.Media.Imaging;
public static class IconMaker {
  public static BitmapSource Load(string path) {
    using (var s = File.OpenRead(path)) {
      var d = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
      var c = new FormatConvertedBitmap(d.Frames[0], PixelFormats.Pbgra32, null, 0); c.Freeze(); return c;
    }
  }
  static BitmapSource Render(BitmapSource src, double x, double y, double side, int size, double pad) {
    var v = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(v, BitmapScalingMode.HighQuality);
    using (var dc = v.RenderOpen()) {
      double k = size * (1 - 2 * pad) / side;
      dc.PushTransform(new TranslateTransform(size * pad - x * k, size * pad - y * k));
      dc.PushTransform(new ScaleTransform(k, k));
      dc.DrawImage(src, new Rect(0, 0, src.PixelWidth, src.PixelHeight));
    }
    var rt = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); rt.Render(v); rt.Freeze(); return rt;
  }
  // Halving steps first, so thin edges don't alias when going from ~1000 px to 16.
  public static BitmapSource Scale(BitmapSource src, double x, double y, double side, int size, double pad) {
    while (side / 2 >= size * (1 - 2 * pad) * 1.5) {
      int n = (int)Math.Ceiling(side / 2);
      src = Render(src, x, y, side, n, 0); x = 0; y = 0; side = n;
    }
    return Render(src, x, y, side, size, pad);
  }
  public static byte[] Png(BitmapSource b) {
    var e = new PngBitmapEncoder(); e.Frames.Add(BitmapFrame.Create(b));
    using (var m = new MemoryStream()) { e.Save(m); return m.ToArray(); }
  }
  public static void Ico(string path, int[] sizes, BitmapSource[] frames) {
    var pngs = new List<byte[]>(); foreach (var f in frames) pngs.Add(Png(f));
    using (var w = new BinaryWriter(File.Create(path))) {
      w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
      int offset = 6 + 16 * sizes.Length;
      for (int i = 0; i < sizes.Length; i++) {
        w.Write((byte)(sizes[i] % 256)); w.Write((byte)(sizes[i] % 256)); w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32); w.Write((uint)pngs[i].Length); w.Write((uint)offset);
        offset += pngs[i].Length;
      }
      foreach (var p in pngs) w.Write(p);
    }
  }
  public static void Sheet(string path, int[] sizes, BitmapSource[] top, BitmapSource[] bottom) {
    int W = 1000, H = 560; var v = new DrawingVisual();
    using (var dc = v.RenderOpen()) {
      dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H / 2));
      dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, new Rect(0, H / 2, W, H / 2));
      for (int band = 0; band < 2; band++) for (int row = 0; row < 2; row++) {
        double x = 16; var set = row == 0 ? top : bottom;
        for (int i = 0; i < sizes.Length; i++) {
          dc.DrawImage(set[i], new Rect(x, band * H / 2 + 12 + row * 136, sizes[i], sizes[i]));
          x += sizes[i] + 18;
        }
      }
    }
    var rt = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32); rt.Render(v);
    File.WriteAllBytes(path, Png(rt));
  }
  public static string Run(string mainPath, string smallPath, string outDir, string preview, int smallUpTo) {
    var main = Load(mainPath); var small = Load(smallPath);
    // Artwork bounds measured from the images (alpha > 24), squared up around their centres.
    double mx = 627 - 511, my = 625 - 511, ms = 1022, sx = 768 - 518, sy = 0, ss = 1036;
    int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
    var sm = new BitmapSource[sizes.Length]; var mn = new BitmapSource[sizes.Length]; var pick = new BitmapSource[sizes.Length];
    for (int i = 0; i < sizes.Length; i++) {
      sm[i] = Scale(small, sx, sy, ss, sizes[i], 0.02); mn[i] = Scale(main, mx, my, ms, sizes[i], 0.02);
      pick[i] = sizes[i] <= smallUpTo ? sm[i] : mn[i];
    }
    Directory.CreateDirectory(outDir);
    Ico(Path.Combine(outDir, "Tasky.ico"), sizes, pick);
    File.WriteAllBytes(Path.Combine(outDir, "Tasky.png"), Png(Scale(main, mx, my, ms, 256, 0.02)));
    Sheet(preview, sizes, sm, mn);
    return "ico " + new FileInfo(Path.Combine(outDir, "Tasky.ico")).Length + " bytes";
  }
}
'@

[IconMaker]::Run($Main, $Small, $Out, $Preview, $SmallUpTo)
