using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace InputPlus.Core.Windows;

/// <summary>
/// 面板背后的"毛玻璃"底图。
///
/// 为什么不用 DWM 的亚克力：<see cref="InputPlus.UI.Theme"/> 里的 WindowChrome 只对
/// 非分层窗口（AllowsTransparency=false）生效，而 Qbar 必须是分层窗口（它要圆角 + 半透明 + 无边框）。
/// 分层窗口拿不到系统模糊，只能自己画：抓一张背后的屏幕，模糊它，再垫到面板下面。
///
/// 抓屏用的是 GDI 的 CopyFromScreen（跟 RenderTools 里拍"真机长什么样"同一套），
/// 只在面板弹出的那一瞬间调用一次，代价是几毫秒。
/// </summary>
internal static class BackdropBlur
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const uint WDA_NONE = 0x00000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    /// <summary>
    /// 把窗口从屏幕捕获里排除掉（Win10 2004+），这样抓到的画面里没有我们自己的面板。
    /// 老系统 / 被系统拒绝时返回 false，调用方改用"把窗口临时藏一帧"的兜底。
    /// </summary>
    public static bool TryExcludeFromCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try { return SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// 撤销 <see cref="TryExcludeFromCapture"/>。
    ///
    /// 这一步不能省：排除标记是**一直生效**的，只加不减会让面板在截图、
    /// 录屏、共享屏幕里直接消失（用户截个图给朋友看，面板是块空白）。
    /// 所以抓完立刻归零，只排除"抓的那一瞬间"。
    /// </summary>
    public static void RestoreCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try { SetWindowDisplayAffinity(hwnd, WDA_NONE); }
        catch (Exception) { }
    }

    /// <summary>
    /// 抓一块屏幕区域（物理像素，左上/右下），缩小 → 三次方框模糊 → 放大回原尺寸。
    /// 失败（区域非法、屏幕被保护、内存不足）返回 null，调用方保持纯色底。
    /// </summary>
    public static BitmapSource? Capture(System.Windows.Point topLeftDip, System.Windows.Point bottomRightDip, int radius = 30, int downscale = 4)
    {
        int x = (int)Math.Floor(Math.Min(topLeftDip.X, bottomRightDip.X));
        int y = (int)Math.Floor(Math.Min(topLeftDip.Y, bottomRightDip.Y));
        int w = (int)Math.Ceiling(Math.Abs(bottomRightDip.X - topLeftDip.X));
        int h = (int)Math.Ceiling(Math.Abs(bottomRightDip.Y - topLeftDip.Y));
        if (w < 32 || h < 32 || w > 8192 || h > 8192) return null;

        using var full = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);
        }

        int sw = Math.Max(8, w / Math.Max(1, downscale));
        int sh = Math.Max(8, h / Math.Max(1, downscale));
        using var small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(full, new Rectangle(0, 0, sw, sh), new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);
        }

        int r = Math.Max(2, radius / Math.Max(1, downscale));
        for (int pass = 0; pass < 3; pass++) BoxBlur(small, r);

        using var back = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(back))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(small, new Rectangle(0, 0, w, h), new Rectangle(0, 0, sw, sh), GraphicsUnit.Pixel);
        }

        BitmapData data = back.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var bytes = new byte[stride * h];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var src = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bytes, stride);
            src.Freeze();
            return src;
        }
        finally
        {
            back.UnlockBits(data);
        }
    }

    /// <summary>三次方框模糊已经足够接近高斯，而且单次是 O(n)，跟半径无关。</summary>
    private static void BoxBlur(Bitmap bmp, int radius)
    {
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int w = bmp.Width, h = bmp.Height, stride = data.Stride;
            int rowBytes = w * 4;
            var buf = new byte[stride * h];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            var tmp = new byte[buf.Length];

            Horizontal(buf, tmp, w, h, stride, radius);
            Vertical(tmp, buf, w, h, stride, radius);
            Horizontal(buf, tmp, w, h, stride, radius);
            Vertical(tmp, buf, w, h, stride, radius);
            Horizontal(buf, tmp, w, h, stride, radius);
            Vertical(tmp, buf, w, h, stride, radius);

            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            _ = rowBytes;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static void Horizontal(byte[] src, byte[] dst, int w, int h, int stride, int radius)
    {
        int sumB = 0, sumG = 0, sumR = 0;
        int count = radius * 2 + 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            sumB = sumG = sumR = 0;
            for (int i = -radius; i <= radius; i++)
            {
                int x = Math.Clamp(i, 0, w - 1) * 4;
                sumB += src[row + x];
                sumG += src[row + x + 1];
                sumR += src[row + x + 2];
            }
            for (int x = 0; x < w; x++)
            {
                int o = row + x * 4;
                dst[o] = (byte)(sumB / count);
                dst[o + 1] = (byte)(sumG / count);
                dst[o + 2] = (byte)(sumR / count);
                dst[o + 3] = 255;

                int add = Math.Clamp(x + radius + 1, 0, w - 1) * 4;
                int sub = Math.Clamp(x - radius, 0, w - 1) * 4;
                sumB += src[row + add] - src[row + sub];
                sumG += src[row + add + 1] - src[row + sub + 1];
                sumR += src[row + add + 2] - src[row + sub + 2];
            }
        }
    }

    private static void Vertical(byte[] src, byte[] dst, int w, int h, int stride, int radius)
    {
        int count = radius * 2 + 1;
        for (int x = 0; x < w; x++)
        {
            int col = x * 4;
            int sumB = 0, sumG = 0, sumR = 0;
            for (int i = -radius; i <= radius; i++)
            {
                int y = Math.Clamp(i, 0, h - 1);
                sumB += src[y * stride + col];
                sumG += src[y * stride + col + 1];
                sumR += src[y * stride + col + 2];
            }
            for (int y = 0; y < h; y++)
            {
                int o = y * stride + col;
                dst[o] = (byte)(sumB / count);
                dst[o + 1] = (byte)(sumG / count);
                dst[o + 2] = (byte)(sumR / count);
                dst[o + 3] = 255;

                int add = Math.Clamp(y + radius + 1, 0, h - 1) * stride + col;
                int sub = Math.Clamp(y - radius, 0, h - 1) * stride + col;
                sumB += src[add] - src[sub];
                sumG += src[add + 1] - src[sub + 1];
                sumR += src[add + 2] - src[sub + 2];
            }
        }
    }
}
