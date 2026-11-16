namespace PolymarketDashboard;

internal static class Program
{
    private static readonly string CrashLog = @"C:\Users\Public\pmb-dash-crash.log";

    private static void LogCrash(string kind, Exception ex)
    {
        try
        {
            File.AppendAllText(CrashLog,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {kind}: {ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}\r\n\r\n");
        }
        catch { /* nothing else we can do */ }
    }

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => LogCrash("UI-thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("Unhandled", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            LogCrash("Main", ex);
            throw;
        }
    }
}
