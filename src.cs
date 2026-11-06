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
[assembly: AssemblyVersion("1.1.4.0")]
[assembly: AssemblyFileVersion("1.1.4.0")]

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
        private static extern bool SetForegroundWindow(IntPtr hWnd);

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

        public static void ActivateWindow(string title)
        {
            IntPtr h = FindWindow(null, title);
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
        public const string ConfigVersion = "1130";
        public const string EngineAuto = "auto";
        public const string EngineSystem = "system";
        public const string FileName = "XuYuan-SnapScan_config.ini";
        public const int DefaultBackOpacity = 70;
        public const int DefaultFrost = 10;

        private static string _activePath = string.Empty;

        public static string ConfigFolder
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XuYuan-SnapScan");
            }
        }

        // 首选：程序同目录的 QRSnap_config.ini（绿色便携）
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

        public static void Load(out string hotkeyQr, out string hotkeyOcr, out bool autoCopy, out string ocrDir,
            out string engine, out int backOpacity, out int frost, out string modelSize, out bool restoreWin, out bool legacy)
        {
            hotkeyQr = DefaultHotkeyQr;
            hotkeyOcr = DefaultHotkeyOcr;
            autoCopy = true;
            ocrDir = string.Empty;
            engine = EngineAuto;
            backOpacity = DefaultBackOpacity;
            frost = DefaultFrost;
            modelSize = "Tiny";
            restoreWin = false;
            legacy = false;
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
                    else if (k.Equals("hotkey", StringComparison.OrdinalIgnoreCase) && v.Length > 0) hotkeyQr = v;
                    else if (k.Equals("hotkey2", StringComparison.OrdinalIgnoreCase) && v.Length > 0) hotkeyOcr = v;
                    else if (k.Equals("autocopy", StringComparison.OrdinalIgnoreCase))
                        autoCopy = (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                    else if (k.Equals("engine", StringComparison.OrdinalIgnoreCase) && v.Length > 0) engine = v;
                    else if (k.Equals("ocrdir", StringComparison.OrdinalIgnoreCase) && v.Length > 0) ocrDir = v;
                    else if (k.Equals("bgopacity", StringComparison.OrdinalIgnoreCase))
                    {
                        int iv;
                        if (int.TryParse(v, out iv) && iv >= 0 && iv <= 100) backOpacity = iv;
                    }
                    else if (k.Equals("frost", StringComparison.OrdinalIgnoreCase))
                    {
                        int iv;
                        if (int.TryParse(v, out iv) && iv >= 0 && iv <= 20) frost = iv;
                    }
                    else if (k.Equals("modelsize", StringComparison.OrdinalIgnoreCase) && v.Length > 0)
                    {
                        string s = v.Trim();
                        if (s == "Tiny" || s == "Small" || s == "Medium") modelSize = s;
                    }
                    else if (k.Equals("restorewin", StringComparison.OrdinalIgnoreCase))
                        restoreWin = (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                }
                legacy = !string.Equals(ver, ConfigVersion, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
            }
        }

        public static void Save(string hotkeyQr, string hotkeyOcr, bool autoCopy, string ocrDir, string engine, int backOpacity, int frost, string modelSize, bool restoreWin)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("ver=").Append(ConfigVersion).Append("\r\n");
            sb.Append("hotkey=").Append(hotkeyQr).Append("\r\n");
            sb.Append("hotkey2=").Append(hotkeyOcr).Append("\r\n");
            sb.Append("autocopy=").Append(autoCopy ? "1" : "0").Append("\r\n");
            sb.Append("engine=").Append(engine == EngineSystem ? EngineSystem : EngineAuto).Append("\r\n");
            sb.Append("ocrdir=").Append(ocrDir).Append("\r\n");
            sb.Append("bgopacity=").Append(backOpacity).Append("\r\n");
            sb.Append("frost=").Append(frost).Append("\r\n");
            sb.Append("modelsize=").Append(modelSize ?? "Tiny").Append("\r\n");
            sb.Append("restorewin=").Append(restoreWin ? "1" : "0").Append("\r\n");

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

    // 文字识别：用 PP-OCRv6（OcrEngine + Python rapidocr），找不到就退到系统自带 OCR
    internal static class Ocr
    {
        public const string RapidExeName = "OcrEngine.exe";
        private const string DetName = "det.onnx";
        private const string RecName = "rec.onnx";
        private const string KeysName = "keys.txt";
        public static readonly string[] Sizes = new string[] { "Tiny", "Small", "Medium" };

        private static readonly Regex CjkSpace = new Regex(
            "(?<=[\\u4e00-\\u9fff\\u3000-\\u303f\\uff00-\\uffef0-9])\\s+(?=[\\u4e00-\\u9fff\\u3000-\\u303f\\uff00-\\uffef0-9])");

        public static string MirrorDir
        {
            get { return Path.Combine(AppConfig.ConfigFolder, "rapid"); }
        }

        // 检查某个规格是否可用：ModelDir\size\det.onnx + rec.onnx + keys.txt
        public static bool IsSizeAvailable(string size)
        {
            try
            {
                if (!File.Exists(Path.Combine(ModelDir, RapidExeName))) return false;
                string d = Path.Combine(ModelDir, size);
                return File.Exists(Path.Combine(d, DetName))
                    && File.Exists(Path.Combine(d, RecName))
                    && File.Exists(Path.Combine(d, KeysName));
            }
            catch (Exception) { return false; }
        }

        // 某个规格的模型目录
        public static string GetSizeDir(string size)
        {
            return Path.Combine(ModelDir, size);
        }

        public static bool IsRapidDir(string dir)
        {
            // 兼容旧结构检查
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                if (!Directory.Exists(dir)) return false;
                if (!File.Exists(Path.Combine(dir, RapidExeName))) return false;
                // 新结构：检查任意一个 size 子目录
                foreach (string s in Sizes)
                {
                    string d = Path.Combine(dir, s);
                    if (File.Exists(Path.Combine(d, DetName)) && File.Exists(Path.Combine(d, RecName)))
                        return true;
                }
                return false;
            }
            catch (Exception) { return false; }
        }

        // 程序自身所在目录
        public static string AppFolder
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        // 唯一的模型目录：exe 同目录下名为 "OCR Model" 的文件夹
        public static string ModelDir
        {
            get { return Path.Combine(AppFolder, "OCR Model"); }
        }

        // 根据规格返回可用的模型目录
        public static string GetUpstream(string preferred, string size)
        {
            if (IsSizeAvailable(size)) return GetSizeDir(size);
            // 回退：找第一个可用规格
            foreach (string s in Sizes)
                if (IsSizeAvailable(s)) return GetSizeDir(s);
            return null;
        }

        // 「导入模型」：把一份完整的模型目录拷进 OCR Model，之后程序只读这一处
        public static bool ImportIntoModels(string src, out string note, out int files, out long bytes)
        {
            note = string.Empty;
            files = 0;
            bytes = 0;
            if (!IsRapidDir(src))
            {
                note = "源目录不完整：需要 OcrEngine.exe 与 models 下的 3 个模型文件";
                return false;
            }
            try
            {
                string dst = ModelDir;
                Directory.CreateDirectory(Path.Combine(dst, "models"));
                CopyAll(src, dst, ref bytes, ref files);
                note = "已导入 " + files.ToString() + " 个文件，" + (bytes / 1048576.0).ToString("0.0") + " MB";
                return IsRapidDir(dst);
            }
            catch (Exception ex)
            {
                note = "导入失败：" + ex.Message;
                return false;
            }
        }

        public static string DescribeDir(string dir)
        {
            if (IsRapidDir(dir))
            {
                return dir + "\r\n（含 OcrEngine.exe 与 models 下 4 个模型文件，完整）";
            }
            return dir + "\r\n（缺少 exe 或模型文件，不完整）";
        }

        private static long FileLength(string p)
        {
            try
            {
                FileInfo fi = new FileInfo(p);
                return fi.Exists ? fi.Length : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static DateTime FileTime(string p)
        {
            try
            {
                FileInfo fi = new FileInfo(p);
                return fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        // 上游模型比镜像新（或镜像还不完整）就需要同步
        private static bool NeedRefresh(string upstream, string mirror)
        {
            if (!IsRapidDir(mirror)) return true;
            string[] names = new string[]
            {
                RapidExeName,
                Path.Combine("models", DetName),
                Path.Combine("models", RecName),
                Path.Combine("models", KeysName)
            };
            foreach (string name in names)
            {
                DateTime up = FileTime(Path.Combine(upstream, name));
                DateTime mi = FileTime(Path.Combine(mirror, name));
                if (up > mi.AddSeconds(1)) return true;
            }
            return false;
        }

        private static void CopyFile(string from, string to, ref long bytes, ref int files)
        {
            string dir = Path.GetDirectoryName(to);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.Copy(from, to, true);
            files++;
            bytes += FileLength(to);
        }

        private static void CopyAll(string upstream, string mirror, ref long bytes, ref int files)
        {
            string modelsSrc = Path.Combine(upstream, "models");
            string modelsDst = Path.Combine(mirror, "models");
            if (!Directory.Exists(modelsDst)) Directory.CreateDirectory(modelsDst);

            CopyFile(Path.Combine(upstream, RapidExeName), Path.Combine(mirror, RapidExeName), ref bytes, ref files);
            CopyFile(Path.Combine(modelsSrc, DetName), Path.Combine(modelsDst, DetName), ref bytes, ref files);
            CopyFile(Path.Combine(modelsSrc, RecName), Path.Combine(modelsDst, RecName), ref bytes, ref files);
            CopyFile(Path.Combine(modelsSrc, KeysName), Path.Combine(modelsDst, KeysName), ref bytes, ref files);
        }

        private static bool IsAscii(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] > 127) return false;
            }
            return true;
        }

        // OcrEngine 只认纯英文路径：中文目录先镜像到 %APPDATA%\XuYuan-SnapScan\rapid 再用
        public static string PrepareRapidDir(string sizeDir, out string note)
        {
            note = string.Empty;
            if (!Directory.Exists(sizeDir)) return null;
            if (IsAscii(sizeDir)) return sizeDir;

            string mirror = MirrorDir;
            try
            {
                Directory.CreateDirectory(mirror);
                string[] files = new string[] { DetName, RecName, KeysName };
                foreach (string f in files)
                {
                    string src = Path.Combine(sizeDir, f);
                    string dst = Path.Combine(mirror, f);
                    if (File.Exists(src)) File.Copy(src, dst, true);
                }
                note = "已镜像模型到 " + mirror;
                return mirror;
            }
            catch (Exception ex)
            {
                note = "镜像模型失败：" + ex.Message;
                return null;
            }
        }

        // 「更新上游算法」按钮用：无条件把上游最新的一份覆盖到本地
        public static bool SyncFromUpstream(string upstream, out string note, out int files, out long bytes, out long ms)
        {
            files = 0;
            bytes = 0;
            ms = 0;
            note = string.Empty;

            if (!IsRapidDir(upstream))
            {
                note = "上游目录不完整：需要 OcrEngine.exe 与 models 下 4 个模型文件";
                return false;
            }
            if (IsAscii(upstream))
            {
                note = "上游已在英文路径，直接使用，无需镜像";
                return true;
            }

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                long b = 0;
                int f = 0;
                CopyAll(upstream, MirrorDir, ref b, ref f);
                bytes = b;
                files = f;
                ms = sw.ElapsedMilliseconds;
                note = "已更新 " + f.ToString() + " 个文件，" + (b / 1048576.0).ToString("0.0") + " MB";
                return IsRapidDir(MirrorDir);
            }
            catch (Exception ex)
            {
                ms = sw.ElapsedMilliseconds;
                note = "更新失败：" + ex.Message;
                return false;
            }
        }

        public static string Recognize(Bitmap bitmap, string rapidDir, bool forceSystem,
            out string engine, out long ms, out string detail)
        {
            engine = "系统 OCR";
            ms = 0;
            detail = string.Empty;

            string png = null;
            try
            {
                string tmpDir = Path.Combine(Path.GetTempPath(), "XuYuan-SnapScan");
                Directory.CreateDirectory(tmpDir);
                png = Path.Combine(tmpDir, "ocr" + DateTime.Now.Ticks.ToString() + ".png");
                bitmap.Save(png, ImageFormat.Png);

                if (!forceSystem && rapidDir != null)
                {
                    string note;
                    string prepared = PrepareRapidDir(rapidDir, out note);
                    if (note.Length > 0) detail = note;
                    if (prepared != null)
                    {
                        string text = RunRapid(prepared, png, out ms);
                        if (text != null)
                        {
                            engine = "PP-OCRv6";
                            return text;
                        }
                        detail = "PP-OCR 超时或失败，已改用系统 OCR";
                    }
                }

                string sys = RunSystemOcr(png, out ms);
                engine = "系统 OCR";
                if (sys == null) return null;
                return NormalizeSystemText(sys);
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
                    try { File.Delete(png + "-result.txt"); } catch (Exception) { }
                    try { File.Delete(png + ".ocr.txt"); } catch (Exception) { }
                }
            }
        }

        private static string RunRapid(string sizeDir, string pngPath, out long ms)
        {
            ms = 0;
            // exe 在 ModelDir 根目录，模型在 sizeDir
            string exePath = Path.Combine(ModelDir, RapidExeName);
            string models = sizeDir;
            string resultFile = pngPath + "-result.txt";
            try { if (File.Exists(resultFile)) File.Delete(resultFile); }
            catch (Exception) { }

            Stopwatch sw = Stopwatch.StartNew();
            Process proc = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exePath);
                psi.Arguments = "-d \"" + models + "\" -1 " + DetName + " -3 " + RecName
                    + " -4 " + KeysName + " -i \"" + pngPath + "\"";
                psi.WorkingDirectory = ModelDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                proc = Process.Start(psi);
                if (proc == null) return null;

                // 读空 stdout / stderr，避免管道写满把子进程卡住
                Thread drain1 = new Thread(new ParameterizedThreadStart(Drain));
                drain1.IsBackground = true;
                drain1.Start(proc.StandardOutput);
                Thread drain2 = new Thread(new ParameterizedThreadStart(Drain));
                drain2.IsBackground = true;
                drain2.Start(proc.StandardError);

                // 这个 exe 跑完不会自己退出，只能盯着结果文件
                while (sw.ElapsedMilliseconds < 25000)
                {
                    if (File.Exists(resultFile))
                    {
                        long len = new FileInfo(resultFile).Length;
                        if (len > 0)
                        {
                            Thread.Sleep(120);
                            string text = File.ReadAllText(resultFile, Encoding.UTF8);
                            ms = sw.ElapsedMilliseconds;
                            return text;
                        }
                    }
                    if (proc.HasExited) break;
                    Thread.Sleep(60);
                }
                ms = sw.ElapsedMilliseconds;
                return null;
            }
            catch (Exception)
            {
                ms = sw.ElapsedMilliseconds;
                return null;
            }
            finally
            {
                KillProcess(proc);
            }
        }

        private static void Drain(object streamObj)
        {
            try
            {
                StreamReader r = (StreamReader)streamObj;
                while (r.ReadLine() != null) { }
            }
            catch (Exception)
            {
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

        public Rectangle Selection = Rectangle.Empty;
        public bool Done;
        public bool Confirmed;
        public Bitmap Crop;

        public void Run()
        {
            try
            {
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

                OverlayForm focus = PickFocusForm();
                if (focus != null) Native.BringToFront(focus.Handle);

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

                if (Confirmed) Crop = MakeCrop(Selection);

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
            Confirmed = true;
            Done = true;
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

        public MonitorShot Shot { get { return _shot; } }

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

            if (_shot.Bounds.Contains(Cursor.Position))
            {
                DrawHint(g, sel);
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
            string hint = (sel.Width > MIN_SIZE)
                ? "松开鼠标开始识别    Esc 取消"
                : "拖动鼠标框选要识别的内容    Esc / 右键 取消";
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
                _session.Selection = MakeRect(_anchor, p);
                _session.InvalidateAll();
            }
            else
            {
                // 提示条跟随鼠标所在的那块屏
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!_down)
            {
                base.OnMouseUp(e);
                return;
            }
            _down = false;

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
        private const int STATE_IDLE = 0;
        private const int STATE_BUSY = 1;
        private const int STATE_DONE = 2;

        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const int PAD_X = 16;
        private const int ICON_SIZE = 18;
        private const int GAP = 10;
        private const int BOX_H = 46;

        private readonly System.Windows.Forms.Timer _timer;
        private float _angle;
        private int _state;
        private string _text = string.Empty;
        private bool _ok = true;
        private int _holdMs;

        public ToastForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(30, 36, 48);
            DoubleBuffered = true;
            Opacity = 0.95;
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 60;
            _timer.Tick += OnTick;
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
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        public void ShowBusy(string text)
        {
            SetText(text);
            _state = STATE_BUSY;
            _ok = true;
            _holdMs = 0;
            _angle = 0f;
            PlaceToast();
            if (!Visible) Show();
            BringToFront();
            _timer.Start();
            Invalidate();
        }

        public void ShowDone(string text, bool ok)
        {
            SetText(text);
            _state = STATE_DONE;
            _ok = ok;
            _holdMs = 1700;
            PlaceToast();
            if (!Visible) Show();
            BringToFront();
            _timer.Start();
            Invalidate();
        }

        public void HideToast()
        {
            _state = STATE_IDLE;
            _timer.Stop();
            if (Visible) Hide();
        }

        private void SetText(string text)
        {
            _text = text;
            Size size = TextRenderer.MeasureText(text, Font);
            int w = PAD_X * 2 + ICON_SIZE + GAP + size.Width + 8;
            if (w < 150) w = 150;
            Size = new Size(w, BOX_H);
        }

        private void PlaceToast()
        {
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Left = wa.Right - Width - 28;
            Top = wa.Bottom - Height - 104;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_state == STATE_BUSY)
            {
                _angle += 15f;
                if (_angle > 360f) _angle -= 360f;
                Invalidate();
                return;
            }
            if (_state == STATE_DONE)
            {
                _holdMs -= _timer.Interval;
                if (_holdMs <= 0) HideToast();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int r = 10;
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

            using (SolidBrush bg = new SolidBrush(Color.FromArgb(30, 36, 48)))
            {
                g.FillRectangle(bg, ClientRectangle);
            }

            int cy = (Height - ICON_SIZE) / 2;
            int cx = PAD_X;

            if (_state == STATE_BUSY)
            {
                using (Pen track = new Pen(Color.FromArgb(74, 82, 100), 2F))
                {
                    g.DrawEllipse(track, cx, cy, ICON_SIZE - 2, ICON_SIZE - 2);
                }
                using (Pen arc = new Pen(Color.FromArgb(96, 165, 250), 2F))
                {
                    arc.StartCap = LineCap.Round;
                    arc.EndCap = LineCap.Round;
                    g.DrawArc(arc, cx, cy, ICON_SIZE - 2, ICON_SIZE - 2, _angle, 110f);
                }
            }
            else
            {
                Color c = _ok ? Color.FromArgb(52, 211, 153) : Color.FromArgb(248, 113, 113);
                using (Pen pen = new Pen(c, 2.2F))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    float bx = cx + 1f;
                    float by = cy + 1f;
                    if (_ok)
                    {
                        g.DrawLines(pen, new PointF[]
                        {
                            new PointF(bx + 1f, by + 8f),
                            new PointF(bx + 5.5f, by + 12.5f),
                            new PointF(bx + 13f, by + 3f)
                        });
                    }
                    else
                    {
                        g.DrawLines(pen, new PointF[]
                        {
                            new PointF(bx + 3f, by + 3f),
                            new PointF(bx + 12f, by + 13f)
                        });
                        g.DrawLines(pen, new PointF[]
                        {
                            new PointF(bx + 12f, by + 3f),
                            new PointF(bx + 3f, by + 13f)
                        });
                    }
                }
            }

            using (SolidBrush fg = new SolidBrush(Color.FromArgb(240, 243, 248)))
            {
                g.DrawString(_text, Font, fg, (float)(cx + ICON_SIZE + GAP), (float)((Height - Font.Height) / 2));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
                Font.Dispose();
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

        private readonly Label _lblResult;
        private string _resultText = "";
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

        private string _upstreamDir;
        private string _modelSize = "Tiny";
        private bool _restoreWin = false;
        private string _ocrDirCfg = string.Empty;
        private string _engineMode = AppConfig.EngineAuto;
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

            Label capHint = MakeLabel("双击结果可全选 · Ctrl+A 全选", 8F, FaintInk, ContentAlignment.MiddleRight, false);
            capHint.Dock = DockStyle.Right;
            capHint.Width = 220;
            capHint.Margin = new Padding(0);

            head.Controls.Add(capResult);
            head.Controls.Add(capHint);

            _lblResult = new Label();
            _lblResult.Dock = DockStyle.Fill;
            _lblResult.BackColor = Color.Transparent;
            _lblResult.ForeColor = Ink;
            _lblResult.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Regular, GraphicsUnit.Point);
            _lblResult.TextAlign = ContentAlignment.TopLeft;
            _lblResult.AutoEllipsis = false;
            _lblResult.Margin = new Padding(4, 2, 4, 4);
            _lblResult.DoubleClick += delegate { if (_resultText.Length > 0) { try { Clipboard.SetText(_resultText); } catch {} } };

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
            lblVer.Text = "v1.1.4.0  ·  约 710 KB  ·  免安装  ·  不自启";
            lblVer.Dock = DockStyle.Right;
            lblVer.Width = 210;
            lblVer.TextAlign = ContentAlignment.MiddleRight;
            lblVer.BackColor = Color.Transparent;
            lblVer.ForeColor = FaintInk;
            lblVer.Font = new Font(Font.FontFamily, 7.5F, FontStyle.Regular, GraphicsUnit.Point);

            statusRow.Controls.Add(_lblStatus);
            statusRow.Controls.Add(lblVer);

            resRows.Controls.Add(head, 0, 0);
            resRows.Controls.Add(_lblResult, 0, 1);
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

        private void ResultKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                _resultText = "";
                _lblResult.Text = "";
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
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

            string hkQr;
            string hkOcr;
            bool autoCopy;
            string ocrDir;
            string engine;
            int backOpacity;
            int frost;
            string modelSize;
            bool restoreWin;
            bool legacy;
            AppConfig.Load(out hkQr, out hkOcr, out autoCopy, out ocrDir, out engine, out backOpacity, out frost, out modelSize, out restoreWin, out legacy);
            _ocrDirCfg = ocrDir;
            _modelSize = modelSize;
            _restoreWin = restoreWin;
            _autoCopy = autoCopy;
            _backOpacity = backOpacity;
            _frost = frost;
            GlassAlpha = (int)(_backOpacity * 2.55);
            RebuildBlurred();
            _engineMode = engine == AppConfig.EngineSystem ? AppConfig.EngineSystem : AppConfig.EngineAuto;
            Invalidate(true);

            // 新版只认 exe 同目录的 OCR Model，老配置里指向别处的 ocr 路径作废
            if (legacy && !string.Equals(_ocrDirCfg.TrimEnd('\\'), Ocr.ModelDir.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
            {
                _ocrDirCfg = string.Empty;
            }

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

            _upstreamDir = Ocr.GetUpstream(_ocrDirCfg, _modelSize);
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

            if (_qrRegistered && _ocrRegistered)
            {
                string status = "就绪 · 识码 " + HotkeyText.Format(_modsQr, _vkQr) + " / 识字 " + HotkeyText.Format(_modsOcr, _vkOcr);
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
            // _lblResult.Focus() not needed

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
        private void UpdateEngineLabels()
        {
            bool systemOnly = (_engineMode == AppConfig.EngineSystem);

            if (systemOnly)
            {
                _lblEngine.Text = "系统 OCR · 已选择";
                _lblEngine.ForeColor = Color.FromArgb(154, 116, 36);
                _lblPath.Text = _upstreamDir != null
                    ? "PP-OCRv6 已就绪，可在设置里切回自动"
                    : "模型未安装（不影响使用）";
                _lblPath.ForeColor = FaintInk;
                return;
            }

            if (_upstreamDir != null)
            {
                _lblEngine.Text = "PP-OCRv6 " + _modelSize + " · 已就绪";
                _lblEngine.ForeColor = Color.FromArgb(22, 138, 74);
                _lblPath.Text = "模型：" + _modelSize + " 规格（exe 同目录 OCR Model\\" + _modelSize + "）";
                _lblPath.ForeColor = FaintInk;
            }
            else
            {
                _lblEngine.Text = "系统内置 OCR · 自动降级";
                _lblEngine.ForeColor = Color.FromArgb(154, 116, 36);
                _lblPath.Text = "没找到 OCR Model，设置里可导入";
                _lblPath.ForeColor = FaintInk;
            }
        }

        // 设置页改完引擎 / 模型后回来刷新
        internal void RefreshEngineState(string ocrDir, string engineMode, bool autoCopy)
        {
            _ocrDirCfg = ocrDir;
            _engineMode = engineMode == AppConfig.EngineSystem ? AppConfig.EngineSystem : AppConfig.EngineAuto;
            _autoCopy = autoCopy;
            _upstreamDir = Ocr.GetUpstream(_ocrDirCfg, _modelSize);
            if (_engineMode == AppConfig.EngineSystem) _upstreamDir = null;
            UpdateEngineLabels();
            SaveConfig();
        }

        internal void SetModelSize(string size)
        {
            if (size == "Tiny" || size == "Small" || size == "Medium")
            {
                _modelSize = size;
                _upstreamDir = Ocr.GetUpstream(_ocrDirCfg, _modelSize);
                if (_engineMode == AppConfig.EngineSystem) _upstreamDir = null;
                UpdateEngineLabels();
                SaveConfig();
            }
        }

        internal void SaveConfig()
        {
            AppConfig.Save(HotkeyText.Format(_modsQr, _vkQr), HotkeyText.Format(_modsOcr, _vkOcr),
                _autoCopy, _ocrDirCfg, _engineMode, _backOpacity, _frost, _modelSize, _restoreWin);
        }

        private void RestoreWindowIfNeeded()
        {
            if (!_restoreWin) return;
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
                Activate();
            }
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
        internal string EngineMode { get { return _engineMode; } }
        internal string ModelSize { get { return _modelSize; } }
        internal string OcrDirCfg { get { return _ocrDirCfg; } }

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

            _resultText = text;
            _lblResult.Text = text;
            RestoreWindowIfNeeded();

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
            SetStatus(_upstreamDir != null
                ? "正在识别文字…（PP-OCRv6，约 8 秒）"
                : "正在识别文字…（系统 OCR）");
            _toast.ShowBusy(_upstreamDir != null ? "识别中…  文字（PP-OCRv6）" : "识别中…  文字（系统 OCR）");

            string upstream = _upstreamDir;
            Thread worker = new Thread(new ThreadStart(delegate
            {
                string text = null;
                string engine = string.Empty;
                string detail = string.Empty;
                long elapsed = 0;
                string error = null;
                try
                {
                    text = Ocr.Recognize(crop, upstream, _engineMode == AppConfig.EngineSystem, out engine, out elapsed, out detail);
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

            _resultText = text;
            _lblResult.Text = text;
            RestoreWindowIfNeeded();

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
            string text = _resultText;
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
            _resultText = "";
            _lblResult.Text = "";
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

    // 设置二级页：引擎选择 / 模型目录 / 快捷键 / 自动复制 / 配置路径
    internal sealed class SettingsForm : Form
    {
        private readonly MainForm _owner;
        private readonly RadioButton _rdoAuto;
        private readonly RadioButton _rdoSystem;
        private readonly ComboBox _cboSize;
        private readonly Label _lblNow;
        private readonly Label _lblModel;
        private readonly TextBox _boxQr;
        private readonly TextBox _boxOcr;
        private readonly CheckBox _chkCopy;
        private readonly CheckBox _chkRestore;
        private readonly Label _lblCfg;
        private readonly TrackBar _barOpacity;
        private readonly Label _lblOpacity;
        private readonly TrackBar _barFrost;
        private readonly Label _lblFrost;
        private bool _loading = true;

        public SettingsForm(MainForm owner)
        {
            _owner = owner;

            Text = "XuYuan-SnapScan 设置";
            AutoScroll = true;
            AutoScrollMinSize = new Size(480, 790);
            ClientSize = new Size(480, 790);
            // 屏幕不够高时缩小窗口并自动滚动
            Screen scr = Screen.FromPoint(Cursor.Position);
            int maxH = scr.WorkingArea.Height - 40;
            if (ClientSize.Height > maxH) ClientSize = new Size(480, maxH);
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
            cardEng.Size = new Size(440, 232);

            Label capEng = new Label();
            capEng.Text = "文字引擎";
            capEng.Location = new Point(16, 12);
            capEng.AutoSize = true;
            capEng.ForeColor = MainForm.SubInk;
            capEng.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold, GraphicsUnit.Point);

            _rdoAuto = new RadioButton();
            _rdoAuto.Text = "自动：优先 PP-OCRv6，没有模型时用系统 OCR";
            _rdoAuto.Location = new Point(14, 36);
            _rdoAuto.AutoSize = true;
            _rdoAuto.ForeColor = MainForm.Ink;
            _rdoAuto.Font = new Font(Font.FontFamily, 9F, FontStyle.Regular, GraphicsUnit.Point);

            _rdoSystem = new RadioButton();
            _rdoSystem.Text = "只用系统 OCR：1~2 秒出结果，中文有错字";
            _rdoSystem.Location = new Point(14, 62);
            _rdoSystem.AutoSize = true;
            _rdoSystem.ForeColor = MainForm.Ink;
            _rdoSystem.Font = new Font(Font.FontFamily, 9F, FontStyle.Regular, GraphicsUnit.Point);

            Label capSize = new Label();
            capSize.Text = "模型规格";
            capSize.Location = new Point(16, 92);
            capSize.AutoSize = true;
            capSize.ForeColor = MainForm.SubInk;
            capSize.Font = new Font(Font.FontFamily, 8.5F, FontStyle.Regular, GraphicsUnit.Point);

            _cboSize = new ComboBox();
            _cboSize.Location = new Point(80, 88);
            _cboSize.Size = new Size(180, 24);
            _cboSize.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboSize.FlatStyle = FlatStyle.Flat;
            foreach (string s in Ocr.Sizes)
            {
                bool avail = Ocr.IsSizeAvailable(s);
                string label = s + (avail ? "" : "  (未找到)");
                _cboSize.Items.Add(label);
            }
            _cboSize.SelectedIndexChanged += delegate { if (!_loading) ApplySize(); };

            _lblNow = new Label();
            _lblNow.Location = new Point(16, 120);
            _lblNow.Size = new Size(406, 20);
            _lblNow.ForeColor = MainForm.SubInk;
            _lblNow.Font = new Font(Font.FontFamily, 8.5F, FontStyle.Bold, GraphicsUnit.Point);

            _lblModel = new Label();
            _lblModel.Location = new Point(16, 140);
            _lblModel.Size = new Size(406, 18);
            _lblModel.ForeColor = MainForm.FaintInk;
            _lblModel.Font = new Font(Font.FontFamily, 7.5F, FontStyle.Regular, GraphicsUnit.Point);
            _lblModel.AutoEllipsis = true;

            Button btnImport = MakeSmallButton("导入模型", new Point(16, 172), new Size(96, 28), true);
            btnImport.Click += delegate { ImportModels(); };

            Button btnOpen = MakeSmallButton("打开模型目录", new Point(120, 172), new Size(116, 28), false);
            btnOpen.Click += delegate { OpenModelDir(); };

            Button btnPick = MakeSmallButton("指定别的目录", new Point(244, 172), new Size(110, 28), false);
            btnPick.Click += delegate { ChooseModelDir(); };

            cardEng.Controls.Add(capEng);
            cardEng.Controls.Add(_rdoAuto);
            cardEng.Controls.Add(_rdoSystem);
            cardEng.Controls.Add(capSize);
            cardEng.Controls.Add(_cboSize);
            cardEng.Controls.Add(_lblNow);
            cardEng.Controls.Add(_lblModel);
            cardEng.Controls.Add(btnImport);
            cardEng.Controls.Add(btnOpen);
            cardEng.Controls.Add(btnPick);

            // ---------- 卡片二：快捷键 ----------
            Card cardKeys = new Card();
            cardKeys.Location = new Point(20, 288);
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
            cardBg.Location = new Point(20, 436);
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

            // ---------- 卡片四：行为与配置 ----------
            Card cardMisc = new Card();
            cardMisc.Location = new Point(20, 600);
            cardMisc.Size = new Size(440, 130);

            _chkCopy = new CheckBox();
            _chkCopy.Text = "识别后自动复制到剪贴板";
            _chkCopy.Location = new Point(14, 12);
            _chkCopy.AutoSize = true;
            _chkCopy.ForeColor = MainForm.Ink;
            _chkCopy.Checked = _owner.AutoCopyEnabled;
            _chkCopy.CheckedChanged += delegate
            {
                if (_loading) return;
                _owner.RefreshEngineState(_owner.OcrDirCfg, CurrentMode(), _chkCopy.Checked);
            };

            _chkRestore = new CheckBox();
            _chkRestore.Text = "识别后恢复窗口（默认最小化不恢复）";
            _chkRestore.Location = new Point(14, 38);
            _chkRestore.AutoSize = true;
            _chkRestore.ForeColor = MainForm.Ink;
            _chkRestore.Checked = _owner.RestoreWinEnabled;
            _chkRestore.CheckedChanged += delegate
            {
                if (_loading) return;
                _owner.RestoreWinEnabled = _chkRestore.Checked;
                _owner.SaveConfig();
            };

            Label capCfg = new Label();
            capCfg.Text = "配置文件";
            capCfg.Location = new Point(16, 74);
            capCfg.AutoSize = true;
            capCfg.ForeColor = MainForm.SubInk;
            capCfg.Font = new Font(Font.FontFamily, 8.5F, FontStyle.Bold, GraphicsUnit.Point);

            _lblCfg = new Label();
            _lblCfg.Location = new Point(16, 96);
            _lblCfg.Size = new Size(320, 18);
            _lblCfg.ForeColor = MainForm.FaintInk;
            _lblCfg.Font = new Font(Font.FontFamily, 7.5F, FontStyle.Regular, GraphicsUnit.Point);
            _lblCfg.AutoEllipsis = true;
            _lblCfg.Text = AppConfig.ConfigPath;

            Button btnFolder = MakeSmallButton("所在目录", new Point(340, 92), new Size(88, 26), false);
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
            cardMisc.Controls.Add(_chkRestore);
            cardMisc.Controls.Add(capCfg);
            cardMisc.Controls.Add(_lblCfg);
            cardMisc.Controls.Add(btnFolder);

            Button btnClose = MakeSmallButton("关闭", new Point(376, 740), new Size(84, 30), true);
            btnClose.Click += delegate { Close(); };

            Controls.Add(title);
            Controls.Add(sub);
            Controls.Add(cardEng);
            Controls.Add(cardKeys);
            Controls.Add(cardBg);
            Controls.Add(cardMisc);
            Controls.Add(btnClose);

            _rdoAuto.Checked = _owner.EngineMode != AppConfig.EngineSystem;
            _rdoSystem.Checked = !_rdoAuto.Checked;
            // 选中当前规格
            string curSize = _owner.ModelSize;
            for (int i = 0; i < Ocr.Sizes.Length; i++)
            {
                if (Ocr.Sizes[i] == curSize) { _cboSize.SelectedIndex = i; break; }
            }
            if (_cboSize.SelectedIndex < 0) _cboSize.SelectedIndex = 0;
            _boxQr.Text = HotkeyText.Format(_owner.HotkeyMods(true), _owner.HotkeyVk(true));
            _boxOcr.Text = HotkeyText.Format(_owner.HotkeyMods(false), _owner.HotkeyVk(false));

            _loading = false;

            _rdoAuto.CheckedChanged += delegate { if (!_loading) ApplyEngine(); };
            _rdoSystem.CheckedChanged += delegate { if (!_loading) ApplyEngine(); };

            RefreshState();
        }

        private string CurrentMode()
        {
            return _rdoSystem.Checked ? AppConfig.EngineSystem : AppConfig.EngineAuto;
        }

        private void ApplyEngine()
        {
            _owner.RefreshEngineState(_owner.OcrDirCfg, CurrentMode(), _chkCopy.Checked);
            RefreshState();
        }

        private void ApplySize()
        {
            int idx = _cboSize.SelectedIndex;
            if (idx >= 0 && idx < Ocr.Sizes.Length)
            {
                _owner.SetModelSize(Ocr.Sizes[idx]);
                _owner.RefreshEngineState(_owner.OcrDirCfg, CurrentMode(), _chkCopy.Checked);
            }
            RefreshState();
        }

        private void RefreshState()
        {
            string size = _owner.ModelSize;
            string upstream = Ocr.GetUpstream(_owner.OcrDirCfg, size);
            if (upstream != null)
            {
                _lblNow.Text = "当前：PP-OCRv6 可用";
                _lblNow.ForeColor = Color.FromArgb(22, 138, 74);
                _lblModel.Text = "模型：" + upstream;
            }
            else
            {
                _lblNow.Text = "当前：未找到模型，用系统 OCR";
                _lblNow.ForeColor = Color.FromArgb(154, 116, 36);
                _lblModel.Text = "把 OcrEngine.exe + models 放进 " + Ocr.ModelDir;
            }
            if (CurrentMode() == AppConfig.EngineSystem)
            {
                _lblNow.Text = upstream != null ? "当前：只用系统 OCR（PP-OCRv6 已闲置）" : "当前：只用系统 OCR";
            }
            _lblCfg.Text = AppConfig.ConfigPath;
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

        private void ImportModels()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选一份完整的模型目录（要有 OcrEngine.exe 和 models 文件夹），"
                    + "会整体复制到 " + Ocr.ModelDir;
                if (Directory.Exists(Ocr.ModelDir)) dlg.SelectedPath = Ocr.ModelDir;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string src = dlg.SelectedPath;
                if (!Ocr.IsRapidDir(src))
                {
                    MessageBox.Show(this,
                        "这个目录不完整。需要：OcrEngine.exe\r\n以及 models\\ch_PP-OCRv6_det_infer.onnx、"
                        + "det.onnx、rec.onnx、keys.txt",
                        "目录里没有模型", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Cursor old = Cursor;
                Cursor = Cursors.WaitCursor;
                string note;
                int files;
                long bytes;
                bool ok;
                try
                {
                    ok = Ocr.ImportIntoModels(src, out note, out files, out bytes);
                }
                catch (Exception ex)
                {
                    ok = false;
                    note = ex.Message;
                }
                finally
                {
                    Cursor = old;
                }

                _owner.RefreshEngineState(string.Empty, CurrentMode(), _chkCopy.Checked);
                RefreshState();

                if (ok)
                {
                    MessageBox.Show(this, "模型已导入到 OCR Model。\r\n" + note, "完成",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this, note, "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void OpenModelDir()
        {
            try
            {
                if (!Directory.Exists(Ocr.ModelDir)) Directory.CreateDirectory(Ocr.ModelDir);
                Process.Start("explorer.exe", "\"" + Ocr.ModelDir + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开文件夹失败：" + ex.Message, "打开失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ChooseModelDir()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "指定另一个模型目录（同样要满足 OcrEngine.exe + PP-OCRv6 模型）";
                if (Directory.Exists(Ocr.ModelDir)) dlg.SelectedPath = Ocr.ModelDir;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                if (!Ocr.IsRapidDir(dlg.SelectedPath))
                {
                    MessageBox.Show(this, "这个目录里没有 OcrEngine.exe 或 PP-OCRv6 模型，不能用。",
                        "目录不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _owner.RefreshEngineState(dlg.SelectedPath, CurrentMode(), _chkCopy.Checked);
                RefreshState();
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;

            // 自检：QRSnap.exe --selftest 图片路径 [结果输出路径]
            if (args.Length >= 2 && args[0] == "--selftest")
            {
                SelfTest(args[1], args.Length >= 3 ? args[2] : null);
                return;
            }

            // 文字识别自检：QRSnap.exe --ocrselftest 图片路径 [结果输出路径] [auto|rapid|system]
            if (args.Length >= 2 && args[0] == "--ocrselftest")
            {
                OcrSelfTest(args[1], args.Length >= 3 ? args[2] : null, args.Length >= 4 ? args[3] : "auto");
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
                    if (step == 1) toast.ShowBusy("识别中…  文字（PP-OCRv6）");
                    if (step == 6) toast.ShowDone("识别完成", true);
                    if (step == 10)
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

        private static void OcrSelfTest(string imagePath, string outputPath, string mode)
        {
            if (outputPath == null)
            {
                outputPath = Path.Combine(Path.GetDirectoryName(imagePath), "ocrselftest-result.txt");
            }
            string report;
            try
            {
                string upstream = (mode == "system") ? null : Ocr.GetUpstream(string.Empty, "Tiny");

                Bitmap bmp = LoadBitmap(imagePath);
                string engine;
                long ms;
                string detail;
                string text = Ocr.Recognize(bmp, upstream, mode == "system", out engine, out ms, out detail);
                bmp.Dispose();

                report = "engine=" + engine + "\r\nms=" + ms.ToString() + "\r\nupstream="
                    + (upstream == null ? "(none)" : upstream) + "\r\nmirror=" + Ocr.MirrorDir + "\r\ndetail=" + detail
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
