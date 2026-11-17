using Avalonia;

namespace Globe3D.Demo;

internal static class Program
{
    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "demo.log");

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            File.WriteAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] start\n");
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log("UNHANDLED: " + e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, e) =>
                Log("TASK: " + e.Exception);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            Log("exited normally");
        }
        catch (Exception ex)
        {
            Log("FATAL: " + ex);
        }
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {message}\n");
        }
        catch
        {
            // 忽略日志写入失败
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
