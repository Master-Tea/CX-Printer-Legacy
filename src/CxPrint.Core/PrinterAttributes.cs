using static CxPrint.Core.IppCodec;

namespace CxPrint.Core;

public static class PrinterAttributes
{
    public static readonly string[] Formats = ["application/pdf", "image/urf", "image/pwg-raster", "image/jpeg", "image/png", "application/octet-stream"];
    public static readonly int[] Operations = [0x0002, 0x0004, 0x0005, 0x0006, 0x0008, 0x0009, 0x000a, 0x000b];
    public static IppValue MediaCollection(MediaSize media) => IppValue.Col(
        Text(0, IppTag.Keyword, "media-key", media.Key),
        Text(0, IppTag.Keyword, "media-size-name", media.Key),
        Attr(0, "media-size", IppValue.Col(Int(0, "x-dimension", media.Width), Int(0, "y-dimension", media.Height))),
        Int(0, "media-left-margin", media.Left), Int(0, "media-right-margin", media.Right),
        Int(0, "media-top-margin", media.Top), Int(0, "media-bottom-margin", media.Bottom));

    public static IReadOnlyList<IppAttribute> Build(PrinterInfo printer, SharedPrinter shared, string uri,
        bool authentication, int uptime, int jobs, int configChangeTime, IReadOnlySet<string>? requested = null)
    {
        const byte g = IppTag.Printer;
        var def = printer.Media.FirstOrDefault(m => m.Key == shared.DefaultMedia)
            ?? printer.Media.FirstOrDefault(m => m.Key == printer.DefaultMedia) ?? printer.Media[0];
        var attrs = new List<IppAttribute>
        {
            Text(g,IppTag.Uri,"printer-uri-supported",uri),
            Text(g,IppTag.Uri,"printer-uuid",$"urn:uuid:{StableUuid(printer.Id)}"),
            Text(g,IppTag.Name,"printer-name",shared.EffectiveDisplayName), Text(g,IppTag.Text,"printer-info",shared.EffectiveDisplayName),
            Text(g,IppTag.Text,"printer-location",Environment.MachineName),
            Text(g,IppTag.Text,"printer-make-and-model",printer.Driver),
            Text(g,IppTag.Keyword,"uri-authentication-supported",authentication ? "digest" : "none"),
            Text(g,IppTag.Keyword,"uri-security-supported","none"),
            Text(g,IppTag.Keyword,"ipp-versions-supported","1.1","2.0"),
            Attr(g,"operations-supported",Operations.Select(x=>IppValue.Int(x,IppTag.Enum)).ToArray()),
            Attr(g,"printer-state",IppValue.Int(3,IppTag.Enum)),
            Text(g,IppTag.Keyword,"printer-state-reasons",SupplyAttributes.StateReasons(printer.Supplies)),
            Text(g,IppTag.Text,"printer-state-message",SupplyAttributes.StateReasons(printer.Supplies) is ["none"]
                ? "就绪；作业提交到 Windows 打印队列" : "耗材不足或需要维护；作业提交到 Windows 打印队列"),
            Attr(g,"printer-is-accepting-jobs",IppValue.Bool(true)), Int(g,"queued-job-count",jobs),
            Int(g,"printer-up-time",Math.Max(1,uptime)), Int(g,"printer-config-change-time",configChangeTime),
            Text(g,IppTag.Charset,"charset-configured","utf-8"), Text(g,IppTag.Charset,"charset-supported","utf-8"),
            Text(g,IppTag.Language,"natural-language-configured","zh-cn"),
            Text(g,IppTag.Language,"generated-natural-language-supported","zh-cn","en"),
            Text(g,IppTag.Mime,"document-format-default","application/octet-stream"),
            Text(g,IppTag.Mime,"document-format-supported",Formats),
            Text(g,IppTag.Keyword,"compression-supported","none"),
            Attr(g,"color-supported",IppValue.Bool(printer.Color)),
            Text(g,IppTag.Keyword,"print-color-mode-default",printer.Color ? "color" : "monochrome"),
            Text(g,IppTag.Keyword,"print-color-mode-supported",printer.Color ? ["monochrome","color"] : ["monochrome"]),
            Text(g,IppTag.Keyword,"sides-default","one-sided"),
            Text(g,IppTag.Keyword,"sides-supported",printer.Duplex ? ["one-sided","two-sided-long-edge","two-sided-short-edge"] : ["one-sided"]),
            Int(g,"copies-default",1), Attr(g,"copies-supported",Range(1,99)),
            Attr(g,"multiple-document-jobs-supported",IppValue.Bool(false)),
            Text(g,IppTag.Keyword,"multiple-document-handling-supported","separate-documents-collated-copies"),
            Text(g,IppTag.Keyword,"multiple-document-handling-default","separate-documents-collated-copies"),
            Int(g,"multiple-operation-time-out",120),
            Text(g,IppTag.Keyword,"multiple-operation-time-out-action","abort-job"),
            Attr(g,"job-k-octets-supported",Range(0,(int)(Product.MaxDocumentBytes/1024))),
            Attr(g,"printer-resolution-default",Resolution(300)), Attr(g,"printer-resolution-supported",Resolution(300)),
            Text(g,IppTag.Keyword,"urf-supported",printer.Color ? ["W8","SRGB24","RS300","CP1"] : ["W8","RS300","CP1"]),
            Text(g,IppTag.Keyword,"pwg-raster-document-type-supported",printer.Color ? ["sgray_8","srgb_8"] : ["sgray_8"]),
            Attr(g,"pwg-raster-document-resolution-supported",Resolution(300)),
            Text(g,IppTag.Keyword,"pwg-raster-document-sheet-back","normal"),
            Text(g,IppTag.Keyword,"media-supported",printer.Media.Select(m=>m.Key).Distinct().ToArray()),
            Text(g,IppTag.Keyword,"media-default",def.Key),
            Attr(g,"media-col-default",MediaCollection(def)), Attr(g,"media-col-ready",MediaCollection(def)),
            Text(g,IppTag.Keyword,"media-ready",def.Key),
            Attr(g,"media-col-database",printer.Media.Select(MediaCollection).ToArray()),
            Attr(g,"media-size-supported",printer.Media.Select(m=>IppValue.Col(Int(0,"x-dimension",m.Width),Int(0,"y-dimension",m.Height))).ToArray()),
            Text(g,IppTag.Keyword,"media-col-supported","media-size","media-size-name","media-left-margin","media-right-margin","media-top-margin","media-bottom-margin"),
            Text(g,IppTag.Keyword,"job-creation-attributes-supported","copies","media","media-col","sides","print-color-mode","job-name","document-format","printer-resolution"),
            Text(g,IppTag.Keyword,"pdl-override-supported","attempted"),
            Text(g,IppTag.Keyword,"job-hold-until-supported","no-hold"),
            Text(g,IppTag.Keyword,"job-hold-until-default","no-hold"),
            Attr(g,"finishings-supported",IppValue.Int(3,IppTag.Enum)), Attr(g,"finishings-default",IppValue.Int(3,IppTag.Enum)),
            Text(g,IppTag.Keyword,"job-sheets-supported","none"), Text(g,IppTag.Keyword,"job-sheets-default","none"),
            Attr(g,"orientation-requested-supported",IppValue.Int(3,IppTag.Enum),IppValue.Int(4,IppTag.Enum)),
            Attr(g,"orientation-requested-default",IppValue.Int(3,IppTag.Enum)),
            Attr(g,"print-quality-supported",IppValue.Int(4,IppTag.Enum)), Attr(g,"print-quality-default",IppValue.Int(4,IppTag.Enum))
        };
        foreach (var side in new[]{"left","right","top","bottom"})
        {
            var margins = printer.Media.Select(m=>side switch { "left"=>m.Left,"right"=>m.Right,"top"=>m.Top,_=>m.Bottom }).Distinct().ToArray();
            attrs.Add(Int(g,$"media-{side}-margin-supported",margins));
        }
        attrs.AddRange(SupplyAttributes.Build(printer.Supplies));
        return requested is null || requested.Count == 0 || requested.Contains("all") || requested.Contains("printer-description") || requested.Contains("printer-defaults")
            ? attrs : attrs.Where(a=>requested.Contains(a.Name) || requested.Contains("printer-status") && IsStatus(a.Name)).ToList();
    }
    private static bool IsStatus(string name) => name.StartsWith("marker-",StringComparison.Ordinal) ||
        name.StartsWith("printer-supply",StringComparison.Ordinal) || name.StartsWith("printer-state",StringComparison.Ordinal) ||
        name is "printer-is-accepting-jobs" or "queued-job-count" or "printer-up-time";

