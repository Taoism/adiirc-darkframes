// DarkFrames: dark title bars, borders and scroll bars for AdiIRC's channel, query and server windows.
//
// AdiIRC's windows are WinForms MDI children. Windows has no dark style for MDI child frames (the system
// dark title bar only applies to top-level windows), so they were drawn in the light Windows 7 style even
// when everything else was dark. This plugin paints those frames itself while Windows apps use dark mode,
// and gives the windows' scroll bars the system dark style. In light mode it leaves everything to Windows.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AdiIRCAPIv2.Interfaces;
using Microsoft.Win32;

namespace DarkFrames
{
    public class DarkFramesPlugin : IPlugin
    {
        public string PluginName { get { return "DarkFrames"; } }
        public string PluginDescription { get { return "Dark title bars, borders and scroll bars for the channel windows in Windows dark mode"; } }
        public string PluginAuthor { get { return "Taoism"; } }
        public string PluginVersion { get { return "1.0"; } }
        public string PluginEmail { get { return ""; } }

        IPluginHost host;
        Timer timer;
        readonly Dictionary<IntPtr, DarkFrame> frames = new Dictionary<IntPtr, DarkFrame>();
        readonly HashSet<IntPtr> themedScrollBars = new HashSet<IntPtr>();
        bool lastDark;

        public void Initialize(IPluginHost pluginHost)
        {
            host = pluginHost;
            lastDark = Theme.IsDark();
            // A UI timer finds new windows and follows the Windows light/dark setting (MDI children don't get
            // WM_SETTINGCHANGE). It runs on AdiIRC's UI thread, where Initialize is called.
            timer = new Timer();
            timer.Interval = 500;
            timer.Tick += delegate { Scan(); };
            timer.Start();
            Scan();
        }

        public void Dispose()
        {
            if (timer != null)
            {
                timer.Stop();
                timer.Dispose();
                timer = null;
            }
            foreach (var frame in frames.Values)
                frame.Detach();
            frames.Clear();
            foreach (var h in themedScrollBars)
                if (Native.IsWindow(h))
                    Native.SetWindowTheme(h, null, null);
            themedScrollBars.Clear();
        }

        void Scan()
        {
            var main = host.MainWindowHandle;
            if (main == IntPtr.Zero)
                return;

            var dark = Theme.Refresh();
            var themeChanged = dark != lastDark;
            lastDark = dark;

            // Forget closed windows
            var gone = new List<IntPtr>();
            foreach (var h in frames.Keys)
                if (!Native.IsWindow(h))
                    gone.Add(h);
            foreach (var h in gone)
                frames.Remove(h);
            themedScrollBars.RemoveWhere(h => !Native.IsWindow(h));

            foreach (var child in Native.Children(main))
            {
                var cls = Native.ClassName(child);
                if (cls.StartsWith("WindowsForms10.MDICLIENT", StringComparison.Ordinal))
                {
                    foreach (var mdiChild in Native.DirectChildren(child))
                        if (!frames.ContainsKey(mdiChild))
                            frames[mdiChild] = new DarkFrame(mdiChild);
                }
                else if (cls.StartsWith("WindowsForms10.SCROLLBAR", StringComparison.Ordinal) || cls == "ScrollBar")
                {
                    if (dark && themedScrollBars.Add(child))
                        Native.SetWindowTheme(child, "DarkMode_Explorer", null);
                }
            }

            if (themeChanged)
            {
                if (!dark)
                {
                    foreach (var h in themedScrollBars)
                        Native.SetWindowTheme(h, null, null);
                    themedScrollBars.Clear();
                }

                foreach (var frame in frames.Values)
                    frame.Redraw();
            }
        }
    }

