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
                // 识别期间 UI 线程在泵消息，这时再按热键会叠出一轮新截屏，直接忽略
                if (_pumping) { Log("WM_HOTKEY ignored while busy id=" + id); return; }
                if (id == ID_QR) HotkeyCapture("qr");
                else if (id == ID_OCR) HotkeyCapture("ocr");
                else if (id == ID_MT) HotkeyCapture("mt");
                else if (id == ID_HIDE)
                {
                    // 全局显隐：Ctrl+Alt+G 关掉全部译文 / 再按一次恢复
                    TransOverlay.Toggle();
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
                    JadeHost.HideWindow();
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

        private void RunPumped(Action work, int minVisibleMs)
        {
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
                bool wantMt = mode == "mt" && AppConfig.Translate && MtRunner.Ready;
                if (_toast != null) _toast.ShowBusy("正在识字…  " + busyEngine);

                List<OcrSegment> segs = wantMt ? new List<OcrSegment>() : null;

                string recText = string.Empty;
                string recEngine = string.Empty;
                string recDetail = string.Empty;
                long recMs = 0;
                RunPumped(delegate()
                {
                    recText = Ocr.Recognize(crop, out recEngine, out recMs, out recDetail, segs);
                }, 320);
                text = recText;
                engine = recEngine;
                detail = recDetail;
                ms = recMs;
                ok = !string.IsNullOrEmpty(text);

                if (wantMt && ok)
                {
                    try
                    {
                        if (_toast != null) _toast.ShowBusy("正在翻译…");
                        int shown = TranslateAndShow(segs, session.Selection);
                        if (shown > 0) mtLines = shown;
                        else detail = detail + "；翻译：没有可对齐的文本行";
                    }
                    catch (Exception exMt)
                    {
                        detail = detail + "；翻译失败：" + exMt.Message;
                        Log("translate fail " + exMt.Message);
                    }
                }
            }
            crop.Dispose();

            if (ok && AppConfig.AutoCopy && !string.IsNullOrEmpty(text))
            {
                try { Clipboard.SetText(text); }
                catch (Exception) { }
            }

            string doneWord = mode == "qr" ? "识码完成" : (mtLines > 0 ? "翻译完成" : "识字完成");
            if (_toast != null) _toast.ShowDone(ok ? doneWord : "没有识别到内容", ok);

            string json = "{"
                + "\"ok\":" + J.B(ok)
                + ",\"mode\":" + J.S(mode)
                + ",\"text\":" + J.S(text)
                + ",\"engine\":" + J.S(engine)
                + ",\"detail\":" + J.S(detail)
                + ",\"ms\":" + J.N(ms)
                + ",\"copied\":" + J.B(ok && AppConfig.AutoCopy && !string.IsNullOrEmpty(text))
                + ",\"mtLine\":" + J.N(mtLines)
                + "}";

            CaptureResult cr = new CaptureResult();
            cr.Json = json;
            return cr;
        }

        // 逐行翻译并原地铺译文；返回画出来的行数
        private int TranslateAndShow(List<OcrSegment> segs, Rectangle screenRect)
        {
            if (segs == null || segs.Count == 0) return 0;
            string tgt = AppConfig.TargetLang;
            StringBuilder all = new StringBuilder();
            for (int i = 0; i < segs.Count; i++) all.Append(segs[i].Text).Append('\n');
            string src = LangDetect.Detect(all.ToString());
            if (string.IsNullOrEmpty(src)) return 0;
            if (src == tgt) throw new Exception("这段文字看起来已经是" + MtRouter.NameOf(tgt) + "，不需要翻译");

            List<TransOverlay.Item> items = new List<TransOverlay.Item>();
            string err = null;
            RunPumped(delegate()
            {
                try
                {
                    MtRouter router = MtRunner.Router;
                    string[] plan = router.PlanFor(src, tgt, AppConfig.MtPair);
                    if (plan.Length == 0)
                    {
                        err = "缺少 " + MtRouter.NameOf(src) + "→" + MtRouter.NameOf(tgt) + " 的模型，装一个再试";
                        return;
                    }
                    for (int i = 0; i < segs.Count; i++)
                    {
                        OcrSegment s = segs[i];
                        if (s.Text == null || s.Text.Trim().Length == 0) continue;
                        string route;
                        string tr = router.Translate(s.Text, src, tgt, AppConfig.MtPair, out route);
                        if (string.IsNullOrEmpty(tr)) continue;
                        TransOverlay.Item it = new TransOverlay.Item();
                        it.Box = s.Box;
                        it.Angle = s.Angle;
                        it.Text = tr;
                        items.Add(it);
                        Log("mt line box=" + s.Box.X + "," + s.Box.Y + "," + s.Box.Width + "," + s.Box.Height
                            + " ang=" + s.Angle + " | " + s.Text + " => " + tr);
                    }
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }
            }, 260);

            if (err != null) throw new Exception(err);
            if (items.Count == 0) return 0;
            TransOverlay.Show(screenRect, items);
            Log("translate shown lines=" + items.Count + " src=" + src + " tgt=" + tgt);
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

        public static List<string> Pairs()
        {
            try { return Router.AvailablePairs(); }
            catch (Exception) { return new List<string>(); }
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
                bool show = mode == "qr" ? AppConfig.RestoreAfterQr : AppConfig.RestoreWin;
                ShowWindow(show);
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
            Bind("pickwx", OnPickWx);
            Bind("importmodel", OnImportModel);
            Bind("downloadmodel", OnDownloadModel);
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

        private static void Log(string msg)
        {
            try { NativeShell.Log(msg); } catch (Exception) { }
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
        internal static string BuildState()
        {
            EngineProbe p = Ocr.Probe();
            bool wx = p != null && p.WeChatUsable;
            string mode = AppConfig.NormalizeEngine(AppConfig.Engine);
            string effective = Ocr.EffectiveEngine();
            string engineName = EngineLabel(effective);
            string cn = EngineNote(effective);
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
                AppConfig.EngineWeChat, AppConfig.EnginePpTiny,
                AppConfig.EnginePpMedium, AppConfig.EngineSystem };
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
            sb.Append(",\"mtbar\":").Append(J.B(AppConfig.MtBar));
            sb.Append(",\"mtperf\":").Append(J.S(AppConfig.MtPerf));
            sb.Append(",\"mtidle\":").Append(AppConfig.MtIdle);
            sb.Append(",\"dl\":{\"running\":").Append(J.B(ModelDownload.Running));
            sb.Append(",\"key\":").Append(J.S(ModelDownload.CurrentKey));
            sb.Append(",\"status\":").Append(J.S(ModelDownload.Status));
            sb.Append(",\"percent\":").Append(ModelDownload.Percent).Append("}");
            sb.Append(",\"mtcores\":").Append(Environment.ProcessorCount);
            sb.Append("}");
            sb.Append(",\"hotkeys\":{\"qr\":").Append(J.S(HotkeyQrText()));
            sb.Append(",\"ocr\":").Append(J.S(HotkeyOcrText()));
            sb.Append(",\"mt\":").Append(J.S(AppConfig.HotkeyMt));
            sb.Append(",\"hide\":").Append(J.S(AppConfig.HotkeyHide)).Append("}");
            sb.Append(",\"runtime\":{\"webview\":").Append(J.B(true));
            sb.Append(",\"win11\":").Append(J.B(IsWin11())).Append("}");
            sb.Append(",\"mt\":{");
            sb.Append("\"on\":").Append(J.B(AppConfig.Translate));
            sb.Append(",\"lang\":").Append(J.S(AppConfig.TargetLang));
            sb.Append(",\"root\":").Append(J.S(AppConfig.MtRoot()));
            sb.Append(",\"selected\":").Append(J.S(AppConfig.MtPair));
            List<MtModelInfo> models = MtRunner.Models();
            bool anyReady = false;
            for (int i = 0; i < models.Count; i++) if (models[i].Ready) { anyReady = true; break; }
            sb.Append(",\"ready\":").Append(J.B(anyReady));
            sb.Append(",\"models\":[");
            for (int i = 0; i < models.Count; i++)
            {
                MtModelInfo m = models[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(J.S(m.Name));
                sb.Append(",\"src\":").Append(J.S(m.Src));
                sb.Append(",\"tgt\":").Append(J.S(m.Tgt));
                sb.Append(",\"pair\":").Append(J.S(m.Pair));
                sb.Append(",\"label\":").Append(J.S(m.Multi ? (m.Label != null && m.Label.Length > 0 ? m.Label : m.Name) : (MtRouter.NameOf(m.Src) + " → " + MtRouter.NameOf(m.Tgt))));
                sb.Append(",\"note\":").Append(J.S(m.Name + "  ·  " + m.Pair));
                sb.Append(",\"ready\":").Append(J.B(m.Ready));
                sb.Append("}");
            }
            sb.Append("]");
            List<string> pairs = MtRunner.Pairs();
            sb.Append(",\"pairs\":[");
            for (int i = 0; i < pairs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(J.S(pairs[i]));
            }
            sb.Append("]}");
            sb.Append("}");
            return sb.ToString();
        }

        private static bool IsWin11()
        {
            try { return JadeNative.is_windows_11() == 1; } catch (Exception) { return false; }
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

        private static IntPtr OnConfig(uint win, IntPtr msg)
        {
            string body = ReadUtf8(msg);
            try
            {
                Dictionary<string, string> kv = MiniJson.Flatten(body);
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
                    else if (k == "mtidle")
                    {
                        int iv;
                        if (int.TryParse(v, out iv)) AppConfig.MtIdle = AppConfig.NormalizeMtIdle(iv);
                    }
                    else if (k == "mtlang") { if (v != null && v.Trim().Length > 0) AppConfig.TargetLang = v.Trim().ToLowerInvariant(); }
                    else if (k == "mtpair")
                    {
                        AppConfig.MtPair = v == null ? string.Empty : v.Trim();
                        MtRunner.Invalidate();   // 换模型要重建路由，别拿旧缓存
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
                }
                AppConfig.Save();
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

                string mt = Path.Combine(src, AppConfig.MtFolderName);
                if (Directory.Exists(mt))
                {
                    string[] pairs = Directory.GetDirectories(mt);
                    if (pairs.Length == 0) pairs = new string[] { mt };
                    for (int i = 0; i < pairs.Length; i++)
                    {
                        string name = Path.GetFileName(pairs[i]);
                        InstallMtDir(pairs[i], name, got);
                    }
                }
                else
                {
                    // 单独一个翻译模型包：整个解压目录、或里面某个子目录就是模型本体
                    string zipName = Path.GetFileNameWithoutExtension(zipPath);
                    List<string> candidates = new List<string>();
                    if (LooksLikeMtModel(src)) candidates.Add(src);
                    foreach (string d in Directory.GetDirectories(tmp, "*", SearchOption.AllDirectories))
                        if (LooksLikeMtModel(d)) candidates.Add(d);
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        string d = candidates[i];
                        if (d.Length == 0) continue;
                        // 已经被上一层的模型目录覆盖了就不再重复搬
                        bool nested = false;
                        for (int j = 0; j < i; j++)
                        {
                            if (d.StartsWith(candidates[j] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { nested = true; break; }
                        }
                        if (nested) continue;
                        InstallMtDir(d, zipName, got);
                    }
                }

                // GGUF 翻译模型：一个文件覆盖多语种方向，包里任意位置的 *.gguf 都装到 MT Model 根上
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
                    for (int i = 0; i < ggufs.Count; i++)
                    {
                        string fn = Path.GetFileName(ggufs[i]);
                        try
                        {
                            File.Copy(ggufs[i], Path.Combine(mtRoot, fn), true);
                            got.Add("翻译模型 " + Path.GetFileNameWithoutExtension(fn));
                        }
                        catch (Exception) { }
                    }
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
                    return "这个压缩包里没找到模型（要 OCR Model\\WeChat、PP-OCR\\tiny/small/medium，或一个翻译模型目录 / 一个 .gguf 翻译模型），换一个模型包试试";

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

        // 下载按钮：从 GitHub Releases 拉对应模型 zip，下完自动导入并清理临时文件
        private static IntPtr OnDownloadModel(uint win, IntPtr msg)
        {
            string key = MiniJson.GetString(ReadUtf8(msg), "key");
            string r = ModelDownload.Start(key);
            return Reply("{\"ok\":true,\"msg\":" + J.S(r) + "}");
        }

        private static IntPtr OnCapture(uint win, IntPtr msg)
        {
            string body = ReadUtf8(msg);
            string mode = MiniJson.GetString(body, "mode");
            if (mode != "ocr" && mode != "mt") mode = "qr";

            bool restore = mode == "qr" ? AppConfig.RestoreAfterQr : AppConfig.RestoreWin;
            HideWindow();
            Thread.Sleep(120);

            bool cancelled;
            string json = _shell.CaptureSync(mode, false, out cancelled);

            ShowWindow(restore);
            if (json == null) json = "{\"ok\":false,\"cancelled\":true,\"mode\":" + J.S(mode) + "}";
            return Reply(json);
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
            if (op == "hide") HideWindow();
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

        // 截屏开始：真的把窗口藏起来（藏之前先记住它原来是"最小化"还是"在桌面上"）
        internal static void HideWindow()
        {
            try
            {
                IntPtr h = Native.OwnMainWindow();
                _wasMinimized = Native.IsMinimizedHandle(h);
                JadeNative.set_window_visible(WindowId, 0);
                Log("hide: wasMin=" + _wasMinimized + " visibleNow=" + JadeNative.is_window_visible(WindowId));
            }
            catch (Exception) { }
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
                ShowWindow(mode == "qr" ? AppConfig.RestoreAfterQr : AppConfig.RestoreWin);
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