    public static string StableUuid(string id) => new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("CX Printer:"+Environment.MachineName+":"+id))[..16]).ToString();
}

public sealed record PrintOptions(string Media, int Copies = 1, string Sides = "one-sided", bool Color = true, int Orientation = 3)
{
    public static PrintOptions From(IppMessage request, PrinterInfo printer, SharedPrinter shared)
    {
        foreach (var (name,tag) in new (string,byte)[]
        {
            ("media",IppTag.Keyword),("media-col",IppTag.Collection),("copies",IppTag.Integer),
            ("sides",IppTag.Keyword),("print-color-mode",IppTag.Keyword),("orientation-requested",IppTag.Enum),
            ("compression",IppTag.Keyword),("document-format",IppTag.Mime),("printer-resolution",IppTag.Resolution),
            ("print-quality",IppTag.Enum),("finishings",IppTag.Enum),("number-up",IppTag.Integer)
        })
            if (request.Find(name) is { } attribute && (attribute.Values.Count!=1 || attribute.Values[0].Tag!=tag))
                throw new UnsupportedOptionException(name);
        if (request.Find("printer-resolution") is { } resolution)
        {
            var data=resolution.Values[0].Data;
            if (data.Length!=9 || data[8]!=3 || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data)!=300 ||
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4))!=300)
                throw new UnsupportedOptionException("printer-resolution");
        }
        string? media = request.Text("media");
        var col = request.Find("media-col")?.Values.FirstOrDefault();
        if (col is not null)
        {
            media = col.Member("media-size-name")?.Values[0].Text ?? col.Member("media-key")?.Values[0].Text ?? media;
            var size = col.Member("media-size")?.Values.FirstOrDefault();
            if (size is not null)
            {
                var w = size.Member("x-dimension")?.Values[0].Integer ?? 0;
                var h = size.Member("y-dimension")?.Values[0].Integer ?? 0;
                var exact = printer.Media.FirstOrDefault(m=>Math.Abs(m.Width-w)<=25 && Math.Abs(m.Height-h)<=25);
                if (exact is null) throw new UnsupportedOptionException("media-col");
                media = exact.Key;
            }
        }
        media ??= shared.DefaultMedia ?? printer.DefaultMedia;
        if (!printer.Media.Any(m=>m.Key==media)) throw new UnsupportedOptionException("media");
        var copies = request.Number("copies",1);
        if (copies is < 1 or > 99) throw new UnsupportedOptionException("copies");
        var sides = request.Text("sides") ?? "one-sided";
        if (sides != "one-sided" && (!printer.Duplex || sides is not ("two-sided-long-edge" or "two-sided-short-edge")))
            throw new UnsupportedOptionException("sides");
        var color = request.Text("print-color-mode") ?? (printer.Color ? "color" : "monochrome");
        if (color is not ("color" or "monochrome" or "auto") || color == "color" && !printer.Color)
            throw new UnsupportedOptionException("print-color-mode");
        var orientation = request.Number("orientation-requested",3);
        if (orientation is not (3 or 4)) throw new UnsupportedOptionException("orientation-requested");
        if (request.Text("compression") is { } compression && compression != "none") throw new UnsupportedOptionException("compression");
        if (request.Text("document-format") is { } format && !PrinterAttributes.Formats.Contains(format))
            throw new UnsupportedOptionException("document-format");
        if (request.Find("page-ranges") is not null) throw new UnsupportedOptionException("page-ranges");
        if (request.Number("print-quality",4)!=4) throw new UnsupportedOptionException("print-quality");
        if (request.Number("finishings",3)!=3) throw new UnsupportedOptionException("finishings");
        if (request.Number("number-up",1)!=1) throw new UnsupportedOptionException("number-up");
        if (request.Text("multiple-document-handling") is { } handling && handling!="separate-documents-collated-copies")
            throw new UnsupportedOptionException("multiple-document-handling");
        if (request.Text("job-hold-until") is { } hold && hold!="no-hold") throw new UnsupportedOptionException("job-hold-until");
        if (request.Text("job-sheets") is { } sheets && sheets!="none") throw new UnsupportedOptionException("job-sheets");
        return new(media,copies,sides,color!="monochrome",orientation);
    }
}
public sealed class UnsupportedOptionException(string attribute) : Exception(attribute) { public string Attribute { get; } = attribute; }
