using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CxPrint.Core;
using Microsoft.Win32;

namespace CxPrint.Service;

public sealed class ControlWorker(StateStore state,PrinterMonitor printers,JobManager jobs,DiscoveryWorker discovery,
    IppEndpoint endpoint,ILogger<ControlWorker> logger) : ServiceWorker
{
    private const PipeOptions FirstPipeInstance=(PipeOptions)0x00080000;
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        var active=new List<Task>();
        var pending=CreatePipe(first:true);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                active.RemoveAll(t=>t.IsCompleted);
                if (active.Count>=4) { await Task.WhenAny(active); continue; }
                await pending.WaitForConnectionAsync(stoppingToken);
                var accepted=pending;
                try { pending=CreatePipe(first:false); }
                catch { accepted.Dispose(); throw; }
                active.Add(Serve(accepted,stoppingToken));
            }
        }
        finally { await pending.DisposeAsync(); await Task.WhenAll(active); }
    }
    private NamedPipeServerStream CreatePipe(bool first)
    {
        var security=new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AnonymousSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
        foreach (var sid in new[]{new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),new SecurityIdentifier(state.Settings.OwnerSid),WindowsIdentity.GetCurrent().User!})
            security.AddAccessRule(new PipeAccessRule(sid,PipeAccessRights.FullControl,AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(Paths.PipeName,PipeDirection.InOut,5,PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous|(first ? FirstPipeInstance : PipeOptions.None),65536,65536,security);
    }
    private async Task Serve(NamedPipeServerStream pipe,CancellationToken stoppingToken)
    {
        await using (pipe)
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var prefix=new byte[4]; await StreamReadExtensions.ReadExactlyAsync(pipe,prefix,deadline.Token);
                var length=BitConverter.ToInt32(prefix); if (length is < 1 or > 65536) return;
                var body=new byte[length]; await StreamReadExtensions.ReadExactlyAsync(pipe,body,deadline.Token);
                var request=JsonSerializer.Deserialize<ControlRequest>(body,Product.Json);
                ControlReply reply;
                try
                {
                    switch (request?.Command)
                    {
                        case "status": reply=new(true,Status:GetStatus()); break;
                        case "apply":
                            if (request.Settings is null) throw new ArgumentException("缺少设置。");
                            if (request.Settings.Printers.Any(selected=>!printers.Printers.Any(p=>p.Id==selected.Id && p.Usable)) && printers.Refreshing)
                                throw new InvalidOperationException("后台正在读取所选打印机的驱动与纸张，请等该打印机读取完成后再保存。");
                            state.Apply(request.Settings,printers.Printers);
                            reply=new(true,Status:GetStatus()); break;
                        case "rescan":
                            printers.RequestRefresh(); discovery.Reannounce();
                            reply=new(true,Status:GetStatus()); break;
                        default: reply=new(false,"不支持的本地控制命令。"); break;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException)
                { reply=new(false,ex.Message); }
                var data=JsonSerializer.SerializeToUtf8Bytes(reply,Product.Json);
                await pipe.WriteAsync(BitConverter.GetBytes(data.Length),deadline.Token);
                await pipe.WriteAsync(data,deadline.Token); await pipe.FlushAsync(deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
            { logger.LogDebug(ex,"本地控制连接已结束。"); }
            catch (Exception ex) { logger.LogError(ex,"本地控制请求失败。"); }
        }
    }
    private BridgeStatus GetStatus()
    {
        var settings=state.Settings;
        var scan=printers.Snapshot;
        using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+Product.ServiceName);
        return new(true,settings.Enabled,settings.Port,(int?)key?.GetValue("Start")==2,
            settings.Authentication.Enabled,settings.Authentication.Username,settings.Authentication.ProtectedHa1 is not null,
            scan.Printers,settings.Printers,jobs.Snapshot(),Paths.DebugMode ? ["127.0.0.1"] : DiscoveryWorker.Addresses().Select(a=>a.ToString()).ToArray(),discovery.Status,
            scan.Error,scan.Refreshing,scan.Message,new(ServiceBuild.Version,ServiceBuild.Sha256,discovery.LastError,endpoint.RecentRequests));
    }
}
