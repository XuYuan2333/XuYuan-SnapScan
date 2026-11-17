using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace QRSnap
{
    // 原地译文覆盖层：铺在框选的那块屏幕上，点击穿透、不抢焦点，
    // 每条译文画在原文的位置，字号自适应区域，倾斜行按同角度画，
    // 背景只垫在文字后面，不铺满整个选框。Esc 关闭（低阶键盘钩子，不用抢焦点）。
    internal sealed class TransOverlay : Form
    {
        public sealed class Item
        {
            public Rectangle Box;      // 相对覆盖层的坐标（底板范围）
            public Rectangle TextBox;  // 译文排版范围（段落块：从徽章右侧起排）；空 = 用 Box
            public List<Rectangle> MaskRects;  // 段落块：逐行底板，避免盖住徽章/图标
            public Color BgColor;      // 底板颜色（取原图底色，空 = 默认深色）
            public Color TextColor;    // 译文颜色（按底色明暗自动选）
            public float Angle;        // 原文倾角（度）
            public string Text;        // 译文
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int CX, CY; public SIZE(int x, int y) { CX = x; CY = y; } }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        private const uint WDA_NONE = 0x00000000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

        // 遮罩从截图里排除后，游戏模式不用每帧隐藏/恢复遮罩，避免闪屏
        public static bool CaptureExcluded { get; private set; }

        // QRSNAP_DBG_ZONE=1：分区遮罩的建窗/显隐/推图全过程都落日志，排查「第二次不出现」用
        internal static bool DbgZone
        {
            get { return Environment.GetEnvironmentVariable("QRSNAP_DBG_ZONE") == "1"; }
        }

        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

        // 句柄死了就说明这个 Form 对象已经废了（建它的线程退出了，系统把窗口销毁了）：
        // 这种对象再 Visible=true / UpdateLayeredWindow 都只会静默失败（实测 1400），必须换一个新的。
        private static bool Dead(Form f)
        {
            if (f == null || f.IsDisposed) return true;
            try
            {
                if (!f.IsHandleCreated) return false;
                return !IsWindow(f.Handle);
            }
            catch (Exception) { return true; }
        }

        private static void PinTop(Form f)
        {
            if (f == null || !f.IsHandleCreated) return;
            try { SetWindowPos(f.Handle, (IntPtr)(-1), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW); }
            catch (Exception) { }
        }

        // 注意：WDA_EXCLUDEFROMCAPTURE 对 UpdateLayeredWindow 的分层窗口会连屏幕合成一起剔除，
        // 遮罩会彻底看不见（实测踩过）。所以这里只清成 WDA_NONE，
        // 采样改成「短暂隐藏再抓」的轻量方式，隐藏时长压到个位数毫秒。
        private static void ExcludeCapture(Form f)
        {
            if (f == null || !f.IsHandleCreated) return;
            int err = 0;
            try { SetWindowDisplayAffinity(f.Handle, WDA_NONE); }
            catch (Exception) { err = 1; }
            CaptureExcluded = false;
            try { JadeHost.Log("[overlay] affinity=none err=" + err); } catch (Exception) { }
        }

        // 采样专用：只把可见性掐掉，不重绘不拆钩子，抓完立刻恢复
        private static bool _samplingHidden;
        private static readonly List<Form> _sampledVisible = new List<Form>();

        // 采样期（HideForSample → ShowForSample）遮罩是「故意不可见」的。
        // 游戏模式的「遮罩在屏上就跳过自动采样」必须把这几十毫秒也算成「在屏」，
        // 否则每抓一次底图都会骗出一轮新的 OCR + 翻译，看起来就是遮罩一直在闪。
        public static bool Sampling
        {
            get { return _samplingHidden; }
        }

        public static void HideForSample()
        {
            if (!OnOverlayThread) { RunOverlay(delegate { HideForSample(); }); return; }
            int hidden = 0;
            _samplingHidden = true;
            _sampledVisible.Clear();
            if (_inst != null && !_inst.IsDisposed && _inst.Visible)
            {
                try { _inst.Visible = false; _sampledVisible.Add(_inst); hidden++; } catch (Exception) { }
            }
            lock (_zoneLock)
            {
                foreach (TransOverlay f in _zones.Values)
                {
                    if (f == null || f.IsDisposed || !f.Visible) continue;
                    try { f.Visible = false; _sampledVisible.Add(f); hidden++; } catch (Exception) { }
                }
            }
            if (hidden > 0) _samplingHidden = true;
        }

        public static void ShowForSample()
        {
            if (!OnOverlayThread) { RunOverlay(delegate { ShowForSample(); }); return; }
            if (!_samplingHidden) return;
            _samplingHidden = false;
            for (int i = 0; i < _sampledVisible.Count; i++)
            {
                Form f = _sampledVisible[i];
                if (f == null || f.IsDisposed) continue;
                try { f.Visible = true; } catch (Exception) { }
            }
            _sampledVisible.Clear();
        }
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, int dwThreadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string lpModuleName);

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private static TransOverlay _inst;
        private static TransBar _bar;
        private static IntPtr _hook;
        private static LowLevelKeyboardProc _hookProc;
        private static IntPtr _mouseHook;
        private static LowLevelKeyboardProc _mouseProc;

        // 分区模式：游戏模式每个选区一个独立的遮罩窗口，各自 Bounds / items / 重绘，
        // 一个区变了只重画那一个窗口，别的区的译文原地不动（原来是单实例整块 replace，一个区变整块闪）
        private static readonly Dictionary<int, TransOverlay> _zones = new Dictionary<int, TransOverlay>();
        private static readonly Dictionary<int, Rectangle> _zoneRects = new Dictionary<int, Rectangle>();
        private static readonly Dictionary<int, List<Item>> _zoneItems = new Dictionary<int, List<Item>>();
        private static readonly object _zoneLock = new object();

        // 记住上一次的译文，Ctrl+Alt+G 才能在关掉之后再打开
        private static Rectangle _lastRect;
        private static List<Item> _lastItems = new List<Item>();
        private static Bitmap _lastImage;      // 拟真模式：整块成品图

        private readonly List<Item> _items = new List<Item>();
        private Bitmap _image;
        private bool _closing;

        private TransOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            DoubleBuffered = true;
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
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public static bool IsShowing
        {
            get
            {
                if (_inst != null && !_inst.IsDisposed && _inst.Visible) return true;
                lock (_zoneLock)
                {
                    foreach (TransOverlay f in _zones.Values)
                        if (f != null && !f.IsDisposed && f.Visible) return true;
                }
                return false;
            }
        }

        // 游戏模式专用：只有「真的画着东西」的分区才算遮罩在屏。
        // IsShowing 只看窗口 Visible，桌面翻译留下的空壳主窗口会一直可见，
        // 遮罩期巡检就会永远转下去——每 1 秒 OCR 一遍，再判「文字变了」把遮罩收掉重贴，
        // 表现就是文字消失一下又回来。这里按「有内容」判，空窗口直接不算。
        public static bool GameShowing
        {
            get
            {
                lock (_zoneLock)
                {
                    foreach (TransOverlay f in _zones.Values)
                    {
                        if (f == null || f.IsDisposed || !f.Visible || Dead(f)) continue;
                        if (f._items.Count > 0 || f._image != null) return true;
                    }
                }
                return false;
            }
        }

        // 这块分区当前画着的译文（给遮罩期复核用：确认抓到的不是「遮罩自己」）
        public static string ZoneText(int id)
        {
            lock (_zoneLock)
            {
                TransOverlay f = _zones.ContainsKey(id) ? _zones[id] : null;
                if (f == null || f.IsDisposed) return "";
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < f._items.Count; i++)
                {
                    if (f._items[i] == null || f._items[i].Text == null) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(f._items[i].Text);
                }
                return sb.ToString();
            }
        }

        // 分区显示：只动第 id 块，别的区的遮罩窗口一律不碰。
        // rect 是这块在屏幕上的矩形，items 的坐标统统相对这块（不是相对全部选区的并集）。
        public static void ShowZone(int id, Rectangle screenRect, List<Item> items)
        {
            if (!OnOverlayThread) { RunOverlay(delegate { ShowZone(id, screenRect, items); }); return; }
            ShowZoneCore(id, screenRect, items, null);
        }

        // 分区拟真：这块自己渲染好的成品图，同样只铺这一块
        public static void ShowZoneImage(int id, Rectangle screenRect, Bitmap img)
        {
            if (!OnOverlayThread) { RunOverlay(delegate { ShowZoneImage(id, screenRect, img); }); return; }
            ShowZoneCore(id, screenRect, null, img);
        }

        private static void ShowZoneCore(int id, Rectangle screenRect, List<Item> items, Bitmap img)
        {
            if (screenRect.Width < 8 || screenRect.Height < 8) return;
            if (DbgZone) JadeHost.Log("[zone] enter id=" + id + " rect=" + screenRect.X + "," + screenRect.Y + ","
                + screenRect.Width + "x" + screenRect.Height + " items=" + (items == null ? 0 : items.Count)
                + " tid=" + Thread.CurrentThread.ManagedThreadId);
            lock (_zoneLock)
            {
                TransOverlay f;
                if (!_zones.TryGetValue(id, out f) || Dead(f))
                {
                    f = new TransOverlay();
                    f.CreateControl();
                    _zones[id] = f;
                    if (DbgZone) JadeHost.Log("[zone] create id=" + id + " handle=" + f.Handle.ToInt64()
                        + " tid=" + Thread.CurrentThread.ManagedThreadId);
                }
                f._items.Clear();
                if (items != null) f._items.AddRange(items);
                List<Item> keep = new List<Item>();
                if (items != null) keep.AddRange(items);
                _zoneItems[id] = keep;
                _zoneRects[id] = screenRect;
                f.SetImage(img);
                f._closing = false;
                if (f.Bounds != screenRect) f.Bounds = screenRect;
                bool wasVisible = f.Visible;
                f.Visible = true;
                f.RenderAll();
                PinTop(f);
                if (!wasVisible) ExcludeCapture(f);
                f.HookOn();
                if (DbgZone) JadeHost.Log("[zone] shown id=" + id + " wasVis=" + (wasVisible ? 1 : 0)
                    + " vis=" + (f.Visible ? 1 : 0) + " created=" + (f.IsHandleCreated ? 1 : 0)
                    + " handle=" + f.Handle.ToInt64() + " bounds=" + f.Left + "," + f.Top + ","
                    + f.Width + "x" + f.Height + " tid=" + Thread.CurrentThread.ManagedThreadId);
            }
            if (Environment.GetEnvironmentVariable("QRSNAP_DBG_REAL") == "1")
            {
                // 调试：每块渲染结果单独落盘（遮罩抓不到屏，只能这样离线核对字号/位置）
                try
                {
                    TransOverlay f2;
                    lock (_zoneLock) { f2 = _zones.ContainsKey(id) ? _zones[id] : null; }
                    if (f2 != null) f2.SaveRender(@"E:\下载\豆包\snapscan\verify\zone-" + id + ".png");
                }
                catch (Exception) { }
            }
            BarShow(UnionOfZones());
        }

        private void SaveRender(string path)
        {
            try
            {
                int w = Width, h = Height;
                if (w < 8 || h < 8) return;
                Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Transparent);
                    if (_image != null)
                    {
                        g.DrawImage(_image, new Rectangle(0, 0, w, h));
                    }
                    else
                    {
                        for (int i = 0; i < _items.Count; i++) DrawItemCore(g, _items[i], w, h);
                    }
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                bmp.Dispose();
            }
            catch (Exception) { }
        }

        // ---------- 遮罩专用线程 ----------
        // Win32 的窗口句柄归创建它的线程所有：线程一退出，系统就把这条线程上建的窗口销毁。
        // 游戏模式一轮识字/翻译是在临时线程上跑的（AutoTick 每轮 new 一条线程，跑完就结束），
        // 第一轮的遮罩能看见只因为那条线程当时还活着；线程一结束遮罩就跟着消失，
        // 之后的每一轮 ShowZone 都拿着同一个已经失效的句柄调 UpdateLayeredWindow
        // （实测返回 1400 = ERROR_INVALID_WINDOW_HANDLE），于是「第二次死活不出现」。
        // 所以所有覆盖层窗口都统一挂到这条常驻线程上，任何线程都通过 RunOverlay 派过来。
        private static Thread _ovThread;
        private static Form _ovHost;
        private static readonly object _ovStartLock = new object();
        private static volatile bool _ovReady;

        private static void EnsureOverlayThread()
        {
            if (_ovReady) return;
            lock (_ovStartLock)
            {
                if (_ovReady) return;
                if (_ovThread != null && _ovThread.IsAlive)
                {
                    for (int i = 0; i < 200 && !_ovReady; i++) Thread.Sleep(5);
                    if (_ovReady) return;
                }
                _ovThread = new Thread(delegate()
                {
                    try
                    {
                        _ovHost = new Form();
                        _ovHost.FormBorderStyle = FormBorderStyle.None;
                        _ovHost.ShowInTaskbar = false;
                        _ovHost.StartPosition = FormStartPosition.Manual;
                        _ovHost.Bounds = new Rectangle(-32000, -32000, 8, 8);   // 屏幕外，只用来跑消息循环
                        _ovHost.Show();
                        _ovReady = true;
                        Application.Run(_ovHost);
                    }
                    catch (Exception ex)
                    {
                        try { JadeHost.Log("[overlay] thread fail " + ex.Message); } catch (Exception) { }
                    }
                    finally { _ovReady = false; }
                });
                try { _ovThread.SetApartmentState(ApartmentState.STA); } catch (Exception) { }
                _ovThread.IsBackground = true;
                _ovThread.Name = "qrsnap-overlay";
                try { _ovThread.Start(); } catch (Exception) { }
                for (int i = 0; i < 200 && !_ovReady; i++) Thread.Sleep(5);
            }
        }

        private static bool OnOverlayThread
        {
            get { return _ovThread != null && Thread.CurrentThread == _ovThread; }
        }

        // 所有会碰窗口的调用都从这里过：在遮罩线程上顺序执行。
        // 「隐藏 → 抓图 → 恢复」这种时序必须同步，所以用 Invoke 而不是 BeginInvoke。
        private static void RunOverlay(Action act)
        {
            if (act == null) return;
            EnsureOverlayThread();
            if (OnOverlayThread) { act(); return; }
            Form host = _ovHost;
            if (host == null || !host.IsHandleCreated) { act(); return; }
            try { host.Invoke(act); }
            catch (Exception)
            {
                try { act(); } catch (Exception) { }
            }
        }

        // 这块的遮罩现在到底在不在屏上。游戏模式一轮里判断「这块译文能不能原地留着」全靠它：
        // 遮罩被整块收掉（自动收遮罩 / 换页 / 抓到空帧）之后，光比文本指纹会误以为「旧译文还在」，
        // 于是这一轮干脆不画，屏幕上就永远空着了——第一次有、第二次死活不出现就是这么来的。
        public static bool IsZoneShowing(int id)
        {
            lock (_zoneLock)
            {
                TransOverlay f = _zones.ContainsKey(id) ? _zones[id] : null;
                return f != null && !f.IsDisposed && f.Visible && !Dead(f);
            }
        }

        // 遮罩被收掉、但这一块的译文本身没变时：把窗口里还留着的上一轮内容原样再铺一次。
        // 不重新翻译、不重新 OCR，纯粹是一次上屏动作（几十毫秒），保证「该显示的始终显示」。
        // 返回 false 表示这块没有可复用的内容，调用方应该老实重翻一遍。
        public static bool ReshowZone(int id)
        {
            if (!OnOverlayThread)
            {
                bool got = false;
                RunOverlay(delegate { got = ReshowZone(id); });
                return got;
            }
            bool shown = false;
            Rectangle rect = Rectangle.Empty;
            lock (_zoneLock)
            {
                TransOverlay f = _zones.ContainsKey(id) ? _zones[id] : null;
                if (Dead(f)) return false;
                if (!_zoneRects.ContainsKey(id) && !_zoneItems.ContainsKey(id)) return false;
                rect = _zoneRects.ContainsKey(id) ? _zoneRects[id] : f.Bounds;
                if (rect.Width < 8 || rect.Height < 8) return false;
                f._closing = false;
                bool wasVisible = f.Visible;
                if (f.Bounds != rect) f.Bounds = rect;
                f.Visible = true;
                f.RenderAll();
                PinTop(f);
                if (!wasVisible) ExcludeCapture(f);
                f.HookOn();
                shown = f.Visible;
            }
            if (shown)
            {
                try { BarShow(UnionOfZones()); } catch (Exception) { }
                if (DbgZone) JadeHost.Log("[zone] reshow id=" + id + " rect=" + rect.X + "," + rect.Y
                    + "," + rect.Width + "x" + rect.Height + " tid=" + Thread.CurrentThread.ManagedThreadId);
            }
            return shown;
        }

        // 自动识别区域：字幕原文没变、只是检测框挪了位置/变了尺寸时，
        // 不重新 OCR、不重新翻译，直接把这块遮罩连内容一起搬过去。
        // 之前靠 ReshowZone 复原会连同旧坐标一起复原，看起来就是「遮罩位置不对」。
        public static void MoveZone(int id, Rectangle newRect)
        {
            if (!OnOverlayThread) { RunOverlay(delegate { MoveZone(id, newRect); }); return; }
            if (newRect.Width < 8 || newRect.Height < 8) return;
            Rectangle old = Rectangle.Empty;
            List<Item> items = null;
            bool hasZone = false;
            lock (_zoneLock)
            {
                if (_zoneRects.ContainsKey(id)) old = _zoneRects[id];
                if (_zoneItems.ContainsKey(id)) items = _zoneItems[id];
                hasZone = _zones.ContainsKey(id) || _zoneItems.ContainsKey(id) || _zoneRects.ContainsKey(id);
            }
            if (!hasZone) return;

            if (items != null && items.Count > 0 && old.Width > 0 && old.Height > 0)
            {
                // 平移为主，尺寸变了就把每个译文框等比缩一下（原文没变，框还是那些框）
                double kx = (double)newRect.Width / old.Width;
                double ky = (double)newRect.Height / old.Height;
                List<Item> scaled = new List<Item>(items.Count);
                for (int i = 0; i < items.Count; i++)
                {
                    Item s = items[i];
                    Item c = new Item();
                    c.Text = s.Text;
                    c.Angle = s.Angle;
                    c.BgColor = s.BgColor;
                    c.TextColor = s.TextColor;
                    c.Box = ScaleMove(s.Box, kx, ky);
                    c.TextBox = s.TextBox.IsEmpty ? Rectangle.Empty : ScaleMove(s.TextBox, kx, ky);
                    if (s.MaskRects != null)
                    {
                        c.MaskRects = new List<Rectangle>(s.MaskRects.Count);
                        for (int k = 0; k < s.MaskRects.Count; k++) c.MaskRects.Add(ScaleMove(s.MaskRects[k], kx, ky));
                    }
                    scaled.Add(c);
                }
                ShowZoneCore(id, newRect, scaled, null);
                if (DbgZone) JadeHost.Log("[zone] move id=" + id + " " + old.X + "," + old.Y + " -> "
                    + newRect.X + "," + newRect.Y + " items=" + scaled.Count);
                return;
            }

            // 拟真整块图 / 没有 items：只挪窗口，图片随窗口尺寸铺
            lock (_zoneLock)
            {
                TransOverlay f = _zones.ContainsKey(id) ? _zones[id] : null;
                if (Dead(f)) return;
                _zoneRects[id] = newRect;
                if (f.Bounds != newRect) f.Bounds = newRect;
                f.RenderAll();
                PinTop(f);
            }
            try { BarShow(UnionOfZones()); } catch (Exception) { }
        }

        private static Rectangle ScaleMove(Rectangle r, double kx, double ky)
        {
            return new Rectangle(
                (int)Math.Round(r.X * kx), (int)Math.Round(r.Y * ky),
                Math.Max(1, (int)Math.Round(r.Width * kx)), Math.Max(1, (int)Math.Round(r.Height * ky)));
        }

        // 这一块没内容了就把它撤掉（别的区不动）
        public static void HideZone(int id)
        {
            if (!OnOverlayThread) { RunOverlay(delegate { HideZone(id); }); return; }
            lock (_zoneLock)
            {
                TransOverlay f;
                if (_zones.TryGetValue(id, out f) && f != null)
                {
                    try { f.Visible = false; } catch (Exception) { }
                }
                _zoneItems.Remove(id);
                _zoneRects.Remove(id);
            }
        }

        // 把分区遮罩全部收掉（退出游戏模式、或切回单块模式时用）
        public static void ClearZones()
        {
            if (!OnOverlayThread) { RunOverlay(delegate { ClearZones(); }); return; }
            lock (_zoneLock)
            {
                foreach (TransOverlay f in _zones.Values)
                {
                    if (f == null || f.IsDisposed) continue;
                    try { f.Visible = false; } catch (Exception) { }
                }
                _zoneItems.Clear();
                _zoneRects.Clear();
            }
        }

        // 全部可见区的并集：控制条只挂一根，挂在整个游戏字幕区上
        private static Rectangle UnionOfZones()
        {
            Rectangle u = Rectangle.Empty;
            lock (_zoneLock)
            {
                foreach (Rectangle r in _zoneRects.Values)
                    u = u.IsEmpty ? r : Rectangle.Union(u, r);
            }
            return u;
        }

        public static void Show(Rectangle screenRect, List<Item> items)
        {
            if (!OnOverlayThread) { RunOverlay(delegate { Show(screenRect, items); }); return; }
            if (screenRect.Width < 8 || screenRect.Height < 8) return;
            ClearZones();   // 单块模式接管：先把分区遮罩收掉，免得两套同时挂屏
            if (_inst == null || Dead(_inst))
            {
                _inst = new TransOverlay();
                _inst.CreateControl();
            }
            _inst._items.Clear();
            if (items != null) _inst._items.AddRange(items);
            _inst.SetImage(null);
            _lastRect = screenRect;
            _lastItems = new List<Item>();
            if (items != null) _lastItems.AddRange(items);
            _lastImage = null;
            _inst._closing = false;
            _inst.Bounds = screenRect;
            _inst.Visible = true;
            _inst.RenderAll();
            PinTop(_inst);
            // 把遮罩从截图里排除掉：游戏模式的连续采样才能不隐藏遮罩也不截到自己
            ExcludeCapture(_inst);
            _inst.HookOn();
            BarShow(screenRect);
        }

        // 拟真模式：整块成品图（原图擦掉原字 + 回填译文）直接铺在选区上
        public static void ShowImage(Rectangle screenRect, Bitmap img)
        {
            if (!OnOverlayThread) { RunOverlay(delegate { ShowImage(screenRect, img); }); return; }
            if (img == null || screenRect.Width < 8 || screenRect.Height < 8) return;
            ClearZones();
            if (_inst == null || Dead(_inst))
            {
                _inst = new TransOverlay();
                _inst.CreateControl();
            }
            _inst._items.Clear();
            _inst.SetImage(img);
            _lastRect = screenRect;
            _lastItems = new List<Item>();
            _lastImage = img;
            _inst._closing = false;
            _inst.Bounds = screenRect;
            _inst.Visible = true;
            _inst.RenderAll();
            PinTop(_inst);
            // 把遮罩从截图里排除掉：游戏模式的连续采样才能不隐藏遮罩也不截到自己
            ExcludeCapture(_inst);
            _inst.HookOn();
            BarShow(screenRect);
            if (Environment.GetEnvironmentVariable("QRSNAP_DBG_REAL") == "1")
            {
                // 调试：成品图落盘，离线核对字号/位置（遮罩窗口本身抓不到屏）
                try
                {
                    img.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qrsnap-overlay.png"),
                        System.Drawing.Imaging.ImageFormat.Png);
                }
                catch (Exception) { }
            }
        }

        private void SetImage(Bitmap img)
        {
            _image = img;
        }

        // 改了遮罩透明度等外观参数后立刻重画（不用关掉再重开）
        public static void Repaint()
        {
            if (!OnOverlayThread) { RunOverlay(delegate { Repaint(); }); return; }
            if (_inst != null && !_inst.IsDisposed && _inst.Visible)
            {
                _inst._items.Clear();
                if (_lastItems != null) _inst._items.AddRange(_lastItems);
                _inst.RenderAll();
            }
            lock (_zoneLock)
            {
                foreach (int id in new List<int>(_zoneItems.Keys))
                {
                    TransOverlay f = _zones.ContainsKey(id) ? _zones[id] : null;
                    if (f == null || f.IsDisposed || !f.Visible) continue;
                    f._items.Clear();
                    List<Item> keep = _zoneItems[id];
                    if (keep != null) f._items.AddRange(keep);
                    f.RenderAll();
                }
            }
        }

        // Ctrl+Alt+G：整层译文显示 / 隐藏
        public static void Toggle()
        {
            if (!OnOverlayThread) { RunOverlay(delegate { Toggle(); }); return; }
            if (IsShowing) { HideOverlay(); return; }
            if (_lastImage != null) { ShowImage(_lastRect, _lastImage); return; }
            if (_lastItems.Count == 0) return;
            Show(_lastRect, _lastItems);
        }

        public static void Restore()
        {
            if (!OnOverlayThread) { RunOverlay(delegate { Restore(); }); return; }
            try { if (!IsShowing) Toggle(); } catch (Exception) { }
        }

        // 用户点遮罩上的「关闭译文」或滚轮关闭：游戏模式下要记住“用户关的”，不能下一轮又弹出来
        public static void HideByUser()
        {
            if (!OnOverlayThread) { RunOverlay(delegate { HideByUser(); }); return; }
            try { GameRuntime.SetOverlayClosed(true); } catch (Exception) { }
            HideOverlay();
        }

        public static void HideOverlay()
        {
            if (!OnOverlayThread) { RunOverlay(delegate { HideOverlay(); }); return; }
            _samplingHidden = false;
            _sampledVisible.Clear();
            if (_inst != null)
            {
                try { _inst.Visible = false; }
                catch (Exception) { }
            }
            lock (_zoneLock)
            {
                foreach (TransOverlay f in _zones.Values)
                {
                    if (f == null || f.IsDisposed) continue;
                    try { f.Visible = false; } catch (Exception) { }
                }
            }
            UnhookGlobal();
            BarHide();
        }

        private static void UnhookGlobal()
        {
            if (_hook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_hook); } catch (Exception) { }
                _hook = IntPtr.Zero;
            }
            if (_mouseHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_mouseHook); } catch (Exception) { }
                _mouseHook = IntPtr.Zero;
            }
        }

        private static void BarShow(Rectangle rect)
        {
            if (!AppConfig.MtBar) return;   // 设置里关掉控制条时不弹
            try
            {
                if (Dead(_bar))
                {
                    _bar = new TransBar();
                    _bar.CreateControl();
                }
                // 截图抬手的位置只对紧接着的这次显示有效，用完就清，免得下一次（比如游戏模式）串位
                bool hasAnchor = _hasBarAnchor;
                Point anchor = _barAnchor;
                _hasBarAnchor = false;
                _bar.PlaceNear(rect, hasAnchor, anchor);
                _bar.Visible = true;
                PinTop(_bar);
                _bar.Commit();
                _bar.Raise();
            }
            catch (Exception) { }
        }

        private static Point _barAnchor;
        private static bool _hasBarAnchor;

        // 截图松手的位置：控制条跟着它走
        public static void SetBarAnchor(Point p)
        {
            _barAnchor = p;
            _hasBarAnchor = true;
        }

        private static void BarHide()
        {
            if (_bar == null) return;
            try { _bar.Visible = false; } catch (Exception) { }
        }

        // 设置里关掉控制条时，把已经画出来的那条收掉
        public static void HideBar()
        {
            if (!OnOverlayThread) { RunOverlay(delegate { HideBar(); }); return; }
            BarHide();
        }

        private void HookOn()
        {
            if (_hook == IntPtr.Zero)
            {
                _hookProc = new LowLevelKeyboardProc(OnKey);
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
            }
            // 滚轮关闭：滚一下就走，别挡着下面的操作
            if (_mouseHook == IntPtr.Zero)
            {
                _mouseProc = new LowLevelKeyboardProc(OnMouse);
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
            }
        }

        private void Unhook()
        {
            if (_hook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_hook); } catch (Exception) { }
                _hook = IntPtr.Zero;
            }
            if (_mouseHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_mouseHook); } catch (Exception) { }
                _mouseHook = IntPtr.Zero;
            }
        }

        private IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !_closing && !AppConfig.GameOn)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    int vk = Marshal.ReadInt32(lParam);
                    if (vk == 0x1B)   // Esc
                    {
                        NativeShell.Log("trans esc pressed");
                        _closing = true;
                        BeginInvoke(new MethodInvoker(delegate() { HideByUser(); }));
                    }
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !_closing && !AppConfig.GameOn && wParam.ToInt32() == WM_MOUSEWHEEL)
            {
                _closing = true;
                NativeShell.Log("trans wheel close");
                BeginInvoke(new MethodInvoker(delegate() { HideByUser(); }));
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 用分层窗口自己推画面，OnPaint 不参与实际显示
        }

        // ---------- 绘制 ----------
        private void RenderAll()
        {
            int w = Width, h = Height;
            if (w < 8 || h < 8) return;
            Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);

                if (_image != null)
                {
                    // 拟真：整块成品图铺满选区
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_image, new Rectangle(0, 0, w, h));
                }
                else
                {
                    for (int i = 0; i < _items.Count; i++) DrawItemCore(g, _items[i], w, h);
                }
            }
            if (Environment.GetEnvironmentVariable("QRSNAP_DBG_REAL") == "1")
            {
                // 调试：把这一层实际画出来的样子落盘（分层窗口抓不到屏，只能从这里看）
                try
                {
                    bmp.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qrsnap-layer.png"),
                        System.Drawing.Imaging.ImageFormat.Png);
                }
                catch (Exception) { }
            }
            Commit(bmp);
            bmp.Dispose();
        }

        // 把译文遮罩直接合成到截图上（给「重复截图缓存」用：下次同一屏直接贴成品图）
        internal static Bitmap Compose(Bitmap screen, List<Item> items)
        {
            if (screen == null) return null;
            Bitmap b = new Bitmap(screen.Width, screen.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.DrawImage(screen, new Rectangle(0, 0, b.Width, b.Height));
                if (items != null)
                    for (int i = 0; i < items.Count; i++) DrawItemCore(g, items[i], b.Width, b.Height);
            }
            return b;
        }
        private static void DrawItemCore(Graphics g, Item it, int w, int h)
        {
            if (it == null || string.IsNullOrEmpty(it.Text)) return;
            Rectangle box = Clamp(it.Box, w, h);
            if (box.Width < 6 || box.Height < 6) return;

            Rectangle textRect = it.TextBox;
            if (textRect.Width < 6 || textRect.Height < 6) textRect = box;
            textRect = Clamp(textRect, w, h);
            if (textRect.Width < 6 || textRect.Height < 6) return;

            float padX = Math.Max(3f, textRect.Height * 0.05f);
            float maxW = Math.Max(8f, textRect.Width - padX * 2f);
            float maxH = Math.Max(8f, textRect.Height - 4f);

            List<string> lines;
            Font font = FitFont(g, it.Text, maxW, maxH, out lines);
            if (font == null || lines == null) return;

            float cx = box.X + box.Width / 2f;
            float cy = box.Y + box.Height / 2f;

            GraphicsState gs = g.Save();
            g.TranslateTransform(cx, cy);
            if (Math.Abs(it.Angle) >= 1.5f) g.RotateTransform(it.Angle);
            g.TranslateTransform(-cx, -cy);

            int maskA = AppConfig.NormalizeMtMaskAlpha(AppConfig.MtMaskAlpha);
            int fillA = (int)Math.Round(255.0 * maskA / 100.0);
            int edgeA = it.BgColor.IsEmpty ? (int)Math.Round(52.0 * maskA / 100.0) : 0;
            Color fillBase = it.BgColor.IsEmpty ? Color.FromArgb(14, 18, 26) : it.BgColor;
            Color textBase = it.TextColor.IsEmpty ? Color.FromArgb(246, 249, 255) : it.TextColor;
            // 底板刷成本来的底色，效率模式就会「融进背景」，只留下中文
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(fillA, fillBase)))
            using (Pen edge = new Pen(Color.FromArgb(edgeA, 108, 128, 158), 1f))
            using (Pen glow = new Pen(Color.FromArgb(18, 0, 0, 0), 2f))
            {
                if (it.MaskRects != null && it.MaskRects.Count > 0)
                {
                    // 段落块：每条原文行一块底板，徽章/图标所在的位置不会被整块盖住
                    for (int i = 0; i < it.MaskRects.Count; i++)
                    {
                        Rectangle r = Clamp(it.MaskRects[i], w, h);
                        if (r.Width < 4 || r.Height < 4) continue;
                        RectangleF rf = new RectangleF(r.X - 1f, r.Y - 1f, r.Width + 2f, r.Height + 2f);
                        using (GraphicsPath path = RoundRect(rf, Math.Min(rf.Height / 2f, 7f)))
                        {
                            g.DrawPath(glow, path);
                            g.FillPath(fill, path);
                            g.DrawPath(edge, path);
                        }
                    }
                }
                else
                {
                    RectangleF bg = new RectangleF(box.X, box.Y, box.Width, box.Height);
                    using (GraphicsPath path = RoundRect(bg, Math.Min(bg.Height / 2f, 8f)))
                    {
                        g.DrawPath(glow, path);
                        g.FillPath(fill, path);
                        g.DrawPath(edge, path);
                    }
                }
            }

            // 译文贴着排版区左侧起排，自动折行，垂直居中
            float lh = font.GetHeight(g);
            float total = lines.Count * lh;
            float top = textRect.Y + (textRect.Height - total) / 2f;
            if (top < textRect.Y) top = textRect.Y;
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Near;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.None;
                using (SolidBrush shadow = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(248, textBase)))
                {
                    for (int i = 0; i < lines.Count; i++)
                    {
                        float y = top + i * lh;
                        g.DrawString(lines[i], font, shadow, textRect.X + padX + 1f, y + 1f, sf);
                        g.DrawString(lines[i], font, brush, textRect.X + padX, y, sf);
                    }
                }
            }

            g.Restore(gs);
            font.Dispose();
        }

        private static Rectangle Clamp(Rectangle r, int w, int h)
        {
            if (r.X < 0) { r.Width += r.X; r.X = 0; }
            if (r.Y < 0) { r.Height += r.Y; r.Y = 0; }
            if (r.Right > w) r.Width = w - r.X;
            if (r.Bottom > h) r.Height = h - r.Y;
            return r;
        }

        private static Font FitFont(Graphics g, string text, float maxW, float maxH, out List<string> lines)
        {
            string family = "Microsoft YaHei UI";
            float size = Math.Min(maxH * 0.82f, 42f);
            if (size < 7f) size = 7f;
            for (; size >= 6.5f; size -= 0.5f)
            {
                Font f = null;
                try
                {
                    // 像素单位，和底板尺寸同一套坐标
                    f = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel);
                    List<string> ls = WrapLines(g, text, f, maxW);
                    float lh = f.GetHeight(g);
                    float widest = 0f;
                    for (int i = 0; i < ls.Count; i++) widest = Math.Max(widest, g.MeasureString(ls[i], f).Width);
                    if (widest <= maxW + 1f && ls.Count * lh <= maxH + 1f)
                    {
                        lines = ls;
                        return f;
                    }
                    f.Dispose();
                }
                catch (Exception)
                {
                    if (f != null) f.Dispose();
                }
            }
            lines = null;
            try
            {
                Font f = new Font(family, 6.5f, FontStyle.Regular, GraphicsUnit.Pixel);
                lines = WrapLines(g, text, f, maxW);
                return f;
            }
            catch (Exception) { return null; }
        }

        // 按「英文单词整体 / 单个汉字 / 单个标点」折行，中文不在词中间断
        private static List<string> WrapLines(Graphics g, string text, Font f, float maxW)
        {
            List<string> lines = new List<string>();
            if (string.IsNullOrEmpty(text)) { lines.Add(""); return lines; }
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
            string cur = "";
            for (int t = 0; t < tokens.Count; t++)
            {
                string tk = tokens[t];
                if (tk == "\n") { lines.Add(cur.TrimEnd()); cur = ""; continue; }
                if (tk == " ") { if (cur.Length > 0) cur += " "; continue; }
                string cand = cur + tk;
                if (cur.Length > 0 && g.MeasureString(cand, f).Width > maxW)
                {
                    if (noHead.IndexOf(tk[0]) >= 0)
                    {
                        lines.Add((cur + tk).TrimEnd());
                        cur = "";
                        continue;
                    }
                    lines.Add(cur.TrimEnd());
                    cur = tk;
                }
                else cur = cand;
            }
            if (cur.TrimEnd().Length > 0) lines.Add(cur.TrimEnd());
            if (lines.Count == 0) lines.Add(text);

            List<string> fixedLines = new List<string>();
            foreach (string line in lines)
            {
                string c2 = line;
                while (c2.Length > 0 && g.MeasureString(c2, f).Width > maxW)
                {
                    int take = c2.Length;
                    while (take > 1 && g.MeasureString(c2.Substring(0, take), f).Width > maxW) take--;
                    fixedLines.Add(c2.Substring(0, take));
                    c2 = c2.Substring(take).TrimStart();
                }
                if (c2.Length > 0) fixedLines.Add(c2);
                else if (line.Length == 0) fixedLines.Add("");
            }
            if (fixedLines.Count == 0) fixedLines.Add(text);
            return fixedLines;
        }

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private void Commit(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                SelectObject(memDc, hBmp);
                SIZE size = new SIZE(bmp.Width, bmp.Height);
                POINT src = new POINT(0, 0);
                POINT dst = new POINT(Left, Top);
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AC_SRC_ALPHA;
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Unhook();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            Unhook();
            base.Dispose(disposing);
        }
    }

    // 贴在选区右下角的悬浮控制条：不吃穿透，点一下 × 关掉全部译文。
    // 只占一小条，鼠标滚动 / Esc / 全局热键同样能关。
    internal sealed class TransBar : Form
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int CX, CY; public SIZE(int x, int y) { CX = x; CY = y; } }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;

        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        private const int BarW = 128, BarH = 28;

        public TransBar()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            DoubleBuffered = true;
            Size = new Size(BarW, BarH);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // 注意：这里故意不加 WS_EX_TRANSPARENT，控制条要能点
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        // 优先贴截图松手的位置；没有松手点才退回选区右上角外侧
        public void PlaceNear(Rectangle rect, bool hasAnchor, Point anchor)
        {
            Rectangle screen = Screen.FromRectangle(rect).WorkingArea;
            int x, y;
            if (hasAnchor)
            {
                // 松手点右下方一点，出界就往回翻，始终不压住选区中心
                x = anchor.X + 10;
                y = anchor.Y + 10;
                if (x + BarW > screen.Right - 4) x = anchor.X - BarW - 10;
                if (y + BarH > screen.Bottom - 4) y = anchor.Y - BarH - 10;
            }
            else
            {
                x = rect.Right - BarW;
                y = rect.Top - BarH - 6;
                if (y < screen.Top + 4)
                {
                    y = rect.Top + 6;
                    if (rect.Height < BarH + 14) y = rect.Bottom + 6;
                }
            }
            if (x < screen.Left + 4) x = screen.Left + 4;
            if (x + BarW > screen.Right - 4) x = screen.Right - BarW - 4;
            if (y < screen.Top + 4) y = screen.Top + 4;
            if (y + BarH > screen.Bottom - 4) y = screen.Bottom - BarH - 4;
            Bounds = new Rectangle(x, y, BarW, BarH);
            NativeShell.Log("trans bar place " + x + "," + y + " " + BarW + "x" + BarH
                + (hasAnchor ? " at " + anchor.X + "," + anchor.Y : " by rect"));
        }

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, int flags);

        // 保证控制条压在译文层上面，不然点不到
        public void Raise()
        {
            try { SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); } catch (Exception) { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 画面由 Commit 推给分层窗口
        }

        public void Commit()
        {
            Bitmap bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
                using (GraphicsPath path = Round(r, 12f))
                {
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(205, 16, 20, 28)))
                        g.FillPath(fill, path);
                    using (Pen edge = new Pen(Color.FromArgb(110, 96, 165, 250), 1f))
                        g.DrawPath(edge, path);
                }

                using (Font f = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point))
                using (SolidBrush close = new SolidBrush(Color.FromArgb(245, 255, 236, 240)))
                using (SolidBrush hint = new SolidBrush(Color.FromArgb(160, 214, 224, 240)))
                {
                    g.DrawString("✕", f, close, 9f, 3f);
                    g.DrawString("关闭译文", f, hint, 28f, 3f);
                }
            }
            CommitBitmap(bmp);
            bmp.Dispose();
        }

        private static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private void CommitBitmap(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                SelectObject(memDc, hBmp);
                SIZE size = new SIZE(bmp.Width, bmp.Height);
                POINT src = new POINT(0, 0);
                POINT dst = new POINT(Left, Top);
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AC_SRC_ALPHA;
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            NativeShell.Log("trans bar click " + e.X + "," + e.Y + " btn=" + e.Button);
            TransOverlay.HideByUser();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = (e.X <= 30) ? Cursors.Hand : Cursors.Default;
        }
    }
}
