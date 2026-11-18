using System.Buffers.Binary;
using System.Text;

namespace CxPrint.Core;

public static class IppTag
{
    public const byte Operation = 0x01, Job = 0x02, End = 0x03, Printer = 0x04, Unsupported = 0x05;
    public const byte Integer = 0x21, Boolean = 0x22, Enum = 0x23, Octets = 0x30, Date = 0x31,
        Resolution = 0x32, Range = 0x33, Collection = 0x34, EndCollection = 0x37,
        Text = 0x41, Name = 0x42, Keyword = 0x44, Uri = 0x45, UriScheme = 0x46,
        Charset = 0x47, Language = 0x48, Mime = 0x49, MemberName = 0x4a;
}

public sealed record IppAttribute(byte Group, byte Tag, string Name, IReadOnlyList<IppValue> Values);
public sealed record IppValue(byte Tag, byte[] Data, IReadOnlyList<IppAttribute>? Members = null)
{
    public string Text => Encoding.UTF8.GetString(Data);
    public int Integer => Data.Length == 4 ? BinaryPrimitives.ReadInt32BigEndian(Data) : throw new InvalidDataException("Invalid integer.");
    public bool Boolean => Data.Length == 1 && Data[0] != 0;
    public IppAttribute? Member(string name) => Members?.FirstOrDefault(x => x.Name == name);
    public static IppValue String(byte tag, string value) => new(tag, Encoding.UTF8.GetBytes(value));
    public static IppValue Int(int value, byte tag = IppTag.Integer)
    {
        var data = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(data, value); return new(tag, data);
    }
    public static IppValue Bool(bool value) => new(IppTag.Boolean, [(byte)(value ? 1 : 0)]);
    public static IppValue Col(params IppAttribute[] members) => new(IppTag.Collection, [], members);
}

public sealed record IppMessage(byte Major, byte Minor, ushort Code, int RequestId, IReadOnlyList<IppAttribute> Attributes)
{
    public IppAttribute? Find(string name) => Attributes.FirstOrDefault(a => a.Name == name);
    public string? Text(string name) => Find(name)?.Values.FirstOrDefault()?.Text;
    public int Number(string name, int fallback = 0) => Find(name)?.Values.FirstOrDefault() is { Data.Length: 4 } v ? v.Integer : fallback;
    public bool Flag(string name) => Find(name)?.Values.FirstOrDefault()?.Boolean == true;
}

