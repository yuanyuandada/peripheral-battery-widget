namespace PeripheralBatteryWidget;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "PeripheralBatteryWidget_SingleInstance", out var isNew);
        if (!isNew) return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new TrayContext());
    }
}
