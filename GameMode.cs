using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QRSnap
{
    // 游戏专属模式：固定多个字幕区域、变化跳帧、三种触发方式、字幕过滤/合并
    internal static class GameRuntime
    {
        private static System.Threading.Timer _timer;
        private static System.Threading.Timer _watch;
        private static bool _busy;
        private static int _gen;
        private static byte[] _prevSig;
        private static bool _paused;
        private static bool _overlayClosed;
        private static string _prevProfile;

        public static bool Running { get { return AppConfig.GameOn; } }
        public static string LastError = "";

        public static List<Rectangle> Regions()
        {
            List<Rectangle> res = new List<Rectangle>();
            string raw = AppConfig.GameRegions ?? "";
            string[] parts = raw.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string[] n = parts[i].Split(',');
                if (n.Length != 4) continue;
                int x, y, w, h;
                if (!int.TryParse(n[0], out x) || !int.TryParse(n[1], out y)
                    || !int.TryParse(n[2], out w) || !int.TryParse(n[3], out h)) continue;
                if (w < 16 || h < 12) continue;
                res.Add(new Rectangle(x, y, w, h));
            }
            return res;
        }

        public static void HighlightRegion(int index)
        {
            List<Rectangle> list = Regions();
            if (index < 0 || index >= list.Count) { RegionHighlight.Hide(); return; }
            RegionHighlight.Show(list[index], index + 1);
        }

        public static void SaveRegions(List<Rectangle> list)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(list[i].X).Append(',').Append(list[i].Y).Append(',')
                  .Append(list[i].Width).Append(',').Append(list[i].Height);
            }
            AppConfig.GameRegions = sb.ToString();
            try { AppConfig.Save(); } catch (Exception) { }
        }

        public static bool AddRegion(Rectangle r)
        {
            if (r.Width < 16 || r.Height < 12) return false;
            List<Rectangle> list = Regions();
            for (int i = 0; i < list.Count; i++)
            {
                Rectangle a = list[i];
                if (Math.Abs(a.X - r.X) < 3 && Math.Abs(a.Y - r.Y) < 3
                    && Math.Abs(a.Width - r.Width) < 3 && Math.Abs(a.Height - r.Height) < 3)
                    return false;
            }
            list.Add(r);
            SaveRegions(list);
            return true;
        }

        public static bool RemoveRegion(int index)
        {
            List<Rectangle> list = Regions();
            if (index < 0 || index >= list.Count) return false;
            list.RemoveAt(index);
            SaveRegions(list);
            return true;
        }

        public static void ClearRegions()
        {
            AppConfig.GameRegions = "";
            try { AppConfig.Save(); } catch (Exception) { }
        }

        public static bool Start(out string err)
        {
            err = null;
            List<Rectangle> rs = Regions();
            if (rs.Count == 0)
            {
                err = "先添加至少一个固定字幕区域";
                LastError = err;
                return false;
            }
            string profile = AppConfig.NormalizeMtProfile(AppConfig.GameModel);
            if (profile == AppConfig.MtProfileEfficiency && !MtRunner.GameReadyFor("efficiency"))
            {
                err = "效率档需要先放入 PLaMo 3 Translate Q4_K_M";
                LastError = err;
                return false;
            }
            if (profile == AppConfig.MtProfileTurbo && !MtRunner.GameReadyFor("turbo"))
            {
                err = "极速档需要先放入 PLaMo 3 Translate Q2_K";
                LastError = err;
                return false;
            }
            if (profile == AppConfig.MtProfileStandard && !MtRunner.Ready)
            {
                err = "标准档需要先导入 HyMT1.5 模型";
                LastError = err;
                return false;
            }
            _prevProfile = AppConfig.NormalizeMtProfile(AppConfig.MtProfile);
            AppConfig.MtProfile = profile;
            AppConfig.GameModel = profile;
            AppConfig.GameTrigger = 2;   // 只保留手动快捷键翻译
            AppConfig.GameOn = true;
            LastError = "";
            _overlayClosed = false;
            try { AppConfig.Save(); } catch (Exception) { }
            try { JadeHost.HideWindow(true); } catch (Exception) { }   // 让游戏画面露出来
            _prevSig = null;
            StartTimer();
            WarmModel(profile);
            JadeHost.Log("[game] start regions=" + rs.Count + " profile=" + profile
                + " trigger=" + AppConfig.GameTrigger + " interval=" + AppConfig.GameIntervalMs);
            return true;
        }

        public static void Stop()
        {
            if (_timer != null) { try { _timer.Dispose(); } catch (Exception) { } _timer = null; }
            if (_watch != null) { try { _watch.Dispose(); } catch (Exception) { } _watch = null; }
            _gen++;
            _busy = false;
            _paused = false;
            _overlayClosed = false;
            AppConfig.GameOn = false;
            LastError = "";
            if (!string.IsNullOrEmpty(_prevProfile)) AppConfig.MtProfile = _prevProfile;
            _prevProfile = null;
            try { AppConfig.Save(); } catch (Exception) { }
            try { TransOverlay.HideOverlay(); } catch (Exception) { }
            try { JadeHost.ShowWindow(true); } catch (Exception) { }
            // 关闭游戏模式释放所有本地模型
            try { MtRunner.Invalidate(); } catch (Exception) { }
            try { LlamaMt.Shutdown(); } catch (Exception) { }
            JadeHost.Log("[game] stop, models released");
        }

        public static void Reconfigure()
        {
            if (!AppConfig.GameOn) return;
            _prevSig = null;
            StartTimer();
            JadeHost.Log("[game] reconfigure trigger=" + AppConfig.GameTrigger + " interval=" + AppConfig.GameIntervalMs);
        }

        public static void Pause(bool paused)
        {
            _paused = paused;
            if (paused)
            {
                if (_timer != null) { try { _timer.Dispose(); } catch (Exception) { } _timer = null; }
                if (_watch != null) { try { _watch.Dispose(); } catch (Exception) { } _watch = null; }
            }
            else if (AppConfig.GameOn) StartTimer();
        }

        // 游戏模式下只有「隐藏遮罩」快捷键和遮罩上的关闭按钮能关遮罩
        public static void ToggleOverlay()
        {
            if (_overlayClosed)
            {
                _overlayClosed = false;
                _prevSig = null;
                Trigger(true);
            }
            else
            {
                _overlayClosed = true;
                _gen++;
                try { TransOverlay.HideOverlay(); } catch (Exception) { }
                JadeHost.Log("[game] overlay closed by user");
            }
        }

        public static void SetOverlayClosed(bool closed)
        {
            if (!AppConfig.GameOn) return;
            if (closed)
            {
                _overlayClosed = true;
                _gen++;
                try { TransOverlay.HideOverlay(); } catch (Exception) { }
            }
            else _overlayClosed = false;
        }

        private static void StartTimer()
        {
            if (_timer != null) { try { _timer.Dispose(); } catch (Exception) { } _timer = null; }
            if (AppConfig.GameTrigger == 2) return;   // 手动：不跑定时器
            int due = AppConfig.GameTrigger == 0 ? 250 : Math.Max(250, AppConfig.GameIntervalMs);
            _timer = new System.Threading.Timer(TimerTick, null, due, due);
        }

        private static void TimerTick(object state)
        {
            try { JadeHost.RunOnUi(delegate { Trigger(false); }); }
            catch (Exception ex) { JadeHost.Log("[game] timer fail " + ex.Message); }
        }

        // 固定间隔/手动模式也要盯着画面：一旦变了就先把旧遮罩收掉，别让旧译文停在画面上
        private static void StartWatch()
        {
            if (_watch != null) { try { _watch.Dispose(); } catch (Exception) { } _watch = null; }
            _watch = new System.Threading.Timer(WatchTick, null, 250, 250);
        }

        private static void WatchTick(object state)
        {
            try { JadeHost.RunOnUi(delegate { WatchChange(); }); }
            catch (Exception ex) { JadeHost.Log("[game] watch fail " + ex.Message); }
        }

        private static void WatchChange()
        {
            if (_paused || _overlayClosed || !AppConfig.GameOn) return;
            if (AppConfig.GameTrigger == 0) return;   // 变化模式由翻译轮次自己处理
            List<Rectangle> regions = Regions();
            if (regions.Count == 0) return;
            Rectangle union = Union(regions);
            if (union.Width < 16 || union.Height < 12) return;

            bool excluded = TransOverlay.CaptureExcluded;
            if (!excluded)
            {
                try { TransOverlay.HideOverlay(); } catch (Exception) { }
                Thread.Sleep(16);
            }
            Bitmap bmp = Capture(union);
            if (bmp == null)
            {
                if (!excluded) { try { TransOverlay.Restore(); } catch (Exception) { } }
                return;
            }
            byte[] sig = Signature(bmp, union, regions);
            bmp.Dispose();
            if (!excluded) { try { TransOverlay.Restore(); } catch (Exception) { } }

            double diff = _prevSig == null ? 0 : SignatureDiff(_prevSig, sig);
            if (_prevSig != null && diff >= SensitivityThreshold())
            {
                _prevSig = sig;
                try { TransOverlay.HideOverlay(); } catch (Exception) { }
                JadeHost.Log("[game] page changed, hide overlay diff=" + diff.ToString("0.000"));
            }
            else if (_prevSig == null) _prevSig = sig;
            else if (diff < SensitivityThreshold()) _prevSig = sig;
        }

        private static void WarmModel(string profile)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string[] plan = MtRunner.Router.PlanFor("en", "zh", AppConfig.MtPair, profile);
                    if (plan.Length == 2)
                    {
                        LlamaMt.Translate(plan[1], "Hello", "zh");
                        JadeHost.Log("[game] warmed " + System.IO.Path.GetFileName(plan[1]));
                    }
                }
                catch (Exception ex) { JadeHost.Log("[game] warm fail " + ex.Message); }
            });
        }

        // force=true：手动快捷键，翻译中则中断与上一轮，开新一轮
        public static void Trigger(bool force)
        {
            if (_paused || _overlayClosed)
            {
                JadeHost.Log("[game] skip paused=" + (_paused ? 1 : 0) + " closed=" + (_overlayClosed ? 1 : 0));
                return;
            }
            if (!AppConfig.GameOn) return;
            if (_busy)
            {
                if (!force)
                {
                    JadeHost.Log("[game] skip busy");
                    return;
                }
                _gen++;
                try { LlamaMt.Shutdown(); } catch (Exception) { }
                _busy = false;
                JadeHost.Log("[game] force restart");
            }

            _gen++;
            int g = _gen;
            _busy = true;
            try { DoTick(g, force); }
            catch (Exception ex) { JadeHost.Log("[game] tick fail " + ex.Message); }
            finally { if (_gen == g) _busy = false; }
        }

        private static void DoTick(int gen, bool force)
        {
            List<Rectangle> regions = Regions();
            if (regions.Count == 0) return;
            Rectangle union = Union(regions);
            if (union.Width < 16 || union.Height < 12) return;
            JadeHost.GameBusy("识字中", "OCR · " + Ocr.EffectiveName());

            // 优先让遮罩从截图里排除，这样不用每帧隐藏：不会闪屏
            bool excluded = TransOverlay.CaptureExcluded;
            if (!excluded)
            {
                try { TransOverlay.HideOverlay(); } catch (Exception) { }
                Thread.Sleep(24);
            }
            Bitmap bmp = Capture(union);
            if (bmp == null)
            {
                if (!excluded) { try { TransOverlay.Restore(); } catch (Exception) { } }
                return;
            }
            byte[] sig = Signature(bmp, union, regions);
            double diff = _prevSig == null ? 1.0 : SignatureDiff(_prevSig, sig);
            double threshold = SensitivityThreshold();
            bool changed = _prevSig == null || diff >= threshold;
            _prevSig = sig;
            JadeHost.Log("[game] diff=" + diff.ToString("0.000") + " threshold=" + threshold.ToString("0.000")
                + " changed=" + (changed ? 1 : 0) + " excluded=" + (excluded ? 1 : 0));
            if (!force && AppConfig.GameTrigger == 0 && !changed)
            {
                bmp.Dispose();
                if (!excluded) { try { TransOverlay.Restore(); } catch (Exception) { } }
                return;
            }

            // 没排除能力的系统才需要恢复；排除成功时旧遮罩根本没动
            if (!excluded) { try { TransOverlay.Restore(); } catch (Exception) { } }

            try
            {
                string log;
                List<Rectangle> obstacles;
                List<OcrSegment> segs = AppConfig.OcrFusion
                    ? Ocr.LayoutSegmentsFused(bmp, out obstacles, out log)
                    : Ocr.LayoutSegments(bmp, out obstacles, out log);
                if (segs == null) segs = new List<OcrSegment>();

                segs = GameFilter.KeepDialogue(segs, union, regions);
                if (AppConfig.GameMerge)
                    segs = GameFilter.MergeLines(segs, union, regions);

                if (gen != _gen) return;
                if (segs.Count == 0)
                {
                    JadeHost.Log("[game] no dialogue");
                    JadeHost.GameDone("没有识别到字幕", "模型 · " + JadeHost.ModelLabel(AppConfig.GameModel), false);
                    return;
                }
                JadeHost.GameBusy("翻译中", "模型 · " + JadeHost.ModelLabel(AppConfig.GameModel));
                int lines = JadeHost.GameTranslate(bmp, union, segs);
                if (_overlayClosed) { try { TransOverlay.HideOverlay(); } catch (Exception) { } }
                JadeHost.GameDone(lines > 0 ? "翻译完成" : "没有识别到字幕", "模型 · " + JadeHost.ModelLabel(AppConfig.GameModel) + (lines > 0 ? " · " + lines + " 行" : ""), lines > 0);
                JadeHost.Log("[game] shown=" + lines + " blocks=" + segs.Count
                    + " trigger=" + AppConfig.GameTrigger + " force=" + (force ? 1 : 0));
            }
            finally
            {
                bmp.Dispose();
            }
        }

        private static double SensitivityThreshold()
        {
            switch (AppConfig.GameSensitivity)
            {
                case 0: return 0.004;   // 超灵敏
                case 1: return 0.008;   // 灵敏
                case 3: return 0.030;   // 迟钝
                case 4: return 0.060;   // 最迟钝
                default: return 0.015;  // 平衡
            }
        }

        private static byte[] Signature(Bitmap bmp, Rectangle union, List<Rectangle> regions)
        {
            List<byte> data = new List<byte>();
            for (int i = 0; i < regions.Count; i++)
            {
                Rectangle r = regions[i];
                int sx = r.Left - union.X, sy = r.Top - union.Y;
                int sw = Math.Min(r.Width, bmp.Width - sx);
                int sh = Math.Min(r.Height, bmp.Height - sy);
                if (sw < 4 || sh < 4) continue;
                using (Bitmap small = new Bitmap(128, 48, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.Clear(Color.Black);
                        g.DrawImage(bmp, new Rectangle(0, 0, 128, 48),
                            new Rectangle(sx, sy, sw, sh), GraphicsUnit.Pixel);
                    }
                    for (int y = 0; y < 48; y++)
                        for (int x = 0; x < 128; x++)
                        {
                            Color c = small.GetPixel(x, y);
                            data.Add((byte)((c.R * 77 + c.G * 151 + c.B * 28) >> 8));
                        }
                }
            }
            return data.ToArray();
        }

        private static double SignatureDiff(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length == 0 || b.Length != a.Length) return 1.0;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > 16) diff++;
            return (double)diff / a.Length;
        }

        private static Bitmap Capture(Rectangle r)
        {
            try
            {
                Bitmap b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
                return b;
            }
            catch (Exception ex)
            {
                JadeHost.Log("[game] capture fail " + ex.Message);
                return null;
            }
        }

        private static Rectangle Union(List<Rectangle> list)
        {
            Rectangle u = list[0];
            for (int i = 1; i < list.Count; i++) u = Rectangle.Union(u, list[i]);
            return u;
        }
    }

    internal static class GameFilter
    {
        public static List<OcrSegment> KeepDialogue(List<OcrSegment> segs, Rectangle union, List<Rectangle> regions)
        {
            List<OcrSegment> res = new List<OcrSegment>();
            for (int i = 0; i < segs.Count; i++)
            {
                OcrSegment s = segs[i];
                if (s.Box.Width < 4 || s.Box.Height < 4) continue;
                if (!InAnyRegion(s.Box, union, regions)) continue;
                res.Add(s);
            }
            return res;
        }

        public static List<OcrSegment> MergeLines(List<OcrSegment> segs, Rectangle union, List<Rectangle> regions)
        {
            List<OcrSegment> sorted = new List<OcrSegment>(segs);
            sorted.Sort(delegate(OcrSegment a, OcrSegment b)
            {
                int ay = a.Box.Y + a.Box.Height / 2, by = b.Box.Y + b.Box.Height / 2;
                if (Math.Abs(ay - by) > 4) return ay.CompareTo(by);
                return a.Box.X.CompareTo(b.Box.X);
            });

            List<OcrSegment> res = new List<OcrSegment>();
            for (int i = 0; i < sorted.Count; i++)
            {
                OcrSegment cur = sorted[i];
                if (res.Count == 0) { res.Add(cur); continue; }
                OcrSegment last = res[res.Count - 1];
                if (!CanJoin(last, cur, union, regions)) { res.Add(cur); continue; }
                last.Text = Join(last.Text, cur.Text);
                last.Box = Rectangle.Union(last.Box, cur.Box);
                if (cur.SourceBoxes != null && cur.SourceBoxes.Count > 0)
                {
                    if (last.SourceBoxes == null) last.SourceBoxes = new List<Rectangle>();
                    last.SourceBoxes.AddRange(cur.SourceBoxes);
                }
                last.LowConfidence = last.LowConfidence || cur.LowConfidence;
            }
            return res;
        }

        private static bool CanJoin(OcrSegment a, OcrSegment b, Rectangle union, List<Rectangle> regions)
        {
            if (RegionIndex(a.Box, union, regions) != RegionIndex(b.Box, union, regions)) return false;
            int gap = b.Box.Top - a.Box.Bottom;
            if (gap < -Math.Max(2, a.Box.Height / 2) || gap > Math.Max(6, a.Box.Height)) return false;
            int left = Math.Max(a.Box.Left, b.Box.Left);
            int right = Math.Min(a.Box.Right, b.Box.Right);
            if (right <= left) return false;
            double overlap = (double)(right - left) / Math.Max(1, Math.Min(a.Box.Width, b.Box.Width));
            if (overlap < 0.25) return false;
            string at = (a.Text ?? "").Trim();
            if (at.Length == 0) return true;
            char end = at[at.Length - 1];
            if (end == '.' || end == '!' || end == '?' || end == '。' || end == '！' || end == '？' || end == '…') return false;
            string bt = (b.Text ?? "").Trim();
            if (bt.Length == 0) return false;
            char first = bt[0];
            if (char.IsLower(first)) return true;
            if (first >= '\u3040' && first <= '\u9FFF') return true;   // 日文/中文续行
            if (end == ',' || end == '，' || end == '、' || end == '-' || end == '—') return true;
            string low = bt.ToLowerInvariant();
            string[] conn = { "and ", "or ", "but ", "of ", "the ", "to ", "in ", "with ", "for ", "as ", "that ", "which " };
            for (int i = 0; i < conn.Length; i++) if (low.StartsWith(conn[i])) return true;
            return false;
        }

        private static bool InAnyRegion(Rectangle box, Rectangle union, List<Rectangle> regions)
        {
            return RegionIndex(box, union, regions) >= 0;
        }

        private static int RegionIndex(Rectangle box, Rectangle union, List<Rectangle> regions)
        {
            int cx = box.X + box.Width / 2;
            int cy = box.Y + box.Height / 2;
            for (int i = 0; i < regions.Count; i++)
            {
                Rectangle r = regions[i];
                int l = r.Left - union.X, t = r.Top - union.Y;
                int rr = r.Right - union.X, bb = r.Bottom - union.Y;
                if (cx >= l && cx <= rr && cy >= t && cy <= bb) return i;
            }
            return -1;
        }

        private static string Join(string a, string b)
        {
            a = (a ?? "").Trim();
            b = (b ?? "").Trim();
            if (a.Length == 0) return b;
            if (b.Length == 0) return a;
            char x = a[a.Length - 1], y = b[0];
            if (x < 128 && y < 128) return a + " " + b;
            return a + b;
        }
    }
    // 游戏设置里悬停「删除」时，在屏幕上高亮对应区域框
    internal static class RegionHighlight
    {
        private static RegionHighlightForm _form;

        public static void Show(Rectangle r, int index)
        {
            Hide();
            if (r.Width < 8 || r.Height < 8) return;
            try { _form = new RegionHighlightForm(r, index); _form.Show(); NativeShell.Log("[gamehover] show index=" + index + " rect=" + r.X + "," + r.Y + "," + r.Width + "x" + r.Height); }
            catch (Exception) { _form = null; }
        }

        public static void Hide()
        {
            if (_form == null) return;
            try { _form.Close(); } catch (Exception) { }
            try { _form.Dispose(); } catch (Exception) { }
            _form = null;
        }
    }

    internal sealed class RegionHighlightForm : Form
    {
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
        private readonly Rectangle _r;
        private readonly int _index;
        private readonly System.Windows.Forms.Timer _timer;

        public RegionHighlightForm(Rectangle r, int index)
        {
            _r = r;
            _index = index;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Fuchsia;
            TransparencyKey = Color.Fuchsia;
            Bounds = new Rectangle(r.X - 4, r.Y - 4, r.Width + 8, r.Height + 8);
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 6000;
            _timer.Tick += delegate { _timer.Stop(); RegionHighlight.Hide(); };
            _timer.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040); } catch (Exception) { }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x20 | 0x80 | 0x08000000;   // 鼠标穿透 + 工具窗口 + 不抢焦点
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Color.FromArgb(255, 210, 70), 2f))
            {
                pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
            }
            string label = "区域 " + _index;
            using (Font f = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(235, 40, 44, 54)))
            using (SolidBrush fg = new SolidBrush(Color.FromArgb(255, 255, 220, 100)))
            {
                SizeF sz = e.Graphics.MeasureString(label, f);
                e.Graphics.FillRectangle(bg, 4, 4, sz.Width + 8, sz.Height + 2);
                e.Graphics.DrawString(label, f, fg, 8, 5);
            }
        }
    }
}
