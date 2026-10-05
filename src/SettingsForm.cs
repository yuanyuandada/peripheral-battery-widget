using System.Drawing;

namespace PeripheralBatteryWidget;

public sealed class SettingsForm : Form
{
    private readonly Settings _s;
    private readonly Dictionary<string, string> _hiddenNames;

    private readonly TrackBar _opacity = new() { Left = 110, Top = 14, Width = 190, TickStyle = TickStyle.None, Minimum = 30, Maximum = 100 };
    private readonly Label _opacityVal = new() { Left = 306, Top = 34, Width = 40, AutoSize = true };
    private readonly ComboBox _theme = new() { Left = 110, Top = 52, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _threshold = new() { Left = 110, Top = 86, Width = 60, Minimum = 5, Maximum = 50 };
    private readonly CheckBox _alerts = new() { Left = 110, Top = 116, AutoSize = true, Text = "启用低电量提醒" };
    private readonly CheckBox _topmost = new() { Left = 110, Top = 142, AutoSize = true, Text = "窗口置顶显示" };
    private readonly CheckBox _autostart = new() { Left = 110, Top = 168, AutoSize = true, Text = "开机自动启动" };
    private readonly CheckBox _hotkey = new() { Left = 110, Top = 194, AutoSize = true, Text = "穿透快捷键 Ctrl+Alt+B" };
    private readonly ListBox _hidden = new() { Left = 110, Top = 228, Width = 200, Height = 82, SelectionMode = SelectionMode.One };
    private readonly Button _restore = new() { Left = 318, Top = 262, Width = 52, Height = 26, Text = "恢复" };

    public SettingsForm(Settings s, Dictionary<string, string> hiddenNames)
    {
        _s = s;
        _hiddenNames = hiddenNames;
        Text = "外设电量挂件 — 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(392, 372);
        Font = new Font("Microsoft YaHei UI", 9f);

        _opacity.Value = Math.Clamp((int)(s.Opacity * 100), 30, 100);
        _opacity.ValueChanged += (_, _) => _opacityVal.Text = _opacity.Value + "%";
        _opacityVal.Text = _opacity.Value + "%";

        _theme.Items.AddRange(new object[] { "深色", "浅色" });
        _theme.SelectedIndex = s.Theme == "light" ? 1 : 0;
        _threshold.Value = Math.Clamp(s.LowThreshold, 5, 50);
        _alerts.Checked = s.AlertsEnabled;
        _topmost.Checked = s.TopMost;
        _autostart.Checked = s.Autostart;
        _hotkey.Checked = s.HotkeyEnabled;

        foreach (var k in s.Hidden) _hidden.Items.Add(_hiddenNames.TryGetValue(k, out var n) ? n : k);
        _restore.Click += (_, _) =>
        {
            if (_hidden.SelectedItem is null) return;
            int idx = _hidden.SelectedIndex;
            var key = s.Hidden[idx];
            s.Hidden.Remove(key);
            _hidden.Items.RemoveAt(idx);
        };

        var ok = new Button { Text = "确定", Left = 200, Top = 330, Width = 82, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Left = 292, Top = 330, Width = 82, DialogResult = DialogResult.Cancel };
        AcceptButton = ok; CancelButton = cancel;
        ok.Click += (_, _) => Apply();

        Controls.AddRange(new Control[]
        {
            new Label { Text = "不透明度：", Left = 16, Top = 36, AutoSize = true },
            _opacity, _opacityVal,
            new Label { Text = "主题：", Left = 16, Top = 55, AutoSize = true },
            _theme,
            new Label { Text = "低电量阈值：", Left = 16, Top = 88, AutoSize = true },
            _threshold,
            new Label { Text = "%", Left = 176, Top = 88, AutoSize = true },
            _alerts, _topmost, _autostart, _hotkey,
            new Label { Text = "已隐藏设备：", Left = 16, Top = 230, AutoSize = true },
            _hidden, _restore,
            ok, cancel,
        });
    }

    private void Apply()
    {
        _s.Opacity = _opacity.Value / 100.0;
        _s.Theme = _theme.SelectedIndex == 1 ? "light" : "dark";
        _s.LowThreshold = (int)_threshold.Value;
        _s.AlertsEnabled = _alerts.Checked;
        _s.TopMost = _topmost.Checked;
        _s.Autostart = _autostart.Checked;
        _s.HotkeyEnabled = _hotkey.Checked;
        _s.Save();
    }
}
