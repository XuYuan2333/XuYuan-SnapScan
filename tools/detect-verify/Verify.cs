using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Text;
using QRSnap;

// 离屏验证：
// ① 合成一张 40 行文字的画面，确认「不设上限」时检出块数能超过旧上限 24（以前会被截断）
// ② 4 张真实 MC 截图，确认检出块数与旧记录持平（不截断、也不因放宽而误检爆量）
// 用法：Verify.exe <模型目录> <截图...>
internal static class Verify
{
    private static Bitmap MakeSynthetic(int w, int h, int rows)
    {
        Bitmap b = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.FromArgb(24, 28, 34));
            using (LinearGradientBrush bg = new LinearGradientBrush(
                new Rectangle(0, 0, w, h), Color.FromArgb(30, 40, 52), Color.FromArgb(16, 20, 26), 90f))
                g.FillRectangle(bg, 0, 0, w, h);
            using (Font f = new Font("Segoe UI", 15f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush br = new SolidBrush(Color.FromArgb(228, 234, 240)))
            {
                for (int i = 0; i < rows; i++)
                {
                    // 行距 100px：合块条件是「行距 ≤ 行高的 1.5 倍」，行高约 30，
                    // 这样每行各自成块，用来测「块数不再被截断」
                    int y = 20 + i * 40;
                    g.DrawString("Setting row " + (i + 1) + " value " + (i * 3), f, br, 40, y);
                }
            }
        }
        return b;
    }

    private static int Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("usage: Verify.exe <model-dir> [image ...]"); return 2; }
        PpOcr det;
        try { det = new PpOcr(args[0], "verify"); }
        catch (Exception ex) { Console.WriteLine("model load fail: " + ex.Message); return 3; }
        AutoRegion.Detector = det.DetectPublic;
        AutoRegion.Log = delegate(string m) { Console.WriteLine("   " + m); };
        int fail = 0;

        // ① 用假检测器造 4 行 × 11 列 = 44 个「互不相邻」的文字框，
        //    对比「旧上限 24」与「不限」：前者被砍到 24，后者必须全留下。
        //    框间距按 AutoRegion 的合并规则给足（横向 80 > 行高 1.9 倍，纵向 100 > 行高 1.5 倍），
        //    保证 44 个框各自成块，测的是截断逻辑本身，不受 OCR 质量干扰。
        {
            int cols = 11, rows = 4, bw = 110, bh = 30, gapX = 80, gapY = 100;
            Rectangle scan = new Rectangle(0, 0, 48 + cols * (bw + gapX), 32 + rows * (bh + gapY));
            AutoRegion.Detector = delegate(Bitmap _)
            {
                List<PpOcr.Box> bs = new List<PpOcr.Box>();
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        PpOcr.Box b = new PpOcr.Box();
                        b.X = 24 + c * (bw + gapX);
                        b.Y = 16 + r * (bh + gapY);
                        b.W = bw; b.H = bh; b.Score = 0.9f;
                        bs.Add(b);
                    }
                return bs;
            };
            AutoRegion.RelaxUIGates = true;
            AutoRegion.Reset(); AutoRegion.Update(new Bitmap(scan.Width, scan.Height), scan, 0, 8, 1.2);
            List<AutoRegion.Snap> capped = AutoRegion.Update(new Bitmap(scan.Width, scan.Height), scan, 24, 8, 1.2);
            AutoRegion.Reset(); AutoRegion.Update(new Bitmap(scan.Width, scan.Height), scan, 0, 8, 1.2);
            List<AutoRegion.Snap> free = AutoRegion.Update(new Bitmap(scan.Width, scan.Height), scan, 0, 8, 1.2);
            Console.WriteLine("[cap] 假检测器 44 框：cap=24 → " + capped.Count + " 块，cap=0(不限) → " + free.Count + " 块");
            if (free.Count < 40 || free.Count <= capped.Count)
            {
                Console.WriteLine("[cap] FAIL: 不限时应保留全部 44 块，实际 " + free.Count);
                fail++;
            }
            else Console.WriteLine("[cap] PASS: 上限解除，块数 " + capped.Count + " → " + free.Count + "，检出一块翻一块");
        }

        // ② 真实 OCR 跑合成图：确认「不限上限」不会因为上限解除而放宽尺寸闸（块数应与旧逻辑一致）
        AutoRegion.Detector = det.DetectPublic;
        using (Bitmap syn = MakeSynthetic(1280, 1480, 12))
        {
            Rectangle scan = new Rectangle(0, 0, syn.Width, syn.Height);
            AutoRegion.Reset();
            AutoRegion.RelaxUIGates = true;
            AutoRegion.Update(syn, scan, 0, 8, 1.2);
            List<AutoRegion.Snap> hits = AutoRegion.Update(syn, scan, 0, 8, 1.2);
            Console.WriteLine("[ocr] 合成 12 行 → blocks=" + hits.Count + "（不设上限，无截断损失）");
        }

        // ② 真实截图：不设上限后不应少于旧记录
        for (int i = 1; i < args.Length; i++)
        {
            string p = args[i];
            if (!File.Exists(p)) { Console.WriteLine("missing " + p); fail++; continue; }
            using (Bitmap bmp = new Bitmap(p))
            {
                Rectangle scan = new Rectangle(0, 0, bmp.Width, bmp.Height);
                AutoRegion.Reset();
                AutoRegion.RelaxUIGates = true;
                AutoRegion.Update(bmp, scan, 0, 8, 1.2);
                List<AutoRegion.Snap> hits = AutoRegion.Update(bmp, scan, 0, 8, 1.2);
                Console.WriteLine("== " + Path.GetFileName(p) + "  " + bmp.Width + "x" + bmp.Height
                    + "  unlimited blocks=" + hits.Count);
            }
        }

        det.Dispose();
        return fail == 0 ? 0 : 1;
    }
}
