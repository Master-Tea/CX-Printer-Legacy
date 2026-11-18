using static CxPrint.Core.IppCodec;

namespace CxPrint.Core;

public sealed record PrinterSupply(string Id,string Name,string Type,string Color,int? Level=null,bool? Installed=null)
{
    public int? Percent => Level is >=0 and <=100 && Installed!=false ? Level : null;
    public bool IsWaste => Type.StartsWith("waste-",StringComparison.Ordinal);
    public int MarkerLevel => Installed==false ? -1 : Percent ?? -2;
    public string MarkerColor => Color switch
    {
        "black" or "photo-black"=>"#000000", "cyan" or "photo-cyan"=>"#00FFFF",
        "magenta" or "photo-magenta"=>"#FF00FF", "yellow" or "photo-yellow"=>"#FFFF00",
        "blue"=>"#0000FF", "gray"=>"#808080", "green"=>"#00FF00", "red"=>"#FF0000",
        "white"=>"#FFFFFF", "color" or "photo-color"=>"#00FFFF#FF00FF#FFFF00", _=>"none"
    };
    public string Description => $"{Name}：{(Installed==false ? "未安装" : Percent is {} level ? $"{level}%{(IsWaste ? "（已装满比例）" : "")}" : "余量未知")}";
}

public sealed record PrinterSupplies(IReadOnlyList<PrinterSupply> Items,DateTimeOffset ReadAt,string? Error=null,int ChangeTime=0)
{
    public string Summary => Error ?? (Items.Count==0 ? "Windows 驱动未提供耗材信息。" : string.Join("；",Items.Select(s=>s.Description)));
}

public static class SupplyAttributes
{
    public static string[] StateReasons(PrinterSupplies? supplies)
    {
        if (supplies?.Error is not null) return ["none"];
        var reasons=new HashSet<string>();
        foreach (var supply in supplies?.Items ?? [])
        {
            if (supply.Percent is not {} level) continue;
            if (supply.IsWaste)
            {
                if (level>=90) reasons.Add(level==100 ? "marker-waste-full-warning" : "marker-waste-almost-full-warning");
            }
            else if (level<=10)
            {
                reasons.Add(level==0 ? "marker-supply-empty-warning" : "marker-supply-low-warning");
                if (supply.Type=="toner") reasons.Add(level==0 ? "toner-empty-warning" : "toner-low-warning");
            }
        }
        return reasons.Count==0 ? ["none"] : reasons.OrderBy(reason=>reason,StringComparer.Ordinal).ToArray();
    }
    public static IReadOnlyList<IppAttribute> Build(PrinterSupplies? supplies)
    {
        if (supplies is not { Items.Count:>0,Error:null }) return [];
        const byte g=IppTag.Printer;
        var items=supplies.Items;
        return [
            Text(g,IppTag.Name,"marker-names",items.Select(s=>s.Name).ToArray()),
            Text(g,IppTag.Name,"marker-colors",items.Select(s=>s.MarkerColor).ToArray()),
            Text(g,IppTag.Keyword,"marker-types",items.Select(s=>s.Type).ToArray()),
            Int(g,"marker-levels",items.Select(s=>s.MarkerLevel).ToArray()),
            Int(g,"marker-low-levels",items.Select(s=>s.IsWaste ? 0 : 10).ToArray()),
            Int(g,"marker-high-levels",items.Select(s=>s.IsWaste ? 90 : 100).ToArray()),
            Int(g,"marker-change-time",supplies.ChangeTime),
            Attr(g,"printer-supply",items.Select((s,i)=>IppValue.String(IppTag.Octets,
                FormattableString.Invariant($"index={i};class={(s.IsWaste ? "receptacle" : "supply-that-is-consumed")};type={s.Type};unit=percent;maxcapacity=100;level={s.Percent ?? -2};colorantname={s.Color};"))).ToArray()),
            Text(g,IppTag.Text,"printer-supply-description",items.Select(s=>s.Name).ToArray())
        ];
    }
}
