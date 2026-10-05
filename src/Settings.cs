using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeripheralBatteryWidget;

public sealed class Settings
{
    public double Opacity { get; set; } = 0.85;          // 0.30 ~ 1.00
    public string Theme { get; set; } = "dark";          // dark | light
    public int LowThreshold { get; set; } = 20;          // %
    public bool AlertsEnabled { get; set; } = true;
    public bool Autostart { get; set; } = true;
    public bool Passthrough { get; set; } = false;
    public bool HotkeyEnabled { get; set; } = true;      // Ctrl+Alt+B 穿透开关
    public bool TopMost { get; set; } = true;
    public bool ShowWidget { get; set; } = true;
    public int PosX { get; set; } = -1;                  // -1 = 默认位置
    public int PosY { get; set; } = -1;

    // 设备昵称：key = 设备稳定标识（ble:MAC / xi:槽位 / hid:路径）
    public Dictionary<string, string> Aliases { get; set; } = new();
    public List<string> Hidden { get; set; } = new();

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PeripheralBatteryWidget", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* 损坏则重建 */ }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }
}
