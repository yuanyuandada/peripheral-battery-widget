using Microsoft.Win32;
using System.Drawing;

namespace PeripheralBatteryWidget;

public sealed class TrayContext : ApplicationContext
{
    private readonly Settings _s;
    private readonly DeviceMonitor _monitor = new();
    private readonly OverlayForm _w;
    private readonly NotifyIcon _ni;
    private readonly Dictionary<string, DateTime> _lowSince = new();
    private List<DeviceInfo> _last = new();
    private ContextMenuStrip? _trayMenu;
    private bool _hotkeyRegistered;

    public TrayContext()
    {
        _s = Settings.Load();
        _w = new OverlayForm(_s);
        _w.HideDeviceRequested += _ => ApplyFilter();
        _w.PassthroughToggleRequested += TogglePassthrough;
        _w.SettingsRequested += ShowSettings;
        _w.ExitRequested += ExitApp;

        _monitor.DevicesChanged += OnDevices;

        _ni = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "外设电量挂件",
            Visible = true,
        };
        BuildTrayMenu();
        _ni.ContextMenuStrip = _trayMenu;
        _ni.DoubleClick += (_, _) => ShowSettings();

        RegisterHotkey();
        ApplyAutostart();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        if (_s.ShowWidget) _w.Show();
    }

    private string DisplayName(DeviceInfo d)
        => _s.Aliases.TryGetValue(d.Key, out var a) && a.Length > 0 ? a : d.RawName;

    private void OnDevices(List<DeviceInfo> devices)
    {
        _last = devices;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var vis = _last.Where(d => !_s.Hidden.Contains(d.Key)).ToList();
        _w.UpdateDevices(vis);
        CheckAlerts(vis);
    }

    private void CheckAlerts(List<DeviceInfo> devices)
    {
        if (!_s.AlertsEnabled) { _lowSince.Clear(); return; }
        var now = DateTime.Now;
        foreach (var d in devices)
        {
            if (d.Percent is not int p) continue;
            bool low = p <= _s.LowThreshold && d.Charging != true;
            if (low)
            {
                bool known = _lowSince.TryGetValue(d.Key, out var t);
                if (!known || (now - t).TotalMinutes >= 30)
                {
                    _ni.ShowBalloonTip(8000, "外设电量提醒",
                        $"{DisplayName(d)} 电量约 {p}%，请尽快充电", ToolTipIcon.Warning);
                    _lowSince[d.Key] = now;
                }
            }
            else _lowSince.Remove(d.Key);
        }
    }

    private void BuildTrayMenu()
    {
        _trayMenu?.Dispose();
        _trayMenu = new ContextMenuStrip();
        var font = new Font("Microsoft YaHei UI", 9f);

        var miShow = new ToolStripMenuItem("显示/隐藏悬浮窗") { Font = font, Checked = _s.ShowWidget };
        miShow.Click += (_, _) => ToggleWidget();
        var miPass = new ToolStripMenuItem("鼠标穿透 (Ctrl+Alt+B)") { Font = font, Checked = _s.Passthrough };
        miPass.Click += (_, _) => TogglePassthrough();
        var miRefresh = new ToolStripMenuItem("立即刷新") { Font = font };
        miRefresh.Click += (_, _) => _ = _monitor.RefreshAsync();
        var miSettings = new ToolStripMenuItem("设置…") { Font = font };
        miSettings.Click += (_, _) => ShowSettings();
        var miAutostart = new ToolStripMenuItem("开机自启") { Font = font, Checked = _s.Autostart };
        miAutostart.Click += (_, _) => { _s.Autostart = !_s.Autostart; _s.Save(); ApplyAutostart(); BuildTrayMenu(); };
        var miExit = new ToolStripMenuItem("退出") { Font = font };
        miExit.Click += (_, _) => ExitApp();

        _trayMenu.Items.AddRange(new ToolStripItem[] { miShow, miPass, miRefresh, new ToolStripSeparator(), miSettings, miAutostart, new ToolStripSeparator(), miExit });
    }

    private void ToggleWidget()
    {
        _s.ShowWidget = !_s.ShowWidget;
        _s.Save();
        if (_s.ShowWidget) { _w.Show(); _w.RefreshSettings(); }
        else _w.Hide();
        BuildTrayMenu();
    }

    private void TogglePassthrough()
    {
        _s.Passthrough = !_s.Passthrough;
        _s.Save();
        _w.ApplyPassthroughStyle();
        BuildTrayMenu();
    }

    private void RegisterHotkey()
    {
        if (_hotkeyRegistered)
        {
            Win32.UnregisterHotKey(_w.Handle, Win32.HOTKEY_ID);
            _hotkeyRegistered = false;
        }
        if (_s.HotkeyEnabled && _w.IsHandleCreated)
            _hotkeyRegistered = Win32.RegisterHotKey(_w.Handle, Win32.HOTKEY_ID, Win32.MOD_CONTROL | Win32.MOD_ALT, 'B');
    }

    private void ApplyAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null) return;
            if (_s.Autostart)
                key.SetValue("PeripheralBatteryWidget", $"\"{Application.ExecutablePath}\"");
            else
                key.DeleteValue("PeripheralBatteryWidget", false);
        }
        catch { /* 注册表失败不致命 */ }
    }

    private void ShowSettings()
    {
        var names = _last.ToDictionary(d => d.Key, DisplayName);
        using var f = new SettingsForm(_s, names);
        if (f.ShowDialog() == DialogResult.OK)
        {
            _w.RefreshSettings();
            RegisterHotkey();
            ApplyAutostart();
            ApplyFilter();
            BuildTrayMenu();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _ = _monitor.RefreshAsync();
    }

    private static Icon LoadAppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Information; }
        catch { return SystemIcons.Information; }
    }

    private void ExitApp()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _monitor.Dispose();
        if (_hotkeyRegistered) Win32.UnregisterHotKey(_w.Handle, Win32.HOTKEY_ID);
        _ni.Visible = false;
        _ni.Dispose();
        _w.Dispose();
        _s.Save();
        Application.Exit();
    }
}
