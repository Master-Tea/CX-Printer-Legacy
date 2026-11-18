using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CxPrint.Core;
using CxPrint.Windows;
using Makaretu.Dns;

namespace CxPrint.Service;

public sealed class PrinterMonitor(ILogger<PrinterMonitor> logger,PrinterScanner? scanner=null,PrinterSupplyMonitor? supplies=null) : ServiceWorker
{
    private readonly PrinterScanner _scanner=scanner ?? new(onRead:(name,elapsed)=>
    {
        if (elapsed>TimeSpan.FromSeconds(5)) logger.LogWarning("打印机 {Printer} 的驱动与纸张读取耗时 {Seconds:F1} 秒。",name,elapsed.TotalSeconds);
    });
    private readonly PrinterSupplyMonitor _supplies=supplies ?? new();
    public PrinterScanSnapshot Snapshot
    {
        get
        {
            var snapshot=_scanner.Snapshot;
            _supplies.RequestRefresh(snapshot.Printers);
            return snapshot with { Printers=snapshot.Printers.Select(p=>p with { Supplies=_supplies.Get(p.Id) }).ToArray() };
        }
    }
    public IReadOnlyList<PrinterInfo> Printers=>Snapshot.Printers;
    public bool Refreshing=>Snapshot.Refreshing;
    public Task RefreshAsync(CancellationToken cancellation=default)=>_scanner.RefreshAsync(cancellation);
    public void RequestRefresh()=>_ = RefreshAsync();
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(30),stoppingToken);
        }
    }
}

public sealed class DiscoveryWorker(StateStore state,PrinterMonitor printers,ILogger<DiscoveryWorker> logger) : ServiceWorker
{
    private ServiceDiscovery? _discovery;
    private MulticastService? _mdns;
    private readonly List<ServiceProfile> _profiles=[];
    private int _dirty=1;
    private string _status="等待网络";
    private string? _lastError;
    public string Status => Volatile.Read(ref _status);
    public string? LastError => Volatile.Read(ref _lastError);
    public int ConfigChangeTime { get; private set; } = 1;
    public static IReadOnlyList<IPAddress> Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n=>n.OperationalStatus==OperationalStatus.Up && n.SupportsMulticast &&
            n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
            !n.Description.Contains("Hyper-V",StringComparison.OrdinalIgnoreCase) &&
            !n.Description.Contains("Virtual",StringComparison.OrdinalIgnoreCase))
        .SelectMany(n=>n.GetIPProperties().UnicastAddresses)
        .Select(u=>u.Address).Where(a=>!IPAddress.IsLoopback(a) &&
            (a.AddressFamily==AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.",StringComparison.Ordinal) ||
             a.AddressFamily==AddressFamily.InterNetworkV6 && !a.IsIPv6Multicast))
        .Distinct().ToArray();
    public void Reannounce() => Interlocked.Exchange(ref _dirty,1);
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        NetworkChange.NetworkAddressChanged+=NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged+=AvailabilityChanged;
        state.Changed+=Reannounce;
        string fingerprint=""; var refreshAt=DateTimeOffset.MinValue; var announceAt=DateTimeOffset.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var addresses=Addresses(); var settings=state.Settings;
                    var available=printers.Printers.Where(p=>p.Usable && settings.Printers.Any(s=>s.Id==p.Id)).ToArray();
                    var current=JsonSerializer.Serialize(new { addresses=addresses.Select(a=>a.ToString()),settings.Enabled,AuthenticationEnabled=settings.Authentication.Enabled,
                        shared=settings.Printers,capabilities=available.Select(p=>new { p.Id,p.Media,p.Color,p.Duplex }) },Product.Json);
                    if (Interlocked.Exchange(ref _dirty,0)==1 || current!=fingerprint || DateTimeOffset.UtcNow>=refreshAt)
                    {
                        Close(); fingerprint=current; ConfigChangeTime=Product.UptimeSeconds;
                        if (Paths.DebugMode) _status="调试模式：仅监听本机，不向局域网广播";
                        else if (!settings.Enabled) _status="共享已暂停";
                        else if (addresses.Count==0) _status="没有可用局域网，等待网络恢复";
                        else if (available.Length==0) _status="请选择一台可用的 Windows 打印机";
                        else
                        {
                            _mdns=new MulticastService(); _discovery=new ServiceDiscovery(_mdns);
                            foreach (var printer in available)
                            {
                                var profile=PrinterAdvertisement.Create(printer,settings,addresses);
                                _discovery.Advertise(profile); _profiles.Add(profile);
                                logger.LogInformation("DNS-SD {Instance} -> {Host}:{Port}/printers/{PrinterId}",profile.FullyQualifiedName,profile.HostName,settings.Port,printer.Id);
                            }
                            _mdns.Start(); _status=$"已广播 {available.Length} 台打印机 · IPv4 / IPv6";
                        }
                        refreshAt=DateTimeOffset.UtcNow.AddMinutes(10); announceAt=DateTimeOffset.MinValue;
                    }
                    if (_discovery is not null && DateTimeOffset.UtcNow>=announceAt)
                    {
                        foreach (var profile in _profiles) _discovery.Announce(profile);
                        announceAt=DateTimeOffset.UtcNow.AddSeconds(60);
                    }
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _lastError,ex.ToString());
                    _status="设备发现暂时失败，5 秒后重试："+ex.Message;
                    logger.LogWarning(ex,"DNS-SD 广播失败。"); Close(); fingerprint=""; refreshAt=DateTimeOffset.MinValue;
                }
                await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken);
            }
        }
        finally
        {
            NetworkChange.NetworkAddressChanged-=NetworkChanged;
            NetworkChange.NetworkAvailabilityChanged-=AvailabilityChanged;
            state.Changed-=Reannounce; Close();
        }
    }
    private void NetworkChanged(object? sender,EventArgs args)=>Reannounce();
    private void AvailabilityChanged(object? sender,NetworkAvailabilityEventArgs args)=>Reannounce();
    private void Close()
    {
        try { _discovery?.Unadvertise(); } catch (Exception ex) { logger.LogDebug(ex,"发送 goodbye 失败。"); }
        _discovery?.Dispose(); _mdns?.Dispose(); _discovery=null; _mdns=null; _profiles.Clear();
    }
}
