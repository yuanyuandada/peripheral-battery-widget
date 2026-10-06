using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PeripheralBatteryWidget;

// ---------- 盖世小鸡 T4N2（Nova 2 Lite 等）HID 电量通道 ----------
// 协议逆向自 GameSir Connect 的 zw-lib 日志:
//   查询: 65B 报文 [0x00, 0x01, 0x01, 0x00, 其余 0]
//   回应: 65B 报文 [0x00(占位), 0x01(类型), MAC(6B), ... 第19字节=电量%, 第20字节=充电状态]
// 该通道覆盖有线与接收器连接；按 MAC 识别设备，昵称跨连接方式跟随。
internal static class GameSirHid
{
    [DllImport("Cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List(ref Guid interfaceClass, string? deviceID, char[] buffer, uint bufferLen, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(Microsoft.Win32.SafeHandles.SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr pp, ref HidpCaps caps);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, uint len, out uint read, ref System.Threading.NativeOverlapped ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, uint len, out uint written, ref System.Threading.NativeOverlapped ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(Microsoft.Win32.SafeHandles.SafeFileHandle h, ref System.Threading.NativeOverlapped ov, out uint bytes, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(Microsoft.Win32.SafeHandles.SafeFileHandle h, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateEventW(IntPtr attrs, int manual, int initial, string? name);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
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
    private static readonly Guid HidGuid = new(0x4d1e55b2, 0xf16f, 0x11cf, 0x88, 0xcb, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);

    [DllImport("Cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("Cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("Cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder id, int len, uint flags);

    public static List<DeviceInfo> Collect()
    {
        var result = new List<DeviceInfo>();
        try
        {
            var paths = GetHidPaths();
            foreach (var path in paths)
            {
                if (path.IndexOf("vid_3537", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try { CollectOne(path, result); } catch { }
            }
            // 蓝牙模式的手柄（伪装成 Xbox 手柄 PID_02E0）：无电量查询通道，但要让设备可见并写明原因
            foreach (var path in paths)
            {
                if (path.IndexOf("pid_02e0", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try { CollectBtPad(path, result); } catch { }
            }
        }
        catch { }
        return result;
    }

    // 蓝牙手柄：接口路径反查设备树 → 父节点(蓝牙设备) → MAC
    private static void CollectBtPad(string path, List<DeviceInfo> result)
    {
        var h = CreateFileW(path, GENERIC_READ, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) return;
        string mac;
        using (h)
        {
            if (!HidD_GetPreparsedData(h, out var pp)) return;
            try
            {
                var caps = new HidpCaps();
                if (HidP_GetCaps(pp, ref caps) != HIDP_SUCCESS) return;
                // GameSir 蓝牙模式特征：游戏控制页(0x0005) 且无输出通道（真 Xbox 手柄是页1、有64B输出）
                if (caps.UsagePage != 0x0005 || caps.OutputReportByteLength != 0) return;
            }
            finally { HidD_FreePreparsedData(pp); }

            // \\?\HID#实例ID#{GUID} → 实例ID（#还原为\）→ 定位设备节点 → 父节点 → 设备ID含蓝牙MAC
            int guidIdx = path.IndexOf("#{", StringComparison.Ordinal);
            string instId = path.Length > 4 && guidIdx > 4 ? path[4..guidIdx].Replace('#', '\\') : "";
            mac = "";
            if (instId.Length > 0 && CM_Locate_DevNodeW(out var devInst, instId, 0) == 0 && CM_Get_Parent(out var parent, devInst, 0) == 0)
            {
                var sb = new StringBuilder(512);
                if (CM_Get_Device_IDW(parent, sb, sb.Capacity, 0) == 0)
                {
                    var m = Regex.Match(sb.ToString(), @"DEV_([0-9A-Fa-f]{12})");
                    if (m.Success) mac = m.Groups[1].Value.ToUpperInvariant();
                }
            }
            if (mac.Length == 0) mac = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..12];
        }
        var key = "gsbt:" + mac;
        if (result.Any(r => r.Key == key)) return;
        result.Add(new DeviceInfo
        {
            Key = key,
            RawName = "手柄 (蓝牙)",
            Transport = "蓝牙",
            Percent = null,
            FourLevel = false,
            Error = "蓝牙模式不开放电量查询（线连或2.4G接收器可显示精确电量）",
        });
    }

    private static string[] GetHidPaths()
    {
        for (uint len = 4096; len <= 262144; len *= 2)
        {
            var buf = new char[len];
            var guid = HidGuid;
            int cr = CM_Get_Device_Interface_List(ref guid, null, buf, len, 0);
            if (cr == 0)
            {
                string all = new string(buf);
                return all.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            if (cr != 0x1A) return Array.Empty<string>(); // 0x1A = 缓冲区太小
        }
        return Array.Empty<string>();
    }

    private static void CollectOne(string path, List<DeviceInfo> result)
    {
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) return;
        using (h)
        {
            if (!HidD_GetPreparsedData(h, out var pp)) return;
            int inLen, outLen;
            try
            {
                var caps = new HidpCaps();
                if (HidP_GetCaps(pp, ref caps) != HIDP_SUCCESS) return;
                inLen = caps.InputReportByteLength; outLen = caps.OutputReportByteLength;
            }
            finally { HidD_FreePreparsedData(pp); }
            if (inLen < 21 || outLen < 4) return; // 需要读到第20字节、能写4字节命令

            var ev = CreateEventW(IntPtr.Zero, 0, 0, null);
            if (ev == IntPtr.Zero) return;
            try
            {
                Drain(h, inLen, ev, 120); // 清空积压的流帧

                var ob = new byte[outLen];
                ob[0] = 0x00; ob[1] = 0x01; ob[2] = 0x01; ob[3] = 0x00; // GetDeviceInfo
                if (!Write(h, ob, ev)) return;

                var misses = 0;
                var deadline = Environment.TickCount64 + 600;
                while (Environment.TickCount64 < deadline)
                {
                    var buf = new byte[inLen];
                    if (!ReadOne(h, buf, ev, 80))
                    {
                        if (++misses >= 4) break;
                        continue;
                    }
                    if (buf[0] != 0x00 || buf[1] != 0x01) continue; // 只要设备信息回应（流帧=0xF4）
                    int batt = buf[19], charge = buf[20];
                    if (batt > 100) continue;
                    string mac = Convert.ToHexString(buf, 2, 6);
                    if (result.Any(r => r.Key == "gs:" + mac)) break; // 同一设备另一接口已收录
                    result.Add(new DeviceInfo
                    {
                        Key = "gs:" + mac,
                        RawName = "手柄 (盖世小鸡)",
                        Transport = "有线",
                        Percent = batt,
                        FourLevel = false,
                        Charging = charge != 0,
                    });
                    break;
                }
            }
            finally { CloseHandle(ev); }
        }
    }

    private static void Drain(Microsoft.Win32.SafeHandles.SafeFileHandle h, int inLen, IntPtr ev, uint ms)
    {
        var deadline = Environment.TickCount64 + ms;
        var buf = new byte[inLen];
        while (Environment.TickCount64 < deadline)
            if (!ReadOne(h, buf, ev, 30)) break;
    }

    private static bool Write(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] ob, IntPtr ev)
    {
        var ov = new System.Threading.NativeOverlapped { EventHandle = ev };
        if (WriteFile(h, ob, (uint)ob.Length, out _, ref ov)) return true;
        if (Marshal.GetLastWin32Error() != 997) return false;
        if (WaitForSingleObject(ev, 300) != 0) { CancelIoEx(h, IntPtr.Zero); WaitForSingleObject(ev, 100); return false; }
        return GetOverlappedResult(h, ref ov, out _, true);
    }

    private static bool ReadOne(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, IntPtr ev, uint timeoutMs)
    {
        var ov = new System.Threading.NativeOverlapped { EventHandle = ev };
        if (!ReadFile(h, buf, (uint)buf.Length, out _, ref ov))
        {
            if (Marshal.GetLastWin32Error() != 997) return false;
            if (WaitForSingleObject(ev, timeoutMs) != 0)
            {
                CancelIoEx(h, IntPtr.Zero);
                WaitForSingleObject(ev, 100);
                return false;
            }
        }
        return GetOverlappedResult(h, ref ov, out var n, true) && n >= 21;
    }
}
