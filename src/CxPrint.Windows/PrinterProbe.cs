using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CxPrint.Core;

namespace CxPrint.Windows;

// Native drivers run in bounded child processes; waiting for them never occupies control-server threads.
public static class PrinterProbe
{
    public static PrinterScanner CreateScanner(string executable, Action<string, TimeSpan>? onRead = null) => new(
        onRead: onRead,
        asyncNames: async () => await ReadAsync<string[]>(executable, "--list-printer-names", null, TimeSpan.FromSeconds(10)).ConfigureAwait(false),
        asyncRead: name => ReadAsync<PrinterInfo>(executable, "--read-printer", name, TimeSpan.FromSeconds(20)));

    private static async Task<T> ReadAsync<T>(string executable, string command, string? name, TimeSpan timeout)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("缺少打印机读取程序，请完整解压安装包。", executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add(command);
        if (name is not null) start.ArgumentList.Add(name);
        using var process = Process.Start(start) ?? throw new IOException("无法启动打印机读取程序。");
        using var deadline = new CancellationTokenSource(timeout);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("打印机读取失败：" + error.Result.Trim());
            return JsonSerializer.Deserialize<T>(output.Result, Product.Json) ?? throw new InvalidDataException("打印机读取结果为空。");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            throw new TimeoutException(name is null ? "读取 Windows 打印机列表超时，请检查 Print Spooler 服务。" : $"读取 {name} 的驱动超时，已跳过，可刷新重试。");
        }
    }
}