// RFC 8010: parses only attributes; document bytes remain in the source stream.
public static class IppCodec
{
    private const int MaxAttributesBytes = 1024 * 1024;
    public static async Task<IppMessage> ReadAsync(Stream input, CancellationToken cancellation = default)
    {
        var reader = new Reader(input, cancellation);
        var header = await reader.Bytes(8);
        var attrs = new List<IppAttribute>();
        byte group = 0;
        var groupStart = 0;
        for (var fields = 0; fields < 8192; fields++)
        {
            var tag = await reader.Byte();
            if (tag == IppTag.End)
                return new(header[0], header[1], BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)),
                    BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4)), attrs);
            if (tag < 0x10) { group = tag; groupStart = attrs.Count; continue; }
            if (group == 0) throw new InvalidDataException("Missing IPP group.");
            var name = await reader.Text();
            var value = await reader.Value(tag, 0);
            if (name.Length == 0)
            {
                if (attrs.Count <= groupStart || attrs[^1].Group != group) throw new InvalidDataException("Orphan IPP value.");
                ((List<IppValue>)attrs[^1].Values).Add(value);
            }
            else
            {
                if (attrs.Skip(groupStart).Any(a => a.Name == name)) throw new InvalidDataException("Duplicate IPP attribute.");
                attrs.Add(new(group, tag, name, new List<IppValue> { value }));
            }
        }
        throw new InvalidDataException("Too many IPP attributes.");
    }

    private sealed class Reader(Stream stream, CancellationToken cancellation)
    {
        private int _read;
        public async Task<byte[]> Bytes(int length)
        {
            _read = checked(_read + length);
            if (_read > MaxAttributesBytes) throw new InvalidDataException("IPP attributes too large.");
            var data = new byte[length]; await StreamReadExtensions.ReadExactlyAsync(stream,data,cancellation); return data;
        }
        public async Task<byte> Byte() => (await Bytes(1))[0];
        public async Task<byte[]> Field() => await Bytes(BinaryPrimitives.ReadUInt16BigEndian(await Bytes(2)));
        public async Task<string> Text() => Encoding.UTF8.GetString(await Field());
        public async Task<IppValue> Value(byte tag, int depth)
        {
            var data = await Field();
            if (tag != IppTag.Collection)
            {
                if ((tag is IppTag.Integer or IppTag.Enum) && data.Length != 4 || tag == IppTag.Boolean && data.Length != 1)
                    throw new InvalidDataException("Invalid IPP scalar length.");
                return new(tag, data);
            }
            if (depth >= 8 || data.Length != 0) throw new InvalidDataException("Invalid IPP collection.");
            var members = new List<IppAttribute>(); string? memberName = null;
            for (var count = 0; count < 4096; count++)
            {
                var memberTag = await Byte();
                if ((await Field()).Length != 0) throw new InvalidDataException("Collection name must be empty.");
                if (memberTag == IppTag.EndCollection)
                {
                    if ((await Field()).Length != 0) throw new InvalidDataException("Invalid collection end.");
                    return new(tag, [], members);
                }
                if (memberTag == IppTag.MemberName)
                {
                    memberName = await Text();
                    if (memberName.Length == 0 || members.Any(m => m.Name == memberName)) throw new InvalidDataException("Duplicate collection member.");
                    continue;
                }
                if (memberName is null) throw new InvalidDataException("Missing member name.");
                var memberValue = await Value(memberTag, depth + 1);
                if (members.LastOrDefault()?.Name == memberName) ((List<IppValue>)members[^1].Values).Add(memberValue);
                else members.Add(new(0, memberTag, memberName, new List<IppValue> { memberValue }));
            }
            throw new InvalidDataException("Collection too large.");
        }
    }

    public static byte[] Write(IppMessage message)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(message.Major); stream.WriteByte(message.Minor); U16(stream, message.Code);
        Span<byte> id = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(id, message.RequestId); stream.Write(id);
        byte group = 0;
        foreach (var a in message.Attributes)
        {
            if (a.Group != group || a.Values.Count == 0) { stream.WriteByte(a.Group); group = a.Group; }
            for (var i = 0; i < a.Values.Count; i++) WriteValue(stream, i == 0 ? a.Name : "", a.Values[i]);
        }
        stream.WriteByte(IppTag.End); return stream.ToArray();
    }
    private static void WriteValue(Stream stream, string name, IppValue value)
    {
        stream.WriteByte(value.Tag); Field(stream, Encoding.UTF8.GetBytes(name)); Field(stream, value.Data);
        if (value.Tag != IppTag.Collection) return;
        foreach (var member in value.Members ?? [])
        {
            stream.WriteByte(IppTag.MemberName); U16(stream, 0); Field(stream, Encoding.UTF8.GetBytes(member.Name));
            foreach (var v in member.Values) WriteValue(stream, "", v);
        }
        stream.WriteByte(IppTag.EndCollection); U16(stream, 0); U16(stream, 0);
    }
    private static void Field(Stream s, byte[] data) { U16(s, checked((ushort)data.Length)); s.Write(data); }
    private static void U16(Stream s, ushort n) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, n); s.Write(b); }
    public static IppAttribute Attr(byte group, string name, params IppValue[] values) => new(group, values[0].Tag, name, values);
    public static IppAttribute Text(byte group, byte tag, string name, params string[] values) => Attr(group, name, values.Select(v => IppValue.String(tag, v)).ToArray());
    public static IppAttribute Int(byte group, string name, params int[] values) => Attr(group, name, values.Select(v => IppValue.Int(v)).ToArray());
    public static IppMessage Response(IppMessage request, ushort status, IEnumerable<IppAttribute>? attrs = null, string? message = null)
    {
        var list = new List<IppAttribute>
        {
            Text(IppTag.Operation, IppTag.Charset, "attributes-charset", "utf-8"),
            Text(IppTag.Operation, IppTag.Language, "attributes-natural-language", "zh-cn")
        };
        if (message is not null) list.Add(Text(IppTag.Operation, IppTag.Text, "status-message", message));
        if (attrs is not null) list.AddRange(attrs);
        return new(request.Major is 1 or 2 ? request.Major : (byte)2, request.Major == 1 ? (byte)1 : (byte)0, status, request.RequestId, list);
    }
    public static IppValue Range(int min, int max)
    {
        byte[] b = new byte[8]; BinaryPrimitives.WriteInt32BigEndian(b, min); BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), max);
        return new(IppTag.Range, b);
    }
    public static IppValue Resolution(int dpi)
    {
        byte[] b = new byte[9]; BinaryPrimitives.WriteInt32BigEndian(b, dpi); BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), dpi); b[8] = 3;
        return new(IppTag.Resolution, b);
    }
}
