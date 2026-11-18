using System.Diagnostics;
using System.Text.Json;
using CxPrint.Core;

namespace CxPrint.Win7;

internal static class ServiceDiagnostic
{
    internal static int Run()
    {
        var result = 1;
        Exception? failure = null;
        using var runner = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(1, 1) };
        runner.Shown += async (_, _) =>
        {
            try
            {
                using var form = new MainForm();
                _ = form.Handle;
                var checks = new List<object>();
                var connected = true;
                for (var i = 0; i < 3; i++)
                {
                    var timer = Stopwatch.StartNew();
                    await form.RefreshPrintersAsync(startScan: false);
                    var status = form.ConnectedService;
                    connected &= status?.Running == true;
                    checks.Add(new
                    {
                        Connected = status?.Running == true, ElapsedMs = timer.ElapsedMilliseconds,
                        ServiceVersion = status?.Diagnostics?.ServiceVersion,
                        PrinterCount = form.PrinterCount, form.StatusText, form.ConnectionDiagnostic
                    });
                }
                var logPath = ClientLog.Append("setup-ui.log", $"{DateTimeOffset.Now:O} 管理端连接诊断：Connected={connected}{Environment.NewLine}");
                var report = new
                {
                    Timestamp = DateTimeOffset.Now, Environment = ClientEnvironment.Describe(),
                    StartupProblem = ClientEnvironment.StartupProblem(), Connected = connected,
                    LogWritable = logPath is not null, LogPath = logPath, Checks = checks
                };
                var saved = ClientLog.Write("service-diagnostics.json", JsonSerializer.Serialize(report, Product.Json));
                if (saved is null) throw new IOException("服务诊断报告无法写入任何日志目录。");
                result = connected && logPath is not null && ClientEnvironment.StartupProblem() is null ? 0 : 2;
            }
            catch (Exception ex) { failure = ex; }
            finally { runner.Close(); }
        };
        Application.Run(runner);
        if (failure is not null) throw failure;
        return result;
    }
}
