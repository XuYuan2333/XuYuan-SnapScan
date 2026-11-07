using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
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

        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
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
            get { return _inst != null && _inst.Visible; }
        }

        public static void Show(Rectangle screenRect, List<Item> items)
        {
            if (screenRect.Width < 8 || screenRect.Height < 8) return;
            if (_inst == null || _inst.IsDisposed)
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
            _inst.HookOn();
            BarShow(screenRect);
        }

        // 拟真模式：整块成品图（原图擦掉原字 + 回填译文）直接铺在选区上
        public static void ShowImage(Rectangle screenRect, Bitmap img)
        {
            if (img == null || screenRect.Width < 8 || screenRect.Height < 8) return;
            if (_inst == null || _inst.IsDisposed)
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
            _inst.HookOn();
            BarShow(screenRect);
        }

        private void SetImage(Bitmap img)
        {
            _image = img;
        }

        // 改了遮罩透明度等外观参数后立刻重画（不用关掉再重开）
        public static void Repaint()
        {
            if (!IsShowing) return;
            _inst._items.Clear();
            if (_lastItems != null) _inst._items.AddRange(_lastItems);
            _inst.RenderAll();
        }

        // Ctrl+Alt+G：整层译文显示 / 隐藏
        public static void Toggle()
        {
            if (IsShowing) { HideOverlay(); return; }
            if (_lastImage != null) { ShowImage(_lastRect, _lastImage); return; }
            if (_lastItems.Count == 0) return;
            Show(_lastRect, _lastItems);
        }

        public static void Restore()
        {
            try { if (!IsShowing) Toggle(); } catch (Exception) { }
        }

        public static void HideOverlay()
        {
            if (_inst != null)
            {
                try
                {
                    _inst.Unhook();
                    _inst.Visible = false;
                }
                catch (Exception) { }
            }
            BarHide();
        }

        private static void BarShow(Rectangle rect)
        {
            if (!AppConfig.MtBar) return;   // 设置里关掉控制条时不弹
            try
            {
                if (_bar == null || _bar.IsDisposed)
                {
                    _bar = new TransBar();
                    _bar.CreateControl();
                }
                _bar.PlaceNear(rect);
                _bar.Visible = true;
                _bar.Commit();
                _bar.Raise();
            }
            catch (Exception) { }
        }

        private static void BarHide()
        {
            if (_bar == null) return;
            try { _bar.Visible = false; } catch (Exception) { }
        }

        // 设置里关掉控制条时，把已经画出来的那条收掉
        public static void HideBar() { BarHide(); }

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
            if (nCode >= 0 && !_closing)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    int vk = Marshal.ReadInt32(lParam);
                    if (vk == 0x1B)   // Esc
                    {
                        NativeShell.Log("trans esc pressed");
                        _closing = true;
                        BeginInvoke(new MethodInvoker(delegate() { HideOverlay(); }));
                    }
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !_closing && wParam.ToInt32() == WM_MOUSEWHEEL)
            {
                _closing = true;
                NativeShell.Log("trans wheel close");
                BeginInvoke(new MethodInvoker(delegate() { HideOverlay(); }));
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

        private const int BarW = 150, BarH = 32;

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

        // 放在选区右下角内侧；选区太小时挪到选区下方
        public void PlaceNear(Rectangle rect)
        {
            int x = rect.Right - BarW - 8;
            int y = rect.Bottom - BarH - 8;
            if (y < rect.Top + 4) y = rect.Bottom + 6;
            Rectangle screen = Screen.FromRectangle(rect).WorkingArea;
            if (x < screen.Left + 4) x = screen.Left + 4;
            if (y + BarH > screen.Bottom - 4) y = screen.Bottom - BarH - 4;
            Bounds = new Rectangle(x, y, BarW, BarH);
            NativeShell.Log("trans bar place " + x + "," + y + " " + BarW + "x" + BarH);
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
                using (GraphicsPath path = Round(r, 15f))
                {
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(232, 16, 20, 28)))
                        g.FillPath(fill, path);
                    using (Pen edge = new Pen(Color.FromArgb(120, 96, 165, 250), 1f))
                        g.DrawPath(edge, path);
                }

                using (Font f = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point))
                using (SolidBrush close = new SolidBrush(Color.FromArgb(245, 255, 236, 240)))
                using (SolidBrush hint = new SolidBrush(Color.FromArgb(150, 214, 224, 240)))
                {
                    g.DrawString("✕", f, close, 10f, 5f);
                    g.DrawString("关闭译文", f, hint, 34f, 5f);
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
            TransOverlay.HideOverlay();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = (e.X <= 30) ? Cursors.Hand : Cursors.Default;
        }
    }
}
