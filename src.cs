using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("XuYuan-SnapScan 二维码 / 文字识别工具")]
[assembly: AssemblyProduct("XuYuan-SnapScan")]
[assembly: AssemblyDescription("截图识别二维码与文字的小工具")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

namespace QRSnap
{
    internal static class Native
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        private const int SRCCOPY = 0x00CC0020;
        private const int SW_RESTORE = 9;
        private const int SW_MINIMIZE = 6;
        private const int SW_SHOWNOACTIVATE = 4;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
            IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        // 找自己进程里的 JadeView 主窗口（不靠标题，标题里带中文/中点容易匹配不上）
        public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr h, StringBuilder sb, int n);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        public static IntPtr OwnMainWindow()
        {
            IntPtr found = IntPtr.Zero;
            uint me = GetCurrentProcessId();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != me) return true;
                StringBuilder cls = new StringBuilder(128);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() == "Window Class") { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }


        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        // 强制 DWM 立刻合成当前画面：窗口隐藏后马上抓屏不会抓到自己的残影
        public static void FlushComposition()
        {
            DwmFlush();
        }

        // 抓一块矩形区域（一个显示器一块，坐标用该显示器自己的物理像素）
        public static Bitmap Capture(Rectangle bounds)
        {
            Bitmap bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            IntPtr src = GetDC(IntPtr.Zero);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                IntPtr dst = g.GetHdc();
                try
                {
                    BitBlt(dst, 0, 0, bounds.Width, bounds.Height, src, bounds.X, bounds.Y, SRCCOPY);
                }
                finally
                {
                    g.ReleaseHdc(dst);
                }
            }
            ReleaseDC(IntPtr.Zero, src);
            return bmp;
        }

        public static void BringToFront(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
        }

        // 强制把某个窗口变成前台并拿到键盘焦点。
        // 直接 SetForegroundWindow 会被系统的前台锁拦下（尤其从隐藏状态刚恢复时），
        // 遮罩拿不到焦点，Esc 就送不到它手里；AttachThreadInput 能绕过这个限制。
        public static bool ForceForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            uint pid = 0;
            IntPtr fg = GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out pid);
            uint myThread = GetCurrentThreadId();
            bool attached = false;
            try
            {
                if (fgThread != 0 && fgThread != myThread)
                {
                    attached = AttachThreadInput(fgThread, myThread, true);
                }
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
                SetFocus(hWnd);
                return GetForegroundWindow() == hWnd;
            }
            finally
            {
                if (attached) AttachThreadInput(fgThread, myThread, false);
            }
        }

        public static void ActivateWindow(string title)
        {
            IntPtr h = FindWindow(null, title);
            if (h == IntPtr.Zero) return;
            ShowWindow(h, SW_RESTORE);
            SetForegroundWindow(h);
        }

        // 缩到任务栏（保留任务栏按钮）
        public static bool MinimizeHandle(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            ShowWindow(h, SW_MINIMIZE);
            return true;
        }

        // 不隐藏，压到所有普通窗口的最底层：截屏时不会挡住目标，截完也还在原处，
        // 用户切回来就能看到（隐藏之后窗口既不在任务栏、也点不到，很难找）。
        public static bool SendToBottom(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            try
            {
                SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                return SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch (Exception) { return false; }
        }

        public static bool IsMinimizedHandle(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            return IsIconic(h);
        }

        // 显示但不抢焦点、不抬到最前（还原后该多大就多大、该被谁盖住就盖住）
        public static void ShowNoActivate(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            ShowWindow(h, SW_SHOWNOACTIVATE);
        }

        // 强制提到最前
        public static void BringFront(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            ShowWindow(h, SW_RESTORE);
            SetForegroundWindow(h);
        }
    }

    internal static class HotkeyText
    {
        public static string Format(uint mods, uint vk)
        {
            StringBuilder sb = new StringBuilder();
            if ((mods & Native.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((mods & Native.MOD_ALT) != 0) sb.Append("Alt+");
            if ((mods & Native.MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((mods & Native.MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(KeyName(vk));
            return sb.ToString();
        }

        public static string KeyName(uint vk)
        {
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1).ToString();
            return ((Keys)vk).ToString();
        }

        public static bool IsSupportedKey(uint vk)
        {
            if (vk >= 0x30 && vk <= 0x39) return true;
            if (vk >= 0x41 && vk <= 0x5A) return true;
            if (vk >= 0x70 && vk <= 0x87) return true;
            return false;
        }

        public static bool TryParse(string text, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrEmpty(text)) return false;

            string[] parts = text.Split(new char[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            uint m = 0;
            uint key = 0;
            bool hasKey = false;

            foreach (string raw in parts)
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;

                if (p.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("control", StringComparison.OrdinalIgnoreCase))
                {
                    m |= Native.MOD_CONTROL;
                    continue;
                }
                if (p.Equals("alt", StringComparison.OrdinalIgnoreCase))
                {
                    m |= Native.MOD_ALT;
                    continue;
                }
                if (p.Equals("shift", StringComparison.OrdinalIgnoreCase))
                {
                    m |= Native.MOD_SHIFT;
                    continue;
                }
                if (p.Equals("win", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("windows", StringComparison.OrdinalIgnoreCase))
                {
                    m |= Native.MOD_WIN;
                    continue;
                }

                if (hasKey) return false;
                uint candidate;
                if (!TryParseKey(p, out candidate)) return false;
                key = candidate;
                hasKey = true;
            }

            if (m == 0 || !hasKey) return false;
            mods = m;
            vk = key;
            return true;
        }

        private static bool TryParseKey(string p, out uint vk)
        {
            vk = 0;
            if (p.Length == 1)
            {
                char c = char.ToUpperInvariant(p[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    vk = (uint)c;
                    return true;
                }
                return false;
            }
            if (p[0] == 'F' || p[0] == 'f')
            {
                int n;
                if (int.TryParse(p.Substring(1), out n) && n >= 1 && n <= 24)
                {
                    vk = (uint)(0x70 + n - 1);
                    return true;
                }
            }
            return false;
        }
    }

    internal static class AppConfig
    {
        public const string DefaultHotkeyQr = "Ctrl+Alt+D";
        public const string DefaultHotkeyOcr = "Ctrl+Alt+X";
        public const string DefaultHotkeyMt = "Ctrl+Alt+F";     // 截图翻译
        public const string DefaultHotkeyHide = "Ctrl+Alt+G";   // 译文遮罩 显示/隐藏
        public const string ConfigVersion = "1301";
        // 引擎：wechat = 微信内置 OCR（默认，中文支持好）；system = Windows 内置 OCR
        // pp* = 本地 PP-OCR（模型放在 exe 同级 "OCR Model" 目录，离线可用，无需装任何软件）
        public const string EngineWeChat = "wechat";
        public const string EngineSystem = "system";
        public const string EnginePpTiny = "pptiny";
        public const string EnginePpSmall = "ppsmall";
        public const string EnginePpMedium = "ppmed";
        public const string ModelFolderName = "OCR Model";
        public const string MtFolderName = "MT Model";

        // 翻译模型根目录：优先用户指定，其次 exe 同级 "MT Model"，最后 %LOCALAPPDATA%
        public static string MtRoot()
        {
            string custom = MtDir != null ? MtDir.Trim() : string.Empty;
            if (custom.Length > 0) return custom;
            string beside = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), MtFolderName);
            try { if (!Directory.Exists(beside)) Directory.CreateDirectory(beside); return beside; }
            catch (Exception) { }
            return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XuYuan-SnapScan"), MtFolderName);
        }

        public static string ModelDir = string.Empty;  // 自定义模型根目录，空=exe 同级 OCR Model

        // 模型根目录：优先用户指定，其次 exe 同级 "OCR Model"，最后 %LOCALAPPDATA%
        public static string ModelRoot()
        {
            string custom = ModelDir != null ? ModelDir.Trim() : string.Empty;
            if (custom.Length > 0) return custom;
            string beside = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), ModelFolderName);
            try { if (!Directory.Exists(beside)) Directory.CreateDirectory(beside); return beside; }
            catch (Exception) { }
            return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XuYuan-SnapScan"), ModelFolderName);
        }

        // 某个 PP-OCR 档位的模型目录
        public static string ModelTierDir(string engine)
        {
            string tier = "tiny";
            if (engine == EnginePpMedium) tier = "medium";
            return Path.Combine(Path.Combine(ModelRoot(), "PP-OCR"), tier);
        }
        public const string FileName = "XuYuan-SnapScan_config.ini";
        public const int DefaultBackOpacity = 30;
        public const int DefaultFrost = 3;

        private static string _activePath = string.Empty;

        // 运行时配置（Load 填、Save 写）
        public static string HotkeyQr = DefaultHotkeyQr;
        public static string HotkeyOcr = DefaultHotkeyOcr;
        public static string HotkeyMt = DefaultHotkeyMt;
        public static string HotkeyHide = DefaultHotkeyHide;
        public static bool AutoCopy = true;
        public static string Engine = EngineWeChat;
        public static string WeChatDir = string.Empty;   // 手动指定的微信安装目录（自动探测失败时用）
        public static int BackOpacity = DefaultBackOpacity;
        public static int Frost = DefaultFrost;
        // 右下角「识别中 / 识别完成」悬浮提示；关掉后完全静默
        public static bool ShowToast = true;
        public static bool RestoreWin = false;           // 识别后恢复本软件窗口
        public static bool RestoreAfterQr = false;       // 扫码后显示本软件窗口
        public static bool Freehand = false;             // 自由圈选（不规则选区）
        public static bool Translate = true;            // 翻译功能启用（没有模型时界面会置灰）
        public static string TargetLang = "zh";          // 目标语言
        public static string MtPair = string.Empty;      // 选中的翻译模型（MT Model 下的目录名），空=自动
        public static string MtDir = string.Empty;       // 自定义翻译模型根目录，空=exe 同级 MT Model
        public static bool MtBar = true;                 // 译文右下角「✕ 关闭译文」控制条是否显示
        // 翻译线程档位：后台无感 / 均衡 / 性能 / 全力以赴
        public const string MtPerfSilent = "silent";
        public const string MtPerfBalanced = "balanced";
        public const string MtPerfPerformance = "performance";
        public const string MtPerfFull = "full";
        public static string MtPerf = MtPerfBalanced;
        // GPU 加速：auto = 有 Vulkan 运行时就用，失败回 CPU；on = 强制尝试；off = 纯 CPU
        public static string MtGpu = "auto";
        // ONNX（PP-OCR / Opus-MT）的 DirectML：实测 PP-OCR 框质量下降且不快，默认关闭
        public static bool OcrGpu;
        // PP 检测 + 微信识字融合：默认关（纯识字保持微信极速）；翻译链路可开
        public static bool OcrFusion;
        // 重复截图缓存：同一屏（允许轻微差异）直接贴上次成品，不再 OCR/翻译
        public static bool ShotCacheEnabled = true;
        public const int DefaultMtIdle = 30;             // 闲置多少秒没翻译就卸载翻译服务（0 = 不自动卸载）
        public static int MtIdle = DefaultMtIdle;
        public const int DefaultMtMaskAlpha = 100;       // 译文遮罩不透明度（0-100，100=完全不透明）
        public static int MtMaskAlpha = DefaultMtMaskAlpha;
        public const string MtModeEfficiency = "efficiency";   // 效率：遮罩块直接盖住原文
        public const string MtModeRealistic = "realistic";     // 拟真：擦掉原字、按背景补洞、再画译文
        public static string MtMode = MtModeEfficiency;
        // 翻译模式（与上面的「译文遮罩」是两件事）
        public const string MtProfileStandard = "standard";      // 标准：用大模型（HyMT1.5）翻所有方向
        public const string MtProfileEfficiency = "efficiency";  // 效率：英→中 / 日→中 先走小模型，其余回退标准
        public const string MtProfileGame = "game";              // 游戏：只用中日互译，强制停用标准模型
        public static string MtProfile = MtProfileStandard;

        // ---------- AI 大模型（只做辅助增强，默认 DeepSeek 官方；不思考、不联网，只求快） ----------
        public const string AiProviderDeepSeek = "deepseek";
        public static string AiProvider = AiProviderDeepSeek;
        public static string AiModel = "deepseek-flash";                       // 模型名称，用户可改
        public static string AiBase = "https://api.deepseek.com";     // 官方地址
        public static string AiKey = "";                                 // 用户自己填 key
        public static bool AiOcr;                                        // AI 识字强化：只修 OCR 认错的符号
        public static bool AiCheck;                                      // AI 译文质检：只重翻低质量行
        public static bool AiMerge;                                      // AI 短句合并：窄栏续行先合并再翻
        public static bool AiMt;                                         // AI 翻译接管：只用大模型翻，不走本地模型
        public static bool AiLayout = true;                              // AI 版面强化：分栏/徽章/段落切分后再翻译、回填
        public static bool Legacy;

        public static string ConfigFolder
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XuYuan-SnapScan");
            }
        }

        // 首选：程序同目录的 XuYuan-SnapScan_config.ini（绿色便携）
        public static string PrimaryConfigPath
        {
            get
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                return Path.Combine(dir, FileName);
            }
        }

        // 老版本的位置，只用于迁移读取；目录不可写时也退到这里
        public static string LegacyConfigPath
        {
            get { return Path.Combine(ConfigFolder, "config.ini"); }
        }

        public static string ConfigPath
        {
            get { return _activePath.Length > 0 ? _activePath : PrimaryConfigPath; }
        }

        public static string NormalizeEngine(string value)
        {
            if (string.IsNullOrEmpty(value)) return EngineWeChat;
            string v = value.Trim().ToLowerInvariant();
            // 兼容旧版 auto / rapid / ppocr 等写法；标准档已下线，旧配置一律并到极速
            if (v == EngineSystem || v == "win" || v == "windows") return EngineSystem;
            if (v == EnginePpTiny || v == "ppocr" || v == "rapid" || v == "tiny" || v == "pp-ocr") return EnginePpTiny;
            if (v == EnginePpSmall || v == "small" || v == "standard") return EnginePpTiny;
            if (v == EnginePpMedium || v == "medium" || v == "med" || v == "高精") return EnginePpTiny;
            return EngineWeChat;
        }

        public static bool IsLocalEngine(string engine)
        {
            string e = NormalizeEngine(engine);
            return e == EnginePpTiny || e == EnginePpSmall || e == EnginePpMedium;
        }

        private static bool ToBool(string v)
        {
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        // 翻译线程档位：兼容中文/英文/数字写法，统一成英文键
        public static string NormalizeMtPerf(string v)
        {
            if (v == null) return MtPerfBalanced;
            string s = v.Trim().ToLowerInvariant();
            if (s.Length == 0) return MtPerfBalanced;
            if (s == MtPerfSilent || s == "0" || s == "后台无感" || s == "silentmode") return MtPerfSilent;
            if (s == MtPerfBalanced || s == "1" || s == "均衡") return MtPerfBalanced;
            if (s == MtPerfPerformance || s == "2" || s == "性能") return MtPerfPerformance;
            if (s == MtPerfFull || s == "3" || s == "全力以赴" || s == "full-power") return MtPerfFull;
            return MtPerfBalanced;
        }

        public static string NormalizeMtGpu(string v)
        {
            if (v == null) return "auto";
            string s = v.Trim().ToLowerInvariant();
            if (s == "on" || s == "1" || s == "true" || s == "开启") return "on";
            if (s == "off" || s == "0" || s == "false" || s == "关闭") return "off";
            return "auto";
        }

        // 空闲释放秒数：0 或负数=不自动释放；其余夹在 5–86400 之间
        public static int NormalizeMtIdle(int v)
        {
            if (v <= 0) return 0;
            if (v < 5) return 5;
            if (v > 86400) return 86400;
            return v;
        }

        // 译文遮罩不透明度：夹在 0–100
        public static int NormalizeMtMaskAlpha(int v)
        {
            if (v < 0) return 0;
            if (v > 100) return 100;
            return v;
        }

        // 译文模式：效率 / 拟真，其余一律按效率
        public static string NormalizeMtMode(string v)
        {
            if (v == null) return MtModeEfficiency;
            string s = v.Trim().ToLowerInvariant();
            if (s == MtModeRealistic || s == "real" || s == "拟真") return MtModeRealistic;
            return MtModeEfficiency;
        }

        // 翻译模式：标准 / 效率；旧的 game 一律并到效率（0.5B 短句模型）
        public static string NormalizeMtProfile(string v)
        {
            if (v == null) return MtProfileStandard;
            string s = v.Trim().ToLowerInvariant();
            if (s == MtProfileEfficiency || s == "eff" || s == "效率") return MtProfileEfficiency;
            if (s == MtProfileGame || s == "游戏" || s == "game") return MtProfileEfficiency;
            return MtProfileStandard;
        }

        public static string MtProfileName(string v)
        {
            string s = NormalizeMtProfile(v);
            if (s == MtProfileEfficiency) return "效率";
            return "标准";
        }

        public static void Load()
        {
            HotkeyQr = DefaultHotkeyQr;
            HotkeyOcr = DefaultHotkeyOcr;
            HotkeyMt = DefaultHotkeyMt;
            HotkeyHide = DefaultHotkeyHide;
            AutoCopy = true;
            Engine = EngineWeChat;
            WeChatDir = string.Empty;
            BackOpacity = DefaultBackOpacity;
            Frost = DefaultFrost;
            RestoreWin = false;
            RestoreAfterQr = false;
            Freehand = false;
            Translate = true;
            TargetLang = "zh";
            MtPair = string.Empty;
            MtDir = string.Empty;
            MtBar = true;
            MtPerf = MtPerfBalanced;
            MtIdle = DefaultMtIdle;
            MtMaskAlpha = DefaultMtMaskAlpha;
            MtMode = MtModeEfficiency;
            MtProfile = MtProfileStandard;
            AiProvider = AiProviderDeepSeek;
            AiModel = "deepseek-flash";
            AiBase = "https://api.deepseek.com";
            AiKey = "";
            AiOcr = false;
            AiCheck = false;
            AiMerge = false;
            AiMt = false;
            AiLayout = true;
            Legacy = false;
            try
            {
                string p = PrimaryConfigPath;
                if (!File.Exists(p)) p = LegacyConfigPath;
                if (!File.Exists(p)) return;
                _activePath = p;

                string ver = string.Empty;
                foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim();
                    string v = line.Substring(i + 1).Trim();
                    if (k.Equals("ver", StringComparison.OrdinalIgnoreCase)) ver = v;
                    else if (k.Equals("hotkey", StringComparison.OrdinalIgnoreCase) && v.Length > 0) HotkeyQr = v;
                    else if (k.Equals("hotkey2", StringComparison.OrdinalIgnoreCase) && v.Length > 0) HotkeyOcr = v;
                    else if (k.Equals("hotkey3", StringComparison.OrdinalIgnoreCase) && v.Length > 0) HotkeyMt = v;
                    else if (k.Equals("hotkey4", StringComparison.OrdinalIgnoreCase) && v.Length > 0) HotkeyHide = v;
                    else if (k.Equals("autocopy", StringComparison.OrdinalIgnoreCase)) AutoCopy = ToBool(v);
                    else if (k.Equals("engine", StringComparison.OrdinalIgnoreCase) && v.Length > 0) Engine = NormalizeEngine(v);
                    else if (k.Equals("bgopacity", StringComparison.OrdinalIgnoreCase))
                    {
                        int iv;
                        if (int.TryParse(v, out iv) && iv >= 0 && iv <= 100) BackOpacity = iv;
                    }
                    else if (k.Equals("frost", StringComparison.OrdinalIgnoreCase))
                    {
                        int iv;
                        if (int.TryParse(v, out iv) && iv >= 0 && iv <= 20) Frost = iv;
                    }
                    else if (k.Equals("toast", StringComparison.OrdinalIgnoreCase)) ShowToast = ToBool(v);
                    else if (k.Equals("restorewin", StringComparison.OrdinalIgnoreCase)) RestoreWin = ToBool(v);
                    else if (k.Equals("restoreqr", StringComparison.OrdinalIgnoreCase)) RestoreAfterQr = ToBool(v);
                    else if (k.Equals("freehand", StringComparison.OrdinalIgnoreCase)) Freehand = ToBool(v);
                    else if (k.Equals("translate", StringComparison.OrdinalIgnoreCase)) Translate = ToBool(v);
                    // 目标语言固定中文：老 ini 里的 mtlang 一律忽略，产品只做外语 → 中文
                    else if (k.Equals("mtlang", StringComparison.OrdinalIgnoreCase)) TargetLang = "zh";
                    else if (k.Equals("mtpair", StringComparison.OrdinalIgnoreCase)) MtPair = string.Empty;   // 手动选模型已取消
                    else if (k.Equals("mtdir", StringComparison.OrdinalIgnoreCase) && v.Length > 0) MtDir = v;
                    else if (k.Equals("mtbar", StringComparison.OrdinalIgnoreCase)) MtBar = ToBool(v);
                    else if (k.Equals("mtperf", StringComparison.OrdinalIgnoreCase)) MtPerf = NormalizeMtPerf(v);
                    else if (k.Equals("mtgpu", StringComparison.OrdinalIgnoreCase)) MtGpu = NormalizeMtGpu(v);
                    else if (k.Equals("ocrgpu", StringComparison.OrdinalIgnoreCase)) OcrGpu = ToBool(v);
                    else if (k.Equals("ocrfusion", StringComparison.OrdinalIgnoreCase)) OcrFusion = ToBool(v);
                    else if (k.Equals("shotcache", StringComparison.OrdinalIgnoreCase)) ShotCacheEnabled = ToBool(v);
                    else if (k.Equals("mtidle", StringComparison.OrdinalIgnoreCase))
                    {
                        int iv;
                        if (int.TryParse(v, out iv)) MtIdle = NormalizeMtIdle(iv);
                    }
                    else if (k.Equals("mtalpha", StringComparison.OrdinalIgnoreCase))
                    {
                        int av;
                        if (int.TryParse(v, out av)) MtMaskAlpha = NormalizeMtMaskAlpha(av);
                    }
                    else if (k.Equals("mtmode", StringComparison.OrdinalIgnoreCase)) MtMode = NormalizeMtMode(v);
                    else if (k.Equals("mtprofile", StringComparison.OrdinalIgnoreCase)) MtProfile = NormalizeMtProfile(v);
                    else if (k.Equals("aiprovider", StringComparison.OrdinalIgnoreCase) && v.Length > 0) AiProvider = v;
                    else if (k.Equals("aimodel", StringComparison.OrdinalIgnoreCase) && v.Length > 0) AiModel = v;
                    else if (k.Equals("aibase", StringComparison.OrdinalIgnoreCase) && v.Length > 0) AiBase = v;
                    else if (k.Equals("aikey", StringComparison.OrdinalIgnoreCase)) AiKey = v;
                    else if (k.Equals("aiocr", StringComparison.OrdinalIgnoreCase)) AiOcr = ToBool(v);
                    else if (k.Equals("aicheck", StringComparison.OrdinalIgnoreCase)) AiCheck = ToBool(v);
                    else if (k.Equals("aimerge", StringComparison.OrdinalIgnoreCase)) AiMerge = ToBool(v);
                    else if (k.Equals("aimt", StringComparison.OrdinalIgnoreCase)) AiMt = ToBool(v);
                    else if (k.Equals("ailayout", StringComparison.OrdinalIgnoreCase)) AiLayout = ToBool(v);
                    else if (k.Equals("modeldir", StringComparison.OrdinalIgnoreCase) && v.Length > 0) ModelDir = v;
                }
                Legacy = !string.Equals(ver, ConfigVersion, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
            }
        }

        public static void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("ver=").Append(ConfigVersion).Append("\r\n");
            sb.Append("hotkey=").Append(HotkeyQr).Append("\r\n");
            sb.Append("hotkey2=").Append(HotkeyOcr).Append("\r\n");
            sb.Append("hotkey3=").Append(HotkeyMt).Append("\r\n");
            sb.Append("hotkey4=").Append(HotkeyHide).Append("\r\n");
            sb.Append("autocopy=").Append(AutoCopy ? "1" : "0").Append("\r\n");
            sb.Append("engine=").Append(NormalizeEngine(Engine)).Append("\r\n");
            if (ModelDir != null && ModelDir.Length > 0) sb.Append("modeldir=").Append(ModelDir).Append("\r\n");
            sb.Append("bgopacity=").Append(BackOpacity).Append("\r\n");
            sb.Append("frost=").Append(Frost).Append("\r\n");
            sb.Append("toast=").Append(ShowToast ? "1" : "0").Append("\r\n");
            sb.Append("restorewin=").Append(RestoreWin ? "1" : "0").Append("\r\n");
            sb.Append("restoreqr=").Append(RestoreAfterQr ? "1" : "0").Append("\r\n");
            sb.Append("freehand=").Append(Freehand ? "1" : "0").Append("\r\n");
            sb.Append("translate=").Append(Translate ? "1" : "0").Append("\r\n");
            sb.Append("mtlang=").Append(TargetLang).Append("\r\n");
            if (MtPair != null && MtPair.Length > 0) sb.Append("mtpair=").Append(MtPair).Append("\r\n");
            if (MtDir != null && MtDir.Length > 0) sb.Append("mtdir=").Append(MtDir).Append("\r\n");
            sb.Append("mtbar=").Append(MtBar ? "1" : "0").Append("\r\n");
            sb.Append("mtperf=").Append(MtPerf).Append("\r\n");
            sb.Append("mtgpu=").Append(MtGpu).Append("\r\n");
            sb.Append("ocrgpu=").Append(OcrGpu ? "1" : "0").Append("\r\n");
            sb.Append("ocrfusion=").Append(OcrFusion ? "1" : "0").Append("\r\n");
            sb.Append("shotcache=").Append(ShotCacheEnabled ? "1" : "0").Append("\r\n");
            sb.Append("mtidle=").Append(MtIdle).Append("\r\n");
            sb.Append("mtalpha=").Append(MtMaskAlpha).Append("\r\n");
            sb.Append("mtmode=").Append(MtMode).Append("\r\n");
            sb.Append("mtprofile=").Append(MtProfile).Append("\r\n");
            sb.Append("aiprovider=").Append(AiProvider).Append("\r\n");
            sb.Append("aimodel=").Append(AiModel).Append("\r\n");
            sb.Append("aibase=").Append(AiBase).Append("\r\n");
            sb.Append("aikey=").Append(AiKey).Append("\r\n");
            sb.Append("aiocr=").Append(AiOcr ? "1" : "0").Append("\r\n");
            sb.Append("aicheck=").Append(AiCheck ? "1" : "0").Append("\r\n");
            sb.Append("aimerge=").Append(AiMerge ? "1" : "0").Append("\r\n");
            sb.Append("aimt=").Append(AiMt ? "1" : "0").Append("\r\n");
            sb.Append("ailayout=").Append(AiLayout ? "1" : "0").Append("\r\n");

            string text = sb.ToString();
            string primary = PrimaryConfigPath;
            try
            {
                string dir = Path.GetDirectoryName(primary);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(primary, text, new UTF8Encoding(false));
                _activePath = primary;
                return;
            }
            catch (Exception)
            {
            }

            // 程序目录不可写（例如放在 Program Files），退到 %APPDATA%
            try
            {
                Directory.CreateDirectory(ConfigFolder);
                File.WriteAllText(LegacyConfigPath, text, new UTF8Encoding(false));
                _activePath = LegacyConfigPath;
            }
            catch (Exception)
            {
            }
        }
    }

    internal static class QrDecoder
    {
        public static string Decode(Bitmap bitmap, out string info)
        {
            info = string.Empty;

            ZXing.BarcodeReader reader = new ZXing.BarcodeReader();
            reader.AutoRotate = true;
            reader.Options = new ZXing.Common.DecodingOptions();
            reader.Options.TryHarder = true;
            reader.Options.TryInverted = true;
            reader.Options.PossibleFormats = new List<ZXing.BarcodeFormat>(new ZXing.BarcodeFormat[] { ZXing.BarcodeFormat.QR_CODE });

            ZXing.Result r = SafeDecode(reader, bitmap);

            if (r == null)
            {
                using (Bitmap scaled = Scale(bitmap, 2))
                {
                    r = SafeDecode(reader, scaled);
                }
            }
            if (r == null)
            {
                using (Bitmap scaled3 = Scale(bitmap, 3))
                {
                    r = SafeDecode(reader, scaled3);
                }
            }

            if (r == null) return null;
            info = r.BarcodeFormat.ToString();
            return r.Text;
        }

        private static ZXing.Result SafeDecode(ZXing.BarcodeReader reader, Bitmap bitmap)
        {
            try
            {
                return reader.Decode(bitmap);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Bitmap Scale(Bitmap src, int factor)
        {
            int w = src.Width * factor;
            int h = src.Height * factor;
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
            }
            return dst;
        }
    }

    // ==========================================================================
    // 文字识别引擎层
    //   首选：微信客户端内置 OCR（用 mmmojo 起 wxocr 插件，中文识别明显更准）
    //   兜底：Windows 10/11 自带的 Windows.Media.Ocr（免安装）
    // 两条路都不需要额外装 Python / VS / 运行时，满足“Win10+ 即开即用”。
    // ==========================================================================

    // 引擎探测结果：只做文件/注册表级检查，不启动任何进程
    internal sealed class EngineProbe
    {
        public bool WeChatFound;
        public bool WeChatVerified;     // 本次运行里真正握手成功过
        public string WeChatInstallDir = string.Empty;
        public string WeChatVersion = string.Empty;
        public string WeChatHostExe = string.Empty;
        public string PluginDir = string.Empty;
        public string PluginDll = string.Empty;
        public string PluginVersion = string.Empty;
        public bool WeChat3Only;        // 只找到微信 3.x 的 32 位插件
        public string Problem = string.Empty;

        public bool WeChatUsable { get { return WeChatFound; } }
    }

    // 负责找微信 OCR 插件与宿主 exe
    internal static class WeChatOcrLocator
    {
        private const string PluginRoot4 = @"Tencent\xwechat\XPlugin\Plugins\WeChatOCR";
        private const string PluginRoot3 = @"Tencent\WeChat\XPlugin\Plugins\WeChatOCR";

        private static string AppData
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
        }

        // 4.x 插件：优先用随包自带的（OCR Model\WeChat\plugin），没有才用本机微信的
        private static string FindPluginDll(out string pluginVersion)
        {
            pluginVersion = string.Empty;

            // 1) 随包自带的离线副本
            try
            {
                string own = BundledPluginDir();
                string ownDll = Path.Combine(own, "wxocr.dll");
                if (File.Exists(ownDll))
                {
                    pluginVersion = "bundle";
                    return ownDll;
                }
            }
            catch (Exception)
            {
            }

            // 2) 本机微信 4.x 的插件
            string root = Path.Combine(AppData, PluginRoot4);
            string best = null;
            int bestVer = -1;
            try
            {
                if (!Directory.Exists(root)) return null;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    int v;
                    if (!int.TryParse(Path.GetFileName(dir), out v)) continue;
                    string dll = Path.Combine(dir, "extracted", "wxocr.dll");
                    if (!File.Exists(dll)) continue;
                    if (v > bestVer) { bestVer = v; best = dll; }
                }
            }
            catch (Exception)
            {
            }
            if (best != null) pluginVersion = bestVer.ToString();
            return best;
        }

        // 随包自带的微信 OCR 目录：<模型根>\WeChat
        internal static string BundledWeChatDir()
        {
            return Path.Combine(AppConfig.ModelRoot(), "WeChat");
        }

        internal static string BundledPluginDir()
        {
            return Path.Combine(BundledWeChatDir(), "plugin");
        }

        internal static string BundledRuntimeDir()
        {
            return Path.Combine(BundledWeChatDir(), "runtime");
        }

        // 3.x 插件是 WeChatOCR.exe（32 位），本工具跑不起来，只用来给出准确提示
        private static bool HasPlugin3()
        {
            try
            {
                string root = Path.Combine(AppData, PluginRoot3);
                if (!Directory.Exists(root)) return false;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    if (File.Exists(Path.Combine(dir, "extracted", "WeChatOCR.exe"))) return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private static void AddCandidate(List<string> list, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            string d = dir.Trim().TrimEnd('\\');
            if (d.Length == 0) return;
            foreach (string x in list)
            {
                if (string.Equals(x, d, StringComparison.OrdinalIgnoreCase)) return;
            }
            list.Add(d);
        }

        // 微信安装目录：注册表 → 正在运行的微信用途 → 常见安装位置 → 用户手动指定
        private static List<string> InstallCandidates(string manualDir)
        {
            List<string> list = new List<string>();

            // 0) 随包自带的运行时（有就不用管本机装没装微信）
            AddCandidate(list, BundledRuntimeDir());
            AddCandidate(list, manualDir);

            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Tencent\Weixin"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("InstallPath");
                        if (v != null) AddCandidate(list, v.ToString());
                    }
                }
            }
            catch (Exception)
            {
            }
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Tencent\Weixin"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("InstallPath");
                        if (v != null) AddCandidate(list, v.ToString());
                    }
                }
            }
            catch (Exception)
            {
            }

            // 微信正开着的时候，直接从进程路径拿，最准
            try
            {
                Process[] ps = Process.GetProcessesByName("Weixin");
                foreach (Process p in ps)
                {
                    try
                    {
                        string exe = p.MainModule.FileName;
                        if (string.IsNullOrEmpty(exe)) continue;
                        string dir = Path.GetDirectoryName(exe);
                        AddCandidate(list, dir);
                        AddCandidate(list, Path.Combine(dir, ".."));
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch (Exception)
            {
            }

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            AddCandidate(list, Path.Combine(pf, @"Tencent\Weixin"));
            AddCandidate(list, Path.Combine(pf86, @"Tencent\Weixin"));
            AddCandidate(list, Path.Combine(AppData, @"Tencent\Weixin"));
            AddCandidate(list, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Tencent\Weixin"));

            // 装了但装在别的盘（最常见的情况）：注册表值可能过期，看卸载项和开始菜单快捷方式最准
            AddUninstallCandidates(list);
            AddShortcutCandidates(list);
            return list;
        }

        // 卸载注册表里的 InstallLocation / DisplayIcon，常带真实安装路径
        private static void AddUninstallCandidates(List<string> list)
        {
            string[] roots = new string[]
            {
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (string root in roots)
            {
                foreach (bool machine in new bool[] { false, true })
                {
                    Microsoft.Win32.RegistryKey baseKey = machine
                        ? Microsoft.Win32.Registry.LocalMachine
                        : Microsoft.Win32.Registry.CurrentUser;
                    try
                    {
                        using (Microsoft.Win32.RegistryKey un = baseKey.OpenSubKey(root))
                        {
                            if (un == null) continue;
                            foreach (string sub in un.GetSubKeyNames())
                            {
                                using (Microsoft.Win32.RegistryKey item = un.OpenSubKey(sub))
                                {
                                    if (item == null) continue;
                                    object name = item.GetValue("DisplayName");
                                    string dn = name == null ? string.Empty : name.ToString();
                                    if (dn.IndexOf("微信", StringComparison.Ordinal) < 0
                                        && dn.IndexOf("WeChat", StringComparison.OrdinalIgnoreCase) < 0
                                        && dn.IndexOf("Weixin", StringComparison.OrdinalIgnoreCase) < 0) continue;

                                    object loc = item.GetValue("InstallLocation");
                                    if (loc != null) AddCandidate(list, loc.ToString());
                                    object icon = item.GetValue("DisplayIcon");
                                    if (icon != null)
                                    {
                                        string s = icon.ToString().Trim();
                                        int comma = s.IndexOf(',');
                                        if (comma > 0) s = s.Substring(0, comma);
                                        s = s.Trim('"');
                                        if (s.Length > 0) AddCandidate(list, Path.GetDirectoryName(s));
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        // 开始菜单 / 桌面快捷方式：微信安装器一定会建，目标路径就是真安装路径
        private static void AddShortcutCandidates(List<string> list)
        {
            List<string> dirs = new List<string>();
            try
            {
                dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
                dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
                dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
            }
            catch (Exception)
            {
            }
            foreach (string dir in dirs)
            {
                AddShortcutInDir(list, dir, 0);
            }
        }

        private static void AddShortcutInDir(List<string> list, string dir, int depth)
        {
            if (depth > 2) return;
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (string file in Directory.GetFiles(dir, "*.lnk"))
                {
                    string n = Path.GetFileNameWithoutExtension(file);
                    if (n.IndexOf("微信", StringComparison.Ordinal) < 0
                        && n.IndexOf("WeChat", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("Weixin", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string target = ResolveShortcut(file);
                    if (string.IsNullOrEmpty(target)) continue;
                    string exeDir = Path.GetDirectoryName(target);
                    AddCandidate(list, exeDir);
                    AddCandidate(list, Path.GetDirectoryName(exeDir));
                }
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    AddShortcutInDir(list, sub, depth + 1);
                }
            }
            catch (Exception)
            {
            }
        }

        // 解析 .lnk 目标；WScript.Shell 在 .NET Framework 里可直接用 COM
        private static string ResolveShortcut(string path)
        {
            object shell = null;
            object link = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                shell = Activator.CreateInstance(t);
                link = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                    null, shell, new object[] { path });
                object target = link.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, link, null);
                return target == null ? null : target.ToString();
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                try
                {
                    if (link != null) Marshal.ReleaseComObject(link);
                    if (shell != null) Marshal.ReleaseComObject(shell);
                }
                catch (Exception)
                {
                }
            }
        }

        // 最后的兜底：在固定磁盘里浅扫一层，找含 mmmojo_64.dll 的目录（限时，避免卡启动）
        private static string ScanForLibDir(int budgetMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(budgetMs);
            Queue<string> dirs = new Queue<string>();
            Queue<int> depths = new Queue<int>();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        dirs.Enqueue(d.RootDirectory.FullName);
                        depths.Enqueue(0);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }

            while (dirs.Count > 0 && DateTime.UtcNow < deadline)
            {
                string dir = dirs.Dequeue();
                int depth = depths.Dequeue();
                try
                {
                    if (File.Exists(Path.Combine(dir, "mmmojo_64.dll"))) return dir;
                }
                catch (Exception)
                {
                }
                if (depth >= 3) continue;
                string[] subs;
                try
                {
                    subs = Directory.GetDirectories(dir);
                }
                catch (Exception)
                {
                    continue;
                }
                if (subs.Length > 350) continue;   // 系统大目录就别钻了
                foreach (string sub in subs)
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("$", StringComparison.Ordinal)) continue;
                    dirs.Enqueue(sub);
                    depths.Enqueue(depth + 1);
                }
            }
            return null;
        }

        // 在候选目录里找“含 mmmojo_64.dll 的那一层”，返回 user-lib-dir
        private static string ResolveLibDir(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return null;
                if (File.Exists(Path.Combine(root, "mmmojo_64.dll"))) return root;
                string best = null;
                long bestVer = -1;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    if (!File.Exists(Path.Combine(dir, "mmmojo_64.dll"))) continue;
                    long ver = 0;
                    string name = Path.GetFileName(dir);
                    foreach (char c in name)
                    {
                        if (c >= '0' && c <= '9') ver = ver * 10 + (c - '0');
                    }
                    if (ver >= bestVer) { bestVer = ver; best = dir; }
                }
                return best;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ResolveHostExe(string libDir)
        {
            try
            {
                string[] names = new string[] { "Weixin.exe", "weixin.exe", "WeChat.exe" };
                foreach (string n in names)
                {
                    string p = Path.Combine(libDir, n);
                    if (File.Exists(p)) return p;
                }
                string parent = Path.GetDirectoryName(libDir);
                if (parent != null)
                {
                    foreach (string n in names)
                    {
                        string p = Path.Combine(parent, n);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        // 只认本软件自己的 OCR Model\WeChat 目录，不再去探测本机微信
        public static EngineProbe Detect()
        {
            EngineProbe probe = new EngineProbe();

            string pluginDir = BundledPluginDir();
            string runtimeDir = BundledRuntimeDir();
            string dll = Path.Combine(pluginDir, "wxocr.dll");

            if (!File.Exists(dll))
            {
                probe.Problem = "没找到微信 OCR 模型，请把 OCR Model\\WeChat 目录放进软件同级";
                return probe;
            }

            string libDir = ResolveLibDir(runtimeDir);
            if (libDir == null)
            {
                probe.Problem = "OCR Model\\WeChat\\runtime 里缺 mmmojo_64.dll";
                return probe;
            }

            string host = ResolveHostExe(libDir);
            if (host == null)
            {
                probe.Problem = "OCR Model\\WeChat\\runtime 里缺 Weixin.exe";
                return probe;
            }

            probe.PluginDll = dll;
            probe.PluginDir = pluginDir;
            probe.PluginVersion = "bundle";
            probe.WeChatInstallDir = libDir;
            probe.WeChatVersion = Path.GetFileName(libDir);
            probe.WeChatHostExe = host;
            probe.WeChatFound = true;
            return probe;
        }
    }

    // 微信 4.x OCR 客户端（纯 P/Invoke + 手写 protobuf，不依赖任何第三方库）
    internal static class WxOcr
    {
        private const int ReqHandshake = 10001;
        private const int ReqOcr = 10010;
        private const int RespOcr = 10011;

        // mmmojo 常量
        private const int CbReadPush = 1;
        private const int CbReadPull = 2;
        private const int CbReadShared = 3;
        private const int CbRemoteConnect = 4;
        private const int CbRemoteDisconnect = 5;
        private const int CbProcessLaunched = 6;
        private const int CbProcessLaunchFailed = 7;
        private const int CbMojoError = 8;

        private const int ParamHostProcess = 0;
        private const int ParamExePath = 2;

        private const int MethodPush = 1;

        private delegate void CbRead(uint requestId, IntPtr readInfo, IntPtr userData);
        private delegate void CbConnect(bool connected, IntPtr userData);
        private delegate void CbVoid(IntPtr userData);
        private delegate void CbLaunchFailed(int errorCode, IntPtr userData);
        private delegate void CbError(IntPtr errorBuf, int errorSize, IntPtr userData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FnInit(int argc, IntPtr argv);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FnCreateEnv();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FnSetCb(IntPtr env, int type, IntPtr cb);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FnSetParamInt(IntPtr env, int type, int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FnSetParamStr(IntPtr env, int type, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FnAppendSwitch(IntPtr env, string key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FnEnvOp(IntPtr env);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FnGetReadReq(IntPtr readInfo, out uint size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FnRemoveReadInfo(IntPtr readInfo);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FnCreateWriteInfo(int method, int sync, uint requestId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FnGetWriteReq(IntPtr writeInfo, UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool FnSendWrite(IntPtr env, IntPtr writeInfo);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FnDeinit();

        private static readonly object Sync = new object();
        private static IntPtr _module;
        private static IntPtr _env;
        private static string _startedFor = string.Empty;
        private static bool _connected;
        private static bool _handshakeDone;
        private static bool _supported;
        private static bool _faulted;
        private static string _fault = string.Empty;
        private static ulong _taskSeed = 2;
        private static IntPtr _hostPtr;   // 必须一直有效，不能释放

        private static FnGetReadReq _getReadReq;
        private static FnRemoveReadInfo _removeReadInfo;
        private static FnCreateWriteInfo _createWriteInfo;
        private static FnGetWriteReq _getWriteReq;
        private static FnSendWrite _sendWrite;

        private static readonly Dictionary<ulong, string> _texts = new Dictionary<ulong, string>();
        private static readonly Dictionary<ulong, List<Rectangle>> _boxes = new Dictionary<ulong, List<Rectangle>>();
        private static readonly Dictionary<ulong, int> _codes = new Dictionary<ulong, int>();
        private static readonly Dictionary<ulong, int[]> _sizes = new Dictionary<ulong, int[]>();

        // 回调委托必须被静态引用，否则会被 GC 回收导致崩溃
        private static CbRead _cbPush;
        private static CbRead _cbPull;
        private static CbRead _cbShared;
        private static CbConnect _cbConnect;
        private static CbVoid _cbDisconnect;
        private static CbVoid _cbLaunched;
        private static CbLaunchFailed _cbLaunchFailed;
        private static CbError _cbError;

        private static T Fn<T>(string name) where T : class
        {
            IntPtr p = GetProcAddress(_module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        private static void OnRead(uint requestId, IntPtr readInfo, IntPtr userData)
        {
            try
            {
                uint size = 0;
                IntPtr data = _getReadReq(readInfo, out size);
                byte[] buf = new byte[size];
                if (size > 0) Marshal.Copy(data, buf, 0, (int)size);
                _removeReadInfo(readInfo);
                HandleMessage(requestId, buf);
            }
            catch (Exception)
            {
            }
        }

        private static void HandleMessage(uint requestId, byte[] data)
        {
            if (requestId == ReqHandshake)
            {
                bool supported = false;
                PB.ForEachField(data, delegate(int field, int wire, byte[] payload, long varint)
                {
                    if (field == 1 && wire == 0) supported = varint != 0;
                });
                lock (Sync)
                {
                    _handshakeDone = true;
                    _supported = supported;
                    if (!supported) _fault = "微信 OCR 不支持当前环境";
                    Monitor.PulseAll(Sync);
                }
                return;
            }
            if (requestId != RespOcr) return;

            ulong taskId = 0;
            int errCode = 0;
            List<string> lines = new List<string>();
            List<Rectangle> boxes = new List<Rectangle>();
            int[] size = new int[2];
            PB.ForEachField(data, delegate(int field, int wire, byte[] payload, long varint)
            {
                if (field == 1 && wire == 0) taskId = (ulong)varint;
                else if (field == 2 && wire == 0) errCode = (int)varint;
                else if (field == 3 && wire == 2) ParseResultInfo(payload, lines, size, boxes);
            });

            StringBuilder sb = new StringBuilder();
            foreach (string line in lines)
            {
                if (sb.Length > 0) sb.Append("\r\n");
                sb.Append(line);
            }

            lock (Sync)
            {
                _texts[taskId] = sb.ToString();
                _boxes[taskId] = boxes;
                _codes[taskId] = errCode;
                _sizes[taskId] = size;
                Monitor.PulseAll(Sync);
            }
        }

        private static void ParseResultInfo(byte[] data, List<string> lines, int[] size, List<Rectangle> boxes)
        {
            if (Environment.GetEnvironmentVariable("QRSNAP_DUMP_OCR") == "1")
            {
                try
                {
                    StringBuilder dump = new StringBuilder();
                    DumpPb(data, 0, dump);
                    File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr-pb.txt"),
                        dump.ToString(), new UTF8Encoding(false));
                }
                catch (Exception) { }
            }
            PB.ForEachField(data, delegate(int field, int wire, byte[] payload, long varint)
            {
                if (field == 3 && wire == 2)
                {
                    string text = string.Empty;
                    Toks.Clear();
                    PB.ForEachField(payload, delegate(int f2, int w2, byte[] p2, long v2)
                    {
                        if (f2 == 2 && w2 == 2) text = Encoding.UTF8.GetString(p2);
                        else if (f2 == 4 && w2 == 2)
                        {
                            WxTok t = ParseTok(p2);
                            // 纯空格也是一个 token，丢了会把整行字粘成一串，必须留着
                            if (t != null && t.Text.Length > 0) Toks.Add(t);
                        }
                    });
                    // 微信 OCR 每个词都带四角坐标，用它自己重组行：
                    // 同一水平带上左右两栏的文字，中间隔着大片空白，直接断行。
                    // 否则「Level 24」和右边正文首行会被拼成一行，AI 会把 Level 24 误改成 Level 2.4。
                    if (Toks.Count >= 2 && RebuildFromTokens(lines, boxes)) { }
                    // 空行（纯噪点）丢掉，避免结果里出现一串空行
                    else if (text.Trim().Length > 0)
                    {
                        lines.Add(text);
                        boxes.Add(Rectangle.Empty);
                    }
                }
                else if (field == 4 && wire == 0) size[0] = (int)varint;
                else if (field == 5 && wire == 0) size[1] = (int)varint;
            });
        }

        private sealed class WxTok
        {
            public string Text;
            public float X0, Y0, X1, Y1;
            public float H { get { return Y1 - Y0; } }
            public float CY { get { return (Y0 + Y1) * 0.5f; } }
        }

        [ThreadStatic]
        private static List<WxTok> _toks;

        private static List<WxTok> Toks
        {
            get
            {
                if (_toks == null) _toks = new List<WxTok>();
                return _toks;
            }
        }

        private static WxTok ParseTok(byte[] payload)
        {
            string text = null;
            float[] acc = new float[] { float.MaxValue, float.MaxValue, float.MinValue, float.MinValue };
            PB.ForEachField(payload, delegate(int f, int w, byte[] p, long v)
            {
                if (f == 2 && w == 2) text = Encoding.UTF8.GetString(p);
                else if (f == 1 && w == 2) ParseCorners(p, acc);
            });
            if (text == null || acc[0] == float.MaxValue) return null;
            WxTok t = new WxTok();
            t.Text = text;
            t.X0 = acc[0];
            t.Y0 = acc[1];
            t.X1 = acc[2];
            t.Y1 = acc[3];
            return t;
        }

        private static void ParseCorners(byte[] payload, float[] acc)
        {
            PB.ForEachField(payload, delegate(int f, int w, byte[] p, long v)
            {
                if (w != 2 || p == null) return;
                float cx = float.NaN, cy = float.NaN;
                PB.ForEachField(p, delegate(int f2, int w2, byte[] p2, long v2)
                {
                    if (w2 == 5 && p2 != null && p2.Length == 4)
                    {
                        float val = BitConverter.ToSingle(p2, 0);
                        if (f2 == 1) cx = val;
                        else if (f2 == 2) cy = val;
                    }
                });
                if (float.IsNaN(cx) || float.IsNaN(cy)) return;
                if (cx < acc[0]) acc[0] = cx;
                if (cy < acc[1]) acc[1] = cy;
                if (cx > acc[2]) acc[2] = cx;
                if (cy > acc[3]) acc[3] = cy;
            });
        }

        // 按坐标重排：先按纵坐标聚成行，行内按横向排；
        // 两个词之间的横向空隙超过约两个字高，就认为跨了栏，从这里断开。
        // 每行同时交出自己的包围盒，供遮罩精确落位（不再靠别的引擎按序号均分）。
        private static bool RebuildFromTokens(List<string> lines, List<Rectangle> boxes)
        {
            List<WxTok> all = new List<WxTok>();
            for (int i = 0; i < Toks.Count; i++) all.Add(Toks[i]);
            if (all.Count < 2) return false;

            float sumH = 0f;
            int cnt = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].H > 0f) { sumH += all[i].H; cnt++; }
            }
            if (cnt == 0) return false;
            float lineH = sumH / cnt;
            float sameRow = lineH * 0.6f;
            float cutGap = Math.Max(lineH * 1.6f, 20f);

            all.Sort(delegate(WxTok a, WxTok b) { return a.CY.CompareTo(b.CY); });

            List<List<WxTok>> rows = new List<List<WxTok>>();
            List<WxTok> row = null;
            float rowCy = 0f;
            for (int i = 0; i < all.Count; i++)
            {
                WxTok t = all[i];
                if (row != null && Math.Abs(t.CY - rowCy) <= sameRow)
                {
                    row.Add(t);
                    // 行基准慢慢往中间挪，避免一行字高低不齐时越漂越远
                    rowCy = (rowCy * (row.Count - 1) + t.CY) / row.Count;
                    continue;
                }
                row = new List<WxTok>();
                row.Add(t);
                rowCy = t.CY;
                rows.Add(row);
            }

            int produced = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                List<WxTok> one = rows[r];
                one.Sort(delegate(WxTok a, WxTok b) { return a.X0.CompareTo(b.X0); });
                StringBuilder sb = new StringBuilder();
                float prevX1 = 0f;
                bool has = false;
                float bx0 = 0f, by0 = 0f, bx1 = 0f, by1 = 0f;
                for (int i = 0; i < one.Count; i++)
                {
                    WxTok t = one[i];
                    if (has && t.X0 - prevX1 > cutGap && sb.ToString().Trim().Length > 0)
                    {
                        AddLine(lines, boxes, sb.ToString().TrimEnd(), bx0, by0, bx1, by1);
                        produced++;
                        sb.Length = 0;
                        has = false;
                    }
                    sb.Append(t.Text);
                    if (!has)
                    {
                        bx0 = t.X0; by0 = t.Y0; bx1 = t.X1; by1 = t.Y1;
                    }
                    else
                    {
                        if (t.X0 < bx0) bx0 = t.X0;
                        if (t.Y0 < by0) by0 = t.Y0;
                        if (t.X1 > bx1) bx1 = t.X1;
                        if (t.Y1 > by1) by1 = t.Y1;
                    }
                    prevX1 = t.X1;
                    has = true;
                }
                if (sb.ToString().Trim().Length > 0)
                {
                    AddLine(lines, boxes, sb.ToString().TrimEnd(), bx0, by0, bx1, by1);
                    produced++;
                }
            }
            return produced > 0;
        }

        private static void AddLine(List<string> lines, List<Rectangle> boxes, string text,
            float x0, float y0, float x1, float y1)
        {
            lines.Add(text);
            int ix = (int)Math.Floor(x0);
            int iy = (int)Math.Floor(y0);
            int iw = (int)Math.Ceiling(x1) - ix;
            int ih = (int)Math.Ceiling(y1) - iy;
            if (iw < 1) iw = 1;
            if (ih < 1) ih = 1;
            boxes.Add(new Rectangle(ix, iy, iw, ih));
        }

        // 排查用：把微信 OCR 回包的字段结构摊开，确认每个识别单元到底带不带坐标
        private static void DumpPb(byte[] data, int depth, StringBuilder sb)
        {
            if (data == null || depth > 4) return;
            PB.ForEachField(data, delegate(int f, int w, byte[] p, long v)
            {
                sb.Append(' ', depth * 2).Append("f").Append(f).Append(" w").Append(w);
                if (w == 0) sb.Append(" v=").Append(v);
                if (w == 5 && p != null && p.Length == 4)
                    sb.Append(" f=").Append(BitConverter.ToSingle(p, 0).ToString("0.##"));
                if (w == 2)
                {
                    sb.Append(" len=").Append(p == null ? 0 : p.Length);
                    if (p != null && p.Length > 0 && p.Length <= 120)
                    {
                        string s = Encoding.UTF8.GetString(p);
                        bool ascii = s.Length > 0;
                        for (int i = 0; i < s.Length; i++)
                            if (s[i] < 32 || s[i] > 126) { ascii = false; break; }
                        if (ascii) sb.Append(" txt=\"").Append(s).Append('"');
                    }
                }
                sb.Append('\n');
                if (w == 2 && p != null && depth < 3) DumpPb(p, depth + 1, sb);
            });
        }

        public static bool IsRunning
        {
            get
            {
                lock (Sync)
                {
                    return _env != IntPtr.Zero && _handshakeDone && _supported;
                }
            }
        }

        public static bool Start(EngineProbe probe, out string note)
        {
            note = string.Empty;
            lock (Sync)
            {
                // 已经为同一份微信起过一次，直接复用
                if (_env != IntPtr.Zero && string.Equals(_startedFor, probe.PluginDll, StringComparison.OrdinalIgnoreCase))
                {
                    if (_faulted)
                    {
                        note = _fault;
                        return false;
                     }
                    return WaitHandshake(12000, out note);
                }
                if (_env != IntPtr.Zero)
                {
                    Stop();
                }

                _handshakeDone = false;
                _supported = false;
                _faulted = false;
                _fault = string.Empty;
                _texts.Clear();
                _boxes.Clear();
                _codes.Clear();
                _sizes.Clear();

                try
                {
                    string mojo = Path.Combine(probe.WeChatInstallDir, "mmmojo_64.dll");
                    _module = LoadLibraryExW(mojo, IntPtr.Zero, 0x00000008);
                    if (_module == IntPtr.Zero)
                    {
                        int err = Marshal.GetLastWin32Error();
                        note = err == 193
                            ? "微信是本机 32 位版本，和本工具 64 位进程不匹配"
                            : "加载 mmmojo_64.dll 失败（错误 " + err + "）";
                        _faulted = true;
                        _fault = note;
                        return false;
                    }

                    FnInit init = Fn<FnInit>("InitializeMMMojo");
                    FnCreateEnv create = Fn<FnCreateEnv>("CreateMMMojoEnvironment");
                    FnSetCb setCb = Fn<FnSetCb>("SetMMMojoEnvironmentCallbacks");
                    FnSetParamInt setInt = Fn<FnSetParamInt>("SetMMMojoEnvironmentInitParams");
                    FnSetParamStr setStr = Fn<FnSetParamStr>("SetMMMojoEnvironmentInitParams");
                    FnAppendSwitch append = Fn<FnAppendSwitch>("AppendMMSubProcessSwitchNative");
                    FnEnvOp startEnv = Fn<FnEnvOp>("StartMMMojoEnvironment");
                    _getReadReq = Fn<FnGetReadReq>("GetMMMojoReadInfoRequest");
                    _removeReadInfo = Fn<FnRemoveReadInfo>("RemoveMMMojoReadInfo");
                    _createWriteInfo = Fn<FnCreateWriteInfo>("CreateMMMojoWriteInfo");
                    _getWriteReq = Fn<FnGetWriteReq>("GetMMMojoWriteInfoRequest");
                    _sendWrite = Fn<FnSendWrite>("SendMMMojoWriteInfo");

                    _cbPush = OnRead;
                    _cbPull = OnRead;
                    _cbShared = OnRead;
                    _cbConnect = delegate(bool ok, IntPtr u)
                    {
                        lock (Sync)
                        {
                            _connected = ok;
                            if (!ok) _fault = "与微信 OCR 进程断开连接";
                            Monitor.PulseAll(Sync);
                        }
                    };
                    _cbDisconnect = delegate(IntPtr u)
                    {
                        lock (Sync)
                        {
                            _faulted = true;
                            _fault = "微信 OCR 进程意外退出";
                            Monitor.PulseAll(Sync);
                        }
                    };
                    _cbLaunched = delegate(IntPtr u) { };
                    _cbLaunchFailed = delegate(int code, IntPtr u)
                    {
                        lock (Sync)
                        {
                            _faulted = true;
                            _fault = "微信 OCR 进程启动失败（code " + code + "）";
                            Monitor.PulseAll(Sync);
                        }
                    };
                    _cbError = delegate(IntPtr buf, int len, IntPtr u)
                    {
                        string s = string.Empty;
                        if (len > 0)
                        {
                            byte[] b = new byte[len];
                            Marshal.Copy(buf, b, 0, len);
                            s = Encoding.UTF8.GetString(b);
                        }
                        lock (Sync)
                        {
                            _fault = "微信 OCR 报错：" + s;
                            Monitor.PulseAll(Sync);
                        }
                    };

                    init(0, IntPtr.Zero);
                    _env = create();
                    if (_env == IntPtr.Zero)
                    {
                        note = "创建微信 OCR 环境失败";
                        _faulted = true;
                        _fault = note;
                        return false;
                    }

                    setCb(_env, CbReadPush, Marshal.GetFunctionPointerForDelegate(_cbPush));
                    setCb(_env, CbReadPull, Marshal.GetFunctionPointerForDelegate(_cbPull));
                    setCb(_env, CbReadShared, Marshal.GetFunctionPointerForDelegate(_cbShared));
                    setCb(_env, CbRemoteConnect, Marshal.GetFunctionPointerForDelegate(_cbConnect));
                    setCb(_env, CbRemoteDisconnect, Marshal.GetFunctionPointerForDelegate(_cbDisconnect));
                    setCb(_env, CbProcessLaunched, Marshal.GetFunctionPointerForDelegate(_cbLaunched));
                    setCb(_env, CbProcessLaunchFailed, Marshal.GetFunctionPointerForDelegate(_cbLaunchFailed));
                    setCb(_env, CbMojoError, Marshal.GetFunctionPointerForDelegate(_cbError));

                    // 注意：宽字符指针必须常驻，GC 一挪就崩
                    _hostPtr = Marshal.StringToHGlobalUni(probe.WeChatHostExe);
                    setInt(_env, ParamHostProcess, 1);
                    setStr(_env, ParamExePath, _hostPtr);

                    append(_env, "no-sandbox", string.Empty);
                    append(_env, "user-lib-dir", probe.WeChatInstallDir);
                    append(_env, "type", Path.GetFileNameWithoutExtension(probe.PluginDll));
                    append(_env, "app-path", probe.PluginDir);

                    _startedFor = probe.PluginDll;
                    startEnv(_env);
                }
                catch (Exception ex)
                {
                    note = "启动微信 OCR 失败：" + ex.Message;
                    _faulted = true;
                    _fault = note;
                    Stop();
                    return false;
                }
            }

            // 等待握手在锁外做，避免回调线程被锁住
            return WaitHandshake(15000, out note);
        }

        private static bool WaitHandshake(int timeoutMs, out string note)
        {
            note = string.Empty;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (Sync)
            {
                while (!_handshakeDone && !_faulted && DateTime.UtcNow < deadline)
                    Monitor.Wait(Sync, 150);
                if (_handshakeDone && _supported) return true;
                if (_handshakeDone && !_supported)
                {
                    note = "微信 OCR 不支持当前环境";
                    _faulted = true;
                    _fault = note;
                    return false;
                }
                note = _faulted
                    ? (_fault.Length > 0 ? _fault : "微信 OCR 启动失败")
                    : "微信 OCR 启动超时（微信刚启动时偶尔会慢一点，重试一次即可）";
                if (!_faulted)
                {
                    // 超时不算永久故障，下次还能再试
                    return false;
                }
                return false;
            }
        }

        public static string Recognize(string pngPath, int timeoutMs, out string note)
        {
            return Recognize(pngPath, timeoutMs, out note, null);
        }

        public static string Recognize(string pngPath, int timeoutMs, out string note, List<Rectangle> outBoxes)
        {
            note = string.Empty;
            if (_env == IntPtr.Zero) { note = "微信 OCR 未启动"; return null; }

            ulong taskId;
            lock (Sync)
            {
                taskId = _taskSeed++;
            }

            byte[] req = PB.EncodeOcrRequest(taskId, Path.GetFullPath(pngPath));
            IntPtr wi = _createWriteInfo(MethodPush, 0, ReqOcr);
            if (wi == IntPtr.Zero) { note = "创建请求失败"; return null; }
            IntPtr buf = _getWriteReq(wi, (UIntPtr)req.Length);
            if (buf == IntPtr.Zero) { note = "申请请求缓冲失败"; return null; }
            Marshal.Copy(req, 0, buf, req.Length);
            if (!_sendWrite(_env, wi)) { note = "发送识别请求失败"; return null; }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (Sync)
            {
                while (!_texts.ContainsKey(taskId) && !_faulted && DateTime.UtcNow < deadline)
                    Monitor.Wait(Sync, 150);
                if (!_texts.ContainsKey(taskId))
                {
                    note = _faulted && _fault.Length > 0 ? _fault : "微信 OCR 识别超时";
                    return null;
                }
                int code = _codes[taskId];
                int[] size = _sizes[taskId];
                if (code != 0)
                {
                    note = "微信 OCR 返回错误码 " + code;
                    return null;
                }
                if (size[0] > 0) note = size[0].ToString() + "×" + size[1].ToString();
                if (outBoxes != null)
                {
                    outBoxes.Clear();
                    List<Rectangle> got = _boxes[taskId];
                    if (got != null)
                        for (int i = 0; i < got.Count; i++) outBoxes.Add(got[i]);
                }
                return _texts[taskId];
            }
        }

        public static void Stop()
        {
            lock (Sync)
            {
                if (_env != IntPtr.Zero)
                {
                    try
                    {
                        FnEnvOp stopEnv = Fn<FnEnvOp>("StopMMMojoEnvironment");
                        FnEnvOp removeEnv = Fn<FnEnvOp>("RemoveMMMojoEnvironment");
                        stopEnv(_env);
                        removeEnv(_env);
                    }
                    catch (Exception)
                    {
                    }
                    _env = IntPtr.Zero;
                }
                _startedFor = string.Empty;
                _handshakeDone = false;
                _connected = false;
                // mmmojo 内部线程不会随 env 一起结束，模块句柄故意不释放，避免卸载时崩
            }
        }

        public static string ShutdownAndGetNote()
        {
            Stop();
            return string.Empty;
        }
    }

    // 极简 protobuf：只做我们需要的“字段遍历 + 编码一条 OCR 请求”
    internal static class PB
    {
        public delegate void FieldHandler(int field, int wire, byte[] payload, long varint);

        public static void ForEachField(byte[] data, FieldHandler handler)
        {
            int i = 0;
            while (i < data.Length)
            {
                long tag = 0;
                int shift = 0;
                while (true)
                {
                    if (i >= data.Length) return;
                    byte b = data[i++];
                    tag |= (long)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }
                int field = (int)(tag >> 3);
                int wire = (int)(tag & 7);
                if (wire == 0)
                {
                    long v = 0;
                    shift = 0;
                    while (true)
                    {
                        if (i >= data.Length) return;
                        byte b = data[i++];
                        v |= (long)(b & 0x7F) << shift;
                        if ((b & 0x80) == 0) break;
                        shift += 7;
                    }
                    handler(field, wire, null, v);
                }
                else if (wire == 1)
                {
                    if (i + 8 > data.Length) return;
                    byte[] p8 = new byte[8];
                    Array.Copy(data, i, p8, 0, 8);
                    i += 8;
                    handler(field, wire, p8, 0);
                }
                else if (wire == 2)
                {
                    long len = 0;
                    shift = 0;
                    while (true)
                    {
                        if (i >= data.Length) return;
                        byte b = data[i++];
                        len |= (long)(b & 0x7F) << shift;
                        if ((b & 0x80) == 0) break;
                        shift += 7;
                    }
                    if (len < 0 || i + len > data.Length) return;
                    byte[] p = new byte[(int)len];
                    Array.Copy(data, i, p, 0, (int)len);
                    i += (int)len;
                    handler(field, wire, p, 0);
                }
                else if (wire == 5)
                {
                    if (i + 4 > data.Length) return;
                    byte[] p4 = new byte[4];
                    Array.Copy(data, i, p4, 0, 4);
                    i += 4;
                    handler(field, wire, p4, 0);
                }
                else
                {
                    return;
                }
            }
        }

        private static void Varint(Stream s, long v)
        {
            ulong u = (ulong)v;
            while (u >= 0x80)
            {
                s.WriteByte((byte)((u | 0x80) & 0xFF));
                u >>= 7;
            }
            s.WriteByte((byte)u);
        }

        private static void Tag(Stream s, int field, int wire)
        {
            Varint(s, (field << 3) | wire);
        }

        // ParseOCRReqMessage{1:task_id, 2:pic_path, 6:rt{t1,t2,t3}}
        public static byte[] EncodeOcrRequest(ulong taskId, string picPath)
        {
            MemoryStream ms = new MemoryStream();
            Tag(ms, 1, 0);
            Varint(ms, (long)taskId);
            byte[] path = Encoding.UTF8.GetBytes(picPath);
            Tag(ms, 2, 2);
            Varint(ms, path.Length);
            ms.Write(path, 0, path.Length);

            MemoryStream rt = new MemoryStream();
            Tag(rt, 1, 0);
            Varint(rt, 1);
            Tag(rt, 2, 0);
            Varint(rt, 1);
            Tag(rt, 3, 0);
            Varint(rt, 0);
            byte[] rtBytes = rt.ToArray();
            Tag(ms, 6, 2);
            Varint(ms, rtBytes.Length);
            ms.Write(rtBytes, 0, rtBytes.Length);
            return ms.ToArray();
        }
    }

    // 一段识别结果：文字 + 在原图里的位置（坐标系是截图 crop，左上角为原点）
    public sealed class OcrSegment
    {
        public string Text;
        public Rectangle Box;
        public float Angle;
        public int Order;
        public bool LowConfidence;   // 融合不一致/可疑行，AI 识字只修这些
        // 多行被聚成一段时，这里保留每一行原来的框：擦除只擦这些行，
        // 不会把行间的徽章/头像/空白一起抹掉（Box 此时是排版用的整段区域）
        public List<Rectangle> SourceBoxes;
    }

    internal static class Ocr
    {
        private static readonly Regex CjkSpace = new Regex(
            "(?<=[\\u4e00-\\u9fff\\u3000-\\u303f\\uff00-\\uffef0-9])\\s+(?=[\\u4e00-\\u9fff\\u3000-\\u303f\\uff00-\\uffef0-9])");

        private static readonly object ProbeLock = new object();
        private static EngineProbe _probe;

        public static string SystemEngineName = "Windows 内置 OCR";

        // 探测（只查文件/注册表，不启动进程）；结果缓存，避免每次刷新界面都扫盘
        public static EngineProbe Probe()
        {
            lock (ProbeLock)
            {
                if (_probe == null) _probe = WeChatOcrLocator.Detect();
                return _probe;
            }
        }

        // 设置页“重新检测”走这里：强制重扫
        public static EngineProbe ProbeForce()
        {
            lock (ProbeLock)
            {
                _probe = WeChatOcrLocator.Detect();
                return _probe;
            }
        }

        public static EngineProbe CurrentProbe
        {
            get
            {
                lock (ProbeLock)
                {
                    if (_probe == null) _probe = WeChatOcrLocator.Detect();
                    return _probe;
                }
            }
        }

        public static bool WeChatVerified
        {
            get
            {
                EngineProbe p = CurrentProbe;
                return p != null && p.WeChatVerified;
            }
        }

        public static bool WeChatRunning
        {
            get { return WxOcr.IsRunning; }
        }

        public static void Shutdown()
        {
            WxOcr.Stop();
            ShutdownLocal();
        }

        // ---------- 本地 PP-OCR（离线，模型在 "OCR Model" 目录） ----------
        private static readonly object LocalLock = new object();
        private static PpOcr _local;
        private static string _localEngine = string.Empty;

        public static string LocalName(string engine)
        {
            string e = AppConfig.NormalizeEngine(engine);
            if (e == AppConfig.EnginePpMedium) return "PP-OCR 高精";
            return "PP-OCR 极速";
        }

        // 只查文件，不加载模型
        public static bool LocalReady(string engine)
        {
            return PpOcr.FolderReady(AppConfig.ModelTierDir(engine));
        }

        public static void ShutdownLocal()
        {
            lock (LocalLock)
            {
                if (_local != null)
                {
                    try { _local.Dispose(); } catch (Exception) { }
                    _local = null;
                    _localEngine = string.Empty;
                }
            }
        }

        // 取当前配置档位的本地引擎；模型缺失返回 null
        public static PpOcr Local()
        {
            return LocalFor(AppConfig.NormalizeEngine(AppConfig.Engine));
        }

        public static PpOcr LocalFor(string e)
        {
            e = AppConfig.NormalizeEngine(e);
            if (!AppConfig.IsLocalEngine(e)) return null;
            lock (LocalLock)
            {
                if (_local != null && _localEngine == e) return _local;
                if (_local != null)
                {
                    try { _local.Dispose(); } catch (Exception) { }
                    _local = null;
                    _localEngine = string.Empty;
                }
                string dir = AppConfig.ModelTierDir(e);
                if (!PpOcr.FolderReady(dir)) return null;
                try
                {
                    _local = new PpOcr(dir, LocalName(e));
                    _localEngine = e;
                }
                catch (Exception)
                {
                    _local = null;
                    _localEngine = string.Empty;
                }
                return _local;
            }
        }

        // 版面强化：用 PP-OCR 做检测 + 逐块识字，再交给 LayoutBoost 做分栏/徽章/段落切分。
        // 单独缓存一个检测器，避免把用户当前选的识字引擎（可能是高精）顶掉。
        private static PpOcr _layoutPp;
        private static string _layoutPpDir = string.Empty;

        // 启动时后台预热版面检测器：第一次翻译不再等模型加载（约 1~2 秒）
        public static void WarmLayout()
        {
            try
            {
                string tier = AppConfig.EnginePpTiny;
                string dir = AppConfig.ModelTierDir(tier);
                if (!PpOcr.FolderReady(dir)) { tier = AppConfig.EnginePpMedium; dir = AppConfig.ModelTierDir(tier); }
                if (!PpOcr.FolderReady(dir)) return;
                lock (LocalLock)
                {
                    if (_layoutPp != null && _layoutPpDir == dir) return;
                    try { if (_layoutPp != null) _layoutPp.Dispose(); } catch (Exception) { }
                    _layoutPp = new PpOcr(dir, LocalName(tier));
                    _layoutPpDir = dir;
                }
            }
            catch (Exception) { }
        }

        public static List<OcrSegment> LayoutSegments(Bitmap bmp, out List<Rectangle> obstacles, out string log)        {
            obstacles = null;
            log = null;
            if (bmp == null) return null;
            string tier = AppConfig.EnginePpTiny;
            string dir = AppConfig.ModelTierDir(tier);
            if (!PpOcr.FolderReady(dir)) { tier = AppConfig.EnginePpMedium; dir = AppConfig.ModelTierDir(tier); }
            if (!PpOcr.FolderReady(dir)) { log = "PP-OCR 模型缺失，版面强化跳过"; return null; }

            lock (LocalLock)
            {
                if (_layoutPp == null || _layoutPpDir != dir)
                {
                    try { if (_layoutPp != null) _layoutPp.Dispose(); } catch (Exception) { }
                    _layoutPp = null;
                    _layoutPp = new PpOcr(dir, LocalName(tier));
                    _layoutPpDir = dir;
                }
            }
            return LayoutBoost.Recognize(bmp, _layoutPp, out log, out obstacles);
        }

        // 融合：PP 出框/版面，微信出字。两边并行，翻译链路里 PP 本来就要跑，总时间基本不变。
        public static List<OcrSegment> LayoutSegmentsFused(Bitmap bmp, out List<Rectangle> obstacles, out string log)
        {
            obstacles = null;
            log = null;
            if (bmp == null) return null;
            if (!Probe().WeChatUsable)
            {
                log = "微信 OCR 不可用，融合跳过";
                return LayoutSegments(bmp, out obstacles, out log);
            }

            List<OcrSegment> pp = null;
            List<Rectangle> obs = null;
            string plog = null;
            List<OcrSegment> wx = new List<OcrSegment>();
            Bitmap wbmp = (Bitmap)bmp.Clone();
            System.Threading.Tasks.Task t2 = System.Threading.Tasks.Task.Run(delegate()
            {
                using (wbmp)
                {
                    string we, wd;
                    long wms;
                    Recognize(wbmp, out we, out wms, out wd, wx, "wechat");
                }
            });
            // PP 必须在调用线程跑（后台线程上 GDI/版面链路会抛异常）；微信并行跑。
            pp = LayoutSegments(bmp, out obs, out plog);
            try { t2.Wait(); } catch (Exception ex2) { plog = (plog == null ? "" : plog + " | ") + "wx task " + (ex2.InnerException != null ? ex2.InnerException.Message : ex2.Message); }
            obstacles = obs;
            if (pp == null) { log = plog; return null; }

            int changed = 0;
            for (int i = 0; i < pp.Count; i++)
            {
                OcrSegment p = pp[i];
                if (p.Box.Width < 4 || p.Box.Height < 4) continue;
                int best = -1;
                double bestRatio = 0;
                for (int j = 0; j < wx.Count; j++)
                {
                    Rectangle inter = Rectangle.Intersect(p.Box, wx[j].Box);
                    if (inter.Width <= 0 || inter.Height <= 0) continue;
                    double ratio = inter.Width * (double)inter.Height
                        / Math.Max(1.0, Math.Min(p.Box.Width * (double)p.Box.Height, wx[j].Box.Width * (double)wx[j].Box.Height));
                    if (ratio > bestRatio) { bestRatio = ratio; best = j; }
                }
                if (best < 0 || bestRatio < 0.45) continue;
                OcrSegment w = wx[best];
                if (string.IsNullOrEmpty(w.Text)) continue;
                // 微信框明显跨了多个 PP 框时不要整体替换
                if (w.Box.Width > p.Box.Width * 1.45) continue;
                if (w.Text.Length < (p.Text == null ? 0 : p.Text.Length) * 0.55) continue;
                if (!string.Equals(w.Text, p.Text, StringComparison.Ordinal))
                {
                    p.Text = w.Text;
                    p.LowConfidence = true;
                    changed++;
                }
            }
            log = "fusion pp=" + pp.Count + " wx=" + wx.Count + " changed=" + changed
                + (plog == null ? "" : " | " + plog);
            return pp;
        }

        // 实际可用的首选引擎
        public static string EffectiveEngine()
        {
            string want = AppConfig.NormalizeEngine(AppConfig.Engine);
            if (AppConfig.IsLocalEngine(want) && LocalReady(want)) return want;
            if (want == AppConfig.EngineWeChat && CurrentProbe.WeChatUsable) return AppConfig.EngineWeChat;
            if (LocalReady(AppConfig.EnginePpTiny)) return AppConfig.EnginePpTiny;
            return AppConfig.EngineSystem;
        }

        // 实际可用引擎的显示名：本地档位必须显示 PP-OCR 极速/标准/高精，
        // 之前所有非微信的分支都写成“Windows 内置 OCR”，选了 PP-OCR 也这么提示，纯属说错
        public static string EffectiveName()
        {
            string e = EffectiveEngine();
            if (e == AppConfig.EngineWeChat) return "微信 OCR";
            if (AppConfig.IsLocalEngine(e)) return LocalName(e);
            return SystemEngineName;
        }

        public static string Recognize(Bitmap bitmap, out string engine, out long ms, out string detail)
        {
            return Recognize(bitmap, out engine, out ms, out detail, null);
        }

        // segs 非空时顺便把每行的位置填进去（原地显示译文要用）
        public static string Recognize(Bitmap bitmap, out string engine, out long ms, out string detail, List<OcrSegment> segs, string forceEngine = null)
        {
            engine = SystemEngineName;
            ms = 0;
            detail = string.Empty;
            if (segs != null) segs.Clear();

            string png = null;
            try
            {
                string tmpDir = Path.Combine(Path.GetTempPath(), "XuYuan-SnapScan");
                Directory.CreateDirectory(tmpDir);
                png = Path.Combine(tmpDir, "ocr" + DateTime.Now.Ticks.ToString() + ".png");
                bitmap.Save(png, ImageFormat.Png);

                string want = AppConfig.NormalizeEngine(string.IsNullOrEmpty(forceEngine) ? AppConfig.Engine : forceEngine);
                if (AppConfig.IsLocalEngine(want))
                {
                    PpOcr pp = LocalFor(want);
                    if (pp == null)
                    {
                        detail = "本地模型缺失（" + AppConfig.ModelTierDir(want) + "），已改用 " + SystemEngineName;
                    }
                    else
                    {
                        Stopwatch swLocal = Stopwatch.StartNew();
                        try
                        {
                            string text;
                            if (segs != null)
                            {
                                List<PpOcr.Seg> ps = pp.RecognizeSegments(bitmap);
                                StringBuilder sb = new StringBuilder();
                                for (int i = 0; i < ps.Count; i++)
                                {
                                    PpOcr.Seg s = ps[i];
                                    if (sb.Length > 0) sb.Append("\r\n");
                                    sb.Append(s.Text);
                                    OcrSegment seg = new OcrSegment();
                                    seg.Text = s.Text;
                                    seg.Box = new Rectangle(s.X, s.Y, s.W, s.H);
                                    seg.Angle = s.Angle;
                                    seg.Order = s.Order;
                                    segs.Add(seg);
                                }
                                text = sb.ToString();
                            }
                            else
                            {
                                text = pp.Recognize(bitmap);
                            }
                            ms = swLocal.ElapsedMilliseconds;
                            engine = LocalName(want);
                            detail = AppConfig.ModelTierDir(want);
                            if (text == null) return null;
                            string fixedPp = AiTextFix.Polish(segs);
                            return fixedPp ?? NormalizeSystemText(text);
                        }
                        catch (Exception exLocal)
                        {
                            detail = "本地 OCR 失败：" + exLocal.Message + "，已改用 " + SystemEngineName;
                        }
                    }
                }
                if (want == AppConfig.EngineWeChat)
                {
                    EngineProbe probe = CurrentProbe;
                    if (!probe.WeChatUsable)
                    {
                        detail = probe.Problem + "，已改用 " + SystemEngineName;
                    }
                    else
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        string note;
                        if (!WxOcr.Start(probe, out note))
                        {
                            detail = "微信 OCR 启动失败：" + note + "，已改用 " + SystemEngineName;
                        }
                        else
                        {
                            List<Rectangle> wxBoxes = new List<Rectangle>();
                            string text = WxOcr.Recognize(png, 20000, out note, wxBoxes);
                            if (text != null)
                            {
                                ms = sw.ElapsedMilliseconds;
                                engine = "微信 OCR";
                                detail = note;
                                probe.WeChatVerified = true;
                                string wxText = NormalizeWeChatText(text);
                                if (segs != null)
                                {
                                    // 微信自己就给了每行的包围盒，直接拿来落遮罩；
                                    // 只有拿不到坐标时才退回用 PP-OCR 检测框按序号均分。
                                    if (!FillSegmentsFromWx(segs, text, wxBoxes))
                                        BuildSegmentsByDetect(segs, bitmap, text);
                                }
                                if (segs != null) AiTextFix.PolishText(segs, ref wxText);
                                return wxText;
                            }
                            detail = note + "，已改用 " + SystemEngineName;
                        }
                    }
                }

                string sys = RunSystemOcr(png, out ms);
                if (sys == null)
                {
                    if (detail.Length == 0) detail = "Windows 内置 OCR 不可用（需 Win10 以上且装有中文语言包）";
                    return null;
                }
                engine = SystemEngineName;
                if (segs != null) BuildSegmentsByDetect(segs, bitmap, sys);
                string sysText = NormalizeSystemText(sys);
                if (segs != null) AiTextFix.PolishText(segs, ref sysText);
                return sysText;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return null;
            }
            finally
            {
                if (png != null)
                {
                    try { File.Delete(png); } catch (Exception) { }
                    try { File.Delete(png + ".ocr.txt"); } catch (Exception) { }
                }
            }
        }

        // 微信 OCR / 系统 OCR 只给文字、不给坐标，译文要借 PP-OCR 的检测框逐行对齐。
        // 当前引擎若是微信，PP-OCR 可能一个都没加载过，这里按 高精→极速 的顺序临时拉一个检测器；
        // 一个都没有就返回 null，让调用方走整块兜底，翻译照样能出。
        private static PpOcr DetectorForSegments()
        {
            lock (LocalLock)
            {
                if (_local != null && AppConfig.IsLocalEngine(_localEngine)) return _local;
            }
            string[] tiers = new string[] { AppConfig.EnginePpMedium, AppConfig.EnginePpTiny };
            for (int i = 0; i < tiers.Length; i++)
            {
                if (!LocalReady(tiers[i])) continue;
                PpOcr p = LocalFor(tiers[i]);
                if (p != null) return p;
            }
            return null;
        }

        // 微信 OCR / 系统 OCR 只给文字，没有坐标：借已经加载的 PP-OCR 检测框按行对齐
        // 微信 OCR 的行自带包围盒（由词级坐标按行合并而来），直接建成 segs。
        // 行数与框数对不上就返回 false，让调用方退回检测框均分。
        private static bool FillSegmentsFromWx(List<OcrSegment> segs, string text, List<Rectangle> boxes)
        {
            if (boxes == null || boxes.Count == 0) return false;
            string[] ls = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            List<string> okText = new List<string>();
            List<Rectangle> okBox = new List<Rectangle>();
            for (int i = 0; i < ls.Length; i++)
            {
                string t = ls[i].TrimEnd();
                if (t.Length == 0) continue;
                if (i >= boxes.Count) return false;
                Rectangle b = boxes[i];
                if (b.Width <= 0 || b.Height <= 0) return false;
                okText.Add(t);
                okBox.Add(b);
            }
            if (okText.Count == 0 || okText.Count != boxes.Count) return false;

            segs.Clear();
            for (int i = 0; i < okText.Count; i++)
            {
                OcrSegment s = new OcrSegment();
                s.Text = okText[i];
                s.Box = okBox[i];
                s.Angle = 0f;
                s.Order = i;
                segs.Add(s);
            }
            return true;
        }

        private static void BuildSegmentsByDetect(List<OcrSegment> segs, Bitmap bmp, string text)
        {
            if (segs == null || bmp == null) return;
            segs.Clear();
            List<string> lines = new List<string>();
            foreach (string raw in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                string t = CjkSpace.Replace(raw.Trim(), string.Empty);
                if (t.Length > 0) lines.Add(t);
            }
            List<OcrSegment> boxes = new List<OcrSegment>();
            PpOcr pp = DetectorForSegments();
            if (pp != null)
            {
                List<PpOcr.Box> bs = pp.DetectPublic(bmp);
                int cur = int.MinValue;
                OcrSegment seg = null;
                for (int i = 0; i < bs.Count; i++)
                {
                    PpOcr.Box b = bs[i];
                    if (seg == null || b.Order != cur)
                    {
                        if (seg != null) boxes.Add(seg);
                        seg = new OcrSegment();
                        seg.Order = b.Order;
                        seg.Box = new Rectangle(b.X, b.Y, b.W, b.H);
                        seg.Angle = b.Angle;
                        cur = b.Order;
                        continue;
                    }
                    Rectangle u = Rectangle.Union(seg.Box, new Rectangle(b.X, b.Y, b.W, b.H));
                    seg.Box = u;
                    seg.Angle = seg.Angle == 0f ? b.Angle : seg.Angle;
                }
                if (seg != null) boxes.Add(seg);
            }

            if (boxes.Count == 0)
            {
                // 连检测框都没有（只装了微信 OCR、机器上没有 PP-OCR 模型）也别让译文落空：
                // 整块当一行，译文铺在选区上
                OcrSegment whole = new OcrSegment();
                whole.Box = new Rectangle(0, 0, bmp.Width, bmp.Height);
                whole.Angle = 0f;
                whole.Order = 0;
                StringBuilder all = new StringBuilder();
                foreach (string l in lines)
                {
                    if (all.Length > 0) all.Append(' ');
                    all.Append(l);
                }
                whole.Text = all.ToString();
                if (whole.Text.Length > 0) segs.Add(whole);
                return;
            }

            // 按行数均分到检测框：以前是从第 i 行一直拼到末尾，导致第一个框吃掉所有行
            int nBoxes = boxes.Count;
            int nLines = lines.Count;
            // 行数对不上（微信把一整条导航栏拆成 6 行、检测只给 1 行）时，
            // 按序号均分会让后面所有行整体错位，译文就糊到别的行上。
            // 这种情况直接改用 PP-OCR 自己的「框+字」结果，几何位置才是对的。
            if (pp != null && (nLines != nBoxes) && nLines > 0)
            {
                try
                {
                    List<PpOcr.Seg> ps = pp.RecognizeSegments(bmp);
                    if (ps != null && ps.Count >= 2)
                    {
                        segs.Clear();
                        for (int i = 0; i < ps.Count; i++)
                        {
                            PpOcr.Seg s = ps[i];
                            if (s.Text == null || s.Text.Trim().Length == 0) continue;
                            OcrSegment seg = new OcrSegment();
                            seg.Text = s.Text;
                            seg.Box = new Rectangle(s.X, s.Y, s.W, s.H);
                            seg.Angle = s.Angle;
                            seg.Order = s.Order;
                            segs.Add(seg);
                        }
                        if (segs.Count > 0) return;
                    }
                }
                catch (Exception) { }
            }
            for (int i = 0; i < nBoxes; i++)
            {
                int from = (int)((long)i * nLines / nBoxes);
                int to = (int)((long)(i + 1) * nLines / nBoxes);   // 不含
                if (to <= from)
                {
                    if (i < nLines) { from = i; to = i + 1; }
                    else continue;
                }
                if (from >= nLines) continue;
                if (to > nLines) to = nLines;
                StringBuilder sb = new StringBuilder();
                for (int k = from; k < to; k++)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(lines[k]);
                }
                boxes[i].Text = sb.ToString();
            }
            foreach (OcrSegment s in boxes)
            {
                if (s.Text.Length > 0) segs.Add(s);
            }
        }

        // 微信 OCR 一行行给回来，去掉行尾空白即可
        private static string NormalizeWeChatText(string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            StringBuilder sb = new StringBuilder();
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (sb.Length > 0) sb.Append("\r\n");
                sb.Append(line);
            }
            return sb.ToString();
        }

        private static string RunSystemOcr(string pngPath, out long ms)
        {
            ms = 0;
            string outFile = pngPath + ".ocr.txt";
            try { if (File.Exists(outFile)) File.Delete(outFile); }
            catch (Exception) { }

            string script = BuildSystemOcrScript(pngPath, outFile);
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe");
            if (!File.Exists(ps)) return null;

            Stopwatch sw = Stopwatch.StartNew();
            Process proc = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ps);
                psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                proc = Process.Start(psi);
                if (proc == null) return null;
                proc.WaitForExit(30000);
                ms = sw.ElapsedMilliseconds;
                if (!File.Exists(outFile)) return null;
                return File.ReadAllText(outFile, Encoding.UTF8);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                KillProcess(proc);
            }
        }

        private static void KillProcess(Process proc)
        {
            if (proc == null) return;
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill();
                    proc.WaitForExit(300);
                }
            }
            catch (Exception)
            {
            }
        }

        private static string BuildSystemOcrScript(string pngPath, string outPath)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Add-Type -AssemblyName System.Runtime.WindowsRuntime;\r\n");
            sb.Append("$null=[Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime];\r\n");
            sb.Append("$null=[Windows.Storage.Streams.IRandomAccessStream,Windows.Storage,ContentType=WindowsRuntime];\r\n");
            sb.Append("$null=[Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime];\r\n");
            sb.Append("$null=[Windows.Media.Ocr.OcrEngine,Windows.Media,ContentType=WindowsRuntime];\r\n");
            sb.Append("$g=([System.WindowsRuntimeSystemExtensions].GetMethods()|Where-Object{$_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'})[0];\r\n");
            sb.Append("function Aw($t,$r){$k=$g.MakeGenericMethod($r);$nt=$k.Invoke($null,@($t));$nt.Wait(-1)|Out-Null;$nt.Result};\r\n");
            sb.Append("$p='").Append(pngPath).Append("';\r\n");
            sb.Append("$o='").Append(outPath).Append("';\r\n");
            sb.Append("$f=Aw ([Windows.Storage.StorageFile]::GetFileFromPathAsync($p)) ([Windows.Storage.StorageFile]);\r\n");
            sb.Append("$s=Aw ($f.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream]);\r\n");
            sb.Append("$d=Aw ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($s)) ([Windows.Graphics.Imaging.BitmapDecoder]);\r\n");
            sb.Append("$b=Aw ($d.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap]);\r\n");
            sb.Append("$e=[Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages();\r\n");
            sb.Append("$r=Aw ($e.RecognizeAsync($b)) ([Windows.Media.Ocr.OcrResult]);\r\n");
            sb.Append("[IO.File]::WriteAllText($o, (($r.Lines | ForEach-Object { $_.Text }) -join [Environment]::NewLine), (New-Object System.Text.UTF8Encoding($false)));\r\n");
            return sb.ToString();
        }

        // 系统 OCR 会把汉字之间塞空格，粘出来很难看，这里清掉
        private static string NormalizeSystemText(string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            StringBuilder sb = new StringBuilder();
            foreach (string raw in lines)
            {
                string line = CjkSpace.Replace(raw.Trim(), string.Empty);
                if (line.Length == 0) continue;
                if (sb.Length > 0) sb.Append("\r\n");
                sb.Append(line);
            }
            return sb.ToString();
        }
    }

    // 单个显示器的冻结画面
    internal sealed class MonitorShot : IDisposable
    {
        public Screen Screen;
        public Rectangle Bounds;
        public Bitmap Image;

        private Bitmap _dim;

        public Bitmap Dim
        {
            get
            {
                if (_dim == null && Image != null) _dim = BuildDim(Image);
                return _dim;
            }
        }

        private static Bitmap BuildDim(Bitmap src)
        {
            Bitmap dim = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppRgb);
            using (Graphics g = Graphics.FromImage(dim))
            {
                ColorMatrix cm = new ColorMatrix(new float[][]
                {
                    new float[] { 0.42f, 0f, 0f, 0f, 0f },
                    new float[] { 0f, 0.44f, 0f, 0f, 0f },
                    new float[] { 0f, 0f, 0.50f, 0f, 0f },
                    new float[] { 0f, 0f, 0f, 1f, 0f },
                    new float[] { 0f, 0f, 0f, 0f, 1f }
                });
                ImageAttributes ia = new ImageAttributes();
                ia.SetColorMatrix(cm);
                try
                {
                    g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height),
                        0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
                finally
                {
                    ia.Dispose();
                }
            }
            return dim;
        }

        public void Dispose()
        {
            if (_dim != null) { _dim.Dispose(); _dim = null; }
            if (Image != null) { Image.Dispose(); Image = null; }
        }
    }

    // 一次截图会话：每块屏一个遮罩窗口，选区统一用“虚拟屏幕坐标”共享
    internal sealed class CaptureSession
    {
        private readonly List<MonitorShot> _shots = new List<MonitorShot>();
        private readonly List<OverlayForm> _forms = new List<OverlayForm>();

        private const int ESC_HOTKEY_ID = 0x5A53;
        private IntPtr _escHotkeyHwnd = IntPtr.Zero;

        public Rectangle Selection = Rectangle.Empty;
        public bool Done;
        public bool Confirmed;
        public Bitmap Crop;

        // 自由圈选：多边形顶点（虚拟屏坐标），Freehand=true 时走这条路
        public bool Freehand;
        public List<Point> Polygon = new List<Point>();
        public bool HasPolygon;

        public void Run()
        {
            try
            {
                Dbg("run start screens=" + Screen.AllScreens.Length);
                List<MonitorShot> blank = new List<MonitorShot>();

                foreach (Screen sc in Screen.AllScreens)
                {
                    Rectangle bounds = sc.Bounds;
                    if (bounds.Width < 32 || bounds.Height < 32) continue;

                    MonitorShot shot = new MonitorShot();
                    shot.Screen = sc;
                    shot.Bounds = bounds;
                    try
                    {
                        shot.Image = Native.Capture(bounds);
                    }
                    catch (Exception)
                    {
                        shot.Image = null;
                    }
                    if (shot.Image == null)
                    {
                        shot.Dispose();
                        continue;
                    }

                    // 调试：SNAPSCAN_DEBUG_CAP=1 时把抓到的每块屏存成 png，方便排查“抓不到画面”
                    if (Environment.GetEnvironmentVariable("SNAPSCAN_DEBUG_CAP") == "1")
                    {
                        try
                        {
                            string dbg = Path.Combine(Path.GetTempPath(), "snapcap_" + bounds.Width + "x" + bounds.Height + "_" + bounds.X + "_" + bounds.Y + ".png");
                            shot.Image.Save(dbg, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        catch (Exception) { }
                    }

                    // 纯黑的显示器（虚拟/已断开的幽灵屏）不弹出遮罩，避免屏幕上出现莫名的黑窗口
                    if (IsNearlyBlank(shot.Image))
                    {
                        blank.Add(shot);
                        continue;
                    }
                    _shots.Add(shot);
                }

                // 万一只剩黑屏（例如锁屏、黑底画面），仍然把遮罩显示出来，保证能框选
                if (_shots.Count == 0 && blank.Count > 0)
                {
                    _shots.AddRange(blank);
                    blank.Clear();
                }
                foreach (MonitorShot shot in blank) shot.Dispose();
                blank.Clear();

                if (_shots.Count == 0)
                {
                    Done = true;
                    return;
                }

                foreach (MonitorShot shot in _shots)
                {
                    OverlayForm form = new OverlayForm(this, shot);
                    _forms.Add(form);
                    form.Show();
                }
                Dbg("forms shown=" + _forms.Count);

                OverlayForm focus = PickFocusForm();
                if (focus != null)
                {
                    // 遮罩必须真的拿到键盘焦点：直接从隐藏状态弹出来时，SetForegroundWindow
                    // 常被前台锁拦掉，Esc 就被别的程序吃走了（表现为“按 Esc 没反应”）。
                    bool ok = Native.ForceForeground(focus.Handle);
                    for (int i = 0; i < 3 && !ok; i++)
                    {
                        Thread.Sleep(30);
                        ok = Native.ForceForeground(focus.Handle);
                    }
                    Dbg("focus brought ok=" + ok);

                    // 焦点不保险，就把 Esc 临时注册成系统热键：走 WM_HOTKEY → WndProc 取消，
                    // 无论谁在前台、遮罩有没有焦点都能取消。
                    if (Native.RegisterHotKey(focus.Handle, ESC_HOTKEY_ID, 0, 0x1B))
                    {
                        _escHotkeyHwnd = focus.Handle;
                        Dbg("esc hotkey registered");
                    }
                }

                int started = Environment.TickCount;
                while (!Done)
                {
                    Application.DoEvents();
                    Thread.Sleep(3);
                    // 兜底：遮罩挂住超过 2 分钟就自动退出，避免万一卡死只能杀进程
                    if (unchecked(Environment.TickCount - started) > 120000) Cancel();
                }
            }
            finally
            {
                if (_escHotkeyHwnd != IntPtr.Zero)
                {
                    try { Native.UnregisterHotKey(_escHotkeyHwnd, ESC_HOTKEY_ID); }
                    catch (Exception) { }
                    _escHotkeyHwnd = IntPtr.Zero;
                }

                foreach (OverlayForm form in _forms)
                {
                    if (!form.IsDisposed)
                    {
                        try { form.Close(); }
                        catch (Exception) { }
                        form.Dispose();
                    }
                }
                _forms.Clear();

                if (Confirmed) Crop = HasPolygon ? MakePolygonCrop(Polygon) : MakeCrop(Selection);

                foreach (MonitorShot shot in _shots) shot.Dispose();
                _shots.Clear();
            }
        }

        // 采样判断这块屏是不是几乎全黑（幽灵显示器抓出来是纯黑）
        private static bool IsNearlyBlank(Bitmap bmp)
        {
            int stepX = Math.Max(1, bmp.Width / 60);
            int stepY = Math.Max(1, bmp.Height / 40);
            int samples = 0;
            int bright = 0;
            for (int y = 0; y < bmp.Height; y += stepY)
            {
                for (int x = 0; x < bmp.Width; x += stepX)
                {
                    samples++;
                    Color c = bmp.GetPixel(x, y);
                    if (c.R + c.G + c.B > 40) bright++;
                }
            }
            return samples > 0 && (bright * 100 / samples) < 2;
        }

        private OverlayForm PickFocusForm()
        {
            Point cursor = Cursor.Position;
            foreach (OverlayForm form in _forms)
            {
                if (form.Shot.Bounds.Contains(cursor)) return form;
            }
            return _forms.Count > 0 ? _forms[0] : null;
        }

        public void InvalidateAll()
        {
            foreach (OverlayForm form in _forms)
            {
                if (!form.IsDisposed && form.Visible) form.Invalidate();
            }
        }

        public void Confirm(Rectangle selection)
        {
            Selection = selection;
            HasPolygon = false;
            Confirmed = true;
            Done = true;
        }

        // 自由圈选确认：顶点不足 3 个或面积太小当取消处理
        public void ConfirmPolygon(List<Point> pts)
        {
            if (pts == null || pts.Count < 3)
            {
                Cancel();
                return;
            }
            Rectangle bbox = BoundsOf(pts);
            if (bbox.Width < 8 || bbox.Height < 8)
            {
                Cancel();
                return;
            }
            Polygon = new List<Point>(pts);
            Selection = bbox;
            HasPolygon = true;
            Confirmed = true;
            Done = true;
        }

        public static Rectangle BoundsOf(List<Point> pts)        {
            if (pts == null || pts.Count == 0) return Rectangle.Empty;
            int minX = pts[0].X, minY = pts[0].Y, maxX = minX, maxY = minY;
            for (int i = 1; i < pts.Count; i++)
            {
                Point p = pts[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            return new Rectangle(minX, minY, maxX - minX, maxY - minY);
        }

        // 仅调试用：SNAPSCAN_DEBUG_CAP=1 时记流水账到 %TEMP%\snapcap.log
        internal static void Dbg(string msg)
        {
            if (Environment.GetEnvironmentVariable("SNAPSCAN_DEBUG_CAP") != "1") return;
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "snapcap.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n", new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        public void Cancel()
        {
            Confirmed = false;
            Done = true;
        }

        private Bitmap MakeCrop(Rectangle sel)
        {
            // 优先从包含选区左上角的那块屏里裁剪
            MonitorShot first = null;
            foreach (MonitorShot shot in _shots)
            {
                if (shot.Bounds.Contains(sel.X, sel.Y))
                {
                    first = shot;
                    break;
                }
            }

            List<MonitorShot> order = new List<MonitorShot>();
            if (first != null) order.Add(first);
            foreach (MonitorShot shot in _shots)
            {
                if (shot != first) order.Add(shot);
            }

            foreach (MonitorShot shot in order)
            {
                Rectangle local = new Rectangle(sel.X - shot.Bounds.X, sel.Y - shot.Bounds.Y, sel.Width, sel.Height);
                Rectangle client = new Rectangle(0, 0, shot.Bounds.Width, shot.Bounds.Height);
                Rectangle valid = Rectangle.Intersect(local, client);
                if (valid.Width < 6 || valid.Height < 6) continue;
                try
                {
                    return shot.Image.Clone(valid, PixelFormat.Format32bppRgb);
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

        // 自由圈选裁剪：只保留多边形内部的像素，其余全透明（背景透明，文字仍是原像素）
        private Bitmap MakePolygonCrop(List<Point> pts)
        {
            Rectangle bbox = BoundsOf(pts);
            if (bbox.Width < 8 || bbox.Height < 8) return null;

            Point center = new Point(bbox.X + bbox.Width / 2, bbox.Y + bbox.Height / 2);
            MonitorShot host = null;
            foreach (MonitorShot shot in _shots)
            {
                if (shot.Bounds.Contains(center)) { host = shot; break; }
            }
            if (host == null && _shots.Count > 0) host = _shots[0];
            if (host == null || host.Image == null) return null;

            Bitmap mask = new Bitmap(bbox.Width, bbox.Height, PixelFormat.Format32bppArgb);
            using (Graphics mg = Graphics.FromImage(mask))
            {
                mg.Clear(Color.Transparent);
                mg.SmoothingMode = SmoothingMode.None;
                mg.PixelOffsetMode = PixelOffsetMode.Half;
                Point[] local = new Point[pts.Count];
                for (int i = 0; i < pts.Count; i++) local[i] = new Point(pts[i].X - bbox.X, pts[i].Y - bbox.Y);
                using (SolidBrush white = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                {
                    mg.FillPolygon(white, local);
                }
                // 描一圈边，把锯齿收干净，避免把字的边缘吃掉
                using (Pen pen = new Pen(Color.FromArgb(255, 255, 255, 255), 2F))
                {
                    mg.DrawPolygon(pen, local);
                }
            }

            Bitmap result = new Bitmap(bbox.Width, bbox.Height, PixelFormat.Format32bppArgb);
            using (Graphics rg = Graphics.FromImage(result))
            {
                rg.Clear(Color.Transparent);
                rg.CompositingMode = CompositingMode.SourceCopy;
                rg.PixelOffsetMode = PixelOffsetMode.Half;
                rg.InterpolationMode = InterpolationMode.NearestNeighbor;

                // 多边形可能画到屏幕外（或跨屏），GDI+ 遇到越界的源矩形会直接抛“参数无效”，
                // 所以这里手工把源矩形夹进图像范围，再换算成目标矩形。
                int sx = bbox.X - host.Bounds.X;
                int sy = bbox.Y - host.Bounds.Y;
                int dx = 0, dy = 0;
                int w = bbox.Width, h = bbox.Height;

                if (sx < 0) { dx = -sx; w += sx; sx = 0; }
                if (sy < 0) { dy = -sy; h += sy; sy = 0; }
                if (sx + w > host.Image.Width) w = host.Image.Width - sx;
                if (sy + h > host.Image.Height) h = host.Image.Height - sy;

                if (w > 0 && h > 0)
                {
                    Rectangle dest = new Rectangle(dx, dy, w, h);
                    Rectangle src = new Rectangle(sx, sy, w, h);
                    rg.DrawImage(host.Image, dest, src, GraphicsUnit.Pixel);
                }
            }

            ApplyMask(result, mask);
            mask.Dispose();
            return result;
        }

        // 用蒙版把多边形外的像素清零（透明度）
        private static void ApplyMask(Bitmap target, Bitmap mask)
        {
            if (target.Width != mask.Width || target.Height != mask.Height) return;
            int w = target.Width;
            int h = target.Height;
            BitmapData td = target.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            BitmapData md = mask.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] trow = new byte[td.Stride];
                byte[] mrow = new byte[md.Stride];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(td.Scan0, td.Stride * y), trow, 0, trow.Length);
                    Marshal.Copy(IntPtr.Add(md.Scan0, md.Stride * y), mrow, 0, mrow.Length);
                    for (int x = 0; x < w; x++)
                    {
                        byte m = mrow[x * 4 + 3];
                        int o = x * 4;
                        if (m < 128)
                        {
                            trow[o] = 0;
                            trow[o + 1] = 0;
                            trow[o + 2] = 0;
                            trow[o + 3] = 0;
                        }
                        else
                        {
                            trow[o + 3] = 255;
                        }
                    }
                    Marshal.Copy(trow, 0, IntPtr.Add(td.Scan0, td.Stride * y), trow.Length);
                }
            }
            finally
            {
                target.UnlockBits(td);
                mask.UnlockBits(md);
            }
        }
    }

    internal sealed class OverlayForm : Form
    {
        private const int MIN_SIZE = 6;

        private readonly CaptureSession _session;
        private readonly MonitorShot _shot;
        private readonly Font _hintFont;
        private readonly Font _sizeFont;

        private Point _anchor;
        private bool _down;
        private readonly List<Point> _pts = new List<Point>();

        public MonitorShot Shot { get { return _shot; } }

        private const int WM_HOTKEY = 0x0312;

        public OverlayForm(CaptureSession session, MonitorShot shot)
        {
            _session = session;
            _shot = shot;

            _hintFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            _sizeFont = new Font("Consolas", 8.5F, FontStyle.Regular, GraphicsUnit.Point);

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = shot.Bounds;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            KeyPreview = true;
            BackColor = Color.Black;
            SetStyle(ControlStyles.Opaque, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            // 1:1 画出本屏的暗化画面，绝不缩放，所以不会出现“画面被放大”
            Bitmap dim = _shot.Dim;
            if (dim != null)
            {
                g.DrawImageUnscaled(dim, 0, 0);
            }
            else
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 60, 66)))
                {
                    g.FillRectangle(b, 0, 0, ClientSize.Width, ClientSize.Height);
                }
            }

            Rectangle sel = _session.Selection;
            if (_session.Freehand)
            {
                DrawFreehand(g);
            }
            else
            {
                if (sel.Width > 2 && sel.Height > 2)
                {
                    Rectangle frame = new Rectangle(sel.X - _shot.Bounds.X, sel.Y - _shot.Bounds.Y, sel.Width, sel.Height);
                    Rectangle local = Rectangle.Intersect(frame, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));

                    if (local.Width > 0 && local.Height > 0)
                    {
                        // 选区内用原图 1:1 重画一遍（不缩放）
                        g.SetClip(local, CombineMode.Replace);
                        if (_shot.Image != null) g.DrawImageUnscaled(_shot.Image, 0, 0);
                        g.ResetClip();
                    }

                    using (Pen pen = new Pen(Color.FromArgb(255, 0, 132, 255), 2F))
                    {
                        g.DrawRectangle(pen, frame);
                    }

                    DrawSizeBadge(g, sel, local);
                }
            }

            if (_shot.Bounds.Contains(Cursor.Position))
            {
                DrawHint(g, sel);
            }
        }

        // 自由圈选：沿鼠标轨迹画出多边形，内部露出原图，外部保持暗化
        private void DrawFreehand(Graphics g)
        {
            // GDI+ 的 GraphicsPath.AddPolygon 至少需要 3 个点，少于 3 点会抛“参数无效”，
            // 这里必须先拦掉（刚按下鼠标、只移动了一次时就只有 2 点）。
            if (_pts.Count < 3) return;

            GraphicsPath path = new GraphicsPath();
            try
            {
                Point[] local = new Point[_pts.Count];
                for (int i = 0; i < _pts.Count; i++)
                {
                    local[i] = new Point(_pts[i].X - _shot.Bounds.X, _pts[i].Y - _shot.Bounds.Y);
                }
                path.AddPolygon(local);

                if (_shot.Image != null)
                {
                    g.SetClip(path, CombineMode.Replace);
                    g.DrawImageUnscaled(_shot.Image, 0, 0);
                    g.ResetClip();
                }
                using (Pen pen = new Pen(Color.FromArgb(255, 0, 132, 255), 2F))
                {
                    g.DrawPath(pen, path);
                }
                Rectangle vb = CaptureSession.BoundsOf(_pts);
                Rectangle lb = Rectangle.Intersect(
                    new Rectangle(vb.X - _shot.Bounds.X, vb.Y - _shot.Bounds.Y, vb.Width, vb.Height),
                    new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
                if (lb.Width > 0 && lb.Height > 0) DrawSizeBadge(g, vb, lb);
            }
            catch (Exception)
            {
                // 圈选过程中的绘制异常绝不能冒到 WinForms 的 Paint 处理里，否则整窗口报错退出
            }
            finally
            {
                path.Dispose();
            }
        }

        private void DrawSizeBadge(Graphics g, Rectangle sel, Rectangle local)
        {
            string size = sel.Width.ToString() + " × " + sel.Height.ToString();
            SizeF s = g.MeasureString(size, _sizeFont);
            int bw = (int)s.Width + 14;
            int bh = (int)s.Height + 8;
            int bx = local.Right + 8;
            int by = local.Bottom + 8;
            if (bx + bw > ClientSize.Width - 4) bx = local.Right - bw - 8;
            if (by + bh > ClientSize.Height - 4) by = local.Bottom - bh - 8;
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(225, 18, 20, 26)))
            {
                g.FillRectangle(bg, bx, by, bw, bh);
            }
            using (SolidBrush fg = new SolidBrush(Color.White))
            {
                g.DrawString(size, _sizeFont, fg, bx + 7, by + 4);
            }
        }

        private void DrawHint(Graphics g, Rectangle sel)
        {
            string hint;
            if (_session.Freehand)
            {
                hint = _down
                    ? "松开鼠标，按你画的轮廓裁剪    Esc 取消"
                    : "按住鼠标自由圈出要识别的范围    Esc / 右键 取消";
            }
            else
            {
                hint = (sel.Width > MIN_SIZE)
                    ? "松开鼠标开始识别    Esc 取消"
                    : "拖动鼠标框选要识别的内容    Esc / 右键 取消";
            }
            SizeF hs = g.MeasureString(hint, _hintFont);
            int hw = (int)hs.Width + 34;
            int hh = (int)hs.Height + 16;
            int hx = (ClientSize.Width - hw) / 2;
            int hy = 26;
            if (hx < 4)
            {
                hx = 4;
                hw = ClientSize.Width - 8;
                hint = "拖动框选    Esc 取消";
            }
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(210, 16, 18, 24)))
            {
                g.FillRectangle(bg, hx, hy, hw, hh);
            }
            using (SolidBrush fg = new SolidBrush(Color.FromArgb(245, 245, 245)))
            {
                g.DrawString(hint, _hintFont, fg, (float)(hx + 17), (float)(hy + 8));
            }
        }

        private Point ToVirtual(Point p)
        {
            return new Point(_shot.Bounds.X + p.X, _shot.Bounds.Y + p.Y);
        }

        private static Rectangle MakeRect(Point a, Point b)
        {
            int x = Math.Min(a.X, b.X);
            int y = Math.Min(a.Y, b.Y);
            int w = Math.Abs(a.X - b.X);
            int h = Math.Abs(a.Y - b.Y);
            return new Rectangle(x, y, w, h);
        }

        protected override void WndProc(ref Message m)
        {
            // 框选期间 Esc 是注册过的系统热键，走 WM_HOTKEY 进来，不依赖窗口有没有键盘焦点
            if (m.Msg == WM_HOTKEY)
            {
                _session.Cancel();
                return;
            }
            base.WndProc(ref m);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                _session.Cancel();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                _session.Cancel();
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                _down = true;
                _anchor = ToVirtual(e.Location);
                if (_session.Freehand)
                {
                    _pts.Clear();
                    _pts.Add(_anchor);
                    _session.Polygon = new List<Point>(_pts);
                }
                _session.Selection = new Rectangle(_anchor.X, _anchor.Y, 0, 0);
                _session.InvalidateAll();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_down)
            {
                Point p = ToVirtual(e.Location);
                if (_session.Freehand)
                {
                    // 点间至少留 3px，既能画出平滑轮廓，也不会攒出几万个点
                    if (_pts.Count == 0 || Distance(_pts[_pts.Count - 1], p) >= 3)
                    {
                        _pts.Add(p);
                        _session.Polygon = new List<Point>(_pts);
                        _session.Selection = CaptureSession.BoundsOf(_pts);
                    }
                }
                else
                {
                    _session.Selection = MakeRect(_anchor, p);
                }
                _session.InvalidateAll();
            }
            else
            {
                // 提示条跟随鼠标所在的那块屏
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        private static int Distance(Point a, Point b)
        {
            int dx = a.X - b.X;
            int dy = a.Y - b.Y;
            return (int)Math.Sqrt(dx * dx + dy * dy);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!_down)
            {
                base.OnMouseUp(e);
                return;
            }
            _down = false;

            if (_session.Freehand)
            {
                _session.ConfirmPolygon(_pts);
                base.OnMouseUp(e);
                return;
            }

            Rectangle sel = _session.Selection;
            if (sel.Width >= MIN_SIZE && sel.Height >= MIN_SIZE) _session.Confirm(sel);
            else _session.Cancel();

            base.OnMouseUp(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_session.Done) _session.Cancel();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hintFont.Dispose();
                _sizeFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // 全屏置顶悬浮提示：不在主界面里，而是浮在桌面右下角，不抢焦点、不进 Alt+Tab
    internal sealed class ToastForm : Form
    {
        internal static ToastForm Current;   // 供设置里关掉提示时立刻收起
        private const int STATE_IDLE = 0;
        private const int STATE_BUSY = 1;
        private const int STATE_DONE = 2;

        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;   // 提示条只用于看，不让它挡住鼠标点击

        private const int PAD_X = 18;
        private const int ICON_SIZE = 20;
        private const int GAP = 12;
        private const int BOX_H = 54;
        private const int SUB_H = 18;        // 有明细行时额外的高度（96 dpi 逻辑值）

        private const int TICK_MS = 30;      // 30ms 一帧，比原来的 60ms 顺滑
        private const int SLIDE_PX = 10;     // 进场时从下方 10px 滑上来
        private const float TOAST_OPACITY = 0.85f;   // 整体再透一点

        private readonly System.Windows.Forms.Timer _timer;
        private float _angle;
        private int _state;
        private string _text = string.Empty;
        private int _textW;                  // 文字区最大宽度，绘制时用来居中
        private string _sub;                 // 第二行明细（AI 过程 / 战果），null 表示只有一行
        private Font _title;                 // 主行字体，稍粗一点
        private Font _small;                 // 明细行字体，缓存起来别每帧 new
        private bool _ok = true;
        private int _holdMs;
        private int _anim;                   // 0 静止 / 1 进场 / 2 出场
        private float _animT;
        private int _restTop;                // 静止时的最终 Top
        private Bitmap _blurBg;              // 截屏后做的高斯/盒式模糊背景

        // 布局常量都是按 96 dpi 写的，而字体会按系统缩放渲染（150% 下 9.5pt 会画成 1.5 倍高）。
        // 不换算的话，框子还是 96 dpi 的尺寸，字却变大了，尾巴就被裁掉。统一按 DPI 比例缩放。
        private float _scale = 1f;

        private int S(int v)
        {
            return (int)Math.Round(v * _scale);
        }

        public ToastForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(30, 36, 48);
            DoubleBuffered = true;
            Opacity = TOAST_OPACITY;
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
            _title = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = TICK_MS;
            _timer.Tick += OnTick;
            Current = this;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public void ShowBusy(string text)
        {
            Present(text, null, STATE_BUSY, true, 0);
        }

        // 带第二行明细：主行说正在干什么，明细行说 AI 这一步在做什么
        public void ShowBusy(string text, string sub)
        {
            Present(text, sub, STATE_BUSY, true, 0);
        }

        public void ShowDone(string text, bool ok)
        {
            Present(text, null, STATE_DONE, ok, 1700);
        }

        public void ShowDone(string text, string sub, bool ok)
        {
            Present(text, sub, STATE_DONE, ok, 1700);
        }

        private Font SmallFont()
        {
            if (_small == null)
                _small = new Font(_title.FontFamily, Math.Max(7.5f, _title.SizeInPoints - 1.5f),
                    FontStyle.Regular, GraphicsUnit.Point);
            return _small;
        }

        // 统一入口：第一次出现走「淡入 + 上滑」；已经在屏幕上（比如 识别中 → 识别完成）就地换内容，不重播动画
        private void Present(string text, string sub, int state, bool ok, int holdMs)
        {
            if (!AppConfig.ShowToast) { HideToast(); return; }
            bool wasVisible = Visible;
            SetText(text, sub);
            _state = state;
            _ok = ok;
            _holdMs = holdMs;
            _angle = 0f;
            PlaceToast();

            if (!wasVisible)
            {
                CaptureBackdrop();
                Opacity = 0.0;
                Top = _restTop + SLIDE_PX;
                Show();
                _anim = 1;
                _animT = 0f;
            }
            else
            {
                Opacity = TOAST_OPACITY;
                Top = _restTop;
                _anim = 0;
                _animT = 0f;
            }

            BringToFront();
            _timer.Start();
            Invalidate();
        }

        // 把自己画进 PNG：自检时不受别的窗口遮挡影响
        public void SaveShot(string path)
        {
            using (Bitmap b = new Bitmap(Width, Height))
            {
                DrawToBitmap(b, new Rectangle(0, 0, Width, Height));
                b.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        public void HideToast()
        {
            _state = STATE_IDLE;
            _anim = 0;
            _animT = 0f;
            _holdMs = 0;
            _timer.Stop();
            Opacity = TOAST_OPACITY;                  // 复位，下次 Present 再从 0 淡入
            if (_blurBg != null) { _blurBg.Dispose(); _blurBg = null; }
            if (Visible) Hide();
        }

        private void SetText(string text, string sub)
        {
            _text = text;
            _sub = string.IsNullOrEmpty(sub) ? null : sub;
            // 以前用 TextRenderer（GDI）量宽度、用 Graphics.DrawString（GDI+）画字，
            // 两种度量对同一条字符串给出不同宽度，GDI+ 更宽，尾巴就被裁掉了
            // （“正在识字…  Windows 内置 OCR” 的 OCR 看不到）。这里统一按 GDI+ 量。
            int textW;
            using (Graphics g = CreateGraphics())
            {
                if (g != null)
                {
                    _scale = g.DpiX / 96f;
                    textW = (int)Math.Ceiling(g.MeasureString(text, _title).Width);
                    if (_sub != null)
                    {
                        int sw = (int)Math.Ceiling(g.MeasureString(_sub, SmallFont()).Width);
                        if (sw > textW) textW = sw;
                    }
                }
                else
                {
                    _scale = 1f;
                    textW = TextRenderer.MeasureText(text, _title).Width;
                }
            }
            _textW = textW;
            int w = S(PAD_X) * 2 + S(28) + S(GAP) + textW;
            if (w < S(170)) w = S(170);
            int padY = S(15);
            int contentH = _title.Height + (_sub != null ? S(3) + SmallFont().Height : 0);
            int h = contentH + padY * 2;
            if (h < S(52)) h = S(52);
            Size = new Size(w, h);
        }

        // 居中靠下：在鼠标所在那块屏上水平居中，离工作区底边留一点距离
        private void PlaceToast()
        {
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            if (Left < wa.Left + S(8)) Left = wa.Left + S(8);
            _restTop = wa.Bottom - Height - S(56);
            Top = _restTop;
        }

        private void CaptureBackdrop()
        {
            try
            {
                if (_blurBg != null) { _blurBg.Dispose(); _blurBg = null; }
                Rectangle r = new Rectangle(Left, Top, Width, Height);
                if (r.Width <= 0 || r.Height <= 0) return;

                using (Bitmap full = new Bitmap(r.Width, r.Height))
                {
                    using (Graphics g = Graphics.FromImage(full))
                        g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size);

                    // 先缩小再模糊，效果接近高斯，速度也快
                    int sw = Math.Max(1, r.Width / 4);
                    int sh = Math.Max(1, r.Height / 4);
                    Bitmap small = new Bitmap(sw, sh);
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.DrawImage(full, new Rectangle(0, 0, sw, sh));
                    }
                    BoxBlur(small, 2, 3);
                    _blurBg = small;
                }
            }
            catch (Exception)
            {
                if (_blurBg != null) { _blurBg.Dispose(); _blurBg = null; }
            }
        }

        // 盒式模糊跑 3 遍，足够接近高斯；只处理缩小后的小图
        private static void BoxBlur(Bitmap bmp, int radius, int passes)
        {
            int w = bmp.Width, h = bmp.Height;
            if (w < 2 || h < 2) return;
            Color[] cur = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    cur[y * w + x] = bmp.GetPixel(x, y);

            Color[] tmp = new Color[w * h];
            for (int p = 0; p < passes; p++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int r = 0, g = 0, b = 0, n = 0;
                        for (int dy = -radius; dy <= radius; dy++)
                        {
                            int yy = y + dy;
                            if (yy < 0 || yy >= h) continue;
                            for (int dx = -radius; dx <= radius; dx++)
                            {
                                int xx = x + dx;
                                if (xx < 0 || xx >= w) continue;
                                Color c = cur[yy * w + xx];
                                r += c.R; g += c.G; b += c.B; n++;
                            }
                        }
                        tmp[y * w + x] = Color.FromArgb(255, r / n, g / n, b / n);
                    }
                }
                Color[] swap = cur; cur = tmp; tmp = swap;
            }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    bmp.SetPixel(x, y, cur[y * w + x]);
        }

        private static float EaseOut(float t)
        {
            float u = 1f - t;
            return 1f - u * u * u;           // 先快后慢，收尾柔和
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_anim == 1)
            {
                _animT += 0.12f;
                if (_animT >= 1f) { _animT = 1f; _anim = 0; }
                float t = EaseOut(_animT);
                Opacity = TOAST_OPACITY * t;
                Top = _restTop + (int)(S(SLIDE_PX) * (1f - t));
            }
            else if (_anim == 2)
            {
                _animT += 0.10f;
                if (_animT >= 1f) { HideToast(); return; }
                Opacity = TOAST_OPACITY * (1f - _animT);
                Top = _restTop + (int)(S(8) * _animT);
            }

            if (_state == STATE_BUSY)
            {
                _angle += 9f;
                if (_angle > 360f) _angle -= 360f;
                Invalidate();
                return;
            }
            if (_state == STATE_DONE && _anim == 0)
            {
                _holdMs -= TICK_MS;
                if (_holdMs <= 0) { _anim = 2; _animT = 0f; }
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int r = S(18);
            using (GraphicsPath path = Rounded(0, 0, Width, Height, r))
            {
                Region = new Region(path);
            }
        }

        private static GraphicsPath Rounded(int x, int y, int w, int h, int r)
        {
            GraphicsPath path = new GraphicsPath();
            int d = r * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d - 1, y, d, d, 270, 90);
            path.AddArc(x + w - d - 1, y + h - d - 1, d, d, 0, 90);
            path.AddArc(x, y + h - d - 1, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            Color accent = _state == STATE_BUSY ? Color.FromArgb(96, 165, 250)
                : (_ok ? Color.FromArgb(52, 211, 153) : Color.FromArgb(248, 113, 113));

            // 圆角裁剪：背景模糊 + 一层轻微黑遮罩，不加边框
            using (GraphicsPath clip = Rounded(0, 0, Width, Height, S(18)))
            {
                GraphicsState cs = g.Save();
                g.SetClip(clip);
                if (_blurBg != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_blurBg, new Rectangle(0, 0, Width, Height));
                }
                else
                {
                    using (SolidBrush fallback = new SolidBrush(Color.FromArgb(24, 28, 36)))
                        g.FillRectangle(fallback, ClientRectangle);
                }
                using (SolidBrush shade = new SolidBrush(Color.FromArgb(62, 5, 8, 12)))
                    g.FillRectangle(shade, ClientRectangle);
                g.Restore(cs);
            }

            // 内容整体水平居中：徽章 + 文字块；文字在自己的块里再水平居中
            int badge = S(28);
            int box = S(ICON_SIZE);
            int gapY = S(3);
            int subH = _sub != null ? SmallFont().Height : 0;
            int contentH = _title.Height + (_sub != null ? gapY + subH : 0);
            int contentW = badge + S(GAP) + _textW;
            int bx = (Width - contentW) / 2;
            if (bx < S(8)) bx = S(8);
            int by = (Height - badge) / 2;
            // 图标直接画，不加圆底/描边

            int ix = bx + (badge - box) / 2;
            int iy = by + (badge - box) / 2;

            // 图标按 96 dpi 的逻辑尺寸画，再整体缩放到当前 DPI，免得 150% 缩放时图标歪掉
            GraphicsState gs = g.Save();
            g.TranslateTransform((float)ix, (float)iy);
            g.ScaleTransform(_scale, _scale);

            if (_state == STATE_BUSY)
            {
                using (Pen track = new Pen(Color.FromArgb(64, 78, 96), 2F))
                {
                    g.DrawEllipse(track, 0f, 0f, ICON_SIZE - 2, ICON_SIZE - 2);
                }
                using (Pen arc = new Pen(accent, 2.2F))
                {
                    arc.StartCap = LineCap.Round;
                    arc.EndCap = LineCap.Round;
                    g.DrawArc(arc, 0f, 0f, ICON_SIZE - 2, ICON_SIZE - 2, _angle, 110f);
                }
            }
            else
            {
                using (Pen pen = new Pen(accent, 2.2F))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    if (_ok)
                    {
                        g.DrawLines(pen, new PointF[]
                        {
                            new PointF(2f, 9f),
                            new PointF(6.5f, 13.5f),
                            new PointF(14f, 4f)
                        });
                    }
                    else
                    {
                        g.DrawLines(pen, new PointF[]
                        {
                            new PointF(4f, 4f),
                            new PointF(13f, 14f)
                        });
                        g.DrawLines(pen, new PointF[]
                        {
                            new PointF(13f, 4f),
                            new PointF(4f, 14f)
                        });
                    }
                }
            }
            g.Restore(gs);

            int tx = bx + badge + S(GAP);
            int ty = (Height - contentH) / 2;
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Near;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                using (SolidBrush fg = new SolidBrush(Color.FromArgb(244, 247, 252)))
                {
                    g.DrawString(_text, _title, fg,
                        new RectangleF(tx, ty, _textW, _title.Height + 1), sf);
                }
                if (_sub != null)
                {
                    using (SolidBrush dim = new SolidBrush(Color.FromArgb(158, 169, 188)))
                    {
                        g.DrawString(_sub, SmallFont(), dim,
                            new RectangleF(tx, ty + _title.Height + gapY, _textW, subH + 1), sf);
                    }
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
                Font.Dispose();
                if (_title != null) { _title.Dispose(); _title = null; }
                if (_small != null) { _small.Dispose(); _small = null; }
                if (_blurBg != null) { _blurBg.Dispose(); _blurBg = null; }
                if (Current == this) Current = null;
            }
            base.Dispose(disposing);
        }
    }

    // 带 1px 描边的白色卡片
    // 圆角卡片：主窗口里交给主窗体画毛玻璃，设置页里自绘浅色圆角
    internal sealed class Card : Panel
    {
        public PaintEventHandler FrostPaint;
        public int Radius = 14;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        internal static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            if (d <= 0 || radius <= 0) { p.AddRectangle(r); p.CloseFigure(); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (FrostPaint != null)
            {
                FrostPaint(this, e);
                return;
            }
            // 设置页等没有背景图的窗口：浅色圆角
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = RoundedRect(r, Radius))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(248, 250, 253)))
                e.Graphics.FillPath(b, p);
            using (GraphicsPath p = RoundedRect(r, Radius))
            using (Pen pen = new Pen(Color.FromArgb(225, 230, 238)))
                e.Graphics.DrawPath(pen, p);
        }
    }

    // 毛玻璃按钮：自绘圆角半透明
    internal sealed class FrostButton : Button
    {
        public bool Primary;
        public bool Subtle;
        private bool _hover;

        public FrostButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatAppearance.BorderSize = 0;
            FlatStyle = FlatStyle.Flat;
            Cursor = Cursors.Hand;
            AutoSize = false;
            MinimumSize = new Size(0, 0);
            MouseEnter += delegate { _hover = true; Invalidate(); };
            MouseLeave += delegate { _hover = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Rectangle r = new Rectangle(0, 0, Width, Height);

            Color fill, borderColor, textColor;
            if (Primary)
            {
                fill = Color.FromArgb(_hover ? 59 : 74, 130, 230);
                borderColor = Color.FromArgb(_hover ? 50 : 60, 120, 220);
                textColor = Color.White;
            }
            else if (Subtle)
            {
                fill = Color.FromArgb(_hover ? 240 : 250, 250, 252);
                borderColor = Color.FromArgb(206, 212, 222);
                textColor = Color.FromArgb(50, 90, 190);
            }
            else
            {
                fill = Color.FromArgb(_hover ? 242 : 252, 252, 253);
                borderColor = Color.FromArgb(216, 222, 232);
                textColor = MainForm.Ink;
            }

            using (SolidBrush b = new SolidBrush(fill)) g.FillRectangle(b, r);
            using (Pen pen = new Pen(borderColor))
                g.DrawRectangle(pen, new Rectangle(0, 0, Width - 1, Height - 1));

            TextRenderer.DrawText(g, Text, Font, r, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // 透明底面板：不铺底色，让主窗口自绘的背景图透过来
    internal class Glass : Panel
    {
        public Glass()
        {
            SetStyle(ControlStyles.Opaque, false);
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }
    }

    // 透明背景文本框：文字直接画在背景上，无底色块
    internal sealed class TransparentTextBox : TextBox
    {
        public TransparentTextBox()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
            BorderStyle = BorderStyle.None;
        }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x20; // WS_EX_TRANSPARENT
                return cp;
            }
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 不画背景，让父控件的毛玻璃透出来
            if (Parent != null)
            {
                // 把父控件在本控件区域的内容画上来
                e.Graphics.ResetTransform();
                e.Graphics.TranslateTransform(-Left, -Top);
                using (PaintEventArgs pe = new PaintEventArgs(e.Graphics, Bounds))
                {
                    base.OnPaintBackground(pe);
                }
                e.Graphics.TranslateTransform(Left, Top);
            }
        }
    }

    // 透明底表格布局（卡片之间的空隙也要透出背景）
    internal sealed class GlassGrid : TableLayoutPanel
    {
        public GlassGrid()
        {
            SetStyle(ControlStyles.Opaque, false);
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }
    }

    // 结果区：原生 RichTextBox —— 可框选、可编辑、长文本自带滚动条；
    // 背景不由控件自己画，而是回调主窗体用同一套毛玻璃采样填进来，
    // 看上去还是透明透底，但文字是真文本，不再是一张画不出来的图片。
    internal sealed class ResultBox : RichTextBox
    {
        public delegate void SurfacePainter(Graphics g, Rectangle rect);

        public SurfacePainter PaintSurface;

        public ResultBox()
        {
            BorderStyle = BorderStyle.None;
            ScrollBars = RichTextBoxScrollBars.Vertical;
            Multiline = true;
            WordWrap = true;
            DetectUrls = false;
            AcceptsTab = false;
            HideSelection = false;
            ShortcutsEnabled = true;
            AllowDrop = false;
            BackColor = Color.White;
            ForeColor = MainForm.Ink;
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Regular, GraphicsUnit.Point);
            LanguageOption = RichTextBoxLanguageOptions.UIFonts;
            ZoomFactor = 1F;
        }

        protected override void WndProc(ref Message m)
        {
            // WM_ERASEBKGND：用卡片自己的毛玻璃底把这块区域填掉，实现“真透明”又不失可编辑
            if (m.Msg == 0x0014 && PaintSurface != null)
            {
                using (Graphics g = Graphics.FromHdc(m.WParam))
                {
                    g.SmoothingMode = SmoothingMode.None;
                    PaintSurface(g, ClientRectangle);
                }
                m.Result = (IntPtr)1;
                return;
            }

            base.WndProc(ref m);

            // 滚动后 RichEdit 可能自己刷一遍白底，这里再补一次自绘，避免滚动残影
            if (m.Msg == 0x0115 || m.Msg == 0x020A)
            {
                if (PaintSurface != null) Invalidate();
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private const int HOTKEY_QR = 0x5151;
        private const int HOTKEY_OCR = 0x5152;
        private const string WINDOW_TITLE = "XuYuan-SnapScan 二维码 / 文字识别";

        internal static readonly Color PageBack = Color.FromArgb(244, 246, 249);
        internal static readonly Color Ink = Color.FromArgb(31, 35, 41);
        internal static readonly Color SubInk = Color.FromArgb(75, 85, 99);
        internal static readonly Color FaintInk = Color.FromArgb(120, 128, 140);
        internal static readonly Color Accent = Color.FromArgb(47, 111, 224);
        internal static readonly Color Border = Color.FromArgb(203, 213, 225);

        private readonly ResultBox _txtResult;
        private readonly Label _lblStatus;
        private readonly Label _lblEngine;
        private readonly Label _lblPath;
        private readonly Label _lblHkQr;
        private readonly Label _lblHkOcr;
        private readonly Button _btnQr;
        private readonly Button _btnOcr;
        private readonly ToastForm _toast;

        private uint _modsQr = Native.MOD_CONTROL | Native.MOD_ALT;
        private uint _vkQr = (uint)Keys.D;
        private uint _modsOcr = Native.MOD_CONTROL | Native.MOD_ALT;
        private uint _vkOcr = (uint)Keys.X;

        private bool _qrRegistered;
        private bool _ocrRegistered;
        private bool _capturing;
        private bool _working;
        private bool _autoCopy = true;

        private bool _restoreWin = false;
        private bool _restoreQr = false;
        private bool _freehand = false;
        private SettingsForm _settings;

        private Image _backImage;
        private Bitmap _backBlurred;
        private int _backOpacity = AppConfig.DefaultBackOpacity;
        private int _frost = AppConfig.DefaultFrost;

        // 供 FrostButton 读取：卡片/按钮白色蒙层不透明度 0~255
        internal static int GlassAlpha = 200;

        internal int BackOpacity { get { return _backOpacity; } }
        internal int Frost { get { return _frost; } }

        // 原滑杆：现在控制卡片/按钮的白色蒙层不透明度（不是背景图）
        internal void SetBackOpacity(int value)
        {
            if (value < 0) value = 0;
            if (value > 100) value = 100;
            if (value == _backOpacity) return;
            _backOpacity = value;
            GlassAlpha = (int)(value * 2.55);
            Invalidate(true);
            if (_txtResult != null && !_txtResult.IsDisposed) _txtResult.Invalidate();
            Update();
            SaveConfig();
        }

        // 毛玻璃强度 0~20：重新生成模糊版背景图
        internal void SetFrost(int value)
        {
            if (value < 0) value = 0;
            if (value > 20) value = 20;
            if (value == _frost) return;
            _frost = value;
            RebuildBlurred();
            Invalidate(true);
            Update();
            SaveConfig();
        }

        private void RebuildBlurred()
        {
            if (_backBlurred != null) { _backBlurred.Dispose(); _backBlurred = null; }
            if (_backImage == null) return;
            _backBlurred = FastBlur((Bitmap)_backImage, _frost);
            // 结果区是自绘背景的子控件，背景图换了要让它重新擦一遍
            if (_txtResult != null && !_txtResult.IsDisposed) _txtResult.Invalidate();
        }

        // 3 趟盒式模糊近似高斯；radius=0 直接用原图
        private static Bitmap FastBlur(Bitmap src, int radius)
        {
            if (radius <= 0) return new Bitmap(src);
            int w = src.Width, h = src.Height;
            Bitmap cur = new Bitmap(src, w, h);
            for (int pass = 0; pass < 2; pass++)
            {
                Bitmap tmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                BoxH(cur, tmp, radius);
                cur.Dispose();
                Bitmap next = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                BoxV(tmp, next, radius);
                tmp.Dispose();
                cur = next;
            }
            return cur;
        }

        private static void BoxH(Bitmap src, Bitmap dst, int r)
        {
            int w = src.Width, h = src.Height;
            System.Drawing.Imaging.BitmapData sd = src.LockBits(new Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Drawing.Imaging.BitmapData dd = dst.LockBits(new Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int stride = sd.Stride;
                byte[] s = new byte[stride * h];
                byte[] d = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(sd.Scan0, s, 0, s.Length);
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int sr = 0, sg = 0, sb = 0;
                        int cnt = 0;
                        for (int k = x - r; k <= x + r; k++)
                        {
                            if (k < 0 || k >= w) continue;
                            int o = row + k * 4;
                            sb += s[o]; sg += s[o + 1]; sr += s[o + 2]; cnt++;
                        }
                        int oo = row + x * 4;
                        d[oo] = (byte)(sb / cnt);
                        d[oo + 1] = (byte)(sg / cnt);
                        d[oo + 2] = (byte)(sr / cnt);
                        d[oo + 3] = 255;
                    }
                }
                System.Runtime.InteropServices.Marshal.Copy(d, 0, dd.Scan0, d.Length);
            }
            finally { src.UnlockBits(sd); dst.UnlockBits(dd); }
        }

        private static void BoxV(Bitmap src, Bitmap dst, int r)
        {
            int w = src.Width, h = src.Height;
            System.Drawing.Imaging.BitmapData sd = src.LockBits(new Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Drawing.Imaging.BitmapData dd = dst.LockBits(new Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int stride = sd.Stride;
                byte[] s = new byte[stride * h];
                byte[] d = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(sd.Scan0, s, 0, s.Length);
                for (int x = 0; x < w; x++)
                {
                    int col = x * 4;
                    for (int y = 0; y < h; y++)
                    {
                        int sr = 0, sg = 0, sb = 0;
                        int cnt = 0;
                        for (int k = y - r; k <= y + r; k++)
                        {
                            if (k < 0 || k >= h) continue;
                            int o = k * stride + col;
                            sb += s[o]; sg += s[o + 1]; sr += s[o + 2]; cnt++;
                        }
                        int oo = y * stride + col;
                        d[oo] = (byte)(sb / cnt);
                        d[oo + 1] = (byte)(sg / cnt);
                        d[oo + 2] = (byte)(sr / cnt);
                        d[oo + 3] = 255;
                    }
                }
                System.Runtime.InteropServices.Marshal.Copy(d, 0, dd.Scan0, d.Length);
            }
            finally { src.UnlockBits(sd); dst.UnlockBits(dd); }
        }

        // 画一张卡片：圆角内先画模糊后的背景图，再叠半透明白
        private void PaintCard(object sender, PaintEventArgs e)
        {
            Card card = (Card)sender;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Rectangle r = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            using (GraphicsPath path = Card.RoundedRect(r, card.Radius))
            {
                if (_backBlurred != null)
                {
                    Point abs = card.PointToScreen(Point.Empty);
                    Point fp = PointToClient(abs);
                    float sx = (float)_backBlurred.Width / ClientSize.Width;
                    float sy = (float)_backBlurred.Height / ClientSize.Height;
                    RectangleF src = new RectangleF(fp.X * sx, fp.Y * sy,
                        card.Width * sx, card.Height * sy);
                    Region old = g.Clip;
                    g.SetClip(path);
                    g.DrawImage(_backBlurred, new Rectangle(0, 0, card.Width, card.Height),
                        src, GraphicsUnit.Pixel);
                    g.Clip = old;
                }
                int a = (int)(_backOpacity * 2.55);
                if (a > 0)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
                        g.FillPath(b, path);
                }
                using (Pen pen = new Pen(Color.FromArgb(70, 255, 255, 255)))
                    g.DrawPath(pen, path);
            }
        }

        public MainForm()
        {
            Text = WINDOW_TITLE;
            ClientSize = new Size(920, 640);
            MinimumSize = SizeFromClientSize(new Size(860, 600));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = PageBack;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            KeyPreview = true;
            DoubleBuffered = true;
            Icon = LoadAppIcon();

            _toast = new ToastForm();

            // 整窗背景图（编译时内嵌，透明度在设置里实时调）
            _backImage = LoadBackImage();

            // ================= 顶栏：标题 + 设置包在同一个毛玻璃卡片里 =================
            Glass header = new Glass();
            header.Dock = DockStyle.Top;
            header.Height = 56;
            header.Padding = new Padding(14, 8, 14, 8);

            Card titleBar = new Card();
            titleBar.FrostPaint = PaintCard;
            titleBar.Dock = DockStyle.Fill;
            titleBar.Radius = 12;

            Label appTitle = new Label();
            appTitle.Text = "XuYuan-SnapScan  ·  二维码 / 文字识别";
            appTitle.AutoSize = false;
            appTitle.Dock = DockStyle.Fill;
            appTitle.TextAlign = ContentAlignment.MiddleLeft;
            appTitle.BackColor = Color.Transparent;
            appTitle.ForeColor = Ink;
            appTitle.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold, GraphicsUnit.Point);
            appTitle.Padding = new Padding(14, 0, 0, 0);

            Button gear = new Button();
            gear.Text = "⚙  设置";
            gear.FlatStyle = FlatStyle.Flat;
            gear.FlatAppearance.BorderSize = 0;
            gear.FlatAppearance.MouseOverBackColor = Color.FromArgb(238, 244, 253);
            gear.FlatAppearance.MouseDownBackColor = Color.FromArgb(226, 236, 250);
            gear.BackColor = Color.Transparent;
            gear.ForeColor = Ink;
            gear.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold, GraphicsUnit.Point);
            gear.UseVisualStyleBackColor = false;
            gear.Dock = DockStyle.Right;
            gear.Width = 90;
            gear.Margin = new Padding(0);
            gear.Cursor = Cursors.Hand;
            gear.Click += delegate { OpenSettings(); };

            titleBar.Controls.Add(appTitle);
            titleBar.Controls.Add(gear);
            header.Controls.Add(titleBar);

            Glass page = new Glass();
            page.Dock = DockStyle.Fill;
            page.Padding = new Padding(16, 4, 16, 14);

            Glass rightCol = new Glass();
            rightCol.Dock = DockStyle.Right;
            rightCol.Width = 288;
            rightCol.Padding = new Padding(10, 0, 0, 0);

            GlassGrid stack = new GlassGrid();
            stack.Dock = DockStyle.Fill;
            stack.ColumnCount = 1;
            stack.RowCount = 4;
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 180F));
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 10F));
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 112F));
            stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            rightCol.Controls.Add(stack);

            // ================= 卡片一：操作 =================
            Card cardAct = new Card();
            cardAct.FrostPaint = PaintCard;
            cardAct.Dock = DockStyle.Fill;
            cardAct.Margin = new Padding(0);
            cardAct.Padding = new Padding(14, 12, 14, 12);

            TableLayoutPanel actRows = new TableLayoutPanel();
            actRows.Dock = DockStyle.Fill;
            actRows.BackColor = Color.Transparent;
            actRows.ColumnCount = 1;
            actRows.RowCount = 5;
            actRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            actRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            actRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 8F));
            actRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            actRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 8F));
            actRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));

            _btnQr = MakeButton("截图识码", true, 11.5F);
            _btnQr.Dock = DockStyle.Fill;
            _btnQr.Margin = new Padding(0);
            _btnQr.Click += delegate { StartCapture(false); };

            _btnOcr = MakeButton("截图识字", false, 11F);
            _btnOcr.Dock = DockStyle.Fill;
            _btnOcr.Margin = new Padding(0);
            _btnOcr.Click += delegate { StartCapture(true); };

            TableLayoutPanel smallRow = new TableLayoutPanel();
            smallRow.Dock = DockStyle.Fill;
            smallRow.BackColor = Color.Transparent;
            smallRow.ColumnCount = 2;
            smallRow.RowCount = 1;
            smallRow.Margin = new Padding(0);
            smallRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            smallRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            smallRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            Button btnCopy = MakeGhostButton("复制结果", 8.5F);
            btnCopy.Dock = DockStyle.Fill;
            btnCopy.Margin = new Padding(0, 0, 4, 0);
            btnCopy.Click += delegate { CopyResult(); };

            Button btnClear = MakeGhostButton("清空", 8.5F);
            btnClear.Dock = DockStyle.Fill;
            btnClear.Margin = new Padding(4, 0, 0, 0);
            btnClear.Click += delegate { ClearResult(); };

            smallRow.Controls.Add(btnCopy, 0, 0);
            smallRow.Controls.Add(btnClear, 1, 0);

            actRows.Controls.Add(_btnQr, 0, 0);
            actRows.Controls.Add(_btnOcr, 0, 2);
            actRows.Controls.Add(smallRow, 0, 4);

            cardAct.Controls.Add(actRows);

            // ================= 卡片二：文字引擎 =================
            Card cardEng = new Card();
            cardEng.FrostPaint = PaintCard;
            cardEng.Dock = DockStyle.Fill;
            cardEng.Margin = new Padding(0);
            cardEng.Padding = new Padding(14, 10, 14, 10);

            TableLayoutPanel engRows = new TableLayoutPanel();
            engRows.Dock = DockStyle.Fill;
            engRows.BackColor = Color.Transparent;
            engRows.ColumnCount = 1;
            engRows.RowCount = 4;
            engRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            engRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 18F));
            engRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            engRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            engRows.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            Label capEng = MakeLabel("文字引擎", 8.5F, SubInk, ContentAlignment.MiddleLeft, true);
            capEng.Dock = DockStyle.Fill;
            capEng.Margin = new Padding(0);

            _lblEngine = MakeLabel("检测中…", 9F, SubInk, ContentAlignment.MiddleLeft, true);
            _lblEngine.Dock = DockStyle.Fill;
            _lblEngine.Margin = new Padding(0);
            _lblEngine.AutoEllipsis = true;

            _lblPath = MakeLabel("", 7.5F, FaintInk, ContentAlignment.MiddleLeft, false);
            _lblPath.Dock = DockStyle.Fill;
            _lblPath.Margin = new Padding(0);
            _lblPath.AutoEllipsis = true;

            engRows.Controls.Add(capEng, 0, 0);
            engRows.Controls.Add(_lblEngine, 0, 1);
            engRows.Controls.Add(_lblPath, 0, 2);

            cardEng.Controls.Add(engRows);

            stack.Controls.Add(cardAct, 0, 0);
            stack.Controls.Add(cardEng, 0, 2);

            // ================= 左侧：识别结果 =================
            Card cardResult = new Card();
            cardResult.FrostPaint = PaintCard;
            cardResult.Dock = DockStyle.Fill;
            cardResult.Margin = new Padding(0, 0, 0, 0);
            cardResult.Padding = new Padding(16, 12, 16, 10);

            TableLayoutPanel resRows = new TableLayoutPanel();
            resRows.Dock = DockStyle.Fill;
            resRows.BackColor = Color.Transparent;
            resRows.ColumnCount = 1;
            resRows.RowCount = 3;
            resRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            resRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            resRows.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            resRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));

            Panel head = new Panel();
            head.Dock = DockStyle.Fill;
            head.BackColor = Color.Transparent;
            head.Margin = new Padding(0);

            Label capResult = MakeLabel("识别结果", 10.5F, Ink, ContentAlignment.MiddleLeft, true);
            capResult.Dock = DockStyle.Fill;
            capResult.Margin = new Padding(0);

            Label capHint = MakeLabel("可选中 / 编辑 · Ctrl+A 全选 · Ctrl+C 复制", 8F, FaintInk, ContentAlignment.MiddleRight, false);
            capHint.Dock = DockStyle.Right;
            capHint.Width = 250;
            capHint.Margin = new Padding(0);

            head.Controls.Add(capResult);
            head.Controls.Add(capHint);

            // 结果区：真透明背景 + 原生文字排版能力（可框选、可编辑、长文本自动出滚动条）
            _txtResult = new ResultBox();
            _txtResult.Dock = DockStyle.Fill;
            _txtResult.Margin = new Padding(2, 2, 2, 4);
            _txtResult.PaintSurface = PaintResultSurface;
            _txtResult.TextChanged += delegate { SyncResultPlaceholder(); };
            _txtResult.KeyDown += OnResultKeyDown;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            ToolStripMenuItem miCopy = new ToolStripMenuItem("复制");
            miCopy.Click += delegate { CopySelectionOrAll(); };
            ToolStripMenuItem miCut = new ToolStripMenuItem("剪切");
            miCut.Click += delegate { if (_txtResult.SelectionLength > 0) _txtResult.Cut(); };
            ToolStripMenuItem miPaste = new ToolStripMenuItem("粘贴");
            miPaste.Click += delegate
            {
                if (Clipboard.ContainsText()) _txtResult.SelectedText = Clipboard.GetText();
            };
            ToolStripMenuItem miAll = new ToolStripMenuItem("全选");
            miAll.Click += delegate { _txtResult.SelectAll(); };
            ToolStripMenuItem miClear = new ToolStripMenuItem("清空");
            miClear.Click += delegate { ClearResult(); };
            menu.Items.Add(miCopy);
            menu.Items.Add(miCut);
            menu.Items.Add(miPaste);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miAll);
            menu.Items.Add(miClear);
            _txtResult.ContextMenuStrip = menu;

            _lblStatus = MakeLabel("就绪", 8.5F, SubInk, ContentAlignment.MiddleLeft, false);
            _lblStatus.Dock = DockStyle.Fill;
            _lblStatus.Margin = new Padding(0);
            _lblStatus.AutoEllipsis = true;

            // 版本/体积信息放在卡片内，文字不透明
            Panel statusRow = new Panel();
            statusRow.Dock = DockStyle.Fill;
            statusRow.BackColor = Color.Transparent;
            statusRow.Margin = new Padding(0);

            Label lblVer = new Label();
            lblVer.Text = VersionLine();
            lblVer.Dock = DockStyle.Right;
            lblVer.Width = 214;
            lblVer.TextAlign = ContentAlignment.MiddleRight;
            lblVer.BackColor = Color.Transparent;
            lblVer.ForeColor = FaintInk;
            lblVer.Font = new Font(Font.FontFamily, 7.5F, FontStyle.Regular, GraphicsUnit.Point);

            statusRow.Controls.Add(_lblStatus);
            statusRow.Controls.Add(lblVer);

            resRows.Controls.Add(head, 0, 0);
            resRows.Controls.Add(_txtResult, 0, 1);
            resRows.Controls.Add(statusRow, 0, 2);

            cardResult.Controls.Add(resRows);

            page.Controls.Add(cardResult);
            page.Controls.Add(rightCol);
            Controls.Add(page);
            Controls.Add(header);
        }

        // 背景图（编译时内嵌为 Back.jpg，70 KB 左右）
        internal static Image LoadBackImage()
        {
            return LoadEmbeddedImage("Back.jpg");
        }

        internal static Icon LoadAppIcon()
        {
            try
            {
                System.Reflection.Assembly asm = typeof(MainForm).Assembly;
                using (Stream s = asm.GetManifestResourceStream("App.ico"))
                {
                    if (s != null) return new Icon(s);
                }
            }
            catch (Exception) { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { return SystemIcons.Application; }
        }

        internal static Image LoadEmbeddedImage(string resName)
        {
            try
            {
                System.Reflection.Assembly asm = typeof(MainForm).Assembly;
                using (Stream s = asm.GetManifestResourceStream(resName))
                {
                    if (s == null) return null;
                    MemoryStream ms = new MemoryStream();
                    byte[] buf = new byte[4096];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    ms.Position = 0;
                    return Image.FromStream(ms);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------- 小工具：控件外观 ----------
        private Button MakeButton(string text, bool primary, float size)
        {
            FrostButton b = new FrostButton();
            b.Text = text;
            b.Primary = primary;
            b.Font = new Font(Font.FontFamily, size, FontStyle.Bold, GraphicsUnit.Point);
            return b;
        }

        private Button MakeGhostButton(string text, float size)
        {
            FrostButton b = new FrostButton();
            b.Text = text;
            b.Subtle = true;
            b.Font = new Font(Font.FontFamily, size, FontStyle.Regular, GraphicsUnit.Point);
            return b;
        }

        private Label MakeLabel(string text, float size, Color color, ContentAlignment align, bool bold)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.Dock = DockStyle.Fill;
            l.TextAlign = align;
            l.ForeColor = color;
            l.Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
            l.Margin = new Padding(0);
            l.BackColor = Color.Transparent;
            return l;
        }

        // ---------- 结果区 ----------
        // 结果框每帧的背景都由卡片自己画，所以既保持毛玻璃透明，又能用原生文本控件框选/编辑
        private void PaintResultSurface(Graphics g, Rectangle rect)
        {
            // 背景图还没生成时不要填任何东西：宁可留空，也不要糊一块白色
            if (_backBlurred == null || rect.Width <= 0 || rect.Height <= 0) return;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Point win = PointToClient(_txtResult.PointToScreen(Point.Empty));
            float sx = (float)_backBlurred.Width / ClientSize.Width;
            float sy = (float)_backBlurred.Height / ClientSize.Height;
            RectangleF src = new RectangleF(win.X * sx, win.Y * sy, rect.Width * sx, rect.Height * sy);
            g.DrawImage(_backBlurred, rect, src, GraphicsUnit.Pixel);

            // 卡片上已经有一层白色蒙层；这里再叠一层，保证结果区和卡片完全同一档次
            int a = (int)(_backOpacity * 2.55);
            if (a > 0)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
                    g.FillRectangle(b, rect);
            }
        }

        private void OnResultKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                _txtResult.SelectAll();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void CopySelectionOrAll()
        {
            if (_txtResult.SelectionLength > 0)
            {
                if (TryCopy(_txtResult.SelectedText)) SetStatus("已复制选中的文字");
                return;
            }
            CopyResult();
        }

        private void SyncResultPlaceholder()
        {
            // 结果为空时给一句轻提示，长文本时有内容就不打扰
            if (_txtResult.TextLength == 0) return;
        }

        private static string VersionLine()
        {
            string ver = "1.3.0.0";
            try
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                ver = v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision;
            }
            catch (Exception)
            {
            }
            long kb = 0;
            try
            {
                FileInfo fi = new FileInfo(Application.ExecutablePath);
                if (fi.Exists) kb = fi.Length / 1024;
            }
            catch (Exception)
            {
            }
            string size = kb > 0 ? "约 " + kb.ToString() + " KB" : "免安装";
            return "v" + ver + "  ·  " + size + "  ·  不自启";
        }

        // ---------- 生命周期 ----------
        // 整窗背景：先铺浅灰底，再把那张画按正常透明度画上去（背景本身永远全彩）
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle rc = ClientRectangle;
            if (rc.Width <= 0 || rc.Height <= 0) return;
            using (SolidBrush b = new SolidBrush(PageBack))
            {
                e.Graphics.FillRectangle(b, rc);
            }
            if (_backImage == null) return;

            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(_backImage, rc, 0, 0, _backImage.Width, _backImage.Height,
                GraphicsUnit.Pixel);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            AppConfig.Load();
            string hkQr = AppConfig.HotkeyQr;
            string hkOcr = AppConfig.HotkeyOcr;
            bool legacy = AppConfig.Legacy;
            _restoreWin = AppConfig.RestoreWin;
            _restoreQr = AppConfig.RestoreAfterQr;
            _freehand = AppConfig.Freehand;
            _autoCopy = AppConfig.AutoCopy;
            _backOpacity = AppConfig.BackOpacity;
            _frost = AppConfig.Frost;
            GlassAlpha = (int)(_backOpacity * 2.55);
            RebuildBlurred();
            Invalidate(true);
            if (_txtResult != null && !_txtResult.IsDisposed) _txtResult.Invalidate();

            // 老版本默认是 Ctrl+Alt+Q，新默认改成 Ctrl+Alt+D
            if (legacy && string.Equals(hkQr.Replace(" ", ""), "Ctrl+Alt+Q", StringComparison.OrdinalIgnoreCase))
            {
                hkQr = AppConfig.DefaultHotkeyQr;
            }

            uint m;
            uint k;
            if (HotkeyText.TryParse(hkQr, out m, out k))
            {
                _modsQr = m;
                _vkQr = k;
            }
            if (HotkeyText.TryParse(hkOcr, out m, out k))
            {
                _modsOcr = m;
                _vkOcr = k;
            }
            if (_modsQr == _modsOcr && _vkQr == _vkOcr)
            {
                _modsOcr = Native.MOD_CONTROL | Native.MOD_ALT;
                _vkOcr = (uint)Keys.X;
            }

            UpdateEngineLabels();

            // 老配置在 %APPDATA%，顺手迁到 exe 同目录的 QRSnap_config.ini
            if (!string.Equals(AppConfig.ConfigPath, AppConfig.PrimaryConfigPath, StringComparison.OrdinalIgnoreCase))
            {
                SaveConfig();
            }

            // 先注册识码；被占用就按候选表自动换一个（和 Snipaste / QQ 截图一个思路）
            string noteQr;
            _qrRegistered = TryRegisterWithFallback(HOTKEY_QR, ref _modsQr, ref _vkQr,
                new uint[] { (uint)Keys.D, (uint)Keys.Q, (uint)Keys.R, (uint)Keys.S },
                0, 0, out noteQr);

            string noteOcr;
            _ocrRegistered = TryRegisterWithFallback(HOTKEY_OCR, ref _modsOcr, ref _vkOcr,
                new uint[] { (uint)Keys.X, (uint)Keys.Z, (uint)Keys.W, (uint)Keys.E, (uint)Keys.C },
                _modsQr, _vkQr, out noteOcr);

            UpdateHotkeyLabels();
            UpdateEngineLabels();

            if (_qrRegistered && _ocrRegistered)
            {
                string status = "就绪 · " + EngineShortName() + " · 识码 " + HotkeyText.Format(_modsQr, _vkQr)
                    + " / 识字 " + HotkeyText.Format(_modsOcr, _vkOcr);
                if (noteQr.Length > 0) status += "  ·  " + noteQr;
                if (noteOcr.Length > 0) status += "  ·  " + noteOcr;
                SetStatus(status);
                if (noteQr.Length > 0 || noteOcr.Length > 0) SaveConfig();
            }
            else
            {
                string fail = !_qrRegistered ? noteQr : noteOcr;
                SetStatus(fail + "，请到 设置 里换一个组合键");
            }
            // _txtResult.Focus() not needed

            if (OpenSettingsOnStart)
            {
                BeginInvoke((MethodInvoker)delegate { OpenSettings(); });
            }
        }

        private void UpdateHotkeyLabels()
        {
            string qr = HotkeyText.Format(_modsQr, _vkQr);
            string oc = HotkeyText.Format(_modsOcr, _vkOcr);
            _btnQr.Text = "截图识码  " + qr;
            _btnOcr.Text = "截图识字  " + oc;
        }

        // 快捷键注册：先试配置里的组合，失败则按候选表自动退让
        private bool TryRegisterWithFallback(int id, ref uint mods, ref uint vk,
            uint[] candidates, uint otherMods, uint otherVk, out string note)
        {
            note = string.Empty;
            if (RegisterHotkey(id, mods, vk)) return true;

            string wanted = HotkeyText.Format(mods, vk);
            uint m = mods;
            if (m == 0) m = Native.MOD_CONTROL | Native.MOD_ALT;
            bool duplicate = (otherVk != 0 && mods == otherMods && vk == otherVk);

            foreach (uint candidate in candidates)
            {
                if (candidate == vk) continue;
                if (otherVk != 0 && m == otherMods && candidate == otherVk) continue;
                if (RegisterHotkey(id, m, candidate))
                {
                    mods = m;
                    vk = candidate;
                    note = duplicate
                        ? wanted + " 与另一个功能重复，已自动改用 " + HotkeyText.Format(mods, vk)
                        : wanted + " 已被别的程序占用，已自动改用 " + HotkeyText.Format(mods, vk);
                    return true;
                }
            }

            note = wanted + " 注册失败（被别的程序占用）";
            return false;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_qrRegistered) Native.UnregisterHotKey(Handle, HOTKEY_QR);
            if (_ocrRegistered) Native.UnregisterHotKey(Handle, HOTKEY_OCR);
            _qrRegistered = false;
            _ocrRegistered = false;
            try
            {
                _toast.HideToast();
                _toast.Dispose();
            }
            catch (Exception)
            {
            }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_HOTKEY = 0x0312;
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HOTKEY_QR) { if (!_capturing && !_working) StartCapture(false); return; }
                if (id == HOTKEY_OCR) { if (!_capturing && !_working) StartCapture(true); return; }
            }
            base.WndProc(ref m);
        }

        private bool RegisterHotkey(int id, uint mods, uint vk)
        {
            return Native.RegisterHotKey(Handle, id, mods | Native.MOD_NOREPEAT, vk);
        }

        // ---------- 引擎信息 ----------
        // 首页只写“这一台机器上真实探测到”的引擎，探不到就不许显示已就绪
        private void UpdateEngineLabels()
        {
            EngineProbe probe = Ocr.Probe();
            string selected = AppConfig.NormalizeEngine(AppConfig.Engine);

            if (probe.WeChatUsable)
            {
                bool ok = probe.WeChatVerified;
                _lblEngine.Text = ok
                    ? "微信 OCR · 中文支持好 · 已就绪"
                    : "微信 OCR · 中文支持好 · 可用";
                _lblEngine.ForeColor = ok ? Color.FromArgb(22, 138, 74) : Color.FromArgb(22, 120, 74);
                _lblPath.Text = "当前：" + (selected == AppConfig.EngineWeChat ? "微信 OCR" : "Windows 内置 OCR")
                    + "  ·  微信 " + probe.WeChatVersion + " / 插件 " + probe.PluginVersion;
                _lblPath.ForeColor = FaintInk;
                return;
            }

            // 没探到微信 → 真正能用的只有系统 OCR，文案必须说实话
            _lblEngine.Text = "Windows 内置 OCR · 中文一般";
            _lblEngine.ForeColor = Color.FromArgb(154, 116, 36);
            _lblPath.Text = "未检测到微信 OCR（" + ShortProblem(probe.Problem) + "）";
            _lblPath.ForeColor = FaintInk;
        }

        private static string ShortProblem(string problem)
        {
            if (string.IsNullOrEmpty(problem)) return "未安装";
            string s = problem.Trim();
            int i = s.IndexOf('，');
            if (i > 0 && i < 26) s = s.Substring(0, i);
            if (s.Length > 28) s = s.Substring(0, 28) + "…";
            return s;
        }

        private string EngineShortName()
        {
            string e = Ocr.EffectiveEngine();
            return e == AppConfig.EngineWeChat ? "微信 OCR" : "Windows OCR";
        }

        // 设置页改完设置后统一回到这里刷新（引擎重新探测、开关同步、配置落盘）
        internal void ReloadSettings()
        {
            _autoCopy = AppConfig.AutoCopy;
            _restoreWin = AppConfig.RestoreWin;
            _restoreQr = AppConfig.RestoreAfterQr;
            _freehand = AppConfig.Freehand;
            UpdateEngineLabels();
            UpdateHotkeyLabels();
            SaveConfig();
        }

        internal void SaveConfig()
        {
            AppConfig.HotkeyQr = HotkeyText.Format(_modsQr, _vkQr);
            AppConfig.HotkeyOcr = HotkeyText.Format(_modsOcr, _vkOcr);
            AppConfig.AutoCopy = _autoCopy;
            AppConfig.RestoreWin = _restoreWin;
            AppConfig.RestoreAfterQr = _restoreQr;
            AppConfig.Freehand = _freehand;
            AppConfig.BackOpacity = _backOpacity;
            AppConfig.Frost = _frost;
            AppConfig.Save();
        }

        private void RestoreWindowIfNeeded(bool afterCapture)
        {
            bool want = afterCapture ? _restoreQr : _restoreWin;
            if (!want) return;
            ShowMainWindow();
        }

        private void ShowMainWindow()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        // 设置页里改快捷键：校验 → 重注册 → 落盘
        internal bool ApplyHotkey(bool isQr, uint mods, uint vk, out string message)
        {
            message = string.Empty;
            if (!HotkeyText.IsSupportedKey(vk))
            {
                message = "不支持的按键，请用 A~Z、0~9 或 F1~F24";
                return false;
            }
            if (mods == 0)
            {
                message = "快捷键需包含 Ctrl / Alt / Shift 至少一个";
                return false;
            }
            if (isQr && mods == _modsOcr && vk == _vkOcr)
            {
                message = "这个组合键已经给了「截图识字」";
                return false;
            }
            if (!isQr && mods == _modsQr && vk == _vkQr)
            {
                message = "这个组合键已经给了「截图识码」";
                return false;
            }

            uint oldMods = isQr ? _modsQr : _modsOcr;
            uint oldVk = isQr ? _vkQr : _vkOcr;
            int id = isQr ? HOTKEY_QR : HOTKEY_OCR;
            bool wasRegistered = isQr ? _qrRegistered : _ocrRegistered;

            if (wasRegistered) Native.UnregisterHotKey(Handle, id);
            if (!RegisterHotkey(id, mods, vk))
            {
                RegisterHotkey(id, oldMods, oldVk);
                message = "这个组合键已经被其他程序占用了，换一个试试。";
                return false;
            }

            if (isQr)
            {
                _modsQr = mods;
                _vkQr = vk;
                _qrRegistered = true;
            }
            else
            {
                _modsOcr = mods;
                _vkOcr = vk;
                _ocrRegistered = true;
            }
            SaveConfig();
            UpdateHotkeyLabels();
            SetStatus((isQr ? "识码" : "识字") + "快捷键已更新为 " + HotkeyText.Format(mods, vk));
            return true;
        }

        internal uint HotkeyMods(bool isQr) { return isQr ? _modsQr : _modsOcr; }
        internal uint HotkeyVk(bool isQr) { return isQr ? _vkQr : _vkOcr; }
        internal bool HotkeyRegistered(bool isQr) { return isQr ? _qrRegistered : _ocrRegistered; }
        internal bool AutoCopyEnabled { get { return _autoCopy; } }
        internal bool RestoreWinEnabled { get { return _restoreWin; } set { _restoreWin = value; } }
        internal bool RestoreQrEnabled { get { return _restoreQr; } set { _restoreQr = value; } }
        internal bool FreehandEnabled { get { return _freehand; } set { _freehand = value; } }

        internal void SetAutoCopy(bool value)
        {
            _autoCopy = value;
            SaveConfig();
        }

        // ---------- 截屏 ----------
        private void StartCapture(bool forOcr)
        {
            if (_working)
            {
                SetStatus("正在识别，请稍候…");
                return;
            }
            if (_capturing) return;
            _capturing = true;
            _toast.HideToast();

            // 截图期间把主窗口缩到任务栏（不 Hide()）：Hide 会让它在识别完成后自己弹回来
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Minimized;
                // 等最小化动画走完再抓屏，免得把自己的残影拍进去
                Native.FlushComposition();
                Thread.Sleep(170);
                Native.FlushComposition();
            }

            Bitmap crop = null;
            CaptureSession session = new CaptureSession();
            session.Freehand = _freehand;
            try
            {
                session.Run();
                crop = session.Crop;
            }
            catch (Exception ex)
            {
                SetStatus("截图失败：" + ex.Message);
            }

            _capturing = false;

            if (crop == null)
            {
                SetStatus("已取消");
                return;
            }

            if (forOcr) BeginOcr(crop);
            else BeginDecode(crop);
        }


        private void BeginDecode(Bitmap crop)
        {
            SetStatus("正在识别二维码…");
            _toast.ShowBusy("识别中…  二维码");

            Thread worker = new Thread(new ThreadStart(delegate
            {
                string text = null;
                string info = string.Empty;
                long elapsed = 0;
                string error = null;
                try
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    text = QrDecoder.Decode(crop, out info);
                    sw.Stop();
                    elapsed = sw.ElapsedMilliseconds;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                finally
                {
                    crop.Dispose();
                }

                if (IsDisposed) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate { FinishDecode(text, info, elapsed, error); });
                }
                catch (Exception)
                {
                }
            }));
            worker.IsBackground = true;
            worker.Start();
        }

        private void FinishDecode(string text, string info, long elapsed, string error)
        {
            if (IsDisposed) return;

            if (error != null)
            {
                SetStatus("识别出错：" + error);
                _toast.ShowDone("识别失败", false);
                return;
            }
            if (string.IsNullOrEmpty(text))
            {
                SetStatus("未识别到二维码，框小一点再试（耗时 " + elapsed.ToString() + " ms）");
                _toast.ShowDone("未发现二维码", false);
                return;
            }

            _txtResult.Text = text;
            RestoreWindowIfNeeded(true);

            string status = "识别成功 · " + info + " · " + elapsed.ToString() + " ms · " + text.Length.ToString() + " 字符";
            if (_autoCopy && TryCopy(text))
            {
                status += " · 已复制";
            }
            SetStatus(status);
            _toast.ShowDone("识别完成", true);
        }

        private void BeginOcr(Bitmap crop)
        {
            _working = true;
            string want = Ocr.EffectiveName();
            SetStatus("正在识别文字…（" + want + "）");
            _toast.ShowBusy("识别中…  文字（" + want + "）");

            Thread worker = new Thread(new ThreadStart(delegate
            {
                string text = null;
                string engine = string.Empty;
                string detail = string.Empty;
                long elapsed = 0;
                string error = null;
                try
                {
                    text = Ocr.Recognize(crop, out engine, out elapsed, out detail);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                finally
                {
                    crop.Dispose();
                }

                if (IsDisposed) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate { FinishOcr(text, engine, elapsed, detail, error); });
                }
                catch (Exception)
                {
                }
            }));
            worker.IsBackground = true;
            worker.Start();
        }

        private void FinishOcr(string text, string engine, long elapsed, string detail, string error)
        {
            _working = false;
            if (IsDisposed) return;

            if (error != null)
            {
                SetStatus("文字识别出错：" + error);
                _toast.ShowDone("识别失败", false);
                return;
            }
            if (string.IsNullOrEmpty(text))
            {
                SetStatus("未识别到文字（" + engine + " · " + elapsed.ToString() + " ms）");
                _toast.ShowDone("未识别到文字", false);
                return;
            }

            _txtResult.Text = text;
            RestoreWindowIfNeeded(false);

            string status = "文字识别成功 · " + engine + " · " + elapsed.ToString() + " ms · "
                + text.Length.ToString() + " 字符";
            if (detail.Length > 0) status = status + " · " + detail;
            if (_autoCopy && TryCopy(text))
            {
                status += " · 已复制";
            }
            SetStatus(status);
            _toast.ShowDone("识别完成", true);
        }

        private void CopyResult()
        {
            string text = _txtResult.Text;
            if (string.IsNullOrEmpty(text))
            {
                SetStatus("没有可复制的内容");
                return;
            }
            if (TryCopy(text)) SetStatus("已复制到剪贴板");
            else SetStatus("复制失败，剪贴板被其他程序占用");
        }

        private bool TryCopy(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return true;
                }
                catch (Exception)
                {
                    Thread.Sleep(80);
                }
            }
            return false;
        }

        private void ClearResult()
        {
            _txtResult.Text = "";
            _toast.HideToast();
            SetStatus("已清空 · 识码 " + HotkeyText.Format(_modsQr, _vkQr) + " / 识字 " + HotkeyText.Format(_modsOcr, _vkOcr));
        }

        // ---------- 设置二级页 ----------
        internal bool OpenSettingsOnStart;   // --settings 自检入口用

        private void OpenSettings()
        {
            if (_working)
            {
                SetStatus("正在识别，请稍候…");
                return;
            }
            if (_settings != null && !_settings.IsDisposed)
            {
                _settings.Activate();
                return;
            }
            _settings = new SettingsForm(this);
            _settings.Show(this);
            _settings.FormClosed += delegate { _settings = null; };
        }

        private void SetStatus(string text)
        {
            _lblStatus.Text = text;
        }
    }

    // 设置二级页：文字引擎 / 快捷键 / 外观 / 截图方式 / 行为
    internal sealed class SettingsForm : Form
    {
        private readonly MainForm _owner;
        private readonly ComboBox _cboEngine;
        private readonly List<string> _engineKeys = new List<string>();
        private readonly Label _lblNow;
        private readonly Label _lblModel;
        private readonly Button _btnPickWx;
        private readonly TextBox _boxQr;
        private readonly TextBox _boxOcr;
        private readonly CheckBox _chkCopy;
        private readonly CheckBox _chkRestore;
        private readonly CheckBox _chkRestoreQr;
        private readonly CheckBox _chkFreehand;
        private readonly Label _lblCfg;
        private readonly TrackBar _barOpacity;
        private readonly Label _lblOpacity;
        private readonly TrackBar _barFrost;
        private readonly Label _lblFrost;
        private bool _loading = true;

        private const int PageWidth = 480;
        private const int PageHeight = 900;

        public SettingsForm(MainForm owner)
        {
            _owner = owner;

            Text = "XuYuan-SnapScan 设置";
            AutoScroll = true;
            AutoScrollMinSize = new Size(PageWidth, PageHeight);
            ClientSize = new Size(PageWidth, PageHeight);
            // 屏幕不够高时缩小窗口并自动滚动
            Screen scr = Screen.FromPoint(Cursor.Position);
            int maxH = scr.WorkingArea.Height - 40;
            if (ClientSize.Height > maxH) ClientSize = new Size(PageWidth, maxH);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            DoubleBuffered = true;
            Icon = MainForm.LoadAppIcon();

            Label title = new Label();
            title.Text = "设置";
            title.Location = new Point(22, 16);
            title.AutoSize = true;
            title.ForeColor = MainForm.Ink;
            title.Font = new Font(Font.FontFamily, 14F, FontStyle.Bold, GraphicsUnit.Point);

            Label sub = new Label();
            sub.Text = "改动即时生效，自动保存到 XuYuan-SnapScan_config.ini";
            sub.Location = new Point(24, 47);
            sub.AutoSize = true;
            sub.ForeColor = MainForm.FaintInk;
            sub.Font = new Font(Font.FontFamily, 8.5F, FontStyle.Regular, GraphicsUnit.Point);

            // ---------- 卡片一：文字引擎 ----------
            Card cardEng = new Card();
            cardEng.Location = new Point(20, 76);
            cardEng.Size = new Size(440, 246);

            Label capEng = new Label();
            capEng.Text = "文字引擎（只列这台电脑上真正检测到的）";
            capEng.Location = new Point(16, 12);
            capEng.AutoSize = true;
            capEng.ForeColor = MainForm.SubInk;
            capEng.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold, GraphicsUnit.Point);

            _cboEngine = new ComboBox();
            _cboEngine.Location = new Point(16, 38);
            _cboEngine.Size = new Size(300, 24);
            _cboEngine.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboEngine.FlatStyle = FlatStyle.Flat;
            _cboEngine.SelectedIndexChanged += delegate
            {
                if (_loading) return;
                int i = _cboEngine.SelectedIndex;
                if (i < 0 || i >= _engineKeys.Count) return;
                AppConfig.Engine = _engineKeys[i];
                _owner.ReloadSettings();
                RefreshState();
            };

            _btnPickWx = MakeSmallButton("指定微信目录", new Point(324, 37), new Size(100, 27), false);
            _btnPickWx.Click += delegate { PickWeChatDir(); };

            _lblNow = new Label();
            _lblNow.Location = new Point(16, 70);
            _lblNow.Size = new Size(404, 20);
            _lblNow.ForeColor = MainForm.SubInk;
            _lblNow.Font = new Font(Font.FontFamily, 8.5F, FontStyle.Bold, GraphicsUnit.Point);

            _lblModel = new Label();
            _lblModel.Location = new Point(16, 92);
            _lblModel.Size = new Size(404, 34);
            _lblModel.ForeColor = MainForm.FaintInk;
            _lblModel.Font = new Font(Font.FontFamily, 7.5F, FontStyle.Regular, GraphicsUnit.Point);
            _lblModel.AutoEllipsis = true;

            Button btnRecheck = MakeSmallButton("重新检测", new Point(16, 132), new Size(96, 28), true);
            btnRecheck.Click += delegate
            {
                Ocr.Probe();
                RebuildEngineList();
                RefreshState();
            };

            Label hintWx = new Label();
            hintWx.Location = new Point(124, 136);
            hintWx.Size = new Size(300, 74);
            hintWx.ForeColor = MainForm.FaintInk;
            hintWx.Font = new Font(Font.FontFamily, 7.5F, FontStyle.Regular, GraphicsUnit.Point);
            hintWx.Text = "微信 OCR 是微信 4.x 自带的能力，中文识别明显更准；本软件直接调用它，"
                + "不用另外装模型或 Python。没有微信时自动用 Windows 10/11 内置 OCR（免安装、中文一般）。";

            cardEng.Controls.Add(capEng);
            cardEng.Controls.Add(_cboEngine);
            cardEng.Controls.Add(_btnPickWx);
            cardEng.Controls.Add(_lblNow);
            cardEng.Controls.Add(_lblModel);
            cardEng.Controls.Add(btnRecheck);
            cardEng.Controls.Add(hintWx);

            // ---------- 卡片二：快捷键 ----------
            Card cardKeys = new Card();
            cardKeys.Location = new Point(20, 296);
            cardKeys.Size = new Size(440, 136);

            Label capKeys = new Label();
            capKeys.Text = "快捷键";
            capKeys.Location = new Point(16, 12);
            capKeys.AutoSize = true;
            capKeys.ForeColor = MainForm.SubInk;
            capKeys.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold, GraphicsUnit.Point);

            Label capQr = new Label();
            capQr.Text = "识码";
            capQr.Location = new Point(16, 45);
            capQr.Size = new Size(44, 26);
            capQr.TextAlign = ContentAlignment.MiddleLeft;
            capQr.ForeColor = MainForm.SubInk;

            _boxQr = MakeHotkeyBox(new Point(68, 43), new Size(140, 26));

            Label capOcr = new Label();
            capOcr.Text = "识字";
            capOcr.Location = new Point(16, 81);
            capOcr.Size = new Size(44, 26);
            capOcr.TextAlign = ContentAlignment.MiddleLeft;
            capOcr.ForeColor = MainForm.SubInk;

            _boxOcr = MakeHotkeyBox(new Point(68, 79), new Size(140, 26));

            Label hint = new Label();
            hint.Text = "点进输入框，直接按下\r\n新的组合键即可（需带 Ctrl/Alt/Shift）";
            hint.Location = new Point(226, 43);
            hint.Size = new Size(196, 56);
            hint.ForeColor = MainForm.FaintInk;
            hint.Font = new Font(Font.FontFamily, 8F, FontStyle.Regular, GraphicsUnit.Point);

            cardKeys.Controls.Add(capKeys);
            cardKeys.Controls.Add(capQr);
            cardKeys.Controls.Add(_boxQr);
            cardKeys.Controls.Add(capOcr);
            cardKeys.Controls.Add(_boxOcr);
            cardKeys.Controls.Add(hint);

            // ---------- 卡片三：外观（毛玻璃） ----------
            Card cardBg = new Card();
            cardBg.Location = new Point(20, 444);
            cardBg.Size = new Size(440, 150);

            Label capBg = new Label();
            capBg.Text = "外观 · 毛玻璃";
            capBg.Location = new Point(16, 12);
            capBg.AutoSize = true;
            capBg.ForeColor = MainForm.SubInk;
            capBg.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold, GraphicsUnit.Point);

            Label capOpacity = new Label();
            capOpacity.Text = "不透明度";
            capOpacity.Location = new Point(16, 42);
            capOpacity.Size = new Size(54, 26);
            capOpacity.TextAlign = ContentAlignment.MiddleLeft;
            capOpacity.ForeColor = MainForm.SubInk;

            _barOpacity = new TrackBar();
            _barOpacity.Minimum = 0;
            _barOpacity.Maximum = 100;
            _barOpacity.TickFrequency = 10;
            _barOpacity.SmallChange = 5;
            _barOpacity.LargeChange = 10;
            _barOpacity.Location = new Point(70, 30);
            _barOpacity.Size = new Size(272, 45);
            _barOpacity.Value = _owner.BackOpacity;

            _lblOpacity = new Label();
            _lblOpacity.Text = _owner.BackOpacity + "%";
            _lblOpacity.Location = new Point(348, 42);
            _lblOpacity.Size = new Size(60, 26);
            _lblOpacity.TextAlign = ContentAlignment.MiddleLeft;
            _lblOpacity.ForeColor = MainForm.Ink;
            _lblOpacity.Font = new Font("Consolas", 9.5F, FontStyle.Bold, GraphicsUnit.Point);

            Label capFrost = new Label();
            capFrost.Text = "模糊度";
            capFrost.Location = new Point(16, 96);
            capFrost.Size = new Size(54, 26);
            capFrost.TextAlign = ContentAlignment.MiddleLeft;
            capFrost.ForeColor = MainForm.SubInk;

            _barFrost = new TrackBar();
            _barFrost.Minimum = 0;
            _barFrost.Maximum = 20;
            _barFrost.TickFrequency = 2;
            _barFrost.Size = new Size(272, 45);
            _barFrost.Location = new Point(70, 84);
            _barFrost.Value = _owner.Frost;

            _lblFrost = new Label();
            _lblFrost.Text = _owner.Frost.ToString();
            _lblFrost.Location = new Point(348, 96);
            _lblFrost.Size = new Size(60, 26);
            _lblFrost.TextAlign = ContentAlignment.MiddleLeft;
            _lblFrost.ForeColor = MainForm.Ink;
            _lblFrost.Font = new Font("Consolas", 9.5F, FontStyle.Bold, GraphicsUnit.Point);

            Label hintBg = new Label();
            hintBg.Text = "拖动即时生效；不透明度控制卡片/按钮，背景图始终全彩";
            hintBg.Location = new Point(16, 126);
            hintBg.Size = new Size(410, 18);
            hintBg.ForeColor = MainForm.FaintInk;
            hintBg.Font = new Font(Font.FontFamily, 8F, FontStyle.Regular, GraphicsUnit.Point);

            _barOpacity.ValueChanged += delegate
            {
                int v = _barOpacity.Value;
                _lblOpacity.Text = v + "%";
                if (_loading) return;
                _owner.SetBackOpacity(v);
            };
            _barFrost.ValueChanged += delegate
            {
                int v = _barFrost.Value;
                _lblFrost.Text = v.ToString();
                if (_loading) return;
                _owner.SetFrost(v);
            };

            cardBg.Controls.Add(capBg);
            cardBg.Controls.Add(capOpacity);
            cardBg.Controls.Add(_barOpacity);
            cardBg.Controls.Add(_lblOpacity);
            cardBg.Controls.Add(capFrost);
            cardBg.Controls.Add(_barFrost);
            cardBg.Controls.Add(_lblFrost);
            cardBg.Controls.Add(hintBg);

            // ---------- 卡片四：截图方式与行为 ----------
            Card cardShot = new Card();
            cardShot.Location = new Point(20, 606);
            cardShot.Size = new Size(440, 146);

            Label capShot = new Label();
            capShot.Text = "截图方式与行为";
            capShot.Location = new Point(16, 12);
            capShot.AutoSize = true;
            capShot.ForeColor = MainForm.SubInk;
            capShot.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold, GraphicsUnit.Point);

            _chkFreehand = new CheckBox();
            _chkFreehand.Text = "自由圈选（按住鼠标画任意形状，只裁轮廓内的内容）";
            _chkFreehand.Location = new Point(14, 38);
            _chkFreehand.AutoSize = true;
            _chkFreehand.ForeColor = MainForm.Ink;
            _chkFreehand.Checked = _owner.FreehandEnabled;
            _chkFreehand.CheckedChanged += delegate
            {
                if (_loading) return;
                _owner.FreehandEnabled = _chkFreehand.Checked;
                _owner.SaveConfig();
            };

            _chkRestore = new CheckBox();
            _chkRestore.Text = "识别后恢复本软件窗口（默认最小化不恢复）";
            _chkRestore.Location = new Point(14, 66);
            _chkRestore.AutoSize = true;
            _chkRestore.ForeColor = MainForm.Ink;
            _chkRestore.Checked = _owner.RestoreWinEnabled;
            _chkRestore.CheckedChanged += delegate
            {
                if (_loading) return;
                _owner.RestoreWinEnabled = _chkRestore.Checked;
                _owner.SaveConfig();
            };

            _chkRestoreQr = new CheckBox();
            _chkRestoreQr.Text = "扫码后显示本软件窗口（只对识码生效）";
            _chkRestoreQr.Location = new Point(14, 94);
            _chkRestoreQr.AutoSize = true;
            _chkRestoreQr.ForeColor = MainForm.Ink;
            _chkRestoreQr.Checked = _owner.RestoreQrEnabled;
            _chkRestoreQr.CheckedChanged += delegate
            {
                if (_loading) return;
                _owner.RestoreQrEnabled = _chkRestoreQr.Checked;
                _owner.SaveConfig();
            };

            cardShot.Controls.Add(capShot);
            cardShot.Controls.Add(_chkFreehand);
            cardShot.Controls.Add(_chkRestore);
            cardShot.Controls.Add(_chkRestoreQr);

            // ---------- 卡片五：自动复制与配置 ----------
            Card cardMisc = new Card();
            cardMisc.Location = new Point(20, 762);
            cardMisc.Size = new Size(440, 118);

            _chkCopy = new CheckBox();
            _chkCopy.Text = "识别后自动复制到剪贴板";
            _chkCopy.Location = new Point(14, 12);
            _chkCopy.AutoSize = true;
            _chkCopy.ForeColor = MainForm.Ink;
            _chkCopy.Checked = _owner.AutoCopyEnabled;
            _chkCopy.CheckedChanged += delegate
            {
                if (_loading) return;
                _owner.SetAutoCopy(_chkCopy.Checked);
            };

            Label capCfg = new Label();
            capCfg.Text = "配置文件";
            capCfg.Location = new Point(16, 44);
            capCfg.AutoSize = true;
            capCfg.ForeColor = MainForm.SubInk;
            capCfg.Font = new Font(Font.FontFamily, 8.5F, FontStyle.Bold, GraphicsUnit.Point);

            _lblCfg = new Label();
            _lblCfg.Location = new Point(16, 68);
            _lblCfg.Size = new Size(320, 18);
            _lblCfg.ForeColor = MainForm.FaintInk;
            _lblCfg.Font = new Font(Font.FontFamily, 7.5F, FontStyle.Regular, GraphicsUnit.Point);
            _lblCfg.AutoEllipsis = true;
            _lblCfg.Text = AppConfig.ConfigPath;

            Button btnFolder = MakeSmallButton("所在目录", new Point(340, 62), new Size(88, 26), false);
            btnFolder.Click += delegate
            {
                try
                {
                    Process.Start("explorer.exe", "/select,\"" + AppConfig.ConfigPath + "\"");
                }
                catch (Exception)
                {
                }
            };

            cardMisc.Controls.Add(_chkCopy);
            cardMisc.Controls.Add(capCfg);
            cardMisc.Controls.Add(_lblCfg);
            cardMisc.Controls.Add(btnFolder);

            Button btnClose = MakeSmallButton("关闭", new Point(376, 850), new Size(84, 30), true);
            btnClose.Click += delegate { Close(); };

            Controls.Add(title);
            Controls.Add(sub);
            Controls.Add(cardEng);
            Controls.Add(cardKeys);
            Controls.Add(cardBg);
            Controls.Add(cardShot);
            Controls.Add(cardMisc);
            Controls.Add(btnClose);

            _boxQr.Text = HotkeyText.Format(_owner.HotkeyMods(true), _owner.HotkeyVk(true));
            _boxOcr.Text = HotkeyText.Format(_owner.HotkeyMods(false), _owner.HotkeyVk(false));

            _loading = false;

            RebuildEngineList();
            RefreshState();
        }

        // 引擎下拉只放真正可用的项；一项都没有时直接禁用并给出获取提示
        private void RebuildEngineList()
        {
            _loading = true;
            _engineKeys.Clear();
            _cboEngine.Items.Clear();

            EngineProbe probe = Ocr.CurrentProbe;
            if (probe != null && probe.WeChatUsable)
            {
                _engineKeys.Add(AppConfig.EngineWeChat);
                _cboEngine.Items.Add("微信 OCR · 中文支持好（本机已检测到微信 " + probe.WeChatVersion + "）");
            }
            _engineKeys.Add(AppConfig.EngineSystem);
            _cboEngine.Items.Add("Windows 内置 OCR · 免安装（中文一般）");

            string cur = AppConfig.NormalizeEngine(AppConfig.Engine);
            int idx = _engineKeys.IndexOf(cur);
            if (idx < 0)
            {
                idx = 0;
                AppConfig.Engine = _engineKeys[0];
                _owner.ReloadSettings();
            }
            _cboEngine.SelectedIndex = idx;
            _cboEngine.Enabled = _engineKeys.Count > 0;
            _loading = false;
        }

        private void RefreshState()
        {
            EngineProbe probe = Ocr.CurrentProbe;
            string cur = AppConfig.NormalizeEngine(AppConfig.Engine);
            string name = cur == AppConfig.EngineWeChat ? "微信 OCR · 中文支持好" : "Windows 内置 OCR";

            if (cur == AppConfig.EngineWeChat && (probe == null || !probe.WeChatUsable))
            {
                name = "Windows 内置 OCR（微信 OCR 不可用，已自动降级）";
            }

            _lblNow.Text = "当前使用：" + name;
            _lblNow.ForeColor = (cur == AppConfig.EngineWeChat && probe != null && probe.WeChatUsable)
                ? Color.FromArgb(22, 138, 74)
                : Color.FromArgb(154, 116, 36);

            if (probe != null && probe.WeChatUsable)
            {
                _lblModel.Text = "微信 OCR：微信 " + probe.WeChatVersion + " · 插件 " + probe.PluginVersion
                    + " · " + probe.WeChatInstallDir;
                _btnPickWx.Text = "换微信目录";
            }
            else
            {
                _lblModel.Text = "未检测到微信 OCR：" + (probe == null ? "探测失败" : probe.Problem);
                _btnPickWx.Text = "指定微信目录";
            }
            _lblCfg.Text = AppConfig.ConfigPath;
        }

        // 微信装在非默认位置时，允许手动指定（会存进配置的 wxdir）
        private void PickWeChatDir()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选微信安装目录（含 mmmojo_64.dll 的那一层，例如 D:\\Tencent\\Weixin\\4.1.15.56），"
                    + "或选上一级让程序自己往下找";
                string cur = AppConfig.WeChatDir;
                if (string.IsNullOrEmpty(cur)) cur = Ocr.CurrentProbe.WeChatInstallDir;
                if (!string.IsNullOrEmpty(cur) && Directory.Exists(cur)) dlg.SelectedPath = cur;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                AppConfig.WeChatDir = dlg.SelectedPath;
                Ocr.Probe();
                RebuildEngineList();
                RefreshState();
                _owner.ReloadSettings();

                EngineProbe p = Ocr.CurrentProbe;
                if (p != null && p.WeChatUsable)
                {
                    MessageBox.Show(this, "微信 OCR 已接通：\r\n" + p.WeChatInstallDir
                        + "\r\n\r\n插件：" + p.PluginDll, "检测成功",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this, (p == null ? "探测失败" : p.Problem)
                        + "\r\n\r\n常见原因：微信是 3.x（只有 32 位插件）；或微信 4.x 还没登录、没用过截图识字。",
                        "没找到微信 OCR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private Button MakeSmallButton(string text, Point loc, Size size, bool primary)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = loc;
            b.Size = size;
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.Font = new Font(Font.FontFamily, 8.5F, primary ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
            b.Cursor = Cursors.Hand;
            if (primary)
            {
                b.BackColor = MainForm.Accent;
                b.ForeColor = Color.White;
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(62, 128, 240);
                b.FlatAppearance.MouseDownBackColor = Color.FromArgb(36, 96, 200);
            }
            else
            {
                b.BackColor = Color.FromArgb(250, 251, 253);
                b.ForeColor = Color.FromArgb(66, 72, 84);
                b.FlatAppearance.BorderSize = 1;
                b.FlatAppearance.BorderColor = MainForm.Border;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 244, 250);
                b.FlatAppearance.MouseDownBackColor = Color.FromArgb(228, 235, 246);
            }
            return b;
        }

        private TextBox MakeHotkeyBox(Point loc, Size size)
        {
            TextBox t = new TextBox();
            t.Location = loc;
            t.Size = size;
            t.ReadOnly = true;
            t.TextAlign = HorizontalAlignment.Center;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = Color.FromArgb(250, 251, 253);
            t.ForeColor = MainForm.Ink;
            t.Font = new Font("Consolas", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
            t.KeyDown += HotkeyBoxKeyDown;
            t.MouseDown += delegate { t.Focus(); };
            return t;
        }

        private void HotkeyBoxKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;

            Keys code = e.KeyCode;
            if (code == Keys.None || code == Keys.ControlKey || code == Keys.ShiftKey ||
                code == Keys.Menu || code == Keys.LWin || code == Keys.RWin)
            {
                return;
            }

            uint mods = 0;
            if (e.Control) mods |= Native.MOD_CONTROL;
            if (e.Alt) mods |= Native.MOD_ALT;
            if (e.Shift) mods |= Native.MOD_SHIFT;

            TextBox box = (TextBox)sender;
            bool isQr = (box == _boxQr);
            string message;
            if (!_owner.ApplyHotkey(isQr, mods, (uint)code, out message))
            {
                box.Text = HotkeyText.Format(_owner.HotkeyMods(isQr), _owner.HotkeyVk(isQr));
                MessageBox.Show(this, message, "快捷键不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            box.Text = HotkeyText.Format(mods, (uint)code);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;

            // 正式界面：JadeView 2.0 + Web UI（新增 --classic 可退回旧的 WinForms 界面）
            bool legacyUi = args.Length >= 1 && args[0] == "--classic";
            bool selfTestArgs = args.Length >= 1 && (args[0] == "--selftest" || args[0] == "--ocrselftest"
                || args[0] == "--probe" || args[0] == "--toasttest" || args[0] == "--dumpstate" || args[0] == "--import"
                || args[0] == "--aiocrselftest" || args[0] == "--aimttest" || args[0] == "--repltest"
                || args[0] == "--layouttest" || args[0] == "--segtest" || args[0] == "--cacheclear");
            if (!legacyUi && !selfTestArgs)
            {
                if (!RuntimeCheck.Verify()) return;
                JadeHost.Run();
                return;
            }

            // 自检：QRSnap.exe --selftest 图片路径 [结果输出路径]
            if (args.Length >= 2 && args[0] == "--selftest")
            {
                SelfTest(args[1], args.Length >= 3 ? args[2] : null);
                return;
            }

            // 文字识别自检：QRSnap.exe --ocrselftest 图片路径 [结果输出路径] [wechat|system]
            if (args.Length >= 2 && args[0] == "--ocrselftest")
            {
                OcrSelfTest(args[1], args.Length >= 3 ? args[2] : null, args.Length >= 4 ? args[3] : "wechat");
                return;
            }

            // AI 识字强化自检：QRSnap.exe --aiocrselftest 图片路径 [结果输出路径]
            // 同一张图先关 AI 跑一遍拿原文，再开 AI 跑一遍，输出逐行对照
            if (args.Length >= 2 && args[0] == "--aiocrselftest")
            {
                AiOcrSelfTest(args[1], args.Length >= 3 ? args[2] : null);
                return;
            }

            // AI 翻译接管 / 遮罩分组自检：QRSnap.exe --aimttest 图片路径 [结果输出路径]
            if (args.Length >= 2 && args[0] == "--aimttest")
            {
                AiMtSelfTest(args[1], args.Length >= 3 ? args[2] : null);
                return;
            }

            // 字号 / 遮罩自检：QRSnap.exe --repltest 图片路径 输出png
            if (args.Length >= 3 && args[0] == "--repltest")
            {
                ReplaceSelfTest(args[1], args[2]);
                return;
            }

            // 版面感知链路自检：QRSnap.exe --layouttest 图片 输出png [ai|local] [ppmed|pptiny]
            if (args.Length >= 3 && args[0] == "--layouttest")
            {
                LayoutSelfTest(args[1], args[2],
                    args.Length >= 4 ? args[3] : "ai",
                    args.Length >= 5 ? args[4] : "ppmed",
                    args.Length >= 6 ? args[5] : null);
                return;
            }

            // 识别框自检：QRSnap.exe --segtest 图片 输出txt [wechat|pptiny|ppmed|system]
            if (args.Length >= 3 && args[0] == "--segtest")
            {
                SegSelfTest(args[1], args[2], args.Length >= 4 ? args[3] : "wechat");
                return;
            }

            // 导入模型包自检：QRSnap.exe --import 包路径 [结果输出路径]
            if (args.Length >= 2 && args[0] == "--import")
            {
                string res;
                try { AppConfig.Load(); res = JadeHost.ImportForTest(args[1]); }
                catch (Exception ex) { res = "ERROR: " + ex.Message; }
                if (args.Length >= 3)
                {
                    try { File.WriteAllText(args[2], res, new UTF8Encoding(false)); } catch (Exception) { }
                }
                return;
            }

            // 清理缓存（截图缓存 + 本地译文缓存）：QRSnap.exe --cacheclear
            if (args.Length >= 1 && args[0] == "--cacheclear")
            {
                try
                {
                    AppConfig.Load();
                    ShotCache.Clear();
                    try { MtRunner.Router.ClearCache(); } catch (Exception) { }
                    AiBoost.ClearCache();
                }
                catch (Exception) { }
                return;
            }

            // 界面状态自检：QRSnap.exe --dumpstate 输出路径
            if (args.Length >= 2 && args[0] == "--dumpstate")
            {
                try
                {
                    AppConfig.Load();
                    File.WriteAllText(args[1], JadeHost.BuildState(), new UTF8Encoding(false));
                }
                catch (Exception exState)
                {
                    try { File.WriteAllText(args[1], "ERROR: " + exState.ToString(), new UTF8Encoding(false)); } catch (Exception) { }
                }
                return;
            }

            // 引擎探测自检：QRSnap.exe --probe 结果输出路径
            if (args.Length >= 2 && args[0] == "--probe")
            {
                EngineProbe probe = Ocr.Probe();
                string report = "wechatFound=" + (probe.WeChatUsable ? "1" : "0")
                    + "\r\nwechatVerified=" + (probe.WeChatVerified ? "1" : "0")
                    + "\r\ndir=" + probe.WeChatInstallDir
                    + "\r\nversion=" + probe.WeChatVersion
                    + "\r\nhost=" + probe.WeChatHostExe
                    + "\r\nplugin=" + probe.PluginDll
                    + "\r\npluginVersion=" + probe.PluginVersion
                    + "\r\nonly3=" + (probe.WeChat3Only ? "1" : "0")
                    + "\r\nproblem=" + probe.Problem;
                try
                {
                    File.WriteAllText(args[1], report, new UTF8Encoding(false));
                }
                catch (Exception)
                {
                }
                return;
            }

            // 悬浮提示自检：QRSnap.exe --toasttest（不弹主窗口，只演示右下角浮窗）
            if (args.Length >= 1 && args[0] == "--toasttest")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                ToastForm toast = new ToastForm();
                System.Windows.Forms.Timer seq = new System.Windows.Forms.Timer();
                seq.Interval = 700;
                int step = 0;
                seq.Tick += delegate
                {
                    step++;
                    if (step == 1) toast.ShowBusy("正在识字…  微信 OCR", "AI 识字强化已开：认错的符号交给大模型修，每张图多几秒");
                    if (step == 5) toast.ShowBusy("正在翻译…", "AI 接管：整段交给大模型翻，不用本地模型");
                    if (step == 8) toast.SaveShot(@"E:\下载\豆包\_mt\toast-busy.png");
                    if (step == 9) toast.ShowDone("翻译完成", "AI 修字 2/12 行 · AI 分段 2 段 · AI 接管 19 行 3.9s", true);
                    if (step == 12) toast.SaveShot(@"E:\下载\豆包\_mt\toast-done.png");
                    if (step == 16)
                    {
                        seq.Stop();
                        toast.Close();
                        Application.ExitThread();
                    }
                };
                seq.Start();
                Application.Run(new ApplicationContext());
                return;
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "XuYuan-SnapScan_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    Native.ActivateWindow("XuYuan-SnapScan 二维码 / 文字识别");
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    MainForm form = new MainForm();
                    bool withSettings = args.Length >= 1 && args[0] == "--settings";
                    if (withSettings) form.OpenSettingsOnStart = true;
                    Application.Run(form);
                }
                finally
                {
                    GC.KeepAlive(mutex);
                }
            }
        }

        private static Assembly ResolveEmbeddedAssembly(object sender, ResolveEventArgs args)
        {
            string simpleName = new AssemblyName(args.Name).Name;
            if (!string.Equals(simpleName, "zxing", StringComparison.OrdinalIgnoreCase)) return null;

            Assembly self = Assembly.GetExecutingAssembly();
            using (Stream stream = self.GetManifestResourceStream("ZXing.Net.dll"))
            {
                if (stream == null) return null;
                byte[] buffer = new byte[stream.Length];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return Assembly.Load(buffer);
            }
        }

        private static Bitmap LoadBitmap(string imagePath)
        {
            Bitmap bmp;
            using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(imagePath)))
            using (Image img = Image.FromStream(ms))
            {
                bmp = new Bitmap(img);
            }
            return bmp;
        }

        private static void SelfTest(string imagePath, string outputPath)
        {
            if (outputPath == null)
            {
                outputPath = Path.Combine(Path.GetDirectoryName(imagePath), "selftest-result.txt");
            }
            string report;
            try
            {
                Bitmap bmp = LoadBitmap(imagePath);
                string info;
                Stopwatch sw = Stopwatch.StartNew();
                string text = QrDecoder.Decode(bmp, out info);
                sw.Stop();
                bmp.Dispose();

                if (text == null) report = "DECODE_FAILED";
                else report = text + "\r\ninfo=" + info + "\r\nms=" + sw.ElapsedMilliseconds.ToString();
            }
            catch (Exception ex)
            {
                report = "ERROR: " + ex.ToString();
            }
            try
            {
                File.WriteAllText(outputPath, report, new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
        }

        // AI 识字强化自检：同一张图，先关 AI 拿原文，再开 AI 拿校正版，输出逐行对照
        private static void AiOcrSelfTest(string imagePath, string outputPath)
        {
            if (outputPath == null)
                outputPath = Path.Combine(Path.GetDirectoryName(imagePath), "aiocrselftest-result.txt");
            string report;
            try
            {
                AppConfig.Load();
                bool want = AppConfig.AiOcr;
                bool ready = JadeHost.AiVerifySync(12000);
                Bitmap bmp = LoadBitmap(imagePath);

                List<OcrSegment> raw = new List<OcrSegment>();
                AppConfig.AiOcr = false;
                string e1; long ms1; string d1;
                string t1 = Ocr.Recognize(bmp, out e1, out ms1, out d1, raw);

                List<OcrSegment> fin = new List<OcrSegment>();
                AppConfig.AiOcr = true;
                string e2; long ms2; string d2;
                // ms2 只是引擎自己报的识别耗时，AI 纠错在 Recognize 内部追加，
                // 所以这里单独掐表，报告里给出的才是"这一轮真实花了多久"
                System.Diagnostics.Stopwatch swAi = System.Diagnostics.Stopwatch.StartNew();
                string t2 = Ocr.Recognize(bmp, out e2, out ms2, out d2, fin);
                swAi.Stop();

                AppConfig.AiOcr = want;
                bmp.Dispose();

                StringBuilder sb = new StringBuilder();
                sb.Append("engine=").Append(e1).Append("\r\n");
                sb.Append("aiReady=").Append(ready ? "1" : "0").Append("\r\n");
                sb.Append("aiOcrSwitch=").Append(want ? "1" : "0").Append("\r\n");
                sb.Append("rawMs=").Append(ms1).Append(" aiMs=").Append(swAi.ElapsedMilliseconds)
                    .Append(" engineMs=").Append(ms2).Append("\r\n");
                sb.Append("linesRaw=").Append(raw.Count).Append(" linesAi=").Append(fin.Count).Append("\r\n");

                int n = Math.Min(raw.Count, fin.Count);
                int diff = 0;
                sb.Append("DIFF_BEGIN\r\n");
                for (int i = 0; i < n; i++)
                {
                    string a = raw[i].Text ?? "";
                    string b = fin[i].Text ?? "";
                    if (!string.Equals(a, b))
                    {
                        diff++;
                        sb.Append("[").Append(i).Append("] - ").Append(a).Append("\r\n     + ").Append(b).Append("\r\n");
                    }
                }
                sb.Append("diff=").Append(diff).Append("\r\nDIFF_END\r\n");
                // 遮罩落位是否跟文本行一致，就看这段：每行文本配它自己的包围盒
                sb.Append("SEG_BEGIN\r\n");
                for (int i = 0; i < fin.Count; i++)
                {
                    Rectangle b = fin[i].Box;
                    sb.Append(i).Append(" @").Append(b.X).Append(",").Append(b.Y)
                      .Append(" ").Append(b.Width).Append("x").Append(b.Height)
                      .Append(" | ").Append(fin[i].Text ?? "").Append("\r\n");
                }
                sb.Append("SEG_END\r\n");
                sb.Append("RAW_BEGIN\r\n").Append(t1 ?? "(null)").Append("\r\nRAW_END\r\n");
                sb.Append("AI_BEGIN\r\n").Append(t2 ?? "(null)").Append("\r\nAI_END\r\n");
                report = sb.ToString();
            }
            catch (Exception ex)
            {
                report = "ERROR: " + ex.ToString();
            }
            try { File.WriteAllText(outputPath, report, new UTF8Encoding(false)); } catch (Exception) { }
        }

        // 自检：OCR 一遍 → 遮罩分组 → AI 接管翻译，把两件事的结果都摊开看
        private static void AiMtSelfTest(string imagePath, string outputPath)
        {
            if (outputPath == null)
                outputPath = Path.Combine(Path.GetDirectoryName(imagePath), "aimttest-result.txt");
            string report;
            try
            {
                AppConfig.Load();
                bool ready = JadeHost.AiVerifySync(12000);
                Bitmap bmp = LoadBitmap(imagePath);
                List<OcrSegment> segs = new List<OcrSegment>();
                string eng; long ms; string det;
                string text = Ocr.Recognize(bmp, out eng, out ms, out det, segs);
                bmp.Dispose();

                StringBuilder sb = new StringBuilder();
                sb.Append("engine=").Append(eng).Append("\r\n");
                sb.Append("aiReady=").Append(ready ? "1" : "0").Append("\r\n");
                sb.Append("model=").Append(AppConfig.AiModel).Append(" @ ").Append(AppConfig.AiBase).Append("\r\n");
                sb.Append("lines=").Append(segs.Count).Append("\r\n");

                StringBuilder all = new StringBuilder();
                for (int i = 0; i < segs.Count; i++) all.Append(segs[i].Text).Append('\n');
                string src = LangDetect.Detect(all.ToString());
                sb.Append("src=").Append(src).Append("\r\n");

                int t0 = Environment.TickCount;
                List<List<int>> groups = AiBoost.PlanMask(segs);
                sb.Append("maskMs=").Append(Environment.TickCount - t0).Append("\r\n");
                sb.Append("GROUP_BEGIN\r\n");
                if (groups == null) sb.Append("(null，没介入或校验没过)\r\n");
                else
                    for (int g = 0; g < groups.Count; g++)
                    {
                        sb.Append('[').Append(g).Append("] ");
                        for (int k = 0; k < groups[g].Count; k++) sb.Append(groups[g][k]).Append(' ');
                        sb.Append("\r\n");
                    }
                sb.Append("GROUP_END\r\n");

                t0 = Environment.TickCount;
                string err;
                Dictionary<int, string> trs = AiBoost.TranslateAll(segs, src, out err);
                sb.Append("mtMs=").Append(Environment.TickCount - t0).Append("\r\n");
                sb.Append("TR_BEGIN\r\n");
                if (trs == null) sb.Append("FAIL: ").Append(err).Append("\r\n");
                else
                    for (int i = 0; i < segs.Count; i++)
                    {
                        sb.Append('[').Append(i).Append("] ").Append(segs[i].Text ?? "").Append("\r\n     → ")
                          .Append(trs.ContainsKey(i) ? trs[i] : "(缺)").Append("\r\n");
                    }
                sb.Append("TR_END\r\n");
                report = sb.ToString();
            }
            catch (Exception ex)
            {
                report = "ERROR: " + ex.ToString();
            }
            try { File.WriteAllText(outputPath, report, new UTF8Encoding(false)); } catch (Exception) { }
        }

        // 字号/遮罩自检：把「原文」当译文原样回填一遍，字号贴不贴原文一看就知道。
        //   QRSnap.exe --repltest <图> <输出.png>
        private static void ReplaceSelfTest(string imagePath, string outputPng)
        {
            string report;
            try
            {
                AppConfig.Load();
                Bitmap bmp = LoadBitmap(imagePath);
                List<OcrSegment> segs = new List<OcrSegment>();
                string eng; long ms; string det;
                Ocr.Recognize(bmp, out eng, out ms, out det, segs);

                List<MtRealistic.Block> blocks = new List<MtRealistic.Block>();
                for (int i = 0; i < segs.Count; i++)
                {
                    MtRealistic.Block b = new MtRealistic.Block();
                    b.Box = segs[i].Box;
                    b.Text = segs[i].Text ?? "";
                    b.Group = -1;
                    blocks.Add(b);
                }

                MtRealistic.Stats st;
                Bitmap outImg = MtRealistic.Render(bmp, blocks, out st);
                if (outImg != null) outImg.Save(outputPng, System.Drawing.Imaging.ImageFormat.Png);
                bmp.Dispose();

                StringBuilder sb = new StringBuilder();
                sb.Append("engine=").Append(eng).Append("\r\n");
                sb.Append("lines=").Append(segs.Count).Append("\r\n");
                sb.Append("hard=").Append(st.Hard).Append(" wrap=").Append(st.Wrapped)
                  .Append(" skip=").Append(st.Skipped).Append("\r\n");
                sb.Append("BLOCK_BEGIN\r\n");
                for (int i = 0; i < blocks.Count; i++)
                {
                    sb.Append(i).Append(" h=").Append(blocks[i].Box.Height)
                      .Append(" cap=").Append(blocks[i].SizeCap.ToString("0.0"))
                      .Append(" ratio=").Append((blocks[i].SizeCap / Math.Max(1, blocks[i].Box.Height)).ToString("0.00"))
                      .Append(" | ").Append(blocks[i].Text).Append("\r\n");
                }
                sb.Append("BLOCK_END\r\n");
                report = sb.ToString();
            }
            catch (Exception ex) { report = "ERROR: " + ex.ToString(); }
            try
            {
                File.WriteAllText(Path.ChangeExtension(outputPng, ".txt"), report, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        // 版面感知链路自检：分栏 OCR → 词表/日期本地化 → AI 翻译 → 拟真回填
        //   QRSnap.exe --layouttest <图> <输出.png> [ai|local] [ppmed|pptiny]
        private static void LayoutSelfTest(string imagePath, string outputPng, string mode, string tier, string profileOverride)
        {
            string report;
            try
            {
                AppConfig.Load();
                bool aiReady = JadeHost.AiVerifySync(12000);
                string ppTier = string.Equals(tier, "pptiny", StringComparison.OrdinalIgnoreCase)
                    ? AppConfig.EnginePpTiny : AppConfig.EnginePpMedium;
                PpOcr pp = Ocr.LocalFor(ppTier);
                if (pp == null) { File.WriteAllText(Path.ChangeExtension(outputPng, ".txt"), "ERROR: PP-OCR 模型缺失"); return; }

                Bitmap bmp = LoadBitmap(imagePath);
                Stopwatch sw = Stopwatch.StartNew();
                string log;
                List<Rectangle> obstacles;
                List<OcrSegment> segs = LayoutBoost.Recognize(bmp, pp, out log, out obstacles);
                long ocrMs = sw.ElapsedMilliseconds;

                StringBuilder all = new StringBuilder();
                for (int i = 0; i < segs.Count; i++) all.Append(segs[i].Text).Append('\n');
                string src = LangDetect.Detect(all.ToString());

                string err = null;
                Dictionary<int, string> trs = null;
                long mtMs = 0;
                string usedProfile = profileOverride;
                if (string.Equals(mode, "ai", StringComparison.OrdinalIgnoreCase) && aiReady)
                {
                    sw.Restart();
                    trs = AiBoost.TranslateAll(segs, src, out err);
                    mtMs = sw.ElapsedMilliseconds;
                }
                else if (string.Equals(mode, "local", StringComparison.OrdinalIgnoreCase))
                {
                    // 纯离线：本地 MT（efficiency = Opus-MT 小模型，standard = GGUF 大模型）
                    string prof = AppConfig.NormalizeMtProfile(
                        string.IsNullOrEmpty(profileOverride) ? AppConfig.MtProfile : profileOverride);
                    usedProfile = prof;
                    sw.Restart();
                    try
                    {
                        MtRouter router = MtRunner.Router;
                        string[] plan = router.PlanFor(src, "zh", AppConfig.MtPair, prof);
                        if (plan.Length == 0)
                        {
                            List<string> miss = router.MissingKit();
                            err = miss.Count > 0 ? "缺少 " + string.Join("、", miss.ToArray()) : "没有可用的 " + src + "→zh 模型";
                        }
                        else
                        {
                            trs = new Dictionary<int, string>();
                            List<int> need = new List<int>();
                            for (int i = 0; i < segs.Count; i++)
                            {
                                string text = segs[i].Text ?? "";
                                string pre = LayoutBoost.PreTranslate(text);
                                if (pre != null) { trs[i] = pre; continue; }
                                if (text.Trim().Length == 0) continue;
                                need.Add(i);
                            }
                            if (plan[0] == "gguf")
                            {
                                // 大模型：一次送多行，少几十次往返；对不上行数就回退逐行
                                int k = 0;
                                while (k < need.Count)
                                {
                                    List<int> ids = new List<int>();
                                    List<string> texts = new List<string>();
                                    int chars = 0;
                                    while (k < need.Count && ids.Count < 8 && chars < 650)
                                    {
                                        string t = segs[need[k]].Text ?? "";
                                        ids.Add(need[k]);
                                        texts.Add(t);
                                        chars += t.Length;
                                        k++;
                                    }
                                    List<string> got = null;
                                    try { got = LlamaMt.TranslateBatch(plan[1], texts, "zh"); }
                                    catch (Exception) { got = null; }
                                    if (got != null && got.Count == ids.Count)
                                    {
                                        for (int i = 0; i < ids.Count; i++) trs[ids[i]] = got[i];
                                    }
                                    else
                                    {
                                        for (int i = 0; i < ids.Count; i++)
                                        {
                                            string route;
                                            string tr = router.Translate(texts[i], src, "zh", AppConfig.MtPair, prof, out route);
                                            if (!string.IsNullOrEmpty(tr)) trs[ids[i]] = tr;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                for (int i = 0; i < need.Count; i++)
                                {
                                    string route;
                                    string tr = router.Translate(segs[need[i]].Text, src, "zh", AppConfig.MtPair, prof, out route);
                                    if (!string.IsNullOrEmpty(tr)) trs[need[i]] = tr;
                                }
                            }
                        }
                    }
                    catch (Exception exLoc) { err = exLoc.Message; }
                    mtMs = sw.ElapsedMilliseconds;
                }

                List<MtRealistic.Block> blocks = new List<MtRealistic.Block>();
                StringBuilder sb = new StringBuilder();
                sb.Append("engine=").Append(pp.Label).Append("\r\n");
                sb.Append("aiReady=").Append(aiReady ? "1" : "0").Append("\r\n");
                sb.Append("src=").Append(src).Append("\r\n");
                sb.Append("ocrMs=").Append(ocrMs).Append(" mtMs=").Append(mtMs).Append("\r\n");
                sb.Append("lines=").Append(segs.Count).Append("\r\n");
                if (err != null) sb.Append("mtErr=").Append(err).Append("\r\n");
                sb.Append("GUTTER_BEGIN\r\n").Append(log).Append("GUTTER_END\r\n");
                sb.Append("TR_BEGIN\r\n");

                for (int i = 0; i < segs.Count; i++)
                {
                    string tr = trs != null && trs.ContainsKey(i) ? trs[i] : null;
                    string pre = LayoutBoost.PreTranslate(segs[i].Text);
                    if (pre != null) tr = pre;
                    if (tr != null) tr = LayoutBoost.PostFix(tr);
                    if (string.IsNullOrEmpty(tr)) tr = segs[i].Text;

                    sb.Append(i).Append(" @").Append(segs[i].Box.X).Append(',').Append(segs[i].Box.Y)
                      .Append(' ').Append(segs[i].Box.Width).Append('x').Append(segs[i].Box.Height)
                      .Append(" | ").Append(segs[i].Text).Append("\r\n     -> ").Append(tr).Append("\r\n");

                    MtRealistic.Block b = new MtRealistic.Block();
                    b.Box = segs[i].Box;
                    b.Text = tr;
                    b.Angle = segs[i].Angle;
                    b.Group = -1;
                    b.SourceBoxes = segs[i].SourceBoxes;
                    b.PreGrouped = true;
                    b.Obstacles = obstacles;
                    blocks.Add(b);
                }
                sb.Append("TR_END\r\n");

                MtRealistic.Stats st;
                Bitmap outImg = MtRealistic.Render(bmp, blocks, out st);
                if (outImg != null) outImg.Save(outputPng, System.Drawing.Imaging.ImageFormat.Png);
                sb.Append("hard=").Append(st.Hard).Append(" wrap=").Append(st.Wrapped).Append(" skip=").Append(st.Skipped).Append("\r\n");
                bmp.Dispose();
                report = sb.ToString();
            }
            catch (Exception ex) { report = "ERROR: " + ex.ToString(); }
            try { File.WriteAllText(Path.ChangeExtension(outputPng, ".txt"), report, new UTF8Encoding(false)); } catch (Exception) { }
        }

        // 识别框自检：把「文字 + 每行框」原样摊开，用来分别调微信 / PP-OCR 两种引擎
        private static void SegSelfTest(string imagePath, string outputPath, string mode)
        {
            string report;
            try
            {
                AppConfig.Load();
                if (mode == "wechat") AppConfig.Engine = AppConfig.EngineWeChat;
                else if (mode == "pptiny") AppConfig.Engine = AppConfig.EnginePpTiny;
                else if (mode == "ppmed" || mode == "ppmedium") AppConfig.Engine = AppConfig.EnginePpMedium;
                else if (mode == "system") AppConfig.Engine = AppConfig.EngineSystem;

                Bitmap bmp = LoadBitmap(imagePath);
                List<OcrSegment> segs = new List<OcrSegment>();
                string eng; long ms; string det;
                string text = Ocr.Recognize(bmp, out eng, out ms, out det, segs);
                bmp.Dispose();

                StringBuilder sb = new StringBuilder();
                sb.Append("engine=").Append(eng).Append("\r\n");
                sb.Append("ms=").Append(ms).Append("\r\n");
                sb.Append("lines=").Append(segs.Count).Append("\r\n");
                sb.Append("SEG_BEGIN\r\n");
                for (int i = 0; i < segs.Count; i++)
                {
                    Rectangle b = segs[i].Box;
                    sb.Append(i).Append(" @").Append(b.X).Append(',').Append(b.Y)
                      .Append(' ').Append(b.Width).Append('x').Append(b.Height)
                      .Append(" | ").Append(segs[i].Text ?? "").Append("\r\n");
                }
                sb.Append("SEG_END\r\n");
                report = sb.ToString();
            }
            catch (Exception ex) { report = "ERROR: " + ex.ToString(); }
            try { File.WriteAllText(outputPath, report, new UTF8Encoding(false)); } catch (Exception) { }
        }

        private static void OcrSelfTest(string imagePath, string outputPath, string mode)        {
            if (outputPath == null)
            {
                outputPath = Path.Combine(Path.GetDirectoryName(imagePath), "ocrselftest-result.txt");
            }
            string report;
            try
            {
                // 自检也要按 ini 里的设置跑（尤其是 wxdir 手动指定的微信目录）
                AppConfig.Load();
                if (mode == "system") AppConfig.Engine = AppConfig.EngineSystem;
                else if (mode == "wechat") AppConfig.Engine = AppConfig.EngineWeChat;
                else if (mode == "pptiny") AppConfig.Engine = AppConfig.EnginePpTiny;
                else if (mode == "ppsmall") AppConfig.Engine = AppConfig.EnginePpTiny;   // 标准档已下线，并入极速
                else if (mode == "ppmed" || mode == "ppmedium") AppConfig.Engine = AppConfig.EnginePpMedium;

                EngineProbe probe = Ocr.Probe();
                Bitmap bmp = LoadBitmap(imagePath);
                string engine;
                long ms;
                string detail;
                string text = Ocr.Recognize(bmp, out engine, out ms, out detail);
                bmp.Dispose();

                report = "engine=" + engine + "\r\nms=" + ms.ToString()
                    + "\r\nwechatFound=" + (probe.WeChatUsable ? "1" : "0")
                    + "\r\nwechatVerified=" + (probe.WeChatVerified ? "1" : "0")
                    + "\r\nwechatDir=" + probe.WeChatInstallDir
                    + "\r\nplugin=" + probe.PluginDll
                    + "\r\nproblem=" + probe.Problem
                    + "\r\ndetail=" + detail
                    + "\r\nTEXT_BEGIN\r\n" + (text ?? "(null)") + "\r\nTEXT_END\r\n";
            }
            catch (Exception ex)
            {
                report = "ERROR: " + ex.ToString();
            }
            try
            {
                File.WriteAllText(outputPath, report, new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
        }
    }
}
