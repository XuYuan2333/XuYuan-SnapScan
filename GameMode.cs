using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace QRSnap
{
    // 游戏专属模式：固定多个字幕区域、变化跳帧、三种触发方式、字幕过滤/合并
    internal static class GameRuntime
    {
        private static System.Windows.Forms.Timer _timer;
        private static bool _busy;
        private static int _gen;
        private static ulong _lastHash;
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
            if (profile == AppConfig.MtProfileEfficiency && !MtRunner.GameReady)
            {
                err = "效率档需要先导入 Qwen2.5-0.5B 模型";
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
            AppConfig.GameOn = true;
            LastError = "";
            _overlayClosed = false;
            try { AppConfig.Save(); } catch (Exception) { }
            try { JadeHost.HideWindow(true); } catch (Exception) { }   // 让游戏画面露出来
            _lastHash = 0;
            StartTimer();
            WarmModel(profile);
            JadeHost.Log("[game] start regions=" + rs.Count + " profile=" + profile
                + " trigger=" + AppConfig.GameTrigger + " interval=" + AppConfig.GameIntervalMs);
            return true;
        }

        public static void Stop()
        {
            if (_timer != null) _timer.Stop();
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
            _lastHash = 0;
            StartTimer();
            JadeHost.Log("[game] reconfigure trigger=" + AppConfig.GameTrigger + " interval=" + AppConfig.GameIntervalMs);
        }

        public static void Pause(bool paused)
        {
            _paused = paused;
            if (paused)
            {
                if (_timer != null) _timer.Stop();
            }
            else if (AppConfig.GameOn) StartTimer();
        }

        // 游戏模式下只有「隐藏遮罩」快捷键和遮罩上的关闭按钮能关遮罩
        public static void ToggleOverlay()
        {
            if (_overlayClosed)
            {
                _overlayClosed = false;
                _lastHash = 0;
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
            if (_timer == null)
            {
                _timer = new System.Windows.Forms.Timer();
                _timer.Tick += delegate { Trigger(false); };
            }
            _timer.Stop();
            if (AppConfig.GameTrigger == 2) return;   // 手动：不跑定时器
            _timer.Interval = AppConfig.GameTrigger == 0 ? 250 : Math.Max(250, AppConfig.GameIntervalMs);
            _timer.Start();
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
            if (_paused || _overlayClosed) return;
            if (!AppConfig.GameOn) return;
            if (_busy)
            {
                if (!force) return;
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

            // 先收起遮罩，避免 OCR 把上一轮译文当成游戏原文；没变化再恢复
            try { TransOverlay.HideOverlay(); } catch (Exception) { }
            Thread.Sleep(24);
            Bitmap bmp = Capture(union);
            if (bmp == null) return;
            ulong hash = ShotCache.Hash(bmp);
            bool changed = _lastHash == 0 || hash != _lastHash;
            _lastHash = hash;
            if (!force && AppConfig.GameTrigger == 0 && !changed)
            {
                bmp.Dispose();
                try { TransOverlay.Restore(); } catch (Exception) { }
                return;
            }

            try
            {
                string log;
                List<Rectangle> obstacles;
                List<OcrSegment> segs = AppConfig.OcrFusion
                    ? Ocr.LayoutSegmentsFused(bmp, out obstacles, out log)
                    : Ocr.LayoutSegments(bmp, out obstacles, out log);
                if (segs == null) segs = new List<OcrSegment>();

                if (AppConfig.GameFilter)
                    segs = GameFilter.KeepDialogue(segs, union, regions);
                if (AppConfig.GameMerge)
                    segs = GameFilter.MergeLines(segs, union, regions);

                if (gen != _gen) return;
                if (segs.Count == 0)
                {
                    JadeHost.Log("[game] no dialogue");
                    return;
                }
                int lines = JadeHost.GameTranslate(bmp, union, segs);
                if (_overlayClosed) { try { TransOverlay.HideOverlay(); } catch (Exception) { } }
                JadeHost.Log("[game] shown=" + lines + " blocks=" + segs.Count
                    + " trigger=" + AppConfig.GameTrigger + " force=" + (force ? 1 : 0));
            }
            finally
            {
                bmp.Dispose();
            }
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
        private static readonly string[] HudWords = new string[]
        {
            "HP", "MP", "SP", "EXP", "FPS", "LV", "LV.", "WASD", "ESC", "TAB",
            "SHIFT", "CTRL", "ALT", "ENTER", "SPACE", "GPU", "CPU", "RAM", "VRAM",
            "DX", "API", "DLSS", "FSR", "HUD"
        };

        public static List<OcrSegment> KeepDialogue(List<OcrSegment> segs, Rectangle union, List<Rectangle> regions)
        {
            List<OcrSegment> res = new List<OcrSegment>();
            for (int i = 0; i < segs.Count; i++)
            {
                OcrSegment s = segs[i];
                if (s.Box.Width < 4 || s.Box.Height < 4) continue;
                if (!InAnyRegion(s.Box, union, regions)) continue;
                if (IsHud(s.Text)) continue;
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

        private static bool IsHud(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            string t = text.Trim();
            if (t.Length == 0) return true;
            if (LayoutBoost.PreTranslate(t) != null) return true;
            string compact = Regex.Replace(t, @"\s+", "");
            if (compact.Length <= 8 && Regex.IsMatch(compact, @"^[vV]?\d+(\.\d+){1,3}$")) return true;
            if (Regex.IsMatch(compact, @"^\d+([/:]\d+)+$")) return true;
            int digits = 0, letters = 0;
            for (int i = 0; i < compact.Length; i++)
            {
                if (compact[i] >= '0' && compact[i] <= '9') digits++;
                else if (char.IsLetter(compact[i])) letters++;
            }
            if (letters == 0 && digits > 0) return true;
            if (compact.Length <= 8 && letters > 0 && digits >= letters) return true;
            string up = compact.ToUpperInvariant();
            for (int i = 0; i < HudWords.Length; i++)
                if (up == HudWords[i] || up == HudWords[i] + ":" || up == HudWords[i] + "：") return true;
            if (compact.Length <= 6 && Regex.IsMatch(up, @"^(HP|MP|SP|EXP|FPS|LV|WASD|ESC|TAB|SHIFT|CTRL|ALT|ENTER|SPACE)[:：]?\d*$")) return true;
            if (t.Length <= 18 && (up.Contains("FPS") || up.Contains("VRAM") || up.Contains("GPU"))) return true;
            return false;
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
}
