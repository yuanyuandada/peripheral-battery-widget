using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== 外设电量探针 v2 ===");
Console.WriteLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine();

await ProbeBleAsync();
ProbeXInput();
ProbeHidAsync().GetAwaiter().GetResult();

Console.WriteLine();
Console.WriteLine("=== 探针结束 ===");

// ---------------- 通道1: 蓝牙BLE标准电量服务 (0x180F / 0x2A19) ----------------
static async Task ProbeBleAsync()
{
    Console.WriteLine("---- 通道1: 蓝牙BLE标准电量服务 (GATT 0x180F) ----");
    try
    {
        string aqs = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
        var devs = await DeviceInformation.FindAllAsync(aqs);
        if (devs.Count == 0) Console.WriteLine("(未发现已连接的BLE设备)");
        foreach (var d in devs)
        {
            var macMatch = Regex.Matches(d.Id ?? "", @"[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}");
            if (macMatch.Count == 0) continue;
            ulong addr = Convert.ToUInt64(macMatch[^1].Value.Replace(":", ""), 16);
            string mac = macMatch[^1].Value.ToUpperInvariant();
            string name = string.IsNullOrWhiteSpace(d.Name) ? "蓝牙设备" : d.Name;

            var ble = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
            if (ble == null) { Console.WriteLine($"[BLE] {name}: 无法打开"); continue; }
            using (ble)
            {
                var svcResult = await ble.GetGattServicesForUuidAsync(GattServiceUuids.Battery);
                if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
                {
                    Console.WriteLine($"[BLE] {name} ({mac}) — 无电量服务 status={svcResult.Status}");
                    continue;
                }
                var chrResult = await svcResult.Services[0].GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel);
                if (chrResult.Status == GattCommunicationStatus.Success && chrResult.Characteristics.Count > 0)
                {
                    var read = await chrResult.Characteristics[0].ReadValueAsync();
                    if (read.Status == GattCommunicationStatus.Success)
                    {
                        byte level = DataReader.FromBuffer(read.Value).ReadByte();
                        Console.WriteLine($"[BLE] {name} ({mac}) — 精确电量: {level}%");
                    }
                    else Console.WriteLine($"[BLE] {name}: 读失败 status={read.Status}");
                }
                else Console.WriteLine($"[BLE] {name}: 无电量特征值 status={chrResult.Status}");
            }
        }
    }
    catch (Exception ex) { Console.WriteLine($"BLE通道异常: {ex}"); }
    Console.WriteLine();
}

// ---------------- 通道2: XInput (Xbox 360 式接收器/有线) ----------------
static void ProbeXInput()
{
    Console.WriteLine("---- 通道2: XInput 电量档位 ----");
    try
    {
        for (int slot = 0; slot < 4; slot++)
        {
            int rc = XI.XInputGetBatteryInformation(slot, 0, out var info);
            if (rc != 0) continue;
            if (info.BatteryType == 0x00) { Console.WriteLine($"[XInput] 槽位{slot}: 无手柄"); continue; }
            string type = info.BatteryType switch { 0x01 => "有线", 0x02 => "碱性电池", 0x03 => "镍氢电池", 0xFF => "未知", _ => $"0x{info.BatteryType:X2}" };
            Console.WriteLine($"[XInput] 槽位{slot}: {type} — 档位 {info.BatteryLevel} (0空1低2中3满)");
        }
    }
    catch (Exception ex) { Console.WriteLine($"XInput通道异常: {ex.Message}"); }
    Console.WriteLine();
}

