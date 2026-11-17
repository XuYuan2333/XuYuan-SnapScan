using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        // 换页/换 tab 时所有遮罩已被清空，但紧接着的 DoTick 会按像素重新算脏，
        // 把「整页都变了」这件事覆盖成「只有一块变了」，结果只重贴一块、其余遮罩消失不再出现。
        // 这个标志让下一次 DoTick 把所有分区都当脏的（并且不先撤旧遮罩，避免闪一下）。
        private static bool _forceRepaint;
        // 整页重贴的冷却时刻：全量清空遮罩对体验是有代价的（旧译文全消失再一起出现），
        // 只在「真换页」这么干；连续多轮检测都数出「几乎全脏」时，冷却期内改走逐块处理，
        // 否则遮罩就会陷入「清空-重贴-再清空」的循环，看起来一直在闪。
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
            public List<OcrSegment> PrevSegs; // 上一轮真正上屏的逐行结果：整块拼一起会把「只改一行」稀释掉，必须逐行比
            public Rectangle ShownRect;    // 这块遮罩上一次真正贴在屏幕上的矩形（用于「原文没变只挪框」时搬遮罩）
            public int DirtyStreak;       // 连续几次检测到像素变化：单帧抖动不撤罩重译，连续四次才判脏
            public string JpKey;          // 上一次日文复核时的原文指纹：同一句话不重复跑日文模型
            public byte[] RoiGray;        // 上一轮「已接受」画面的 160x90 灰度采样，做动态 ROI 只重扫变化行
            public int RoiW, RoiH;
            public List<Rectangle> RoiBands;   // 本轮要重扫的变化横带（null=整区，空表=无变化）
            public byte[] RoiNew;             // 本轮画面的灰度采样，OCR 接受后作为下一轮基线
            public bool RoiFull;
        }
        private static List<RegionSlot> _slots = new List<RegionSlot>();
        private static bool _paused;
        private static bool _overlayClosed;
        private static string _prevProfile;
        // 自动识别模式下的遮罩编号池：轨道 id 会一直涨，直接拿它当窗口编号会不停新建窗口，
        // 这里用一个池子循环复用，屏幕上的遮罩窗口数量就跟着「实际块数」走。
        // 池大小 96：不再按 24 块截断识别结果，一屏几十个 UI 标签也能各占一个遮罩窗口。
        private const int ZidPool = 96;
        private static readonly List<int> _freeZid = new List<int>();
        private static int _zidNext;
        // 单槽合并：OCR 正在跑时再来一批变化，不排队堆任务，只记一个「跑完再补一次」。
        private static bool _pendingRun;
        private static long _autoHoldUntil;      // 空结果退避截止时间（Environment.TickCount）
        private static int _emptyStreak;         // 连续几轮没有识别到文字
        private static bool _allowWhileShowing;  // 遮罩期 MaskTick 主动触发重扫时，允许进入 DoTick
        private static Point _dwellPos;          // 鼠标停稳触发：上一次采样时的光标位置
        private static long _dwellSince;          // 光标停住起始时刻
        private static Point _dwellAnchor;        // 上一轮扫描时的光标位置（移动够远才允许再扫）


        private static int NewZid()
        {
            if (_freeZid.Count > 0)
            {
                int id = _freeZid[_freeZid.Count - 1];
                _freeZid.RemoveAt(_freeZid.Count - 1);
                return id;
            }
            if (_zidNext < ZidPool) return _zidNext++;
            return (_zidNext++ % ZidPool);   // 兜底：极端情况下复用，同一时刻最多 ZidPool 块遮罩
        }

        private static void FreeZid(int id)
        {
            if (id < 0 || id >= ZidPool) return;
            for (int i = 0; i < _freeZid.Count; i++) if (_freeZid[i] == id) return;
            if (_freeZid.Count < ZidPool) _freeZid.Add(id);
        }

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

        // 当前生效的选区：只认用户画好的固定区域。
        // 「要抓哪块图、要比哪块、要贴哪个遮罩」的所有环节都走这里，
        // 免得有的地方拿配置、有的地方拿内存，两边对不上。
        public static List<Rectangle> ActiveRegions()
        {
            return Regions();
        }

        // 识别范围：几个固定区域的并集。玩家一般就画一个（比如整个字幕带或整屏）。
        public static Rectangle ScanRect()
        {
            List<Rectangle> rs = Regions();
            if (rs.Count == 0) return Rectangle.Empty;
            Rectangle u = Union(rs);
            u.Intersect(SystemInformation.VirtualScreen);
            return u;
        }

        // 遮罩编号池与选区槽位：槽位按配置里的固定选区顺序重建，遮罩编号沿用上一轮，
        // 免得「内容没变也整片撤遮罩重翻」。

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

        // 改了固定选区、或者重置游戏模式时调用：
        public static void ResetAutoState()
        {
            _freeZid.Clear();
            _zidNext = 0;

            _autoBaseReady = false;
            _autoLastText = "";
            _prevSig = null;
            _slots.Clear();
            try { TransOverlay.ClearZones(); } catch (Exception) { }
            JadeHost.Log("[auto] reset");
        }

        // 游戏「翻译服务」运行态：和「游戏模式开没开」分开。
        // false = 只做准备（自动识别区域照常找字、界面照常显示区域），不 OCR、不翻译、不上遮罩。
        public static bool TranslateActive { get; private set; }
        // 上次退出游戏模式时翻译是开是关：再次启动游戏模式要恢复，而不是每次都退回「只准备」。
        private static bool _translateWanted;

        // 每游戏缓存命名空间：用遮罩下面那个窗口的进程名做身份，换游戏自动换缓存桶。
        // 只取进程名不取标题（标题经常带章节/关卡名，会一直变，缓存全被拆散）。
        public static string CacheProfile
        {
            get
            {
                if (!TranslateActive || _maskHwnd == IntPtr.Zero) return "";
                try
                {
                    uint pid = 0;
                    GetWindowThreadProcessId(_maskHwnd, out pid);
                    if (pid == 0) return "";
                    using (Process p = Process.GetProcessById((int)pid))
                        return p.ProcessName == null ? "" : p.ProcessName.ToLowerInvariant();
                }
                catch (Exception) { return ""; }
            }
        }

        // 专用快捷键/界面开关：真正启动或暂停翻译。
        public static void ToggleTranslate()
        {
            if (!AppConfig.GameOn) { JadeHost.GameDone("先开启游戏模式", false); return; }
            if (!AppConfig.GameAutoMode)
            {
                // 手动模式没有「服务」的开/关态：这个动作就是「翻当前画面一次」
                TriggerOnce("翻译服务");
                return;
            }
            if (TranslateActive)
            {
                TranslateActive = false;
                _translateWanted = false;
                _gen++;
                _busy = false;
                _pendingRun = false;
                StopAuto();                 // 暂停服务就真的停采样，别让它在后台把当前帧缓存成「已识字」
                StopMaskWatch();
                ResetAutoImageCache();
                try { TransOverlay.HideOverlay(); } catch (Exception) { }
                try { TransOverlay.ClearZones(); } catch (Exception) { }
                JadeHost.GameDone("游戏翻译已暂停", AppConfig.HotkeyGameTrans + " 再次启动", true);
                JadeHost.Log("[game] translate=off");
            }
            else
            {
                TranslateActive = true;
                _translateWanted = true;
                _gen++;
                _busy = false;
                _pendingRun = false;
                _overlayClosed = false;
                ResetAutoImageCache();
                string profile = AppConfig.NormalizeMtProfile(AppConfig.GameModel);
                WarmModel(profile);
                StartAutoResume();
                JadeHost.GameDone("游戏翻译已启动", AppConfig.HotkeyGameTrans + " 可暂停", true);
                JadeHost.Log("[game] translate=on profile=" + profile);
            }
        }

        // 手动模式：按一次翻译快捷键 = 只翻当前画面这一次。
        // 必须扔到后台线程：OCR + 翻译要好几秒，占着 UI 线程会把界面卡住。
        public static void TriggerOnce(string why)
        {
            if (!AppConfig.GameOn) { JadeHost.GameDone("先开启游戏模式", false); return; }
            TranslateActive = true;
            _translateWanted = true;
            _overlayClosed = false;
            JadeHost.GameDone("正在翻译当前画面", AppConfig.HotkeyMt + " 再按一次重翻", true);
            Thread th = new Thread(delegate() { try { Trigger(true); } catch (Exception) { } });
            th.IsBackground = true;
            th.Start();
            JadeHost.Log("[game] manual once (" + why + ")");
        }

        public static bool Start(out string err)
        {
            err = null;
            List<Rectangle> rs = Regions();
            if (rs.Count == 0)
            {
                err = "先画一个识别区域（整屏或字幕带都行）再启动游戏模式";
                LastError = err;
                return false;
            }

            string profile = AppConfig.NormalizeMtProfile(AppConfig.GameModel);
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
            // 重新开游戏模式必须从零认：旧的逐槽 Segs/RoiGray/遮罩编号全部作废，
            // 否则同一句字幕会命中「clean (roi)」直接 continue，遮罩已经 ClearZones 了却再也不重画。
            ResetAutoState();
            // 游戏模式只做「准备」：不再自动预热模型、不自动开翻译。
            // 真正开始/暂停翻译由专用快捷键（默认 Ctrl+Alt+T）或界面开关决定。
            // QRSNAP_GAME_TRANS=1 仅供自动化测试：启动即处于「翻译已启动」状态。
            TranslateActive = Environment.GetEnvironmentVariable("QRSNAP_GAME_TRANS") == "1" || _translateWanted;
            if (TranslateActive) WarmModel(profile);
            _autoLastText = "";
            _autoLastGray = null;
            _autoBaseReady = false;

            StartAuto();   // 开跑高频自动监听（画面定住才识字）
            JadeHost.Log("[game] start regions=" + rs.Count
                + " profile=" + profile
                + " trigger=" + AppConfig.GameTrigger + " interval=" + AppConfig.GameIntervalMs
                + " translate=" + (TranslateActive ? "on" : "off"));
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
            _translateWanted = TranslateActive;
            TranslateActive = false;
            _autoLastText = "";
            _autoBaseReady = false;
            AppConfig.GameOn = false;
            LastError = "";
            // 编号池清掉、遮罩窗口收干净，下次开启从零认
            _freeZid.Clear();
            _zidNext = 0;

            try { TransOverlay.ClearZones(); } catch (Exception) { }
            try { BlockDebug.Hide(); } catch (Exception) { }
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
            _gen++;
            _busy = false;
            _pendingRun = false;
            ResetAutoImageCache();
            StartTimer();
            StartAuto();
            if (TranslateActive && TransOverlay.GameShowing) StartMaskWatch();
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
                _gen++;
                _busy = false;
                _pendingRun = false;
                ResetAutoImageCache();
            }
            else if (AppConfig.GameOn)
            {
                _overlayClosed = false;
                ResetAutoImageCache();
                StartTimer();
                if (TranslateActive)
                {
                    // 暂停前遮罩还在屏上：恢复时优先接回遮罩期监听；否则重新开自动采样。
                    if (TransOverlay.GameShowing) StartMaskWatch();
                    else StartAuto();
                }
            }
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
                JadeHost.Log("[game] overlay opened by user showing=" + (showing ? 1 : 0));
                // 交给 TriggerOnce：它会把 TranslateActive 打开、并在后台线程翻，
                // 不占 UI 线程（这里原本是同步 Trigger，一按就卡好几秒）
                TriggerOnce("恢复译文");
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
        private static int _maskConfirmAt;   // 上一次跑「文字确认」的时刻（OCR 节流）
        private static int _maskMiss;        // 连续两次文字对不上才收遮罩，防一次 OCR 误读就闪
        private static string _maskMissKey;  // 上一次复核读到的新文字：两次读到同一段才算「真换了」

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

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr h);

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
                List<Rectangle> rs = ActiveRegions();
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
            _maskConfirmAt = 0;
            _maskMiss = 0;
            _maskMissKey = null;
            // 区域哈希必须跟这一轮的底图一起重算：留着上一轮（甚至上一句字幕）的旧值，
            // 第一拍就会判「区域变了」，然后每隔半秒跑一次 OCR 复核——遮罩就是这么闪起来的
            _zoneBase.Clear();

            // 记下这轮遮罩贴着的是哪个窗口、它的矩形；后面只查这个，不再靠像素。
            // 取点要跳过自己程序的窗口（主窗口被 send-bottom 挪过来时 WindowFromPoint 会先命中它）
            _maskHwnd = IntPtr.Zero;
            {
                List<Rectangle> cand = ActiveRegions();
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
                if (!AppConfig.GameAutoMode)
                {
                    // 手动模式一律不跑自动采样（resume 也一样）：按一次快捷键才翻一次，
                    // 遮罩收掉之后就等着下一次按键，不能自己又转起来 —— 那正是「手动和自适应没区别」。
                    JadeHost.Log("[game] auto skip gamemode=manual" + (resume ? " resume" : ""));
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
                _autoStableAt = Environment.TickCount;
                _autoOcrSkip = 0;
                _autoTextSkip = 0;
                _dwellPos = Cursor.Position;
                _dwellAnchor = _dwellPos;
                _dwellSince = Environment.TickCount;
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

        // 翻译开/关、暂停恢复、改选区之后，画面像素没变但「上一次识字的基准」还停在旧帧；
        // 不清掉它，AutoTick 会一直命中 sameimg，表现为「重新打开翻译却再也不出字」。
        private static void ResetAutoImageCache()
        {
            _autoLastGray = null;
            _autoLastBaseGray = null;
            _autoBaseReady = false;
            _autoOcrSkip = 0;
            _autoTextSkip = 0;
            _autoHoldUntil = 0;
            _autoStableAt = Environment.TickCount;
            // 逐槽基线也必须一起清：暂停时遮罩被收掉，底层画面和「贴着遮罩那一帧」的像素差很大，
            // 留着旧 RoiGray/Sig 会让 MotionScore 一直报 moving=88，稳定判定永远不成立 → 再也不 OCR。
            for (int i = 0; i < _slots.Count; i++)
            {
                RegionSlot sl = _slots[i];
                sl.RoiGray = null;
                sl.RoiNew = null;
                sl.RoiBands = null;
                sl.Sig = null;
                sl.HasSegs = false;
                sl.Segs = new List<OcrSegment>();
                sl.PrevSegs = null;
                sl.LastText = null;
                sl.Lines = 0;
                sl.ShownRect = Rectangle.Empty;
            }
        }

        // 鼠标停稳：同一位置停够 GameDwellMs，且离上一次真正扫描的锚点至少 30px，
        // 才认为用户「停下来看字幕了」。遮罩显示期间的自动更新也走这一条。
        private static bool DwellStill()
        {
            long nowTick = (long)Environment.TickCount;
            Point mp = Cursor.Position;
            if (mp != _dwellPos)
            {
                _dwellPos = mp;
                _dwellSince = nowTick;
                return false;
            }
            return nowTick - _dwellSince >= Math.Max(100, AppConfig.GameDwellMs);
        }

        private static bool DwellReady()
        {
            if (!DwellStill()) return false;
            Point mp = Cursor.Position;
            int ddx = mp.X - _dwellAnchor.X, ddy = mp.Y - _dwellAnchor.Y;
            return ddx * ddx + ddy * ddy >= 30 * 30;
        }

        private static void AutoTick(object state)
        {
            if (!AppConfig.GameOn || _paused || _overlayClosed) { StopAuto(); return; }
            // 翻译服务没开时只让定时器空转：千万别抓图识字，否则会先把当前画面缓存成「已处理」，
            // 等用户再按 Ctrl+Alt+T 打开翻译，sameimg 直接跳过，看起来就是「怎么都不翻」。
            if (!TranslateActive) return;
            if (TransOverlay.GameShowing || TransOverlay.Sampling) return;      // 遮罩真的画着东西（含采样掐图的几十毫秒），这段交给 MaskTick
            if (_busy) return;
            if (_maskClosing) { AutoNote("closing"); return; }
            if (_autoBusy) return;
            long nowTick = (long)Environment.TickCount;
            if (nowTick < _autoHoldUntil) { AutoNote("backoff"); return; }
            // 鼠标停稳触发：光标静止够久、且相比上一轮扫描移动过一段距离，才允许扫。
            // 鼠标一直在动就不扫，避免边玩边翻、白白拉高 CPU。
            if (AppConfig.GameDwellOn && !DwellReady()) return;

            // 只认固定区域：直接按选区并集抓图
            Rectangle scan;
            List<Rectangle> rs = ActiveRegions();
            if (rs.Count == 0) { StopAuto(); return; }
            scan = Union(rs);
            if (scan.Width < 32 || scan.Height < 24) { AutoNote("scan small"); return; }

            _autoBusy = true;
            bool handed = false;
            Bitmap b = null;
            try
            {
                _autoTickCount++;
                if (_autoTickCount % 125 == 0)
                {
                    JadeHost.Log("[game] auto alive ticks=" + _autoTickCount + " moved=" + _autoLastMoved
                        + " held=" + unchecked(Environment.TickCount - _autoStableAt) + "ms ocrSkip=" + _autoOcrSkip
                        + " textSkip=" + _autoTextSkip + " regions=" + ActiveRegions().Count);
                }
                // 没有遮罩，直接抓，不用隐藏/恢复——这一路完全不闪
                b = Capture(scan);
                if (b == null) { AutoNote("nocap"); return; }
                if (!HasContent(b)) { AutoNote("blank"); return; }   // 纯色/黑屏（过场、独占全屏）：不判也不再费一次 OCR

                bool hit = AutoTickFixed(b, scan, ref handed);
                if (hit) return;   // 已经交给工作线程，_autoBusy 由它收尾
            }
            catch (Exception) { }
            finally
            {
                if (b != null) { try { b.Dispose(); } catch (Exception) { } }
                if (!handed) _autoBusy = false;
            }
        }

        private static void StartOcrWork(ref bool handed)
        {
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
        }

        // 固定区域模式：原来的逻辑，一点不动
        private static bool AutoTickFixed(Bitmap b, Rectangle union, ref bool handed)
        {
            byte[] gray;
            ulong h = MaskHash(b, out gray);

            // ① 画面还在动就重置稳定计时——绝不每帧跑 OCR
            List<Rectangle> rs = ActiveRegions();
            int moved = _autoLastGray == null ? 999 : MotionScore(b, union, rs);
            if (moved < 0) moved = _autoLastGray == null ? 999 : GrayDiff(gray, _autoLastGray);
            _autoLastGray = gray;
            _autoLastMoved = moved;
            if (moved > 3) { _autoStableAt = Environment.TickCount; AutoNote("moving=" + moved); return false; }

            int hold = AppConfig.GameAutoHoldMs;
            if (hold < 100) hold = 100;
            int held = unchecked(Environment.TickCount - _autoStableAt);
            if (held < hold) { AutoNote("settling " + held + "ms"); return false; }   // 还没定住够久

            // ② 图像层缓存：这一帧和上次识字的那帧几乎一样才跳过。
            // 32x24 的整区平均差会把「三行细字」摊到接近 0；缓存只认完全一样的那一帧，
            // 背景动画已经被上面的稳定判定挡掉，这里 0 不会造成额外 OCR。
            if (_autoBaseReady && _autoLastBaseGray != null && GrayDiff(gray, _autoLastBaseGray) <= 0)
            { _autoOcrSkip++; AutoNote("sameimg"); return false; }
            _autoLastBase = h;
            _autoLastBaseGray = gray;
            _autoBaseReady = true;
            if (AppConfig.GameDwellOn) _dwellAnchor = Cursor.Position;
            JadeHost.Log("[game] auto ocr trigger held=" + held + "ms moved=" + moved);
            StartOcrWork(ref handed);
            return handed;
        }

        // 自动识别区域模式：
        //   ① 用「每块选区自己的指纹」判抖动（整屏 32x24 的均值会被背景和 UI 稀释，判不出字幕换句）
        //   ② 画面定住够久才重新检测（整屏检测 25~60ms，不能每帧跑）
        //   ③ 检测结果刷新槽位：新块/挪位/换字都标脏，交给下面同一套 OCR→翻译→贴遮罩

        // 本软件窗口自己要是不小心压在画面上，它的按钮/文字也会被认成字幕区。
        // 游戏模式下窗口不隐藏，所以这块必须剔掉：窗口可见时，落在它范围内的块直接丢。
        private static Rectangle OwnCover()
        {
            try
            {
                IntPtr h = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (h == IntPtr.Zero) return Rectangle.Empty;
                if (IsIconic(h)) return Rectangle.Empty;
                Rectangle r = WndRect(h);
                if (r.Width < 200 || r.Height < 120) return Rectangle.Empty;
                return r;
            }
            catch (Exception) { return Rectangle.Empty; }
        }

        // 有固定区域时：只保留中心点落在某个固定区域内的检测块，区域外的 UI/背景文字一律不翻译。

        // 文本指纹：同样的几句就不重复调翻译接口。
        // 必须做归一化——OCR 同一句两次常出现空格、标点、大小写抖动，
        // 用原样字符串比会全部判成「新文本」，缓存等于没有
        // 文本「算没算变」：OCR 每轮都会抖一两个字（尤其小字号、半透明背景），
        // 严格相等会让「字幕其实没变」被判成换句，于是遮罩被收掉再重画——看起来就是闪。
        // 这里允许小差异：长度差超过 1/6 直接算不同，否则编辑距离 ≤ 长度 1/8 就算同一句。
        private static bool SameText(string a, string b)
        {
            // 统一走 TextSimilar：短句严格、长段按 3-gram 覆盖率放宽，
            // 免得整段文字因为 OCR 重排/重复被判成「换了」而反复重译。
            return TextSimilar(a, b);
        }

        private static List<OcrSegment> CopySegs(List<OcrSegment> segs)
        {
            List<OcrSegment> res = new List<OcrSegment>();
            if (segs == null) return res;
            for (int i = 0; i < segs.Count; i++)
            {
                OcrSegment s = segs[i];
                OcrSegment c = new OcrSegment();
                c.Text = s.Text;
                c.Box = s.Box;
                c.Angle = s.Angle;
                res.Add(c);
            }
            return res;
        }

        // 逐行比：把 3 行拼成一个 key 再用 3-gram 相似度，改一行也会被另外两行“稀释”成没变，
        // 旧译文就一直贴在屏上、新原文从旧遮罩右边漏出来。这里按行数 + 每行文本分别比。
        // 段落块可能把多行并进一个 OcrSegment（text 里带换行），所以先摊平成「逻辑行」。
        private static List<string> TextLines(List<OcrSegment> segs)
        {
            List<string> res = new List<string>();
            if (segs == null) return res;
            for (int i = 0; i < segs.Count; i++)
            {
                string t = segs[i] == null ? null : segs[i].Text;
                if (string.IsNullOrEmpty(t)) continue;
                string[] parts = t.Replace("\r", "\n").Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int k = 0; k < parts.Length; k++)
                {
                    string s = parts[k].Trim();
                    if (s.Length > 0) res.Add(s);
                }
            }
            return res;
        }

        private static bool SameLines(List<OcrSegment> a, List<OcrSegment> b)
        {
            List<string> la = TextLines(a), lb = TextLines(b);
            if (la.Count != lb.Count) return false;
            for (int i = 0; i < la.Count; i++)
                if (!TextSimilar(la[i], lb[i])) return false;
            return true;
        }

        private static int EditDistance(string a, string b)
        {
            if (a == b) return 0;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            int[] prev = new int[b.Length + 1];
            int[] cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                char ca = a[i - 1];
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = ca == b[j - 1] ? 0 : 1;
                    int v = prev[j - 1] + cost;
                    if (prev[j] + 1 < v) v = prev[j] + 1;
                    if (cur[j - 1] + 1 < v) v = cur[j - 1] + 1;
                    cur[j] = v;
                }
                int[] tmp = prev; prev = cur; cur = tmp;
            }
            return prev[b.Length];
        }

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
            if (!AppConfig.GameOn || _paused || _overlayClosed || !TranslateActive) { StopMaskWatch(); return; }
            if (!TransOverlay.GameShowing) { StopMaskWatch(); return; }
            if (_maskClosing) return;
                List<Rectangle> rs = ActiveRegions();
            if (rs.Count == 0) { StopMaskWatch(); return; }
            Rectangle union = Union(rs);
            if (union.Width < 16 || union.Height < 12) return;

            // ① 窗口守护：每 0.5s 只查句柄和窗口矩形，不抓图，所以完全不闪。
            // 选区底下换成别的窗口（游戏被切走/最小化）、游戏窗口被移走或缩放 → 直接收掉
            string why;
            if (WindowGone(union, out why)) { CloseMask(why); return; }
            // 鼠标停稳才扫描同样适用于「遮罩已经贴在屏上」的阶段：鼠标还在动时不抓图，
            // 既省 OCR 也避免边玩边疯狂重扫。这里只要求「停住」，不要求「移动过 30px」。
            if (AppConfig.GameDwellOn && !DwellStill()) return;

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

                // 按区比对：像素判据只负责「提请确认」，不再直接收遮罩。
                // 背景动画/光影/粒子都会让哈希变化，但背景变了不等于字幕变了——
                // 真正该收遮罩的判据是 OCR 后的文字：文字一致就只刷新背景基准，遮罩留着、不重翻。
                bool zoneHit = false;
                for (int i = 0; i < rs.Count && i < _zoneBase.Count; i++)
                {
                    byte[] zg = null;
                    ulong zh = MaskHashOf(b, union, rs[i], out zg);
                    if (zg == null) continue;
                    int zd = Hamming(zh, _zoneBase[i]);
                    // ROI 关掉时没有 RoiGray 基线，但 128x48 的选区指纹还能用：
                    // 一行字幕换掉大约差 2%~8%，比 32x24 哈希灵敏得多。
                    int zsig = -1;
                    if (i < _slots.Count && _slots[i].Sig != null)
                    {
                        byte[] nowSig = SignatureOne(b, union, rs[i]);
                        if (nowSig != null) zsig = (int)(SignatureDiff(_slots[i].Sig, nowSig) * 100);
                    }
                    // 高分辨率兜底：32x24 的哈希里一行字幕只占两三像素，改一整句也可能只差 1 位；
                    // 用 SlotGray 的 160x90 采样直接数变化像素，字幕换一行会命中上百个点。
                    int zpx = -1;
                    if (i < _slots.Count && _slots[i].RoiGray != null)
                    {
                        byte[] g2 = SlotGray(b, union, rs[i]);
                        if (g2 != null && g2.Length == _slots[i].RoiGray.Length)
                        {
                            zpx = 0;
                            for (int k = 0; k < g2.Length; k++)
                            {
                                int d = g2[k] - _slots[i].RoiGray[k];
                                if (d < 0) d = -d;
                                if (d >= 12) zpx++;
                            }
                        }
                    }
                    if (zpx >= 80 || (zpx < 0 && zsig >= 2) || zd >= 3)
                    {
                        JadeHost.Log("[game] zone " + (i + 1) + " pixel-change dist=" + zd + " px=" + zpx + " sig=" + zsig + "（待重扫）");
                        zoneHit = true;
                        break;
                    }
                }

                bool unionHit = (dist >= 18 || diff >= 22 || ratio >= 16 || chan >= 75 || block >= 100 || row >= 100);
                if (unionHit)
                    JadeHost.Log("[game] watch pixel-change（待文字确认） dist=" + dist + " diff=" + diff + " ratio=" + ratio
                        + " chan=" + chan + " block=" + block + " row=" + row);

                // 手动模式：不做文字复核。按一次快捷键看的就是这一帧，画面一变遮罩立刻走人——
                // 既满足「画面变化遮罩就消失」，也省掉每半秒一次、专门用来验证文字没变的多余 OCR。
                if (!AppConfig.GameAutoMode)
                {
                    if (zoneHit || unionHit) CloseMask("画面已变化（手动模式）");
                    return;
                }

                // 自动模式：画面有变化只让主链路重扫，遮罩原地不动。
                // 以前这里先去跑 OCR 复核、再 CloseMask 整层收掉、400ms 后重新贴，
                // 所以画面不动也会看到「消失—出现」；现在文字由 DoTick 原子替换，窗口常驻。
                if (!zoneHit && !unionHit) return;

                int nowTick = Environment.TickCount;
                int confirmGap = AppConfig.GameMaskConfirmMs;
                if (confirmGap > 500) confirmGap = 500;   // 遮罩期重扫上限 0.5s：局部 ROI 很便宜，不必等到 1~3s
                if (confirmGap < 250) confirmGap = 250;
                if (_maskConfirmAt != 0 && unchecked(nowTick - _maskConfirmAt) < confirmGap) return;
                _maskConfirmAt = nowTick;
                // 先把基准换成当前帧：否则同一处变化会在下一轮反复触发重扫
                _maskBase = h;
                _maskBaseGray = gray;
                _zoneBase = ZoneHashes(b, union, rs);
                _maskMiss = 0;
                _maskMissKey = null;
                _dwellAnchor = Cursor.Position;
                JadeHost.Log("[game] auto pixel-change → rescan（遮罩保留） dist=" + dist + " diff=" + diff);
                _allowWhileShowing = true;
                try { Trigger(false); }
                finally { _allowWhileShowing = false; }
            }
            catch (Exception) { }
            finally { b.Dispose(); }
        }

        // OCR 复核说「文字变了」之后，要连读两次同一段新文字才认账：
        // 单次 OCR 的抖动每次都不一样，只按次数计数会把不同的噪声累加成一次误关（遮罩闪一下再贴回来）。
        private static bool MaskChangeConfirmed(string key)
        {
            string k = key == null ? "" : key;
            if (_maskMissKey == null || !TextSimilar(_maskMissKey, k))
            {
                _maskMissKey = k;
                _maskMiss = 1;
                return false;
            }
            _maskMiss++;
            if (_maskMiss < 2) return false;
            _maskMiss = 0;
            _maskMissKey = null;
            return true;
        }

        // 遮罩期的「文字确认」：像素比对说画面变了之后，用 OCR 复核文字本身。
        // true = 各区域文字和上一句一致（或一致地没字）→ 遮罩该留；
        // false = 字幕真的换句/消失/新出现 → 收了重翻（真正收之前还会过一遍 MaskChangeConfirmed）。
        // 注意：必须自己抓一帧「屏幕坐标」的图。MaskTick 里那张 b 可能是窗口自绘图，
        // 原点和尺寸都不是屏幕坐标，拿它按选区裁只会裁空，于是每轮都判「文字消失」——这正是遮罩反复消失的原因。
        // 识不出来（抓图失败、OCR 失败）一律按「没变」处理：宁可多留一会，不乱撤遮罩。
        private static bool MaskTextUnchanged(Rectangle union, List<Rectangle> rs, out string changeKey)
        {
            changeKey = null;
            Bitmap fresh = null;
            bool dbgHid = BlockDebug.SuspendForCapture();
            try { fresh = TransOverlay.GameShowing ? CaptureUnderOverlay(union) : Capture(union); }
            catch (Exception ex) { JadeHost.Log("[game] watch confirm capture fail " + ex.Message); return true; }
            finally { if (dbgHid) BlockDebug.ResumeAfterCapture(); }
            if (fresh == null) { JadeHost.Log("[game] watch confirm capture null（保留遮罩）"); return true; }
            try
            {
                return MaskTextUnchangedCore(fresh, union, rs, out changeKey);
            }
            finally { try { fresh.Dispose(); } catch (Exception) { } }
        }

        private static bool MaskTextUnchangedCore(Bitmap b, Rectangle union, List<Rectangle> rs, out string changeKey)
        {
            changeKey = null;
            // 固定区域模式：槽位就是用户画的那几个区域
            List<RegionSlot> slots = new List<RegionSlot>();
            for (int i = 0; i < _slots.Count; i++) slots.Add(_slots[i]);
            for (int i = 0; i < rs.Count; i++)
            {
                List<OcrSegment> segs;
                try { segs = OcrRegion(b, union, rs[i], false); }
                catch (Exception ex) { JadeHost.Log("[game] watch ocr fail " + ex.Message); return true; }
                if (segs == null) return true;
                try { segs = GameFilter.KeepDialogue(segs, union, rs); } catch (Exception) { }
                // 过滤链和 DoTick 保持一致，否则同一个字在两边归一化结果不同，会误判成「文字变了」
                if (AppConfig.GameFontFilter) { try { segs = GameFilter.DropSmallText(segs); } catch (Exception) { } }
                if (AppConfig.GameMerge) { try { segs = GameFilter.MergeLines(segs, union, rs); } catch (Exception) { } }

                List<OcrSegment> mine = new List<OcrSegment>();
                // OcrRegion 回来的是「union 相对坐标」，这里必须换算成 union 坐标再筛，
                // 直接用屏幕坐标会把所有行都筛掉，表现就是每轮都判「文字消失」。
                Rectangle rr = new Rectangle(rs[i].X - union.X, rs[i].Y - union.Y, rs[i].Width, rs[i].Height);
                rr = Rectangle.Inflate(rr, 10, 10);
                for (int k = 0; k < segs.Count; k++)
                {
                    OcrSegment s = segs[k];
                    int cx = s.Box.Left + s.Box.Width / 2;
                    int cy = s.Box.Top + s.Box.Height / 2;
                    if (rr.Contains(cx, cy)) mine.Add(s);
                }
                int chars = 0;
                for (int k = 0; k < mine.Count; k++) chars += mine[k].Text.Length;
                string tkey = TextKey(mine);

                RegionSlot sl = null;
                if (slots != null)
                {
                    for (int k = 0; k < slots.Count; k++)
                    {
                        RegionSlot c = slots[k];
                        if (c.R == rs[i] || c.ShownRect == rs[i]) { sl = c; break; }
                    }
                }
                if (sl != null)
                {
                    string last = sl.LastText ?? "";
                    // 只有「真的贴过译文」的区域才有资格否决：没翻译的（中文 UI、跳过的选区）
                    // 本来就不该有遮罩，拿它的像素变化去收别人的遮罩，正是「背景一动整片闪」的来源。
                    if (last.Length == 0 || sl.ShownRect.IsEmpty || sl.Lines <= 0) continue;
                    // 抓图时遮罩没掐干净 → OCR 读到的是这块自己的译文。这种情况一律按「没变」处理，
                    // 否则每轮都被自己的译文判成「换句」，遮罩就消失一下又贴回来。
                    string shownTr = TransOverlay.ZoneText(sl.Zid);
                    if (shownTr.Length > 0 && TextSimilar(tkey, shownTr))
                    {
                        JadeHost.Log("[game] watch zone " + (i + 1) + " 读到的是译文本身（底图没抓干净），按没变处理");
                        continue;
                    }
                    if (chars < 2)
                    {
                        changeKey = "";
                        JadeHost.Log("[game] watch zone " + (i + 1) + " 文字消失");
                        return false;
                    }
                    if (!SameLines(mine, sl.PrevSegs))
                    {
                        changeKey = tkey;
                        JadeHost.Log("[game] watch zone " + (i + 1) + " 文字确实变了 last=" + last + " now=" + tkey);
                        return false;
                    }
                }
                else
                {
                    // 没有逐槽原文：拿整句指纹粗比（DoTick 每轮都会更新 _autoLastText）
                    if (chars < 2 && !string.IsNullOrEmpty(_autoLastText)) { changeKey = ""; return false; }
                    if (chars >= 2 && tkey.Length > 0 && !string.IsNullOrEmpty(_autoLastText)
                        && _autoLastText.IndexOf(tkey, StringComparison.Ordinal) < 0
                        && tkey.IndexOf(_autoLastText, StringComparison.Ordinal) < 0)
                    {
                        changeKey = tkey;
                        return false;
                    }
                }
            }
            return true;
        }

        // 文字确认用的模糊比较：OCR 对同一句常会多认/少认一两个字符（尾部的 ]、按 ESC 被拆成两段），
        // 逐字相等会把「字幕没换」误判成「换句了」。分两档：
        // - 短句（一句字幕）：LCS 80% 兜一两个字的噪声；
        // - 长段（整段对话/整屏文字）：OCR 会重排、重复、漏字，逐字 LCS 必然掉下来，
        //   改看 3-gram 覆盖率，大部分三字片段还在就算同一段话。
        private static bool TextSimilar(string a, string b)
        {
            if (a == null) a = "";
            if (b == null) b = "";
            if (a.Length == 0 && b.Length == 0) return true;
            if (a.Length == 0 || b.Length == 0) return false;
            string na = TextKeyNorm(a), nb = TextKeyNorm(b);
            if (na.Length == 0 && nb.Length == 0) return true;
            if (na == nb) return true;
            int max = Math.Max(na.Length, nb.Length), min = Math.Min(na.Length, nb.Length);
            if (max == 0) return true;

            if (max <= 40)
            {
                int lcs = LcsLen(na, nb);
                return lcs >= (int)Math.Floor(max * 0.8) && (max - min) <= Math.Max(3, max / 4);
            }

            if (min < max * 0.55) return false;
            return ShingleCover(na, nb) >= 0.55 || ShingleCover(nb, na) >= 0.55;
        }

        private static int LcsLen(string a, string b)
        {
            int[,] dp = new int[a.Length + 1, b.Length + 1];
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    dp[i, j] = a[i - 1] == b[j - 1]
                        ? dp[i - 1, j - 1] + 1
                        : Math.Max(dp[i - 1, j], dp[i, j - 1]);
            return dp[a.Length, b.Length];
        }

        // from 的 3-gram 有多少出现在 to 里。OCR 重复/重排时原句片段仍大多健在，
        // 换了一句话则基本对不上，比逐字 LCS 稳得多，也不用 O(n*m) 的大表。
        private static double ShingleCover(string from, string to)
        {
            if (from.Length < 3) return from == to ? 1.0 : 0.0;
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i + 3 <= to.Length; i++) set.Add(to.Substring(i, 3));
            int n = 0, hit = 0;
            for (int i = 0; i + 3 <= from.Length; i++)
            {
                n++;
                if (set.Contains(from.Substring(i, 3))) hit++;
            }
            return n == 0 ? 0.0 : (double)hit / n;
        }

        private static string TextKeyNorm(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = char.ToLowerInvariant(s[i]);
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
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
                // 首选窗口自绘：拿到的就是游戏自己的当前帧，遮罩压在屏幕上也不影响，
                // 既不会把译文读成原文，也不用 Hide/Show 掐遮罩（不闪）。
                Bitmap pw = CaptureWindowPrint(_maskHwnd, union);
                if (pw != null)
                {
                    fromWin = true;
                    LogCapSource("print");
                    return pw;
                }
                Bitmap w = CaptureWindowPart(_maskHwnd, union);
                if (w != null) { fromWin = true; LogCapSource("screen"); return w; }
            }
            Bitmap b = CaptureUnderOverlay(union);
            if (b != null) { fromWin = true; LogCapSource("hidecap"); return b; }
            return CaptureBand(union);
        }

        private static long _capLogAt;
        private static string _capLast;
        private static void LogCapSource(string src)
        {
            long now = (long)Environment.TickCount;
            if (_capLast == src && now - _capLogAt < 5000) return;
            _capLast = src;
            _capLogAt = now;
            JadeHost.Log("[game] capture src=" + src);
        }

        // PrintWindow：直接从窗口取原始内容，遮罩是另一个分层窗口，不会混进来。
        // 独占全屏 / GPU 直出窗口常返回纯色，用 HasContent 判掉后走屏幕抓图兜底。
        private static Bitmap CaptureWindowPrint(IntPtr hwnd, Rectangle union)
        {
            if (hwnd == IntPtr.Zero) return null;
            Rectangle wr = WndRect(hwnd);
            if (wr.Width < 40 || wr.Height < 40 || wr.Width > 8000 || wr.Height > 8000) return null;
            // 遮罩窗口在重绘的瞬间，PrintWindow 偶尔会回一帧空白；重试两次再判失败，
            // 避免掉进「掐遮罩 8ms」的兜底路径（那一下就是肉眼看到的闪）。
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Bitmap full = null;
                try
                {
                    full = new Bitmap(wr.Width, wr.Height, PixelFormat.Format24bppRgb);
                    bool ok = false;
                    using (Graphics g = Graphics.FromImage(full))
                    {
                        IntPtr dc = g.GetHdc();
                        try
                        {
                            // 2 = PW_RENDERFULLCONTENT（Win10 起对 DirectComposition/Chromium 更有效）
                            ok = PrintWindow(hwnd, dc, 2);
                            if (!ok) ok = PrintWindow(hwnd, dc, 0);
                        }
                        finally { g.ReleaseHdc(dc); }
                    }
                    if (ok)
                    {
                        Rectangle cut = new Rectangle(union.Left - wr.Left, union.Top - wr.Top, union.Width, union.Height);
                        cut.Intersect(new Rectangle(0, 0, full.Width, full.Height));
                        if (cut.Width >= 16 && cut.Height >= 12)
                        {
                            Bitmap part = full.Clone(cut, PixelFormat.Format24bppRgb);
                            if (part != null)
                            {
                                if (HasContent(part)) return part;
                                part.Dispose();
                            }
                        }
                    }
                }
                catch (Exception) { }
                finally { if (full != null) { try { full.Dispose(); } catch (Exception) { } } }
                Thread.Sleep(12);
            }
            return null;
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
                // 窗口超出屏幕时只抓可见部分，避免整张 CopyFromScreen 报「句柄无效」
                Rectangle vr = Rectangle.Intersect(wr, SystemInformation.VirtualScreen);
                if (vr.Width < 4 || vr.Height < 4) return null;
                using (Graphics g = Graphics.FromImage(full))
                {
                    g.CopyFromScreen(vr.Left, vr.Top, vr.Left - wr.Left, vr.Top - wr.Top,
                        vr.Size, CopyPixelOperation.SourceCopy);
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
                List<Rectangle> rs = ActiveRegions();
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
                // 只有自适应模式才需要接着跑；手动模式收掉遮罩就是「这一帧看完了」，等下一次按键
                if (AppConfig.GameAutoMode) StartAutoResume();
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
                List<Rectangle> rs = ActiveRegions();
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
            // 翻译服务没启动时，自动链路（定时器/定住采样）一律不进入 OCR/翻译。
            // force=true 是用户主动按「手动翻译一次」，属于明确动作，不受这个总开关限制。
            if (!TranslateActive && !force)
            {
                JadeHost.Log("[game] skip translate-off");
                return;
            }
            // 遮罩已经在屏上、遮罩期低频比对（MaskTick）也在跑时，定时触发的这一轮 OCR 是多余的：
            // DoTick 抓图前要把所有遮罩隐藏约 34ms，每 250ms 藏一次就是肉眼可见的闪屏。
            // 字幕换句/消失由 MaskTick 的 OCR 复核负责，确认变了会先收罩，再交给 AutoTick 重新识字。
            if (!force && !_allowWhileShowing && (TransOverlay.GameShowing || TransOverlay.Sampling) && _maskWatch != null)
            {
                JadeHost.Log("[game] skip timer tick: mask watch active");
                return;
            }
            if (_busy)
            {
                if (!force)
                {
                    // 单槽 latest-wins：正在 OCR/翻译时又来新变化，不堆任务，只记一次「跑完补扫」。
                    _pendingRun = true;
                    JadeHost.Log("[game] pending merge (busy)");
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
                _pendingRun = false;
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
                if (_pendingRun)
                {
                    _pendingRun = false;
                    Thread th = new Thread(delegate()
                    {
                        try { Thread.Sleep(40); _allowWhileShowing = true; Trigger(false); }
                        catch (Exception) { }
                        finally { _allowWhileShowing = false; }
                    });
                    th.IsBackground = true;
                    th.Start();
                    JadeHost.Log("[game] pending rescan kicked");
                }
            }
        }

        // 「这块译文还能不能原地留着」的唯一判据：窗口真的在屏上。
        // 不在屏上就立刻用上一轮的内容重新铺一次；窗口已经拆掉才返回 false，让调用方重翻。
        // 自动识别区域：框挪了但原文没变时，把旧遮罩整体搬过去（不重新翻译、不重新 OCR）。
        private static bool KeepZoneOnScreen(RegionSlot sl, int no)
        {
            try
            {
                if (TransOverlay.IsZoneShowing(sl.Zid))
                {
                    if (sl.ShownRect.Width > 0 && sl.ShownRect.Height > 0 && sl.ShownRect != sl.R)
                    {
                        TransOverlay.MoveZone(sl.Zid, sl.R);
                        sl.ShownRect = sl.R;
                        JadeHost.Log("[game] zone " + no + " move rect（原文没变，只搬遮罩）");
                    }
                    return true;
                }
                if (TransOverlay.ReshowZone(sl.Zid))
                {
                    if (sl.ShownRect != sl.R)
                    {
                        TransOverlay.MoveZone(sl.Zid, sl.R);
                        sl.ShownRect = sl.R;
                    }
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
            long tStart = (long)Environment.TickCount;
            long tCap = 0, tOcr = 0, tMt = 0;
            List<Rectangle> regions = ActiveRegions();
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
            // 分块框在屏上时先藏一下再抓：框线和编号会被 OCR 当成正文
            bool dbgHid = BlockDebug.SuspendForCapture();
            Bitmap bmp = null;
            string capSrc = "screen";
            try
            {
                if (TransOverlay.IsShowing)
                {
                    // 遮罩在屏上时优先 PrintWindow 取原始窗口，完全不闪；失败才退回「掐遮罩 8ms」
                    bmp = CaptureWindowPrint(_maskHwnd, union);
                    if (bmp != null) capSrc = "print";
                    else { bmp = CaptureUnderOverlay(union); capSrc = "hidecap"; }
                }
                else bmp = Capture(union);
            }
            finally { if (dbgHid) BlockDebug.ResumeAfterCapture(); }
            if (bmp == null) return;
            tCap = (long)Environment.TickCount - tStart;
            JadeHost.Log("[game] tick captured src=" + capSrc);
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
            // 槽位跟着配置里的固定选区重排；遮罩编号沿用上一轮，不整片重贴
            SyncSlots(regions);
            int dirtyCount = 0;
            bool forceAll = force || _forceRepaint;
            for (int i = 0; i < _slots.Count; i++)
            {
                RegionSlot sl = _slots[i];
                byte[] cur = SignatureOne(bmp, union, sl.R);
                double rd = sl.Sig == null ? 1.0 : SignatureDiff(sl.Sig, cur);
                sl.Sig = cur;
                bool full = true;
                List<Rectangle> bands = null;
                byte[] grayNew = null;
                if (!forceAll && sl.HasSegs && AppConfig.GameRoi)
                {
                    // 动态 ROI：只在变化的那几条横带里识字，没变的部分沿用上一轮结果。
                    // 返回 null = 整区都变了，退回整区 OCR；返回空表 = 这一区没变化。
                    bands = ChangeBands(bmp, union, sl, out grayNew);
                    if (bands != null && bands.Count == 0)
                    {
                        sl.Dirty = false;
                        sl.RoiBands = null;
                        sl.RoiNew = null;
                        sl.DirtyStreak = 0;
                        JadeHost.Log("[game] region " + (i + 1) + "/" + _slots.Count
                            + " clean (roi) diff=" + rd.ToString("0.000"));
                        continue;
                    }
                    full = bands == null;
                }
                else
                {
                    grayNew = SlotGray(bmp, union, sl.R);
                }
                if (rd >= threshold) sl.DirtyStreak++; else sl.DirtyStreak = 0;
                sl.Dirty = true;
                sl.RoiBands = bands;
                sl.RoiNew = grayNew;
                sl.RoiFull = full;
                dirtyCount++;
                JadeHost.Log("[game] region " + (i + 1) + "/" + _slots.Count + " dirty"
                    + (full ? " full" : " bands=" + (bands == null ? 0 : bands.Count))
                    + " diff=" + rd.ToString("0.000") + " streak=" + sl.DirtyStreak);
            }
            JadeHost.Log("[game] diff=" + diff.ToString("0.000") + " threshold=" + threshold.ToString("0.000")
                + " dirty=" + dirtyCount + "/" + _slots.Count + " excluded=" + (excluded ? 1 : 0)
                + (_forceRepaint ? " repaint=all" : ""));
            _forceRepaint = false;

            try
            {
                List<OcrSegment> segs = new List<OcrSegment>();
                List<Thread> ths = new List<Thread>();
                List<Bitmap> cropBufs = new List<Bitmap>();

                // 整区识字：每个固定区域整个裁出来跑一次 OCR，识别到几行就是几行，没有块数上限。
                // 每轮都识字（不是只识脏块）：判定要以「这一轮真实的文字」为准，
                // 拿上一轮的旧结论去猜「可能没变」，换页/切 tab 时就会把新画面当成没变、旧译文一直贴在上面。
                for (int i = 0; i < _slots.Count; i++)
                {
                    RegionSlot sl = _slots[i];
                    RegionSlot cur = sl;
                    int idx = i;
                    if (!sl.Dirty)
                    {
                        // 没变化的区不识字，直接沿用上一轮 Segs（动态 ROI 的省调用核心）
                        continue;
                    }
                    // 复制图必须在这个（主）线程里做：GDI+ 的 Bitmap 不能几个线程同时读
                    Bitmap mine = CropAt(bmp, 0, 0, bmp.Width, bmp.Height);
                    Bitmap forTh = mine;
                    cropBufs.Add(mine);
                    bool partial = !sl.RoiFull && sl.RoiBands != null && sl.RoiBands.Count > 0;
                    Thread th = new Thread(delegate()
                    {
                        if (partial) DoOcrSlotBands(cur, forTh, union, idx, _slots.Count, cur.RoiBands);
                        else DoOcrSlot(cur, forTh, union, idx, _slots.Count);
                    });
                    th.IsBackground = true;
                    th.Priority = System.Threading.ThreadPriority.BelowNormal;
                    th.Start();
                    ths.Add(th);
                }
                for (int i = 0; i < ths.Count; i++)
                {
                    try { ths[i].Join(20000); } catch (Exception) { }   // 单块卡死也不能把整轮拖住
                }
                for (int i = 0; i < cropBufs.Count; i++)
                {
                    try { if (cropBufs[i] != null) cropBufs[i].Dispose(); } catch (Exception) { }
                }
                tOcr = (long)Environment.TickCount - tStart - tCap;
                for (int i = 0; i < _slots.Count; i++)
                {
                    RegionSlot sl = _slots[i];
                    // OCR 已接受这一帧：把采样换成新基线，下一轮只认之后的像素变化。
                    // 不限于 dirty 块：干净块这一轮也跟着抓了新帧，背景动画累积会让旧基线越来越偏。
                    if (sl.RoiNew != null) sl.RoiGray = sl.RoiNew;
                    sl.RoiBands = null;
                    sl.RoiNew = null;
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
                // 再按位置并成段落块：翻译单元从「一行」变成「一块」，块之间本来就并发
                if (AppConfig.GameBlock)
                {
                    int before = segs.Count;
                    segs = GameFilter.GroupBlocks(segs, union, regions);
                    if (before != segs.Count)
                        JadeHost.Log("[game] blocks lines=" + before + " -> " + segs.Count);
                }

                if (gen != _gen) return;

                // —— 过滤后按选区拆开：每一块只认自己范围内的行 ——
                SplitByRegion(segs, union, _slots);
                // 调试：把这一轮最终的分块结果画在屏幕上（粗框=块，细虚线=块内原行）
                if (AppConfig.GameBlockDebug) BlockDebug.Show(union, segs);
                // 文字层判定：像素指纹只负责「把这一轮叫醒」，真正决定要不要重译重贴的是文字本身。
                // 背景在变色、UI 在做动画，但字一个没动 → 不算变化，遮罩原样留着，一个字都不重画。
                int textChanged = 0;
                for (int i = 0; i < _slots.Count; i++)
                {
                    RegionSlot sl = _slots[i];
                    string nowKey = TextKey(sl.Segs == null ? new List<OcrSegment>() : sl.Segs);
                    if (!SameLines(sl.Segs, sl.PrevSegs))
                    {
                        sl.Dirty = true;
                        textChanged++;
                        JadeHost.Log("[game] region " + (i + 1) + "/" + _slots.Count + " text changed");
                    }
                    if (sl.Dirty) sl.HasSegs = sl.Segs != null && sl.Segs.Count > 0;
                }
                if (textChanged > 0)
                    JadeHost.Log("[game] text changed blocks=" + textChanged + "/" + _slots.Count);

                // —— 一轮只问一次模型：本轮所有要重翻的分区文本合成一批 ——
                // 逐块串行翻会让遮罩一块一块往外冒；整批翻好后各块只差构建遮罩的时间，看起来是一起出现。
                List<OcrSegment> preBatch = new List<OcrSegment>();
                for (int i = 0; i < _slots.Count; i++)
                {
                    RegionSlot psl = _slots[i];
                    if (!psl.Dirty) continue;
                    if (psl.Segs == null || psl.Segs.Count == 0) continue;
                    preBatch.AddRange(psl.Segs);
                }
                int preHit = 0;
                if (preBatch.Count > 0 && !_overlayClosed)
                {
                    try { preHit = JadeHost.PreTranslateGameZones(preBatch); }
                    catch (Exception exp) { JadeHost.Log("[game] pretranslate fail " + exp.Message); }
                    JadeHost.Log("[game] pretranslate lines=" + preBatch.Count + " hit=" + preHit
                        + " blocks=" + _slots.Count);
                }

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
                    // 原文换了也不再先 HideZone：旧译文留在屏上，等新译文回来后由 ShowZone
                    // 原文换了也不再先 HideZone：旧译文留在屏上，等新译文回来后由 ShowZone
                    // 在同一次绘制里原子替换（清空+RenderAll 是一次窗口更新，不会出现空白帧）。
                    // 以前先撤再贴，用户看到的就是「文字消失一下又出现」。
                    if (!force && ztk.Length > 0 && SameLines(zs, sl.PrevSegs) && sl.Lines > 0)
                    {
                        // 原文没变（或只抖了一两个字）：遮罩在屏上就原样留着，一个字都不重画；
                        // 遮罩已经不在屏上了才把上一轮的译文铺回去。
                        if (KeepZoneOnScreen(sl, i + 1)) { totalLines += sl.Lines; continue; }
                    }
                    sl.LastText = ztk;
                    if (gen != _gen) { JadeHost.Log("[game] zone " + (i + 1) + " abort gen=" + gen + " now=" + _gen); return; }

                    // 坐标从「相对并集」转成「相对这一块」，抓图也只裁这一块。
                    // 但检测块可能比实际字形小一圈（或补边让 OCR 看到了框外的行），
                    // 只按块建窗会把行切掉、遮罩就贴不到文字上。所以窗口取「块 ∪ 行的实际范围」。
                    Rectangle show = sl.R;
                    Rectangle rowsRel = UnionOfBoxes(zs);
                    if (rowsRel.Width > 0 && rowsRel.Height > 0)
                    {
                        Rectangle rowsAbs = new Rectangle(rowsRel.Left + union.Left, rowsRel.Top + union.Top,
                            rowsRel.Width, rowsRel.Height);
                        int pad = Math.Max(2, Math.Min(6, rowsAbs.Height / 10));
                        Rectangle rowsPad = Rectangle.Inflate(rowsAbs, pad, pad);
                        show = Rectangle.Union(sl.R, rowsPad);
                    }
                    show.Intersect(new Rectangle(union.Left, union.Top, bmp.Width, bmp.Height));
                    if (show.Width < 8 || show.Height < 6) show = sl.R;
                    int dx = show.Left - union.Left, dy = show.Top - union.Top;
                    List<OcrSegment> rel = ShiftSegs(zs, dx, dy);
                    Bitmap zcrop = CropAt(bmp, dx, dy, show.Width, show.Height);
                    int lines = 0;
                    try
                    {
                        if (!_overlayClosed && zcrop != null)
                            lines = JadeHost.GameTranslate(zcrop, show, rel, sl.Zid);
                    }
                    catch (Exception exz)
                    {
                        JadeHost.Log("[game] zone " + (i + 1) + " translate fail " + exz.Message);
                    }
                    finally { if (zcrop != null) { try { zcrop.Dispose(); } catch (Exception) { } } }
                    sl.Lines = lines;
                    sl.ShownRect = show;   // 记录遮罩真正贴屏的矩形，下一轮原文没变就只搬不重翻
                    totalLines += lines;
                }

                if (gen != _gen) return;
                tMt = (long)Environment.TickCount - tStart - tCap - tOcr;
                JadeHost.Log("[game] latency capture=" + tCap + "ms ocr=" + tOcr + "ms mt=" + tMt
                    + "ms total=" + ((long)Environment.TickCount - tStart) + "ms"
                    + " dirty=" + dirtyCount + "/" + _slots.Count);
                // 脏标记原来是「置位后永不消除」（检测循环里脏块直接 continue 不参与重算），
                // 于是下一轮检测永远数出「全部脏」，遮罩就一直陷在整页清空-重贴的循环里闪。
                for (int i = 0; i < _slots.Count; i++)
                {
                    _slots[i].Dirty = false;
                    _slots[i].DirtyStreak = 0;
                    _slots[i].PrevSegs = CopySegs(_slots[i].Segs);
                }
                if (totalBlocks == 0)
                {
                    // 空结果退避：连续认不出字时把自动采样间隔逐步拉长（1.5x/2x/2.5x，上限 1s），
                    // 避免在菜单/黑屏/过场上每 25~80ms 空跑一次 OCR；一旦有字立刻复位。
                    if (AppConfig.GameBackoff && AppConfig.GameAutoMode)
                    {
                        _emptyStreak++;
                        int baseMs = Math.Max(250, AppConfig.GameAutoSampleMs);
                        double f = 1.0 + 0.5 * Math.Min(3, _emptyStreak);
                        int hold = (int)Math.Min(1000, baseMs * f);
                        _autoHoldUntil = (long)Environment.TickCount + hold;
                        JadeHost.Log("[game] empty backoff streak=" + _emptyStreak + " hold=" + hold + "ms");
                    }
                    JadeHost.Log("[game] no dialogue raw=" + rawCount + " rawMaxH=" + rawMaxH
                        + " dirty=" + dirtyCount + "/" + _slots.Count);
                    try { TransOverlay.HideOverlay(); } catch (Exception) { }   // 没结果就别留旧译文
                    if (AppConfig.GameAutoMode) StartAutoResume();               // 空帧也马上接着盯，别等 2 秒
                    JadeHost.GameDone("没有识别到字幕", "模型 · " + JadeHost.ModelLabel(AppConfig.GameModel), false);
                    return;
                }
                _emptyStreak = 0;
                _autoHoldUntil = 0;
                JadeHost.GameDone(totalLines > 0 ? "翻译完成" : (JadeHost.GameSkipShort ?? "没有识别到字幕"),
                    "模型 · " + JadeHost.ModelLabel(AppConfig.GameModel) + (totalLines > 0 ? " · " + totalLines + " 行" : ""),
                    totalLines > 0);
                if (_overlayClosed) { try { TransOverlay.HideOverlay(); } catch (Exception) { } }
                else if (totalLines > 0 && TransOverlay.IsShowing) StartMaskWatch();
                else if (AppConfig.GameAutoMode) StartAutoResume();   // 自适应才接着盯；手动等下一次按键
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
                    // 整页换语言（日文对话 → 英文设置项）时，日文模型读英文会吐出杂字。
                    // 这一轮已经一个假名都没有、全是拉丁字母，就别再拿日文结果当依据，
                    // 退回中文模型并重开判定，下一轮按新语言处理。
                    int c1 = 0, k1 = 0, l1 = 0, d1 = 0;
                    CountChars(sl.Segs, ref c1, ref k1, ref l1, ref d1);
                    if (k1 == 0 && l1 >= 4)
                    {
                        sl.Jp = 0;
                        sl.Segs = OcrRegion(own, union, sl.R, false);
                        JadeHost.Log("[game] region " + (idx + 1) + "/" + total + " jp lost（换语言，重判）");
                    }
                }
            else
                {
                    sl.Segs = OcrRegion(own, union, sl.R, false);
                    // 原来靠 LooksJpCandidate 从「中文模型的输出」里找日文特征——可中文模型
                    // 压根吐不出假名（「くな」被认成「<本」），这个门永远不开，
                    // 日文模型一次都用不上，字幕就被当成中文跳过翻译了。
                    // 改成反过来：有日文模型就直接拿它试一遍，再看输出里假名够不够。
                    // 另外「判过一次就锁死」也不行：换页后同一块位置的语言会整个换掉，
                    // 所以中文模型再怎么读都出不了假名、但汉字很多（典型是日文被硬映射成汉字）时也复核一次。
                    int cq = 0, kq = 0, lq = 0, dq = 0;
                    CountChars(sl.Segs, ref cq, ref kq, ref lq, ref dq);
                    bool recheck = sl.Jp == 0 || (sl.Jp == 2 && cq >= 6);
                    if (recheck)
                    {
                        bool cand = LooksJpCandidate(sl.Segs);
                        bool jpTry = AppConfig.GameJpAuto && Ocr.JapanReady();
                        JadeHost.Log("[game] jp gate zone=" + (idx + 1) + " auto=" + AppConfig.GameJpAuto
                            + " ready=" + Ocr.JapanReady() + " cand=" + cand + " probe=" + jpTry
                            + " cjk=" + cq + " kana=" + kq);
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

        // 动态 ROI 的局部识字：只把变化的那几条横带送 OCR，横带外的旧行原样保留。
        // 语言已判定过的区沿用原语言模型；局部更新不做日文重判（整页换语言时覆盖率会超限，自然退回整区）。
        private static void DoOcrSlotBands(RegionSlot sl, Bitmap own, Rectangle union, int idx, int total, List<Rectangle> bands)
        {
            try
            {
                if (own == null) return;
                List<OcrSegment> fresh = new List<OcrSegment>();
                bool jp = sl.Jp == 1;
                for (int i = 0; i < bands.Count; i++)
                {
                    Rectangle b = Rectangle.Intersect(bands[i], union);
                    if (b.Width < 8 || b.Height < 6) continue;
                    List<OcrSegment> bs = OcrRegion(own, union, b, jp);
                    if (bs != null && bs.Count > 0) fresh.AddRange(bs);
                }
                List<OcrSegment> merged = MergeBandSegs(sl.Segs, fresh, bands, union);
                merged.Sort(delegate(OcrSegment a, OcrSegment b)
                {
                    int c = a.Box.Y.CompareTo(b.Box.Y);
                    return c != 0 ? c : a.Box.X.CompareTo(b.Box.X);
                });
                sl.Segs = merged;
                sl.HasSegs = true;
                JadeHost.Log("[game] roi zone=" + (idx + 1) + "/" + total + " bands=" + bands.Count
                    + " fresh=" + fresh.Count + " keep=" + (merged.Count - fresh.Count));
            }
            catch (Exception ex)
            {
                JadeHost.Log("[game] zone " + (idx + 1) + " roi ocr fail " + ex.Message);
            }
            finally
            {
                if (own != null) { try { own.Dispose(); } catch (Exception) { } }
            }
        }

        // 旧行里，中心落在变化横带内的删掉（由 fresh 替换），横带外的保留。
        // 横带是屏幕坐标，旧行是「相对并集」坐标，先换算再判。
        private static List<OcrSegment> MergeBandSegs(List<OcrSegment> old, List<OcrSegment> fresh, List<Rectangle> bands, Rectangle union)
        {
            List<OcrSegment> res = new List<OcrSegment>();
            if (old != null)
            {
                for (int i = 0; i < old.Count; i++)
                {
                    OcrSegment s = old[i];
                    int cx = s.Box.X + s.Box.Width / 2 + union.Left;
                    int cy = s.Box.Y + s.Box.Height / 2 + union.Top;
                    bool hit = false;
                    for (int j = 0; j < bands.Count; j++)
                    {
                        Rectangle b = bands[j];
                        b.Inflate(6, 6);
                        if (cx >= b.Left && cx <= b.Right && cy >= b.Top && cy <= b.Bottom) { hit = true; break; }
                    }
                    if (!hit) res.Add(s);
                }
            }
            if (fresh != null) res.AddRange(fresh);
            return res;
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

        // 一组行框的并集（空集返回 Width=Height=0）
        private static Rectangle UnionOfBoxes(List<OcrSegment> segs)
        {
            if (segs == null || segs.Count == 0) return Rectangle.Empty;
            Rectangle r = segs[0].Box;
            for (int i = 1; i < segs.Count; i++) r = Rectangle.Union(r, segs[i].Box);
            return r;
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

        // 选区固定采样成 160x90 灰度：动态 ROI 的比对底图。
        // 不用逐像素 GetPixel（一个区一万多次调用，采样定时器会被拖慢），走 LockBits 一次读出来。
        private const int RoiW = 160, RoiH = 90;
        private static byte[] SlotGray(Bitmap bmp, Rectangle union, Rectangle r)
        {
            byte[] data = new byte[RoiW * RoiH];
            if (bmp == null) return data;
            int sx = r.Left - union.X, sy = r.Top - union.Y;
            int sw = Math.Min(r.Width, bmp.Width - sx);
            int sh = Math.Min(r.Height, bmp.Height - sy);
            if (sw < 4 || sh < 4) return data;
            Bitmap small = null;
            BitmapData bd = null;
            try
            {
                small = new Bitmap(RoiW, RoiH, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.Clear(Color.Black);
                    g.DrawImage(bmp, new Rectangle(0, 0, RoiW, RoiH), new Rectangle(sx, sy, sw, sh), GraphicsUnit.Pixel);
                }
                bd = small.LockBits(new Rectangle(0, 0, RoiW, RoiH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                byte[] px = new byte[bd.Stride * RoiH];
                Marshal.Copy(bd.Scan0, px, 0, px.Length);
                int k = 0;
                for (int y = 0; y < RoiH; y++)
                {
                    int off = y * bd.Stride;
                    for (int x = 0; x < RoiW; x++)
                    {
                        int p = off + x * 4;
                        data[k++] = (byte)((px[p + 2] * 77 + px[p + 1] * 151 + px[p] * 28) >> 8);
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                if (bd != null) { try { small.UnlockBits(bd); } catch (Exception) { } }
                if (small != null) { try { small.Dispose(); } catch (Exception) { } }
            }
            return data;
        }

        // 动态 ROI：返回「这一轮真正变了的横带」（屏幕坐标）。
        // null = 变化太大/没有基线，退回整区 OCR；空表 = 这一区没变化，直接沿用上一轮 Segs。
        // 只看横向长条而不是任意矩形：字幕是一行行排的，横带裁出来仍然是一张正常比例的图，
        // DB 检测模型不会因为窄条而整行漏检；同时把 OCR 面积从整区压到变化的那几行。
        private static List<Rectangle> ChangeBands(Bitmap bmp, Rectangle union, RegionSlot sl, out byte[] gray)
        {
            gray = SlotGray(bmp, union, sl.R);
            List<Rectangle> empty = new List<Rectangle>();
            if (sl.RoiGray == null || sl.RoiGray.Length != gray.Length)
            {
                JadeHost.Log("[game] roi no baseline → full");
                return null;
            }
            if (sl.R.Width < 80 || sl.R.Height < 60) return null;   // 小选区没有省的必要，整区更准

            const int tw = 8, th = 4;
            int cols = (RoiW + tw - 1) / tw;          // 20
            int rows = (RoiH + th - 1) / th;          // 23
            bool[,] active = new bool[rows, cols];
            int changedTiles = 0;
            for (int ty = 0; ty < rows; ty++)
            {
                for (int tx = 0; tx < cols; tx++)
                {
                    int cnt = 0;
                    int y0 = ty * th, yEnd = Math.Min(RoiH, y0 + th);
                    int x0 = tx * tw, xEnd = Math.Min(RoiW, x0 + tw);
                    for (int y = y0; y < yEnd; y++)
                    {
                        int off = y * RoiW;
                        for (int x = x0; x < xEnd; x++)
                        {
                            int d = gray[off + x] - sl.RoiGray[off + x];
                            if (d < 0) d = -d;
                            if (d >= 12) cnt++;
                        }
                    }
                    if (cnt >= 2) { active[ty, tx] = true; changedTiles++; }
                }
            }
            if (changedTiles == 0) return empty;

            // 膨胀一格：字幕抗锯齿/轻微位移只落在边缘时，把相邻格一起带上，避免半行被切
            bool[,] dil = new bool[rows, cols];
            for (int ty = 0; ty < rows; ty++)
                for (int tx = 0; tx < cols; tx++)
                {
                    if (!active[ty, tx]) continue;
                    int y1 = Math.Max(0, ty - 1), y2 = Math.Min(rows - 1, ty + 1);
                    int x1 = Math.Max(0, tx - 1), x2 = Math.Min(cols - 1, tx + 1);
                    for (int yy = y1; yy <= y2; yy++)
                        for (int xx = x1; xx <= x2; xx++) dil[yy, xx] = true;
                }

            bool[] rowOn = new bool[rows];
            for (int ty = 0; ty < rows; ty++)
                for (int tx = 0; tx < cols; tx++) if (dil[ty, tx]) { rowOn[ty] = true; break; }

            List<int[]> spans = new List<int[]>();
            int sy = 0;
            while (sy < rows)
            {
                if (!rowOn[sy]) { sy++; continue; }
                int start = sy, end = sy, gap = 0;
                while (end + 1 < rows)
                {
                    if (rowOn[end + 1]) { end++; gap = 0; }
                    else if (gap < 1) { gap++; end++; }
                    else break;
                }
                spans.Add(new int[] { start, end });
                sy = end + 1;
            }
            if (spans.Count == 0) return empty;

            double scaleX = (double)sl.R.Width / RoiW;
            double scaleY = (double)sl.R.Height / RoiH;
            int cov = 0;
            List<Rectangle> bands = new List<Rectangle>();
            for (int i = 0; i < spans.Count; i++)
            {
                int top = spans[i][0] * th;
                int bot = Math.Min(RoiH, (spans[i][1] + 1) * th);
                // 上下各扩一行的高度当上下文，横带太薄时补到至少 36px
                int pad = Math.Max(6, (bot - top) / 2);
                top = Math.Max(0, top - pad);
                bot = Math.Min(RoiH, bot + pad);
                int y0 = sl.R.Top + (int)Math.Round(top * scaleY);
                int y1 = sl.R.Top + (int)Math.Round(bot * scaleY);
                if (y1 - y0 < 32) y1 = Math.Min(sl.R.Bottom, y0 + 32);
                cov += (y1 - y0);
                bands.Add(new Rectangle(sl.R.Left, y0, sl.R.Width, Math.Max(8, y1 - y0)));
            }
            // 变化覆盖超过 45% 或横带太多：省不了多少，整区一次更稳（也避免碎 OCR 把句子拆散）
            if (bands.Count > 6 || cov > sl.R.Height * 0.45)
            {
                JadeHost.Log("[game] roi too much bands=" + bands.Count + " cov=" + cov + "/" + sl.R.Height + " → full");
                return null;
            }
            JadeHost.Log("[game] roi bands=" + bands.Count + " cov=" + cov + "/" + sl.R.Height
                + " tiles=" + changedTiles);
            return bands;
        }

        // 单个选区的指纹（128x48 灰度，恒定长度，好和上一轮逐字节比）
        // 画面稳定判定改成高分辨率像素占比：32x24 的均值差会把三行细字摊成 1~2，
        // 结果字幕早就换了、还要干等 6 秒。160x90 上直接数「差异 >= 12」的像素占比，
        // 换一行字通常 5%~25%，背景轻微动画一般 <3%。
        private static int MotionScore(Bitmap bmp, Rectangle union, List<Rectangle> rs)
        {
            int best = -1;
            if (bmp == null || rs == null) return best;
            for (int i = 0; i < rs.Count && i < _slots.Count; i++)
            {
                RegionSlot sl = _slots[i];
                if (sl.RoiGray == null) continue;
                byte[] g = SlotGray(bmp, union, rs[i]);
                if (g == null || g.Length != sl.RoiGray.Length || g.Length == 0) continue;
                int n = 0;
                for (int k = 0; k < g.Length; k++)
                {
                    int d = g[k] - sl.RoiGray[k];
                    if (d < 0) d = -d;
                    if (d >= 12) n++;
                }
                int pct = n * 100 / g.Length;
                if (pct > best) best = pct;
            }
            return best;
        }

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
        // 游戏模式的识字入口：直接用检测+逐行识别的平铺结果，不走 LayoutBoost 的分栏清洗。
        // LayoutBoost 是给「界面截图」设计的：它会找贯穿上下的竖向空白走廊，把稀疏字幕的整块背景
        // 误判成分栏，3 行字幕会被切成 0 块；游戏字幕区域通常是纯色背景 + 少量行，直接用平坦结果更稳。
        private static List<OcrSegment> GameRecognize(Bitmap bmp, bool japan, out string log)
        {
            List<OcrSegment> segs = new List<OcrSegment>();
            log = null;
            string eng = null;
            long ms = 0;
            string det = null;
            try
            {
                // 微信 OCR 经常把多行字幕并成一句、只给一个整块框，游戏模式逐行贴译文就全糊成一行。
                // 游戏模式优先用随包附带的 PP-OCR 快速档做「检测+逐行识别」；模型不在才退回用户引擎。
                string force = japan ? AppConfig.EnginePpJapan : (string)null;
                if (!japan
                    && AppConfig.NormalizeEngine(AppConfig.Engine) == AppConfig.EngineWeChat
                    && PpOcr.FolderReady(AppConfig.ModelTierDir(AppConfig.EnginePpRapid)))
                    force = AppConfig.EnginePpRapid;
                Ocr.Recognize(bmp, out eng, out ms, out det, segs, force);
                log = det;
            }
            catch (Exception ex) { log = ex.Message; }
            return segs ?? new List<OcrSegment>();
        }

        private static List<OcrSegment> OcrRegion(Bitmap unionBmp, Rectangle union, Rectangle r, bool japan)
        {
            List<OcrSegment> segs = new List<OcrSegment>();
                List<Thread> ths = new List<Thread>();
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
                JadeHost.Log("[game] ocr in=" + ocrIn.Width + "x" + ocrIn.Height + " r=" + r.Width + "x" + r.Height
                    + " engine=" + AppConfig.Engine + " jp=" + (japan ? 1 : 0));
                if (Environment.GetEnvironmentVariable("QRSNAP_DBG_CROP") == "1")
                {
                    try { ocrIn.Save(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\qrsnap-ocrin.png", System.Drawing.Imaging.ImageFormat.Png); }
                    catch (Exception) { }
                }
                try
                {
                    segs = GameRecognize(ocrIn, japan, out log);
                    obstacles = new List<Rectangle>();
                    if (segs == null) segs = new List<OcrSegment>();
                    if (segs.Count == 0 && log != null)
                        JadeHost.Log("[game] ocr0 " + log.Replace("\r\n", " | ").Replace("\n", " | "));
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
                            string lg2 = "";
                            segs = GameRecognize(big, japan, out lg2);
                            obstacles = new List<Rectangle>();
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
                                string lg2 = "";
                                List<OcrSegment> s2 = GameRecognize(big2, true, out lg2);
                                if (s2 != null && s2.Count > 0)
                                {
                                    ScaleSegs(s2, new List<Rectangle>(), 2);
                                    int k2 = 0, c2 = 0, lat2 = 0, dg2 = 0;
                                    CountChars(s2, ref c2, ref k2, ref lat2, ref dg2);
                                    if (k2 > k)
                                    {
                                        segs = s2;
                                        obstacles = new List<Rectangle>();
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
                // 「遮罩还压在上面」的半截帧。等待从 34ms 压到 8ms：复核每 1~3 秒来一次，
                // 34ms 足够肉眼看到一次闪；压到 8ms 基本只丢一帧。万一还是抓到半截帧，
                // 下面「读到的是译文本身」那道守卫会兜住，不会误收遮罩。
                DwmFlush();
                Thread.Sleep(8);
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
                Rectangle screen = SystemInformation.VirtualScreen;
                Rectangle v = Rectangle.Intersect(r, screen);
                Bitmap b = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
                if (v.Width <= 0 || v.Height <= 0)
                {
                    // 区域整个在屏幕外：给一张同尺寸的空图，后续自然读不到字，不再整轮报错
                    LogClipOnce();
                    return b;
                }
                if (v.Width != r.Width || v.Height != r.Height) LogClipOnce();
                using (Graphics g = Graphics.FromImage(b))
                    g.CopyFromScreen(v.Left, v.Top, v.Left - r.Left, v.Top - r.Top, v.Size, CopyPixelOperation.SourceCopy);
                return b;
            }
            catch (Exception ex)
            {
                JadeHost.Log("[game] capture fail " + ex.Message);
                return null;
            }
        }

        // 选区超出屏幕时只提示一次：用户把区域画到屏幕外（换分辨率、缩放变了）时，
        // 原来整轮 CopyFromScreen 直接抛「句柄无效」，游戏模式看起来就是「什么都不翻」
        private static bool _clipWarned;
        private static void LogClipOnce()
        {
            if (_clipWarned) return;
            _clipWarned = true;
            JadeHost.Log("[game] region outside screen, clipped to visible part");
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

        // 段落块：按「位置」把同一选区里上下相邻、左右对得上的行并成一小块，整块去翻译。
        // 和 MergeLines 的分工：
        //   MergeLines 只认「同一句被 OCR 拆成两行」（标点/大小写/连词判断）；
        //   这里认「同一个对话框/字幕框里的连续 2~4 行」——几何关系严格限制，
        //   不跨分区、不把任务榜那种多行列表滚成一个大框（行数/字数/单行高倍率都有上限）。
        // 块合并之后，预翻译本来就是整批并发打 llama-server 的 4 个槽，
        // 调用数从「一行一次」降到「一块一次」，模型也能看到上一句。
        public static List<OcrSegment> GroupBlocks(List<OcrSegment> segs, Rectangle union, List<Rectangle> regions)
        {
            if (segs == null || segs.Count < 2) return segs;
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
                if (res.Count == 0) { res.Add(CloneSeg(cur)); continue; }
                OcrSegment last = res[res.Count - 1];
                if (!CanGroup(last, cur, union, regions)) { res.Add(CloneSeg(cur)); continue; }
                last.Text = Join(last.Text, cur.Text);
                last.Box = Rectangle.Union(last.Box, cur.Box);
                AddSources(last, cur);
                last.LowConfidence = last.LowConfidence || cur.LowConfidence;
                last.SkipMt = last.SkipMt || cur.SkipMt;
            }
            return res;
        }

        private static bool CanGroup(OcrSegment blk, OcrSegment b, Rectangle union, List<Rectangle> regions)
        {
            if (blk == null || b == null) return false;
            if (RegionIndex(blk.Box, union, regions) != RegionIndex(b.Box, union, regions)) return false;
            int lines = blk.SourceBoxes == null ? 1 : blk.SourceBoxes.Count;
            if (lines >= 4) return false;                        // 一块最多 4 行
            string bt = (blk.Text ?? "").Trim();
            if (bt.Length > 220) return false;                   // 再长就不像一句字幕了
            Rectangle lastLine = (blk.SourceBoxes != null && blk.SourceBoxes.Count > 0)
                ? blk.SourceBoxes[blk.SourceBoxes.Count - 1] : blk.Box;
            int hRef = Math.Min(lastLine.Height, b.Box.Height);   // 只看单行高度，防止滚雪球
            if (hRef < 1) hRef = 1;
            int gap = b.Box.Top - blk.Box.Bottom;
            if (gap < -(hRef / 2) || gap > (hRef * 9) / 10) return false;   // 行距最多 0.9 行高
            int left = Math.Max(blk.Box.Left, b.Box.Left);
            int right = Math.Min(blk.Box.Right, b.Box.Right);
            if (right - left <= 0) return false;
            double ov = (double)(right - left) / Math.Max(1, Math.Min(blk.Box.Width, b.Box.Width));
            if (ov < 0.35) return false;
            double hr = (double)Math.Max(lastLine.Height, b.Box.Height) / Math.Max(1, Math.Min(lastLine.Height, b.Box.Height));
            if (hr > 1.8) return false;                          // 小字贴大字不算同一段
            char end = bt.Length == 0 ? '\0' : bt[bt.Length - 1];
            bool sentEnd = end == '.' || end == '!' || end == '?' || end == '。' || end == '！' || end == '？' || end == '…';
            // 上一行已经收句：只有贴得非常近、且左右几乎对齐，才当同一段的第二句
            if (sentEnd && (gap > (hRef * 2) / 5 || ov < 0.55)) return false;
            return true;
        }

        private static OcrSegment CloneSeg(OcrSegment s)
        {
            OcrSegment n = new OcrSegment();
            n.Text = s.Text;
            n.Box = s.Box;
            n.Angle = s.Angle;
            n.Order = s.Order;
            n.LowConfidence = s.LowConfidence;
            n.SkipMt = s.SkipMt;
            n.SourceBoxes = s.SourceBoxes == null ? new List<Rectangle>() : new List<Rectangle>(s.SourceBoxes);
            if (n.SourceBoxes.Count == 0) n.SourceBoxes.Add(s.Box);
            return n;
        }

        private static void AddSources(OcrSegment blk, OcrSegment cur)
        {
            if (blk.SourceBoxes == null) blk.SourceBoxes = new List<Rectangle>();
            if (cur.SourceBoxes != null && cur.SourceBoxes.Count > 0) blk.SourceBoxes.AddRange(cur.SourceBoxes);
            else blk.SourceBoxes.Add(cur.Box);
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

    // 调试用：把「按位置自动分割出来的文字块」用矩形画在屏幕上。
    // 粗框 = 一个翻译块，细虚线 = 块内每一行的原框；窗口边界就是选区并集。
    internal static class BlockDebug
    {
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        private static Thread _th;
        private static BlockDebugForm _form;
        private static readonly object Gate = new object();
        private static readonly ManualResetEventSlim Ready = new ManualResetEventSlim(false);
        private static List<Rectangle> _blocks = new List<Rectangle>();
        private static List<Rectangle> _lines = new List<Rectangle>();
        private static Rectangle _union = Rectangle.Empty;
        private static int _seq;
        private static int _lastLogCount = -1;
        private static bool _visible;

        public static void Show(Rectangle union, List<OcrSegment> segs)
        {
            if (!AppConfig.GameBlockDebug) { Hide(); return; }
            List<Rectangle> blocks = new List<Rectangle>();
            List<Rectangle> lines = new List<Rectangle>();
            if (segs != null)
            {
                for (int i = 0; i < segs.Count; i++)
                {
                    OcrSegment s = segs[i];
                    if (s == null || s.Box.Width < 2 || s.Box.Height < 2) continue;
                    blocks.Add(new Rectangle(s.Box.X + union.X, s.Box.Y + union.Y, s.Box.Width, s.Box.Height));
                    if (s.SourceBoxes != null)
                    {
                        for (int j = 0; j < s.SourceBoxes.Count; j++)
                        {
                            Rectangle r = s.SourceBoxes[j];
                            if (r.Width < 2 || r.Height < 2) continue;
                            lines.Add(new Rectangle(r.X + union.X, r.Y + union.Y, r.Width, r.Height));
                        }
                    }
                }
            }
            EnsureForm();
            lock (Gate)
            {
                _blocks = blocks;
                _lines = lines;
                _union = union;
                _seq++;
            }
            BlockDebugForm f = _form;
            if (f == null) return;
            int seq = _seq;
            if (_lastLogCount != blocks.Count)
            {
                _lastLogCount = blocks.Count;
                try { NativeShell.Log("[blockdbg] show blocks=" + blocks.Count + " lines=" + lines.Count); } catch (Exception) { }
            }
            try { f.BeginInvoke(new Action(delegate() { f.Apply(union, blocks, lines, seq); })); }
            catch (Exception) { }
            _visible = true;
        }

        // 抓屏前把分块框藏起来（放回后马上恢复）：否则 OCR 会把框线/编号当成正文。
        // 不用 WDA_EXCLUDEFROMCAPTURE——这个项目里实测分层窗口用它会整块看不见。
        public static bool SuspendForCapture()
        {
            BlockDebugForm f = _form;
            if (f == null || !_visible) return false;
            try { f.Invoke(new Action(delegate() { f.Hide(); })); }
            catch (Exception) { return false; }
            try { DwmFlush(); System.Threading.Thread.Sleep(8); DwmFlush(); }
            catch (Exception) { }
            return true;
        }

        public static void ResumeAfterCapture()
        {
            BlockDebugForm f = _form;
            if (f == null) return;
            try
            {
                f.Invoke(new Action(delegate()
                {
                    f.Show();
                    f.TopMost = true;
                    f.Raise();
                    f.Invalidate();
                }));
            }
            catch (Exception) { }
        }

        public static void Hide()
        {
            BlockDebugForm f = _form;
            _visible = false;
            if (f == null) return;
            try { f.BeginInvoke(new Action(delegate() { f.Hide(); })); } catch (Exception) { }
        }

        private static void EnsureForm()
        {
            lock (Gate)
            {
                if (_th != null && _th.IsAlive) return;
                Ready.Reset();
                _th = new Thread(delegate()
                {
                    try
                    {
                        BlockDebugForm f = new BlockDebugForm();
                        lock (Gate) { _form = f; }
                        f.Show();
                        Ready.Set();
                        Application.Run(f);
                    }
                    catch (Exception) { Ready.Set(); }
                });
                _th.IsBackground = true;
                _th.SetApartmentState(ApartmentState.STA);
                _th.Start();
            }
            // 等 STA 线程把窗口建出来：第一次 Show 时 _form 还是 null，直接返回就再也不会画了
            try { Ready.Wait(1500); } catch (Exception) { }
        }
    }

    internal sealed class BlockDebugForm : Form
    {
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

        private List<Rectangle> _blocks = new List<Rectangle>();
        private List<Rectangle> _lines = new List<Rectangle>();
        private Rectangle _union = Rectangle.Empty;
        private int _seq = -1;

        public BlockDebugForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Fuchsia;
            TransparencyKey = Color.Fuchsia;
            DoubleBuffered = true;
            Bounds = new Rectangle(0, 0, 1, 1);
            // 抓图前会 Hide、抓完 Show，Show 会把窗口塞回「活动窗口」下面；
            // 游戏窗口通常也是 TopMost，所以得定时把自己顶回最前，否则框会被游戏盖住
            System.Windows.Forms.Timer keep = new System.Windows.Forms.Timer();
            keep.Interval = 300;
            keep.Tick += delegate(object s, EventArgs e) { if (Visible) RaiseTopmost(); };
            keep.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x20 | 0x80 | 0x08000000;   // 鼠标穿透 + 工具窗口 + 不抢焦点
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
        }

        public void Apply(Rectangle union, List<Rectangle> blocks, List<Rectangle> lines, int seq)
        {
            if (seq < _seq) return;
            _seq = seq;
            _union = union;
            _blocks = blocks;
            _lines = lines;
            if (union.Width > 0 && union.Height > 0 && Bounds != union) Bounds = union;
            if (!Visible) Show();
            RaiseTopmost();
            Invalidate();
        }

        // 和游戏/测试窗口同为 TopMost 时，光设 TopMost 不会抬到最前，必须显式 SetWindowPos
        private void RaiseTopmost()
        {
            try { SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE); }
            catch (Exception) { }
        }

        public void Raise() { RaiseTopmost(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int ox = _union.X, oy = _union.Y;

            // 块内每一行：白色细点线，用来核对“这行到底被并进哪块”
            if (_lines.Count > 0)
            {
                using (Pen lp = new Pen(Color.FromArgb(170, 255, 255, 255), 1f))
                {
                    lp.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;
                    for (int i = 0; i < _lines.Count; i++)
                    {
                        Rectangle r = _lines[i];
                        e.Graphics.DrawRectangle(lp, r.X - ox, r.Y - oy, r.Width - 1, r.Height - 1);
                    }
                }
            }

            Color[] cols = {
                Color.FromArgb(57, 255, 20), Color.FromArgb(0, 229, 255), Color.FromArgb(255, 196, 0),
                Color.FromArgb(255, 109, 0), Color.FromArgb(224, 64, 251), Color.FromArgb(0, 255, 156),
                Color.FromArgb(255, 64, 129), Color.FromArgb(100, 221, 23)
            };
            using (Font f = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    Rectangle r = _blocks[i];
                    Color c = cols[i % cols.Length];
                    using (Pen bp = new Pen(c, 2f))
                        e.Graphics.DrawRectangle(bp, r.X - ox, r.Y - oy, r.Width - 1, r.Height - 1);

                    string label = "#" + (i + 1);
                    SizeF sz = e.Graphics.MeasureString(label, f);
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(210, 20, 22, 28)))
                    using (SolidBrush fg = new SolidBrush(c))
                    {
                        e.Graphics.FillRectangle(bg, r.X - ox, r.Y - oy - sz.Height - 2, sz.Width + 8, sz.Height + 2);
                        e.Graphics.DrawString(label, f, fg, r.X - ox + 4, r.Y - oy - sz.Height);
                    }
                }
            }

            // 左上角总览：这一轮分出来几块
            string sum = "分块 " + _blocks.Count + " 个";
            using (Font f = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(215, 20, 22, 28)))
            using (SolidBrush fg = new SolidBrush(Color.FromArgb(255, 120, 255, 190)))
            {
                SizeF sz = e.Graphics.MeasureString(sum, f);
                e.Graphics.FillRectangle(bg, 4, 4, sz.Width + 10, sz.Height + 4);
                e.Graphics.DrawString(sum, f, fg, 9, 6);
            }
        }
    }
}
