using CxPrint.Core;

namespace CxPrint.Windows;

// A hung vendor call occupies one slot but never delays IPP, capability scans or the UI.
// Do not cancel/restart native calls: that would leave accumulating threads in the driver.
public sealed class PrinterSupplyMonitor(Func<string,PrinterSupplies>? read=null,TimeProvider? clock=null)
{
    private sealed class Entry
    {
        public PrinterSupplies? Value;
        public DateTimeOffset Due;
    }
    private readonly object _gate=new();
    private readonly Dictionary<string,Entry> _entries=new();
    private readonly HashSet<string> _reading=new();
    private readonly Func<string,PrinterSupplies> _read=read ?? PrinterSupplyReader.Read;
    private readonly TimeProvider _clock=clock ?? TimeProvider.System;
    public PrinterSupplies? Get(string printerId)
    {
        lock (_gate)
        {
            var value=_entries.GetValueOrDefault(printerId)?.Value;
            if (value is not null && _clock.GetUtcNow()-value.ReadAt>TimeSpan.FromMinutes(2))
                return new([],value.ReadAt,"耗材信息已过期，等待 Windows 驱动响应。",value.ChangeTime);
            return value;
        }
    }
    public void RequestRefresh(IReadOnlyList<PrinterInfo> printers)
    {
        lock (_gate)
        {
            var candidates=printers.Where(p=>p.Usable && !p.IsPdfTest).ToArray();
            foreach (var removed in _entries.Keys.Except(candidates.Select(p=>p.Id)).ToArray()) _entries.Remove(removed);
            foreach (var printer in candidates)
            {
                if (!_entries.TryGetValue(printer.Id,out var entry)) _entries[printer.Id]=entry=new();
                if (_reading.Contains(printer.Id) || _reading.Count>=2 || _clock.GetUtcNow()<entry.Due) continue;
                _reading.Add(printer.Id);
                _ = Task.Run(()=>Refresh(printer,entry));
            }
        }
    }
    private void Refresh(PrinterInfo printer,Entry entry)
    {
        PrinterSupplies result;
        try { result=_read(printer.Name); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { result=new([],_clock.GetUtcNow(),$"读取耗材信息失败（0x{ex.HResult:x8}）。"); }
        lock (_gate)
        {
            _reading.Remove(printer.Id);
            if (!_entries.TryGetValue(printer.Id,out var current) || !ReferenceEquals(current,entry)) return;
            var changed=entry.Value is null || entry.Value.Error!=result.Error || !entry.Value.Items.SequenceEqual(result.Items);
            entry.Value=result with { ReadAt=_clock.GetUtcNow(),ChangeTime=changed ? Product.UptimeSeconds : entry.Value!.ChangeTime };
            entry.Due=_clock.GetUtcNow().AddMinutes(1);
        }
    }
}
