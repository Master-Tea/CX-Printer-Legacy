using System.Net;
using System.Text;
using CxPrint.Core;
using Makaretu.Dns;

namespace CxPrint.Service;

internal static class PrinterAdvertisement
{
    static PrinterAdvertisement()=>ResourceRegistry.Records[DnsType.TXT]=()=>new Utf8TxtRecord();
    internal static ServiceProfile Create(PrinterInfo printer,BridgeSettings settings,IEnumerable<IPAddress> addresses,string? machineName=null)
    {
        machineName ??= Environment.MachineName;
        var shared=settings.Printers.FirstOrDefault(p=>p.Id==printer.Id) ?? new SharedPrinter(printer.Id,printer.Name);
        shared.ValidateDisplayName();
        // Pass a literal label so dots and backslashes in a custom name remain visible.
        var name=new DomainName(new[]{shared.EffectiveDisplayName});
        var profile=new ServiceProfile(name,"_ipp._tcp",(ushort)settings.Port,addresses);
        // DNS-SD instance labels allow spaces and UTF-8, but clients use the SRV
        // target as a URI hostname. ServiceProfile defaults that target to the
        // display name, which produces invalid IPP URIs for ordinary queue names.
        var host=new DomainName($"cx-{Product.PrinterId(machineName)}.local");
        profile.HostName=host;
        foreach (var srv in profile.Resources.OfType<SRVRecord>()) srv.Target=host;
        foreach (var address in profile.Resources.OfType<AddressRecord>()) address.Name=host;
        // The library's TXT writer only accepts ASCII. DNS-SD TXT values are UTF-8
        // octet strings; even our own middle dot in "CX Printer · ..." broke it.
        var old=profile.Resources.OfType<TXTRecord>().Single();
        profile.Resources.Remove(old);
        profile.Resources.Add(new Utf8TxtRecord { Name=old.Name,Strings=old.Strings });
        profile.Subtypes.Add("_universal");
        profile.AddProperty("rp","printers/"+printer.Id);
        profile.AddProperty("ty",TrimUtf8(printer.Driver,252));
        profile.AddProperty("product","("+TrimUtf8(printer.Driver,245)+")");
        profile.AddProperty("note",TrimUtf8("CX Printer - "+machineName,250));
        profile.AddProperty("pdl",string.Join(',',PrinterAttributes.Formats.Where(f=>f!="application/octet-stream")));
        profile.AddProperty("URF",printer.Color ? "W8,SRGB24,RS300,CP1" : "W8,RS300,CP1");
        profile.AddProperty("Color",printer.Color ? "T" : "F");
        profile.AddProperty("Duplex",printer.Duplex ? "T" : "F");
        profile.AddProperty("qtotal","1"); profile.AddProperty("priority","0");
        profile.AddProperty("UUID",PrinterAttributes.StableUuid(printer.Id));
        if (settings.Authentication.Enabled) profile.AddProperty("air","username,password");
        foreach (var record in profile.Resources) record.TTL=TimeSpan.FromSeconds(120);
        return profile;
    }
    private static string TrimUtf8(string value,int maxBytes)
    {
        var text=new StringBuilder(); var bytes=0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes+rune.Utf8SequenceLength>maxBytes) break;
            text.Append(rune.ToString()); bytes+=rune.Utf8SequenceLength;
        }
        return text.ToString();
    }
    private sealed class Utf8TxtRecord : TXTRecord
    {
        public override void ReadData(WireReader reader,int length)
        {
            var end=reader.Position+length;
            while (reader.Position<end)
            {
                var data=reader.ReadByteLengthPrefixedBytes();
                if (reader.Position>end) throw new InvalidDataException("TXT 字符串超出记录长度。");
                Strings.Add(Encoding.UTF8.GetString(data));
            }
        }
        public override void WriteData(WireWriter writer)
        {
            foreach (var text in Strings) writer.WriteByteLengthPrefixedBytes(Encoding.UTF8.GetBytes(text));
        }
    }
}
