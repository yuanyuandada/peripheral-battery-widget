using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace PeripheralBatteryWidget;

public sealed class OverlayForm : Form
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PeripheralBatteryWidget", "log.txt");

    private readonly Settings _settings;
    private List<DeviceInfo> _devices = new();
    private readonly List<(Rectangle Rect, string? Key)> _rowRects = new();

    private bool _dragging;
    private Point _dragCursor0, _formPos0;

    public event Action<string>? HideDeviceRequested;
    public event Action? PassthroughToggleRequested;

    public OverlayForm(Settings settings)
    {
        _settings = settings;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        Text = "PeripheralBatteryWidget";
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 恢复位置或放到主屏右下角
        var wa = Screen.PrimaryScreen!.WorkingArea;
        if (_settings.PosX >= 0 && _settings.PosY >= 0)
            Location = new Point(_settings.PosX, _settings.PosY);
        else
            Location = new Point(wa.Right - 320, wa.Bottom - 260);
        ApplyPassthroughStyle();
        TopMost = _settings.TopMost;
        Render();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Render();
    }

    public void UpdateDevices(List<DeviceInfo> devices)
    {
        _devices = devices;
        if (IsHandleCreated) Render();
    }

    public new void Show()
    {
        base.Show();
        if (IsHandleCreated) Render();
    }

    // ---------------- 穿透 ----------------
    public void RefreshSettings()
    {
        ApplyPassthroughStyle();
        TopMost = _settings.TopMost;
        if (IsHandleCreated) Render();
    }

    public void ApplyPassthroughStyle()
    {
        if (!IsHandleCreated) return;
        long ex = Win32.GetWindowLongPtr(Handle, Win32.GWL_EXSTYLE).ToInt64();
        if (_settings.Passthrough) ex |= Win32.WS_EX_TRANSPARENT;
        else ex &= ~Win32.WS_EX_TRANSPARENT;
        Win32.SetWindowLongPtr(Handle, Win32.GWL_EXSTYLE, new IntPtr(ex));
    }

    // ---------------- 渲染 ----------------
    private (Color bg, Color caption, Color text, Color barBg, Color errorText) Theme => _settings.Theme == "light"
        ? (Color.FromArgb(216, 246, 246, 248), Color.FromArgb(120, 20, 20, 20), Color.FromArgb(235, 25, 25, 28), Color.FromArgb(40, 0, 0, 0), Color.FromArgb(170, 90, 90, 90))
        : (Color.FromArgb(172, 22, 22, 26), Color.FromArgb(110, 255, 255, 255), Color.FromArgb(235, 255, 255, 255), Color.FromArgb(42, 255, 255, 255), Color.FromArgb(165, 200, 200, 200));

    private void Render()
    {
        try
        {
            if (!Visible) return;
            float s = DeviceDpi / 96f;
            int pad = (int)(12 * s), rowH = (int)(26 * s), capH = (int)(20 * s);
            int width = (int)(258 * s);
            int barW = (int)(54 * s), barH = (int)(7 * s);
            int pctW = (int)(44 * s);
            int iconD = (int)(17 * s);
            int radius = (int)(12 * s);

            var visible = _devices.OrderBy(d => d.FourLevel ? 1 : 0).ThenBy(d => d.RawName, StringComparer.CurrentCulture).ToList();

            var th = Theme;
            // 先测量总高
            int contentH = 0;
            bool exactAny = visible.Any(d => !d.FourLevel), lvlAny = visible.Any(d => d.FourLevel);
            if (exactAny) contentH += capH + visible.Count(d => !d.FourLevel) * rowH;
            if (lvlAny) contentH += (exactAny ? (int)(4 * s) : 0) + capH + visible.Count(d => d.FourLevel) * rowH;
            if (contentH == 0) contentH = rowH;
            int height = contentH + pad * 2;

            using var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // 圆角卡片
            using (var card = new GraphicsPath())
            {
                MgRound(card, 0, 0, width - 1, height - 1, radius);
                using var b = new SolidBrush(th.bg);
                g.FillPath(b, card);
            }

            _rowRects.Clear();
            float y = pad;
            using var fCap = new Font("Microsoft YaHei UI", 7.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            using var fName = new Font("Microsoft YaHei UI", 9f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            using var fSmall = new Font("Microsoft YaHei UI", 7.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            using var fIcon = new Font("Microsoft YaHei UI", 8.5f * s, FontStyle.Bold, GraphicsUnit.Pixel);

            void DrawGroup(string caption, bool four)
            {
                var rows = visible.Where(d => d.FourLevel == four).ToList();
                if (rows.Count == 0) return;
                using var b = new SolidBrush(th.caption);
                g.DrawString(caption, fCap, b, pad, y + 2 * s);
                y += capH;
                foreach (var d in rows)
                {
                    var rowTop = (int)y;
                    // 图标
                    var (glyph, iconColor) = IconFor(d.RawName);
                    using (var ib = new SolidBrush(iconColor))
                        g.FillEllipse(ib, pad, y + (rowH - iconD) / 2, iconD, iconD);
                    TextRendererDraw(g, glyph, fIcon, Color.White,
                        new Rectangle(pad, (int)(y + (rowH - iconD) / 2), iconD, iconD),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                    // 名称
                    int nameX = pad + iconD + (int)(7 * s);
                    int rightEdge = width - pad;
                    int pctLeft = rightEdge - pctW;
                    int barLeft = pctLeft - (int)(8 * s) - barW;
                    int tagLeft = barLeft - (int)(8 * s) - (int)(34 * s);
                    var nameRect = new Rectangle(nameX, (int)y, tagLeft - nameX - (int)(6 * s), rowH);
                    TextRendererDraw(g, DisplayName(d), fName, th.text, nameRect,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                    // 连接方式 / 充电中
                    bool charging = d.Charging == true;
                    string tag = charging ? "充电中" : d.Transport;
                    var tagColor = charging ? Color.FromArgb(120, 220, 130) : d.Transport switch
                    {
                        "蓝牙" => Color.FromArgb(130, 175, 255),
                        "接收器" => Color.FromArgb(195, 150, 255),
                        _ => Color.FromArgb(170, 170, 170)
                    };
                    TextRendererDraw(g, tag, fSmall, tagColor, new Rectangle(tagLeft, (int)y, (int)(34 * s), rowH),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                    // 百分比 / 不支持
                    if (d.Percent is int p)
                    {
                        string txt = (d.FourLevel ? "≈" : "") + p + "%";
                        TextRendererDraw(g, txt, fName, th.text, new Rectangle(pctLeft, (int)y, pctW, rowH),
                            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        // 电量条
                        int barY = (int)(y + (rowH - barH) / 2);
                        if (four)
                        {
                            int seg = (barW - (int)(3 * 2 * s)) / 4;
                            for (int i = 0; i < 4; i++)
                            {
                                var r = new Rectangle(barLeft + i * (seg + (int)(2 * s)), barY, seg, barH);
                                using var bgb = new SolidBrush(th.barBg);
                                MgRoundFill(g, bgb, r, 2 * s);
                                if (p >= (i + 1) * 25 - 12 || (i == 0 && p > 0))
                                {
                                    using var fb = new SolidBrush(BarColor(p));
                                    MgRoundFill(g, fb, r, 2 * s);
                                }
                            }
                        }
                        else
                        {
                            var full = new Rectangle(barLeft, barY, barW, barH);
                            using var bgb = new SolidBrush(th.barBg);
                            MgRoundFill(g, bgb, full, barH / 2f);
                            int w = (int)(barW * Math.Clamp(p / 100f, 0.02f, 1f));
                            if (w > 0)
                            {
                                var fillR = new Rectangle(barLeft, barY, w, barH);
                                using var fb = new SolidBrush(BarColor(p));
                                MgRoundFill(g, fb, fillR, barH / 2f);
                            }
                        }
                    }
                    else
                    {
                        TextRendererDraw(g, "不支持", fSmall, th.errorText, new Rectangle(pctLeft, (int)y, pctW, rowH),
                            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }

                    _rowRects.Add((new Rectangle(0, rowTop, width, rowH), d.Key));
                    y += rowH;
                }
            }

            DrawGroup("精确电量", four: false);
            if (exactAny && lvlAny) y += (int)(4 * s);
            DrawGroup("档位电量", four: true);
            if (contentH == rowH)
            {
                TextRendererDraw(g, "未发现可读取电量的外设", fSmall, th.caption, new Rectangle(pad, (int)y, width - pad * 2, rowH),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }

            PushToScreen(bmp, Location);
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss") + " Render异常: " + ex + "\r\n"); } catch { }
        }
    }

    private static Color BarColor(int p) => p > 50 ? Color.FromArgb(96, 205, 96) : p > 20 ? Color.FromArgb(255, 193, 7) : Color.FromArgb(240, 80, 80);

    private static (string glyph, Color color) IconFor(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("手柄") || n.Contains("pad") || n.Contains("controller") || n.Contains("game")) return ("柄", Color.FromArgb(70, 120, 200));
        if (n.Contains("键") || n.Contains("keyboard") || n.Contains("x75")) return ("键", Color.FromArgb(120, 90, 200));
        if (n.Contains("鼠") || n.Contains("mouse") || n.Contains("a7")) return ("鼠", Color.FromArgb(40, 150, 140));
        if (n.Contains("耳") || n.Contains("head") || n.Contains("buds") || n.Contains("pods")) return ("耳", Color.FromArgb(200, 110, 90));
        return ("外", Color.FromArgb(120, 120, 130));
    }

    private string DisplayName(DeviceInfo d) => _settings.Aliases.TryGetValue(d.Key, out var alias) && alias.Length > 0 ? alias : d.RawName;

    private static void MgRound(GraphicsPath p, int x, int y, int w, int h, int r)
    {
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
    }

    private static void MgRoundFill(Graphics g, Brush b, Rectangle r, float radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var p = new GraphicsPath();
        int rr = (int)Math.Min(radius, Math.Min(r.Width, r.Height) / 2f);
        p.AddArc(r.X, r.Y, rr * 2, rr * 2, 180, 90);
        p.AddArc(r.X + r.Width - rr * 2, r.Y, rr * 2, rr * 2, 270, 90);
        p.AddArc(r.X + r.Width - rr * 2, r.Y + r.Height - rr * 2, rr * 2, rr * 2, 0, 90);
        p.AddArc(r.X, r.Y + r.Height - rr * 2, rr * 2, rr * 2, 90, 90);
        p.CloseFigure();
        g.FillPath(b, p);
    }

    private static void TextRendererDraw(Graphics g, string text, Font f, Color color, Rectangle r, TextFormatFlags flags)
    {
        // PArgb 透明位图上用 GDI+ DrawString（TextRenderer 的 ClearType 会产生黑边）
        var sf = new StringFormat { Trimming = StringTrimming.None };
        if ((flags & TextFormatFlags.HorizontalCenter) != 0) sf.Alignment = StringAlignment.Center;
        else if ((flags & TextFormatFlags.Right) != 0) sf.Alignment = StringAlignment.Far;
        if ((flags & TextFormatFlags.EndEllipsis) != 0) sf.Trimming = StringTrimming.EllipsisCharacter;
        using var br = new SolidBrush(color);
        var rf = new RectangleF(r.X, r.Y - 1f, r.Width, r.Height + 2f);
        g.DrawString(text, f, br, rf, sf);
    }

    // ---------------- 分层窗口推送 ----------------
    private void PushToScreen(Bitmap bmp, Point pos)
    {
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        try
        {
            IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
            try
            {
                IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
                try
                {
                    IntPtr old = Win32.SelectObject(memDc, hbmp);
                    var size = new Win32.SIZE(bmp.Width, bmp.Height);
                    var src = new Win32.POINT(0, 0);
                    var dst = new Win32.POINT(pos.X, pos.Y);
                    var blend = new Win32.BLENDFUNCTION
                    {
                        BlendOp = Win32.AC_SRC_OVER,
                        BlendFlags = 0,
                        SourceConstantAlpha = (byte)Math.Clamp((int)(_settings.Opacity * 255), 30, 255),
                        AlphaFormat = Win32.AC_SRC_ALPHA,
                    };
                    bool ulwOk = Win32.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Win32.ULW_ALPHA);
                    if (!ulwOk) File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss") + " ULW失败 err=" + Marshal.GetLastWin32Error() + "\r\n");
                    Win32.SelectObject(memDc, old);
                }
                finally { Win32.DeleteObject(hbmp); }
            }
            finally { Win32.DeleteDC(memDc); }
        }
        finally { Win32.ReleaseDC(IntPtr.Zero, screenDc); }
    }

    // ---------------- 交互 ----------------
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && !_settings.Passthrough)
        {
            _dragging = true;
            _dragCursor0 = Cursor.Position;
            _formPos0 = Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging && e.Button == MouseButtons.Left)
        {
            Location = new Point(_formPos0.X + Cursor.Position.X - _dragCursor0.X,
                                 _formPos0.Y + Cursor.Position.Y - _dragCursor0.Y);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _dragging)
        {
            _dragging = false;
            _settings.PosX = Location.X;
            _settings.PosY = Location.Y;
            _settings.Save();
        }
        else if (e.Button == MouseButtons.Right)
        {
            ShowContextMenu(e.Location);
        }
    }

    private ContextMenuStrip? _menu;
    private void ShowContextMenu(Point at)
    {
        _menu?.Dispose();
        _menu = new ContextMenuStrip();

        string? hitKey = _rowRects.FirstOrDefault(r => r.Rect.Contains(at)).Key;

        if (hitKey != null)
        {
            var miRename = new ToolStripMenuItem("重命名…") { Font = new Font("Microsoft YaHei UI", 9f) };
            miRename.Click += (_, _) => RenameDevice(hitKey);
            var miHide = new ToolStripMenuItem("隐藏此设备") { Font = new Font("Microsoft YaHei UI", 9f) };
            miHide.Click += (_, _) => { _settings.Hidden.Add(hitKey); _settings.Save(); HideDeviceRequested?.Invoke(hitKey); };
            _menu.Items.Add(miRename);
            _menu.Items.Add(miHide);
            _menu.Items.Add(new ToolStripSeparator());
        }

        var miPass = new ToolStripMenuItem("鼠标穿透 (Ctrl+Alt+B)") { Checked = _settings.Passthrough, Font = new Font("Microsoft YaHei UI", 9f) };
        miPass.Click += (_, _) => PassthroughToggleRequested?.Invoke();
        var miTop = new ToolStripMenuItem("置顶显示") { Checked = _settings.TopMost, Font = new Font("Microsoft YaHei UI", 9f) };
        miTop.Click += (_, _) => { _settings.TopMost = !_settings.TopMost; TopMost = _settings.TopMost; _settings.Save(); };
        var miSettings = new ToolStripMenuItem("设置…") { Font = new Font("Microsoft YaHei UI", 9f) };
        miSettings.Click += (_, _) => SettingsRequested?.Invoke();
        var miExit = new ToolStripMenuItem("退出") { Font = new Font("Microsoft YaHei UI", 9f) };
        miExit.Click += (_, _) => ExitRequested?.Invoke();

        _menu.Items.AddRange(new ToolStripItem[] { miPass, miTop, new ToolStripSeparator(), miSettings, miExit });
        _menu.Show(this, at);
    }

    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    private void RenameDevice(string key)
    {
        var cur = _settings.Aliases.TryGetValue(key, out var a) ? a : "";
        using var dlg = new RenameDialog(cur);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            if (string.IsNullOrWhiteSpace(dlg.Value)) _settings.Aliases.Remove(key);
            else _settings.Aliases[key] = dlg.Value.Trim();
            _settings.Save();
            Render();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 托盘应用：点关闭=隐藏而不是退出
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_HOTKEY && m.WParam.ToInt32() == Win32.HOTKEY_ID)
        {
            PassthroughToggleRequested?.Invoke();
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Win32.UnregisterHotKey(Handle, Win32.HOTKEY_ID);
            _menu?.Dispose();
        }
        base.Dispose(disposing);
    }
}

public sealed class RenameDialog : Form
{
    private readonly TextBox _tb = new() { Left = 12, Top = 34, Width = 280 };
    public string Value => _tb.Text;

    public RenameDialog(string current)
    {
        Text = "设备昵称";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(304, 108);
        var label = new Label { Text = "给这个设备起个好认的名字：", Left = 12, Top = 10, AutoSize = true };
        _tb.Text = current;
        var ok = new Button { Text = "确定", Left = 130, Top = 70, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Left = 215, Top = 70, Width = 75, DialogResult = DialogResult.Cancel };
        CancelButton = cancel;
        Controls.AddRange(new Control[] { label, _tb, ok, cancel });
        AcceptButton = ok;
        Font = new Font("Microsoft YaHei UI", 9f);
    }
}

internal static class Win32
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_LAYERED = 0x00080000;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_HOTKEY = 0x0312;
    public const int HOTKEY_ID = 0xB22B;
    public const uint MOD_CONTROL = 0x0002, MOD_ALT = 0x0001;
    public const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
    public const uint ULW_ALPHA = 2;

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern int DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern int DeleteObject(IntPtr obj);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, uint dwFlags);
}
