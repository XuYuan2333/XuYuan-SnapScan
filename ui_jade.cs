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
        const int ID_QR = 1, ID_OCR = 2;

        private ToastForm _toast;
        private readonly System.Windows.Forms.Timer _watch;
        private bool _seenWindow;
        private uint _modsQr = Native.MOD_CONTROL | Native.MOD_ALT, _vkQr = (uint)Keys.D;
        private uint _modsOcr = Native.MOD_CONTROL | Native.MOD_ALT, _vkOcr = (uint)Keys.X;

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

            uint m, k;
            if (HotkeyText.TryParse(AppConfig.HotkeyQr, out m, out k)) { _modsQr = m; _vkQr = k; }
            if (HotkeyText.TryParse(AppConfig.HotkeyOcr, out m, out k)) { _modsOcr = m; _vkOcr = k; }
            if (m == 0 && k == 0) { }

            TryRegister(ID_QR, _modsQr, _vkQr, true);
            TryRegister(ID_OCR, _modsOcr, _vkOcr, false);
        }

        private void TryRegister(int id, uint mods, uint vk, bool isQr)
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
            if (isQr) { _modsQr = mods; _vkQr = vk; AppConfig.HotkeyQr = HotkeyText.Format(mods, vk); }
            else { _modsOcr = mods; _vkOcr = vk; AppConfig.HotkeyOcr = HotkeyText.Format(mods, vk); }
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
                if (id == ID_QR) HotkeyCapture("qr");
                else if (id == ID_OCR) HotkeyCapture("ocr");
                return;
            }
            base.WndProc(ref m);
        }

        private void HotkeyCapture(string mode)
        {
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

        private CaptureResult CaptureOnUi(string mode)
        {
            Log("capture start " + mode);
            CaptureSession session = new CaptureSession();
            session.Freehand = AppConfig.Freehand;

            // 截屏前先把提示条收掉。原来这里直接 ShowBusy，结果上一轮还没消失的
            // “识别完成 / 正在识别”会被一起拍进截图里，框选时也能看到这块残留。
            if (_toast != null) _toast.HideToast();
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

            if (mode == "qr")
            {
                if (_toast != null) _toast.ShowBusy("正在识码…");
                string info;
                text = QrDecoder.Decode(crop, out info);
                engine = "二维码";
                detail = info;
                ok = !string.IsNullOrEmpty(text);
            }
            else
            {
                if (_toast != null) _toast.ShowBusy("正在识字…  " + (Ocr.EffectiveEngine() == AppConfig.EngineWeChat ? "微信 OCR" : "Windows 内置 OCR"));
                text = Ocr.Recognize(crop, out engine, out ms, out detail);
                ok = !string.IsNullOrEmpty(text);
            }
            crop.Dispose();

            if (ok && AppConfig.AutoCopy && !string.IsNullOrEmpty(text))
            {
                try { Clipboard.SetText(text); }
                catch (Exception) { }
            }

            if (_toast != null) _toast.ShowDone(ok ? (mode == "qr" ? "识码完成" : "识字完成") : "没有识别到内容", ok);

            string json = "{"
                + "\"ok\":" + J.B(ok)
                + ",\"mode\":" + J.S(mode)
                + ",\"text\":" + J.S(text)
                + ",\"engine\":" + J.S(engine)
                + ",\"detail\":" + J.S(detail)
                + ",\"ms\":" + J.N(ms)
                + ",\"copied\":" + J.B(ok && AppConfig.AutoCopy && !string.IsNullOrEmpty(text))
                + "}";

            CaptureResult cr = new CaptureResult();
            cr.Json = json;
            return cr;
        }

        public void DisposeShell()
        {
            try { Native.UnregisterHotKey(Handle, ID_QR); Native.UnregisterHotKey(Handle, ID_OCR); } catch (Exception) { }
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
            Bind("capture", OnCapture);
            Bind("clipcopy", OnCopy);
            Bind("win", OnWin);
            Bind("hotkey", OnHotkey);

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
            if (e == AppConfig.EnginePpSmall) return "PP-OCR 标准";
            if (e == AppConfig.EnginePpMedium) return "PP-OCR 高精";
            return "Windows OCR";
        }

        // 设置里选项后面的括号说明，短到不会撑破界面
        // 注意：微信 OCR 也是本地模型、不联网，所以不再给 PP-OCR 单独标“离线”
        internal static string EngineNote(string e)
        {
            if (e == AppConfig.EngineWeChat) return "推荐";
            if (e == AppConfig.EnginePpTiny) return "轻量 · 快";
            if (e == AppConfig.EnginePpSmall) return "均衡";
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
                AppConfig.EnginePpSmall, AppConfig.EnginePpMedium, AppConfig.EngineSystem };
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
            sb.Append("}");
            sb.Append(",\"hotkeys\":{\"qr\":").Append(J.S(HotkeyQrText()));
            sb.Append(",\"ocr\":").Append(J.S(HotkeyOcrText())).Append("}");
            sb.Append(",\"runtime\":{\"webview\":").Append(J.B(true));
            sb.Append(",\"win11\":").Append(J.B(IsWin11())).Append("}}");
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
            catch (Exception) { return "v1.1.7.0"; }
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
            }
            catch (Exception) { }
            return Reply(BuildState());
        }

        private static IntPtr OnPickWx(uint win, IntPtr msg)
        {
            // 微信 OCR 只认随包的 OCR Model\WeChat，不再允许指定目录
            Ocr.ProbeForce();
            return Reply(BuildState());
        }

        private static IntPtr OnCapture(uint win, IntPtr msg)
        {
            string body = ReadUtf8(msg);
            string mode = body.IndexOf("\"ocr\"", StringComparison.Ordinal) >= 0 ? "ocr" : "qr";

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