using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace QRSnap
{
    // 预处理流水线验收：对原图跑一遍固定的截图预处理，产出 OCR 推理图与彩色预览 WebP，
    // 打印尺寸 / 耗时 / 编码信息。产物只写到调用方给的本工程子目录。
    internal static class PreprocessDemo
    {
        public static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("用法：PreprocessDemo <原图> <输出目录>");
                return;
            }
            string srcPath = args[0];
            string outDir = args[1];
            Directory.CreateDirectory(outDir);

            using (Bitmap src = new Bitmap(srcPath))
            {
                Console.WriteLine("原图 " + src.Width + "x" + src.Height + "  像素格式=" + src.PixelFormat);

                // 推理分支：长边 1600 + 灰度
                Stopwatch sw = Stopwatch.StartNew();
                PreparedImage prep = OcrImage.ForOcr(src);
                sw.Stop();
                try
                {
                    string file = Path.Combine(outDir, "ocr.png");
                    prep.Bitmap.Save(file, ImageFormat.Png);
                    Rectangle probe = new Rectangle(src.Width / 4, src.Height / 4, 100, 40);
                    Console.WriteLine("[OCR ] " + prep.Bitmap.Width + "x" + prep.Bitmap.Height
                        + "  " + sw.ElapsedMilliseconds + " ms  " + prep.Trace
                        + "  文件 " + new FileInfo(file).Length / 1024 + " KB");
                    Console.WriteLine("       MapBack(" + probe + ") = " + prep.MapBack(probe)
                        + "  scale=" + prep.ScaleX.ToString("0.####") + "/" + prep.ScaleY.ToString("0.####"));
                }
                finally { prep.Dispose(); }

                // 预览分支：彩色，只做 Lanczos4 缩小
                Stopwatch sw2 = Stopwatch.StartNew();
                Bitmap preview = OcrImage.ForPreview(src);
                sw2.Stop();
                string note;
                Stopwatch sw3 = Stopwatch.StartNew();
                byte[] webp = WebpCodec.Encode(preview, OcrImage.WebpQuality, true, false, out note);
                sw3.Stop();
                try
                {
                    Console.WriteLine("[PVW ] " + preview.Width + "x" + preview.Height
                        + "  缩放 " + sw2.ElapsedMilliseconds + " ms  编码 " + sw3.ElapsedMilliseconds + " ms  " + note);
                    if (webp != null) File.WriteAllBytes(Path.Combine(outDir, "preview.webp"), webp);
                    else preview.Save(Path.Combine(outDir, "preview.png"), ImageFormat.Png);
                }
                finally { preview.Dispose(); }
            }
        }
    }
}
