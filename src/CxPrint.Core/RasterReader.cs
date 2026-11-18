using System.Buffers.Binary;

namespace CxPrint.Core;

// Independent, bounded reader for the advertised URF W8/SRGB24 and PWG sgray_8/srgb_8 formats.
// Wire layout and modified PackBits are documented by OpenPrinting CUPS raster-stream.c.
public sealed class RasterReader
{
    private readonly Stream _stream;
    private readonly bool _apple;
    private readonly uint _pageCount;
    private int _pages;
    public RasterReader(Stream stream)
    {
        _stream=stream; var signature=Read(4);
        _apple=signature.AsSpan().SequenceEqual("UNIR"u8);
        if (_apple)
        {
            var header=Read(8);
            if (!header.AsSpan(0,4).SequenceEqual("AST\0"u8)) throw new InvalidDataException("无效的 URF 文件头。");
            _pageCount=BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        }
        else if (!signature.AsSpan().SequenceEqual("RaS2"u8)) throw new InvalidDataException("不支持的光栅格式。");
    }
    public RasterPage? NextPage()
    {
        var first=_stream.ReadByte();
        if (first<0)
        {
            if (_apple && _pageCount!=0 && _pageCount!=uint.MaxValue && _pageCount!=_pages) throw new InvalidDataException("URF 页数不匹配。");
            return null;
        }
        if (++_pages>1000) throw new InvalidDataException("最多支持 1000 页。");
        var header=new byte[_apple ? 32 : 1796]; header[0]=(byte)first; _stream.ReadExactly(header.AsSpan(1));
        int Number(int offset) => checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(offset,4)));
        var width=Number(_apple ? 12 : 372); var height=Number(_apple ? 16 : 376);
        var bpp=_apple ? header[0] : Number(388);
        var colorspace=_apple ? header[1] : Number(400);
        var dpi=Number(_apple ? 20 : 276);
        if (width is < 1 or > 20000 || height is < 1 or > 20000 || (long)width*height>64_000_000 || dpi is < 72 or > 2400)
            throw new InvalidDataException("光栅页面尺寸或分辨率超出限制。");
        if (bpp is not (8 or 24) || (_apple ? bpp==8 && colorspace!=0 || bpp==24 && colorspace!=1 :
            Number(384)!=8 || Number(396)!=0 || bpp==8 && colorspace!=18 || bpp==24 && colorspace!=19))
            throw new InvalidDataException("仅支持 8 位灰度和 24 位 sRGB 光栅。");
        var stride=checked(width*(bpp/8));
        if (!_apple && Number(392)!=stride) throw new InvalidDataException("光栅行长度与宽度不一致。");
        return new RasterPage(_stream,width,height,bpp/8,dpi);
    }
    private byte[] Read(int length) { var b=new byte[length]; _stream.ReadExactly(b); return b; }
}

public sealed class RasterPage
{
    private readonly Stream _stream;
    public int Width { get; }
    public int Height { get; }
    public int BytesPerPixel { get; }
    public int Dpi { get; }
    internal RasterPage(Stream stream,int width,int height,int bytesPerPixel,int dpi)
        => (_stream,Width,Height,BytesPerPixel,Dpi)=(stream,width,height,bytesPerPixel,dpi);
    public void DecodeRows(Action<int,byte[]> consume, CancellationToken cancellation = default)
    {
        var row=new byte[checked(Width*BytesPerPixel)]; var y=0;
        while (y<Height)
        {
            cancellation.ThrowIfCancellationRequested();
            var repeats=Byte()+1;
            if (y+repeats>Height) throw new InvalidDataException("光栅重复行超出页面。");
            var offset=0;
            while (offset<row.Length)
            {
                var code=Byte();
                if (code==128) { row.AsSpan(offset).Fill(255); break; }
                var pixels=code<128 ? code+1 : 257-code;
                var length=checked(pixels*BytesPerPixel);
                if (offset+length>row.Length) throw new InvalidDataException("光栅压缩数据超出行长度。");
                if (code<128)
                {
                    _stream.ReadExactly(row.AsSpan(offset,BytesPerPixel));
                    for (var i=1;i<pixels;i++) row.AsSpan(offset,BytesPerPixel).CopyTo(row.AsSpan(offset+i*BytesPerPixel));
                }
                else _stream.ReadExactly(row.AsSpan(offset,length));
                offset+=length;
            }
            for (var i=0;i<repeats;i++) consume(y++,row);
        }
    }
    private int Byte() { var n=_stream.ReadByte(); return n<0 ? throw new EndOfStreamException("光栅数据不完整。") : n; }
}
