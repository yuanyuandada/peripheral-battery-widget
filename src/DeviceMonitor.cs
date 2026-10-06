using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace PeripheralBatteryWidget;

public sealed class DeviceInfo
{
    public required string Key { get; init; }        // 稳定标识: ble:MAC / xi:槽位
    public required string RawName { get; init; }
    public required string Transport { get; init; }  // 蓝牙 / 接收器 / 有线
    public int? Percent { get; set; }                // null = 读不到
    public bool FourLevel { get; init; }             // 档位精度（≈25/50/75/100）
    public bool? Charging { get; set; }
    public string? Error { get; set; }               // 读不到时的原因说明
    public DateTime LastSeen { get; set; } = DateTime.Now;
}

public sealed class DeviceMonitor : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>每次扫描后的设备列表（未过滤隐藏项）</summary>
    public event Action<List<DeviceInfo>>? DevicesChanged;

    public DeviceMonitor()
    {
        // 启动3秒后首次扫描，之后每60秒
        _timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null, 3_000, 60_000);
    }

    public Task RefreshAsync() => RefreshAsync(forceFirst: false);

    public async Task RefreshAsync(bool forceFirst)
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            var list = await CollectAsync();
            DevicesChanged?.Invoke(list);
        }
        catch { /* 扫描失败保留旧数据 */ }
        finally { _gate.Release(); }
    }

    private static async Task<List<DeviceInfo>> CollectAsync()
    {
        var bleTask = CollectBleAsync();
        var gsTask = Task.Run(CollectGameSir);
        var xi = CollectXInput();
        var ble = await bleTask;
        var gs = await gsTask;
        var list = new List<DeviceInfo>(ble.Count + xi.Count + gs.Count);
        list.AddRange(ble);
        // 盖世小鸡 T4N2 通道识别到手柄后，压掉 XInput 的"有线桥"行
        // （那是同一手柄的接收器/有线身份，永远没有电量数据）
        if (gs.Count > 0)
            xi.RemoveAll(d => d.Percent == null);
        list.AddRange(xi);
        list.AddRange(gs);
        return list;
    }

    // ---------- 通道1: BLE 标准电量服务 ----------
    private static async Task<List<DeviceInfo>> CollectBleAsync()
    {
        var result = new List<DeviceInfo>();
        try
        {
            string aqs = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var devs = await DeviceInformation.FindAllAsync(aqs);
            foreach (var d in devs)
            {
                if (d.Id == null || !d.Id.StartsWith("BluetoothLE#", StringComparison.OrdinalIgnoreCase)) continue;
                var macMatch = Regex.Matches(d.Id, @"[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}");
                if (macMatch.Count == 0) continue;
                ulong addr = Convert.ToUInt64(macMatch[^1].Value.Replace(":", ""), 16);
                string mac = macMatch[^1].Value.ToUpperInvariant();
                string name = string.IsNullOrWhiteSpace(d.Name) ? "蓝牙设备" : d.Name;
                var info = new DeviceInfo { Key = "ble:" + mac, RawName = name, Transport = "蓝牙" };

                try
                {
                    var ble = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
                    if (ble == null) { info.Error = "暂时无法连接"; result.Add(info); continue; }
                    using (ble)
                    {
                        var svcResult = await ble.GetGattServicesForUuidAsync(GattServiceUuids.Battery);
                        if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
                        {
                            // 无电量服务的设备不列出（没有可显示的内容）
                            continue;
                        }
                        var chrResult = await svcResult.Services[0]
                            .GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel);
                        if (chrResult.Status == GattCommunicationStatus.Success && chrResult.Characteristics.Count > 0)
                        {
                            var read = await chrResult.Characteristics[0].ReadValueAsync();
                            if (read.Status == GattCommunicationStatus.Success)
                            {
                                var reader = DataReader.FromBuffer(read.Value);
                                info.Percent = reader.ReadByte();
                            }
                            else info.Error = "读取失败 (" + read.Status + ")";
                        }
                        else info.Error = "读取失败 (" + chrResult.Status + ")";

                        // 可选：充电状态 (0x2BED Battery Level Status)
                        try
                        {
                            var bls = await svcResult.Services[0]
                                .GetCharacteristicsForUuidAsync(Guid.Parse("00002bed-0000-1000-8000-00805f9b34fb"));
                            if (bls.Status == GattCommunicationStatus.Success && bls.Characteristics.Count > 0)
                            {
                                var r2 = await bls.Characteristics[0].ReadValueAsync();
                                if (r2.Status == GattCommunicationStatus.Success && r2.Value.Length >= 3)
                                {
                                    var rd = DataReader.FromBuffer(r2.Value);
                                    rd.ReadByte();
                                    rd.ByteOrder = ByteOrder.LittleEndian;
                                    ushort powerState = rd.ReadUInt16();
                                    info.Charging = (powerState & 0x0001) != 0;
                                }
                            }
                        }
                        catch { /* 可选特性 */ }
                        result.Add(info);
                    }
                }
                catch (Exception ex)
                {
                    info.Error = "读取异常: " + ex.Message;
                    result.Add(info);
                }
            }
        }
        catch { /* 蓝牙栈暂时不可用 */ }
        return result;
    }

    // ---------- 通道2: XInput（Xbox 360 式 2.4G 接收器 / USB 有线） ----------
    private static List<DeviceInfo> CollectXInput()
    {
        var list = new List<DeviceInfo>();
        for (int slot = 0; slot < 4; slot++)
        {
            try
            {
                int rc = XInputNative.XInputGetBatteryInformation(slot, 0, out var info);
                if (rc != 0) continue;
                if (info.BatteryType == 0x00) continue; // 槽位无手柄
                bool wired = info.BatteryType == 0x01;
                var dev = new DeviceInfo
                {
                    Key = "xi:" + slot,
                    RawName = "手柄 (2.4G接收器)",
                    Transport = wired ? "接收器" : "接收器",
                    FourLevel = true,
                };
                if (wired) dev.Error = "该接收器不公开电量数据（厂商私有协议）";
                else dev.Percent = info.BatteryLevel switch { 0 => 15, 1 => 40, 2 => 65, 3 => 90, _ => null };
                list.Add(dev);
            }
            catch { /* XInput 不可用 */ }
        }
        return list;
    }

    // ---------- 通道3: 盖世小鸡 T4N2（Nova 2 Lite 等）精确电量 ----------
    private static List<DeviceInfo> CollectGameSir() => GameSirHid.Collect();

    public void Dispose() => _timer.Dispose();
}

internal static class XInputNative
{
    // xinput1_4 在 Win10/11 一定存在；失败则回退 1_3
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation", SetLastError = false)]
    private static extern int GetBattery14(int userIndex, byte devType, out BatteryInfo info);

    [DllImport("xinput1_3.dll", EntryPoint = "XInputGetBatteryInformation", SetLastError = false)]
    private static extern int GetBattery13(int userIndex, byte devType, out BatteryInfo info);

    [StructLayout(LayoutKind.Sequential)]
    public struct BatteryInfo { public byte BatteryType; public byte BatteryLevel; }

    public static int XInputGetBatteryInformation(int userIndex, byte devType, out BatteryInfo info)
    {
        try { return GetBattery14(userIndex, devType, out info); }
        catch (DllNotFoundException) { return GetBattery13(userIndex, devType, out info); }
    }
}
