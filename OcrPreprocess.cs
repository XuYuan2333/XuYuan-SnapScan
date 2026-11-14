using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace QRSnap
{

    // 预处理结果。推理图和预览图是两次独立生成，绝不复用同一张 Bitmap。
    internal sealed class PreparedImage : IDisposable
    {
        public Bitmap Bitmap;        // 送进 OCR 的图（可能已灰度 / 已增强 / 已缩放）
        public bool Owned;           // Bitmap 是否由本对象创建，需要 Dispose
        public double ScaleX = 1.0;  // 推理图 / 原图
        public double ScaleY = 1.0;
        public string Trace = string.Empty;

        public bool Scaled { get { return Math.Abs(ScaleX - 1.0) > 1e-6 || Math.Abs(ScaleY - 1.0) > 1e-6; } }

        // 把推理图上的坐标换算回原图坐标（遮罩对齐必须用原图坐标）
        public Rectangle MapBack(Rectangle r)
        {
            if (!Scaled) return r;
            int x = (int)Math.Round(r.X / ScaleX);
            int y = (int)Math.Round(r.Y / ScaleY);
            int w = (int)Math.Round(r.Width / ScaleX);
            int h = (int)Math.Round(r.Height / ScaleY);
            if (w < 1) w = 1;
            if (h < 1) h = 1;
            return new Rectangle(x, y, w, h);
        }

        public void Dispose()
        {
            if (Owned && Bitmap != null)
            {
                try { Bitmap.Dispose(); } catch (Exception) { }
            }
            Bitmap = null;
        }
    }

    // 送 OCR 的图和预览图是两条独立流水线，绝不复用同一张 Bitmap。
    // OCR 侧固定彩色原尺寸（不再做缩放/灰度预处理），预览侧仍按 1600 上限缩小，只用于界面显示。
    internal static class OcrImage
    {
        public const int NoScaleFloor = 640;      // 长边 ≤ 640 时不缩放
        public const int PreviewMaxSide = 1600;   // 预览图长边上限
        public const int WebpQuality = 90;        // 预览图 WebP 质量

        // ---------- 推理分支：彩色原尺寸直接送 OCR ----------
        // 原来的「长边超上限先 Lanczos4 缩小、再转灰度」已经删掉：检测端 Detect() 本来就会把长边统一到
        // 1280，外层再缩一次等于多插值一遍，小字和长行反而更容易糊，实际收益只有峰值内存小一点。
        public static PreparedImage ForOcr(Bitmap src)
        {
            PreparedImage r = new PreparedImage();
            if (src == null) return r;

            int sw = src.Width, sh = src.Height;
            // 只做一次 24bpp 拷贝，PP-OCR 的 ReadPixels 固定按 24bpp 读
            try { r.Bitmap = CopyRgb24(src, sw, sh); }
            catch (Exception)
            {
                if (r.Bitmap != null) { try { r.Bitmap.Dispose(); } catch (Exception) { } r.Bitmap = null; }
                throw;
            }
            r.Owned = true;
            r.ScaleX = 1.0;
            r.ScaleY = 1.0;
            r.Trace = "pre " + sw + "x" + sh + " 彩色原尺寸";
            return r;
        }
        // ---------- 预览 / 上传分支：始终彩色，只做 Lanczos4 等比缩小 ----------
        public static Bitmap ForPreview(Bitmap src)
        {
            if (src == null) return null;
            int sw = src.Width, sh = src.Height;
            int maxSide = Math.Max(sw, sh);
            bool wantScale = maxSide > NoScaleFloor && maxSide > PreviewMaxSide;
            if (!wantScale) return CopyBgra(src);

            double scale = (double)PreviewMaxSide / maxSide;
            int dw = Math.Max(1, (int)Math.Round(sw * scale));
            int dh = Math.Max(1, (int)Math.Round(sh * scale));
            if (dw > sw) dw = sw;
            if (dh > sh) dh = sh;
            byte[] bgra = ReadBgra(src, sw, sh);
            byte[] ra = Resample.Bgra(bgra, sw, sh, dw, dh);
            return Bitmaps.FromBgra(ra, dw, dh);
        }

        public static string PreviewTrace(Bitmap src)
        {
            if (src == null) return "preview=null";
            int sw = src.Width, sh = src.Height;
            int maxSide = Math.Max(sw, sh);
            if (maxSide > NoScaleFloor && maxSide > PreviewMaxSide)
            {
                int dw = (int)Math.Round(sw * ((double)PreviewMaxSide / maxSide));
                int dh = (int)Math.Round(sh * ((double)PreviewMaxSide / maxSide));
                return "preview 彩色 " + sw + "x" + sh + "\u2192" + dw + "x" + dh + "(L4)";
            }
            return "preview 彩色 " + sw + "x" + sh + " 原尺寸";
        }

        public static Bitmap CopyBgra(Bitmap src)
        {
            int w = src.Width, h = src.Height;
            byte[] px = ReadBgra(src, w, h);
            return Bitmaps.FromBgra(px, w, h);
        }

        // 彩色原样拷贝成 24bpp（PP-OCR 的 ReadPixels 固定按 24bpp 读）
        public static Bitmap CopyRgb24(Bitmap src, int w, int h)
        {
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);
            }
            return dst;
        }

        // ---------- 像素读取 ----------
        internal static byte[] ReadBgra(Bitmap src, int w, int h)
        {
            byte[] dst = new byte[w * h * 4];
            Bitmap work = src;
            Bitmap conv = null;
            try
            {
                if (src.PixelFormat != PixelFormat.Format32bppArgb && src.PixelFormat != PixelFormat.Format32bppRgb)
                {
                    conv = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(conv))
                    {
                        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);
                    }
                    work = conv;
                }
                BitmapData bd = work.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = bd.Stride;
                    byte[] row = new byte[Math.Abs(stride)];
                    for (int y = 0; y < h; y++)
                    {
                        IntPtr p = new IntPtr(bd.Scan0.ToInt64() + (long)y * stride);
                        Marshal.Copy(p, row, 0, row.Length);
                        Buffer.BlockCopy(row, 0, dst, y * w * 4, w * 4);
                    }
                }
                finally { work.UnlockBits(bd); }
                return dst;
            }
            finally
            {
                if (conv != null) { try { conv.Dispose(); } catch (Exception) { } }
            }
        }

        internal static byte[] ReadGray(Bitmap src, int w, int h)
        {
            byte[] bgra = ReadBgra(src, w, h);
            byte[] g = new byte[w * h];
            int n = w * h;
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                int b = bgra[o], gg = bgra[o + 1], r = bgra[o + 2];
                g[i] = (byte)((r * 299 + gg * 587 + b * 114) / 1000);   // BT.601
            }
            return g;
        }
    }

    // ================= Lanczos4 可分离重采样（a = 4） =================
    internal static class Resample
    {
        private const double A = 4.0;
        private const int WeightBits = 14;
        private const int WeightOne = 1 << WeightBits;
        private const int HScale = 8;    // 水平中间结果用 1/8 像素精度

        private sealed class Taps
        {
            public int Start;
            public int Len;
            public int[] Idx;
            public int[] W;
        }

        private static double Kernel(double x)
        {
            x = Math.Abs(x);
            if (x < 1e-9) return 1.0;
            if (x >= A) return 0.0;
            double px = Math.PI * x;
            return (A * Math.Sin(px) * Math.Sin(px / A)) / (px * px);
        }

        private static Taps[] BuildTaps(int srcLen, int dstLen)
        {
            double scale = (double)srcLen / dstLen;
            Taps[] t = new Taps[dstLen];
            for (int d = 0; d < dstLen; d++)
            {
                double center = (d + 0.5) * scale - 0.5;
                int start = (int)Math.Floor(center - A + 1.0);
                Taps tp = new Taps();
                tp.Start = start;
                tp.Len = (int)(2 * A);
                tp.Idx = new int[tp.Len];
                tp.W = new int[tp.Len];
                double sum = 0.0;
                for (int k = 0; k < tp.Len; k++)
                {
                    int s = start + k;
                    double w = Kernel(center - s);
                    if (s < 0) s = 0;
                    else if (s >= srcLen) s = srcLen - 1;
                    tp.Idx[k] = s;
                    tp.W[k] = (int)Math.Round(w * WeightOne);
                    sum += w;
                }
                if (sum <= 1e-9)
                {
                    // 极端缩小时兜底：最近邻
                    int s = (int)Math.Round(center);
                    if (s < 0) s = 0; if (s >= srcLen) s = srcLen - 1;
                    for (int k = 0; k < tp.Len; k++) { tp.Idx[k] = s; tp.W[k] = 0; }
                    tp.W[0] = WeightOne;
                }
                else
                {
                    // 归一化到 1.0（权重已被 clamp 索引影响，按总权重重新分配）
                    int[] w = tp.W;
                    int total = 0;
                    for (int k = 0; k < w.Length; k++) total += w[k];
                    if (total != WeightOne && total > 0)
                    {
                        double fix = (double)WeightOne / total;
                        total = 0;
                        for (int k = 0; k < w.Length; k++) { w[k] = (int)Math.Round(w[k] * fix); total += w[k]; }
                        // 余量补到权重最大的那一档，保证总和精确
                        int diff = WeightOne - total;
                        if (diff != 0)
                        {
                            int big = 0;
                            for (int k = 1; k < w.Length; k++) if (w[k] > w[big]) big = k;
                            w[big] += diff;
                        }
                    }
                }
                t[d] = tp;
            }
            return t;
        }

        // 灰度平面缩放
        public static byte[] Gray(byte[] src, int sw, int sh, int dw, int dh)
        {
            Taps[] hx = BuildTaps(sw, dw);
            Taps[] vy = BuildTaps(sh, dh);

            // 水平 pass：sh 行 × dw 列，1/8 像素定点
            ushort[] mid = new ushort[sh * dw];
            for (int y = 0; y < sh; y++)
            {
                int srow = y * sw;
                int mrow = y * dw;
                for (int x = 0; x < dw; x++)
                {
                    Taps tp = hx[x];
                    int sum = 0;
                    for (int k = 0; k < tp.Len; k++)
                        sum += src[srow + tp.Idx[k]] * tp.W[k];
                    int v = sum >> WeightBits;
                    mid[mrow + x] = (ushort)(v * HScale);
                }
            }

            byte[] dst = new byte[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                Taps tp = vy[y];
                int drow = y * dw;
                for (int x = 0; x < dw; x++)
                {
                    int sum = 0;
                    for (int k = 0; k < tp.Len; k++)
                        sum += mid[(tp.Idx[k]) * dw + x] * tp.W[k];
                    int v = (sum >> WeightBits) / HScale;
                    if (v < 0) v = 0;
                    else if (v > 255) v = 255;
                    dst[drow + x] = (byte)v;
                }
            }
            return dst;
        }

        // 彩色 BGRA 缩放，返回 BGRA
        public static byte[] Bgra(byte[] src, int sw, int sh, int dw, int dh)
        {
            Taps[] hx = BuildTaps(sw, dw);
            Taps[] vy = BuildTaps(sh, dh);

            ushort[] mid = new ushort[sh * dw * 3];
            for (int y = 0; y < sh; y++)
            {
                int srow = y * sw * 4;
                int mrow = y * dw * 3;
                for (int x = 0; x < dw; x++)
                {
                    Taps tp = hx[x];
                    int sb = 0, sg = 0, sr = 0;
                    for (int k = 0; k < tp.Len; k++)
                    {
                        int o = srow + tp.Idx[k] * 4;
                        int w = tp.W[k];
                        sb += src[o] * w;
                        sg += src[o + 1] * w;
                        sr += src[o + 2] * w;
                    }
                    int mo = mrow + x * 3;
                    mid[mo] = (ushort)(((sb >> WeightBits) * HScale));
                    mid[mo + 1] = (ushort)(((sg >> WeightBits) * HScale));
                    mid[mo + 2] = (ushort)(((sr >> WeightBits) * HScale));
                }
            }

            byte[] dst = new byte[dw * dh * 4];
            for (int y = 0; y < dh; y++)
            {
                Taps tp = vy[y];
                int drow = y * dw * 4;
                for (int x = 0; x < dw; x++)
                {
                    int sb = 0, sg = 0, sr = 0;
                    for (int k = 0; k < tp.Len; k++)
                    {
                        int mo = (tp.Idx[k] * dw + x) * 3;
                        int w = tp.W[k];
                        sb += mid[mo] * w;
                        sg += mid[mo + 1] * w;
                        sr += mid[mo + 2] * w;
                    }
                    int o = drow + x * 4;
                    int b = (int)(((long)sb >> WeightBits) / HScale);
                    int g = (int)(((long)sg >> WeightBits) / HScale);
                    int r = (int)(((long)sr >> WeightBits) / HScale);
                    dst[o] = (byte)(b < 0 ? 0 : (b > 255 ? 255 : b));
                    dst[o + 1] = (byte)(g < 0 ? 0 : (g > 255 ? 255 : g));
                    dst[o + 2] = (byte)(r < 0 ? 0 : (r > 255 ? 255 : r));
                    dst[o + 3] = 255;
                }
            }
            return dst;
        }
    }

    // ================= Bitmap 组装 =================
    internal static class Bitmaps
    {
        public static Bitmap FromBgra(byte[] bgra, int w, int h)
        {
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                byte[] row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Buffer.BlockCopy(bgra, y * w * 4, row, 0, w * 4);
                    IntPtr p = new IntPtr(bd.Scan0.ToInt64() + (long)y * stride);
                    Marshal.Copy(row, 0, p, row.Length);
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        // 灰度图也输出 24bppRgb（PP-OCR 读像素固定要求该格式）
        public static Bitmap FromGray(byte[] gray, int w, int h)
        {
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = bd.Stride;
                byte[] row = new byte[Math.Abs(stride)];
                for (int y = 0; y < h; y++)
                {
                    int ro = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int o = x * 3;
                        byte v = gray[ro + x];
                        row[o] = v; row[o + 1] = v; row[o + 2] = v;
                    }
                    IntPtr p = new IntPtr(bd.Scan0.ToInt64() + (long)y * stride);
                    Marshal.Copy(row, 0, p, w * 3);
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }
    }

    // ================= WebP 编码（cwebp 子进程；找不到时回退 PNG） =================
    internal static class WebpCodec
    {
        private static string _cwebp;
        private static bool _probed;

        public static string CwebpPath()
        {
            if (_probed) return _cwebp;
            _probed = true;
            string[] candidates = new string[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cwebp.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "cwebp.exe")
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                try { if (File.Exists(candidates[i])) { _cwebp = candidates[i]; return _cwebp; } }
                catch (Exception) { }
            }
            return null;
        }

        public static bool Available { get { return CwebpPath() != null; } }

        // sharpYuv：libwebp 的锐利色度下采样。VP8 有损固定 4:2:0，无法真正 4:4:4；
        // 需要严格不降采样色度时把 lossless 置 true（VP8L 天然 4:4:4）。
        public static byte[] Encode(Bitmap bmp, int quality, bool sharpYuv, bool lossless, out string note)
        {
            note = null;
            string cwebp = CwebpPath();
            if (cwebp == null) { note = "未找到 cwebp.exe，预览回退 PNG"; return null; }
            if (bmp == null) { note = "空图"; return null; }

            string tmp = Path.Combine(Path.GetTempPath(), "XuYuan-SnapScan", "webp");
            string src = null, dst = null;
            try
            {
                Directory.CreateDirectory(tmp);
                long tick = DateTime.Now.Ticks;
                src = Path.Combine(tmp, "p" + tick + ".png");
                dst = Path.Combine(tmp, "p" + tick + ".webp");
                bmp.Save(src, ImageFormat.Png);

                StringBuilder args = new StringBuilder();
                if (lossless) args.Append("-lossless -z 6 ");
                else
                {
                    args.Append("-q ").Append(quality).Append(' ');
                    if (sharpYuv) args.Append("-sharp_yuv ");
                    args.Append("-m 4 ");
                }
                args.Append("-metadata none ");
                args.Append('"').Append(src).Append("\" -o \"").Append(dst).Append('"');

                ProcessStartInfo psi = new ProcessStartInfo(cwebp, args.ToString());
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(8000))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        note = "cwebp 超时";
                        return null;
                    }
                }
                if (!File.Exists(dst)) { note = "cwebp 未产出文件"; return null; }
                byte[] bytes = File.ReadAllBytes(dst);
                if (bytes.Length < 32) { note = "cwebp 输出过小"; return null; }
                note = (lossless ? "lossless 4:4:4" : ("q" + quality + (sharpYuv ? " sharp_yuv" : ""))) + " " + bytes.Length + "B";
                return bytes;
            }
            catch (Exception ex)
            {
                note = "WebP 编码失败：" + ex.Message;
                return null;
            }
            finally
            {
                try { if (src != null && File.Exists(src)) File.Delete(src); } catch (Exception) { }
                try { if (dst != null && File.Exists(dst)) File.Delete(dst); } catch (Exception) { }
            }
        }
    }
}