// ---------------- 通道4: HID 打捞 ----------------
static async Task ProbeHidAsync()
{
    Console.WriteLine("---- 通道4: HID 私有通道打捞 (VID_3537) ----");
    var hidClass = new Guid(0x4d1e55b2, 0xf16f, 0x11cf, 0x88, 0xcb, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);
    string[] paths = HidNative.GetDeviceInterfacePaths(hidClass);
    Console.WriteLine($"HID 接口总数: {paths.Length}");
    foreach (var p in paths)
    {
        var m = Regex.Match(p, @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
        if (!m.Success) continue;
        ushort vid = Convert.ToUInt16(m.Groups[1].Value, 16);
        ushort pid = Convert.ToUInt16(m.Groups[2].Value, 16);
        if (vid != 0x3537) continue;
        Console.WriteLine($"\n[HID] VID=0x{vid:X4} PID=0x{pid:X4}");
        HidNative.Dump(p);
    }
    await Task.CompletedTask;
}


internal static class XI
{
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    public static extern int XInputGetBatteryInformation(int userIndex, byte devType, out BatteryInfo info);

    [StructLayout(LayoutKind.Sequential)]
    public struct BatteryInfo { public byte BatteryType; public byte BatteryLevel; }
}

internal class HidNative
{
    [DllImport("Cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List(ref Guid interfaceClass, string? deviceID, char[] buffer, uint bufferLen, uint flags);

    public static string[] GetDeviceInterfacePaths(Guid classGuid)
    {
        for (uint len = 4096; len <= 262144; len *= 2)
        {
            var buf = new char[len];
            int cr = CM_Get_Device_Interface_List(ref classGuid, null, buf, len, 0);
            if (cr == 0)
            {
                string all = new string(buf);
                return all.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            if (cr != 0x1A) return Array.Empty<string>();
        }
        return Array.Empty<string>();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr pp, ref HIDP_CAPS caps);
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, uint len);
    [DllImport("hid.dll")] private static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, uint len);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint len, out uint read, ref System.Threading.NativeOverlapped ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(SafeFileHandle h, ref System.Threading.NativeOverlapped ov, out uint bytes, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateEventW(IntPtr attrs, int manual, int initial, string? name);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort UsagePage; public ushort Usage;
        public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps, NumberInputValueCaps;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps;
    }

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, SHARE_RW = 3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int HIDP_SUCCESS = 0x00110000;

    public static void Dump(string path)
    {
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            h = CreateFileW(path, GENERIC_READ, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { Console.WriteLine($"  打开失败 err={Marshal.GetLastWin32Error()}"); return; }
        }
        using (h)
        {
            if (!HidD_GetPreparsedData(h, out var pp)) { Console.WriteLine("  GetPreparsedData 失败"); return; }
            try
            {
                var caps = new HIDP_CAPS();
                int hr = HidP_GetCaps(pp, ref caps);
                if (hr != HIDP_SUCCESS) { Console.WriteLine($"  HidP_GetCaps 失败 hr=0x{hr:X8}"); return; }
                Console.WriteLine($"  用法页=0x{caps.UsagePage:X4} 用法=0x{caps.Usage:X4} in={caps.InputReportByteLength}B out={caps.OutputReportByteLength}B feat={caps.FeatureReportByteLength}B");

                // 特征报表打捞
                if (caps.FeatureReportByteLength > 1)
                {
                    int flen = caps.FeatureReportByteLength;
                    for (byte rid = 0; rid < 0x40; rid++)
                    {
                        var buf = new byte[flen];
                        buf[0] = rid;
                        if (HidD_GetFeature(h, buf, (uint)flen) && AnyNonZero(buf))
                            Console.WriteLine($"    [特征] ID 0x{rid:X2}: {Convert.ToHexString(buf, 0, Math.Min(flen, 20)).ToLowerInvariant()}");
                    }
                }

                // 输入流打捞（需重开 OVERLAPPED 句柄）
                if (caps.InputReportByteLength >= 16)
                {
                    h.Dispose();
                    DumpInputStream(path, caps);
                }
            }
            finally { HidD_FreePreparsedData(pp); }
        }
    }

    private static bool AnyNonZero(byte[] b) { for (int i = 1; i < b.Length; i++) if (b[i] != 0) return true; return false; }

    private static void DumpInputStream(string path, HIDP_CAPS caps)
    {
        Console.WriteLine("  [输入流] 开始打捞…");
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) { Console.WriteLine($"  [输入流] 重开失败 err={Marshal.GetLastWin32Error()}"); return; }
        using (h)
        {
            var ev = CreateEventW(IntPtr.Zero, 0, 0, null);
            if (ev == IntPtr.Zero) { Console.WriteLine($"  [输入流] CreateEvent失败 err={Marshal.GetLastWin32Error()}"); return; }
            try
            {
                Console.WriteLine("  --- 静默监听 (4帧×300ms) ---");
                ReadFrames(h, caps, ev, 4, 300);

                if (caps.OutputReportByteLength >= 65)
                {
                    byte[][] cmds =
                    {
                        new byte[]{ 0x04, 0x02, 0x00, 0x05 },
                        new byte[]{ 0x03, 0x02, 0x00, 0x01 },
                        new byte[]{ 0x05, 0x02, 0x00, 0x01 },
                    };
                    foreach (var cmd in cmds)
                    {
                        var ob = new byte[caps.OutputReportByteLength];
                        ob[0] = 0;
                        Array.Copy(cmd, 0, ob, 1, cmd.Length);
                        bool ok = HidD_SetOutputReport(h, ob, (uint)ob.Length);
                        Console.WriteLine($"  --- 发送 {Convert.ToHexString(cmd)} (SetOutputReport={ok}) → 监听6帧×400ms ---");
                        ReadFrames(h, caps, ev, 6, 400);
                    }
                }
            }
            finally { CloseHandle(ev); }
        }
    }

    private static void ReadFrames(SafeFileHandle h, HIDP_CAPS caps, IntPtr ev, int n, uint timeoutMs)
    {
        int len = caps.InputReportByteLength;
        int timeouts = 0;
        for (int i = 0; i < n; i++)
        {
            var buf = new byte[len];
            var ov = new System.Threading.NativeOverlapped { EventHandle = ev };
            bool ok = ReadFile(h, buf, (uint)len, out _, ref ov);
            if (!ok && Marshal.GetLastWin32Error() != 997) { Console.WriteLine($"    ReadFile err={Marshal.GetLastWin32Error()}"); return; }
            if (WaitForSingleObject(ev, timeoutMs) != 0)
            {
                CancelIoEx(h, IntPtr.Zero);
                WaitForSingleObject(ev, 200);
                if (++timeouts >= 2) return;
                continue;
            }
            timeouts = 0;
            if (!GetOverlappedResult(h, ref ov, out var bytes, true) || bytes < 4) continue;
            string hex = Convert.ToHexString(buf, 0, (int)Math.Min(bytes, 16)).ToLowerInvariant();
            string note = buf[0] == 0x04 ? "  ← 疑似电量帧" : (buf[0] == 0x02 || buf[0] == 0x03) ? "  ← 状态帧" : "";
            Console.WriteLine($"    {bytes}B: {hex}{note}");
        }
    }
}
