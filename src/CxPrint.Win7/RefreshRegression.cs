using CxPrint.Core;
using CxPrint.Windows;

namespace CxPrint.Win7;

internal static class RefreshRegression
{
    internal static void Run()
    {
        Exception? failure = null;
        using var runner = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(1, 1) };
        runner.Shown += async (_, _) =>
        {
            try { await CheckAsync(); }
            catch (Exception ex) { failure = ex; }
            finally { runner.Close(); }
        };
        Application.Run(runner);
        if (failure is not null) throw new InvalidOperationException("打印机刷新回归检查失败。", failure);
    }

    private static PrinterInfo Pdf() => new("pdf-test", "Microsoft Print to PDF", "Microsoft Print To PDF", "PORTPROMPT:",
        true, false, new[] { new MediaSize("test-a4", "A4", 21000, 29700, 9) }, "test-a4");

    private static async Task CheckAsync()
    {
        await PipeRegression.CheckAsync();
        using var blocked = new ManualResetEventSlim();
        var scanner = new PrinterScanner(() => new[] { "Microsoft Print to PDF", "Stalled driver" }, name =>
        {
            if (name == "Stalled driver") blocked.Wait();
            return name == "Microsoft Print to PDF" ? Pdf() : Pdf() with { Id = "stalled", Name = name };
        });
        try
        {
            using var form = new MainForm(scanner, (_, _) => Task.FromException<ControlReply>(new IOException("服务不可用")));
            await form.RefreshPrintersAsync().WaitAsync(TimeSpan.FromSeconds(2));
            for (var i = 0; i < 100 && !scanner.Snapshot.Printers.Any(p => p.Id == "pdf-test"); i++) await Task.Delay(20);
            form.UpdateLocalScan();
            if (!scanner.Snapshot.Refreshing || form.PrinterCount != 2 || !form.ServiceButtonEnabled ||
                form.StatusText.Contains("正在连接打印服务") || !scanner.Snapshot.Printers.Any(p => p.Id == "pdf-test" && p.Usable))
                throw new Exception("驱动阻塞时应显示已完成的 PDF 队列并允许安装服务。");
        }
        finally
        {
            blocked.Set();
            await scanner.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }

        var silentService = new TaskCompletionSource<ControlReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var emptyScanner = new PrinterScanner(() => Array.Empty<string>(), _ => throw new InvalidOperationException());
        using (var form = new MainForm(emptyScanner, (_, _) => silentService.Task, TimeSpan.FromMilliseconds(100)))
        {
            await form.RefreshPrintersAsync().WaitAsync(TimeSpan.FromSeconds(2));
            if (!form.ServiceButtonEnabled || !form.StatusText.Contains("连接超时"))
                throw new Exception("服务不响应时连接应超时并恢复按钮。");
        }

        var local = new PrinterScanner(() => new[] { "Microsoft Print to PDF" }, _ => Pdf());
        var status = new BridgeStatus(true, true, 8631, true, false, "", false, Array.Empty<PrinterInfo>(),
            Array.Empty<SharedPrinter>(), Array.Empty<JobInfo>(), Array.Empty<string>(), "test", PrintersRefreshing: true);
        using (var form = new MainForm(local, (_, _) => Task.FromResult(new ControlReply(true, Status: status))))
        {
            await form.RefreshPrintersAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await local.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(2));
            form.UpdateLocalScan();
            if (form.PrinterCount != 1) throw new Exception("服务扫描尚无结果时应显示本地 PDF 队列。");
        }
    }
}
