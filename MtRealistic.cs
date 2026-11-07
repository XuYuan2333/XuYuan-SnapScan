using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace QRSnap
{
    // 拟真模式：照着 manga-image-translator 的 检测→擦除(inpainting)→回填 三段式，
    // 但检测/OCR/翻译沿用本软件现有链路，这里只吃「原文框 + 已翻译译文 + 原图」，
    // 先把框里的原文字擦掉（按周围像素补洞），再把译文按原框大小画回去，输出成品图。
    internal static class MtRealistic
    {
        public sealed class Block
        {
            public Rectangle Box;   // 原图坐标系里的原文框（段落块 = 排版区域）
            public string Text;     // 译文
            public float Angle;     // 原文倾角（度）
            public float SizeCap;   // 同段落统一字号上限，0 表示按框高自适应
            public int Group;       // AI 判断的段落分组（-1 = 没有 AI 建议，按几何聚类）
            public List<Rectangle> SourceBoxes;  // 段落由多行合并时，擦除只擦这些原行框
            public bool PreGrouped;              // 已在 OCR 层做过版面切分，不再走 MergeBlocks
            public List<Rectangle> Obstacles;    // 头像/表情等不参与翻译的图形，回填时躲开
        }

        public sealed class Stats
        {
            public int Lines;
            public int Hard;        // 背景太花、补洞可能露痕迹的行数
            public int Wrapped;     // 折过行的行数
            public int Skipped;     // 跨列串行、放弃替换的行数
        }

        internal static Bitmap Render(Bitmap src, List<Block> blocks, out Stats stats)
        {
            stats = new Stats();
            if (src == null) return null;
            Bitmap work = Clone(src);
            if (blocks == null || blocks.Count == 0) return work;

            int w = src.Width, h = src.Height;
            bool preGrouped = false;
            for (int i = 0; i < blocks.Count; i++) if (blocks[i] != null && blocks[i].PreGrouped) { preGrouped = true; break; }

            List<Block> work2;
            int skippedCross = 0;
            if (preGrouped)
            {
                // 版面切分已经在 OCR 层做完（分栏 / 徽章 / 段落），这里原样使用。
                // 再跑一遍 MergeBlocks 会把徽章和正文重新粘回去，前功尽弃。
                work2 = new List<Block>();
                for (int i = 0; i < blocks.Count; i++) if (blocks[i] != null) work2.Add(blocks[i]);
            }
            else
            {
                work2 = MergeBlocks(blocks, w, h);
                // 一整行里词距拉得很开（导航栏、分栏标签）时，按框内空白列拆成独立的小块，
                // 每块自己擦、自己排，观感比「一大块遮罩 + 一串挤在左边的译文」自然
                work2 = SplitByGaps(src, work2, out skippedCross);
            }
            stats.Skipped = skippedCross;
            for (int i = 0; i < work2.Count; i++)
            {
                Block b = work2[i];
                if (b == null || string.IsNullOrEmpty(b.Text)) continue;
                Rectangle r = b.Box;
                r.Intersect(new Rectangle(0, 0, w, h));
                if (r.Width < 6 || r.Height < 6) continue;

                if (!b.PreGrouped)
                {
                    // 单行框塞不下长译文时，看框下方有没有空白可以借一点高度；
                    // 但不能长到下一行身上去
                    int limitBottom = h;
                    for (int j = 0; j < work2.Count; j++)
                    {
                        if (j == i) continue;
                        Rectangle o = work2[j].Box;
                        if (o.Top <= r.Top) continue;
                        if (o.Left >= r.Right || o.Right <= r.Left) continue;
                        if (o.Top < limitBottom) limitBottom = o.Top;
                    }
                    r = TryGrowDown(src, r, limitBottom, w, h, b.Text);
                }

                // 颜色要在擦之前取，擦完就只剩背景了
                Color textColor;
                int bgLum;
                SampleColors(src, r, out textColor, out bgLum);

                bool hard = false;
                int inkPixels = 0;
                List<Rectangle> erase = (b.SourceBoxes != null && b.SourceBoxes.Count > 0) ? b.SourceBoxes : null;
                if (erase == null)
                {
                    Rectangle rr = r;
                    rr.Inflate(2, 2);
                    rr.Intersect(new Rectangle(0, 0, w, h));
                    inkPixels = EraseBlock(work, rr, out hard);
                }
                else
                {
                    // 段落块只擦每一行原来的字，不擦行间空白/徽章/头像
                    for (int k = 0; k < erase.Count; k++)
                    {
                        Rectangle rr = erase[k];
                        // 段落块的行框来自 OCR，边缘往往比实际字形紧 1~2px；
                        // 留一圈 4px 把抗锯齿和行首的残留符号一起带走
                        rr.Inflate(4, 3);
                        rr.Intersect(new Rectangle(0, 0, w, h));
                        if (rr.Width < 4 || rr.Height < 4) continue;
                        bool hh;
                        inkPixels += EraseBlock(work, rr, out hh);
                        hard = hard || hh;
                    }
                }
                if (hard) stats.Hard++;
                stats.Lines++;

                bool wrapped;
                bool eraseOnly = Environment.GetEnvironmentVariable("QRSNAP_DBG_ERASE_ONLY") == "1";
                if (!eraseOnly) DrawText(work, r, b.Text, b.Angle, textColor, bgLum, b.SizeCap, b.Obstacles, out wrapped);
                else wrapped = false;
                if (wrapped) stats.Wrapped++;

                NativeShell.Log("mt realistic box=" + b.Box.X + "," + b.Box.Y + "," + b.Box.Width + "," + b.Box.Height
                    + (r.Height > b.Box.Height + 4 ? " grown=" + (r.Height - b.Box.Height) : "")
                    + " ink=" + inkPixels + (hard ? " hardbg=1" : "") + (wrapped ? " wrap=1" : "") + " | " + b.Text);
            }
            return work;
        }

        // 相邻、左对齐、行距很小的行合并成段：整段一起擦、一起排版，
        // 长译文就能按段落折行，不会挤成一行 9px 小字。列表项、缩进不同的行不会合并。
        private static List<Block> MergeBlocks(List<Block> blocks, int w, int h)
        {
            List<Block> src = new List<Block>();
            for (int i = 0; i < blocks.Count; i++)
                if (blocks[i] != null && !string.IsNullOrEmpty(blocks[i].Text)) src.Add(blocks[i]);
            src.Sort(delegate(Block a, Block b) { return a.Box.Y != b.Box.Y ? a.Box.Y - b.Box.Y : a.Box.X - b.Box.X; });
            // OCR 有时会把一整行（甚至整条导航栏）当成一个框，文本却只有开头几个字。
            // 框远大于文字实际宽度时必须收窄，否则擦掉一大片、译文又按整框排版，观感就崩了。
            for (int i = 0; i < src.Count; i++) FitBoxToText(src[i], 0);
            src = MergeSameLine(src);

            // 不再把上下相邻的行拼成「段落」：译文一律按正常横排一行一行地画，
            // 只在框内放不下时才折行，避免把导航、标签、列表项拼成一大坨。
            List<Block> outList = new List<Block>();
            for (int i = 0; i < src.Count; i++) outList.Add(src[i]);

            // 行框有时上下重叠（OCR 框 + 微扩都会让框变高），重叠会导致两行译文叠在一起。
            // 只在「横向确实重叠」的框之间裁，避免把左右两栏的行也一起削掉。
            for (int i = 0; i < outList.Count; i++)
            {
                Block b = outList[i];
                int prevBottom = -1;
                for (int j = 0; j < i; j++)
                {
                    Block p = outList[j];
                    int ow = Math.Min(p.Box.Right, b.Box.Right) - Math.Max(p.Box.Left, b.Box.Left);
                    if (ow <= Math.Min(p.Box.Width, b.Box.Width) * 0.5f) continue;
                    if (p.Box.Bottom > prevBottom) prevBottom = p.Box.Bottom;
                }
                if (prevBottom > 0 && b.Box.Top < prevBottom && b.Box.Bottom - prevBottom < b.Box.Height)
                {
                    int newH = b.Box.Bottom - prevBottom;
                    if (newH >= 12) b.Box = new Rectangle(b.Box.X, prevBottom, b.Box.Width, newH);
                }
            }
            // 同一段落里若干行字号各不相同，看起来会很杂：这里按「行距很近 + 横向重叠」聚成段，
            // 段内取最矮那行的字号上限，让整段的字大小统一。
            UnifyParagraphSizes(outList);
            return outList;
        }

        // 行框高 → 字号。行框是 OCR 给的词框按行合并出来的，高度基本等于这一行的字高，
        // 所以 0.85 倍是稳妥的字面大小；再夹一个上下限，避免极端图把字画飞或画成蚂蚁。
        private static float CapFromHeight(float h)
        {
            float cap = h * 0.85f;
            if (cap < 7f) cap = 7f;       // 再小就不可读了
            if (cap > 48f) cap = 48f;     // 超大字保护，防止个别框异常把整块撑爆
            return cap;
        }

        // 把上下相邻、横向重叠、行高接近的行视为同一段落，统一字号上限
        private static void UnifyParagraphSizes(List<Block> list)
        {
            // 先按 AI 给的分组走：它认得「哪几行是一段」，比纯几何聚类更准；
            // 分组已经过行号连续 / 同栏 / 行距校验，不合规的根本不会被采纳。
            Dictionary<int, List<Block>> ai = new Dictionary<int, List<Block>>();
            for (int i = 0; i < list.Count; i++)
            {
                Block b = list[i];
                if (b.Group < 0) continue;
                List<Block> one;
                if (!ai.TryGetValue(b.Group, out one)) { one = new List<Block>(); ai[b.Group] = one; }
                one.Add(b);
            }
            int aiDone = 0;
            foreach (KeyValuePair<int, List<Block>> kv in ai)
            {
                List<Block> one = kv.Value;
                if (one.Count < 2) continue;
                int sumH = 0;
                for (int k = 0; k < one.Count; k++) sumH += one[k].Box.Height;
                // 字号跟着 OCR 行框高度走：行框高≈这一行的字高（含少量上下留白），淘 0.85 就是字面大小。
                // 原来固定卡 24，遇到大标题（行框 40~60）会被压成正文大小，这一版按行高给。
                float cap = CapFromHeight(sumH / (float)one.Count);
                for (int k = 0; k < one.Count; k++) one[k].SizeCap = cap;
                aiDone++;
            }
            // 没被 AI 分组的行继续走几何聚类；UnifyGeometric 内部会跳过已定字的块
            UnifyGeometric(list);
        }

        private static void UnifyGeometric(List<Block> list)
        {
            int i = 0;
            while (i < list.Count)
            {
                if (list[i].SizeCap > 0f) { i++; continue; }    // 已被 AI 分组定过字号
                int j = i + 1;
                int minH = list[i].Box.Height, maxH = minH;
                int sumH = minH, cntH = 1;
                int lastBottom = list[i].Box.Bottom;
                Rectangle span = list[i].Box;
                while (j < list.Count)
                {
                    Block b = list[j];
                    if (b.SizeCap > 0f) break;
                    int hAvg = (span.Height + b.Box.Height) / 2;
                    int vGap = b.Box.Top - lastBottom;
                    if (vGap < -Math.Max(4, hAvg / 2) || vGap > Math.Max(6, hAvg / 2)) break;
                    int ow = Math.Min(span.Right, b.Box.Right) - Math.Max(span.Left, b.Box.Left);
                    if (ow <= Math.Min(span.Width, b.Box.Width) * 0.4f) break;
                    int nh = Math.Min(minH, b.Box.Height), xh = Math.Max(maxH, b.Box.Height);
                    if (xh - nh > Math.Max(6, nh / 3)) break;      // 高差太大，不是同一段（标题 vs 正文）
                    minH = nh; maxH = xh;
                    sumH += b.Box.Height; cntH++;
                    span = Rectangle.Union(span, b.Box);
                    lastBottom = b.Box.Bottom;
                    j++;
                }
                if (j - i >= 2)
                {
                    // 用段落内的平均行高定字号：取最矮行会把整段字压小一圈
                    float cap = CapFromHeight(sumH / (float)cntH);
                    for (int k = i; k < j; k++) list[k].SizeCap = cap;
                }
                i = j > i ? j : i + 1;
            }
        }

        // 把「一行里夹着大段空白」的框按空白列切开。
        //  ① 普通一行里词距很大（导航栏）：子块数与译文空格分词数一一对应时才拆，各画各的；
        //  ② 左栏和正文被 OCR 串成一条（用户名和 Features 拼在一起）：整条放弃替换、保留原文。
        //     判据是「第一个大空白正好落在左栏边界」，导航那种空白分散的不会被误判。
        private static List<Block> SplitByGaps(Bitmap src, List<Block> blocks, out int skipped)
        {
            skipped = 0;
            List<Block> outp = new List<Block>();
            int imgW = src.Width;
            for (int i = 0; i < blocks.Count; i++)
            {
                Block b = blocks[i];
                if (b.Box.Width < 40 || b.Box.Height < 8) { outp.Add(b); continue; }
                int gapX, gapW;
                List<Rectangle> parts = GapParts(src, b.Box, out gapX, out gapW);

                string[] words = Regex.Split(b.Text.Trim(), @"\s+");
                // 串行（左栏文字和正文被 OCR 拼成一句）不再跳过：按用户要求「以 OCR 翻译为准、尽可能都翻」，
                // 这条也照常擦除重绘，只是位置可能不完全对。导航那种多词一行仍走下面的拆块。

                if (parts.Count < 2) { outp.Add(b); continue; }
                if (words.Length == parts.Count && words.Length >= 2)
                {
                    for (int k = 0; k < parts.Count; k++) outp.Add(NewBlock(parts[k], words[k], b.Angle));
                    continue;
                }
                outp.Add(b);
            }
            return outp;
        }


        // 扫描框内每一列有没有墨迹，连续空白宽度 >= 0.6 个字高就当成词间空隙。
        // 同时回报「第一个空隙的起点和宽度」，用来判断这条框是不是横跨了两列。
        private static List<Rectangle> GapParts(Bitmap src, Rectangle box, out int firstGapX, out int firstGapW)
        {
            firstGapX = -1; firstGapW = 0;
            List<Rectangle> parts = new List<Rectangle>();
            int x0 = Math.Max(0, box.Left), x1 = Math.Min(src.Width, box.Right);
            int y0 = Math.Max(0, box.Top), y1 = Math.Min(src.Height, box.Bottom);
            if (x1 - x0 < 10 || y1 - y0 < 6) return parts;

            // 框内背景亮度中位数：比它亮/暗 30 以上的算墨迹
            List<int> lums = new List<int>();
            for (int y = y0; y < y1; y += 2)
                for (int x = x0; x < x1; x += 2)
                    lums.Add(LumOf(src.GetPixel(x, y)));
            if (lums.Count < 8) return parts;
            lums.Sort();
            int bg = lums[lums.Count / 2];

            bool[] ink = new bool[x1 - x0];
            for (int x = x0; x < x1; x++)
            {
                int hit = 0, total = 0;
                for (int y = y0; y < y1; y += 2)
                {
                    total++;
                    if (Math.Abs(LumOf(src.GetPixel(x, y)) - bg) >= 30) hit++;
                }
                ink[x - x0] = total > 0 && hit * 100 >= total * 8;   // 该列有 8% 以上墨迹列才算有字
            }

            int gapNeed = Math.Max(8, (int)(box.Height * 0.6f));
            int startX = -1, lastInk = -1, gapRun = 0;
            for (int i = 0; i < ink.Length; i++)
            {
                if (ink[i])
                {
                    if (startX < 0) startX = i;
                    lastInk = i;
                    gapRun = 0;
                }
                else if (startX >= 0)
                {
                    gapRun++;
                    if (gapRun >= gapNeed && lastInk >= 0)
                    {
                        if (firstGapX < 0) { firstGapX = x0 + lastInk + 1; firstGapW = gapRun; }
                        parts.Add(new Rectangle(x0 + startX, y0, lastInk - startX + 1, y1 - y0));
                        startX = -1;
                        lastInk = -1;
                        gapRun = 0;
                    }
                }
            }
            if (startX >= 0 && lastInk >= startX)
                parts.Add(new Rectangle(x0 + startX, y0, lastInk - startX + 1, y1 - y0));
            return parts;
        }

        // 按文字长度估算「这段字大概占多宽」，OCR 框明显离谱时把右边裁回来。
        private static void FitBoxToText(Block b, int maxRight)
        {
            if (b == null || b.Box.Width <= 0) return;
            float h = b.Box.Height;
            float est = 0f;
            for (int i = 0; i < b.Text.Length; i++)
            {
                char c = b.Text[i];
                if (c == ' ') est += h * 0.3f;
                else if (c < 128) est += h * 0.58f;
                else est += h * 0.98f;
            }
            float allowed = Math.Max(est * 2.4f + 12f, h * 3.2f);
            if (b.Box.Width > allowed)
            {
                int nw = Math.Max(8, (int)allowed);
                b.Box = new Rectangle(b.Box.X, b.Box.Y, nw, b.Box.Height);
            }
            if (maxRight > 0 && b.Box.Right > maxRight)
                b.Box = new Rectangle(b.Box.X, b.Box.Y, Math.Max(8, maxRight - b.Box.X), b.Box.Height);
        }

        // 同一行被切成多个横向小块（「Oct 18,」「2026」这种）时先拼回一整行，
        // 否则每块单独擦、单独排，就会出现只翻出半句、原字还剩一截的情况。
        private static List<Block> MergeSameLine(List<Block> src)
        {
            List<Block> outp = new List<Block>();
            Block cur = null;
            for (int i = 0; i < src.Count; i++)
            {
                Block b = src[i];
                if (cur == null) { cur = NewBlock(b.Box, b.Text, b.Angle); continue; }
                int hAvg = (cur.Box.Height + b.Box.Height) / 2;
                int vGap = b.Box.Y - cur.Box.Bottom;          // 正数=下方，负数=重叠
                int hGap = b.Box.X - cur.Box.Right;           // 正数=右侧
                bool sameRow = vGap >= -Math.Max(5, hAvg / 3) && vGap <= Math.Max(1, hAvg / 4);
                // 词间距只容忍「一个词的宽度以内」：导航栏、分栏标签之间空隙很大，
                // 硬拼成一整行会得到一大块遮罩和一个离原文很远的译文，拆开各画各的更自然
                bool adjacent = hGap >= -Math.Max(5, hAvg / 3) && hGap <= Math.Max(4, hAvg / 4);
                bool similarHeight = Math.Abs(b.Box.Height - cur.Box.Height) <= Math.Max(5, hAvg / 2);
                if (sameRow && adjacent && similarHeight)
                {
                    cur.Box = Rectangle.Union(cur.Box, b.Box);
                    cur.Text = JoinText(cur.Text, b.Text);
                    if (Math.Abs(b.Angle) > Math.Abs(cur.Angle)) cur.Angle = b.Angle;
                    continue;
                }
                outp.Add(cur);
                cur = NewBlock(b.Box, b.Text, b.Angle);
            }
            if (cur != null) outp.Add(cur);
            outp.Sort(delegate(Block a, Block b) { return a.Box.Y != b.Box.Y ? a.Box.Y - b.Box.Y : a.Box.X - b.Box.X; });
            return outp;
        }

        private static Block NewBlock(Rectangle r, string t, float a)
        {
            Block b = new Block();
            b.Box = r; b.Text = t; b.Angle = a;
            return b;
        }

        private static string JoinText(string a, string b)
        {
            if (a.Length == 0) return b;
            if (b.Length == 0) return a;
            char ca = a[a.Length - 1], cb = b[0];
            bool aAscii = ca < 128, bAscii = cb < 128;
            if (aAscii && bAscii) return a + " " + b;
            return a + b;
        }

        // 框下方若是空白（没有墨迹），允许把框向下借一点高度，给折行的长译文腾地方
        private static Rectangle TryGrowDown(Bitmap src, Rectangle r, int limitBottom, int w, int h, string text)
        {
            int need = EstimateLines(text, r.Width, r.Height);
            if (need <= 1) return r;
            int maxGrow = Math.Min(limitBottom - r.Bottom - 1, (int)(r.Height * 0.75f));
            if (maxGrow < 4) return r;
            int grow = 0;
            for (int g = 2; g <= maxGrow; g += 2)
            {
                Rectangle strip = new Rectangle(r.Left, r.Bottom + g - 2, r.Width, 2);
                if (strip.Bottom > h) break;
                if (!IsBlank(src, strip)) break;
                grow = g;
            }
            if (grow < 4) return r;
            Rectangle outR = new Rectangle(r.X, r.Y, r.Width, r.Height + grow);
            return outR;
        }

        private static bool IsBlank(Bitmap src, Rectangle s)
        {
            int minL = 255, maxL = 0, seen = 0;
            for (int y = s.Top; y < s.Bottom; y += 2)
                for (int x = s.Left; x < s.Right; x += 4)
                {
                    if (x < 0 || y < 0 || x >= src.Width || y >= src.Height) continue;
                    Color c = src.GetPixel(x, y);
                    int l = (c.R * 77 + c.G * 151 + c.B * 28) >> 8;
                    if (l < minL) minL = l;
                    if (l > maxL) maxL = l;
                    seen++;
                }
            return seen > 0 && (maxL - minL) <= 12;   // 一整条都是同一种底色才算空白
        }

        private static int EstimateLines(string text, int boxW, int boxH)
        {
            float unit = 12f; // 粗略：12px 字宽约一个汉字
            int perLine = Math.Max(1, (int)(boxW / unit));
            int lines = (text.Length + perLine - 1) / perLine;
            int fit = Math.Max(1, boxH / 14);
            return lines > fit ? lines : 1;
        }

        private static Bitmap Clone(Bitmap src)
        {
            Bitmap b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
            }
            return b;
        }

        // ---------- 擦除 ----------
        private static int EraseBlock(Bitmap bmp, Rectangle r, out bool hard)
        {
            hard = false;
            int rw = r.Width, rh = r.Height;
            int[] px = new int[rw * rh];
            BitmapData bd = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < rh; y++)
                {
                    IntPtr row = bd.Scan0 + y * bd.Stride;
                    int[] tmp = new int[rw];
                    Marshal.Copy(row, tmp, 0, rw);
                    Array.Copy(tmp, 0, px, y * rw, rw);
                }
            }
            finally { bmp.UnlockBits(bd); }

            int[] hist = new int[32];
            for (int i = 0; i < px.Length; i++) hist[Math.Min(31, Lum(px[i]) >> 3)]++;
            int bestBin = 0, bestNum = -1;
            for (int i = 0; i < 32; i++) if (hist[i] > bestNum) { bestNum = hist[i]; bestBin = i; }
            int bgLum = bestBin * 8 + 4;

            bool[] ink = new bool[px.Length];
            int inkCount = 0;
            for (int i = 0; i < px.Length; i++)
            {
                if (Math.Abs(Lum(px[i]) - bgLum) >= 34) { ink[i] = true; inkCount++; }
            }
            if (inkCount > px.Length * 0.55)
            {
                int med = MedianLum(px);
                inkCount = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    bool v = Math.Abs(Lum(px[i]) - med) >= 38;
                    ink[i] = v;
                    if (v) inkCount++;
                }
            }
            if (inkCount == 0) return 0;
            Dilate(ink, rw, rh, 2);

            double sum = 0, sum2 = 0; int n = 0;
            for (int i = 0; i < px.Length; i++)
            {
                if (ink[i]) continue;
                double l = Lum(px[i]);
                sum += l; sum2 += l * l; n++;
            }
            if (n > 0)
            {
                double mean = sum / n;
                if (sum2 / n - mean * mean > 34.0 * 34.0) hard = true;
            }

            int[] rgba = new int[px.Length];
            Array.Copy(px, rgba, px.Length);
            for (int pass = 0; pass < 64; pass++)
            {
                bool changed = false;
                for (int y = 1; y < rh - 1; y++)
                {
                    for (int x = 1; x < rw - 1; x++)
                    {
                        int idx = y * rw + x;
                        if (!ink[idx]) continue;
                        int rr = 0, gg = 0, bb = 0, cnt = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int j = idx + dy * rw + dx;
                                if (ink[j]) continue;
                                int wgt = (dx == 0 || dy == 0) ? 2 : 1;
                                int c = rgba[j];
                                rr += ((c >> 16) & 0xFF) * wgt;
                                gg += ((c >> 8) & 0xFF) * wgt;
                                bb += (c & 0xFF) * wgt;
                                cnt += wgt;
                            }
                        }
                        if (cnt == 0) continue;
                        int nr = rr / cnt, ng = gg / cnt, nb = bb / cnt;
                        int old = rgba[idx];
                        ink[idx] = false;
                        rgba[idx] = (0xFF << 24) | (nr << 16) | (ng << 8) | nb;
                        if (Math.Abs(nr - ((old >> 16) & 0xFF)) + Math.Abs(ng - ((old >> 8) & 0xFF)) + Math.Abs(nb - (old & 0xFF)) > 3)
                            changed = true;
                    }
                }
                if (!changed) break;
            }
            Soften(px, rgba, rw, rh);

            bd = bmp.LockBits(r, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < rh; y++)
                {
                    int[] tmp = new int[rw];
                    Array.Copy(rgba, y * rw, tmp, 0, rw);
                    Marshal.Copy(tmp, 0, bd.Scan0 + y * bd.Stride, rw);
                }
            }
            finally { bmp.UnlockBits(bd); }
            return inkCount;
        }

        private static void Soften(int[] before, int[] after, int w, int h)
        {
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    if (before[i] == after[i]) continue;
                    int r = 0, g = 0, b = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int c = after[i + dy * w + dx];
                            r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF;
                        }
                    after[i] = (0xFF << 24) | ((r / 9) << 16) | ((g / 9) << 8) | (b / 9);
                }
            }
        }

        private static void Dilate(bool[] m, int w, int h, int radius)
        {
            bool[] src = new bool[m.Length];
            Array.Copy(m, src, m.Length);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (src[y * w + x]) continue;
                    bool hit = false;
                    for (int dy = -radius; dy <= radius && !hit; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= h) continue;
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0 || xx >= w) continue;
                            if (src[yy * w + xx]) { hit = true; break; }
                        }
                    }
                    if (hit) m[y * w + x] = true;
                }
            }
        }

        private static int Lum(int c)
        {
            int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
            return (r * 77 + g * 151 + b * 28) >> 8;
        }

        private static int MedianLum(int[] px)
        {
            int[] hist = new int[256];
            for (int i = 0; i < px.Length; i++) hist[Lum(px[i])]++;
            int half = px.Length / 2, acc = 0;
            for (int i = 0; i < 256; i++)
            {
                acc += hist[i];
                if (acc >= half) return i;
            }
            return 128;
        }

        // ---------- 回填 ----------
        private static void DrawText(Bitmap bmp, Rectangle box, string text, float angle, Color textColor, int bgLum, float sizeCap, List<Rectangle> obstacles, out bool wrapped)
        {
            wrapped = false;
            int W = box.Width, H = box.Height;
            // 允许往右侧/下方空白里溢出一点点，避免长译文被压成小字
            float maxW = W * 1.02f, maxH = H * 1.06f;
            // 段落首行右侧有头像/表情时，把排版宽度收到图形左边，别把字画到图标上
            if (obstacles != null)
            {
                for (int i = 0; i < obstacles.Count; i++)
                {
                    Rectangle o = obstacles[i];
                    if (o.Bottom <= box.Top + 2 || o.Top >= box.Top + H * 0.65f) continue;
                    float cut = o.Left - box.X - 3f;
                    if (cut >= W * 0.45f && cut < maxW) maxW = cut;
                }
            }
            if (Math.Abs(LumOf(textColor) - bgLum) < 70)
                textColor = bgLum > 128 ? Color.FromArgb(24, 24, 28) : Color.FromArgb(242, 244, 248);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                Font font = null;
                List<string> lines = null;
                float startSize = Math.Min(H * 0.79f, sizeCap > 0f ? sizeCap : 24f);
                Font singleFont = null, multiFont = null;
                List<string> singleLines = null, multiLines = null;
                // 候选一：一行放得下的最大字号（最像原图排版）
                for (float size = startSize; size >= 7f; size -= 0.5f)
                {
                    Font f = null;
                    try { f = new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Pixel); }
                    catch (Exception) { if (f != null) f.Dispose(); continue; }

                    List<string> ls = WrapText(g, text, f, maxW - 3);
                    float lh = f.GetHeight(g);
                    if (ls.Count == 1 && g.MeasureString(ls[0], f).Width <= maxW - 2 && lh <= maxH + 1)
                    {
                        singleFont = f; singleLines = ls;
                        break;
                    }
                    f.Dispose();
                }
                // 候选二：允许折行，整块塞进框内
                for (float size = startSize; size >= 7f; size -= 0.5f)
                {
                    Font f = null;
                    try { f = new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Pixel); }
                    catch (Exception) { if (f != null) f.Dispose(); continue; }

                    List<string> ls = WrapText(g, text, f, maxW - 3);
                    float lh = f.GetHeight(g);
                    if (ls.Count * lh <= maxH + 1)
                    {
                        float widest = 0f;
                        for (int i = 0; i < ls.Count; i++) widest = Math.Max(widest, g.MeasureString(ls[i], f).Width);
                        if (widest <= maxW - 2) { multiFont = f; multiLines = ls; break; }
                    }
                    f.Dispose();
                }
                // 两个字号取大的那个（宁可折行也不想把字压成小蚂蚁）；
                // 但折行只多一点点字时，优先不折行，免得「2026年1/0月18日」这种断得难看
                if (singleFont != null && (multiFont == null || singleFont.Size >= multiFont.Size * 0.62f - 0.5f))
                {
                    font = singleFont; lines = singleLines;
                    if (multiFont != null) multiFont.Dispose();
                }
                else if (multiFont != null)
                {
                    font = multiFont; lines = multiLines;
                    if (singleFont != null) singleFont.Dispose();
                }
                else
                {
                    try { font = new Font("Microsoft YaHei UI", 7f, FontStyle.Regular, GraphicsUnit.Pixel); }
                    catch (Exception) { return; }
                    lines = WrapText(g, text, font, maxW - 2);
                }
                if (lines.Count > 1) wrapped = true;
                if (Environment.GetEnvironmentVariable("QRSNAP_DBG_REAL") == "1")
                {
                    NativeShell.Log("mt realistic font box=" + box.Width + "x" + box.Height + " size=" + font.Size.ToString("0.0")
                        + " lines=" + lines.Count + " color=" + textColor.R + "," + textColor.G + "," + textColor.B + " bg=" + bgLum);
                    // 调试框单独输出，绝不画进成品图
                }


                GraphicsState gs = g.Save();
                // 只允许在原框范围内（含少量空白余量）绘制，宁可裁掉也不能糊到隔壁行
                g.SetClip(new RectangleF(box.X - 1f, box.Y - 1f, maxW + 2f, maxH + 2f));
                float cx = box.X + W / 2f, cy = box.Y + H / 2f;
                // 译文一律按正常横排从左到右画，不再跟着原文倾斜/竖排走

                float lh2 = Math.Min(font.GetHeight(g), H * 0.98f);
                float total = lines.Count * lh2;
                float top = cy - total / 2f;
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Near;   // 跟原文一样靠左，居中会显得很「贴纸」
                    sf.LineAlignment = StringAlignment.Near;
                    sf.Trimming = StringTrimming.None;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    using (SolidBrush b = new SolidBrush(textColor))
                    {
                        for (int i = 0; i < lines.Count; i++)
                            g.DrawString(lines[i], font, b, new RectangleF(box.X + 1f, top + i * lh2, maxW - 2f, lh2), sf);
                    }
                }
                g.Restore(gs);
                font.Dispose();
            }
        }

        // 背景取框内亮度中位数；文字色取偏离中位数最多那一侧的像素（亮字取亮端、暗字取暗端）
        private static void SampleColors(Bitmap bmp, Rectangle box, out Color textColor, out int bgLum)
        {
            textColor = Color.Black;
            bgLum = 128;
            try
            {
                List<int> lums = new List<int>();
                List<Color> cols = new List<Color>();
                for (int y = box.Top; y < box.Bottom; y += 2)
                    for (int x = box.Left; x < box.Right; x += 2)
                    {
                        Color c = bmp.GetPixel(x, y);
                        cols.Add(c);
                        lums.Add((c.R * 77 + c.G * 151 + c.B * 28) >> 8);
                    }
                if (lums.Count < 8) return;
                int[] hist = new int[256];
                for (int i = 0; i < lums.Count; i++) hist[lums[i]]++;
                int half = lums.Count / 2, acc = 0, med = 128;
                for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= half) { med = i; break; } }
                bgLum = med;

                int brightCnt = 0, darkCnt = 0;
                foreach (int l in lums) { if (l > med + 32) brightCnt++; else if (l < med - 32) darkCnt++; }
                bool useBright = brightCnt >= darkCnt;
                int selCnt = useBright ? brightCnt : darkCnt;
                if (selCnt < 3) return;

                int maxDist = 0;
                for (int i = 0; i < lums.Count; i++)
                {
                    int d = useBright ? lums[i] - med : med - lums[i];
                    if (d > maxDist) maxDist = d;
                }
                if (maxDist < 30) return;
                int cut = (int)(maxDist * 0.7);
                int tr = 0, tg = 0, tb = 0, tn = 0;
                for (int i = 0; i < lums.Count; i++)
                {
                    int d = useBright ? lums[i] - med : med - lums[i];
                    if (d < cut) continue;
                    Color c = cols[i];
                    tr += c.R; tg += c.G; tb += c.B; tn++;
                }
                if (tn >= 3) textColor = Color.FromArgb(tr / tn, tg / tn, tb / tn);
            }
            catch (Exception) { }
        }

        private static int LumOf(Color c) { return (c.R * 77 + c.G * 151 + c.B * 28) >> 8; }

        // 给效率模式的底板取「这一块本来的底色」：底板刷成同色，看上去就像没盖东西，
        // 只剩中文浮在上面。量化到 16 级取众数，文字是少数派，不会把底色带偏。
        public static Color SampleBackground(Bitmap bmp, Rectangle box)
        {
            Color fallback = Color.FromArgb(14, 18, 26);
            if (bmp == null) return fallback;
            int x0 = Math.Max(0, box.Left), y0 = Math.Max(0, box.Top);
            int x1 = Math.Min(bmp.Width, box.Right), y1 = Math.Min(bmp.Height, box.Bottom);
            if (x1 <= x0 || y1 <= y0) return fallback;
            Dictionary<int, int> hist = new Dictionary<int, int>();
            int step = (x1 - x0) * (y1 - y0) > 4000 ? 2 : 1;
            for (int y = y0; y < y1; y += step)
                for (int x = x0; x < x1; x += step)
                {
                    Color c = bmp.GetPixel(x, y);
                    // 6bit/通道（步长 4）：比 16 级细，底板颜色和背景几乎一致
                    int k = ((c.R >> 2) << 12) | ((c.G >> 2) << 6) | (c.B >> 2);
                    int n;
                    hist.TryGetValue(k, out n);
                    hist[k] = n + 1;
                }
            int bestK = 0, bestN = -1;
            foreach (KeyValuePair<int, int> kv in hist)
                if (kv.Value > bestN) { bestN = kv.Value; bestK = kv.Key; }
            if (bestN < 0) return fallback;
            return Color.FromArgb(((bestK >> 12) & 0x3F) * 4 + 2,
                ((bestK >> 6) & 0x3F) * 4 + 2, (bestK & 0x3F) * 4 + 2);
        }

        private static List<string> WrapText(Graphics g, string text, Font f, float maxW)
        {
            List<string> lines = new List<string>();
            if (string.IsNullOrEmpty(text)) { lines.Add(""); return lines; }
            // 按「英文单词整体 / 单个汉字 / 单个标点」切成最小单位，尽量不在单词中间断行
            List<string> tokens = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\r' || c == '\n') { tokens.Add("\n"); i++; continue; }
                if (c == ' ')
                {
                    int j = i;
                    while (j < text.Length && text[j] == ' ') j++;
                    tokens.Add(" ");
                    i = j;
                    continue;
                }
                if (c < 128)
                {
                    int j = i;
                    while (j < text.Length && text[j] < 128 && text[j] != ' ') j++;
                    tokens.Add(text.Substring(i, j - i));
                    i = j;
                    continue;
                }
                tokens.Add(c.ToString());
                i++;
            }

            const string noHead = "，。、！？：；）】」』》…—·%";
            const string noTail = "（【「『《";
            string cur = "";
            for (int t = 0; t < tokens.Count; t++)
            {
                string tk = tokens[t];
                if (tk == "\n") { lines.Add(cur.TrimEnd()); cur = ""; continue; }
                if (tk == " ")
                {
                    if (cur.Length > 0) cur += " ";
                    continue;
                }
                string cand = cur + tk;
                if (cur.Length > 0 && g.MeasureString(cand, f).Width > maxW)
                {
                    // 收尾标点不另起一行，宁可让它稍微顶出边界
                    if (noHead.IndexOf(tk[0]) >= 0)
                    {
                        lines.Add((cur + tk).TrimEnd());
                        cur = "";
                        continue;
                    }
                    // 开引号/开括号不留在行尾，带到下一行
                    char last = cur[cur.Length - 1];
                    if (noTail.IndexOf(last) >= 0 && cur.Length > 1)
                    {
                        lines.Add(cur.Substring(0, cur.Length - 1).TrimEnd());
                        cur = last + tk;
                        continue;
                    }
                    lines.Add(cur.TrimEnd());
                    cur = tk;
                }
                else cur = cand;
            }
            if (cur.TrimEnd().Length > 0) lines.Add(cur.TrimEnd());
            if (lines.Count == 0) lines.Add(text);

            // 兜底：万一某个「单词」比整行还宽，硬切
            List<string> fixedLines = new List<string>();
            foreach (string line in lines)
            {
                string c2 = line;
                while (c2.Length > 0)
                {
                    int take = c2.Length;
                    while (take > 1 && g.MeasureString(c2.Substring(0, take), f).Width > maxW) take--;
                    fixedLines.Add(c2.Substring(0, take));
                    c2 = c2.Substring(take).TrimStart();
                }
                if (line.Length == 0) fixedLines.Add("");
            }
            if (fixedLines.Count == 0) fixedLines.Add(text);
            return fixedLines;
        }
    }
}
