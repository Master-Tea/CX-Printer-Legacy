using System.Text;
using System.Windows.Forms;

namespace CxPrint.Win7;

internal static class Program
{
    private static bool _selfTest;

    [STAThread]
    private static int Main(string[] args)
    {
        _selfTest = args.Contains("--self-test", StringComparer.OrdinalIgnoreCase);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportError(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject.ToString()));
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var form = new MainForm();
            if (_selfTest)
            {
                // Exercise window construction and native control handles without connecting to the service.
                _ = form.Handle;
                foreach (var size in new[] { new System.Drawing.Size(900, 590), form.MinimumSize, new System.Drawing.Size(1200, 750) })
                {
                    form.Size = size;
                    form.PerformLayout();
                }
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-self-test.txt"),
                    "PASS: MainForm construction, native window handle, and resize layout." + Environment.NewLine, Encoding.UTF8);
                return 0;
            }
            Application.Run(form);
            return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            ReportError(ex);
            return 1;
        }
    }

    private static void ReportError(Exception exception)
    {
        Environment.ExitCode = 1;
        var report = $"{DateTimeOffset.Now:O}{Environment.NewLine}" +
            $"Version: {typeof(Program).Assembly.GetName().Version}{Environment.NewLine}" +
            $"OS: {Environment.OSVersion}; 64-bit: {Environment.Is64BitProcess}; Runtime: {Environment.Version}{Environment.NewLine}" +
            exception + Environment.NewLine;
        string? savedPath = null;
        foreach (var directory in new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CX Printer Legacy", "Logs"),
            Path.GetTempPath()
        })
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "CX-Printer-Legacy-error.log");
                File.AppendAllText(path, report, Encoding.UTF8);
                savedPath = path;
                break;
            }
            catch { /* Error reporting must not hide the original exception. */ }
        }
        if (!_selfTest)
        {
            MessageBox.Show("程序发生错误：" + Environment.NewLine + exception.Message + Environment.NewLine + Environment.NewLine +
                (savedPath is null ? report : "详细日志：" + savedPath),
                "CX Printer Legacy", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
