using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CxPrint.Core;

namespace CxPrint.Windows;

// Windows' bidi schema works with USB and network queues when the installed driver/port
// monitor exposes consumables. It is independent of the driver's paper capability calls.
public static class PrinterSupplyReader
{
    private const string BidiNamespace="http://schemas.microsoft.com/windows/2005/03/printing/bidi";
    private const string Request="<bidi:Get xmlns:bidi=\""+BidiNamespace+"\"><Query schema=\"\\Printer.Consumables\"/></bidi:Get>";
    public static PrinterSupplies Read(string printer)
    {
        IBidiSpl2? bidi=null;
        var bound=false;
        try
        {
            var type=System.Type.GetTypeFromCLSID(new Guid("2A614240-A4C5-4C33-BD87-1BC709331639"),throwOnError:true)!;
            bidi=(IBidiSpl2)Activator.CreateInstance(type)!;
            // BIDI_ACCESS_USER, not PRINTER_ACCESS_USE (the constants differ).
            Marshal.ThrowExceptionForHR(bidi.BindDevice(printer,2)); bound=true;
            Marshal.ThrowExceptionForHR(bidi.SendRecvXMLString(Request,out var response));
            return ParseResponse(response);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or XmlException or InvalidDataException)
        { return new([],DateTimeOffset.UtcNow,$"Windows 驱动未提供耗材信息（0x{ex.HResult:x8}）。"); }
        finally
        {
            if (bidi is not null)
            {
                if (bound) bidi.UnbindDevice();
                Marshal.ReleaseComObject(bidi);
            }
        }
    }
    public static PrinterSupplies ParseResponse(string xml)
    {
        using var text=new StringReader(xml);
        using var reader=XmlReader.Create(text,new XmlReaderSettings
        { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1024*1024 });
        var root=XDocument.Load(reader).Root;
        if (root is null || root.Name.LocalName!="Get" ||
            root.Name.NamespaceName is not (BidiNamespace or "https://schemas.microsoft.com/windows/2005/03/printing/bidi"))
            throw new InvalidDataException("Invalid bidi response.");
        const string prefix=@"\Printer.Consumables.";
        var values=new Dictionary<string,Dictionary<string,string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in root.Elements().Where(e=>e.Name.LocalName=="Query")
            .SelectMany(q=>q.Elements().Where(e=>e.Name.LocalName=="Schema")))
        {
            var path=(string?)schema.Attribute("name") ?? "";
            if (!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) continue;
            var split=path.IndexOf(':',prefix.Length);
            if (split<=prefix.Length || schema.Elements().Count()!=1) continue;
            var id=path[prefix.Length..split]; var field=path[(split+1)..]; var data=schema.Elements().Single();
            var value=data.Value.Trim();
            if (id.Length>128 || value.Length>128) continue;
            var valid=field.ToLowerInvariant() switch
            {
                "level"=>data.Name.LocalName=="BIDI_INT",
                "installed"=>data.Name.LocalName=="BIDI_BOOL",
                "type" or "color" or "model"=>data.Name.LocalName=="BIDI_STRING",
                _=>false
            };
            if (!valid) continue;
            if (!values.TryGetValue(id,out var fields))
            {
                if (values.Count>=64) continue;
                values[id]=fields=new(StringComparer.OrdinalIgnoreCase);
            }
            fields[field]=value;
        }
        var items=new List<PrinterSupply>();
        foreach (var (id,fields) in values.OrderBy(v=>v.Key,StringComparer.OrdinalIgnoreCase))
        {
            var type=fields.GetValueOrDefault("Type","").ToLowerInvariant() switch
            {
                "ink"=>"ink", "toner"=>"toner", "developer"=>"developer", "fuseroil"=>"fuser-oil",
                "wax"=>"wax", "wastetoner"=>"waste-toner", "wasteink"=>"waste-ink", "wastewax"=>"waste-wax", _=>"other"
            };
            var color=fields.GetValueOrDefault("Color","").ToLowerInvariant() switch
            {
                "photoblack"=>"photo-black", "photocolor"=>"photo-color", "photocyan"=>"photo-cyan",
                "photomagenta"=>"photo-magenta", "photoyellow"=>"photo-yellow",
                "black"=>"black", "blue"=>"blue", "color"=>"color", "cyan"=>"cyan", "gray"=>"gray",
                "green"=>"green", "magenta"=>"magenta", "red"=>"red", "white"=>"white", "yellow"=>"yellow", _=>"none"
            };
            int? level=int.TryParse(fields.GetValueOrDefault("Level"),NumberStyles.Integer,CultureInfo.InvariantCulture,out var number) && number is >=0 and <=100 ? number : null;
            bool? installed=fields.GetValueOrDefault("Installed")?.ToLowerInvariant() switch { "true" or "1"=>true,"false" or "0"=>false,_=>null };
            var model=fields.GetValueOrDefault("Model");
            var name=string.IsNullOrWhiteSpace(model) ? id : $"{id} ({model})";
            var limited=new StringBuilder(); var bytes=0;
            foreach (var rune in name.EnumerateRunes())
            {
                if (bytes+rune.Utf8SequenceLength>255) break;
                limited.Append(rune); bytes+=rune.Utf8SequenceLength;
            }
            items.Add(new(id,limited.ToString(),type,color,level,installed));
        }
        return new(items.ToArray(),DateTimeOffset.UtcNow);
    }
    [ComImport,Guid("0E8F51B8-8273-4906-8E7B-BE453FFD2E2B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBidiSpl2
    {
        [PreserveSig] int BindDevice([MarshalAs(UnmanagedType.LPWStr)] string device,uint access);
        [PreserveSig] int UnbindDevice();
        [PreserveSig] int SendRecvXMLString([MarshalAs(UnmanagedType.BStr)] string request,[MarshalAs(UnmanagedType.BStr)] out string response);
    }
}
