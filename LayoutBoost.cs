using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QRSnap
{
    // 版面感知的识字分段。
    //
    // 现在的问题是：PP-OCR 的检测框经常把「左栏图标 + 徽章 + 标题 + 右栏 Replies:」串成一行，
    // 应用层再按行去擦、去回填，必然擦错位置、译文也贴到别的栏上。
    // 这里在 OCR 之后、翻译之前补一层「版面清洗」：
    //   1. 先从整图找出贯穿上下的竖向空白走廊（表格/列表的分栏线），它们是硬边界；
    //   2. 检测框按走廊切开，每块单独重新识字（分辨率更高、也不跨栏）；
    //   3. 丢掉图标/头像被误识成字的噪声（拉丁文界面里冒出汉字、单个字母等）；
    //   4. 同栏、同一行、间距很小的框才合并；徽章之间的空隙比词距大，不会被粘成一句；
    //   5. 日期/固定标签用本地词表直接给中文，不交给模型自由发挥。
    //
    // 输出仍然是 OcrSegment（文字 + 原图坐标），后面的翻译和遮罩都不用改接口。
    internal static class LayoutBoost
    {
        // ---------------- 对外入口 ----------------

        public static List<OcrSegment> Recognize(Bitmap bmp, PpOcr pp, out string log)
        {
            List<Rectangle> ignored;
            return Recognize(bmp, pp, out log, out ignored);
        }

        public static List<OcrSegment> Recognize(Bitmap bmp, PpOcr pp, out string log, out List<Rectangle> obstacles)
        {
            log = string.Empty;
            List<OcrSegment> result = new List<OcrSegment>();
            obstacles = new List<Rectangle>();
            if (bmp == null || pp == null) return result;

            int w = bmp.Width, h = bmp.Height;
            List<int[]> gutters = FindGutters(bmp);

            // 1) 逐框识字（先不合并），噪声在这一步就能按「单个框」判断，不会污染整行
            List<PpOcr.Box> raw = pp.DetectPublic(bmp);
            List<OcrSegment> pieces = new List<OcrSegment>();
            List<Rectangle> jobs = new List<Rectangle>();
            List<float> jobAngles = new List<float>();
            for (int i = 0; i < raw.Count; i++)
            {
                PpOcr.Box b = raw[i];
                Rectangle box = new Rectangle(b.X, b.Y, b.W, b.H);
                List<Rectangle> parts = SplitAtGutters(bmp, box, gutters);
                List<Rectangle> parts2 = new List<Rectangle>();
                for (int k = 0; k < parts.Count; k++)
                    parts2.AddRange(SplitInnerBadges(bmp, parts[k]));
                for (int k = 0; k < parts2.Count; k++)
                {
                    Rectangle r = parts2[k];
                    if (r.Width < 5 || r.Height < 7) continue;
                    jobs.Add(r);
                    jobAngles.Add(b.Angle);
                }
            }

            // 逐块识字互相独立，丢到线程池并行跑：检测是一次性的，识别是几十上百次小推理，
            // 串行时占了大头。PpOcr 内部只对「裁图」串行，ONNX Run 本身是线程安全的。
            OcrSegment[] got = new OcrSegment[jobs.Count];
            ParallelOptions po = new ParallelOptions();
            po.MaxDegreeOfParallelism = Math.Max(2, Math.Min(6, Environment.ProcessorCount / 2));
            // GDI+ 的 Bitmap 不是线程安全的（连读 Width 都会报「对象当前正在其他地方使用」），
            // 每个工作线程拿一份只读副本，识别本身并行跑。
            ThreadLocal<Bitmap> tl = new ThreadLocal<Bitmap>(delegate { return CloneBitmap(bmp); }, true);
            try
            {
                Parallel.For(0, jobs.Count, po, delegate(int i)
                {
                    Rectangle r = jobs[i];
                    PpOcr.Box rb = new PpOcr.Box();
                    rb.X = r.X; rb.Y = r.Y; rb.W = r.Width; rb.H = r.Height; rb.Angle = jobAngles[i];
                    string text = pp.RecognizeBox(tl.Value, rb);
                    if (text == null) return;
                    text = Clean(text);
                    if (text.Length == 0) return;
                    OcrSegment s = new OcrSegment();
                    s.Text = text;
                    s.Box = r;
                    s.Angle = jobAngles[i];
                    got[i] = s;
                });
            }
            finally
            {
                foreach (Bitmap b in tl.Values) { try { b.Dispose(); } catch (Exception) { } }
                tl.Dispose();
            }
            for (int i = 0; i < got.Length; i++) if (got[i] != null) pieces.Add(got[i]);
            pieces = MergeHeaderPieces(pieces, bmp);

            bool latinDoc = IsLatinDoc(pieces);
            StringBuilder dbg = new StringBuilder();
            dbg.Append("gutters=").Append(GutterText(gutters)).Append("\r\n");
            dbg.Append("rawBoxes=").Append(raw.Count).Append(" pieces=").Append(pieces.Count).Append("\r\n");

            // 2) 去噪声
            List<OcrSegment> kept = new List<OcrSegment>();
            for (int i = 0; i < pieces.Count; i++)
            {
                OcrSegment s = pieces[i];
                if (latinDoc)
                {
                    int letters = 0;
                    for (int k = 0; k < s.Text.Length; k++)
                    {
                        char c = s.Text[k];
                        if (c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z') letters++;
                    }
                    // 长句首尾粘上的汉字/箭头多半是图标，剥掉；短词不剥，避免把“七58”剥成“58”
                    if (letters >= 8) s.Text = StripEdgeCjk(s.Text);
                }
                bool bigShort = s.Box.Width >= 30 && s.Box.Height >= 30
                    && s.Text.Replace(" ", string.Empty).Length <= 2;
                // 头像/表情只检查很短的识别结果；正常单词（Homebrew）即使底色鲜艳也不能丢
                bool graphic = s.Text.Replace(" ", string.Empty).Length <= 3 && IsGraphic(bmp, s.Box);
                if (IsNoise(s.Text, latinDoc) || bigShort || graphic)
                {
                    // 头像/表情/缩略图：不进译文，但要记下来，回填时躲开它们。
                    // 有些图标被识成一两个怪字（“七58”“e51”），按尺寸也算图形。
                    bool iconBox = s.Box.Width >= 20 && s.Box.Height >= 14 && s.Box.Width * s.Box.Height >= 450;
                    if (graphic || bigShort || iconBox) obstacles.Add(s.Box);
                    dbg.Append("drop ").Append(s.Box.X).Append(',').Append(s.Box.Y).Append(' ')
                       .Append(s.Box.Width).Append('x').Append(s.Box.Height).Append(" | ").Append(s.Text).Append("\r\n");
                    continue;
                }
                kept.Add(s);
            }

            // 3) 同栏、同一行、间距很小的才合并成一条
            kept.Sort(delegate(OcrSegment a, OcrSegment b)
            {
                if (Math.Abs(a.Box.Y + a.Box.Height / 2 - (b.Box.Y + b.Box.Height / 2)) > Math.Max(a.Box.Height, b.Box.Height) / 2)
                    return (a.Box.Y + a.Box.Height / 2).CompareTo(b.Box.Y + b.Box.Height / 2);
                return a.Box.X.CompareTo(b.Box.X);
            });
            List<OcrSegment> merged = new List<OcrSegment>();
            for (int i = 0; i < kept.Count; i++)
            {
                OcrSegment cur = kept[i];
                OcrSegment last = merged.Count > 0 ? merged[merged.Count - 1] : null;
                if (last != null && CanJoin(bmp, last.Box, cur.Box, gutters))
                {
                    last.Box = Rectangle.Union(last.Box, cur.Box);
                    last.Text = JoinText(last.Text, cur.Text);
                    continue;
                }
                OcrSegment one = new OcrSegment();
                one.Text = cur.Text;
                one.Box = cur.Box;
                one.Angle = cur.Angle;
                merged.Add(one);
            }

            // 4) 段落聚合：标题/正文常被 OCR 拆成多行，逐行翻译会得到「半句 + 半句」。
            //    这里只把同一栏、左对齐、行距很小、且明显是「上一行没说完」的行并成一段。
            merged = GroupParagraphs(merged, gutters, bmp.Width, bmp);

            // 5) 阅读顺序：按行（y 中心）再按 x
            merged.Sort(delegate(OcrSegment a, OcrSegment b)
            {
                int ac = a.Box.Y + a.Box.Height / 2, bc = b.Box.Y + b.Box.Height / 2;
                if (Math.Abs(ac - bc) > Math.Max(a.Box.Height, b.Box.Height) / 2) return ac.CompareTo(bc);
                return a.Box.X.CompareTo(b.Box.X);
            });
            for (int i = 0; i < merged.Count; i++)
            {
                merged[i].Order = i;
                result.Add(merged[i]);
                dbg.Append(i).Append(" @").Append(merged[i].Box.X).Append(',').Append(merged[i].Box.Y)
                   .Append(' ').Append(merged[i].Box.Width).Append('x').Append(merged[i].Box.Height)
                   .Append(" | ").Append(merged[i].Text).Append("\r\n");
            }
            dbg.Append("kept=").Append(result.Count).Append(" latinDoc=").Append(latinDoc ? "1" : "0").Append("\r\n");
            log = dbg.ToString();
            return result;
        }

        // ---------------- 分栏 ----------------

        // 整图逐列统计「墨迹占比」：整列几乎没有和底色不一样的像素，就是一条贯穿上下的空白走廊。
        // 表格的列间空白、列表左右栏之间的空隙都会落在这里。
        public static List<int[]> FindGutters(Bitmap bmp)
        {
            List<int[]> res = new List<int[]>();
            int w = bmp.Width, h = bmp.Height;
            if (w < 40 || h < 40) return res;

            int[] lum;
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                lum = new int[w * h];
                int stride = bd.Stride;
                byte[] row = new byte[Math.Abs(stride)];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(bd.Scan0 + y * stride, row, 0, row.Length);
                    for (int x = 0; x < w; x++)
                    {
                        int o = x * 4;
                        lum[y * w + x] = (row[o + 2] * 77 + row[o + 1] * 151 + row[o] * 28) >> 8;
                    }
                }
            }
            finally { bmp.UnlockBits(bd); }

            float[] ink = new float[w];
            for (int x = 0; x < w; x++)
            {
                // 该列亮度中位数 = 这一列的底色（渐变也能跟上）
                int[] hist = new int[256];
                int n = 0;
                for (int y = 0; y < h; y += 2) { hist[lum[y * w + x]]++; n++; }
                int half = n / 2, acc = 0, med = 128;
                for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= half) { med = i; break; } }

                int cnt = 0;
                for (int y = 0; y < h; y++)
                    if (Math.Abs(lum[y * w + x] - med) > 30) cnt++;
                ink[x] = cnt / (float)h;
            }

            // 「分带投票」：导航栏/页眉是横贯全宽的一整行字，会把真正的分栏走廊整列弄脏，
            // 只按整图统计就永远找不到 x=260 / x=1310 这种分栏线。把图横向切成若干带，
            // 只统计「这一带里这一列是否干净」，多数带都干净的列才是结构分栏。
            int bandH = Math.Max(80, h / 6);
            int bands = 0;
            int[] vote = new int[w];
            for (int by = 0; by < h; by += bandH)
            {
                int y0 = by, y1 = Math.Min(h, by + bandH);
                if (y1 - y0 < 50) continue;
                bands++;
                for (int x = 0; x < w; x++)
                {
                    int[] hist = new int[256];
                    int n = 0;
                    for (int y = y0; y < y1; y += 2) { hist[lum[y * w + x]]++; n++; }
                    if (n == 0) continue;
                    int half = n / 2, acc = 0, med = 128;
                    for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= half) { med = i; break; } }
                    int cnt = 0, total = 0;
                    for (int y = y0; y < y1; y++) { total++; if (Math.Abs(lum[y * w + x] - med) > 30) cnt++; }
                    if (total > 0 && cnt / (float)total <= 0.012f) vote[x]++;
                }
            }
            int need = Math.Max(3, (int)Math.Round(bands * 0.55));

            int start = -1;
            for (int x = 0; x < w; x++)
            {
                bool g = x > 6 && x < w - 7
                    && (ink[x] <= 0.008f || (bands >= 4 && vote[x] >= need));
                if (g)
                {
                    if (start < 0) start = x;
                }
                else if (start >= 0)
                {
                    AddGutter(res, start, x - 1, w);
                    start = -1;
                }
            }
            if (start >= 0) AddGutter(res, start, w - 1, w);

            // 两条走廊中间只隔着 1~6px 的分隔线时并成一条
            List<int[]> merged = new List<int[]>();
            for (int i = 0; i < res.Count; i++)
            {
                if (merged.Count > 0 && res[i][0] - merged[merged.Count - 1][1] <= 7)
                {
                    merged[merged.Count - 1][1] = res[i][1];
                    continue;
                }
                merged.Add(new int[] { res[i][0], res[i][1] });
            }
            // 太窄的走廊不算：标题里两个词之间的空隙（如 “Similar threads”）也会很干净
            List<int[]> ok = new List<int[]>();
            for (int i = 0; i < merged.Count; i++)
                if (merged[i][1] - merged[i][0] + 1 >= 12) ok.Add(merged[i]);
            return ok;
        }

        private static void AddGutter(List<int[]> list, int s, int e, int w)
        {
            if (e - s + 1 < 6) return;
            list.Add(new int[] { s, e });
        }

        private static List<Rectangle> SplitAtGutters(Bitmap bmp, Rectangle box, List<int[]> gutters)
        {
            List<Rectangle> parts = new List<Rectangle>();
            parts.Add(box);
            // 顶部 6% 是 logo/站名/浏览器条，跨栏也不切：切开只会得到 “G / EMP” 这种碎片
            if (box.Top < bmp.Height * 0.06f) return parts;
            // 单行文字（标题 / meta / logo）即使横跨走廊也是一句完整的话，不能切；
            // 只有「多行 + 跨栏」才是真的把左右两栏粘在了一起。
            if (TextRowCount(bmp, box) < 2) return parts;
            for (int gi = 0; gi < gutters.Count; gi++)
            {
                int gs = gutters[gi][0], ge = gutters[gi][1];
                List<Rectangle> next = new List<Rectangle>();
                for (int i = 0; i < parts.Count; i++)
                {
                    Rectangle r = parts[i];
                    if (r.Right <= gs || r.Left > ge) { next.Add(r); continue; }
                    // 框真的跨过了这条走廊：走廊左侧一块、右侧一块，走廊本身不擦
                    if (r.Left < gs)
                    {
                        int rw = gs - r.Left;
                        // 左边只剩一小截（logo 的一段、标题前的图标）时不要切：切了也识别不出
                        if (rw < 80) { next.Add(r); continue; }
                        next.Add(new Rectangle(r.Left, r.Top, rw, r.Height));
                    }
                    if (r.Right > ge + 1)
                    {
                        int lx = ge + 1;
                        int rw = r.Right - lx;
                        if (rw >= 5) next.Add(new Rectangle(lx, r.Top, rw, r.Height));
                    }
                }
                parts = next;
            }
            return parts;
        }

        // 一个检测框里同时装着「徽章 pill + 标题」时，按内部底色变化切开：
        // 徽章底色（绿/黄/蓝）和它跟标题之间的面板底色差别很大，普通词间距两侧都是面板底色，不会误切。
        private static List<Rectangle> SplitInnerBadges(Bitmap bmp, Rectangle box)
        {
            List<Rectangle> one = new List<Rectangle>();
            one.Add(box);
            if (box.Width < 60 || box.Height < 10) return one;

            int x0 = Math.Max(0, box.Left), x1 = Math.Min(bmp.Width, box.Right);
            int y0 = Math.Max(0, box.Top), y1 = Math.Min(bmp.Height, box.Bottom);
            if (x1 - x0 < 40) return one;

            Color pageBg = ModeColor(bmp, box);
            bool[] ink = new bool[x1 - x0];
            for (int x = x0; x < x1; x++)
            {
                int hit = 0, total = 0;
                for (int y = y0; y < y1; y += 2)
                {
                    Color c = bmp.GetPixel(x, y);
                    int l = (c.R * 77 + c.G * 151 + c.B * 28) >> 8;
                    int bl = (pageBg.R * 77 + pageBg.G * 151 + pageBg.B * 28) >> 8;
                    total++;
                    if (Math.Abs(l - bl) > 30) hit++;
                }
                ink[x - x0] = total > 0 && hit * 100 >= total * 8;
            }

            List<int> cuts = new List<int>();
            int run = 0;
            for (int i = 1; i < ink.Length; i++)
            {
                if (!ink[i]) run++;
                else
                {
                    if (run >= 4)
                    {
                        int gs = i - run, ge = i - 1;
                        Rectangle gap = new Rectangle(x0 + gs, y0, run, y1 - y0);
                        int lw = Math.Min(12, gs);
                        int rw = Math.Min(12, ink.Length - 1 - ge);
                        Color cg = ModeColor(bmp, gap);
                        Color cl = lw >= 4 ? ModeColor(bmp, new Rectangle(x0 + gs - lw, y0, lw, y1 - y0)) : cg;
                        Color cr = rw >= 4 ? ModeColor(bmp, new Rectangle(x0 + ge + 1, y0, rw, y1 - y0)) : cg;
                        // 一侧底色和空隙底色不同 => 这里是「彩色 pill 的边缘」，不是词间距
                        if (Dist(cl, cg) > 26 || Dist(cr, cg) > 26) cuts.Add(x0 + gs);
                    }
                    run = 0;
                }
            }
            if (cuts.Count == 0) return one;

            List<Rectangle> res = new List<Rectangle>();
            int left = box.Left;
            for (int i = 0; i < cuts.Count && i < 4; i++)
            {
                int right = cuts[i] - 2;
                if (right - left >= 8) res.Add(new Rectangle(left, box.Top, right - left, box.Height));
                left = cuts[i] + 1;
            }
            if (box.Right - left >= 8) res.Add(new Rectangle(left, box.Top, box.Right - left, box.Height));
            return res.Count >= 2 ? res : one;
        }

        // 数一个框里有几行字：按行统计墨迹，连续 >=2 行空白才算换行。
        private static int TextRowCount(Bitmap bmp, Rectangle box)
        {
            int x0 = Math.Max(0, box.Left), x1 = Math.Min(bmp.Width, box.Right);
            int y0 = Math.Max(0, box.Top), y1 = Math.Min(bmp.Height, box.Bottom);
            if (x1 - x0 < 8 || y1 - y0 < 8) return 1;
            Color bg = ModeColor(bmp, box);
            int bl = (bg.R * 77 + bg.G * 151 + bg.B * 28) >> 8;
            bool[] ink = new bool[y1 - y0];
            for (int y = y0; y < y1; y++)
            {
                int hit = 0, total = 0;
                for (int x = x0; x < x1; x += 2)
                {
                    Color c = bmp.GetPixel(x, y);
                    int l = (c.R * 77 + c.G * 151 + c.B * 28) >> 8;
                    total++;
                    if (Math.Abs(l - bl) > 30) hit++;
                }
                ink[y - y0] = total > 0 && hit * 100 >= total * 2;
            }
            int rows = 0, blank = 0;
            bool inBand = false;
            for (int i = 0; i < ink.Length; i++)
            {
                if (ink[i])
                {
                    if (!inBand) { rows++; inBand = true; }
                    blank = 0;
                }
                else if (inBand)
                {
                    blank++;
                    if (blank >= 2) inBand = false;
                }
            }
            return Math.Max(1, rows);
        }



        private static bool CanJoin(Bitmap bmp, Rectangle a, Rectangle b, List<int[]> gutters)
        {
            int ha = a.Height, hb = b.Height;
            int hAvg = (ha + hb) / 2;
            int ac = a.Top + ha / 2, bc = b.Top + hb / 2;
            if (Math.Abs(ac - bc) > Math.Max(4, hAvg / 3)) return false;          // 不是同一行
            if (Math.Abs(ha - hb) > Math.Max(6, hAvg / 2)) return false;          // 字号差太多（标题 vs 正文）

            int gap = b.Left - a.Right;
            if (gap < -Math.Max(4, hAvg / 3)) return false;                       // 重叠太多，可能跨栏
            // 词距：英文单词间约 0.25~0.35 字高；徽章之间通常 >= 0.5 字高，不能粘成一句
            if (gap > Math.Max(5, (int)(hAvg * 0.55f))) return false;

            // 跨栏一律不合并
            for (int i = 0; i < gutters.Count; i++)
                if (a.Right <= gutters[i][0] && b.Left > gutters[i][1]) return false;

            // 底色不一样的两块不是同一句话：徽章 pill（绿/黄/蓝底）和正文（面板底）之间
            // 的空隙虽然只有 8~10px，但底色一对比就能看出来，不能把两个徽章粘成「Homebrew Homebrew app」。
            int gapW = Math.Max(1, gap);
            Rectangle gapRect = new Rectangle(a.Right, a.Top, gapW, Math.Min(a.Bottom, b.Bottom) - a.Top);
            Color ca = ModeColor(bmp, a);
            Color cb = ModeColor(bmp, b);
            Color cg = ModeColor(bmp, gapRect);
            if (Dist(ca, cg) > 26 || Dist(cb, cg) > 26) return false;

            return true;
        }

        // 段落聚合：同一栏 + 左对齐 + 行距很小 + 至少有一行是长句，才并成一段。
        // 先按栏分组再在栏内按 y 排，不能直接看「列表里的下一行」——同一行右栏的
        // Replies 会插在标题两行之间，按列表顺序判断就会把标题拆散。
        private static List<OcrSegment> GroupParagraphs(List<OcrSegment> src, List<int[]> gutters, int imgW, Bitmap bmp)
        {
            bool[] used = new bool[src.Count];
            List<OcrSegment> outp = new List<OcrSegment>();
            for (int i = 0; i < src.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                OcrSegment cur = Copy(src[i]);
                List<Rectangle> sources = new List<Rectangle>();
                sources.Add(src[i].Box);
                int firstLeft = src[i].Box.Left;
                int top = src[i].Box.Top, bottom = src[i].Box.Bottom;

                if (!IsStandalone(cur.Text))
                {
                    while (true)
                    {
                        int best = -1, bestGap = int.MaxValue, bestDx = int.MaxValue;
                        for (int j = 0; j < src.Count; j++)
                        {
                            if (used[j]) continue;
                            OcrSegment nx = src[j];
                            if (IsStandalone(nx.Text)) continue;
                            if (ColumnOf(nx.Box, gutters) != ColumnOf(cur.Box, gutters)) continue;
                            // 列表项：行首左侧有一个彩色小圆点/图标时，这里是新的一条，不能并进上一段
                            if (HasListMarker(bmp, nx.Box)) continue;
                            Rectangle last = sources[sources.Count - 1];
                            if (!LooksContinued(last, cur.Text, nx)) continue;
                            int gap = nx.Box.Top - cur.Box.Bottom;
                            int dx = Math.Abs(nx.Box.Left - cur.Box.Left);
                            if (gap < bestGap || (gap == bestGap && dx < bestDx))
                            { best = j; bestGap = gap; bestDx = dx; }
                        }
                        if (best < 0) break;
                        used[best] = true;
                        sources.Add(src[best].Box);
                        cur.Text = JoinProse(cur.Text, src[best].Text);
                        if (src[best].Box.Top < top) top = src[best].Box.Top;
                        if (src[best].Box.Bottom > bottom) bottom = src[best].Box.Bottom;
                        cur.Box = Rectangle.Union(cur.Box, src[best].Box);
                    }
                }

                if (sources.Count > 1)
                {
                    // 排版区域从「第一行的左边界」开始，到本栏右边界结束：
                    // 标题首行被徽章顶到右边时，译文也从徽章右侧起排，不会盖住徽章。
                    int right = ColumnRight(firstLeft, gutters, imgW);
                    int width = Math.Max(40, right - firstLeft);
                    cur.Box = new Rectangle(firstLeft, top, width, bottom - top);
                    cur.SourceBoxes = sources;
                }
                outp.Add(cur);
            }
            return outp;
        }

        private static int ColumnRight(int left, List<int[]> gutters, int imgW)
        {
            for (int i = 0; i < gutters.Count; i++)
                if (gutters[i][0] > left) return Math.Max(left + 40, gutters[i][0] - 3);
            return imgW;
        }

        private static int ColumnOf(Rectangle r, List<int[]> gutters)
        {
            int c = 0;
            for (int i = 0; i < gutters.Count; i++)
                if (gutters[i][1] < r.Left) c = i + 1;
            return c;
        }

        private static OcrSegment Copy(OcrSegment s)
        {
            OcrSegment o = new OcrSegment();
            o.Text = s.Text;
            o.Box = s.Box;
            o.Angle = s.Angle;
            o.Order = s.Order;
            o.SourceBoxes = s.SourceBoxes;
            return o;
        }

        private static bool SameColumn(Rectangle a, Rectangle b, List<int[]> gutters)
        {
            for (int i = 0; i < gutters.Count; i++)
            {
                bool aL = a.Right <= gutters[i][0], bL = b.Right <= gutters[i][0];
                bool aR = a.Left > gutters[i][1], bR = b.Left > gutters[i][1];
                if ((aL && bR) || (aR && bL)) return false;
            }
            return true;
        }

        private static readonly Regex ReMeta = new Regex(
            @"(·|\bat\s+\d{1,2}:\d{2}|\b(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\b|\b(19|20)\d{2}\b|Replies:|Views:)",
            RegexOptions.IgnoreCase);

        private static bool IsStandalone(string t)
        {
            if (string.IsNullOrEmpty(t)) return true;
            string s = t.Trim();
            if (s.Length <= 3) return true;
            if (ReMeta.IsMatch(s)) return true;
            if (PreTranslate(s) != null) return true;
            bool allDigits = true;
            for (int i = 0; i < s.Length; i++)
                if (!char.IsDigit(s[i]) && s[i] != ' ' && s[i] != ',' && s[i] != 'K') { allDigits = false; break; }
            return allDigits;
        }

        // 同一栏内的上下相邻：行距很小、左对齐、行高接近，且至少有一行是长句。
        // 两行都短（用户名、标签、按钮）不合并，避免把「masagrator / The patches guy / Developer」
        // 拼成一句话；长句后面跟一个短尾巴（Definitive Edition、clocks)）则合并。
        private static bool LooksContinued(Rectangle last, string lastText, OcrSegment b)
        {
            int hAvg = (last.Height + b.Box.Height) / 2;
            int vGap = b.Box.Top - last.Bottom;
            if (vGap < -Math.Max(4, hAvg / 3) || vGap > Math.Max(3, (int)(hAvg * 0.40f))) return false;
            int ow = Math.Min(last.Right, b.Box.Right) - Math.Max(last.Left, b.Box.Left);
            // 下一行比上一行更靠左（标题首行被徽章顶到右边）时，两行本来就只有一小段重叠，
            // 这里只要求「接得上」，不要求重叠过半
            int needOw = b.Box.Left < last.Left - 4 ? 18 : (int)(Math.Min(last.Width, b.Box.Width) * 0.5f);
            if (ow < needOw) return false;
            if (Math.Abs(last.Height - b.Box.Height) > Math.Max(6, hAvg / 3)) return false;
            // 允许下一行比上一行更靠左（标题首行被徽章顶到右边，第二行回到栏左）
            if (b.Box.Left > last.Left + Math.Max(10, hAvg / 2)) return false;

            string s = (lastText ?? string.Empty).TrimEnd();
            string t = b.Text.TrimStart();
            if (s.Length == 0 || t.Length == 0) return false;
            char lastCh = s[s.Length - 1];
            bool cue = lastCh == '-' || lastCh == '–' || lastCh == '—' || lastCh == ',' || lastCh == '&'
                || lastCh == '(' || lastCh == '/' || lastCh == ':' || lastCh == ';';
            // 上一行本身得是一句没说完的长句；短标签/徽章不往下吞并正文
            if (s.Length < 30 && !cue) return false;
            if (cue) return true;
            if (char.IsLower(lastCh) || char.IsLower(t[0])) return true;
            if (Count(s, '(') > Count(s, ')')) return true;
            return true;   // 有长句兜底，剩下的大多是正常折行
        }

        private static int Count(string s, char c)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++) if (s[i] == c) n++;
            return n;
        }

        private static string JoinProse(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            string x = a.TrimEnd(), y = b.TrimStart();
            char last = x[x.Length - 1];
            if (last == '-')
            {
                // “Tap -” 是分隔号，要留空格；“Gyro-” 是词内连字符，直接接上
                if (x.Length >= 2 && char.IsWhiteSpace(x[x.Length - 2])) return x + " " + y;
                return x + y;
            }
            if (last == '(' || last == '/' || last == ':') return x + y;
            return x + " " + y;
        }

        private static string JoinText(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            char ca = a[a.Length - 1], cb = b[0];
            if (ca == '-' || cb == '-' || ca == '·' || cb == '·') return a + " " + b;
            bool aAscii = ca < 128, bAscii = cb < 128;
            if (aAscii && bAscii) return a + " " + b;
            return a + b;
        }

        // ---------------- 噪声判断 ----------------

        private static bool IsLatinDoc(List<OcrSegment> pieces)
        {
            int latin = 0, cjk = 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                string t = pieces[i].Text;
                for (int k = 0; k < t.Length; k++)
                {
                    char c = t[k];
                    if (c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z') latin++;
                    else if (c >= 0x4e00 && c <= 0x9fff) cjk++;
                }
            }
            return latin > cjk * 4 && latin > 20;
        }

        private static readonly string[] KeepShort = new string[]
        {
            "OP", "PC", "UI", "HD", "USB", "FPS", "AI", "DLC", "TV", "VR", "OK", "HDMI", "GPU", "CPU"
        };

        private static bool IsNoise(string t, bool latinDoc)
        {
            if (string.IsNullOrEmpty(t)) return true;
            string s = t.Trim();
            if (s.Length == 0) return true;

            int letters = 0, cjk = 0, digits = 0, other = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') digits++;
                else if (c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z') letters++;
                else if (c >= 0x4e00 && c <= 0x9fff) cjk++;
                else if (!char.IsWhiteSpace(c)) other++;
            }

            // 英文界面里冒出汉字：短的多半是图标/表情被误识（“K 工”“七5百”）；
            // 长句里夹一两个汉字（“二masagrator·Oct…”）是真文字被图标污染，保留，交给翻译/清洗
            if (latinDoc && cjk > 0)
            {
                if (letters + digits < 8) return true;
                if (cjk * 2 > letters) return true;
            }
            if (letters + digits == 0) return true;

            string compact = s.Replace(" ", string.Empty);
            if (compact.Length <= 2 && digits == 0)
            {
                string up = compact.ToUpperInvariant();
                bool keep = false;
                for (int i = 0; i < KeepShort.Length; i++) if (KeepShort[i] == up) { keep = true; break; }
                if (!keep) return true;
            }
            // “L5”“t5”“e51”这类单个字母+数字，是点赞图标被拆出来的
            if (compact.Length <= 3 && letters == 1 && digits >= 1 && !char.IsDigit(compact[0]))
            {
                string up = compact.ToUpperInvariant();
                bool keep = false;
                for (int i = 0; i < KeepShort.Length; i++) if (KeepShort[i] == up) { keep = true; break; }
                if (!keep) return true;
            }
            if (other > letters + digits + cjk) return true;   // 符号比字多
            return false;
        }

        private static string Clean(string s)
        {
            if (s == null) return string.Empty;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\r' || c == '\n' || c == '\t') { sb.Append(' '); continue; }
                if (c < 32) continue;
                sb.Append(c);
            }
            string t = sb.ToString();
            while (t.IndexOf("  ") >= 0) t = t.Replace("  ", " ");
            t = t.Trim();
            // OCR 偶尔把下一个徽章的开括号粘到本块末尾：“Homebrew [”
            if (t.EndsWith("[") && Count(t, '[') > Count(t, ']')) t = t.TrimEnd('[', ' ').TrimEnd();
            return t;
        }

        // 列表项目符号：行框左侧紧挨着一颗小圆点（论坛/文档的 •、▶、小图标）。
        // 圆点可能是白色/灰色，所以按「和底色差得多的孤立小墨块」找，不按颜色。
        // 只认小且孤立的块，头像/缩略图那种大片墨迹不算，避免把两行正文误拆。
        private static bool HasListMarker(Bitmap bmp, Rectangle box)
        {
            int x0 = Math.Max(0, box.Left - 34), x1 = box.Left - 3;
            int y0 = Math.Max(0, box.Top), y1 = Math.Min(bmp.Height, box.Bottom);
            if (x1 - x0 < 5 || y1 - y0 < 6) return false;
            Color bg = ModeColor(bmp, box);
            int bl = (bg.R * 77 + bg.G * 151 + bg.B * 28) >> 8;
            int minx = int.MaxValue, miny = int.MaxValue, maxx = -1, maxy = -1, cnt = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    int l = (c.R * 77 + c.G * 151 + c.B * 28) >> 8;
                    if (Math.Abs(l - bl) < 42) continue;
                    cnt++;
                    if (x < minx) minx = x;
                    if (x > maxx) maxx = x;
                    if (y < miny) miny = y;
                    if (y > maxy) maxy = y;
                }
            if (cnt < 6 || maxx < 0) return false;
            int w = maxx - minx + 1, h = maxy - miny + 1;
            if (w > 18 || h > 18) return false;          // 大片墨迹：头像/缩略图
            if (w * h > 260) return false;
            if (minx <= x0 + 1) return false;            // 贴着截图边缘，不可靠
            if (box.Left - maxx < 1) return false;       // 必须和正文之间留出空隙
            return true;
        }


        private static bool IsGraphic(Bitmap bmp, Rectangle r)
        {
            if (r.Width < 8 || r.Height < 8) return false;
            int x1 = Math.Min(bmp.Width, r.Right), y1 = Math.Min(bmp.Height, r.Bottom);
            int total = 0, sat = 0;
            int[] hue = new int[12];
            for (int y = Math.Max(0, r.Top); y < y1; y += 2)
                for (int x = Math.Max(0, r.Left); x < x1; x += 2)
                {
                    Color c = bmp.GetPixel(x, y);
                    total++;
                    int mx = Math.Max(c.R, Math.Max(c.G, c.B));
                    int mn = Math.Min(c.R, Math.Min(c.G, c.B));
                    if (mx - mn <= 55 || mx <= 70) continue;
                    sat++;
                    hue[HueBin(c, mx, mn)]++;
                }
            if (total < 20) return false;
            if (sat * 100 < total * 18) return false;
            int used = 0, lastBin = -1, firstBin = -1;
            List<int> bins = new List<int>();
            for (int i = 0; i < 12; i++)
                if (hue[i] * 100 >= total * 3) { bins.Add(i); used++; if (firstBin < 0) firstBin = i; lastBin = i; }
            if (used < 3) return false;
            // 色相要真的「散开」：绿色徽章的底色和抗锯齿都是同一色系；
            // 点赞表情/头像才会同时出现黄、蓝、肤色等相隔很远的色相。
            int maxGap = 0;
            for (int i = 0; i < bins.Count; i++)
            {
                int nx = i + 1 < bins.Count ? bins[i + 1] : bins[0] + 12;
                if (nx - bins[i] > maxGap) maxGap = nx - bins[i];
            }
            return maxGap >= 4 && used >= 3;
        }

        private static int HueBin(Color c, int mx, int mn)
        {
            float delta = mx - mn;
            float h;
            if (delta <= 0.001f) h = 0f;
            else if (mx == c.R) h = 60f * (((c.G - c.B) / delta) % 6f);
            else if (mx == c.G) h = 60f * (((c.B - c.R) / delta) + 2f);
            else h = 60f * (((c.R - c.G) / delta) + 4f);
            if (h < 0f) h += 360f;
            int b = (int)(h / 30f);
            return b < 0 ? 0 : (b > 11 ? 11 : b);
        }

        // 框内出现频率最高的颜色（量化到 16 级），文字是少数派，所以得到的是底色
        private static Color ModeColor(Bitmap bmp, Rectangle r)
        {
            int x0 = Math.Max(0, r.Left), y0 = Math.Max(0, r.Top);
            int x1 = Math.Min(bmp.Width, r.Right), y1 = Math.Min(bmp.Height, r.Bottom);
            if (x1 <= x0 || y1 <= y0) return Color.Empty;
            Dictionary<int, int> hist = new Dictionary<int, int>();
            int step = (x1 - x0) * (y1 - y0) > 6000 ? 2 : 1;
            for (int y = y0; y < y1; y += step)
                for (int x = x0; x < x1; x += step)
                {
                    Color c = bmp.GetPixel(x, y);
                    int k = ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);
                    int n;
                    hist.TryGetValue(k, out n);
                    hist[k] = n + 1;
                }
            int bestK = 0, bestN = -1;
            foreach (KeyValuePair<int, int> kv in hist)
                if (kv.Value > bestN) { bestN = kv.Value; bestK = kv.Key; }
            int rr = ((bestK >> 8) & 0xF) * 16 + 8;
            int gg = ((bestK >> 4) & 0xF) * 16 + 8;
            int bb = (bestK & 0xF) * 16 + 8;
            return Color.FromArgb(rr, gg, bb);
        }

        private static int Dist(Color a, Color b)
        {
            return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
        }

        // 顶部 logo 常被检测拆成 G / B / AT / EMP 几块，单字母又被噪声过滤丢掉。
        // 顶部 6% 内同一行的碎片先拼回去，再进噪声过滤。
        private static List<OcrSegment> MergeHeaderPieces(List<OcrSegment> pieces, Bitmap bmp)
        {
            int headerY = (int)(bmp.Height * 0.06f);
            List<OcrSegment> head = new List<OcrSegment>();
            List<OcrSegment> rest = new List<OcrSegment>();
            for (int i = 0; i < pieces.Count; i++)
            {
                if (pieces[i].Box.Bottom <= headerY) head.Add(pieces[i]);
                else rest.Add(pieces[i]);
            }
            if (head.Count < 2) return pieces;
            head.Sort(delegate(OcrSegment a, OcrSegment b)
            {
                if (Math.Abs(a.Box.Y + a.Box.Height / 2 - (b.Box.Y + b.Box.Height / 2)) > Math.Max(a.Box.Height, b.Box.Height) / 2)
                    return (a.Box.Y + a.Box.Height / 2).CompareTo(b.Box.Y + b.Box.Height / 2);
                return a.Box.X.CompareTo(b.Box.X);
            });
            List<OcrSegment> merged = new List<OcrSegment>();
            for (int i = 0; i < head.Count; i++)
            {
                OcrSegment cur = head[i];
                OcrSegment last = merged.Count > 0 ? merged[merged.Count - 1] : null;
                if (last != null)
                {
                    int hAvg = (last.Box.Height + cur.Box.Height) / 2;
                    int ac = last.Box.Y + last.Box.Height / 2, bc = cur.Box.Y + cur.Box.Height / 2;
                    int gap = cur.Box.Left - last.Box.Right;
                    if (Math.Abs(ac - bc) <= Math.Max(4, hAvg / 2)
                        && gap <= Math.Max(4, (int)(hAvg * 0.8f))
                        && Math.Abs(last.Box.Height - cur.Box.Height) <= Math.Max(6, hAvg / 2))
                    {
                        last.Box = Rectangle.Union(last.Box, cur.Box);
                        last.Text = last.Text + cur.Text;   // logo 字母之间不补空格
                        continue;
                    }
                }
                OcrSegment one = new OcrSegment();
                one.Text = cur.Text; one.Box = cur.Box; one.Angle = cur.Angle;
                merged.Add(one);
            }
            merged.AddRange(rest);
            return merged;
        }

        private static readonly object CloneLock = new object();
        private static string StripEdgeCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int i = 0, j = s.Length - 1;
            while (i <= j && IsEdgeNoise(s[i])) i++;
            while (j >= i && IsEdgeNoise(s[j])) j--;
            if (i == 0 && j == s.Length - 1) return s;
            return s.Substring(i, j - i + 1).Trim();
        }

        private static bool IsEdgeNoise(char c)
        {
            if (c >= 0x4e00 && c <= 0x9fff) return true;
            if (c >= 0x3000 && c <= 0x303f) return true;
            if (c == '■' || c == '□' || c == '·' || c == '•' || c == '→' || c == '←') return true;
            return false;
        }

        private static Bitmap CloneBitmap(Bitmap src)
        {
            // GDI+ 同一张源图不能从多个线程同时 Clone，这里串行建副本（只做几次，很快）
            lock (CloneLock)
            {
                Bitmap b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b))
                    g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
                return b;
            }
        }

        private static string GutterText(List<int[]> g)        {
            if (g.Count == 0) return "(none)";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < g.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(g[i][0]).Append('-').Append(g[i][1]);
            }
            return sb.ToString();
        }

        // ---------------- 翻译前的本地化 ----------------
        // 微信的做法是：固定标签走词表，日期走本地化，剩下的才交给翻译模型。
        // 这样既快，又不会出现“ Replies: 被翻成‘回复：’”这种和界面不一致的措辞。

        private static readonly Dictionary<string, string> Gloss = BuildGloss();

        private static Dictionary<string, string> BuildGloss()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            d["Similar threads"] = "相似主题";
            d["Replies:"] = "回复数:";
            d["Views:"] = "浏览量:";
            d["Homebrew"] = "自制程序";
            d["Homebrew app"] = "自制应用";
            d["ROM Hack"] = "ROM黑客";
            d["Front-page"] = "首页";
            d["Gaming"] = "游戏";
            d["OP"] = "楼主";
            d["Developer"] = "开发者";
            d["User Submitted News"] = "用户提交新闻";
            d["Yesterday"] = "昨天";
            // 论坛/网站固定导航：两种离线模型都容易把它们翻歪，直接走词表
            d["Home"] = "首页";
            d["News"] = "新闻";
            d["Forums"] = "论坛";
            d["Groups"] = "群组";
            d["Cheats"] = "金手指";
            d["Tutorials"] = "教程";
            d["Reviews"] = "评测";
            d["Downloads"] = "下载";
            d["Blogs"] = "博客";
            d["Log in"] = "登录";
            d["Register"] = "注册";
            d["New posts"] = "新帖";
            d["Search forums"] = "搜索论坛";
            d["Support us"] = "支持我们";
            d["New"] = "新";
            d["Hot"] = "热门";
            d["Discussed"] = "讨论";
            d["Site & Scene News"] = "站点与场景新闻";
            d["Features:"] = "功能：";
            d["Free games"] = "免费游戏";
            d["Free Games"] = "免费游戏";
            d["Emulation"] = "模拟";
            d["The patches guy"] = "补丁达人";
            d["The Patches Guy"] = "补丁达人";
            d["Level 24"] = "等级 24";
            return d;
        }

        // 命中固定词表 / 纯日期时直接返回中文；返回 null 表示这句要交给翻译模型
        public static string PreTranslate(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string t = text.Trim();

            string g;
            if (Gloss.TryGetValue(t, out g)) return g;
            // 导航图标被识成一两个字母（“E New”“O Hot”）：去掉首字母再查一次词表
            if (t.Length > 2 && t[1] == ' ' && char.IsLetter(t[0]))
            {
                string tail = t.Substring(2).Trim();
                if (Gloss.TryGetValue(tail, out g)) return g;
            }

            string d;
            if (TryLocalizeDate(t, out d)) return d;

            return null;
        }

        private static readonly Regex ReYesterday = new Regex(
            @"^Yesterday\s+at\s+(\d{1,2}):(\d{2})\s*(AM|PM)$", RegexOptions.IgnoreCase);
        private static readonly Regex ReWeekday = new Regex(
            @"^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)\s+at\s+(\d{1,2}):(\d{2})\s*(AM|PM)$",
            RegexOptions.IgnoreCase);
        private static readonly Regex ReMonth = new Regex(
            @"^(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+(\d{1,2}),\s*(\d{4})$",
            RegexOptions.IgnoreCase);

        private static string[] WeekZh = new string[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
        private static string[] MonthZh = new string[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12" };

        public static bool TryLocalizeDate(string t, out string zh)
        {
            zh = null;

            Match m = ReYesterday.Match(t);
            if (m.Success)
            {
                zh = "昨天" + DayPart(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)
                    + HourText(m.Groups[1].Value) + ":" + m.Groups[2].Value;
                return true;
            }

            m = ReWeekday.Match(t);
            if (m.Success)
            {
                int wd = WeekIndex(m.Groups[1].Value);
                if (wd >= 0)
                {
                    zh = WeekZh[wd] + DayPart(m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value)
                        + HourText(m.Groups[2].Value) + ":" + m.Groups[3].Value;
                    return true;
                }
            }

            m = ReMonth.Match(t);
            if (m.Success)
            {
                int mo = MonthIndex(m.Groups[1].Value);
                if (mo > 0)
                {
                    zh = m.Groups[3].Value + "年" + mo + "月" + m.Groups[2].Value + "日";
                    return true;
                }
            }
            return false;
        }

        private static int WeekIndex(string s)
        {
            string[] en = new string[] { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" };
            for (int i = 0; i < en.Length; i++) if (string.Equals(en[i], s, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static int MonthIndex(string s)
        {
            string[] en = new string[] { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
            for (int i = 0; i < en.Length; i++)
                if (s.Length >= 3 && string.Equals(s.Substring(0, 3), en[i], StringComparison.OrdinalIgnoreCase)) return i + 1;
            return 0;
        }

        private static string HourText(string hh)
        {
            int h;
            if (!int.TryParse(hh, out h)) return hh;
            return h.ToString(CultureInfo.InvariantCulture);
        }

        // “凌晨/上午/中午/下午/晚上”跟微信的口径一致：0–5 凌晨，6–11 上午，12 中午，13–17 下午，18–23 晚上
        private static string DayPart(string hh, string mm, string ampm)
        {
            int h;
            if (!int.TryParse(hh, out h)) return string.Empty;
            bool pm = string.Equals(ampm, "PM", StringComparison.OrdinalIgnoreCase);
            if (pm && h < 12) h += 12;
            if (!pm && h == 12) h = 0;
            if (h < 6) return "凌晨";
            if (h < 12) return "上午";
            if (h == 12) return "中午";
            if (h < 18) return "下午";
            return "晚上";
        }

        private static readonly Regex ReInlineMonth = new Regex(
            @"\b(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+(\d{1,2}),\s*(\d{4})\b",
            RegexOptions.IgnoreCase);
        private static readonly Regex ReInlineYesterday = new Regex(
            @"\bYesterday\s+at\s+(\d{1,2}):(\d{2})\s*(AM|PM)\b", RegexOptions.IgnoreCase);

        // 翻译模型偶尔仍会把游戏名/术语/日期留在英文，这里只做「已知名词」的兜底替换，不碰句子结构
        public static string PostFix(string zh)
        {
            if (string.IsNullOrEmpty(zh)) return zh;

            // 日期先本地化：模型没翻的 meta 行（masagrator · Oct 24, 2026）也能对齐微信的口径
            zh = ReInlineMonth.Replace(zh, delegate(Match m)
            {
                int mo = MonthIndex(m.Groups[1].Value);
                return m.Groups[3].Value + "年" + mo + "月" + m.Groups[2].Value + "日";
            });
            zh = ReInlineYesterday.Replace(zh, delegate(Match m)
            {
                return "昨天" + DayPart(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)
                    + HourText(m.Groups[1].Value) + ":" + m.Groups[2].Value;
            });

            string[][] map = new string[][]
            {
                new string[] { "Xenoblade Chronicles Definitive Edition", "《异度之刃：决定版》" },
                new string[] { "Xenoblade Chronicles 2", "《异度之刃 2》" },
                new string[] { "Xenoblade Chronicles", "《异度之刃》" },
                new string[] { "Zelda Wind Waker & Wind Waker HD", "《塞尔达传说：风之杖》与《风之杖 HD》" },
                new string[] { "Wind Waker HD", "《风之杖 HD》" },
                new string[] { "Wind Waker", "《风之杖》" },
                new string[] { "Wave Race 64", "《波浪赛车64》" },
                new string[] { "ROM Hack", "ROM黑客" },
                new string[] { "ROM hack", "ROM黑客" },
                new string[] { "终极版", "决定版" },
                new string[] { "定装版", "决定版" },
                new string[] { "Switch Frame Tap", "Switch Frame Tap" }
            };
            for (int i = 0; i < map.Length; i++)
                zh = zh.Replace(map[i][0], map[i][1]);

            // 中文之间不留空格；中文和《》/（）之间也不留
            zh = Regex.Replace(zh, @"(?<=[\u4e00-\u9fff])\s+(?=[\u4e00-\u9fff])", "");
            zh = Regex.Replace(zh, @"(?<=[\u4e00-\u9fff])\s+(?=[《》（）「」：；，。])", "");
            zh = Regex.Replace(zh, @"(?<=[《》（）「」：；，。])\s+(?=[\u4e00-\u9fff])", "");
            // 模型偶尔叠书名号 / 用近义游戏名
            zh = zh.Replace("《《", "《").Replace("》》", "》");
            zh = zh.Replace("异度神剑", "异度之刃");
            zh = zh.Replace("模仿", "模拟");
            return zh.Trim();
        }
    }
}
