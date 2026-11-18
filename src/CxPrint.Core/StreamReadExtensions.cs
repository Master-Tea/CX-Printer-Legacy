namespace CxPrint.Core;

public static class StreamReadExtensions
{
    public static async Task ReadExactlyAsync(Stream stream,byte[] buffer,CancellationToken cancellation)
    {
        var offset=0;
        while (offset<buffer.Length)
        {
            var read=await stream.ReadAsync(buffer,offset,buffer.Length-offset,cancellation).ConfigureAwait(false);
            if (read==0) throw new EndOfStreamException();
            offset+=read;
        }
    }

    public static void ReadExactly(this Stream stream,Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var read=stream.Read(buffer);
            if (read==0) throw new EndOfStreamException();
            buffer=buffer[read..];
        }
    }
}
