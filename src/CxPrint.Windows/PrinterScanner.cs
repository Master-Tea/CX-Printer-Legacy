using System.Diagnostics;
using CxPrint.Core;

namespace CxPrint.Windows;

public sealed record PrinterScanSnapshot(IReadOnlyList<PrinterInfo> Printers,bool Refreshing=false,
    int Completed=0,int Total=0,string? CurrentPrinter=null,DateTimeOffset? ReadingSince=null,string? Error=null)
{
    public string? Message => Error ?? (Refreshing
        ? CurrentPrinter is null ? "正在读取 Windows 打印机列表…"
            : $"正在读取 {CurrentPrinter} 的驱动与纸张（{Completed}/{Total}，已用 {(int)(DateTimeOffset.UtcNow-ReadingSince!.Value).TotalSeconds} 秒）。已完成的打印机可以先使用。"
        : null);
}

// Driver calls cannot be cancelled safely. Keep one scan in flight, at most two driver
// calls, and publish immutable partial results instead of holding up the UI or control pipe.
public sealed class PrinterScanner(Func<IReadOnlyList<string>>? names=null,Func<string,PrinterInfo>? read=null,
    Action<string,TimeSpan>? onRead=null,
    Func<Task<IReadOnlyList<string>>>? asyncNames=null,Func<string,Task<PrinterInfo>>? asyncRead=null)
{
    private readonly object _gate=new();
    private readonly Func<IReadOnlyList<string>> _names=names ?? PrinterCatalog.Names;
    private readonly Func<string,PrinterInfo> _read=read ?? PrinterCatalog.Read;
    private Task<IReadOnlyList<string>> ReadNamesAsync()=>asyncNames is null ? Task.Run(_names) : asyncNames();
    private Task<PrinterInfo> ReadPrinterAsync(string name)=>asyncRead is null ? Task.Run(()=>_read(name)) : asyncRead(name);
    private readonly Dictionary<string,(PrinterInfo Info,DateTimeOffset ReadAt)> _cache=new(StringComparer.OrdinalIgnoreCase);
    private PrinterScanSnapshot _snapshot=new([]);
    private Task? _scan;
    public PrinterScanSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public Task RefreshAsync(CancellationToken cancellation=default)
    {
        lock (_gate)
        {
            if (_scan is not { IsCompleted:false })
            {
                Volatile.Write(ref _snapshot,Snapshot with { Refreshing=true,CurrentPrinter=null,ReadingSince=null,Error=null });
                _scan=Task.Run(ScanAsync);
            }
            // Cancelling a caller never creates another native driver call on the next refresh.
            return _scan.WaitAsync(cancellation);
        }
    }
    private async Task ScanAsync()
    {
        try
        {
            var names=(await ReadNamesAsync().ConfigureAwait(false)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name=>name,StringComparer.CurrentCultureIgnoreCase).ToArray();
            var results=new Dictionary<string,PrinterInfo>(StringComparer.OrdinalIgnoreCase);
            var pending=new List<string>();
            foreach (var name in names)
            {
                var cached=_cache.GetValueOrDefault(name);
                var lifetime=cached.Info?.Usable==true ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30);
                if (cached.Info is not null && DateTimeOffset.UtcNow-cached.ReadAt<lifetime)
                    results[name]=cached.Info;
                else
                {
                    results[name]=cached.Info ?? new(Product.PrinterId(name),name,"","",false,false,[],"","正在读取驱动与纸张能力…");
                    pending.Add(name);
                }
            }
            foreach (var removed in _cache.Keys.Except(names,StringComparer.OrdinalIgnoreCase).ToArray()) _cache.Remove(removed);
            var completed=names.Length-pending.Count;
            var active=new Dictionary<string,DateTimeOffset>();
            void Publish(bool refreshing=true)
            {
                var current=active.OrderBy(p=>p.Value).ToArray();
                Volatile.Write(ref _snapshot,new(names.Select(n=>results[n]).ToArray(),refreshing,completed,names.Length,
                    current.Length==0 ? null : string.Join("、",current.Select(p=>p.Key)),
                    current.Length==0 ? null : current[0].Value));
            }
            Publish();
            await Parallel.ForEachAsync(pending,new ParallelOptions { MaxDegreeOfParallelism=2 },async (name,cancellation)=>
            {
                lock (results) { active[name]=DateTimeOffset.UtcNow; Publish(); }
                var started=Stopwatch.GetTimestamp();
                PrinterInfo printer;
                try { printer=await ReadPrinterAsync(name).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { printer=new(Product.PrinterId(name),name,"","",false,false,[],"",ex.Message); }
                lock (results)
                {
                    results[name]=printer; _cache[name]=(printer,DateTimeOffset.UtcNow);
                    active.Remove(name); completed++; Publish();
                }
                onRead?.Invoke(name,Product.ElapsedSince(started));
            }).ConfigureAwait(false);
            Publish(refreshing:false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Volatile.Write(ref _snapshot,Snapshot with { Refreshing=false,CurrentPrinter=null,ReadingSince=null,Error="读取 Windows 打印机失败："+ex.Message }); }
    }
}
