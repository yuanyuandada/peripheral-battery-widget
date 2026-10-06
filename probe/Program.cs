using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== 蓝牙手柄电量报文探测 v6 ===");
Console.WriteLine($"开始: {DateTime.Now:HH:mm:ss}  被动听25秒+主动请求");
Console.WriteLine();

var hidClass = new Guid(0x4d1e55b2, 0xf16f, 0x11cf, 0x88, 0xcb, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);
string[] paths = HidNative.GetDeviceInterfacePaths(hidClass);
Console.WriteLine($"HID 接口总数: {paths.Length}");
HidNative.ListAll(paths);

Console.WriteLine();
Console.WriteLine("=== 探测结束 ===");

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

    public static void ListAll(string[] paths)
    {
        foreach (var p in paths)
        {
            var h = CreateFileW(p, GENERIC_READ, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) { Console.WriteLine($"  [skip] err={Marshal.GetLastWin32Error()} {p[^70..]}"); continue; }
            using (h)
            {
                if (!HidD_GetPreparsedData(h, out var pp)) continue;
                try
                {
                    var caps = new HIDP_CAPS();
                    if (HidP_GetCaps(pp, ref caps) == HIDP_SUCCESS)
                        Console.WriteLine($"  用法页=0x{caps.UsagePage:X4} 用法=0x{caps.Usage:X4} in={caps.InputReportByteLength}B out={caps.OutputReportByteLength}B feat={caps.FeatureReportByteLength}B  {p[^70..]}");
                }
                finally { HidD_FreePreparsedData(pp); }
            }
        }
    }

    public static void Capture(string path)
    {
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) { Console.WriteLine($"  打开失败 err={Marshal.GetLastWin32Error()}"); return; }
        using (h)
        {
            if (!HidD_GetPreparsedData(h, out var pp)) return;
            int inLen, outLen;
            try
            {
                var caps = new HIDP_CAPS();
                int hr = HidP_GetCaps(pp, ref caps);
                if (hr != HIDP_SUCCESS) { Console.WriteLine("  GetCaps失败"); return; }
                inLen = caps.InputReportByteLength; outLen = caps.OutputReportByteLength;
                Console.WriteLine($"  用法页=0x{caps.UsagePage:X4} 用法=0x{caps.Usage:X4} in={inLen}B out={outLen}B");
            }
            finally { HidD_FreePreparsedData(pp); }
            if (inLen < 4) { Console.WriteLine("  (输入太短，跳过)"); return; }

            var ev = CreateEventW(IntPtr.Zero, 0, 0, null);
            if (ev == IntPtr.Zero) return;
            try
            {
                var stop = DateTime.Now.AddSeconds(25);
                var seen = new HashSet<string>();
                int total = 0;

                // 10秒后开始每3秒发一次 GIP 电量请求变体
                var reqThread = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(10_000);
                    byte[][] reqs =
                    {
                        new byte[]{ 0x04, 0x02, 0x00, 0x05 },   // GIP battery request
                        new byte[]{ 0x01, 0x03, 0x02 },          // GIP announce?
                        new byte[]{ 0x04, 0x02, 0x00, 0x0F },
                    };
                    while (DateTime.Now < stop)
                    {
                        foreach (var r in reqs)
                        {
                            if (outLen < r.Length) continue;
                            var ob = new byte[outLen];
                            Array.Copy(r, ob, r.Length);
                            var ovw = new System.Threading.NativeOverlapped { EventHandle = ev };
                            if (!WriteFile(h, ob, (uint)ob.Length, out _, ref ovw) && Marshal.GetLastWin32Error() == 997)
                            { if (WaitForSingleObject(ev, 200) != 0) CancelIoEx(h, IntPtr.Zero); }
                            await System.Threading.Tasks.Task.Delay(500);
                        }
                        await System.Threading.Tasks.Task.Delay(2000);
                    }
                });

                while (DateTime.Now < stop)
                {
                    var buf = new byte[inLen];
                    var ov = new System.Threading.NativeOverlapped { EventHandle = ev };
                    bool ok = ReadFile(h, buf, (uint)inLen, out _, ref ov);
                    if (!ok && Marshal.GetLastWin32Error() != 997) break;
                    if (WaitForSingleObject(ev, 700) != 0) { CancelIoEx(h, IntPtr.Zero); WaitForSingleObject(ev, 150); continue; }
                    if (!GetOverlappedResult(h, ref ov, out var n, true) || n < 3) continue;
                    total++;
                    string sig = buf[0] + ":" + Convert.ToHexString(buf, 0, (int)Math.Min(n, 20));
                    if (seen.Add(sig) || buf[0] == 0x03)
                    {
                        Console.WriteLine($"  [{buf[0]:X2}] {n}B: {Convert.ToHexString(buf, 0, (int)Math.Min(n, 40)).ToLowerInvariant()}");
                    }
                }
                Console.WriteLine($"  共{total}帧, 独立签名{seen.Count}种");
            }
            finally { CloseHandle(ev); }
        }
    }
}
