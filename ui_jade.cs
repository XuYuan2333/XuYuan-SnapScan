using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QRSnap
{
    // ================= JadeView 原生接口（2.3.0 · stdcall · 字符串一律自己按 UTF-8 传指针） =================
    internal static class JadeNative
    {
        const string Dll = "JadeView_x64.dll";

        [StructLayout(LayoutKind.Sequential)]
        internal struct Options
        {
            public IntPtr title;
            public int width, height, resizable;
            public IntPtr frame_style;
            public int transparent;
            public IntPtr background_color;
            public int always_on_top;
            public IntPtr theme;
            public int maximized, maximizable, minimizable;
            public int x, y, min_width, min_height, max_width, max_height;
            public int fullscreen, focus, hide_window, use_page_icon, content_protection, auto_save_state, skip_taskbar, no_activate;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Settings
        {
            public int autoplay, background_throttling, allow_right_click;
            public IntPtr ua, preload_js;
            public int allow_fullscreen;
            public IntPtr postmessage_whitelist, cors_whitelist;
            public int autofill, general_autofill_enabled, incognito, disable_clipboard;
            public IntPtr proxy_url;
            public int focused;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate IntPtr IpcCallback(uint windowId, IntPtr message);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern int JadeView_init(int devmod, IntPtr logPath, IntPtr dataDir, IntPtr appName, IntPtr appSig, int singleInstance);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern int run_message_loop();

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern int jadeview_exit();

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern uint jade_on(IntPtr evt, IpcCallback cb);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern int register_ipc_handler(IntPtr channel, IpcCallback cb);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern uint create_webview_window(IntPtr url, uint parent, IntPtr options, IntPtr settings);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern int set_protocol_service_path(IntPtr root, StringBuilder url, int size, int hotReload);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern IntPtr jade_text_create(IntPtr text);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern int set_window_visible(uint id, int visible);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern int set_window_focus(uint id);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern int set_window_backdrop(uint id, IntPtr kind);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern int set_window_theme(uint id, IntPtr theme);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern int set_window_background_color(uint id, IntPtr hex);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern int set_window_title(uint id, IntPtr title);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern int send_ipc_message(uint id, IntPtr type, IntPtr content);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern int is_window_visible(uint id);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        internal static extern int is_windows_11();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr LoadLibraryW(string path);

        internal static bool Preload(string dllPath)
        {
            try { return LoadLibraryW(dllPath) != IntPtr.Zero; }
            catch (Exception) { return false; }
        }
    }

    // ================= 极简 JSON 输出（只做“生成”，不做解析） =================
    internal static class J
    {
        public static string S(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string B(bool b) { return b ? "true" : "false"; }
        public static string N(long n) { return n.ToString(); }
    }

    // ================= 原生壳：热键、托盘、截图、OCR（WinForms 隐藏窗体，和 Jade 消息循环并行） =================
    internal sealed class NativeShell : Form
    {
        const int WM_HOTKEY = 0x0312;
        const int ID_QR = 1, ID_OCR = 2, ID_MT = 3, ID_HIDE = 4;

        private ToastForm _toast;
        private readonly System.Windows.Forms.Timer _watch;
        private bool _seenWindow;
        private uint _modsQr = Native.MOD_CONTROL | Native.MOD_ALT, _vkQr = (uint)Keys.D;
        private uint _modsOcr = Native.MOD_CONTROL | Native.MOD_ALT, _vkOcr = (uint)Keys.X;
        private uint _modsMt = Native.MOD_CONTROL | Native.MOD_ALT, _vkMt = (uint)Keys.F;
        private uint _modsHide = Native.MOD_CONTROL | Native.MOD_ALT, _vkHide = (uint)Keys.G;

        internal Action<string, string> OnCaptured;   // (mode, json)

        public NativeShell()
        {
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            ShowIcon = false;
            Opacity = 0;
            FormBorderStyle = FormBorderStyle.None;
            Size = new Size(1, 1);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-3000, -3000);

            _toast = new ToastForm();
            // 不再有托盘图标：程序只以任务栏按钮的形式存在，关掉窗口就是退出
            _watch = new System.Windows.Forms.Timer();
            _watch.Interval = 1000;
            _watch.Tick += delegate
            {
                if (!JadeHost.Started) return;
                if (Native.OwnMainWindow() == IntPtr.Zero)
                {
                    if (_seenWindow) JadeHost.Quit();   // 主窗口被关掉 → 整个程序退出
                }
                else _seenWindow = true;
                try { MtRunner.TrimIdle(AppConfig.MtIdle); } catch (Exception) { }
            };
            _watch.Start();
        }

        private static Icon LoadIcon()
        {
            try
            {
                Assembly self = Assembly.GetExecutingAssembly();
                object res = null;
                try
                {
                    res = self.GetManifestResourceStream("APPICON");
                    if (res != null) return new Icon((Stream)res);
                }
                catch (Exception) { }
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception) { return SystemIcons.Application; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterAll();
        }

        public void RegisterAll()
        {
            Native.UnregisterHotKey(Handle, ID_QR);
            Native.UnregisterHotKey(Handle, ID_OCR);
            Native.UnregisterHotKey(Handle, ID_MT);
            Native.UnregisterHotKey(Handle, ID_HIDE);

            uint m, k;
            if (HotkeyText.TryParse(AppConfig.HotkeyQr, out m, out k)) { _modsQr = m; _vkQr = k; }
            if (HotkeyText.TryParse(AppConfig.HotkeyOcr, out m, out k)) { _modsOcr = m; _vkOcr = k; }
            if (HotkeyText.TryParse(AppConfig.HotkeyMt, out m, out k)) { _modsMt = m; _vkMt = k; }
            if (HotkeyText.TryParse(AppConfig.HotkeyHide, out m, out k)) { _modsHide = m; _vkHide = k; }

            TryRegister(ID_QR, _modsQr, _vkQr, "qr");
            TryRegister(ID_OCR, _modsOcr, _vkOcr, "ocr");
            TryRegister(ID_MT, _modsMt, _vkMt, "mt");
            TryRegister(ID_HIDE, _modsHide, _vkHide, "hide");
        }

        private void TryRegister(int id, uint mods, uint vk, string which)
        {
            if (mods == 0) mods = Native.MOD_CONTROL | Native.MOD_ALT;
            bool ok = Native.RegisterHotKey(Handle, id, mods | Native.MOD_NOREPEAT, vk);
            Log("register id=" + id + " " + HotkeyText.Format(mods, vk) + " -> " + ok);
            if (!ok)
            {
                // 被占用：换一个不冲突的组合
                uint[] fallback = new uint[] { (uint)Keys.F8, (uint)Keys.F9, (uint)Keys.F10, (uint)Keys.D9 };
                foreach (uint alt in fallback)
                {
                    if (alt == vk) continue;
                    if (Native.RegisterHotKey(Handle, id, mods | Native.MOD_NOREPEAT, alt))
                    {
                        vk = alt;
                        Log("  fallback -> " + HotkeyText.Format(mods, vk));
                        break;
                    }
                }
            }
            if (which == "qr") { _modsQr = mods; _vkQr = vk; AppConfig.HotkeyQr = HotkeyText.Format(mods, vk); }
            else if (which == "ocr") { _modsOcr = mods; _vkOcr = vk; AppConfig.HotkeyOcr = HotkeyText.Format(mods, vk); }
            else if (which == "mt") { _modsMt = mods; _vkMt = vk; AppConfig.HotkeyMt = HotkeyText.Format(mods, vk); }
            else { _modsHide = mods; _vkHide = vk; AppConfig.HotkeyHide = HotkeyText.Format(mods, vk); }
        }

        internal static void Log(string msg)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XuYuan-SnapScan", "jade-data");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "native.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = (int)m.WParam;
                Log("WM_HOTKEY id=" + id);
                // 游戏模式：Ctrl+Alt+F 作为手动翻译键；翻译中再按会强制开新一轮
                if (id == ID_MT && AppConfig.GameOn)
                {
                    GameRuntime.Trigger(true);
                    return;
                }
                // 识别期间 UI 线程在泵消息，这时再按热键会叠出一轮新截屏，直接忽略
                if (_pumping) { Log("WM_HOTKEY ignored while busy id=" + id); return; }
                if (id == ID_QR) HotkeyCapture("qr");
                else if (id == ID_OCR) HotkeyCapture("ocr");
                else if (id == ID_MT) HotkeyCapture("mt");
                else if (id == ID_HIDE)
                {
                    // 全局显隐：Ctrl+Alt+G 关掉全部译文 / 再按一次恢复；游戏模式记住用户关闭状态
                    if (AppConfig.GameOn) GameRuntime.ToggleOverlay();
                    else TransOverlay.Toggle();
                    Log("hide toggle showing=" + TransOverlay.IsShowing);
                }
                return;
            }
            base.WndProc(ref m);
        }

        private void HotkeyCapture(string mode)
        {
            // 翻译是独立功能：没装模型 / 开关关着时，热键只提示，不去截图
            if (mode == "mt" && !(AppConfig.Translate && MtRunner.Ready))
            {
                if (_toast != null) _toast.ShowDone(MtRunner.Ready ? "翻译功能已关闭" : "没有可用的翻译模型", false);
                return;
            }
            // 框选还没结束又按了一次热键：直接忽略。否则会叠出好几层遮罩，
            // Esc 只能关掉最上面那层，看起来就像“按了没反应”。
            if (!JadeHost.TryEnterCapture()) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    JadeHost.HideWindow(mode != "mt");
                    Thread.Sleep(160);
                    JadeHost.RunCapture(mode, true);
                }
                finally
                {
                    JadeHost.LeaveCapture();
                }
            });
        }

        // 由 Jade 线程调用（不阻塞 UI 线程过久）：把活交给 WinForms 线程做
        public string CaptureSync(string mode, bool fromHotkey, out bool cancelled)
        {
            cancelled = false;
            if (InvokeRequired)
            {
                object[] holder = new object[1];
                Invoke(new MethodInvoker(delegate() { holder[0] = SafeCaptureOnUi(mode); }));
                if (holder[0] == null) { cancelled = true; return null; }
                return ((CaptureResult)holder[0]).Json;
            }
            CaptureResult r = SafeCaptureOnUi(mode);
            if (r == null) { cancelled = true; return null; }
            return r.Json;
        }

        private CaptureResult SafeCaptureOnUi(string mode)
        {
            try { return CaptureOnUi(mode); }
            catch (Exception ex)
            {
                Log("capture EX " + ex.ToString());
                try
                {
                    if (_toast != null) _toast.ShowDone("截图失败：" + ex.Message, false);
                    Clipboard.SetText("SnapScan 截图异常：\r\n" + ex.ToString());
                }
                catch (Exception) { }
                return null;
            }
        }

        internal sealed class CaptureResult { public string Json; }

        // 识别是 CPU 活，但提示条窗口的动画和绘制全靠本线程的消息泵。
        // 直接在 UI 线程里同步识别，ShowBusy 一帧都画不出来就被 ShowDone 换掉，
        // 用户只会看到“识字完成”，看不到“正在识字”。所以识别丢到后台线程，
        // UI 线程这边泵消息，让提示条该淡入就淡入、该转圈就转圈。
        private bool _pumping;
        private string _mtSkipReason;   // 这一路没出译文时的具体原因，交给结果详情
        private string _mtSkipShort;    // 原因短句，直接显示在提示条上（「未翻译」到底为什么）
        private string _mtFail;         // AI 接管真失败了（不是「不需要翻译」），提示条要变红叉
        private int _aiMtLines;         // AI 接管译出几行
        private int _aiCheckLines;      // AI 质检重翻几行
        private long _aiCheckMs;
        private int _aiMergeGroups;     // AI 短句合并几组
        private long _aiMtMs;           // AI 接管耗时
        private bool _layoutActive;     // 本轮是否走了「分栏/徽章/段落」版面强化
        private int _layoutLines;       // 版面强化后的块数
        private List<Rectangle> _layoutObstacles;   // 头像/表情等需要避让的图形
        private Bitmap _lastOverlayBitmap;          // 本轮成品图，供重复截图缓存落盘
        private ulong _lastShotHash;                // 本轮截图 dHash
        private string _lastMtText;                 // 本轮译文，自动复制用

        // 提示条第二行：这一轮 AI 到底干了什么。没有 AI 介入就返回 null，提示条还是单行。
        private string AiSummary()
        {
            List<string> parts = new List<string>();
            if (AppConfig.AiOcr && AiTextFix.LastAsked > 0)
                parts.Add("AI 修字 " + AiTextFix.LastChanged + "/" + AiTextFix.LastAsked + " 行");
            if (_layoutActive && _layoutLines > 0)
                parts.Add("AI 版面 " + _layoutLines + " 块");
            if (_aiMergeGroups > 0)
                parts.Add("AI 合并 " + _aiMergeGroups + " 组");
            if (_aiCheckLines > 0)
                parts.Add("AI 质检 " + _aiCheckLines + " 行 " + (_aiCheckMs / 1000.0).ToString("0.0") + "s");
            if (_aiMtLines > 0)
                parts.Add("AI 接管 " + _aiMtLines + " 行 " + (_aiMtMs / 1000.0).ToString("0.0") + "s");
            if (_mtFail != null)
                parts.Add("AI 报错：" + (_mtSkipShort ?? "失败"));
            return parts.Count == 0 ? null : string.Join(" · ", parts.ToArray());
        }

        private static string JoinSegText(List<OcrSegment> segs)
        {
            if (segs == null || segs.Count == 0) return string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < segs.Count; i++)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(segs[i].Text ?? "");
            }
            return sb.ToString();
        }

        private static string OneLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "(null)";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // 遮罩底板避让：首行右侧有头像/表情时，把底板宽度收到图形左边
        private static Rectangle TrimByObstacles(Rectangle r, List<Rectangle> obstacles)
        {
            if (obstacles == null || obstacles.Count == 0) return r;
            int right = r.Right;
            for (int i = 0; i < obstacles.Count; i++)
            {
                Rectangle o = obstacles[i];
                if (o.Bottom <= r.Top + 2 || o.Top >= r.Top + r.Height * 0.65f) continue;
                int cut = o.Left - 3;
                if (cut > r.Left && cut < right) right = cut;
            }
            if (right < r.Right && right - r.Left >= 20)
                return new Rectangle(r.X, r.Y, right - r.X, r.Height);
            return r;
        }

        private void RunPumped(Action work, int minVisibleMs)        {
            Exception error = null;
            _pumping = true;
            try
            {
                Thread worker = new Thread(delegate()
                {
                    try { work(); }
                    catch (Exception ex) { error = ex; }
                });
                worker.IsBackground = true;
                worker.Start();

                long start = Environment.TickCount;
                while (worker.IsAlive)
                {
                    Application.DoEvents();
                    Thread.Sleep(3);
                }
                // 快到肉眼看不见结果的（比如二维码）也保证「识别中」露个面，
                // 不然一闪而过反而像没提示
                while ((Environment.TickCount - start) < minVisibleMs)
                {
                    Application.DoEvents();
                    Thread.Sleep(3);
                }
            }
            finally { _pumping = false; }
            if (error != null) throw error;
        }

        private CaptureResult CaptureOnUi(string mode)
        {
            Log("capture start " + mode);
            CaptureSession session = new CaptureSession();
            session.Freehand = AppConfig.Freehand;

            // 截屏前先把提示条收掉。原来这里直接 ShowBusy，结果上一轮还没消失的
            // “识别完成 / 正在识别”会被一起拍进截图里，框选时也能看到这块残留。
            if (_toast != null) _toast.HideToast();
            TransOverlay.HideOverlay();
            try { Native.FlushComposition(); } catch (Exception) { }
            Thread.Sleep(40);

            session.Run();
            Bitmap crop = session.Crop;
            Log("capture done confirmed=" + session.Confirmed + " crop=" + (crop != null));
            if (!session.Confirmed || crop == null) 
            {
                if (_toast != null) _toast.ShowDone("已取消", false);
                return null; 
            }

            // 重复截图缓存：同一屏（允许轻微差异）直接贴上次成品，不再 OCR / 翻译
            if (_lastOverlayBitmap != null) { try { _lastOverlayBitmap.Dispose(); } catch (Exception) { } _lastOverlayBitmap = null; }
            _lastShotHash = 0;
            _lastMtText = null;
            if (mode == "mt" && AppConfig.Translate && AppConfig.ShotCacheEnabled)
            {
                ShotCache.Entry ce;
                if (ShotCache.TryGet(crop, out ce) && ce.Image != null)
                {
                    try { TransOverlay.ShowImage(session.Selection, ce.Image); } catch (Exception) { }
                    _lastOverlayBitmap = ce.Image;
                    _lastShotHash = ShotCache.Hash(crop);
                    _lastMtText = ce.MtText;
                    if (AppConfig.AutoCopy && !string.IsNullOrEmpty(ce.MtText))
                    {
                        try { Clipboard.SetText(ce.MtText); } catch (Exception) { }
                    }
                    NativeShell.Log("[cache] hit h=" + _lastShotHash.ToString("x16") + " json=" + (ce.Json == null ? 0 : ce.Json.Length));
                    if (_toast != null) _toast.ShowDone("翻译完成 · 缓存命中", "同一屏，直接贴上次成品", true);
                    crop.Dispose();
                    CaptureResult cached = new CaptureResult();
                    cached.Json = ce.Json;
                    return cached;
                }
                _lastShotHash = ShotCache.Hash(crop);
            }

            string text = string.Empty;
            string engine = string.Empty;
            string detail = string.Empty;
            long ms = 0;
            bool ok;
            int mtLines = 0;

            if (mode == "qr")
            {
                if (_toast != null) _toast.ShowBusy("正在识码…");
                string qrText = null;
                string qrInfo = string.Empty;
                RunPumped(delegate()
                {
                    qrText = QrDecoder.Decode(crop, out qrInfo);
                }, 320);
                text = qrText;
                engine = "二维码";
                detail = qrInfo;
                ok = !string.IsNullOrEmpty(text);
            }
            else
            {
                string busyEngine = Ocr.EffectiveName();
                // 识字和翻译是两个独立功能：只有"截图翻译"这一路才叠译文
                // 但行坐标两条路都建：识字也要让 AI 按段落校正文本，翻译还要拿它放遮罩
                bool wantMt = mode == "mt" && AppConfig.Translate && (AppConfig.AiMt || MtRunner.Ready);
                // AI 翻译接管时，翻译提示词里已经要求「先纠 OCR 错再翻」，
                // 不再单独跑一遍识字强化（同一张图付两次模型往返不划算）。
                AiTextFix.Suppress = mode == "mt" && AppConfig.AiMt;
                if (_toast != null)
                {
                    string sub2 = null;
                    if (AppConfig.AiOcr && JadeHost.AiReadyNow())
                        sub2 = "AI 识字强化已开：认错的符号交给大模型修，每张图多几秒";
                    _toast.ShowBusy("正在识字…  " + busyEngine, sub2);
                }
                AiTextFix.LastAsked = 0;
                AiTextFix.LastChanged = 0;
                AiTextFix.LastCostMs = 0;

                List<OcrSegment> segs = new List<OcrSegment>();

                string recText = string.Empty;
                string recEngine = string.Empty;
                string recDetail = string.Empty;
                long recMs = 0;
                bool preLayout = false;
                bool didOcr = false;
                Log("ocr cfg fusion=" + (AppConfig.OcrFusion ? "1" : "0") + " layout=" + (AppConfig.AiLayout ? "1" : "0")
                    + " ready=" + (MtRunner.Ready ? "1" : "0"));

                // 翻译 + 版面强化：直接跑「版面识字」，不要再先跑一遍普通识字。
                // 原来两条链路各跑一次 PP-OCR，等于同一张图识别两遍，白花 2~3 秒。
                if (mode == "mt" && AppConfig.AiLayout && AppConfig.Translate
                    && (AppConfig.AiMt || MtRunner.Ready))
                {
                    try
                    {
                        List<Rectangle> obs;
                        string llog;
                        System.Diagnostics.Stopwatch swL = System.Diagnostics.Stopwatch.StartNew();
                        List<OcrSegment> ls = AppConfig.OcrFusion
                            ? Ocr.LayoutSegmentsFused(crop, out obs, out llog)
                            : Ocr.LayoutSegments(crop, out obs, out llog);
                        swL.Stop();
                        if (ls != null && ls.Count >= 3)
                        {
                            segs = ls;
                            preLayout = true;
                            didOcr = true;
                            _layoutObstacles = obs;
                            _layoutLines = ls.Count;
                            recEngine = AppConfig.OcrFusion ? "PP+微信 · 融合版面" : "PP-OCR 极速 · 版面";
                            recDetail = "layout";
                            recMs = swL.ElapsedMilliseconds;
                            // 离线翻译时 AI 识字强化作用在版面分段上（AI 翻译接管时由翻译提示词纠错）
                            if (AppConfig.AiOcr && !AppConfig.AiMt)
                            {
                                try { AiTextFix.Polish(segs); } catch (Exception) { }
                            }
                            recText = JoinSegText(segs);
                        }
                        else Log("prelayout skip " + OneLine(llog, 220));
                    }
                    catch (Exception exPre)
                    {
                        Log("prelayout fail " + exPre.Message);
                    }
                }

                // 纯识字 + 融合：PP 出框、微信出字；会比微信单引擎慢，开关默认关
                if (!didOcr && mode == "ocr" && AppConfig.OcrFusion)
                {
                    try
                    {
                        List<Rectangle> obs;
                        string llog;
                        System.Diagnostics.Stopwatch swL = System.Diagnostics.Stopwatch.StartNew();
                        List<OcrSegment> ls = Ocr.LayoutSegmentsFused(crop, out obs, out llog);
                        swL.Stop();
                        if (ls != null && ls.Count > 0)
                        {
                            segs = ls;
                            didOcr = true;
                            recEngine = "PP+微信 · 融合识字";
                            recDetail = llog;
                            recMs = swL.ElapsedMilliseconds;
                            recText = JoinSegText(segs);
                        }
                    }
                    catch (Exception exFuse)
                    {
                        Log("ocr fusion fail " + exFuse.Message);
                    }
                }

                if (!didOcr)
                {
                    RunPumped(delegate()
                    {
                        recText = Ocr.Recognize(crop, out recEngine, out recMs, out recDetail, segs);
                    }, 320);
                }
                text = recText;
                engine = recEngine;
                detail = recDetail;
                ms = recMs;
                ok = !string.IsNullOrEmpty(text);
                Log("ocr " + recEngine + " " + recMs + "ms lines=" + (segs == null ? 0 : segs.Count) + (preLayout ? " layout=1" : ""));

                if (wantMt && ok)
                {
                    try
                    {
                        // 空闲释放后引擎是停着的，这一句会明显慢一拍，先说清楚，别让人以为卡死
                        if (_toast != null)
                        {
                            string sub3 = null;
                            if (AppConfig.AiMt) sub3 = "AI 接管：整段交给大模型翻，不用本地模型";
                            else if (!LlamaMt.ServerRunning) sub3 = "首次加载模型，稍等";
                            _toast.ShowBusy(LlamaMt.ServerRunning || AppConfig.AiMt ? "正在翻译…" : "正在翻译…（首次加载模型，稍等）", sub3);
                        }
                        int shown = TranslateAndShow(segs, session.Selection, crop, preLayout);
                        if (shown > 0) mtLines = shown;
                        else detail = detail + "；翻译：未出译文（" + (_mtSkipReason ?? "没有可对齐的文本行") + "）";
                    }
                    catch (Exception exMt)
                    {
                        detail = detail + "；翻译失败：" + exMt.Message;
                        // AI 翻译接管失败时，明确标出来，不要伪装成「没识别到要翻的字」
                        _mtFail = exMt.Message;
                        _mtSkipReason = exMt.Message;
                        _mtSkipShort = "翻译失败";
                        Log("translate fail " + exMt.Message);
                    }
                }
            }
            crop.Dispose();

            if (ok && AppConfig.AutoCopy)
            {
                // 翻译模式复制译文，识字/扫码复制识别原文；以前固定复制 text，翻译时拿到的其实是原文
                string copyText = (mode == "mt" && !string.IsNullOrEmpty(_lastMtText)) ? _lastMtText : text;
                if (!string.IsNullOrEmpty(copyText))
                {
                    try { Clipboard.SetText(copyText); } catch (Exception) { }
                }
            }

            string doneWord = mode == "qr" ? "识码完成"
                : (_mtFail != null ? "翻译失败 · " + _mtSkipShort
                : (mtLines > 0 ? "翻译完成"
                : (mode == "mt" && ok ? "识字完成 · 未翻译：" + (_mtSkipShort ?? "原因未知") : "识字完成")));
            string doneSub = AiSummary();
            if (doneSub != null) detail = detail + "；" + doneSub;   // 结果面板的「识别结果」旁边也能看到
            if (_toast != null) _toast.ShowDone(ok ? doneWord : "没有识别到内容", doneSub, ok && _mtFail == null);

            string json = "{"
                + "\"ok\":" + J.B(ok)
                + ",\"mode\":" + J.S(mode)
                + ",\"text\":" + J.S(text)
                + ",\"engine\":" + J.S(engine)
                + ",\"detail\":" + J.S(detail)
                + ",\"ms\":" + J.N(ms)
                + ",\"copied\":" + J.B(ok && AppConfig.AutoCopy && !string.IsNullOrEmpty(mode == "mt" && !string.IsNullOrEmpty(_lastMtText) ? _lastMtText : text))
                + ",\"mtText\":" + J.S(_lastMtText == null ? "" : _lastMtText)
                + ",\"mtLine\":" + J.N(mtLines)
                + "}";

            if (mode == "mt" && ok && AppConfig.ShotCacheEnabled
                && _lastShotHash != 0 && _lastOverlayBitmap != null)
            {
                try { ShotCache.Save(_lastShotHash, _lastOverlayBitmap, json, _lastMtText); } catch (Exception) { }
                NativeShell.Log("[cache] save h=" + _lastShotHash.ToString("x16"));
            }
            if (_lastOverlayBitmap != null) { try { _lastOverlayBitmap.Dispose(); } catch (Exception) { } _lastOverlayBitmap = null; }

            CaptureResult cr = new CaptureResult();
            cr.Json = json;
            return cr;
        }

        private static Rectangle ScaleRect(Rectangle r, double kx, double ky)
        {
            if (Math.Abs(kx - 1.0) < 0.02 && Math.Abs(ky - 1.0) < 0.02) return r;
            int x0 = (int)Math.Floor(r.X * kx);
            int y0 = (int)Math.Floor(r.Y * ky);
            int x1 = (int)Math.Ceiling(r.Right * kx);
            int y1 = (int)Math.Ceiling(r.Bottom * ky);
            return Rectangle.FromLTRB(x0, y0, x1, y1);
        }

        // 把 OCR 框按原始截图上的真实字形外扩到「起笔到收笔」，避免遮罩盖不住原字
        private static Rectangle InkBounds(Bitmap bmp, Rectangle box)
        {
            if (bmp == null) return box;
            // 先在整行高度上做「列是否有墨」的判断，再从原框向两侧长，遇到空档才停
            int padY = Math.Max(3, box.Height / 6);
            int sy = Math.Max(0, box.Y - padY);
            int ey = Math.Min(bmp.Height - 1, box.Bottom + padY);
            if (ey - sy < 4) return box;

            int W = bmp.Width;
            int h = ey - sy + 1;
            int[] col = new int[W];
            int step = Math.Max(1, W / 400);   // 太宽就抽样，省时间
            for (int x = 0; x < W; x += step)
            {
                int best = 0, mn = 255, mx = 0;
                for (int y = 0; y < h; y++)
                {
                    Color c = bmp.GetPixel(x, sy + y);
                    int l = (c.R * 77 + c.G * 151 + c.B * 28) >> 8;
                    if (l < mn) mn = l;
                    if (l > mx) mx = l;
                }
                best = mx - mn;
                for (int k = x; k < Math.Min(W, x + step); k++) col[k] = best;
            }
            // 背景亮度取整行最常见的亮度（近似：取平均）
            long sum = 0; int n = 0;
            for (int x = 0; x < W; x += Math.Max(1, W / 200))
            {
                for (int y = 0; y < h; y += Math.Max(1, h / 4))
                {
                    Color c = bmp.GetPixel(x, sy + y);
                    sum += (c.R * 77 + c.G * 151 + c.B * 28) >> 8; n++;
                }
            }
            int bg = n > 0 ? (int)(sum / n) : 128;
            int need = Math.Max(18, (int)(box.Height * 0.9f));   // 行内连续空白达到这个宽度就视为文字结束
            int limit = Math.Max(60, (int)(box.Width * 2.5f));

            int x0 = Math.Max(0, box.X), x1 = Math.Min(W - 1, box.Right - 1);
            int gap = 0, stopX = x1;
            for (int x = x1 + 1; x < W && x <= box.Right + limit; x++)
            {
                if (col[x] >= 26) { stopX = x; gap = 0; }
                else
                {
                    gap += step;
                    if (gap >= need) break;
                }
            }
            x1 = stopX;
            gap = 0; stopX = Math.Min(W - 1, box.X);
            for (int x = box.X - 1; x >= 0 && x >= box.X - limit; x--)
            {
                if (col[x] >= 26) { stopX = x; gap = 0; }
                else
                {
                    gap += step;
                    if (gap >= need) break;
                }
            }
            x0 = stopX;
            if (x1 < x0) return box;

            int y0 = sy, y1 = ey;
            return Rectangle.FromLTRB(Math.Max(0, x0), Math.Max(0, y0),
                Math.Min(bmp.Width, x1 + 1), Math.Min(bmp.Height, y1 + 1));
        }

        // 逐行翻译并原地铺译文；返回画出来的行数
        internal int TranslateAndShow(List<OcrSegment> segs, Rectangle screenRect, Bitmap bmp, bool layoutPre = false)
        {
            if (segs == null || segs.Count == 0) return 0;
            if (bmp != null && Environment.GetEnvironmentVariable("QRSNAP_DBG_CROP") == "1")
            {
                try { bmp.Save(@"E:\下载\豆包\_mt\crop.png", System.Drawing.Imaging.ImageFormat.Png); } catch { }
            }
            // 目标语言固定中文：整套产品只做「外语 → 中文」，不再提供译成什么语言的选择
            string tgt = "zh";
            _mtSkipReason = null;
            _mtSkipShort = null;
            _mtFail = null;
            _aiMtLines = 0;
            _aiCheckLines = 0;
            _aiCheckMs = 0;
            _aiMergeGroups = 0;
            _aiMtMs = 0;
            _layoutActive = layoutPre;
            _layoutLines = layoutPre ? segs.Count : 0;
            if (!layoutPre) _layoutObstacles = null;
            if (layoutPre) NativeShell.Log("[layout] precomputed blocks=" + segs.Count);

            // 版面强化：AI 翻译和纯离线翻译都先做「分栏/徽章/段落」切分。
            // 只依赖本地 PP-OCR（默认极速档），模型缺失就静默回退原链路。
            if (!layoutPre && AppConfig.AiLayout && bmp != null)
            {
                try
                {
                    string llog;
                    List<Rectangle> obs;
                    List<OcrSegment> ls = Ocr.LayoutSegments(bmp, out obs, out llog);
                    if (ls != null && ls.Count >= 3)
                    {
                        segs = ls;
                        _layoutActive = true;
                        _layoutLines = ls.Count;
                        _layoutObstacles = obs;
                        NativeShell.Log("[layout] blocks=" + ls.Count + " " + OneLine(llog, 220));
                        // 版面链路会重新识字，原来在 Ocr.Recognize 里做的 AI 纠错会被覆盖掉。
                        // 离线翻译（AiMt=0）时在这里补一次，让 AI 识字强化真正作用到最终译文。
                        if (AppConfig.AiOcr && !AppConfig.AiMt)
                        {
                            try { AiTextFix.Polish(segs); }
                            catch (Exception exFix) { NativeShell.Log("[layout] aiocr fail " + exFix.Message); }
                        }
                    }
                    else NativeShell.Log("[layout] skip " + OneLine(llog, 160));
                }
                catch (Exception exLayout)
                {
                    NativeShell.Log("[layout] fail " + exLayout.Message);
                }
            }

            StringBuilder all = new StringBuilder();
            for (int i = 0; i < segs.Count; i++) all.Append(segs[i].Text).Append('\n');
            string src = LangDetect.Detect(all.ToString());
            if (string.IsNullOrEmpty(src))
            {
                _mtSkipReason = "没有识别到文字";
                _mtSkipShort = "没识别到文字";
                return 0;
            }
            // 不再抛异常：抛出去只会变成「只识字、不翻译」，用户根本不知道是跳过了
            if (src == tgt)
            {
                _mtSkipReason = "选区文字本身就是" + MtRouter.NameOf(tgt) + "，已跳过翻译";
                _mtSkipShort = "选区已是" + MtRouter.NameOf(tgt);
                Log("translate skip " + _mtSkipReason);
                return 0;
            }

            // AI 短句合并：先把窄栏被拆开的续行并成一句，再翻译
            if (AppConfig.AiMerge && !AppConfig.AiMt && JadeHost.AiReadyNow())
            {
                try
                {
                    List<List<int>> groups = AiBoost.MergeShortLines(segs);
                    if (groups != null)
                    {
                        int before = segs.Count;
                        List<OcrSegment> mergedSegs = AiBoost.ApplyGroups(segs, groups);
                        if (mergedSegs != null && mergedSegs.Count < before)
                        {
                            segs = mergedSegs;
                            _layoutLines = segs.Count;
                            for (int g = 0; g < groups.Count; g++) if (groups[g].Count >= 2) _aiMergeGroups++;
                            Log("[aimerge] " + before + " -> " + segs.Count + " 行");
                        }
                    }
                }
                catch (Exception exMerge)
                {
                    Log("[aimerge] fail " + exMerge.Message);
                }
            }

            List<TransOverlay.Item> items = new List<TransOverlay.Item>();
            List<MtRealistic.Block> blocks = new List<MtRealistic.Block>();
            // 截图可能被缩放（高 DPI 下 crop 比选区小），覆盖层是按屏幕像素画的，坐标要按比例还原
            double kx = 1.0, ky = 1.0;
            if (bmp != null && bmp.Width > 0 && bmp.Height > 0 && screenRect.Width > 0 && screenRect.Height > 0)
            {
                kx = screenRect.Width / (double)bmp.Width;
                ky = screenRect.Height / (double)bmp.Height;
                Log("mt scale kx=" + kx.ToString("0.###") + " ky=" + ky.ToString("0.###")
                    + " crop=" + bmp.Width + "x" + bmp.Height + " sel=" + screenRect.Width + "x" + screenRect.Height);
            }
            string err = null;
            string lastRoute = "";
            string profile = AppConfig.NormalizeMtProfile(AppConfig.MtProfile);
            bool realMode = AppConfig.NormalizeMtMode(AppConfig.MtMode) == AppConfig.MtModeRealistic;
            // 效率模式也能吃版面分段：段落框会交给 TransOverlay 做「整块遮罩 + 自动折行」，
            // 不再强制切拟真；用户选哪档就是哪档。
            RunPumped(delegate()
            {
                try
                {
                    string[] trs = new string[segs.Count];
                    if (AppConfig.AiMt)
                    {
                        // 接管：这一轮完全不问本地翻译模型
                        long tAi = Environment.TickCount;
                        Dictionary<int, string> ai = AiBoost.TranslateAll(segs, src, out err);
                        _aiMtMs = Environment.TickCount - tAi;
                        if (ai == null)
                        {
                            // 用户要求：AI 报错就直接报错，不偷偷退回本地模型
                            err = "AI 翻译接管失败：" + err + "。已停止，没有改用本地模型";
                            return;
                        }
                        _aiMtLines = ai.Count;
                        for (int i = 0; i < segs.Count; i++)
                            trs[i] = ai.ContainsKey(i) ? LayoutBoost.PostFix(ai[i]) : null;
                        lastRoute = "ai";
                    }
                    else
                    {
                        MtRouter router = MtRunner.Router;
                        string[] plan = router.PlanFor(src, tgt, AppConfig.MtPair, profile);
                        if (plan.Length == 0)
                        {
                            err = !string.IsNullOrEmpty(router.LastPlanError)
                                ? router.LastPlanError
                                : "当前档位没有可用模型";
                            return;
                        }
                        List<int> need = new List<int>();
                        for (int i = 0; i < segs.Count; i++)
                        {
                            OcrSegment s = segs[i];
                            if (s.Text == null || s.Text.Trim().Length == 0) continue;
                            string pre = LayoutBoost.PreTranslate(s.Text);
                            if (pre != null) { trs[i] = pre; continue; }
                            need.Add(i);
                        }

                        if (plan[0] == "gguf")
                        {
                            // 效率档通常只有一两行字幕：逐行翻，避免 0.5B 批量时漏行
                            if (profile == AppConfig.MtProfileEfficiency)
                            {
                                for (int i = 0; i < need.Count; i++)
                                {
                                    string tr = null;
                                    try { tr = LlamaMt.Translate(plan[1], segs[need[i]].Text, tgt); }
                                    catch (Exception) { tr = null; }
                                    if (!string.IsNullOrEmpty(tr)) trs[need[i]] = LayoutBoost.PostFix(tr);
                                }
                            }
                            else
                            {
                            // 纯离线大模型：整屏按 8 行一批送，少几十次 HTTP + 前缀计算
                            lastRoute = "gguf · " + System.IO.Path.GetFileNameWithoutExtension(plan[1]);
                            int k = 0;
                            while (k < need.Count)
                            {
                                List<int> ids = new List<int>();
                                List<string> texts = new List<string>();
                                int chars = 0;
                                while (k < need.Count && ids.Count < 8 && chars < 650)
                                {
                                    string t = segs[need[k]].Text ?? "";
                                    ids.Add(need[k]); texts.Add(t); chars += t.Length; k++;
                                }
                                List<string> got = null;
                                try { got = LlamaMt.TranslateBatch(plan[1], texts, tgt); }
                                catch (Exception) { got = null; }
                                if (got != null && got.Count == ids.Count)
                                {
                                    for (int i = 0; i < ids.Count; i++)
                                        trs[ids[i]] = LayoutBoost.PostFix(got[i]);
                                }
                                else
                                {
                                    for (int i = 0; i < ids.Count; i++)
                                    {
                                        string route;
                                        string tr = router.Translate(texts[i], src, tgt, AppConfig.MtPair, profile, out route);
                                        lastRoute = route;
                                        if (!string.IsNullOrEmpty(tr)) trs[ids[i]] = LayoutBoost.PostFix(tr);
                                    }
                                }
                            }
                        }
                        }
                        else if (need.Count > 1)
                        {
                            // 纯离线小模型：多 worker 并行翻，避免几十个块排队
                            List<string> texts = new List<string>();
                            for (int i = 0; i < need.Count; i++) texts.Add(segs[need[i]].Text ?? "");
                            string route;
                            string[] outs = router.TranslateMany(texts, src, tgt, AppConfig.MtPair, profile, out route);
                            lastRoute = route;
                            for (int i = 0; i < need.Count; i++)
                                if (!string.IsNullOrEmpty(outs[i])) trs[need[i]] = LayoutBoost.PostFix(outs[i]);
                        }
                        else
                        {
                            for (int i = 0; i < need.Count; i++)
                            {
                                string route;
                                string tr = router.Translate(segs[need[i]].Text, src, tgt, AppConfig.MtPair, profile, out route);
                                lastRoute = route;
                                if (!string.IsNullOrEmpty(tr)) trs[need[i]] = LayoutBoost.PostFix(tr);
                            }
                        }
                    }
                    // AI 译文质检：本地翻译后只重翻低质量行
                    if (!AppConfig.AiMt && AppConfig.AiCheck && JadeHost.AiReadyNow())
                    {
                        try
                        {
                            long tc = Environment.TickCount;
                            string cErr = null;
                            Dictionary<int, string> fixedTr = AiBoost.CheckTranslations(segs, trs, src, out cErr);
                            if (fixedTr != null)
                            {
                                foreach (KeyValuePair<int, string> kv in fixedTr)
                                    trs[kv.Key] = LayoutBoost.PostFix(kv.Value);
                                _aiCheckLines = fixedTr.Count;
                            }
                            _aiCheckMs = Environment.TickCount - tc;
                            if (!string.IsNullOrEmpty(cErr)) Log("[aicheck] " + cErr);
                        }
                        catch (Exception exCheck)
                        {
                            Log("[aicheck] fail " + exCheck.Message);
                        }
                    }

                    for (int i = 0; i < segs.Count; i++)
                    {
                        OcrSegment s = segs[i];
                        string tr = trs[i];
                        if (tr == null || tr.Trim().Length == 0) continue;
                        TransOverlay.Item it = new TransOverlay.Item();
                        // 拟真模式不要 InkBounds：那个外扩是为「实色遮罩盖住整行」服务的，
                        // 在深色/复杂底图上极易把整行甚至整片背景都吃进来，导致一个框跨好几列文字。
                        // 擦除+重绘只需要原文框本身，MtRealistic 内部再按 2px 微扩。
                        Rectangle rb = (realMode || _layoutActive) ? s.Box : InkBounds(bmp, s.Box);
                        if (Environment.GetEnvironmentVariable("QRSNAP_DBG_CROP") == "1")
                            Log("ink box=" + s.Box.X + "," + s.Box.Y + "," + s.Box.Width + "," + s.Box.Height
                                + " -> " + rb.X + "," + rb.Y + "," + rb.Width + "," + rb.Height);
                        // 效率模式：底板也要躲开右侧头像/表情，否则一块实色会把图标一起盖掉
                        Rectangle ib = realMode ? rb : TrimByObstacles(rb, _layoutObstacles);
                        it.Box = ScaleRect(ib, kx, ky);
                        it.Angle = s.Angle;
                        it.Text = tr;
                        if (!realMode)
                        {
                            // 底板取原图底色：效率模式不再是黑块，而是「融进背景、只留中文」
                            Color bgc = MtRealistic.SampleBackground(bmp, s.Box);
                            it.BgColor = bgc;
                            int bl = (bgc.R * 77 + bgc.G * 151 + bgc.B * 28) >> 8;
                            it.TextColor = bl < 128 ? Color.FromArgb(246, 249, 255) : Color.FromArgb(28, 30, 34);
                        }
                        if (!realMode && _layoutActive)
                        {
                            // 段落块：底板按原文每一行分别铺，排版区从首行左边界起，
                            // 这样徽章和右侧图标都不会被整块盖住。
                            it.Box = ScaleRect(s.Box, kx, ky);
                            it.TextBox = ScaleRect(TrimByObstacles(s.Box, _layoutObstacles), kx, ky);
                            List<Rectangle> mask = new List<Rectangle>();
                            if (s.SourceBoxes != null && s.SourceBoxes.Count > 0)
                            {
                                for (int k = 0; k < s.SourceBoxes.Count; k++)
                                {
                                    Rectangle sr = s.SourceBoxes[k];
                                    sr.Inflate(2, 2);
                                    mask.Add(ScaleRect(sr, kx, ky));
                                }
                            }
                            else mask.Add(ScaleRect(s.Box, kx, ky));
                            it.MaskRects = mask;
                        }
                        items.Add(it);

                        // 拟真模式要的是原图坐标的框，直接喂给擦除/回填
                        MtRealistic.Block blk = new MtRealistic.Block();
                        blk.Box = rb;
                        blk.Text = tr;
                        blk.Angle = s.Angle;
                        blk.Group = -1;
                        if (_layoutActive)
                        {
                            blk.SourceBoxes = s.SourceBoxes;
                            blk.PreGrouped = true;
                            blk.Obstacles = _layoutObstacles;
                        }
                        blocks.Add(blk);
                        Log("mt line box=" + s.Box.X + "," + s.Box.Y + "," + s.Box.Width + "," + s.Box.Height
                            + " ang=" + s.Angle + " route=" + lastRoute + " | " + s.Text + " => " + tr);
                    }
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }
            }, 260);

            if (err != null) throw new Exception(err);
            if (items.Count == 0) return 0;

            // 自动复制用：按译文块顺序拼成纯文本
            try
            {
                StringBuilder mtb = new StringBuilder();
                for (int i = 0; i < items.Count; i++)
                {
                    string tx = items[i].Text;
                    if (string.IsNullOrEmpty(tx)) continue;
                    if (mtb.Length > 0) mtb.Append('\n');
                    mtb.Append(tx);
                }
                _lastMtText = mtb.ToString();
            }
            catch (Exception) { }

            // 拟真：擦除原字 + 回填译文，输出整块成品图铺回选区
            if (realMode)
            {
                if (bmp == null) throw new Exception("拟真模式拿不到原图，切回效率模式试试");
                MtRealistic.Stats st;
                Bitmap outImg = MtRealistic.Render(bmp, blocks, out st);
                if (outImg != null && Environment.GetEnvironmentVariable("QRSNAP_DBG_REAL") == "1")
                {
                    // 干净的成品图 + 带框的调试图，各存各的，验收看成品图
                    try { ((Bitmap)outImg.Clone()).Save(@"E:\下载\豆包\_mt\real-overlay.png", System.Drawing.Imaging.ImageFormat.Png); } catch { }
                    try
                    {
                        using (Bitmap dbg = new Bitmap(outImg.Width, outImg.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                        {
                            using (Graphics dg = Graphics.FromImage(dbg))
                            {
                                dg.DrawImage(outImg, 0, 0);
                                using (Pen pen = new Pen(Color.FromArgb(200, 255, 60, 60), 1f))
                                    foreach (MtRealistic.Block b in blocks)
                                        dg.DrawRectangle(pen, b.Box.X, b.Box.Y, Math.Max(1, b.Box.Width - 1), Math.Max(1, b.Box.Height - 1));
                            }
                            dbg.Save(@"E:\下载\豆包\_mt\real-debug.png", System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    catch { }
                }

                if (outImg != null && (kx != 1.0 || ky != 1.0))
                {
                    Bitmap scaled = new Bitmap(screenRect.Width, screenRect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (Graphics sg = Graphics.FromImage(scaled))
                    {
                        sg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        sg.DrawImage(outImg, new Rectangle(0, 0, scaled.Width, scaled.Height));
                    }
                    outImg.Dispose();
                    outImg = scaled;
                }
                TransOverlay.ShowImage(screenRect, outImg);
                try { _lastOverlayBitmap = (Bitmap)outImg.Clone(); } catch (Exception) { }
                Log("translate realistic lines=" + st.Lines + " hard=" + st.Hard + " wrap=" + st.Wrapped
                    + " src=" + src + " tgt=" + tgt + " prof=" + profile + " route=" + lastRoute);
                return st.Lines;
            }

            // 效率模式：顺手把「截图 + 遮罩」合成一张图，供重复截图缓存直接贴
            try
            {
                if (bmp != null)
                {
                    using (Bitmap baseBmp = new Bitmap(screenRect.Width, screenRect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (Graphics bg = Graphics.FromImage(baseBmp))
                        {
                            bg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            bg.DrawImage(bmp, new Rectangle(0, 0, baseBmp.Width, baseBmp.Height));
                        }
                        _lastOverlayBitmap = TransOverlay.Compose(baseBmp, items);
                    }
                }
            }
            catch (Exception) { }

            TransOverlay.Show(screenRect, items);
            Log("translate shown lines=" + items.Count + " src=" + src + " tgt=" + tgt);
            if (items.Count == 0)
            {
                _mtSkipReason = "翻译引擎没有产出译文（模型可能还在加载），重试一次";
                _mtSkipShort = "引擎没出结果，重试";
            }
            return items.Count;
        }

        public void DisposeShell()
        {
            try
            {
                Native.UnregisterHotKey(Handle, ID_QR);
                Native.UnregisterHotKey(Handle, ID_OCR);
                Native.UnregisterHotKey(Handle, ID_MT);
                Native.UnregisterHotKey(Handle, ID_HIDE);
            }
            catch (Exception) { }
        }
    }

    // 翻译模型路由的进程级单例：模型走 MT Model 目录，第一次用到才加载
    internal static class MtRunner
    {
        private static MtRouter _router;
        private static string _root;
        private static readonly object L = new object();

        public static MtRouter Router
        {
            get
            {
                lock (L)
                {
                    string root = AppConfig.MtRoot();
                    if (_router == null || !string.Equals(_root, root, StringComparison.OrdinalIgnoreCase))
                    {
                        if (_router != null) { try { _router.Dispose(); } catch (Exception) { } }
                        _router = new MtRouter(root);
                        _root = root;
                    }
                    return _router;
                }
            }
        }

        public static bool Ready
        {
            get
            {
                try { return Router.AvailablePairs().Count > 0; }
                catch (Exception) { return false; }
            }
        }

        public static bool GameReady
        {
            get
            {
                try { return Router.GameReady; }
                catch (Exception) { return false; }
            }
        }

        public static List<string> Pairs()
        {
            try { return Router.AvailablePairs(); }
            catch (Exception) { return new List<string>(); }
        }

        // 三件套缺哪几件（UI 用来置灰和提示，缺一件就整体禁用翻译）
        public static List<string> MissingKit()
        {
            try { return Router.MissingKit(); }
            catch (Exception) { return new List<string>(new string[] { "翻译模型" }); }
        }

        // 模型列表：界面上「翻译模型」那一栏用（含没装全的，置灰显示）
        public static List<MtModelInfo> Models()
        {
            try { return Router.Models(); }
            catch (Exception) { return new List<MtModelInfo>(); }
        }

        // 换了模型目录内容后重新建一次路由，别让旧缓存挡住新装的模型
        public static void Invalidate()
        {
            lock (L)
            {
                if (_router != null) { try { _router.Dispose(); } catch (Exception) { } }
                _router = null;
                _root = null;
            }
        }

        public static void TrimIdle(int seconds)
        {
            try { if (_router != null) _router.TrimIdle(seconds); } catch (Exception) { }
        }
    }

    // ================= Jade 宿主 =================
    internal static class JadeHost
    {
        internal static uint WindowId;
        internal static bool Started;
        internal const string WindowTitle = "XuYuan-SnapScan · 二维码 / 文字识别";
        private static NativeShell _shell;
        private static readonly List<IntPtr> _alloc = new List<IntPtr>();
        private static readonly List<JadeNative.IpcCallback> _cbs = new List<JadeNative.IpcCallback>();
        private static string _uiDir;
        private static string _dllDir;
        private static Thread _uiThread;

        private static IntPtr U(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? string.Empty);
            IntPtr p = Marshal.AllocHGlobal(b.Length + 1);
            Marshal.Copy(b, 0, p, b.Length);
            Marshal.WriteByte(p, b.Length, 0);
            _alloc.Add(p);
            return p;
        }

        private static string ReadUtf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return string.Empty;
            int n = 0;
            while (Marshal.ReadByte(p, n) != 0 && n < 1 << 20) n++;
            if (n == 0) return string.Empty;
            byte[] raw = new byte[n];
            Marshal.Copy(p, raw, 0, n);
            return Encoding.UTF8.GetString(raw);
        }

        // ---------- 启动 ----------
        public static void Run()
        {
            AppConfig.Load();

            PrepareDll();
            PrepareUi();

            _shell = new NativeShell();
            _shell.OnCaptured = delegate(string mode, string json)
            {
                ShowWindow(RestoreToFront(mode));
                Send("capture-done", json);
            };
            _uiThread = new Thread(delegate()
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs ex)
                {
                    Log("UI EX " + ex.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs ex)
                {
                    Log("DOMAIN EX " + ex.ExceptionObject);
                };
                Application.Run(_shell);
            });
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.IsBackground = true;
            _uiThread.Start();

            StartJade();
        }

        private static void PrepareDll()
        {
            Assembly self = Assembly.GetExecutingAssembly();
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XuYuan-SnapScan");
            _dllDir = Path.Combine(baseDir, "dll");
            Directory.CreateDirectory(_dllDir);
            string target = Path.Combine(_dllDir, "JadeView_x64.dll");

            // 优先用 exe 同目录的 dll（开发时），否则从内嵌资源释放
            string beside = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "JadeView_x64.dll");
            if (File.Exists(beside) && new FileInfo(beside).Length > 1024 * 1024)
            {
                target = beside;
            }
            else
            {
                try
                {
                    Stream s = self.GetManifestResourceStream("JADEVIEWDLL");
                    if (s != null)
                    {
                        bool need = true;
                        if (File.Exists(target) && new FileInfo(target).Length == s.Length) need = false;
                        if (need)
                        {
                            using (s)
                            using (FileStream fs = new FileStream(target, FileMode.Create, FileAccess.Write))
                            {
                                byte[] buf = new byte[81920];
                                int r;
                                while ((r = s.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, r);
                            }
                        }
                        else s.Dispose();
                    }
                }
                catch (Exception) { }
            }

            JadeNative.Preload(target);
        }

        private static void PrepareUi()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XuYuan-SnapScan");
            _uiDir = Path.Combine(baseDir, "ui");
            Directory.CreateDirectory(_uiDir);
            Extract("WEB_INDEX", Path.Combine(_uiDir, "index.html"));
            Extract("WEB_CSS", Path.Combine(_uiDir, "app.css"));
            Extract("WEB_JS", Path.Combine(_uiDir, "app.js"));
            // 界面用的软件背景图与图标，打包在 exe 里，运行时释放到 ui 目录
            string bg = Path.Combine(_uiDir, "bg.jpg");
            string ico = Path.Combine(_uiDir, "app.ico");
            if (!File.Exists(bg) || new FileInfo(bg).Length == 0) Extract("Back.jpg", bg);
            if (!File.Exists(ico) || new FileInfo(ico).Length == 0) Extract("App.ico", ico);
        }

        private static void Extract(string res, string path)
        {
            try
            {
                Assembly self = Assembly.GetExecutingAssembly();
                Stream s = self.GetManifestResourceStream(res);
                if (s == null) return;
                using (s)
                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    byte[] buf = new byte[81920];
                    int r;
                    while ((r = s.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, r);
                }
            }
            catch (Exception) { }
        }

        private static void StartJade()
        {
            JadeNative.IpcCallback appReady = AppReady;
            _cbs.Add(appReady);
            JadeNative.jade_on(U("app-ready"), appReady);

            // 关掉主窗口 = 退出程序（没有托盘图标，也没有后台常驻）
            JadeNative.IpcCallback onClosed = OnWindowClosed;
            _cbs.Add(onClosed);
            JadeNative.jade_on(U("window-closed"), onClosed);
            JadeNative.IpcCallback onClosing = OnWindowClosing;
            _cbs.Add(onClosing);
            JadeNative.jade_on(U("window-closing"), onClosing);

            Bind("state", OnState);
            Bind("config", OnConfig);
            Bind("recheck", OnRecheck);
            Bind("aiverify", OnAiVerify);
            Bind("pickwx", OnPickWx);
            Bind("importmodel", OnImportModel);
            Bind("cacheclear", OnCacheClear);
            Bind("gamepick", OnGamePick);
            Bind("gamedel", OnGameDel);
            Bind("gameclear", OnGameClear);
            Bind("capture", OnCapture);
            Bind("clipcopy", OnCopy);
            Bind("win", OnWin);
            Bind("hotkey", OnHotkey);
            Bind("hidetrans", OnHideTrans);

            string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XuYuan-SnapScan", "jade-data");
            Directory.CreateDirectory(dataDir);
            string logPath = Path.Combine(dataDir, "jade.log");

            int ok = JadeNative.JadeView_init(0, U(logPath), U(dataDir), U("XuYuan-SnapScan"), U("xuyuan-snapscan"), 1);
            if (ok != 1)
            {
                MessageBox.Show("应用初始化失败（JadeView 返回 " + ok + "）。\n可能是另一个实例正在运行。",
                    "XuYuan-SnapScan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Started = true;
            JadeNative.run_message_loop();
            Started = false;

            try { Ocr.Shutdown(); } catch (Exception) { }
        }

        private static void Bind(string channel, JadeNative.IpcCallback cb)
        {
            _cbs.Add(cb);
            JadeNative.register_ipc_handler(U(channel), cb);
        }

        private static IntPtr Reply(string json)
        {
            return JadeNative.jade_text_create(U(json ?? string.Empty));
        }

        internal static void Log(string msg)
        {
            try { NativeShell.Log(msg); } catch (Exception) { }
        }

        internal static void RunOnUi(Action act)
        {
            NativeShell shell = _shell;
            if (shell == null) { try { act(); } catch (Exception) { } return; }
            try
            {
                if (shell.InvokeRequired) shell.BeginInvoke(act);
                else act();
            }
            catch (Exception)
            {
                try { act(); } catch (Exception) { }
            }
        }

        private static IntPtr AppReady(uint win, IntPtr msg)
        {
            if (win != 1)
            {
                MessageBox.Show("初始化未完成：" + ReadUtf8(msg), "XuYuan-SnapScan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return IntPtr.Zero;
            }

            StringBuilder sb = new StringBuilder(512);
            JadeNative.set_protocol_service_path(U(_uiDir), sb, sb.Capacity, 0);

            // 后台预热 PP-OCR 版面检测器，第一次翻译少等 1~2 秒
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object s) { Ocr.WarmLayout(); });

            JadeNative.Options o = new JadeNative.Options();
            o.title = U(WindowTitle);
            o.width = 980; o.height = 660; o.resizable = 1;
            o.frame_style = U("normal");
            o.background_color = U("#F5F8FFFF");
            o.theme = U("Light");
            o.maximizable = 1; o.minimizable = 1;
            o.x = -1; o.y = -1; o.min_width = 860; o.min_height = 560;
            o.focus = 1; o.use_page_icon = 1;

            JadeNative.Settings st = new JadeNative.Settings();
            st.allow_right_click = 0; st.allow_fullscreen = 1; st.focused = 1;

            WindowId = JadeNative.create_webview_window(U(sb.ToString() + "/index.html"), 0,
                StructToPtr(o), StructToPtr(st));

            try
            {
                if (IsWin11()) JadeNative.set_window_backdrop(WindowId, U("mica"));
            }
            catch (Exception) { }

            AiStart();   // 每次启动都自动校验一次，不通过就一直显示不可用

            return IntPtr.Zero;
        }

        // JadeView 关窗回调：主窗口关掉就直接退出，不留后台、不留托盘
        private static IntPtr OnWindowClosing(uint win, IntPtr msg)
        {
            return IntPtr.Zero;   // 0 = 允许关闭
        }

        private static IntPtr OnWindowClosed(uint win, IntPtr msg)
        {
            if (WindowId == 0 || win != WindowId) return IntPtr.Zero;
            ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(120); Quit(); });
            return IntPtr.Zero;
        }
        private static IntPtr StructToPtr(object obj)
        {
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(obj.GetType()));
            Marshal.StructureToPtr(obj, p, false);
            _alloc.Add(p);
            return p;
        }

        internal static string EngineLabel(string e)
        {
            if (e == AppConfig.EngineWeChat) return "微信 OCR";
            if (e == AppConfig.EnginePpTiny) return "PP-OCR 极速";
            if (e == AppConfig.EnginePpMedium) return "PP-OCR 高精";
            return "Windows OCR";
        }

        // 设置里选项后面的括号说明，短到不会撑破界面
        // 注意：微信 OCR 也是本地模型、不联网，所以不再给 PP-OCR 单独标“离线”
        internal static string EngineNote(string e)
        {
            if (e == AppConfig.EngineWeChat) return "推荐";
            if (e == AppConfig.EnginePpTiny) return "轻量 · 快";
            if (e == AppConfig.EnginePpMedium) return "高精度";
            return "系统兜底";
        }

        internal static bool EngineReady(string e)
        {
            if (e == AppConfig.EngineWeChat)
            {
                EngineProbe p = Ocr.Probe();
                return p != null && p.WeChatUsable;
            }
            if (AppConfig.IsLocalEngine(e)) return Ocr.LocalReady(e);
            return true;
        }

        // ---------- 状态 ----------
        // 只把 key 的末 4 位暴露给前端，完整 key 始终留在本机 ini 里
        internal static string KeyTail(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (key.Length <= 4) return "****";
            return "****" + key.Substring(key.Length - 4);
        }

        // ---------- AI 大模型连通性校验 ----------
        // 只回答一件事：现在这套「地址 + 模型名 + Key」到底能不能用。结果只放内存，每次启动重测，不落 ini。
        private static readonly object _aiLock = new object();
        private static string _aiStatus = "unknown";   // unset / checking / unknown / ok / badkey / badmodel / offline
        private static string _aiMsg = "";
        private static string _aiAt = "";
        private static string _aiSig = "";
        private static bool _aiBusy;

        private static string AiSig()
        {
            return AppConfig.AiProvider + "|" + AppConfig.AiBase + "|" + AppConfig.AiModel + "|" + AppConfig.AiKey;
        }

        // 给前端的状态：配置改过就当没测过，避免旧的好状态骗人（比如换了 key 还显示就绪）
        private static void AiStatusPick(out string status, out string msg, out string at)
        {
            lock (_aiLock)
            {
                if (string.IsNullOrEmpty(AppConfig.AiKey))
                {
                    status = "unset"; msg = "还没填 Key"; at = ""; return;
                }
                if (_aiSig == AiSig() && _aiStatus != "unknown")
                {
                    status = _aiStatus; msg = _aiMsg; at = _aiAt; return;
                }
                if (_aiBusy) { status = "checking"; msg = "正在校验…"; at = ""; return; }
                status = "unknown"; msg = "还没校验"; at = "";
            }
        }

        // 起一个后台线程测一次；同一时刻只允许一个在跑
        private static void AiStart()
        {
            string sig = AiSig();
            lock (_aiLock)
            {
                if (string.IsNullOrEmpty(AppConfig.AiKey))
                {
                    _aiStatus = "unset"; _aiMsg = "还没填 Key"; _aiAt = ""; _aiSig = sig; return;
                }
                if (_aiBusy) return;
                _aiBusy = true;
                _aiStatus = "checking"; _aiMsg = "正在校验…";
            }
            string baseUrl = AppConfig.AiBase;
            string model = AppConfig.AiModel;
            string key = AppConfig.AiKey;
            Thread th = new Thread(delegate()
            {
                string st, mg;
                try { AiProbe(baseUrl, model, key, out st, out mg); }
                catch (Exception ex) { st = "offline"; mg = "校验时出错：" + ex.Message; }
                lock (_aiLock)
                {
                    _aiStatus = st; _aiMsg = mg; _aiAt = DateTime.Now.ToString("HH:mm:ss");
                    _aiSig = sig; _aiBusy = false;
                }
                Log("[ai] verify " + st + " | " + mg);
            });
            th.IsBackground = true;
            th.Start();
        }

        private static void AiProbe(string baseUrl, string model, string key, out string status, out string msg)
        {
            status = "offline"; msg = "未连接：接口地址是空的或暂时不通";
            string b = (baseUrl ?? "").Trim().TrimEnd('/');
            if (b.Length == 0) { status = "offline"; msg = "接口地址是空的"; return; }
            if (string.IsNullOrEmpty(model)) { status = "badmodel"; msg = "模型名称是空的"; return; }

            int code = 0; string body = ""; string hit = b;

            // 第一步 /models：不花额度，能同时验证 key 有效性和模型名是否存在
            // 官方两种写法都吃（https://api.deepseek.com 和 …/v1），兼容网关多半只实现 /v1，
            // 所以不带 /v1 时先试一次，404/405 再补 /v1 试一次，免得把「地址写法」误判成「连不上」
            string[] cands = AiBaseCandidates(b);
            for (int i = 0; i < cands.Length; i++)
            {
                try { AiHttp("GET", cands[i] + "/models", key, null, out code, out body); }
                catch (Exception) { status = "offline"; msg = "未连接 " + b + "（网络不通或地址写错）"; return; }
                hit = cands[i];
                if (code == 0) break;
                if (code != 404 && code != 405) break;
            }

            if (code == 0) {
                status = "offline";
                msg = "未连接 " + b + "（直连和系统代理都没通，看看代理是否放行这个地址）";
                return;
            }
            if (code == 401 || code == 403) { status = "badkey"; msg = "Key 无效或不被接受（HTTP " + code + "）"; return; }

            if (code == 200)
            {
                string[] ids = AiIds(body);
                for (int i = 0; i < ids.Length; i++)
                {
                    if (string.Equals(ids[i], model, StringComparison.OrdinalIgnoreCase)) { status = "ok"; msg = "就绪"; return; }
                }
                if (ids.Length > 0)
                {
                    status = "badmodel";
                    msg = "Key 能用，但没有「" + model + "」这个模型；这个账号能用的是 " + AiPreview(ids, 4);
                    return;
                }
            }
            else if (code != 404 && code != 405)
            {
                string em = AiErrMsg(body);
                status = "offline";
                msg = "接口返回 HTTP " + code + (em.Length > 0 ? "：" + em : "");
                return;
            }

            // 第二步：有些兼容网关不实现 /models，直接发一条最小对话请求试（不思考、只出 1 个 token）
            string payload = "{\"model\":\"" + AiEsc(model) + "\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"max_tokens\":1,\"temperature\":0,\"stream\":false}";
            try { AiHttp("POST", hit + "/chat/completions", key, payload, out code, out body); }
            catch (Exception) { status = "offline"; msg = "未连接 " + b + "（网络不通或地址写错）"; return; }

            if (code == 0) { status = "offline"; msg = "未连接 " + b + "（直连和系统代理都没通，看看代理是否放行这个地址）"; return; }
            if (code == 401 || code == 403) { status = "badkey"; msg = "Key 无效或不被接受（HTTP " + code + "）"; return; }
            if (code == 200) { status = "ok"; msg = "就绪"; return; }

            string m = AiErrMsg(body);
            if (code == 400 || code == 404)
            {
                status = "badmodel";
                msg = "调用被拒（HTTP " + code + "）" + (m.Length > 0 ? "：" + m : "，多半是模型名称不对");
                return;
            }
            status = "offline";
            msg = "调用失败（HTTP " + code + "）" + (m.Length > 0 ? "：" + m : "");
        }

        private static void AiHttp(string method, string url, string key, string payload, out int code, out string body)
        {
            // 先按系统代理走（很多机器只有挂了代理才能上外网），代理这层连不上再直连兜底一次。
            // 活的代理多半是「CONNECT 建隧道但握手被拒」，那种情况 code=0，正好触发兜底。
            AiHttpOnce(method, url, key, payload, false, out code, out body);
            if (code == 0 && !AiIsLocalUrl(url))
                AiHttpOnce(method, url, key, payload, true, out code, out body);
        }

        private static bool AiIsLocalUrl(string url)
        {
            return url.IndexOf("127.0.0.1", StringComparison.Ordinal) >= 0
                || url.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0
                || url.IndexOf("://[::1]", StringComparison.Ordinal) >= 0;
        }

        // 地址写法容错：不带 /v1 的再补一份 /v1 候选，避免「地址少写一段」被当成连不上
        private static string[] AiBaseCandidates(string b)
        {
            string t = (b ?? "").Trim().TrimEnd('/');
            if (t.Length == 0) return new string[] { "" };
            if (t.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return new string[] { t };
            return new string[] { t, t + "/v1" };
        }

        // 给识字增强用：校验没到「就绪」就不启用，避免拿一个坏配置去跑业务
        internal static bool AiReadyNow()
        {
            string st, mg, at;
            AiStatusPick(out st, out mg, out at);
            return st == "ok";
        }

        // 自检用：同步跑一次校验，最多等 maxMs，返回是否就绪
        internal static bool AiVerifySync(int maxMs)
        {
            AiStart();
            int t0 = Environment.TickCount;
            while (Environment.TickCount - t0 < maxMs)
            {
                string st, mg, at;
                AiStatusPick(out st, out mg, out at);
                if (st != "checking" && st != "unknown") return st == "ok";
                Thread.Sleep(300);
            }
            return false;
        }

        // 给识字增强用：发一次对话请求，沿用校验那套代理 / TLS / 超时策略
        internal static int AiPostChat(string payload, out string body)
        {
            body = "";
            string b = (AppConfig.AiBase ?? "").Trim().TrimEnd('/');
            if (b.Length == 0) return 0;
            string[] cands = AiBaseCandidates(b);
            int code = 0;
            for (int i = 0; i < cands.Length; i++)
            {
                // 业务请求直连优先：公网地址直连通常两秒内回来，
                // 先走系统代理的话，一个关掉的代理会把每段请求拖成两个超时。
                try { AiHttpOnce("POST", cands[i] + "/chat/completions", AppConfig.AiKey, payload, true, 16000, out code, out body); }
                catch (Exception ex) { code = 0; body = ex.Message; }
                if (code == 0 && !AiIsLocalUrl(cands[i]))
                {
                    try { AiHttpOnce("POST", cands[i] + "/chat/completions", AppConfig.AiKey, payload, false, 16000, out code, out body); }
                    catch (Exception ex) { code = 0; body = ex.Message; }
                }
                if (code != 404 && code != 405) break;
            }
            return code;
        }

        private static void AiHttpOnce(string method, string url, string key, string payload, bool direct, out int code, out string body)
        {
            AiHttpOnce(method, url, key, payload, direct, 6500, out code, out body);
        }

        private static void AiHttpOnce(string method, string url, string key, string payload, bool direct, int timeoutMs, out int code, out string body)
        {
            AiTls();
            System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = "XuYuan-SnapScan/" + AppVersion();
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.KeepAlive = false;
            if (direct) req.Proxy = null;   // 兜底直连：跳过本机代理
            if (!string.IsNullOrEmpty(key)) req.Headers["Authorization"] = "Bearer " + key;
            if (payload != null)
            {
                byte[] data = Encoding.UTF8.GetBytes(payload);
                req.ContentType = "application/json";
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            }
            try
            {
                using (System.Net.HttpWebResponse resp = (System.Net.HttpWebResponse)req.GetResponse())
                {
                    code = (int)resp.StatusCode;
                    body = AiRead(resp);
                }
            }
            catch (System.Net.WebException wex)
            {
                System.Net.HttpWebResponse r = wex.Response as System.Net.HttpWebResponse;
                if (r == null)   // 连不上 / 握手失败：交给调用方决定要不要直连重试
                {
                    code = 0;
                    body = wex.Message;
                    return;
                }
                using (r) { code = (int)r.StatusCode; body = AiRead(r); }
            }
        }

        private static void AiTls()
        {
            // 老框架默认可能只开 SSL3/TLS1.0，这些接口只吃 TLS1.2+
            try
            {
                System.Net.ServicePointManager.SecurityProtocol =
                    System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls11;
            }
            catch (Exception) { }
        }

        private static string AiRead(System.Net.WebResponse resp)
        {
            using (Stream st = resp.GetResponseStream())
            {
                if (st == null) return "";
                using (StreamReader sr = new StreamReader(st, Encoding.UTF8))
                {
                    char[] buf = new char[4096];
                    StringBuilder sb = new StringBuilder();
                    int n;
                    while ((n = sr.Read(buf, 0, buf.Length)) > 0)
                    {
                        sb.Append(buf, 0, n);
                        if (sb.Length > 20000) break;   // 只要状态和错误信息，不需要整包
                    }
                    return sb.ToString();
                }
            }
        }

        private static string[] AiIds(string body)
        {
            List<string> ids = new List<string>();
            if (string.IsNullOrEmpty(body)) return ids.ToArray();
            System.Text.RegularExpressions.Match m =
                System.Text.RegularExpressions.Regex.Match(body, "\"id\"\\s*:\\s*\"([^\"]{1,80})\"");
            while (m.Success)
            {
                string id = m.Groups[1].Value;
                if (!ids.Contains(id)) ids.Add(id);
                m = m.NextMatch();
            }
            return ids.ToArray();
        }

        private static string AiPreview(string[] ids, int n)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < ids.Length && i < n; i++)
            {
                if (i > 0) sb.Append(" / ");
                sb.Append(ids[i]);
            }
            if (ids.Length > n) sb.Append(" 等 " + ids.Length + " 个");
            return sb.ToString();
        }

        private static string AiErrMsg(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            System.Text.RegularExpressions.Match m =
                System.Text.RegularExpressions.Regex.Match(body, "\"message\"\\s*:\\s*\"([^\"]{1,200})\"");
            if (!m.Success) return "";
            string t = m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\n", " ").Replace("\\r", " ");
            if (t.Length > 120) t = t.Substring(0, 120) + "…";
            return t;
        }

        private static string AiEsc(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        internal static int GameTranslate(Bitmap bmp, Rectangle rect, List<OcrSegment> segs)
        {
            NativeShell shell = _shell;
            if (shell == null) return 0;
            return shell.TranslateAndShow(segs, rect, bmp, true);
        }

        internal static string BuildState()
        {
            EngineProbe p = Ocr.Probe();
            bool wx = p != null && p.WeChatUsable;
            string mode = AppConfig.NormalizeEngine(AppConfig.Engine);
            string effective = Ocr.EffectiveEngine();
            string engineName = EngineLabel(effective);
            string cn = EngineNote(effective);
            if (AppConfig.OcrFusion)
            {
                engineName = "融合识字";
                cn = "PP 框 + 微信字";
            }
            bool verified = p != null && p.WeChatVerified;

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"version\":").Append(J.S(AppVersion()));
            sb.Append(",\"engine\":{");
            sb.Append("\"mode\":").Append(J.S(mode));
            sb.Append(",\"effective\":").Append(J.S(effective));
            sb.Append(",\"name\":").Append(J.S(engineName));
            sb.Append(",\"cn\":").Append(J.S(cn));
            sb.Append(",\"wechatUsable\":").Append(J.B(wx));
            sb.Append(",\"verified\":").Append(J.B(verified));
            sb.Append(",\"wechatVersion\":").Append(J.S(p == null ? "" : p.WeChatVersion));
            sb.Append(",\"pluginVersion\":").Append(J.S(p == null ? "" : p.PluginVersion));
            sb.Append(",\"wechatBundled\":").Append(J.B(p != null && p.PluginVersion == "bundle"));
            sb.Append(",\"dir\":").Append(J.S(p == null ? "" : p.WeChatInstallDir));
            sb.Append(",\"problem\":").Append(J.S(p == null ? "" : p.Problem));
            sb.Append(",\"modelRoot\":").Append(J.S(AppConfig.ModelRoot()));
            sb.Append(",\"list\":[");
            bool firstOpt = true;
            string[] keys = new string[] {
                AppConfig.EngineWeChat, AppConfig.EnginePpTiny, AppConfig.EngineSystem };
            for (int i = 0; i < keys.Length; i++)
            {
                string k = keys[i];
                bool ready = EngineReady(k);
                if (!firstOpt) sb.Append(',');
                firstOpt = false;
                sb.Append("{\"key\":").Append(J.S(k));
                sb.Append(",\"name\":").Append(J.S(EngineLabel(k)));
                sb.Append(",\"note\":").Append(J.S(EngineNote(k)));
                sb.Append(",\"ready\":").Append(J.B(ready));
                sb.Append("}");
            }
            sb.Append("]}");
            sb.Append(",\"config\":{");
            sb.Append("\"engine\":").Append(J.S(mode));
            sb.Append(",\"freehand\":").Append(J.B(AppConfig.Freehand));
            sb.Append(",\"restorewin\":").Append(J.B(AppConfig.RestoreWin));
            sb.Append(",\"restoreqr\":").Append(J.B(AppConfig.RestoreAfterQr));
            sb.Append(",\"autocopy\":").Append(J.B(AppConfig.AutoCopy));
            sb.Append(",\"wxdir\":").Append(J.S(AppConfig.WeChatDir));
            sb.Append(",\"opacity\":").Append(AppConfig.BackOpacity);
            sb.Append(",\"frost\":").Append(AppConfig.Frost);
            sb.Append(",\"toast\":").Append(J.B(AppConfig.ShowToast));
            sb.Append(",\"mtbar\":").Append(J.B(AppConfig.MtBar));
            sb.Append(",\"mtperf\":").Append(J.S(AppConfig.MtPerf));
            sb.Append(",\"mtgpu\":").Append(J.S(AppConfig.MtGpu));
            sb.Append(",\"ocrgpu\":").Append(J.B(AppConfig.OcrGpu));
            sb.Append(",\"ocrfusion\":").Append(J.B(AppConfig.OcrFusion));
            sb.Append(",\"shotcache\":").Append(J.B(AppConfig.ShotCacheEnabled));
            sb.Append(",\"cacheinfo\":").Append(J.S(ShotCache.Info()));
            sb.Append(",\"mtidle\":").Append(AppConfig.MtIdle);
            sb.Append(",\"mtalpha\":").Append(AppConfig.MtMaskAlpha);
            sb.Append(",\"mtmode\":").Append(J.S(AppConfig.NormalizeMtMode(AppConfig.MtMode)));
            sb.Append(",\"mttextstyle\":").Append(J.S(AppConfig.MtTextStyle));
            sb.Append(",\"mttextpos\":").Append(AppConfig.MtTextPos);
            sb.Append(",\"mttextautosize\":").Append(J.B(AppConfig.MtTextAutoSize));
            sb.Append(",\"mttextsize\":").Append(AppConfig.MtTextSize);
            sb.Append(",\"mtprofile\":").Append(J.S(AppConfig.NormalizeMtProfile(AppConfig.MtProfile)));
            sb.Append(",\"mtcores\":").Append(Environment.ProcessorCount);
            sb.Append(",\"ai\":{");
            sb.Append("\"provider\":").Append(J.S(AppConfig.AiProvider));
            sb.Append(",\"model\":").Append(J.S(AppConfig.AiModel));
            sb.Append(",\"base\":").Append(J.S(AppConfig.AiBase));
            sb.Append(",\"hasKey\":").Append(J.B(!string.IsNullOrEmpty(AppConfig.AiKey)));
            sb.Append(",\"keyTail\":").Append(J.S(KeyTail(AppConfig.AiKey)));
            sb.Append(",\"ocr\":").Append(J.B(AppConfig.AiOcr));
            sb.Append(",\"check\":").Append(J.B(AppConfig.AiCheck));
            sb.Append(",\"merge\":").Append(J.B(AppConfig.AiMerge));
            sb.Append(",\"mt\":").Append(J.B(AppConfig.AiMt));
            string aiSt, aiMsg, aiAt;
            AiStatusPick(out aiSt, out aiMsg, out aiAt);
            sb.Append(",\"status\":").Append(J.S(aiSt));
            sb.Append(",\"statusMsg\":").Append(J.S(aiMsg));
            sb.Append(",\"checkedAt\":").Append(J.S(aiAt));
            sb.Append("}");
            sb.Append("}");
            sb.Append(",\"game\":{");
            List<Rectangle> gregions = GameRuntime.Regions();
            sb.Append("\"on\":").Append(J.B(AppConfig.GameOn));
            sb.Append(",\"trigger\":").Append(AppConfig.GameTrigger);
            sb.Append(",\"interval\":").Append(AppConfig.GameIntervalMs);
            sb.Append(",\"sens\":").Append(AppConfig.GameSensitivity);
            sb.Append(",\"merge\":").Append(J.B(AppConfig.GameMerge));
            sb.Append(",\"profile\":").Append(J.S(AppConfig.NormalizeMtProfile(AppConfig.GameModel)));
            sb.Append(",\"mask\":").Append(J.S(AppConfig.NormalizeMtMode(AppConfig.MtMode)));
            string gp = AppConfig.NormalizeMtProfile(AppConfig.GameModel);
            bool gready = gp == AppConfig.MtProfileEfficiency ? MtRunner.GameReady : MtRunner.Ready;
            string gmsg = gregions.Count == 0 ? "先添加固定字幕区域"
                : (!gready ? "缺少对应翻译模型" : (AppConfig.GameTrigger == 2 ? "手动快捷键：Ctrl+Alt+F" : "就绪"));
            if (!string.IsNullOrEmpty(GameRuntime.LastError)) gmsg = GameRuntime.LastError;
            sb.Append(",\"ready\":").Append(J.B(gready));
            sb.Append(",\"msg\":").Append(J.S(gmsg));
            sb.Append(",\"regions\":[");
            for (int gi = 0; gi < gregions.Count; gi++)
            {
                if (gi > 0) sb.Append(',');
                sb.Append("{\"x\":").Append(gregions[gi].X)
                  .Append(",\"y\":").Append(gregions[gi].Y)
                  .Append(",\"w\":").Append(gregions[gi].Width)
                  .Append(",\"h\":").Append(gregions[gi].Height).Append('}');
            }
            sb.Append("]}");
            sb.Append(",\"hotkeys\":{\"qr\":").Append(J.S(HotkeyQrText()));
            sb.Append(",\"ocr\":").Append(J.S(HotkeyOcrText()));
            sb.Append(",\"mt\":").Append(J.S(AppConfig.HotkeyMt));
            sb.Append(",\"hide\":").Append(J.S(AppConfig.HotkeyHide)).Append("}");
            sb.Append(",\"runtime\":{\"webview\":").Append(J.B(true));
            sb.Append(",\"win11\":").Append(J.B(IsWin11())).Append("}");
            sb.Append(",\"mt\":{");
            sb.Append("\"on\":").Append(J.B(AppConfig.Translate));
            sb.Append(",\"root\":").Append(J.S(AppConfig.MtRoot()));
            List<MtModelInfo> models = MtRunner.Models();
            // 按档位判断：不再要求三件套齐全
            List<string> miss = MtRunner.MissingKit();
            string multiName = "", multiFile = "";
            long multiSize = 0;
            string gameFile = "";
            long gameSize = 0;
            for (int i = 0; i < models.Count; i++)
            {
                MtModelInfo m = models[i];
                if (m.Gguf == null || m.Gguf.Length == 0) continue;
                try { if (!File.Exists(m.Gguf)) continue; } catch (Exception) { continue; }
                if (m.Game)
                {
                    gameFile = Path.GetFileName(m.Gguf);
                    gameSize = new FileInfo(m.Gguf).Length;
                    continue;
                }
                if (!m.Multi || multiName.Length > 0) continue;
                multiSize = new FileInfo(m.Gguf).Length;
                multiName = m.Name; multiFile = Path.GetFileName(m.Gguf);
            }
            bool gameReady = MtRunner.GameReady;
            bool profReady;
            string profNow = AppConfig.NormalizeMtProfile(AppConfig.MtProfile);
            if (profNow == AppConfig.MtProfileEfficiency) profReady = gameReady;
            else profReady = multiName.Length > 0;
            if (AppConfig.AiMt) profReady = true;   // AI 接管不依赖本地模型
            string active = "", shortTag = "";
            if (profReady)
            {
                if (profNow == AppConfig.MtProfileEfficiency) { active = "0.5B 短句模型"; shortTag = "0.5B 效率模型"; }
                else { active = "HyMT1.5全语种模型"; shortTag = "HyMT1.5全语种模型"; }
            }
            else shortTag = "缺模型";
            sb.Append(",\"active\":").Append(J.S(active));
            sb.Append(",\"short\":").Append(J.S(shortTag));
            sb.Append(",\"ready\":").Append(J.B(profReady));
            sb.Append(",\"missing\":[");
            for (int i = 0; i < miss.Count; i++) { if (i > 0) sb.Append(','); sb.Append(J.S(miss[i])); }
            sb.Append("],\"kit\":[");
            sb.Append("{\"key\":\"gguf\",\"title\":\"HyMT1.5全语种模型\",\"sub\":")
              .Append(J.S(multiFile.Length > 0 ? (multiFile + " · " + (multiSize / (1024L * 1024L)) + " MB") : "未装 · 所有语种 → 中文"))
              .Append(",\"ready\":").Append(J.B(multiName.Length > 0)).Append("}");
            sb.Append(",{\"key\":\"game\",\"title\":\"效率小模型 Qwen2.5-0.5B\",\"sub\":")
              .Append(J.S(gameFile.Length > 0 ? (gameFile + " · " + (gameSize / (1024L * 1024L)) + " MB") : "未装 · 效率档短句"))
              .Append(",\"ready\":").Append(J.B(gameReady)).Append("}");
            sb.Append("]}");
            sb.Append("}");
            return sb.ToString();
        }

        private static bool IsWin11()
        {
            try { if (JadeNative.is_windows_11() == 1) return true; } catch (Exception) { }
            // JadeView 在 Win11 24H2/25H2 上可能误判，按 build 兜底
            return Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000;
        }

        private static string AppVersion()        {
            try
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return "v" + v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision;
            }
            catch (Exception) { return "v1.1.9.0"; }
        }

        private static string HotkeyQrText() { return AppConfig.HotkeyQr; }
        private static string HotkeyOcrText() { return AppConfig.HotkeyOcr; }

        private static IntPtr OnState(uint win, IntPtr msg)
        {
            return Reply(BuildState());
        }

        private static IntPtr OnRecheck(uint win, IntPtr msg)
        {
            Ocr.ProbeForce();
            return Reply(BuildState());
        }

        // AI 大模型：启动时和用户点「重新校验」都走这里。真校验在后台线程，这里立刻返回当前状态
        private static IntPtr OnAiVerify(uint win, IntPtr msg)
        {
            AiStart();
            return Reply(BuildState());
        }

        private static IntPtr OnConfig(uint win, IntPtr msg)
        {
            string body = ReadUtf8(msg);
            try
            {
                Dictionary<string, string> kv = MiniJson.Flatten(body);
                bool aiDirty = false;   // 地址 / 模型名 / Key 一变就要重测一遍
                foreach (KeyValuePair<string, string> pair in kv)
                {
                    string k = pair.Key.ToLowerInvariant();
                    string v = pair.Value;
                    if (k == "engine") AppConfig.Engine = AppConfig.NormalizeEngine(v);
                    else if (k == "freehand") AppConfig.Freehand = v == "true" || v == "1";
                    else if (k == "restorewin") AppConfig.RestoreWin = v == "true" || v == "1";
                    else if (k == "restoreqr") AppConfig.RestoreAfterQr = v == "true" || v == "1";
                    else if (k == "autocopy") AppConfig.AutoCopy = v == "true" || v == "1";
                    else if (k == "wxdir") AppConfig.WeChatDir = v;
                    else if (k == "translate") AppConfig.Translate = v == "true" || v == "1";
                    else if (k == "mtbar")
                    {
                        AppConfig.MtBar = v == "true" || v == "1";
                        if (!AppConfig.MtBar) TransOverlay.HideBar();   // 关掉开关时把已经画出来的控制条一起收掉
                    }
                    else if (k == "mtperf")
                    {
                        string old = AppConfig.MtPerf;
                        AppConfig.MtPerf = AppConfig.NormalizeMtPerf(v);
                        if (old != AppConfig.MtPerf) { try { LlamaMt.Shutdown(); } catch (Exception) { } }   // 换档位要重启服务才会换线程数
                    }
                    else if (k == "mtgpu")
                    {
                        string old = AppConfig.MtGpu;
                        AppConfig.MtGpu = AppConfig.NormalizeMtGpu(v);
                        if (old != AppConfig.MtGpu) { try { LlamaMt.Shutdown(); } catch (Exception) { } }
                    }
                    else if (k == "ocrgpu")
                    {
                        AppConfig.OcrGpu = v == "true" || v == "1";
                    }
                    else if (k == "ocrfusion")
                    {
                        AppConfig.OcrFusion = v == "true" || v == "1";
                    }
                    else if (k == "shotcache")
                    {
                        AppConfig.ShotCacheEnabled = v == "true" || v == "1";
                        if (!AppConfig.ShotCacheEnabled) ShotCache.Clear();
                    }
                    else if (k == "mtidle")
                    {
                        int iv;
                        if (int.TryParse(v, out iv)) AppConfig.MtIdle = AppConfig.NormalizeMtIdle(iv);
                    }
                    else if (k == "mtalpha")
                    {
                        int av;
                        if (int.TryParse(v, out av))
                        {
                            AppConfig.MtMaskAlpha = AppConfig.NormalizeMtMaskAlpha(av);
                            TransOverlay.Repaint();   // 正在显示的话立刻按新透明度重画
                        }
                    }
                    else if (k == "mtmode")
                    {
                        AppConfig.MtMode = AppConfig.NormalizeMtMode(v);
                        try { TransOverlay.Repaint(); } catch (Exception) { }
                    }
                    else if (k == "mttextstyle")
                    {
                        AppConfig.MtTextStyle = v == "black" ? "black" : "white";
                        try { TransOverlay.Repaint(); } catch (Exception) { }
                    }
                    else if (k == "mttextpos")
                    {
                        int gv; if (int.TryParse(v, out gv)) AppConfig.MtTextPos = Math.Max(0, Math.Min(5, gv));
                        try { TransOverlay.Repaint(); } catch (Exception) { }
                    }
                    else if (k == "mttextautosize")
                    {
                        AppConfig.MtTextAutoSize = v == "true" || v == "1";
                        try { TransOverlay.Repaint(); } catch (Exception) { }
                    }
                    else if (k == "mttextsize")
                    {
                        int gv; if (int.TryParse(v, out gv)) AppConfig.MtTextSize = Math.Max(8, Math.Min(72, gv));
                        try { TransOverlay.Repaint(); } catch (Exception) { }
                    }
                    else if (k == "mtprofile")
                    {
                        AppConfig.MtProfile = AppConfig.NormalizeMtProfile(v);
                        MtRunner.Invalidate();   // 换模式要重挑模型
                        // 标准和效率用不同 GGUF，切档时立刻释放旧模型
                        try { LlamaMt.Shutdown(); } catch (Exception) { }
                    }
                    else if (k == "mtlang") { /* 已取消目标语言设置：整套只做外语 → 中文 */ }
                    else if (k == "mtpair") { /* 已取消手动选模型：三个模型都是必需品，不做切换 */ }
                    else if (k == "aiprovider") { if (v.Length > 0 && AppConfig.AiProvider != v.Trim()) { AppConfig.AiProvider = v.Trim(); aiDirty = true; } }
                    else if (k == "aimodel") { if (AppConfig.AiModel != v.Trim()) { AppConfig.AiModel = v.Trim(); aiDirty = true; } }
                    else if (k == "aibase") { if (v.Length > 0 && AppConfig.AiBase != v.Trim()) { AppConfig.AiBase = v.Trim(); aiDirty = true; } }
                    else if (k == "aikey") { if (AppConfig.AiKey != v.Trim()) { AppConfig.AiKey = v.Trim(); aiDirty = true; } }
                    else if (k == "aiocr") { AppConfig.AiOcr = v == "true" || v == "1"; }
                    else if (k == "aicheck") { AppConfig.AiCheck = v == "true" || v == "1"; }
                    else if (k == "aimerge") { AppConfig.AiMerge = v == "true" || v == "1"; }
                    else if (k == "aimt") { AppConfig.AiMt = v == "true" || v == "1"; }
                    else if (k == "gameon")
                    {
                        bool on = v == "true" || v == "1";
                        if (on)
                        {
                            string gerr;
                            if (!GameRuntime.Start(out gerr)) NativeShell.Log("[game] start fail " + gerr);
                        }
                        else GameRuntime.Stop();
                    }
                    else if (k == "gametrigger")
                    {
                        int gv;
                        if (int.TryParse(v, out gv)) AppConfig.GameTrigger = Math.Max(0, Math.Min(2, gv));
                        GameRuntime.Reconfigure();
                    }
                    else if (k == "gameinterval")
                    {
                        int gv;
                        if (int.TryParse(v, out gv)) AppConfig.GameIntervalMs = Math.Max(250, Math.Min(5000, gv));
                        GameRuntime.Reconfigure();
                    }
                    else if (k == "gamesens")
                    {
                        int gv;
                        if (int.TryParse(v, out gv)) AppConfig.GameSensitivity = Math.Max(0, Math.Min(4, gv));
                        GameRuntime.Reconfigure();
                    }
                    else if (k == "gamemodel")
                    {
                        AppConfig.GameModel = AppConfig.NormalizeMtProfile(v);
                        if (AppConfig.GameOn)
                        {
                            AppConfig.MtProfile = AppConfig.GameModel;
                            MtRunner.Invalidate();
                            try { LlamaMt.Shutdown(); } catch (Exception) { }
                            GameRuntime.Reconfigure();
                        }
                    }
                    else if (k == "gamemask")
                    {
                        AppConfig.MtMode = AppConfig.NormalizeMtMode(v);
                        try { TransOverlay.Repaint(); } catch (Exception) { }
                    }
                    else if (k == "gamemerge")
                    {
                        AppConfig.GameMerge = v == "true" || v == "1";
                    }
                    else if (k == "opacity")
                    {
                        int ov;
                        if (int.TryParse(v, out ov) && ov >= 0 && ov <= 100) AppConfig.BackOpacity = ov;
                    }
                    else if (k == "frost")
                    {
                        int fv;
                        if (int.TryParse(v, out fv) && fv >= 0 && fv <= 20) AppConfig.Frost = fv;
                    }
                    else if (k == "toast")
                    {
                        AppConfig.ShowToast = v == "true" || v == "1";
                        if (!AppConfig.ShowToast && ToastForm.Current != null)
                        {
                            try { ToastForm.Current.HideToast(); } catch (Exception) { }
                        }
                    }
                }
                AppConfig.Save();
                if (aiDirty) AiStart();   // 保存即校验：返回的状态是「正在校验」，前端轮询结果
                if (!AppConfig.Translate) TransOverlay.HideOverlay();
            }
            catch (Exception) { }
            return Reply(BuildState());
        }

        // 关译文：主界面按钮 / 再次触发识别时都走这里
        private static IntPtr OnHideTrans(uint win, IntPtr msg)
        {
            TransOverlay.HideOverlay();
            return Reply("{\"ok\":true}");
        }

        private static IntPtr OnPickWx(uint win, IntPtr msg)        {
            // 微信 OCR 只认随包的 OCR Model\WeChat，不再允许指定目录
            Ocr.ProbeForce();
            return Reply(BuildState());
        }

        // ---------- 导入模型包 ----------
        // 用户在设置里选一个下载好的 .zip，直接解开进软件目录里的 OCR Model。
        // 整包（带 exe 的那种）也能导入，只挑里面的模型，其它文件原样丢掉。
        private static string PickZip()
        {
            string chosen = null;
            MethodInvoker act = delegate()
            {
                try
                {
                    using (OpenFileDialog d = new OpenFileDialog())
                    {
                        d.Title = "选择下载好的模型包（.zip）";
                        d.Filter = "模型包 (*.zip)|*.zip|所有文件 (*.*)|*.*";
                        d.CheckFileExists = true;
                        d.Multiselect = false;
                        if (d.ShowDialog() == DialogResult.OK) chosen = d.FileName;
                    }
                }
                catch (Exception) { }
            };
            try { _shell.Invoke(act); }
            catch (Exception) { try { act(); } catch (Exception) { } }
            return chosen;
        }

        // 压缩包里可能直接就是模型目录，也可能外面套了一层（甚至一层 OCR Model），统一找出来
        private static string FindModelSource(string root)
        {
            string[] found = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
            foreach (string d in found)
            {
                if (string.Equals(Path.GetFileName(d), AppConfig.ModelFolderName, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            foreach (string d in found)
            {
                string n = Path.GetFileName(d);
                if (string.Equals(n, "WeChat", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n, "PP-OCR", StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(d);
            }
            return root;
        }

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(from.Length).TrimStart('\\');
                string dst = Path.Combine(to, rel);
                string dir = Path.GetDirectoryName(dst);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.Copy(f, dst, true);
            }
        }

        // ---------- 翻译模型导入 ----------
        // 一个翻译模型目录 = 一堆 onnx + tokenizer.json。HF 下来的包常把权重放在 onnx\ 子目录里，
        // 也可能是 "opus-mt-en-zh" 这种不叫语言对的名字，这里统一认一遍再摆正。
        private static bool LooksLikeMtModel(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            bool tok = File.Exists(Path.Combine(dir, "tokenizer.json"));
            if (!tok) return false;
            return mt_hasOnnx(dir) || mt_hasOnnx(Path.Combine(dir, "onnx"));
        }

        private static bool mt_hasOnnx(string dir)
        {
            if (!Directory.Exists(dir)) return false;
            foreach (string f in Directory.GetFiles(dir, "*.onnx"))
            {
                string n = Path.GetFileName(f).ToLowerInvariant();
                if (n.StartsWith("encoder") || n.StartsWith("decoder")) return true;
            }
            return false;
        }

        private static bool IsLangCode(string s)
        {
            if (s == null || s.Length < 2 || s.Length > 3) return false;
            for (int i = 0; i < s.Length; i++) if (s[i] < 'a' || s[i] > 'z') return false;
            return true;
        }

        // 从 "en-zh" / "opus-mt-en-zh" / "opus_mt_en_zh" 里猜语言对
        private static bool GuessPair(string name, out string src, out string tgt)
        {
            src = tgt = null;
            if (string.IsNullOrEmpty(name)) return false;
            string[] parts = name.ToLowerInvariant().Split(new char[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = parts.Length - 2; i >= 0; i--)
            {
                if (IsLangCode(parts[i]) && IsLangCode(parts[i + 1]))
                {
                    src = parts[i]; tgt = parts[i + 1];
                    return true;
                }
            }
            return false;
        }

        private static string SidecarValue(string dir, string key)
        {
            try
            {
                string f = Path.Combine(dir, "model.json");
                if (!File.Exists(f)) return "";
                string s = File.ReadAllText(f);
                int k = s.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
                if (k < 0) return "";
                int c = s.IndexOf(':', k);
                if (c < 0) return "";
                int q1 = s.IndexOf('"', c + 1);
                if (q1 < 0) return "";
                int q2 = s.IndexOf('"', q1 + 1);
                if (q2 < 0) return "";
                return s.Substring(q1 + 1, q2 - q1 - 1).Trim().ToLowerInvariant();
            }
            catch (Exception) { return ""; }
        }

        private static string MtDirName(string from, string fallback)
        {
            string dirName = Path.GetFileName(from);
            string src, tgt;
            if (GuessPair(dirName, out src, out tgt) && dirName.Length <= 24) return dirName.ToLowerInvariant();
            src = SidecarValue(from, "src");
            tgt = SidecarValue(from, "tgt");
            if (IsLangCode(src) && IsLangCode(tgt)) return src + "-" + tgt;
            if (GuessPair(fallback, out src, out tgt)) return src + "-" + tgt;
            if (!string.IsNullOrEmpty(dirName)) return dirName;
            return "model";
        }

        private static void InstallMtDir(string from, string fallbackName, List<string> got)
        {
            string custom = AppConfig.MtDir != null ? AppConfig.MtDir.Trim() : string.Empty;
            if (custom.Length == 0)
            {
                try
                {
                    string beside = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), AppConfig.MtFolderName);
                    if (!Directory.Exists(beside)) Directory.CreateDirectory(beside);
                }
                catch (Exception) { }
            }
            string root = AppConfig.MtRoot();
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);

            string name = MtDirName(from, fallbackName);
            string dest = Path.Combine(root, name);
            CopyTree(from, dest);

            // onnx\ 里的权重平移到模型目录根，MtModel 只认根上的文件
            string sub = Path.Combine(dest, "onnx");
            if (Directory.Exists(sub))
            {
                foreach (string f in Directory.GetFiles(sub))
                    File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
            }
            got.Add("翻译模型 " + name);
            MtRunner.Invalidate();
        }

        internal static string ImportForTest(string zipPath)
        {
            return ImportModelZip(zipPath);
        }

        private static string ImportModelZip(string zipPath)
        {
            // 模型根还没建过时，就在 exe 旁边建（符合“导入到软件目录”的直觉）
            try
            {
                string beside = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), AppConfig.ModelFolderName);
                if (!Directory.Exists(beside) && !Directory.Exists(AppConfig.ModelRoot())) Directory.CreateDirectory(beside);
            }
            catch (Exception) { }
            string modelRoot = AppConfig.ModelRoot();

            string tmp = Path.Combine(Path.GetTempPath(), "snapscan-import-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmp);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, tmp);
                string src = FindModelSource(tmp);

                List<string> got = new List<string>();
                string wx = Path.Combine(src, "WeChat");
                if (Directory.Exists(wx))
                {
                    CopyTree(wx, Path.Combine(modelRoot, "WeChat"));
                    got.Add("微信 OCR");
                }
                string pp = Path.Combine(src, "PP-OCR");
                if (Directory.Exists(pp))
                {
                    string[] tiers = new string[] { "tiny", "medium" };
                    string[] labels = new string[] { "PP-OCR 极速", "PP-OCR 高精" };
                    for (int i = 0; i < tiers.Length; i++)
                    {
                        string t = Path.Combine(pp, tiers[i]);
                        if (!Directory.Exists(t)) continue;
                        CopyTree(t, Path.Combine(modelRoot, "PP-OCR", tiers[i]));
                        got.Add(labels[i]);
                    }
                }

                // 只支持 GGUF 单文件翻译模型；en-zh / ja-zh 目录包已不再支持
                List<string> ggufs = new List<string>();
                try
                {
                    foreach (string f in Directory.GetFiles(tmp, "*.gguf", SearchOption.AllDirectories))
                    {
                        long len = 0;
                        try { len = new FileInfo(f).Length; } catch (Exception) { }
                        if (len >= 50L * 1024L * 1024L) ggufs.Add(f);
                    }
                }
                catch (Exception) { }
                if (ggufs.Count > 0)
                {
                    string mtRoot = AppConfig.MtRoot();
                    if (!Directory.Exists(mtRoot)) Directory.CreateDirectory(mtRoot);
                    bool sawSmall = false, sawBig = false;
                    for (int i = 0; i < ggufs.Count; i++)
                    {
                        string fn = Path.GetFileName(ggufs[i]);
                        try
                        {
                            File.Copy(ggufs[i], Path.Combine(mtRoot, fn), true);
                            got.Add("翻译模型 " + Path.GetFileNameWithoutExtension(fn));
                            if (MtRouter.IsGameModelPath(fn)) sawSmall = true;
                            else sawBig = true;
                        }
                        catch (Exception) { }
                    }
                    // 导入哪个档的 GGUF 就自动切到对应档，避免“装了却还是缺模型”
                    if (sawSmall) AppConfig.MtProfile = AppConfig.MtProfileEfficiency;
                    else if (sawBig) AppConfig.MtProfile = AppConfig.MtProfileStandard;
                    try { AppConfig.Save(); } catch (Exception) { }
                    MtRunner.Invalidate();
                }

                // 包里要是带了 llama.cpp 运行时（llama-server.exe），一起放到 exe 旁边的 llama 目录
                string srv = null;
                try
                {
                    string[] hs = Directory.GetFiles(tmp, "llama-server.exe", SearchOption.AllDirectories);
                    if (hs.Length > 0) srv = hs[0];
                }
                catch (Exception) { }
                if (srv != null)
                {
                    try
                    {
                        string llamaDir = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "llama");
                        if (!Directory.Exists(llamaDir)) Directory.CreateDirectory(llamaDir);
                        string from = Path.GetDirectoryName(srv);
                        foreach (string f in Directory.GetFiles(from))
                            File.Copy(f, Path.Combine(llamaDir, Path.GetFileName(f)), true);
                        got.Add("llama.cpp 运行时");
                    }
                    catch (Exception) { }
                }

                if (got.Count == 0)
                    return "这个压缩包里没找到模型（要 OCR Model\\WeChat、PP-OCR\\tiny，或一个 .gguf 翻译模型）；en-zh / ja-zh 目录包已不再支持";

                // PP-OCR 的模型包还会带一份推理库（onnxruntime 等），一起放到 exe 旁边
                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                bool runtimeOk = false;
                try
                {
                    foreach (string d in Directory.GetDirectories(tmp, "*", SearchOption.AllDirectories))
                    {
                        if (!string.Equals(Path.GetFileName(d), "Runtime", StringComparison.OrdinalIgnoreCase)) continue;
                        // 只认真正的推理库目录：模型 zip 里的 Runtime 一定有 onnxruntime.dll；
                        // 微信模型自带的 runtime 目录（装的是 Weixin.exe）不能往 exe 旁边倒
                        if (!File.Exists(Path.Combine(d, "onnxruntime.dll"))) continue;
                        foreach (string f in Directory.GetFiles(d))
                        {
                            string name = Path.GetFileName(f);
                            if (name.Equals("QRSnap.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                            if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                            if (name.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) continue;
                            File.Copy(f, Path.Combine(exeDir, name), true);
                            runtimeOk = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Ocr.ProbeForce();
                    return "模型已导入 " + string.Join("、", got.ToArray()) + "，但推理库没放进去（" + ex.Message + "）";
                }

                Ocr.ProbeForce();
                return "已导入 " + string.Join("、", got.ToArray()) + (runtimeOk ? "（含推理库）" : "") + "，设置里现在能直接选";
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (Exception) { }
            }
        }

        private static IntPtr OnImportModel(uint win, IntPtr msg)
        {            string zip = PickZip();
            if (string.IsNullOrEmpty(zip))
                return Reply("{\"ok\":false,\"msg\":" + J.S("已取消") + "}");

            string result;
            try { result = ImportModelZip(zip); }
            catch (Exception ex) { result = "导入失败：" + ex.Message; }

            return Reply("{\"ok\":true,\"msg\":" + J.S(result) + ",\"state\":" + BuildState() + "}");
        }

        private static IntPtr OnCapture(uint win, IntPtr msg)
        {
            string body = ReadUtf8(msg);
            string mode = MiniJson.GetString(body, "mode");
            if (mode != "ocr" && mode != "mt") mode = "qr";

            bool restore = RestoreToFront(mode);
            HideWindow(mode != "mt");
            Thread.Sleep(120);

            bool cancelled;
            string json = _shell.CaptureSync(mode, false, out cancelled);

            ShowWindow(restore);
            if (json == null) json = "{\"ok\":false,\"cancelled\":true,\"mode\":" + J.S(mode) + "}";
            return Reply(json);
        }

        private static IntPtr OnCacheClear(uint win, IntPtr msg)
        {
            try { ShotCache.Clear(); } catch (Exception) { }
            try { MtRunner.Router.ClearCache(); } catch (Exception) { }
            AiBoost.ClearCache();
            NativeShell.Log("[cache] cleared -> " + ShotCache.Info());
            return Reply("{\"ok\":true,\"info\":" + J.S(ShotCache.Info()) + "}");
        }

        private static IntPtr OnGamePick(uint win, IntPtr msg)
        {
            try
            {
                GameRuntime.Pause(true);
                HideWindow(true);
                Thread.Sleep(150);
                CaptureSession s = new CaptureSession();
                s.Freehand = false;
                s.ExistingRegions = GameRuntime.Regions();
                s.Run();
                bool ok = s.Confirmed && s.Selection.Width >= 16 && s.Selection.Height >= 12;
                bool added = false;
                if (ok) added = GameRuntime.AddRegion(s.Selection);
                try { if (s.Crop != null) s.Crop.Dispose(); } catch (Exception) { }
                return Reply("{\"ok\":" + J.B(ok) + ",\"added\":" + J.B(added)
                    + ",\"cancelled\":" + J.B(!ok) + ",\"state\":" + BuildState() + "}");
            }
            catch (Exception ex)
            {
                NativeShell.Log("[game] pick fail " + ex.Message);
                return Reply("{\"ok\":false,\"msg\":" + J.S(ex.Message) + "}");
            }
            finally
            {
                try { ShowWindow(true); } catch (Exception) { }
                GameRuntime.Pause(false);
            }
        }

        private static IntPtr OnGameDel(uint win, IntPtr msg)
        {
            int index = 0;
            try { int.TryParse(MiniJson.GetString(ReadUtf8(msg), "index"), out index); } catch (Exception) { }
            GameRuntime.RemoveRegion(index);
            if (GameRuntime.Regions().Count == 0 && AppConfig.GameOn) GameRuntime.Stop();
            return Reply("{\"ok\":true,\"state\":" + BuildState() + "}");
        }

        private static IntPtr OnGameClear(uint win, IntPtr msg)
        {
            GameRuntime.ClearRegions();
            if (AppConfig.GameOn) GameRuntime.Stop();
            return Reply("{\"ok\":true,\"state\":" + BuildState() + "}");
        }

        private static IntPtr OnCopy(uint win, IntPtr msg)
        {
            string text = MiniJson.GetString(ReadUtf8(msg), "text");
            try { if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text); } catch (Exception) { }
            return Reply("{\"ok\":true}");
        }

        private static IntPtr OnWin(uint win, IntPtr msg)
        {
            string op = MiniJson.GetString(ReadUtf8(msg), "op");
            if (op == "hide") HideWindow(false);
            else if (op == "show") ShowWindow(true);
            return Reply("{\"ok\":true}");
        }

        private static IntPtr OnHotkey(uint win, IntPtr msg)
        {
            string body = ReadUtf8(msg);
            string which = MiniJson.GetString(body, "which");
            string combo = MiniJson.GetString(body, "combo");
            try
            {
                uint m, k;
                if (HotkeyText.TryParse(combo, out m, out k))
                {
                    if (which == "ocr") AppConfig.HotkeyOcr = HotkeyText.Format(m, k);
                    else if (which == "mt") AppConfig.HotkeyMt = HotkeyText.Format(m, k);
                    else if (which == "hide") AppConfig.HotkeyHide = HotkeyText.Format(m, k);
                    else AppConfig.HotkeyQr = HotkeyText.Format(m, k);
                    AppConfig.Save();
                    _shell.Invoke(new MethodInvoker(delegate() { _shell.RegisterAll(); }));
                }
            }
            catch (Exception) { }
            return Reply(BuildState());
        }

        // ---------- 对外 ----------
        internal static void Send(string channel, string json)
        {
            if (WindowId == 0) return;
            try { JadeNative.send_ipc_message(WindowId, U(channel), U(json)); }
            catch (Exception) { }
        }

        private static bool _wasMinimized;

        // 截屏开始：默认压到最底层（不隐藏），需要真隐藏的入口（前端"隐藏窗口"）传 false。
        // 藏之前先记住它原来是"最小化"还是"在桌面上"。
        internal static void HideWindow(bool toBottom)
        {
            try
            {
                IntPtr h = Native.OwnMainWindow();
                _wasMinimized = Native.IsMinimizedHandle(h);
                if (toBottom)
                {
                    // 已经最小化就不用动；否则保证可见后压到最底层。
                    // 这样框选时它不会挡住目标窗口，截完也还在原处，切回来就能用。
                    if (!_wasMinimized)
                    {
                        JadeNative.set_window_visible(WindowId, 1);
                        bool ok = Native.SendToBottom(h);
                        Log("send-bottom ok=" + ok + " wasMin=" + _wasMinimized);
                        return;
                    }
                }
                JadeNative.set_window_visible(WindowId, 0);
                Log("hide: wasMin=" + _wasMinimized + " visibleNow=" + JadeNative.is_window_visible(WindowId));
            }
            catch (Exception) { }
        }

        // 截屏结束回哪一层：
        //   翻译：无论设置如何，一律回最底层（不挡译文，也不消失）
        //   OCR / 扫码：只有设置里勾了「显示本软件窗口」才回最前，否则也回最底层
        private static bool RestoreToFront(string mode)
        {
            if (mode == "mt") return false;
            if (mode == "qr") return AppConfig.RestoreAfterQr;
            return AppConfig.RestoreWin;
        }

        // 截屏结束：按原状态还原。toFront=false 只放回原处（原来最小化就仍最小化、原来被挡住就仍被挡住）；
        // toFront=true（设置里勾了"显示本软件窗口"）才强制提到最前。
        internal static void ShowWindow(bool toFront)
        {
            try
            {
                JadeNative.set_window_visible(WindowId, 1);
                IntPtr h = Native.OwnMainWindow();
                if (toFront)
                {
                    Native.BringFront(h);              // 勾了"显示本软件窗口"：强制提到最前（最小化的也会展开）
                    JadeNative.set_window_focus(WindowId);
                }
                else if (Native.IsMinimizedHandle(h) || _wasMinimized)
                {
                    Native.MinimizeHandle(h);          // 原来是缩着的，还原后照样缩着
                }
                else
                {
                    Native.ShowNoActivate(h);          // 不抢焦点、不抬到最前，原来被挡住就仍被挡住
                    Native.SendToBottom(h);            // 截完仍留在最底层：不挡目标，也随时能切回来
                }
                Log("restore: toFront=" + toFront + " wasMin=" + _wasMinimized + " iconic=" + Native.IsMinimizedHandle(h) + " visible=" + JadeNative.is_window_visible(WindowId));
                _wasMinimized = false;
                Send("state-refresh", BuildState());
            }
            catch (Exception) { }
        }

        internal static void Quit()
        {
            try { Ocr.Shutdown(); } catch (Exception) { }
            try { if (_shell != null) _shell.DisposeShell(); } catch (Exception) { }
            try { JadeNative.jadeview_exit(); } catch (Exception) { }
            Environment.Exit(0);
        }

        // 热键路径的截图（在 WinForms 线程上跑，结果通过 send_ipc_message 推给前端）
        internal static string RunCapture(string mode, bool fromHotkey)
        {
            bool cancelled = false;
            string json = null;
            try
            {
                json = _shell.CaptureSync(mode, fromHotkey, out cancelled);
            }
            finally
            {
                // 关键：无论成功、失败、Esc 取消还是中途异常，都必须把主窗口放回来。
                // 原来这里先 if (json == null) return null; 再 ShowWindow，取消时直接返回，
                // 窗口就永远留在隐藏状态——用户只能去杀进程。
                ShowWindow(RestoreToFront(mode));
            }

            if (json == null) return null;
            Send("capture-done", json);
            return json;
        }

        // 同一时间只允许一个截屏会话
        private static int _capturing;

        internal static bool TryEnterCapture()
        {
            return System.Threading.Interlocked.CompareExchange(ref _capturing, 1, 0) == 0;
        }

        internal static void LeaveCapture()
        {
            System.Threading.Interlocked.Exchange(ref _capturing, 0);
        }
    }

    // 极简 JSON 读取：只够解析 {key:value} 扁平对象和取字符串字段
    internal static class MiniJson
    {
        public static Dictionary<string, string> Flatten(string json)
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(json)) return map;
            int i = 0;
            while (i < json.Length)
            {
                int q = json.IndexOf('"', i);
                if (q < 0) break;
                int qe = FindEnd(json, q + 1);
                if (qe < 0) break;
                string key = json.Substring(q + 1, qe - q - 1);
                int colon = json.IndexOf(':', qe);
                if (colon < 0) break;
                int v = colon + 1;
                while (v < json.Length && (json[v] == ' ' || json[v] == '\r' || json[v] == '\n' || json[v] == '\t')) v++;
                if (v >= json.Length) break;
                string val;
                if (json[v] == '"')
                {
                    int ve = FindEnd(json, v + 1);
                    if (ve < 0) break;
                    val = Unescape(json.Substring(v + 1, ve - v - 1));
                    i = ve + 1;
                }
                else
                {
                    int ve = v;
                    while (ve < json.Length && json[ve] != ',' && json[ve] != '}') ve++;
                    val = json.Substring(v, ve - v).Trim();
                    i = ve;
                }
                map[key] = val;
            }
            return map;
        }

        public static string GetString(string json, string key)
        {
            Dictionary<string, string> map = Flatten(json);
            return map.ContainsKey(key) ? map[key] : string.Empty;
        }

        private static int FindEnd(string s, int start)
        {
            for (int i = start; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == '"') return i;
            }
            return -1;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\') { sb.Append(c); continue; }
                i++;
                if (i >= s.Length) break;
                char n = s[i];
                if (n == 'n') sb.Append('\n');
                else if (n == 'r') sb.Append('\r');
                else if (n == 't') sb.Append('\t');
                else if (n == 'u')
                {
                    if (i + 4 < s.Length)
                    {
                        sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16));
                        i += 4;
                    }
                }
                else sb.Append(n);
            }
            return sb.ToString();
        }
    }
}
