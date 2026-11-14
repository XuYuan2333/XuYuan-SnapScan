using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        // 每个选区各自的状态：自己的指纹、自己上一轮识别出的块、自己脏没脏。
        // 多个选区原来是「合成一个大框、整张重新识字」，任一区域字幕动一下都要把所有区域重跑一遍；
        // 拆开之后只有真的变了的区域才走 OCR，没变的直接沿用上一轮结果。
        private sealed class RegionSlot
        {
            public Rectangle R;
            public byte[] Sig;
            public bool Dirty;
            public bool HasSegs;
            public List<OcrSegment> Segs;
            public int Jp;   // 0 还没判定、1 判定是日文（之后固定走日文模型）、2 判定不是日文
            public int Zid;  // 这块专属的遮罩窗口编号（跟着块走，块顺序变了也不会串）
            public int Lines;      // 这块上一轮画上去几行译文
            public string LastText; // 这块上一轮的原文，用来跳过重复翻译
        }
        private static List<RegionSlot> _slots = new List<RegionSlot>();
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
            }            string profile = AppConfig.NormalizeMtProfile(AppConfig.GameModel);
            // 在线翻译接管时不需要本地模型，跳过所有“模型没装”的拦截
            bool online = AppConfig.AliMt || AppConfig.AiMt;
            if (!online && profile == AppConfig.MtProfileEfficiency && !MtRunner.GameReadyFor("efficiency"))
            {
                err = "效率档需要先放入 PLaMo 3 Translate Q4_K_M";
                LastError = err;
                return false;
            }
            if (!online && profile == AppConfig.MtProfileTurbo && !MtRunner.GameReadyFor("turbo"))
            {
                err = "极速档需要先放入 PLaMo 3 Translate Q2_K";
                LastError = err;
                return false;
            }
            if (!online && profile == AppConfig.MtProfileStandard && !MtRunner.Ready)
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
            _maskClosing = false;
            StopMaskWatch();
            try { AppConfig.Save(); } catch (Exception) { }
            // 游戏模式下不碰软件窗口：不隐藏、不压底层、不进任务栏。
            // 选区要是和主窗口重叠，是选区该挪，不该每轮去动窗口（那会一直在屏幕上晃）
            _prevSig = null;
            StartTimer();
            WarmModel(profile);
            _autoLastText = "";
            _autoBaseReady = false;
            StartAuto();   // 自适应模式下开跑高频自动监听
            JadeHost.Log("[game] start regions=" + rs.Count + " profile=" + profile
                + " trigger=" + AppConfig.GameTrigger + " interval=" + AppConfig.GameIntervalMs);
            return true;
        }

        public static void Stop()
        {
            if (_timer != null) { try { _timer.Dispose(); } catch (Exception) { } _timer = null; }
            if (_watch != null) { try { _watch.Dispose(); } catch (Exception) { } _watch = null; }
            StopMaskWatch();
            StopAuto();
            _gen++;
            _busy = false;
            _paused = false;
            _overlayClosed = false;
            _autoLastText = "";
            _autoBaseReady = false;
            AppConfig.GameOn = false;
            LastError = "";
            if (!string.IsNullOrEmpty(_prevProfile)) AppConfig.MtProfile = _prevProfile;
            _prevProfile = null;
            try { AppConfig.Save(); } catch (Exception) { }
            try { TransOverlay.HideOverlay(); } catch (Exception) { }
            // 退出时也不动主窗口（进游戏模式时就没动过，位置/层级保持原样）
            // 关闭游戏模式释放所有本地模型
            try { MtRunner.Invalidate(); } catch (Exception) { }
            try { LlamaMt.Shutdown(); } catch (Exception) { }
            JadeHost.Log("[game] stop, models released");
        }

        public static void Reconfigure()
        {
            if (!AppConfig.GameOn) return;
            _prevSig = null;
            StopMaskWatch();
            StopAuto();
            StartTimer();
            StartAuto();
            JadeHost.Log("[game] reconfigure trigger=" + AppConfig.GameTrigger + " interval=" + AppConfig.GameIntervalMs
                + " auto=" + (AppConfig.GameAutoMode ? 1 : 0));
        }

        public static void Pause(bool paused)
        {
            _paused = paused;
            if (paused)
            {
                if (_timer != null) { try { _timer.Dispose(); } catch (Exception) { } _timer = null; }
                if (_watch != null) { try { _watch.Dispose(); } catch (Exception) { } _watch = null; }
                StopMaskWatch();
                StopAuto();
            }
            else if (AppConfig.GameOn) { StartTimer(); StartAuto(); }
        }

        // 游戏模式下只有「隐藏遮罩」快捷键和遮罩上的关闭按钮能关遮罩
        public static void ToggleOverlay()
        {
            bool showing = false;
            try { showing = TransOverlay.IsShowing; } catch (Exception) { }
            // 遮罩根本没在屏上时（还没翻出字、或者上一轮已经自动收掉），
            // 按翻译键的意图是「我要看译文」，不能当成"关掉"处理，否则按下去什么都没发生。
            if (_overlayClosed || !showing)
            {
                _overlayClosed = false;
                _prevSig = null;
                JadeHost.GameDone("已开启译文", "自动识别继续", true);
                JadeHost.Log("[game] overlay opened by user showing=" + (showing ? 1 : 0));
                Trigger(true);
            }
            else
            {
                _overlayClosed = true;
                _gen++;
                _busy = false;      // 同上：别让这张「已暂停」的牌永远占着 busy
                StopMaskWatch();
                StopAuto();
                try { TransOverlay.HideOverlay(); } catch (Exception) { }
                JadeHost.GameDone("已关闭译文", "再按一次恢复", false);
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
                // 这一轮就算被打断，_busy 也必须立刻放开：否则自适应循环会一直卡在 busy 上，
                // 之后按翻译键只是「开/关遮罩」，再也不会真正跑一轮
                _busy = false;
                StopMaskWatch();
                StopAuto();
                try { TransOverlay.HideOverlay(); } catch (Exception) { }
            }
            else { _overlayClosed = false; StartAuto(); }
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

        // ---- 遮罩期采样：只在遮罩显示期间跑，判断字幕是否被替换/消失，自动把遮罩收掉。
        // 不主动出新译文，也不常驻；遮罩一关定时器就释放。
        private static System.Threading.Timer _maskWatch;
        private static ulong _maskBase;
        private static byte[] _maskBaseGray;
        private static List<ulong> _zoneBase = new List<ulong>();
        private static long _stableAt;
        private static bool _stable;
        private static bool _ocrDone;    // 本轮稳定期是否已经判过，画面一变就重新开放
        private static long _lastOcrAt;
        private static bool _maskClosing;

        // ---- 自适应模式：遮罩不在屏上时高频采样，画面定住够久才识字，识到字才翻译。
        // 遮罩一上屏就停掉这条高频循环，交给遮罩期的低频比对，避免 40ms 一次把遮罩掐来掐去。
        private static System.Threading.Timer _autoTimer;
        private static bool _autoBusy;
        private static byte[] _autoLastGray;      // 上一帧灰度，用来判断画面还在不在动
        private static int _autoStableAt;         // 画面最后一次变动的时刻（TickCount）
        private static ulong _autoLastBase;       // 上次跑过 OCR 的画面指纹
        private static byte[] _autoLastBaseGray;  // 上次跑过 OCR 的 32x24 灰度（判重比 dHash 可靠得多）
        private static bool _autoBaseReady;
        private static string _autoLastText = ""; // 上次翻译过的文本，用来挡住重复翻译
        private static int _autoOcrSkip;          // 图像指纹命中、跳过 OCR 的次数
        private static int _autoTextSkip;         // 文本没变、跳过翻译的次数
        private static int _autoTickCount;
        private static int _autoLastMoved = -1;
        private static int _autoNextLogAt;

        // 自适应循环卡住时要能看出来是被哪一条挡的，3 秒最多记一条
        private static void AutoNote(string why)
        {
            int now = Environment.TickCount;
            if (unchecked(now - _autoNextLogAt) < 0) return;
            _autoNextLogAt = now + 3000;
            JadeHost.Log("[game] auto wait " + why);
        }

        // 遮罩期窗口守护：只查句柄和窗口矩形，不抓图、不隐藏，所以完全不闪。
        // 选区底下换成了别的窗口、游戏窗口被移走/最小化/缩放，立刻把遮罩收掉。
        private static IntPtr _maskHwnd;
        private static Rectangle _maskWndRect;
        private static int _maskTickCount;
        private static int _maskGrabEvery = 3;
        private static bool _maskUseWin;     // 本轮采样走的是「抓底层」还是「外围一圈」
        private static bool _maskSrcLocked;  // 首帧定下来源后整轮不再变，避免两种来源互相比对
        private static int _maskBlank;       // 连续拿到空帧的次数（独占全屏会这样）

        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point p);

        // Chromium / 游戏常是子窗口承接点击，子窗口的 PrintWindow 只给静态帧，
        // 比对就永远是 0（字幕换了也看不出来）。统一往上回到根窗口
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        private const uint GA_ROOT = 2;

        private static IntPtr RootWindow(IntPtr h)
        {
            if (h == IntPtr.Zero) return h;
            try
            {
                IntPtr r = GetAncestor(h, GA_ROOT);
                return r == IntPtr.Zero ? h : r;
            }
            catch (Exception) { return h; }
        }
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        // 遮罩一上屏，主窗口被 send-bottom 挪过来、工具条也在屏幕上，
        // WindowFromPoint 经常先命中自己程序的窗口——那不是「对话窗口切走」
        private static bool IsOwnWindow(IntPtr h)
        {
            if (h == IntPtr.Zero) return true;
            try
            {
                uint pid = 0;
                GetWindowThreadProcessId(h, out pid);
                return pid != 0 && (int)pid == System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch (Exception) { return false; }
        }
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr h, out RECT r);
        // 让窗口把自己画到内存 DC：拿的是窗口自己的内容，不带压在上面的遮罩
        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);
        private const uint PW_RENDERFULLCONTENT = 2;

        private static Rectangle WndRect(IntPtr h)
        {
            RECT r;
            if (h == IntPtr.Zero || !GetWindowRect(h, out r)) return Rectangle.Empty;
            return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }


        private static void StartMaskWatch()
        {
            StopMaskWatch();
            if (!AppConfig.GameOn || !AppConfig.GameAutoClose) return;
            if (_paused || _overlayClosed) return;
            // 注意：遮罩期不要停掉自适应循环——它只读几个属性就 return，几乎不耗 CPU，
            // 常驻反而避免了「收遮罩后重启不及时」的时序坑
            List<Rectangle> rs = Regions();
            if (rs.Count == 0) return;
            Rectangle union = Union(rs);
            if (union.Width < 16 || union.Height < 12) return;

            _stable = false;
            _stableAt = 0;
            _ocrDone = false;
            _lastOcrAt = 0;
            _maskBase = 0UL;
            _maskBaseGray = null;
            _maskTickCount = 0;

            // 记下这轮遮罩贴着的是哪个窗口、它的矩形；后面只查这个，不再靠像素。
            // 取点要跳过自己程序的窗口（主窗口被 send-bottom 挪过来时 WindowFromPoint 会先命中它）
            _maskHwnd = IntPtr.Zero;
            {
                List<Rectangle> cand = Regions();
                if (cand.Count == 0) cand.Add(union);
                // 多数投票：每个选区中心各取一次窗口，出现最多的那个才算「对话窗口」。
                // 只用第一个命中的话，选区之间那块空白/别的浮窗会把它带偏，
                // 后面每轮都判「窗口已切走」，遮罩就一秒收掉
                Dictionary<IntPtr, int> cnt = new Dictionary<IntPtr, int>();
                IntPtr best = IntPtr.Zero;
                int bestN = 0;
                for (int i = 0; i < cand.Count; i++)
                {
                    Point c = new Point(cand[i].Left + cand[i].Width / 2, cand[i].Top + cand[i].Height / 2);
                    IntPtr h = RootWindow(WindowFromPoint(c));
                    if (h == IntPtr.Zero || IsOwnWindow(h)) continue;
                    int n = cnt.ContainsKey(h) ? cnt[h] + 1 : 1;
                    cnt[h] = n;
                    if (n > bestN) { bestN = n; best = h; }
                }
                _maskHwnd = best;
            }
            _maskWndRect = WndRect(_maskHwnd);

            // 基准图优先直接问窗口自己（PrintWindow）：拿到的是窗口原始内容，
            // 遮罩压在字幕上也不影响；拿不到再退回「外围一圈」的保守比对
            bool srcWin;
            Bitmap b = CaptureSample(union, out srcWin);
            if (b != null)
            {
                try { byte[] g; _maskBase = MaskHash(b, out g); _maskBaseGray = g; }
                catch (Exception) { }
                finally { b.Dispose(); }
            }
            _maskUseWin = srcWin;
            _maskSrcLocked = b != null;

            // tick 只做窗口守护（纯 API，无抓图）；真正的像素比对低频跑，避免看出闪。
            // 自适应模式下字幕换得快，这里默认收到 800ms 一次
            int period = 500;
            int want = AppConfig.GameAutoMode ? AppConfig.GameAutoWatchMs : AppConfig.GameWatchMs;
            _maskGrabEvery = Math.Max(1, (want + period - 1) / period);
            _maskWatch = new System.Threading.Timer(MaskTick, null, period, period);
            JadeHost.Log("[game] maskwatch start period=" + period + " grabEvery=" + _maskGrabEvery
                + " src=" + (_maskUseWin ? "under" : "band")
                + " hwnd=" + _maskHwnd + " base=" + _maskBase.ToString("x16"));
        }

        private static void StopMaskWatch()
        {
            if (_maskWatch != null)
            {
                try { _maskWatch.Dispose(); } catch (Exception) { }
                _maskWatch = null;
            }
        }

        // ---- 自适应模式：没有遮罩时高频采样（默认 40ms），画面定住 400ms 且与上次识字的画面不同才动 OCR。
        // 遮罩一上屏就停掉这条循环交给遮罩期比对；遮罩收掉后自动恢复监听。
        public static void StartAuto()
        {
            StartAutoCore(false);
        }

        // 遮罩自己收掉（字幕变了）之后的续跑。手动模式（trigger=2）下不跑定时器、
        // GameAutoMode 又是关的，这时 StartAuto 会第一行就 return，驱动链直接断掉——
        // 表现就是「第一次遮罩出现了，第二次再也不出现」。所以这条路径必须绕过 GameAutoMode。
        public static void StartAutoResume()
        {
            StartAutoCore(true);
        }

        private static void StartAutoCore(bool resume)
        {
            try
            {
                if (_autoTimer != null) return;   // 已经在跑就别重建，重建会把图像/文本缓存一起清掉
                if (!AppConfig.GameOn) return;
                if (!resume && !AppConfig.GameAutoMode)
                {
                    JadeHost.Log("[game] auto skip gamemode=0");
                    return;
                }
                if (_paused) { JadeHost.Log("[game] auto skip paused=1"); return; }
                if (_overlayClosed) { JadeHost.Log("[game] auto skip closed=1"); return; }
                if (Regions().Count == 0) { JadeHost.Log("[game] auto skip no region"); return; }
                // 遮罩可能还在收（UI 线程异步），这里不拦：AutoTick 每帧自己会看 IsShowing，
                // 遮罩一收就接着跑，免得被一次时序差把监听永久停掉

                int ms = AppConfig.GameAutoSampleMs;
                if (ms < 20) ms = 20;
                if (ms > 500) ms = 500;
                _autoLastGray = null;
                _autoLastBaseGray = null;
                _autoStableAt = Environment.TickCount;
                _autoBaseReady = false;
                _autoOcrSkip = 0;
                _autoTextSkip = 0;
                _autoTimer = new System.Threading.Timer(AutoTick, null, ms, ms);
                JadeHost.Log("[game] auto watch start sample=" + ms + "ms hold=" + AppConfig.GameAutoHoldMs
                    + "ms watch=" + AppConfig.GameAutoWatchMs + "ms");
            }
            catch (Exception ex) { JadeHost.Log("[game] auto start fail " + ex.Message); }
        }

        public static void StopAuto()
        {
            if (_autoTimer != null)
            {
                try { _autoTimer.Dispose(); } catch (Exception) { }
                _autoTimer = null;
            }
        }

        private static void AutoTick(object state)
        {
            if (!AppConfig.GameOn || _paused || _overlayClosed) { StopAuto(); return; }
            if (TransOverlay.IsShowing) return;      // 遮罩在屏上，这段交给 MaskTick 的低频比对（正常态，不记日志）
            if (_busy) return;
            if (_maskClosing) { AutoNote("closing"); return; }
            if (_autoBusy) return;
            List<Rectangle> rs = Regions();
            if (rs.Count == 0) { StopAuto(); return; }
            Rectangle union = Union(rs);
            if (union.Width < 16 || union.Height < 12) return;
            _autoBusy = true;
            bool handed = false;
            try
            {
                _autoTickCount++;
                if (_autoTickCount % 125 == 0)
                {
                    JadeHost.Log("[game] auto alive ticks=" + _autoTickCount + " moved=" + _autoLastMoved
                        + " held=" + unchecked(Environment.TickCount - _autoStableAt) + "ms ocrSkip=" + _autoOcrSkip
                        + " textSkip=" + _autoTextSkip);
                }
                // 没有遮罩，直接抓，不用隐藏/恢复——这一路完全不闪
                Bitmap b = Capture(union);
                if (b == null) { AutoNote("nocap"); return; }
                byte[] gray;
                ulong h;
                bool content;
                try { content = HasContent(b); h = MaskHash(b, out gray); }
                finally { b.Dispose(); }
                if (!content) { AutoNote("blank"); return; }   // 纯色/黑屏（过场、独占全屏）：不判也不再费一次 OCR

                // ① 画面还在动就重置稳定计时——绝不每帧跑 OCR
                int moved = _autoLastGray == null ? 999 : GrayDiff(gray, _autoLastGray);
                _autoLastGray = gray;
                _autoLastMoved = moved;
                if (moved > 6) { _autoStableAt = Environment.TickCount; AutoNote("moving=" + moved); return; }

                int hold = AppConfig.GameAutoHoldMs;
                if (hold < 100) hold = 100;
                int held = unchecked(Environment.TickCount - _autoStableAt);
                if (held < hold) { AutoNote("settling " + held + "ms"); return; }   // 还没定住够久

                // ② 图像层缓存：这一帧和上次识字的那帧几乎一样才跳过。
                // 只用 64 位 dHash 太粗——同一位置换一句话 dHash 可能一模一样，
                // 会永久跳过 OCR 导致「字幕换了却不再翻译」，所以必须比 32x24 灰度
                if (_autoBaseReady && _autoLastBaseGray != null && GrayDiff(gray, _autoLastBaseGray) <= 3)
                { _autoOcrSkip++; AutoNote("sameimg"); return; }
                _autoLastBase = h;
                _autoLastBaseGray = gray;
                _autoBaseReady = true;
                JadeHost.Log("[game] auto ocr trigger held=" + held + "ms moved=" + moved);
                // OCR + 翻译要一两秒，绝不能占着线程池线程跑——里面还要用线程池，
                // 占满会互相等死。扔到独立线程，这一轮抓图比对立刻收工
                System.Threading.Thread work = new System.Threading.Thread(delegate()
                {
                    try { Trigger(false); }
                    catch (Exception) { }
                    finally { _autoBusy = false; }
                });
                work.IsBackground = true;
                work.Start();
                handed = true;
                return;   // _autoBusy 交给工作线程收尾
            }
            catch (Exception) { }
            finally
            {
                if (!handed) _autoBusy = false;
            }
        }

        // 文本指纹：同样的几句就不重复调翻译接口。
        // 必须做归一化——OCR 同一句两次常出现空格、标点、大小写抖动，
        // 用原样字符串比会全部判成「新文本」，缓存等于没有
        private static string TextKey(List<OcrSegment> segs)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < segs.Count; i++)
            {
                string t = segs[i].Text;
                if (t == null) { sb.Append('\u0001'); continue; }
                for (int j = 0; j < t.Length; j++)
                {
                    char c = t[j];
                    if (char.IsWhiteSpace(c)) continue;                       // 空格、换行、制表
                    if (c == '.' || c == ',' || c == '!' || c == '?' || c == ';' || c == ':' || c == '"'
                        || c == '\'' || c == '-' || c == '—' || c == '…'
                        || c == '。' || c == '，' || c == '、' || c == '！' || c == '？'
                        || c == '；' || c == '：' || c == '「' || c == '」' || c == '『' || c == '』'
                        || c == '(' || c == ')' || c == '（' || c == '）') continue;
                    sb.Append(char.ToLowerInvariant(c));
                }
                sb.Append('\u0001');
            }
            return sb.ToString();
        }

        private static void MaskTick(object state)
        {
            if (!AppConfig.GameOn || _paused || _overlayClosed) { StopMaskWatch(); return; }
            if (!TransOverlay.IsShowing) { StopMaskWatch(); return; }
            if (_maskClosing) return;
            List<Rectangle> rs = Regions();
            if (rs.Count == 0) { StopMaskWatch(); return; }
            Rectangle union = Union(rs);
            if (union.Width < 16 || union.Height < 12) return;

            // ① 窗口守护：每 0.5s 只查句柄和窗口矩形，不抓图，所以完全不闪。
            // 选区底下换成别的窗口（游戏被切走/最小化）、游戏窗口被移走或缩放 → 直接收掉
            string why;
            if (WindowGone(union, out why)) { CloseMask(why); return; }

            // ② 外围比对：低频（默认 1.5s 一次），抓图不隐藏遮罩，屏幕不闪
            _maskTickCount++;
            if (_maskGrabEvery > 1 && (_maskTickCount % _maskGrabEvery) != 0) return;

            bool fromWin;
            Bitmap b = CaptureSample(union, out fromWin);
            if (b == null) return;
            // 来源必须跟首帧一致，否则「窗口自绘」和「外围一圈」互相比，差异永远很大，会误关
            if (_maskSrcLocked && fromWin != _maskUseWin) { b.Dispose(); return; }
            if (!_maskSrcLocked) { _maskUseWin = fromWin; _maskSrcLocked = true; }
            // 独占全屏的游戏 GDI 抓不到内容，会返回一片纯色；这种帧不能拿来比，否则要么误关要么永远不关
            if (_maskUseWin && !HasContent(b))
            {
                b.Dispose();
                _maskBlank++;
                if (_maskBlank <= 3)
                    JadeHost.Log("[game] sample blank " + _maskBlank + " (独占全屏抓不到底图，自动关闭会失效)");
                return;
            }
            _maskBlank = 0;
            try
            {
                byte[] gray;
                ulong h = MaskHash(b, out gray);
                if (_maskBaseGray == null)
                {
                    _maskBaseGray = gray;
                    _maskBase = h;
                    _zoneBase = ZoneHashes(b, union, rs);
                    return;
                }
                if (_zoneBase.Count != rs.Count) _zoneBase = ZoneHashes(b, union, rs);

                int dist = Hamming(h, _maskBase);
                int diff = GrayDiff(gray, _maskBaseGray);
                int ratio = ChangedRatio(gray, _maskBaseGray, 12);
                int chan = ChangedCount(gray, _maskBaseGray, 12);
                int block = MaxBlockDiff(gray, _maskBaseGray, 4, 2);
                int row = MaxRowDiff(gray, _maskBaseGray);

                // 六条判据命中任一即收。实测游戏里字幕区背景一直在动（水波/粒子），
                // 原来那套（chan>=10 / block>=20 / row>=15）动不动就判「字幕没了」，
                // 遮罩 3 秒就被收掉，看着就是「又不见了」。这里整体放宽一档：
                // 只有整行字幕真的消失/被替换（大面积变黑或整行改字）才收，轻微背景动画留着。
                JadeHost.Log("[game] watch dist=" + dist + " diff=" + diff + " ratio=" + ratio + " chan=" + chan
                    + " block=" + block + " row=" + row + " src=" + (_maskUseWin ? "under" : "band"));

                // 按区比对：整块并集判不出来（字幕被稀释），单区一变就把遮罩收掉重开一轮
                for (int i = 0; i < rs.Count && i < _zoneBase.Count; i++)
                {
                    byte[] zg = null;
                    ulong zh = MaskHashOf(b, union, rs[i], out zg);
                    if (zg == null) continue;
                    int zd = Hamming(zh, _zoneBase[i]);
                    if (zd >= 6)
                    {
                        JadeHost.Log("[game] zone " + (i + 1) + " changed dist=" + zd);
                        CloseMask("选区 " + (i + 1) + " 内容已变");
                        return;
                    }
                }

                if (dist >= 18 || diff >= 22 || ratio >= 16 || chan >= 75 || block >= 100 || row >= 100)
                {
                    JadeHost.Log("[game] mask auto-close dist=" + dist + " diff=" + diff + " ratio=" + ratio + " chan=" + chan
                        + " block=" + block + " row=" + row + " src=" + (_maskUseWin ? "under" : "band"));
                    CloseMask("画面已变");
                    return;
                }
            }
            catch (Exception) { }
            finally { b.Dispose(); }
        }

        // 抓「选区外扩一圈」的画面：遮罩盖住的那块涂成中性灰，比对只看外围，所以不用隐藏遮罩
        private static Bitmap CaptureBand(Rectangle union)
        {
            const int pad = 90;
            Rectangle outer = Rectangle.Inflate(union, pad, pad);
            outer.Intersect(SystemInformation.VirtualScreen);
            if (outer.Width < 24 || outer.Height < 16) return null;
            Bitmap b = Capture(outer);
            if (b == null) return null;
            try
            {
                Rectangle r = new Rectangle(union.Left - outer.Left, union.Top - outer.Top, union.Width, union.Height);
                r.Intersect(new Rectangle(0, 0, b.Width, b.Height));
                if (r.Width > 0 && r.Height > 0)
                {
                    using (Graphics g = Graphics.FromImage(b))
                        g.FillRectangle(Brushes.Gray, r);
                }
            }
            catch (Exception) { }
            return b;
        }

        // 采样图：优先直接问目标窗口（PrintWindow）——遮罩压在上面也不影响，且不闪；
        // 拿不到才掐掉遮罩抓底层。掐遮罩那条路有个致命毛病：DWM 合成没提交完就抓，
        // 会一直返回「遮罩还在、字幕还是旧的那帧」，于是 dist 恒 0、遮罩永远不收
        private static Bitmap CaptureSample(Rectangle union, out bool fromWin)
        {
            fromWin = false;
            if (_maskHwnd != IntPtr.Zero)
            {
                Bitmap w = CaptureWindowPart(_maskHwnd, union);
                if (w != null) { fromWin = true; return w; }
            }
            Bitmap b = CaptureUnderOverlay(union);
            if (b != null) { fromWin = true; return b; }
            return CaptureBand(union);
        }

        private static Bitmap CaptureWindowPart(IntPtr hwnd, Rectangle union)
        {
            if (hwnd == IntPtr.Zero) return null;
            Rectangle wr = WndRect(hwnd);
            if (wr.Width < 40 || wr.Height < 40 || wr.Width > 8000 || wr.Height > 8000) return null;
            Bitmap full = null;
            try
            {
                full = new Bitmap(wr.Width, wr.Height, PixelFormat.Format24bppRgb);
                // 原来走 PrintWindow：Chromium / GPU 直出窗口只回一帧缓存，
                // 字幕换了它还是老样子，比对下来 dist 永远 0，遮罩判据整个失灵。
                // 改抓屏幕——分层遮罩是 UpdateLayeredWindow 画的、不进 DWM 合成，
                // 屏幕抓到的正是遮罩下面那一层
                using (Graphics g = Graphics.FromImage(full))
                {
                    g.CopyFromScreen(wr.Left, wr.Top, 0, 0,
                        new Size(wr.Width, wr.Height), CopyPixelOperation.SourceCopy);
                }
                Rectangle cut = new Rectangle(union.Left - wr.Left, union.Top - wr.Top, union.Width, union.Height);
                cut.Intersect(new Rectangle(0, 0, full.Width, full.Height));
                if (cut.Width < 16 || cut.Height < 12) return null;
                Bitmap part = full.Clone(cut, PixelFormat.Format24bppRgb);
                if (part == null) return null;
                if (HasContent(part)) return part;
                part.Dispose();
                return null;
            }
            catch (Exception) { return null; }
            finally { if (full != null) { try { full.Dispose(); } catch (Exception) { } } }
        }

        // PrintWindow 对独占全屏 / GPU 直出窗口常返回纯色，先判有没有内容再用
        private static bool HasContent(Bitmap b)
        {
            int min = 255, max = 0;
            int stepX = Math.Max(1, b.Width / 16), stepY = Math.Max(1, b.Height / 8);
            for (int y = 0; y < b.Height; y += stepY)
                for (int x = 0; x < b.Width; x += stepX)
                {
                    Color c = b.GetPixel(x, y);
                    int v = (c.R * 77 + c.G * 151 + c.B * 28) >> 8;
                    if (v < min) min = v;
                    if (v > max) max = v;
                    if (max - min > 10) return true;
                }
            return false;
        }

        // 空字幕兜底：连续稳定状态下 OCR 一次，认不出有效字符才收遮罩
        private static void MaskOcrCheck(Rectangle union)
        {
            try
            {
                List<Rectangle> rs = Regions();
                Bitmap b = Capture(union);
                if (b == null) return;
                string log = "";
                List<Rectangle> obstacles = null;
                List<OcrSegment> segs = null;
                try { segs = Ocr.LayoutSegments(b, out obstacles, out log); }
                catch (Exception ex) { JadeHost.Log("[game] mask ocr fail " + ex.Message); }
                finally { b.Dispose(); }

                if (_maskClosing || !TransOverlay.IsShowing) { StopMaskWatch(); return; }
                if (segs == null) return;   // 识别本身失败 → 不判空，免得误收

                segs = GameFilter.KeepDialogue(segs, union, rs);
                int chars = 0;
                for (int i = 0; i < segs.Count; i++)
                {
                    string t = segs[i].Text;
                    if (t == null) continue;
                    for (int j = 0; j < t.Length; j++)
                    {
                        if (char.IsLetterOrDigit(t[j])) chars++;
                    }
                }
                JadeHost.Log("[game] mask ocr chars=" + chars + " segs=" + segs.Count);
                if (chars < 2) CloseMask("字幕已消失");
            }
            catch (Exception) { }
        }

        private static void CloseMask(string why)
        {
            _maskClosing = true;
            StopMaskWatch();
            _prevSig = null;
            _stable = false;
            _stableAt = 0;
            _ocrDone = false;
            // 直接收，别绕 UI 线程队列：主窗口隐藏时 RunOnUi 会把这次隐藏压到后面，
            // 自适应循环看到 IsShowing 一直是 true 就会一直等下去
            try { TransOverlay.HideOverlay(); }
            catch (Exception)
            {
                try { JadeHost.RunOnUi(delegate { try { TransOverlay.HideOverlay(); } catch (Exception) { } }); }
                catch (Exception) { }
            }
            JadeHost.Log("[game] mask auto-close reason=" + why);
            // 复位放后台，避免同一帧里重复触发（自适应循环常驻，不用在这里重启）
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                Thread.Sleep(400);
                _maskClosing = false;
                // 遮罩收掉本身不改画面像素，自适应循环那句「和上次识字的那帧一样就跳过」
                // 会一直命中 → 再也不 OCR、遮罩再也回不来。这里把基准清掉，
                // 保证收掉遮罩之后一定重新识一次字。
                _autoBaseReady = false;
                _autoLastBaseGray = null;
                _autoStableAt = Environment.TickCount;
                StartAutoResume();   // 手动模式放弃了自适应循环，这里必须绕过 GameAutoMode 把下一轮接上
            });
            t.IsBackground = true;
            t.Start();
        }

        // 窗口守护：对话窗口被切走 / 移走 / 缩小，遮罩就该收（避免遮罩浮在别处）
        // 采样点按每个选区各取中心，任一选区还能看到原窗口就不算切走——
        // 多选区跨屏时 union 中心常落在选区之间的空白上，单点判定必然误报
        private static bool WindowGone(Rectangle union, out string why)
        {
            why = null;
            List<Rectangle> rs = Regions();
            if (rs.Count == 0) rs.Add(union);
            int same = 0, checkedN = 0, own = 0;
            for (int i = 0; i < rs.Count; i++)
            {
                Point c = new Point(rs[i].Left + rs[i].Width / 2, rs[i].Top + rs[i].Height / 2);
                IntPtr h = RootWindow(WindowFromPoint(c));
                if (h == IntPtr.Zero) continue;
                if (IsOwnWindow(h)) { own++; continue; }
                checkedN++;
                if (_maskHwnd == IntPtr.Zero || h == _maskHwnd) same++;
            }
            // 全被自己窗口挡住：说明不了问题，交给像素比对，别在这里判死
            if (checkedN == 0) return false;
            // 改成多数否决：只要有一个取样点还在原窗口上，就认为没切走。
            // 原来 same==0 就判死，选区之间那块空白命中别的浮窗时会误报，遮罩一秒就收
            if (_maskHwnd != IntPtr.Zero && same == 0) { why = "对话窗口已切走"; return true; }
            Rectangle r = WndRect(_maskHwnd);
            if (!_maskWndRect.IsEmpty && !r.IsEmpty && r != _maskWndRect) { why = "对话窗口已移动"; return true; }
            return false;
        }

        // 采样格：9x8 太粗——游戏字幕字号小，换一句话可能只动 1~2 个格子，根本判不出来。
        // 提到 32x24 后，同一位置换句能稳稳地反映成一片格子变灰
        private const int GW = 32, GH = 24;

        // 一帧只做一次缩放 + 一次 LockBits：32x24 灰度既算平均差/变化占比，也生成 dHash
        // 整块并集算出来的 hash 会把字幕稀释掉：1548x839 缩到 GWxGH 之后，
        // 1481x191 那条字幕只剩两三行采样点，一句换成另一句 Hamming 还是 0，
        // MaskTick 于是永远判「画面没变」，遮罩挂着不更新、译文一直停在第一句。
        private static ulong MaskHashOf(Bitmap bmp, Rectangle union, Rectangle zone, out byte[] gray)
        {
            gray = null;
            if (bmp == null) return 0UL;
            int sx = zone.Left - union.Left, sy = zone.Top - union.Top;
            int sw = Math.Min(zone.Width, bmp.Width - sx);
            int sh = Math.Min(zone.Height, bmp.Height - sy);
            if (sw < 12 || sh < 8) return 0UL;
            Bitmap part = null;
            try
            {
                part = new Bitmap(sw, sh, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(part))
                {
                    g.DrawImage(bmp, new Rectangle(0, 0, sw, sh),
                        new Rectangle(sx, sy, sw, sh), GraphicsUnit.Pixel);
                }
                return MaskHash(part, out gray);
            }
            catch (Exception) { gray = null; return 0UL; }
            finally { if (part != null) { try { part.Dispose(); } catch (Exception) { } } }
        }

        private static List<ulong> ZoneHashes(Bitmap bmp, Rectangle union, List<Rectangle> rs)
        {
            List<ulong> res = new List<ulong>();
            for (int i = 0; i < rs.Count; i++)
            {
                byte[] g = null;
                ulong h = MaskHashOf(bmp, union, rs[i], out g);
                res.Add(g == null ? 0UL : h);
            }
            return res;
        }

        private static ulong MaskHash(Bitmap bmp, out byte[] gray)
        {
            gray = new byte[GW * GH];
            if (bmp == null) return 0UL;
            try
            {
                using (Bitmap small = new Bitmap(GW, GH, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(bmp, new Rectangle(0, 0, GW, GH));
                    }
                    BitmapData bd = small.LockBits(new Rectangle(0, 0, GW, GH), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    try
                    {
                        int stride = bd.Stride;
                        byte[] row = new byte[Math.Abs(stride)];
                        for (int y = 0; y < GH; y++)
                        {
                            IntPtr p = new IntPtr(bd.Scan0.ToInt64() + (long)y * stride);
                            Marshal.Copy(p, row, 0, row.Length);
                            for (int x = 0; x < GW; x++)
                            {
                                int o = x * 3;
                                gray[y * GW + x] = (byte)((row[o + 2] * 299 + row[o + 1] * 587 + row[o] * 114) / 1000);
                            }
                        }
                    }
                    finally { small.UnlockBits(bd); }

                    ulong h = 0UL;
                    int bit = 0;
                    for (int y = 0; y < 8; y++)
                    {
                        int yy = y * GH / 8;
                        for (int x = 0; x < 8; x++)
                        {
                            int x1 = x * GW / 9;
                            int x2 = (x + 1) * GW / 9;
                            if (gray[yy * GW + x1] > gray[yy * GW + x2]) h |= (1UL << bit);
                            bit++;
                        }
                    }
                    return h;
                }
            }
            catch (Exception) { return 0UL; }
        }

        // 变化占比（百分比）：差值超过 tol 的格子占多少。
        // 字幕换一句，这块会跳到 15% 以上；背景轻微动、字幕没变时基本是 0~3%
        private static int ChangedRatio(byte[] a, byte[] b, int tol)
        {
            if (a == null || b == null || a.Length != b.Length) return 100;
            int n = ChangedCount(a, b, tol);
            return n * 100 / a.Length;
        }

        // 变化格子的绝对个数。框选范围一大，占比就被摊薄，只能用绝对数
        private static int ChangedCount(byte[] a, byte[] b, int tol)
        {
            if (a == null || b == null || a.Length != b.Length) return 1000;
            int n = 0;
            for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > tol) n++;
            return n;
        }

        // 分块最大平均差：把 32x24 切成 bw*bh 的小块，返回「动得最厉害的那块」的平均灰度差。
        // 小字号字幕只占区域一小条，全局平均会被背景摊薄，这一条才抓得住
        private static int MaxBlockDiff(byte[] a, byte[] b, int bw, int bh)
        {
            if (a == null || b == null || a.Length != b.Length) return 255;
            int best = 0;
            for (int by = 0; by + bh <= GH; by += bh)
            {
                for (int bx = 0; bx + bw <= GW; bx += bw)
                {
                    int sum = 0, n = 0;
                    for (int y = by; y < by + bh; y++)
                        for (int x = bx; x < bx + bw; x++) { sum += Math.Abs(a[y * GW + x] - b[y * GW + x]); n++; }
                    int avg = n > 0 ? sum / n : 0;
                    if (avg > best) best = avg;
                }
            }
            return best;
        }

        // 逐行最大平均差：选区框得很大时（比如框住整个对话框），字幕只占其中几行，
        // 分块平均会被整行空白摊薄；单看「变化最大的那一行」就不会漏
        private static int MaxRowDiff(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 255;
            int best = 0;
            for (int y = 0; y < GH; y++)
            {
                int sum = 0;
                for (int x = 0; x < GW; x++) sum += Math.Abs(a[y * GW + x] - b[y * GW + x]);
                int avg = sum / GW;
                if (avg > best) best = avg;
            }
            return best;
        }

        private static int Hamming(ulong a, ulong b)
        {
            ulong x = a ^ b;
            int n = 0;
            while (x != 0) { x &= x - 1; n++; }
            return n;
        }

        private static int GrayDiff(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 255;
            int sum = 0;
            for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
            return sum / a.Length;
        }

        private static void WarmModel(string profile)
        {
            if (AppConfig.AliMt || AppConfig.AiMt) return;   // 在线翻译不预热本地模型
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
            if (_paused) return;
            // 手动按翻译键就是「我要看译文」：用户之前按隐藏键关掉的遮罩，这里直接恢复
            if (force && _overlayClosed)
            {
                _overlayClosed = false;
                JadeHost.Log("[game] overlay reopened by hotkey");
            }
            if (_overlayClosed)
            {
                JadeHost.Log("[game] skip paused=" + (_paused ? 1 : 0) + " closed=1");
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
            // 手动重开一轮：先把上一轮的遮罩整块拆掉，再停掉上一轮的采样，
            // 别让旧译文留在屏上，也别让旧 timer 把这轮新遮罩误收
            if (force)
            {
                StopMaskWatch();
                StopAuto();
                _maskClosing = false;
                try { TransOverlay.HideOverlay(); } catch (Exception) { }
            }
            _busy = true;
            JadeHost.Log("[game] tick enter gen=" + g + " force=" + (force ? 1 : 0));
            try { DoTick(g, force); }
            catch (Exception ex) { JadeHost.Log("[game] tick fail " + ex.Message); }
            finally
            {
                if (_gen == g) _busy = false;
                JadeHost.Log("[game] tick exit gen=" + g);
            }
        }

        // 「这块译文还能不能原地留着」的唯一判据：窗口真的在屏上。
        // 不在屏上就立刻用上一轮的内容重新铺一次；窗口已经拆掉才返回 false，让调用方重翻。
        private static bool KeepZoneOnScreen(RegionSlot sl, int no)
        {
            try
            {
                if (TransOverlay.IsZoneShowing(sl.Zid)) return true;
                if (TransOverlay.ReshowZone(sl.Zid))
                {
                    JadeHost.Log("[game] zone " + no + " reshow (遮罩已被收掉，沿用上一轮译文)");
                    return true;
                }
                JadeHost.Log("[game] zone " + no + " reshow miss");
            }
            catch (Exception ex) { JadeHost.Log("[game] zone " + no + " reshow fail " + ex.Message); }
            return false;
        }

        private static void DoTick(int gen, bool force)
        {
            // 这一轮重新识字，遮罩期的比对先停掉：否则它会在 OCR 期间反复判「画面已变」并刷屏
            StopMaskWatch();
            List<Rectangle> regions = Regions();
            if (regions.Count == 0) return;
            Rectangle union = Union(regions);
            if (union.Width < 16 || union.Height < 12) return;
            JadeHost.Log("[game] tick ocr stage");
            JadeHost.GameBusy("识字中", "OCR · " + Ocr.EffectiveName());

            // 抓图凡是遮罩已经在屏上，就只「掐一下」再抓（CaptureUnderOverlay），
            // 不再整块撤掉再放回：整块撤/放会让遮罩每轮闪一次，窗口也一动就带出画面变化，
            // 分区「有没有变」的判定会被自己制造的抖动带偏。
            // 主窗口也不在这里动——游戏模式全程保持软件窗口原状。
            bool excluded = false;
            Bitmap bmp = TransOverlay.IsShowing ? CaptureUnderOverlay(union) : Capture(union);
            if (bmp == null) return;
            JadeHost.Log("[game] tick captured");
            if (Environment.GetEnvironmentVariable("QRSNAP_DBG_CROP") == "1")
            {
                try
                {
                    string p = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\qrsnap-union.png";
                    bmp.Save(p, System.Drawing.Imaging.ImageFormat.Png);
                }
                catch (Exception) { }
            }
            byte[] sig = Signature(bmp, union, regions);
            double diff = _prevSig == null ? 1.0 : SignatureDiff(_prevSig, sig);
            double threshold = SensitivityThreshold();
            _prevSig = sig;

            // —— 选区各自判定 ——
            // 每个选区自己算指纹、自己判断变没变；没变的区域后面直接沿用上一轮识别结果。
            SyncSlots(regions);
            int dirtyCount = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                RegionSlot sl = _slots[i];
                byte[] cur = SignatureOne(bmp, union, sl.R);
                double rd = sl.Sig == null ? 1.0 : SignatureDiff(sl.Sig, cur);
                sl.Dirty = force || sl.Sig == null || rd >= threshold;
                sl.Sig = cur;
                if (sl.Dirty) dirtyCount++;
                JadeHost.Log("[game] region " + (i + 1) + "/" + _slots.Count
                    + (sl.Dirty ? " dirty" : " keep") + " diff=" + rd.ToString("0.000"));
            }
            JadeHost.Log("[game] diff=" + diff.ToString("0.000") + " threshold=" + threshold.ToString("0.000")
                + " dirty=" + dirtyCount + "/" + _slots.Count + " excluded=" + (excluded ? 1 : 0));
            if (!force && AppConfig.GameTrigger == 0 && dirtyCount == 0)
            {
                bmp.Dispose();
                return;   // 各区域都没变，遮罩本来就没撤，接着用
            }

            try
            {
                // 只对脏了的选区单独识字：各自裁小图跑一遍，干净的沿用上一轮结果。
                // 每块各开一个线程并行跑——三块串行时一个区变了要等前面两块白跑一遍 OCR
                List<Thread> ths = new List<Thread>();
                for (int i = 0; i < _slots.Count; i++)
                {
                    RegionSlot sl = _slots[i];
                    if (!sl.Dirty) continue;
                    RegionSlot cur = sl;
                    int idx = i;
                    // 复制图必须在这个（主）线程里做：GDI+ 的 Bitmap 不能几个线程同时读
                    Bitmap mine = CropAt(bmp, 0, 0, bmp.Width, bmp.Height);
                    Bitmap forTh;
                    if (mine == null) forTh = null;
                    else { forTh = mine; }
                    Thread th = new Thread(delegate() { DoOcrSlot(cur, forTh, union, idx, _slots.Count); });
                    th.IsBackground = true;
                    th.Priority = System.Threading.ThreadPriority.BelowNormal;
                    th.Start();
                    ths.Add(th);
                }
                for (int i = 0; i < ths.Count; i++)
                {
                    try { ths[i].Join(20000); } catch (Exception) { }   // 单块卡死也不能把整轮拖住
                }
                // —— 合并后统一过滤（沿用原来的跨区规则，保持识别质量）——
                List<OcrSegment> segs = new List<OcrSegment>();
                for (int i = 0; i < _slots.Count; i++)
                {
                    RegionSlot sl = _slots[i];
                    if (sl.HasSegs && sl.Segs != null) segs.AddRange(sl.Segs);
                }

                // 诊断：OCR 原始命中数与最大字高，用来区分「检测不到」和「被后置过滤丢掉」
                int rawCount = segs.Count;
                int rawMaxH = 0;
                for (int i = 0; i < segs.Count; i++)
                {
                    int hh = segs[i].Box.Height;
                    if (hh > rawMaxH) rawMaxH = hh;
                }

                segs = GameFilter.KeepDialogue(segs, union, regions);
                if (AppConfig.GameFontFilter)
                    segs = GameFilter.DropSmallText(segs);   // 框选得大时，任务栏/技能格那类 UI 小字不该翻
                if (AppConfig.GameMerge)
                    segs = GameFilter.MergeLines(segs, union, regions);

                if (gen != _gen) return;

                // —— 过滤后按选区拆开：每一块只认自己范围内的行 ——
                SplitByRegion(segs, union, _slots);

                // —— 每块独立翻译、独立上屏：一个区变了只重画它自己那个遮罩窗口 ——
                JadeHost.GameBusy("翻译中", "模型 · " + JadeHost.ModelLabel(AppConfig.GameModel));
                int totalLines = 0, totalBlocks = 0;
                for (int i = 0; i < _slots.Count; i++)
                {
                    RegionSlot sl = _slots[i];
                    List<OcrSegment> zs = sl.Segs == null ? new List<OcrSegment>() : sl.Segs;
                    totalBlocks += zs.Count;
                    // 没变的区：上一轮的遮罩「还在屏上」才原样留着，这一轮不碰它。
                    // 遮罩已经被整块收掉（自动收遮罩 / 换页 / 空帧）时，光看指纹会误判成
                    // 「旧译文还留着」，于是这块干脆不画——第一次有、第二次死活不出现就是这么来的。
                    if (!sl.Dirty && sl.HasSegs)
                    {
                        if (sl.Lines > 0)
                        {
                            if (KeepZoneOnScreen(sl, i + 1)) { totalLines += sl.Lines; continue; }
                            // 铺不回去（窗口已经拆了）→ 不 continue，落到下面老实重翻重画
                        }
                        else
                        {
                            totalLines += sl.Lines;
                            continue;
                        }
                    }
                    if (zs.Count == 0)
                    {
                        sl.Lines = 0;
                        sl.LastText = null;
                        try { TransOverlay.HideZone(sl.Zid); } catch (Exception) { }
                        continue;
                    }
                    string ztk = TextKey(zs);
                    if (!force && ztk.Length > 0 && ztk == sl.LastText && sl.Lines > 0)
                    {
                        // 原文没变，但遮罩不在屏上了（比如刚被自动收掉）：先把上一轮的译文铺回去
                        if (KeepZoneOnScreen(sl, i + 1)) { totalLines += sl.Lines; continue; }
                    }
                    sl.LastText = ztk;
                    if (gen != _gen) return;

                    // 坐标从「相对并集」转成「相对这一块」，抓图也只裁这一块
                    int dx = sl.R.Left - union.Left, dy = sl.R.Top - union.Top;
                    List<OcrSegment> rel = ShiftSegs(zs, dx, dy);
                    Bitmap zcrop = CropAt(bmp, dx, dy, sl.R.Width, sl.R.Height);
                    int lines = 0;
                    try
                    {
                        if (!_overlayClosed && zcrop != null)
                            lines = JadeHost.GameTranslate(zcrop, sl.R, rel, sl.Zid);
                    }
                    catch (Exception exz)
                    {
                        JadeHost.Log("[game] zone " + (i + 1) + " translate fail " + exz.Message);
                    }
                    finally { if (zcrop != null) { try { zcrop.Dispose(); } catch (Exception) { } } }
                    sl.Lines = lines;
                    totalLines += lines;
                }

                if (gen != _gen) return;
                if (totalBlocks == 0)
                {
                    JadeHost.Log("[game] no dialogue raw=" + rawCount + " rawMaxH=" + rawMaxH
                        + " dirty=" + dirtyCount + "/" + _slots.Count);
                    try { TransOverlay.HideOverlay(); } catch (Exception) { }   // 没结果就别留旧译文
                    JadeHost.GameDone("没有识别到字幕", "模型 · " + JadeHost.ModelLabel(AppConfig.GameModel), false);
                    return;
                }
                JadeHost.GameDone(totalLines > 0 ? "翻译完成" : (JadeHost.GameSkipShort ?? "没有识别到字幕"),
                    "模型 · " + JadeHost.ModelLabel(AppConfig.GameModel) + (totalLines > 0 ? " · " + totalLines + " 行" : ""),
                    totalLines > 0);
                if (_overlayClosed) { try { TransOverlay.HideOverlay(); } catch (Exception) { } }
                else if (totalLines > 0 && TransOverlay.IsShowing) StartMaskWatch();
                else StartAutoResume();   // 该显示的没显示出来 / 一个字都没认出来也要接着盯，下一轮自愈
                JadeHost.Log("[game] shown=" + totalLines + " blocks=" + totalBlocks
                    + " dirty=" + dirtyCount + "/" + _slots.Count
                    + " vis=" + (TransOverlay.IsShowing ? 1 : 0)
                    + " trigger=" + AppConfig.GameTrigger + " force=" + (force ? 1 : 0));
            }
            finally
            {
                bmp.Dispose();
            }
        }

        // 单块识字（在自己的线程里跑）：日文已确认的直接走日文模型，
        // 没判过的先用当前引擎跑，结果全是汉字时再用日文模型复跑一次定调
        private static void DoOcrSlot(RegionSlot sl, Bitmap own, Rectangle union, int idx, int total)
        {
            try
            {
                if (own == null) { JadeHost.Log("[game] zone " + (idx + 1) + " crop null"); return; }
                if (sl.Jp == 1)
                {
                    sl.Segs = OcrRegion(own, union, sl.R, true);
                }
                else
                {
                    sl.Segs = OcrRegion(own, union, sl.R, false);
                    // 原来靠 LooksJpCandidate 从「中文模型的输出」里找日文特征——可中文模型
                    // 压根吐不出假名（「くな」被认成「<本」），这个门永远不开，
                    // 日文模型一次都用不上，字幕就被当成中文跳过翻译了。
                    // 改成反过来：有日文模型就直接拿它试一遍，再看输出里假名够不够。
                    if (sl.Jp == 0)
                    {
                        bool cand = LooksJpCandidate(sl.Segs);
                        bool jpTry = AppConfig.GameJpAuto && Ocr.JapanReady();
                        JadeHost.Log("[game] jp gate zone=" + (idx + 1) + " auto=" + AppConfig.GameJpAuto
                            + " ready=" + Ocr.JapanReady() + " cand=" + cand + " probe=" + jpTry);
                        if (jpTry)
                        {
                            int baseChars = TextChars(sl.Segs);
                            List<OcrSegment> baseSegs = sl.Segs;
                            List<OcrSegment> jpSegs = OcrRegion(own, union, sl.R, true);
                            sl.Jp = KanaStrong(jpSegs, baseChars) ? 1 : 2;
                            if (sl.Jp == 1) sl.Segs = MergeJpBase(jpSegs, baseSegs);
                            JadeHost.Log("[game] region " + (idx + 1) + "/" + total
                                + (sl.Jp == 1 ? " jp auto on" : " jp auto off"));
                        }
                        else sl.Jp = 2;
                    }
                }
                sl.HasSegs = true;
            }
            catch (Exception ex)
            {
                JadeHost.Log("[game] zone " + (idx + 1) + " ocr fail " + ex.Message);
            }
            finally
            {
                if (own != null) { try { own.Dispose(); } catch (Exception) { } }
            }
        }

        // 过滤后的行按选区归属拆开：每块只留落在自己范围内的行（坐标仍相对并集）。
        // 边缘放宽几像素，免得字幕片贴在框边被算到隔壁去。
        private static void SplitByRegion(List<OcrSegment> segs, Rectangle union, List<RegionSlot> slots)
        {
            for (int i = 0; i < slots.Count; i++) slots[i].Segs = new List<OcrSegment>();
            if (segs == null) return;
            for (int i = 0; i < segs.Count; i++)
            {
                OcrSegment s = segs[i];
                Rectangle b = s.Box;
                int cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
                int best = -1; long bestD = long.MaxValue;
                for (int j = 0; j < slots.Count; j++)
                {
                    Rectangle z = new Rectangle(slots[j].R.Left - union.Left, slots[j].R.Top - union.Top,
                        slots[j].R.Width, slots[j].R.Height);
                    z.Inflate(10, 10);   // bmp 被子像素裁过，边缘给一点余量
                    if (cx >= z.Left && cx <= z.Right && cy >= z.Top && cy <= z.Bottom)
                    {
                        best = j;
                        bestD = 0;
                        break;
                    }
                    // 都不在框内（补边溢出的行）就归给中心最近的那一块，别丢
                    int ddx = cx < z.Left ? z.Left - cx : (cx > z.Right ? cx - z.Right : 0);
                    int ddy = cy < z.Top ? z.Top - cy : (cy > z.Bottom ? cy - z.Bottom : 0);
                    long d = (long)ddx * ddx + (long)ddy * ddy;
                    if (d < bestD) { bestD = d; best = j; }
                }
                if (best >= 0) slots[best].Segs.Add(s);
            }
        }

        // 行坐标从「相对并集」平移到「相对某一块」
        private static List<OcrSegment> ShiftSegs(List<OcrSegment> segs, int dx, int dy)
        {
            List<OcrSegment> res = new List<OcrSegment>(segs.Count);
            for (int i = 0; i < segs.Count; i++)
            {
                OcrSegment s = segs[i];
                OcrSegment t = new OcrSegment();
                t.Text = s.Text;
                t.Angle = s.Angle;
                t.Order = s.Order;
                t.LowConfidence = s.LowConfidence;
                t.SkipMt = s.SkipMt;
                Rectangle b = s.Box;
                t.Box = new Rectangle(b.X - dx, b.Y - dy, b.Width, b.Height);
                if (s.SourceBoxes != null)
                {
                    t.SourceBoxes = new List<Rectangle>(s.SourceBoxes.Count);
                    for (int j = 0; j < s.SourceBoxes.Count; j++)
                    {
                        Rectangle rb = s.SourceBoxes[j];
                        t.SourceBoxes.Add(new Rectangle(rb.X - dx, rb.Y - dy, rb.Width, rb.Height));
                    }
                }
                res.Add(t);
            }
            return res;
        }

        // 从并集图里裁出一块（越界部分用黑底补齐）
        private static Bitmap CropAt(Bitmap src, int x, int y, int w, int h)
        {
            if (src == null || w < 4 || h < 4) return null;
            try
            {
                Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.Black);
                    Rectangle dst = new Rectangle(0, 0, w, h);
                    Rectangle s = Rectangle.Intersect(new Rectangle(x, y, w, h), new Rectangle(0, 0, src.Width, src.Height));
                    if (s.Width > 0 && s.Height > 0)
                        g.DrawImage(src, new Rectangle(s.X - x, s.Y - y, s.Width, s.Height), s, GraphicsUnit.Pixel);
                }
                return b;
            }
            catch (Exception ex)
            {
                JadeHost.Log("[game] crop fail " + ex.Message);
                return null;
            }
        }

        // 给 OCR 输入补一圈底色：文字紧贴选区边缘时检测模型会整行漏检。
        // 横向按比例补（文字占满整宽最容易漏），纵向按高度补一点。
        // 填充色取原图角落，尽量和背景一致，避免补边自己变成一条"边框"被识别
        private static Bitmap PadForOcr(Bitmap src, int minPad, out int padX, out int padY)
        {
            padX = 0; padY = 0;
            if (src == null || minPad <= 0) return src;
            padX = Math.Max(minPad, (int)(src.Width * 0.07));
            // 字幕条往往又宽又扁，上下只留 10% 时检测模型经常一行都找不到（日文实测 raw=0），
            // 多给上下留白等于把字幕条包回一张正常比例的图，检测率明显回升
            padY = Math.Max(minPad, (int)(src.Height * 0.28));
            Color edge;
            try
            {
                // 取顶部中间那块底色；不能取 (2,2)，那里可能是左侧的高亮竖条
                edge = src.GetPixel(src.Width / 2, Math.Min(2, src.Height - 1));
            }
            catch (Exception) { edge = Color.Black; }
            Bitmap dst = new Bitmap(src.Width + padX * 2, src.Height + padY * 2, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.Clear(edge);
                g.DrawImage(src, padX, padY);
            }
            return dst;
        }

        private static void ScaleSegs(List<OcrSegment> segs, List<Rectangle> obstacles, int div)
        {
            for (int i = 0; i < segs.Count; i++)
            {
                OcrSegment s = segs[i];
                Rectangle b = s.Box;
                s.Box = new Rectangle(b.X / div, b.Y / div, Math.Max(1, b.Width / div), Math.Max(1, b.Height / div));
                if (s.SourceBoxes != null)
                {
                    for (int j = 0; j < s.SourceBoxes.Count; j++)
                    {
                        Rectangle rb = s.SourceBoxes[j];
                        s.SourceBoxes[j] = new Rectangle(rb.X / div, rb.Y / div, Math.Max(1, rb.Width / div), Math.Max(1, rb.Height / div));
                    }
                }
            }
            if (obstacles != null)
            {
                for (int i = 0; i < obstacles.Count; i++)
                {
                    Rectangle rb = obstacles[i];
                    obstacles[i] = new Rectangle(rb.X / div, rb.Y / div, Math.Max(1, rb.Width / div), Math.Max(1, rb.Height / div));
                }
            }
        }

        private static void UnshiftSegs(List<OcrSegment> segs, List<Rectangle> obstacles, int padX, int padY)
        {
            if (segs != null)
            {
                for (int i = 0; i < segs.Count; i++)
                {
                    OcrSegment s = segs[i];
                    Rectangle b = s.Box;
                    b.Offset(-padX, -padY);
                    s.Box = b;
                    if (s.SourceBoxes != null)
                    {
                        for (int j = 0; j < s.SourceBoxes.Count; j++)
                        {
                            Rectangle sb = s.SourceBoxes[j];
                            sb.Offset(-padX, -padY);
                            s.SourceBoxes[j] = sb;
                        }
                    }
                }
            }
            if (obstacles != null)
            {
                for (int i = 0; i < obstacles.Count; i++)
                {
                    Rectangle o = obstacles[i];
                    o.Offset(-padX, -padY);
                    obstacles[i] = o;
                }
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

        // 选区列表变了（数量或坐标）就把各自的缓存重建，别拿旧区域的指纹去比新区域
        // 统计一段识别结果的字符构成（假名单独算：中文模型字典里根本没有假名）
        private static void CountChars(List<OcrSegment> segs, ref int cjk, ref int kana, ref int latin, ref int digits)
        {
            if (segs == null) return;
            for (int i = 0; i < segs.Count; i++)
            {
                string s = segs[i].Text;
                if (s == null) continue;
                for (int k = 0; k < s.Length; k++)
                {
                    char c = s[k];
                    if (c >= '0' && c <= '9') digits++;
                    else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) latin++;
                    else if (c >= 0x4e00 && c <= 0x9fff) cjk++;
                    else if ((c >= 0x3040 && c <= 0x30ff) || (c >= 0xff66 && c <= 0xff9d)) kana++;
                }
            }
        }

        private static int TextChars(List<OcrSegment> segs)
        {
            int cjk = 0, kana = 0, latin = 0, digits = 0;
            CountChars(segs, ref cjk, ref kana, ref latin, ref digits);
            return cjk + kana + latin + digits;
        }

        // 日文模型经常把「哨兵隊長」这类纯汉字小标题行当成非正文丢掉，而中文模型认汉字更准。
        // 所以日文结果定下来之后，把中文结果里「和日文行不重叠、纯汉字」的行补回来，
        // 否则那行原文会原样露在遮罩外面。
        private static List<OcrSegment> MergeJpBase(List<OcrSegment> jp, List<OcrSegment> bs)
        {
            if (jp == null || jp.Count == 0 || bs == null || bs.Count == 0) return jp;
            List<OcrSegment> res = new List<OcrSegment>(jp);
            for (int i = 0; i < bs.Count; i++)
            {
                OcrSegment b = bs[i];
                if (b.Box.Width < 80) continue;
                List<OcrSegment> one = new List<OcrSegment>();
                one.Add(b);
                int cjk = 0, kana = 0, latin = 0, digits = 0;
                CountChars(one, ref cjk, ref kana, ref latin, ref digits);
                if (kana > 0 || latin > 0 || cjk < 2) continue;
                bool overlap = false;
                for (int k = 0; k < jp.Count; k++)
                {
                    Rectangle a = jp[k].Box;
                    Rectangle hit = Rectangle.Intersect(a, b.Box);
                    if (hit.Height * 2 >= Math.Min(a.Height, b.Box.Height)) { overlap = true; break; }
                }
                if (overlap) continue;
                res.Add(b);
            }
            if (res.Count > 1)
                res.Sort(delegate(OcrSegment x, OcrSegment y) { return x.Box.Y.CompareTo(y.Box.Y); });
            JadeHost.Log("[game] jp merge base=" + bs.Count + " -> " + res.Count);
            return res;
        }

        // 值得用日文模型复跑的候选：全是汉字，一个假名、一个拉丁字母都没有，且有足够的字
        private static bool LooksJpCandidate(List<OcrSegment> segs)
        {
            int cjk = 0, kana = 0, latin = 0, digits = 0;
            CountChars(segs, ref cjk, ref kana, ref latin, ref digits);
            JadeHost.Log("[game] jp probe cjk=" + cjk + " kana=" + kana + " latin=" + latin + " digits=" + digits);
            return kana == 0 && cjk >= 6 && latin <= cjk / 8;
        }

        // 复跑结果确实是日文：假名占够比例，且字数没有塌（中文被日文模型硬认时字数通常掉得很厉害）
        private static bool KanaStrong(List<OcrSegment> segs, int baseChars)
        {
            if (segs == null || segs.Count == 0) return false;
            int cjk = 0, kana = 0, latin = 0, digits = 0;
            CountChars(segs, ref cjk, ref kana, ref latin, ref digits);
            int total = cjk + kana;
            if (total < 4 || kana < 2) return false;
            if (kana * 8 < total) return false;          // 假名至少占 12.5%
            if (baseChars > 0 && total * 100 < baseChars * 70) return false;
            return latin <= total / 4;
        }

        private static void SyncSlots(List<Rectangle> regions)
        {
            bool same = _slots.Count == regions.Count;
            if (same)
            {
                for (int i = 0; i < regions.Count; i++)
                {
                    if (!_slots[i].R.Equals(regions[i])) { same = false; break; }
                }
            }
            if (same) return;
            _slots.Clear();
            // 选区改过了：旧的分区遮罩（编号是按顺序发的）不能留着，否则下一轮
            // 可能把上一版的译文铺到新选区的位置上。清掉之后各块重翻重画。
            try { TransOverlay.ClearZones(); } catch (Exception) { }
            for (int i = 0; i < regions.Count; i++)
            {
                RegionSlot sl = new RegionSlot();
                sl.R = regions[i];
                sl.Zid = i;   // 每个选区一个固定编号的遮罩窗口，互不覆盖
                _slots.Add(sl);
            }
        }

        // 单个选区的指纹（128x48 灰度，恒定长度，好和上一轮逐字节比）
        private static byte[] SignatureOne(Bitmap bmp, Rectangle union, Rectangle r)
        {
            byte[] data = new byte[128 * 48];
            int sx = r.Left - union.X, sy = r.Top - union.Y;
            int sw = Math.Min(r.Width, bmp.Width - sx);
            int sh = Math.Min(r.Height, bmp.Height - sy);
            if (sw < 4 || sh < 4) return data;
            using (Bitmap small = new Bitmap(128, 48, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.Clear(Color.Black);
                    g.DrawImage(bmp, new Rectangle(0, 0, 128, 48), new Rectangle(sx, sy, sw, sh), GraphicsUnit.Pixel);
                }
                int k = 0;
                for (int y = 0; y < 48; y++)
                    for (int x = 0; x < 128; x++)
                    {
                        Color c = small.GetPixel(x, y);
                        data[k++] = (byte)((c.R * 77 + c.G * 151 + c.B * 28) >> 8);
                    }
            }
            return data;
        }

        // 单个选区单独识字：裁出来 → 补边 → OCR（一行都没检出就放大 2x 再试）→ 坐标换回 union 坐标系
        private static List<OcrSegment> OcrRegion(Bitmap unionBmp, Rectangle union, Rectangle r, bool japan)
        {
            List<OcrSegment> segs = new List<OcrSegment>();
            int sx = r.Left - union.X, sy = r.Top - union.Y;
            int sw = Math.Min(r.Width, unionBmp.Width - sx);
            int sh = Math.Min(r.Height, unionBmp.Height - sy);
            if (sw < 8 || sh < 8) return segs;

            Bitmap sub = null;
            List<Rectangle> obstacles = null;
            string log = null;
            int padX = 0, padY = 0;
            try
            {
                sub = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(sub))
                {
                    g.DrawImage(unionBmp, new Rectangle(0, 0, sw, sh), new Rectangle(sx, sy, sw, sh), GraphicsUnit.Pixel);
                }
                // 选区贴着文字边缘时检测模型常常整行漏检（小框最明显），补一圈底色再喂 OCR
                const int pad = 12;
                Bitmap ocrIn = PadForOcr(sub, pad, out padX, out padY);
                try
                {
                    segs = japan
                        ? Ocr.LayoutSegmentsJapan(ocrIn, out obstacles, out log)
                        : (AppConfig.OcrFusion
                            ? Ocr.LayoutSegmentsFused(ocrIn, out obstacles, out log)
                            : Ocr.LayoutSegments(ocrIn, out obstacles, out log));
                    if (segs == null) segs = new List<OcrSegment>();
                    if (segs.Count == 0)
                    {
                        Bitmap big = null;
                        try
                        {
                            big = new Bitmap(ocrIn.Width * 2, ocrIn.Height * 2, PixelFormat.Format32bppArgb);
                            using (Graphics g = Graphics.FromImage(big))
                            {
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                g.DrawImage(ocrIn, new Rectangle(0, 0, big.Width, big.Height));
                            }
                            List<Rectangle> ob2 = null;
                            string lg2 = "";
                            segs = japan
                                ? Ocr.LayoutSegmentsJapan(big, out ob2, out lg2)
                                : (AppConfig.OcrFusion
                                    ? Ocr.LayoutSegmentsFused(big, out ob2, out lg2)
                                    : Ocr.LayoutSegments(big, out ob2, out lg2));
                            if (segs == null) segs = new List<OcrSegment>();
                            obstacles = ob2 == null ? new List<Rectangle>() : ob2;
                            ScaleSegs(segs, obstacles, 2);
                        }
                        catch (Exception) { }
                        finally { if (big != null) { try { big.Dispose(); } catch (Exception) { } } }
                        JadeHost.Log("[game] region ocr retry2x lines=" + segs.Count);
                    }
                    else if (japan)
                    {
                        // 日文模型偶尔把假名压扁认成汉字或符号（「行くな。」被认成「行<本。」），
                        // 结果里一个假名都没有时不能直接当「这不是日文」——很可能只是假名被认没了。
                        // 放大 2x 再跑一次，假名变多就用新的
                        int k = 0, c = 0, lat = 0, dg = 0;
                        CountChars(segs, ref c, ref k, ref lat, ref dg);
                        if (k == 0 && c > 0)
                        {
                            Bitmap big2 = null;
                            try
                            {
                                big2 = new Bitmap(ocrIn.Width * 2, ocrIn.Height * 2, PixelFormat.Format32bppArgb);
                                using (Graphics g = Graphics.FromImage(big2))
                                {
                                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                    g.DrawImage(ocrIn, new Rectangle(0, 0, big2.Width, big2.Height));
                                }
                                List<Rectangle> ob2 = null;
                                string lg2 = "";
                                List<OcrSegment> s2 = Ocr.LayoutSegmentsJapan(big2, out ob2, out lg2);
                                if (s2 != null && s2.Count > 0)
                                {
                                    ScaleSegs(s2, ob2 == null ? new List<Rectangle>() : ob2, 2);
                                    int k2 = 0, c2 = 0, lat2 = 0, dg2 = 0;
                                    CountChars(s2, ref c2, ref k2, ref lat2, ref dg2);
                                    if (k2 > k)
                                    {
                                        segs = s2;
                                        obstacles = ob2 == null ? new List<Rectangle>() : ob2;
                                        JadeHost.Log("[game] region ocr kana retry2x kana=" + k2);
                                    }
                                }
                            }
                            catch (Exception) { }
                            finally { if (big2 != null) { try { big2.Dispose(); } catch (Exception) { } } }
                        }
                    }
                }
                finally { if (!ReferenceEquals(ocrIn, sub)) { try { ocrIn.Dispose(); } catch (Exception) { } } }
            }
            catch (Exception ex)
            {
                JadeHost.Log("[game] region ocr fail " + ex.Message);
            }
            finally
            {
                if (sub != null) { try { sub.Dispose(); } catch (Exception) { } }
            }

            // 坐标：ocrIn（含 pad）→ sub → union
            for (int i = 0; i < segs.Count; i++)
            {
                OcrSegment s = segs[i];
                Rectangle b = s.Box;
                s.Box = new Rectangle(b.X - padX + sx, b.Y - padY + sy, b.Width, b.Height);
                if (s.SourceBoxes != null)
                {
                    for (int j = 0; j < s.SourceBoxes.Count; j++)
                    {
                        Rectangle rb = s.SourceBoxes[j];
                        s.SourceBoxes[j] = new Rectangle(rb.X - padX + sx, rb.Y - padY + sy, rb.Width, rb.Height);
                    }
                }
            }
            // 补边之后黑底边缘常被检测成小块（右上角飘出来的「元I旧」那种），
            // 只按尺寸和位置筛，不按字号筛：按字号筛会把同一行字幕被拆开的一半也丢掉
            // 注意：s.Box 这一步已经是 union 相对坐标，比较基准要用相对选区而不是屏幕坐标
            Rectangle rr = new Rectangle(sx, sy, sw, sh);
            List<OcrSegment> keep = new List<OcrSegment>(segs.Count);
            for (int i = 0; i < segs.Count; i++)
            {
                OcrSegment s = segs[i];
                Rectangle nb = s.Box;
                // 只丢「上边越界」的块（补边之后黑底顶部常被检成一条假字）；
                // 不按尺寸丢，否则「哨兵隊長」这种被拆成两块的小标题会一起没掉
                if (nb.Y < rr.Top - 2) continue;
                keep.Add(s);
            }
            return keep;
        }

        private static double SignatureDiff(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length == 0 || b.Length != a.Length) return 1.0;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > 16) diff++;
            return (double)diff / a.Length;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        // 抓底层画面：把遮罩掐掉，等合成器把这一帧真的提交了再抓，抓完立刻恢复。
        // 不能靠 WDA_EXCLUDEFROMCAPTURE——那会让分层遮罩连屏幕合成一起被剔除，遮罩直接看不见。
        // 只 Sleep 不等合成的话，抓到的往往是遮罩还在的旧帧或纯灰。
        private static Bitmap CaptureUnderOverlay(Rectangle r)
        {
            TransOverlay.HideForSample();
            try
            {
                // 掐掉遮罩到抓到干净底图之间必须真等到合成器提交，否则抓到的是
                // 「遮罩还压在上面」的半截帧，比对出来差异很大，遮罩就会被误判成画面变了
                DwmFlush();
                Thread.Sleep(18);
                DwmFlush();
                Thread.Sleep(16);
                DwmFlush();
                return Capture(r);
            }
            catch (Exception) { return null; }
            finally { try { TransOverlay.ShowForSample(); } catch (Exception) { } }
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

        /// <summary>
        /// 框选范围一大，任务列表、技能格、小地图标签这些 UI 小字会被一起 OCR 进来，
        /// 全部翻译既慢又乱。这里按字号做两级筛：先丢绝对过小的行，再按同屏最大字高
        /// 只留「最大的那几行」——字幕永远比 UI 小字大得多。
        /// </summary>
        public static List<OcrSegment> DropSmallText(List<OcrSegment> segs)
        {
            if (segs == null || segs.Count < 2) return segs;

            int maxH = 0;
            int total = 0;
            for (int i = 0; i < segs.Count; i++)
            {
                int h = segs[i].Box.Height;
                if (h > maxH) maxH = h;
                total += Math.Max(0, (segs[i].Text ?? "").Trim().Length);
            }
            if (maxH < 22) return segs;          // 整屏都是小字（小窗口游戏 / 缩放很小），不瞎筛

            int floor = Math.Max(16, (int)(maxH * 0.58));
            List<OcrSegment> res = new List<OcrSegment>();
            for (int i = 0; i < segs.Count; i++)
            {
                OcrSegment s = segs[i];
                if (s.Box.Height >= floor) res.Add(s);
            }
            // 全被筛掉了说明判断失误（比如整屏只有一行细字），那就原样放行
            if (res.Count == 0) return segs;
            JadeHost.Log("[game] font filter keep=" + res.Count + "/" + segs.Count + " maxH=" + maxH + " floor=" + floor);
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
            // 阈值只能看「单行高度」。原来是 a.Box.Height——可 a 是合并累加过的块，
            // 每并一行它就变高、阈值跟着变大，最后整块任务榜 6 行被滚雪球并成一个大框，
            // 译文只能缩到极小字号塞进去，看着就是「字特别小、位置对不上」
            int hRef = Math.Min(a.Box.Height, b.Box.Height);
            if (hRef < 1) hRef = Math.Max(1, Math.Min(a.Box.Height, b.Box.Height));
            int gap = b.Box.Top - a.Box.Bottom;
            if (gap < -Math.Max(2, hRef / 2) || gap > Math.Max(6, hRef / 2)) return false;
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