    // Follows the Windows "app mode" (Settings > Personalization > Colors), unless DarkFrames.ini next to
    // the plugin says Mode=dark or Mode=light
    static class Theme
    {
        static readonly string iniPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(Theme).Assembly.Location), "DarkFrames.ini");
        static bool dark = Read();

        public static bool IsDark() { return dark; }

        public static bool Refresh()
        {
            dark = Read();
            return dark;
        }

        static bool Read()
        {
            var mode = IniMode();
            if (mode == "dark")
                return true;
            if (mode == "light")
                return false;
            return WindowsAppsDark();
        }

        static string IniMode()
        {
            try
            {
                if (!System.IO.File.Exists(iniPath))
                    return "auto";
                foreach (var line in System.IO.File.ReadAllLines(iniPath))
                {
                    var t = line.Trim();
                    if (t.StartsWith("Mode=", StringComparison.OrdinalIgnoreCase))
                        return t.Substring(5).Trim().ToLowerInvariant();
                }
            }
            catch
            {
            }
            return "auto";
        }

        static bool WindowsAppsDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return value is int && (int)value == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static readonly Color CaptionActive = Color.FromArgb(43, 43, 43);
        public static readonly Color CaptionInactive = Color.FromArgb(32, 32, 32);
        public static readonly Color BorderActive = Color.FromArgb(70, 70, 70);
        public static readonly Color BorderInactive = Color.FromArgb(48, 48, 48);
        public static readonly Color TextActive = Color.FromArgb(240, 240, 240);
        public static readonly Color TextInactive = Color.FromArgb(140, 140, 140);
        public static readonly Color ButtonHot = Color.FromArgb(68, 68, 68);
        public static readonly Color ButtonPressed = Color.FromArgb(88, 88, 88);
        public static readonly Color CloseHot = Color.FromArgb(196, 43, 28);
        public static readonly Color ClosePressed = Color.FromArgb(150, 35, 25);
    }

    enum Button { None, Minimize, Maximize, Close }

    // One MDI child window: paints its non-client area while dark mode is on
    class DarkFrame : NativeWindow
    {
        const int WM_SETTEXT = 0x000C, WM_SETICON = 0x0080, WM_NCHITTEST = 0x0084, WM_NCPAINT = 0x0085,
            WM_NCACTIVATE = 0x0086, WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONDBLCLK = 0x00A3,
            WM_NCUAHDRAWCAPTION = 0x00AE, WM_NCUAHDRAWFRAME = 0x00AF, WM_MOUSEMOVE = 0x0200, WM_LBUTTONUP = 0x0202,
            WM_CAPTURECHANGED = 0x0215, WM_NCMOUSELEAVE = 0x02A2, WM_SYSCOMMAND = 0x0112, WM_NCDESTROY = 0x0082,
            WM_MDIGETACTIVE = 0x0229;
        const int HTCAPTION = 2, HTMINBUTTON = 8, HTMAXBUTTON = 9, HTCLOSE = 20, HTHELP = 21;
        const int SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_CLOSE = 0xF060, SC_RESTORE = 0xF120;
        const int WS_VISIBLE = 0x10000000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;

        bool active;
        Button hot = Button.None, pressed = Button.None;
        bool pressedInside;
        bool tracking;

        public DarkFrame(IntPtr handle)
        {
            AssignHandle(handle);
            active = Native.SendMessage(Native.GetParent(handle), WM_MDIGETACTIVE, IntPtr.Zero, IntPtr.Zero) == handle;
            Redraw();
        }

        public void Detach()
        {
            if (Handle != IntPtr.Zero)
            {
                var h = Handle;
                ReleaseHandle();
                Native.RedrawFrame(h);
            }
        }

        public void Redraw()
        {
            if (Handle != IntPtr.Zero)
                Native.RedrawFrame(Handle);
        }

        // Dark painting applies to windows that show a frame (not maximized MDI children, which have none)
        bool Painting()
        {
            return Theme.IsDark() && !Native.IsZoomed(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_NCDESTROY:
                    base.WndProc(ref m);
                    ReleaseHandle();
                    return;
                case WM_NCPAINT:
                    if (!Painting())
                        break;
                    Paint();
                    m.Result = IntPtr.Zero;
                    return;
                case WM_NCACTIVATE:
                    active = m.WParam != IntPtr.Zero;
                    if (!Painting())
                        break;
                    // -1: change the state without letting Windows repaint the light frame
                    m.LParam = new IntPtr(-1);
                    base.WndProc(ref m);
                    Paint();
                    return;
                case WM_NCUAHDRAWCAPTION:
                case WM_NCUAHDRAWFRAME:
                    // Undocumented messages that themed windows use to redraw the caption (hover, activation)
                    if (!Painting())
                        break;
                    Paint();
                    m.Result = IntPtr.Zero;
                    return;
                case WM_SETTEXT:
                case WM_SETICON:
                    if (!Painting())
                        break;
                    // Windows draws the caption directly while handling these; hide the window from it meanwhile
                    var style = Native.GetWindowLong(Handle, -16);
                    Native.SetWindowLong(Handle, -16, style & ~WS_VISIBLE);
                    base.WndProc(ref m);
                    Native.SetWindowLong(Handle, -16, style);
                    Paint();
                    return;
                case WM_NCHITTEST:
                {
                    base.WndProc(ref m);
                    if (!Painting())
                        return;
                    var button = ButtonAt(Native.PointFromLParam(m.LParam));
                    var hit = m.Result.ToInt32();
                    if (button != Button.None)
                        m.Result = new IntPtr(button == Button.Close ? HTCLOSE : button == Button.Maximize ? HTMAXBUTTON : HTMINBUTTON);
                    else if (hit == HTMINBUTTON || hit == HTMAXBUTTON || hit == HTCLOSE || hit == HTHELP)
                        m.Result = new IntPtr(HTCAPTION);
                    return;
                }
                case WM_NCMOUSEMOVE:
                {
                    if (!Painting())
                        break;
                    var button = ButtonAt(Native.PointFromLParam(m.LParam));
                    if (button != hot)
                    {
                        hot = button;
                        Paint();
                    }

                    Native.TrackNonClientLeave(Handle);
                    if (button != Button.None)
                    {
                        m.Result = IntPtr.Zero;
                        return;
                    }
                    break;
                }
                case WM_NCMOUSELEAVE:
                    if (hot != Button.None && !tracking)
                    {
                        hot = Button.None;
                        if (Painting())
                            Paint();
                    }
                    break;
                case WM_NCLBUTTONDOWN:
                case WM_NCLBUTTONDBLCLK:
                {
                    if (!Painting())
                        break;
                    var button = ButtonAt(Native.PointFromLParam(m.LParam));
                    if (button == Button.None)
                        break;
                    if (m.Msg == WM_NCLBUTTONDOWN)
                    {
                        pressed = button;
                        pressedInside = true;
                        tracking = true;
                        Native.SetCapture(Handle);
                        Paint();
                    }
                    m.Result = IntPtr.Zero;
                    return;
                }
                case WM_MOUSEMOVE:
                    if (!tracking)
                        break;
                {
                    var inside = ButtonAt(Native.ClientToScreen(Handle, Native.PointFromLParam(m.LParam))) == pressed;
                    if (inside != pressedInside)
                    {
                        pressedInside = inside;
                        Paint();
                    }
                    m.Result = IntPtr.Zero;
                    return;
                }
                case WM_LBUTTONUP:
                    if (!tracking)
                        break;
                {
                    var button = pressed;
                    var inside = ButtonAt(Native.ClientToScreen(Handle, Native.PointFromLParam(m.LParam))) == button;
                    tracking = false;
                    pressed = Button.None;
                    hot = inside ? button : Button.None;
                    Native.ReleaseCapture();
                    Paint();
                    if (inside)
                    {
                        int command;
                        if (button == Button.Close)
                            command = SC_CLOSE;
                        else if (button == Button.Minimize)
                            command = Native.IsIconic(Handle) ? SC_RESTORE : SC_MINIMIZE;
                        else
                            command = Native.IsZoomed(Handle) || Native.IsIconic(Handle) ? SC_RESTORE : SC_MAXIMIZE;
                        Native.PostMessage(Handle, WM_SYSCOMMAND, new IntPtr(command), IntPtr.Zero);
                    }
                    m.Result = IntPtr.Zero;
                    return;
                }
                case WM_CAPTURECHANGED:
                    if (tracking)
                    {
                        tracking = false;
                        pressed = Button.None;
                        Paint();
                    }
                    break;
            }

            base.WndProc(ref m);
        }

        // ---------------------------------------------------------------- layout (window coordinates)

        struct Layout
        {
            public Rectangle Window, Client, Caption;
            public Rectangle Minimize, Maximize, Close;
            public bool HasMinimize, HasMaximize;
        }

        Layout GetLayout()
        {
            var l = new Layout();
            var wr = Native.WindowRect(Handle);
            l.Window = new Rectangle(0, 0, wr.Width, wr.Height);

            // The frame insets from the window style; minimized windows have only a caption
            var insets = Native.FrameInsets(Handle);
            var border = insets.Left;
            if (Native.IsIconic(Handle))
                l.Client = new Rectangle(border, wr.Height - border, Math.Max(0, wr.Width - 2 * border), 0);
            else
            {
                var cr = Native.ClientRectOnScreen(Handle);
                l.Client = new Rectangle(cr.Left - wr.Left, cr.Top - wr.Top, cr.Width, cr.Height);
            }

            var captionTop = border;
            var captionHeight = Math.Max(0, insets.Top - border);
            if (Native.IsIconic(Handle))
                captionHeight = Math.Max(0, wr.Height - 2 * border);
            l.Caption = new Rectangle(border, captionTop, Math.Max(0, wr.Width - 2 * border), captionHeight);

            var style = Native.GetWindowLong(Handle, -16);
            l.HasMinimize = (style & WS_MINIMIZEBOX) != 0;
            l.HasMaximize = (style & WS_MAXIMIZEBOX) != 0;

            var bh = captionHeight;
            var bw = (int)Math.Round(bh * 1.45);
            var x = l.Caption.Right;
            x -= bw;
            l.Close = new Rectangle(x, captionTop, bw, bh);
            if (l.HasMaximize || l.HasMinimize)
            {
                x -= bw;
                l.Maximize = new Rectangle(x, captionTop, bw, bh);
                x -= bw;
                l.Minimize = new Rectangle(x, captionTop, bw, bh);
            }
            return l;
        }

        Button ButtonAt(Point screen)
        {
            if (Native.IsZoomed(Handle))
                return Button.None;
            var wr = Native.WindowRect(Handle);
            var p = new Point(screen.X - wr.Left, screen.Y - wr.Top);
            var l = GetLayout();
            if (l.Close.Contains(p))
                return Button.Close;
            if (l.HasMaximize && l.Maximize.Contains(p))
                return Button.Maximize;
            if (l.HasMinimize && l.Minimize.Contains(p))
                return Button.Minimize;
            return Button.None;
        }

        // ---------------------------------------------------------------- painting

        void Paint()
        {
            if (Handle == IntPtr.Zero || !Native.IsWindowVisible(Handle))
                return;
            var l = GetLayout();
            if (l.Window.Width <= 0 || l.Window.Height <= 0)
                return;

            using (var bitmap = new Bitmap(l.Window.Width, l.Window.Height))
            {
                using (var g = Graphics.FromImage(bitmap))
                    PaintFrame(g, l);

                // Copy only the frame strips so that the client area is never touched
                var hdc = Native.GetWindowDC(Handle);
                if (hdc == IntPtr.Zero)
                    return;
                try
                {
                    using (var g = Graphics.FromHdc(hdc))
                    {
                        var c = l.Client;
                        var strips = new[]
                        {
                            new Rectangle(0, 0, l.Window.Width, Math.Max(0, c.Top)),
                            new Rectangle(0, c.Top, Math.Max(0, c.Left), c.Height),
                            new Rectangle(c.Right, c.Top, Math.Max(0, l.Window.Width - c.Right), c.Height),
                            new Rectangle(0, c.Bottom, l.Window.Width, Math.Max(0, l.Window.Height - c.Bottom)),
                        };
                        foreach (var s in strips)
                            if (s.Width > 0 && s.Height > 0)
                                g.DrawImage(bitmap, s, s, GraphicsUnit.Pixel);
                    }
                }
                finally
                {
                    Native.ReleaseDC(Handle, hdc);
                }
            }
        }

        void PaintFrame(Graphics g, Layout l)
        {
            var border = active ? Theme.BorderActive : Theme.BorderInactive;
            var caption = active ? Theme.CaptionActive : Theme.CaptionInactive;
            var text = active ? Theme.TextActive : Theme.TextInactive;

            using (var b = new SolidBrush(caption))
                g.FillRectangle(b, l.Window);
            using (var p = new Pen(border))
                g.DrawRectangle(p, 0, 0, l.Window.Width - 1, l.Window.Height - 1);

            // Icon
            var x = l.Caption.Left + 4;
            var form = Control.FromHandle(Handle) as Form;
            var iconSize = Math.Min(16, Math.Max(0, l.Caption.Height - 4));
            if (form != null && form.Icon != null && form.ShowIcon && iconSize > 0)
            {
                using (var icon = new Icon(form.Icon, iconSize, iconSize))
                    g.DrawIcon(icon, new Rectangle(x, l.Caption.Top + (l.Caption.Height - iconSize) / 2, iconSize, iconSize));
                x += iconSize + 6;
            }

            // Title
            var buttonsLeft = (l.HasMinimize ? l.Minimize.Left : l.HasMaximize ? l.Maximize.Left : l.Close.Left) - 4;
            var textRect = new Rectangle(x, l.Caption.Top, Math.Max(0, buttonsLeft - x), l.Caption.Height);
            TextRenderer.DrawText(g, Native.WindowText(Handle), SystemFonts.CaptionFont, textRect, text, caption,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            // Buttons
            if (l.HasMinimize)
                PaintButton(g, l.Minimize, Button.Minimize, text, caption);
            if (l.HasMaximize)
                PaintButton(g, l.Maximize, Button.Maximize, text, caption);
            PaintButton(g, l.Close, Button.Close, text, caption);
        }

        void PaintButton(Graphics g, Rectangle r, Button button, Color text, Color caption)
        {
            var isPressed = tracking && pressed == button && pressedInside;
            var isHot = !tracking && hot == button;
            var bg = caption;
            var glyph = text;
            if (button == Button.Close && (isPressed || isHot))
            {
                bg = isPressed ? Theme.ClosePressed : Theme.CloseHot;
                glyph = Color.White;
            }
            else if (isPressed)
                bg = Theme.ButtonPressed;
            else if (isHot)
                bg = Theme.ButtonHot;

            using (var b = new SolidBrush(bg))
                g.FillRectangle(b, r);

            var size = Math.Max(6, (int)Math.Round(r.Height * 0.38));
            var cx = r.Left + r.Width / 2;
            var cy = r.Top + r.Height / 2;
            var box = new Rectangle(cx - size / 2, cy - size / 2, size, size);
            using (var p = new Pen(glyph, 1f))
            {
                if (button == Button.Close)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.DrawLine(p, box.Left, box.Top, box.Right, box.Bottom);
                    g.DrawLine(p, box.Right, box.Top, box.Left, box.Bottom);
                    g.SmoothingMode = SmoothingMode.None;
                }
                else if (button == Button.Minimize)
                {
                    if (Native.IsIconic(Handle))
                        DrawRestore(g, p, box);
                    else
                        g.DrawLine(p, box.Left, cy, box.Right, cy);
                }
                else if (Native.IsZoomed(Handle) || Native.IsIconic(Handle))
                    DrawRestore(g, p, box);
                else
                    g.DrawRectangle(p, box);
            }
        }

        static void DrawRestore(Graphics g, Pen p, Rectangle box)
        {
            var d = Math.Max(2, box.Width / 4);
            var front = new Rectangle(box.Left, box.Top + d, box.Width - d, box.Height - d);
            g.DrawRectangle(p, front);
            g.DrawLine(p, box.Left + d, box.Top, box.Right, box.Top);
            g.DrawLine(p, box.Right, box.Top, box.Right, box.Bottom - d);
            g.DrawLine(p, box.Left + d, box.Top, box.Left + d, box.Top + d);
            g.DrawLine(p, box.Right - d, box.Bottom - d, box.Right, box.Bottom - d);
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TRACKMOUSEEVENT
        {
            public int cbSize, dwFlags;
            public IntPtr hwndTrack;
            public int dwHoverTime;
        }

        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
        [DllImport("user32.dll")] static extern bool AdjustWindowRectEx(ref RECT rect, int style, bool menu, int exStyle);
        [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, int flags);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, int flags);

        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        public static List<IntPtr> Children(IntPtr parent)
        {
            var list = new List<IntPtr>();
            EnumChildWindows(parent, (h, _) => { list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }

        public static List<IntPtr> DirectChildren(IntPtr parent)
        {
            var list = new List<IntPtr>();
            for (var h = GetWindow(parent, 5 /* GW_CHILD */); h != IntPtr.Zero; h = GetWindow(h, 2 /* GW_HWNDNEXT */))
                list.Add(h);
            return list;
        }

        public static string ClassName(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string WindowText(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(Math.Max(1, GetWindowTextLength(hwnd) + 1));
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static Rectangle WindowRect(IntPtr hwnd)
        {
            RECT r;
            GetWindowRect(hwnd, out r);
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        public static Rectangle ClientRectOnScreen(IntPtr hwnd)
        {
            RECT r;
            GetClientRect(hwnd, out r);
            var p = new POINT();
            ClientToScreen(hwnd, ref p);
            return new Rectangle(p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top);
        }

        // Border and caption sizes for the window's style (positive values)
        public static Padding FrameInsets(IntPtr hwnd)
        {
            var r = new RECT();
            AdjustWindowRectEx(ref r, GetWindowLong(hwnd, -16), false, GetWindowLong(hwnd, -20));
            return new Padding(-r.Left, -r.Top, r.Right, r.Bottom);
        }

        public static Point ClientToScreen(IntPtr hwnd, Point client)
        {
            var p = new POINT { X = client.X, Y = client.Y };
            ClientToScreen(hwnd, ref p);
            return new Point(p.X, p.Y);
        }

        public static Point PointFromLParam(IntPtr lParam)
        {
            var v = lParam.ToInt64();
            return new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
        }

        public static void TrackNonClientLeave(IntPtr hwnd)
        {
            var tme = new TRACKMOUSEEVENT();
            tme.cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT));
            tme.dwFlags = 0x00000002 | 0x00000010; // TME_LEAVE | TME_NONCLIENT
            tme.hwndTrack = hwnd;
            TrackMouseEvent(ref tme);
        }

        // Repaints the frame (and makes Windows recalculate it)
        public static void RedrawFrame(IntPtr hwnd)
        {
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED
            RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, 0x0400 | 0x0001 | 0x0100); // RDW_FRAME|RDW_INVALIDATE|RDW_UPDATENOW
        }
    }
}
