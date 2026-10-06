using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== 0x12 帧电量解码验证 v5 ===");
Console.WriteLine($"开始: {DateTime.Now:HH:mm:ss}  时长25秒");
Console.WriteLine();

var hidClass = new Guid(0x4d1e55b2, 0xf16f, 0x11cf, 0x88, 0xcb, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);
string[] paths = HidNative.GetDeviceInterfacePaths(hidClass);
foreach (var p in paths)
{
    var m = Regex.Match(p, @"vid_3537&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
    if (!m.Success) continue;
    string pid = m.Groups[1].Value.ToUpperInvariant();
    HidNative.DecodeBattery(p, pid);
}

Console.WriteLine();
Console.WriteLine("=== 验证结束 ===");

internal static class HidNative
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
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint len, out uint read, ref System.Threading.NativeOverlapped ov);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle h, byte[] buf, uint len, out uint written, ref System.Threading.NativeOverlapped ov);
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

    public static void DecodeBattery(string path, string pid)
    {
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) { Console.WriteLine($"[PID_{pid}] 打开失败 err={Marshal.GetLastWin32Error()}"); return; }
        using (h)
        {
            if (!HidD_GetPreparsedData(h, out var pp)) return;
            int inLen = 65, outLen = 65;
            try
            {
                var caps = new HIDP_CAPS();
                if (HidP_GetCaps(pp, ref caps) == HIDP_SUCCESS) { inLen = caps.InputReportByteLength; outLen = caps.OutputReportByteLength; }
            }
            finally { HidD_FreePreparsedData(pp); }
            if (outLen < 3) { Console.WriteLine($"[PID_{pid}] 非厂商通道(in={inLen} out={outLen})，跳过"); return; }

            var ev = CreateEventW(IntPtr.Zero, 0, 0, null);
            if (ev == IntPtr.Zero) return;
            try
            {
                Console.WriteLine($"[PID_{pid}] 心跳+监听25秒 (in={inLen} out={outLen})…");
                var stop = DateTime.Now.AddSeconds(25);
                var hb = System.Threading.Tasks.Task.Run(() =>
                {
                    var ob = new byte[outLen];
                    ob[0] = 0x0F; ob[1] = 0xF2; ob[2] = 0x00;   // getHeartBeatCommand(0)
                    int fails = 0;
                    while (DateTime.Now < stop)
                    {
                        var ov = new System.Threading.NativeOverlapped { EventHandle = ev };
                        if (!WriteFile(h, ob, (uint)ob.Length, out _, ref ov))
                        {
                            if (Marshal.GetLastWin32Error() == 997)
                            { if (WaitForSingleObject(ev, 200) != 0) { CancelIoEx(h, IntPtr.Zero); WaitForSingleObject(ev, 100); fails++; } }
                            else { if (fails++ < 2) Console.WriteLine($"[PID_{pid}] 心跳写失败 err={Marshal.GetLastWin32Error()}"); }
                        }
                        System.Threading.Thread.Sleep(900);
                    }
                });

                int frames12 = 0;
                var seenBattery = new HashSet<string>();
                while (DateTime.Now < stop)
                {
                    var buf = new byte[inLen];
                    var ov = new System.Threading.NativeOverlapped { EventHandle = ev };
                    bool ok = ReadFile(h, buf, (uint)inLen, out _, ref ov);
                    if (!ok && Marshal.GetLastWin32Error() != 997) break;
                    if (WaitForSingleObject(ev, 600) != 0) { CancelIoEx(h, IntPtr.Zero); WaitForSingleObject(ev, 150); continue; }
                    if (!GetOverlappedResult(h, ref ov, out var n, true) || n < 37) continue;

                    if (buf[0] == 0x12)
                    {
                        frames12++;
                        int charge = buf[35], batt = buf[36];
                        string sig = $"{charge}/{batt}";
                        if (seenBattery.Add(sig))
                        {
                            Console.WriteLine($"[PID_{pid}] 0x12帧#{frames12}: 充电状态={charge} 电量={batt}%   全帧: {Convert.ToHexString(buf, 0, (int)Math.Min(n, 48)).ToLowerInvariant()}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[PID_{pid}] 帧0x{buf[0]:X2} {n}B: {Convert.ToHexString(buf, 0, (int)Math.Min(n, 42)).ToLowerInvariant()}");
                    }
                }
                hb.Wait();
                Console.WriteLine($"[PID_{pid}] 共收到 0x12 帧: {frames12}, 电量读数: {(seenBattery.Count == 0 ? "无" : string.Join(", ", seenBattery))}");
            }
            finally { CloseHandle(ev); }
        }
    }
}
