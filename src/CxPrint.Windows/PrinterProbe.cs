using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CxPrint.Core;

namespace CxPrint.Windows;

// Run native driver queries in bounded child processes so an unresponsive driver cannot consume every scan slot.
public static class PrinterProbe
{
    public static PrinterScanner CreateScanner(string executable, Action<string, TimeSpan>? onRead = null) => new(
        names: () => Read<string[]>(executable, "--list-printer-names", null, TimeSpan.FromSeconds(10)),
        read: name => Read<PrinterInfo>(executable, "--read-printer", name, TimeSpan.FromSeconds(20)),
        onRead: onRead);

    private static T Read<T>(string executable, string command, string? name, TimeSpan timeout)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("缺少打印机读取程序，请完整解压安装包。", executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add(command);
        if (name is not null) start.ArgumentList.Add(name);
        var started = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new IOException("无法启动打印机读取程序。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill();
            process.WaitForExit(1000);
            throw new TimeoutException(name is null ? "读取 Windows 打印机列表超时，请检查 Print Spooler 服务。" : $"读取 {name} 的驱动超时，已跳过，可刷新重试。");
        }
        // Do not let inherited pipe handles extend the driver deadline indefinitely.
        var remaining = Math.Max(1, (int)(timeout - started.Elapsed).TotalMilliseconds);
        if (!Task.WaitAll(new Task[] { output, error }, remaining)) throw new TimeoutException("打印机读取程序未及时关闭输出。");
        if (process.ExitCode != 0) throw new IOException("打印机读取失败：" + error.Result.Trim());
        return JsonSerializer.Deserialize<T>(output.Result, Product.Json) ?? throw new InvalidDataException("打印机读取结果为空。");
    }
}
