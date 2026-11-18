using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using CxPrint.Core;
using CxPrint.Windows;

namespace CxPrint.Win7;

internal static class PipeRegression
{
    internal static async Task CheckAsync()
    {
        var name = "CxPrintBridge.Test." + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        using (var server = LegacyPipeServer.Create(name, PipeDirection.InOut, 2, 4096, 4096, security))
        {
            var duplicateRejected = false;
            try { using var duplicate = LegacyPipeServer.Create(name, PipeDirection.InOut, 2, 4096, 4096, security); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 5) { duplicateRejected = true; }
            if (!duplicateRejected) throw new Exception("管道首实例保护未生效。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var accepting = server.WaitForConnectionAsync(timeout.Token);
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);
            await accepting;
            await client.WriteAsync(new byte[] { 42 }, timeout.Token);
            var data = new byte[1];
            await CxPrint.Core.StreamReadExtensions.ReadExactlyAsync(server, data, timeout.Token);
            if (data[0] != 42) throw new Exception("本地管道读写检查失败。");
        }
        using (var listener = new SetupProgressListener())
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var listening = listener.ListenAsync(new Progress<SetupProgress>(), timeout.Token);
            await using (var writer = await SetupProgressWriter.ConnectAsync(listener.Name))
            {
                if (!writer.IsConnected) throw new Exception("安装进度管道连接失败：" + writer.Error);
                await writer.ReportAsync(new SetupProgress(75, "安装进度检查"));
            }
            await listening.WaitAsync(TimeSpan.FromSeconds(5));
            if (listener.Latest?.Percent != 75) throw new Exception("安装进度回传失败。");
        }
        await CheckHostAsync();
    }

    private static async Task CheckHostAsync()
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!,
            "CXPrinter-Service-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Bridge", "CxPrint.Service.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--console");
        start.Environment["CXPRINTER_DEBUG_ROOT"] = directory;
        start.Environment["CXPRINTER_DEBUG_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var service = Process.Start(start) ?? throw new Exception("无法启动服务自检进程。");
        var output = service.StandardOutput.ReadToEndAsync();
        var errors = service.StandardError.ReadToEndAsync();
        try
        {
            var ready = false;
            Exception? last = null;
            for (var attempt = 0; attempt < 15 && !service.HasExited; attempt++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    var reply = await ControlClient.SendToPipeAsync(Product.PipeName + ".Debug", new ControlRequest("status"), timeout.Token)
                        .WaitAsync(TimeSpan.FromSeconds(1));
                    if (!reply.Success || reply.Status?.Port != port) throw new Exception("服务状态不匹配。");
                    ready = true;
                    break;
                }
                catch (Exception ex) { last = ex; await Task.Delay(100); }
            }
            if (!ready) throw new Exception("完整服务未能启动或控制请求失败：" + last?.Message);
            for (var i = 0; i < 3; i++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var reply = await ControlClient.SendToPipeAsync(Product.PipeName + ".Debug", new ControlRequest("status"), timeout.Token)
                    .WaitAsync(TimeSpan.FromSeconds(3));
                if (!reply.Success) throw new Exception("后续控制管道实例连接失败。");
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetStringAsync($"http://127.0.0.1:{port}/");
            if (!response.Contains("CX Printer Bridge")) throw new Exception("IPP 服务 HTTP 健康检查失败。");
            if (service.HasExited) throw new Exception("服务在启动检查后退出。");
        }
        catch (Exception ex)
        {
            var logs = Directory.Exists(Path.Combine(directory, "Logs"))
                ? string.Join(Environment.NewLine, Directory.EnumerateFiles(Path.Combine(directory, "Logs"), "*.log")
                    .SelectMany(path => File.ReadLines(path).TakeLast(25))) : "";
            throw new Exception(ex.Message + Environment.NewLine + logs, ex);
        }
        finally
        {
            if (!service.HasExited) service.Kill(entireProcessTree: true);
            await service.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { /* A failed host's driver worker has its own exit deadline. */ }
            Directory.Delete(directory, true);
        }
    }
}
