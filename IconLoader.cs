using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tasky;

/// <summary>Loads large, alpha-correct icons for files and windows.</summary>
internal static class IconLoader
{
    /// <summary>Icons are fetched in steps of this many pixels as they magnify.</summary>
    public const int Step = 4;

    const int SourcePx = 256;

    static readonly Dictionary<string, BitmapSource?> Sources = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<(string, int), ImageSource?> Cache = new();

    /// <summary>
    /// The icon pre-shrunk to exactly <paramref name="px"/> pixels. Shrinking once here with an
    /// area filter beats every alternative tried: WPF scaling at draw time (and RenderTargetBitmap)
    /// aliases on large ratios, and asking Windows for an odd size returns a hard-edged icon.
    /// </summary>
    public static ImageSource? FromPath(string path, int px)
    {
        var key = (path.ToLowerInvariant(), px);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? image = null;
        if (Source(path) is { } src)
        {
            image = src.PixelWidth == px ? src : Downscale(src, px);
        }

        Cache[key] = image;
        return image;
    }

    /// <summary>
    /// A small icon (e.g. for a file in a menu), fetched at a standard size so it's crisp, and not
    /// cached: menus show a handful at a time and they come and go.
    /// </summary>
    public static BitmapSource? Small(string path, int px = 32)
    {
        try
        {
            var iid = typeof(Native.IShellItemImageFactory).GUID;
            Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            try
            {
                var size = new Native.SIZE { cx = px, cy = px };
                if (factory.GetImage(size, Native.SIIGBF_ICONONLY | Native.SIIGBF_BIGGERSIZEOK, out var hbm) != 0 || hbm == IntPtr.Zero) return null;
                try
                {
                    var image = FromHBitmap(hbm);
                    return image.PixelWidth > px * 2 ? Downscale(image, px) : image;
                }
                finally { Native.DeleteObject(hbm); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch { return null; }
    }

    /// <summary>Drops every cached size of an app's icon, once the app has left the dock.</summary>
    public static void Forget(string path)
    {
        Sources.Remove(path);
        var lower = path.ToLowerInvariant();
        foreach (var key in Cache.Keys.Where(k => k.Item1 == lower).ToList()) Cache.Remove(key);
    }

    /// <summary>Drops cached sizes outside what the dock can currently draw (after an icon-size change).</summary>
    public static void Trim(int minPx, int maxPx)
    {
        foreach (var key in Cache.Keys.Where(k => k.Item2 < minPx || k.Item2 > maxPx).ToList()) Cache.Remove(key);
    }

    static BitmapSource? Source(string path)
    {
        if (Sources.TryGetValue(path, out var cached)) return cached;

        BitmapSource? image = null;
        try
        {
            var iid = typeof(Native.IShellItemImageFactory).GUID;
            Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            try
            {
                var size = new Native.SIZE { cx = SourcePx, cy = SourcePx };
                if (factory.GetImage(size, Native.SIIGBF_ICONONLY | Native.SIIGBF_BIGGERSIZEOK, out var hbm) == 0 && hbm != IntPtr.Zero)
                {
                    try { image = FromHBitmap(hbm); }
                    finally { Native.DeleteObject(hbm); }
                }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch (Exception ex) { Log.Error(ex, $"icon for {path}"); }

        Sources[path] = image;
        return image;
    }

    /// <summary>
    /// Area-averaging shrink (what the Fant filter does): each output pixel is the coverage-weighted
    /// mean of the source pixels under it. Averaging is correct because the data is premultiplied.
    /// </summary>
    static BitmapSource Downscale(BitmapSource src, int px)
    {
        if (src.Format != PixelFormats.Pbgra32) src = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
        int sw = src.PixelWidth, sh = src.PixelHeight;
        var s = new byte[sw * sh * 4];
        src.CopyPixels(s, sw * 4, 0);

        var d = new byte[px * px * 4];
        double fx = (double)sw / px, fy = (double)sh / px;
        Span<double> acc = stackalloc double[4];
        for (int y = 0; y < px; y++)
        {
            double y0 = y * fy, y1 = y0 + fy;
            for (int x = 0; x < px; x++)
            {
                double x0 = x * fx, x1 = x0 + fx, area = 0;
                acc.Clear();
                for (int sy = (int)y0; sy < Math.Min(sh, (int)Math.Ceiling(y1)); sy++)
                {
                    double wy = Math.Min(sy + 1, y1) - Math.Max(sy, y0);
                    for (int sx = (int)x0; sx < Math.Min(sw, (int)Math.Ceiling(x1)); sx++)
                    {
                        double w = wy * (Math.Min(sx + 1, x1) - Math.Max(sx, x0));
                        int i = (sy * sw + sx) * 4;
                        acc[0] += s[i] * w; acc[1] += s[i + 1] * w; acc[2] += s[i + 2] * w; acc[3] += s[i + 3] * w;
                        area += w;
                    }
                }
                int o = (y * px + x) * 4;
                for (int c = 0; c < 4; c++) d[o + c] = (byte)Math.Round(acc[c] / area);
            }
        }

        var result = BitmapSource.Create(px, px, 96, 96, PixelFormats.Pbgra32, null, d, px * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Rounds a pixel size up to the step the cache holds.</summary>
    public static int Bucket(double px) => Math.Max(Step, (int)Math.Ceiling(px / Step) * Step);

    public static ImageSource? FromWindow(IntPtr hwnd)
    {
        IntPtr icon = IntPtr.Zero;
        Native.SendMessageTimeout(hwnd, Native.WM_GETICON, Native.ICON_BIG, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 100, out icon);
        if (icon == IntPtr.Zero)
            Native.SendMessageTimeout(hwnd, Native.WM_GETICON, Native.ICON_SMALL2, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 100, out icon);
        if (icon == IntPtr.Zero) icon = Native.GetClassLongPtr(hwnd, Native.GCLP_HICON);
        if (icon == IntPtr.Zero) icon = Native.GetClassLongPtr(hwnd, Native.GCLP_HICONSM);
        if (icon == IntPtr.Zero) return null;

        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    static BitmapSource FromHBitmap(IntPtr hbm)
    {
        Native.GetObject(hbm, Marshal.SizeOf<Native.BITMAP>(), out var bm);
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);

        // Ask for a top-down 32bpp copy so orientation never depends on the source DIB.
        var info = new Native.BITMAPINFO
        {
            biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        var bits = new byte[w * h * 4];
        var dc = Native.GetDC(IntPtr.Zero);
        try { Native.GetDIBits(dc, hbm, 0, (uint)h, bits, ref info, 0); }
        finally { Native.ReleaseDC(IntPtr.Zero, dc); }

        bool hasAlpha = false;
        for (int i = 3; i < bits.Length; i += 4)
            if (bits[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha)
            for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bits, w * 4);
        src.Freeze();
        return src;
    }
}
