using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Pipes;
using CxPrint.Core;

namespace CxPrint.Windows;

public static class WindowsSecurity
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    public static string ProtectSecret(DigestSecret secret) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(secret,Product.Json)),true));
    public static DigestSecret UnprotectSecret(string encoded)
    {
        var raw=Transform(Convert.FromBase64String(encoded),false);
        try { return JsonSerializer.Deserialize<DigestSecret>(raw,Product.Json) ?? throw new InvalidDataException("认证数据损坏。"); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
    private static byte[] Transform(byte[] input,bool protect)
    {
        var source=new Blob { Length=input.Length,Data=Marshal.AllocHGlobal(input.Length) };
        try
        {
            Marshal.Copy(input,0,source.Data,input.Length);
            Blob output;
            var ok=protect ? CryptProtectData(ref source,null,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,5,out output)
                : CryptUnprotectData(ref source,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { var result=new byte[output.Length]; Marshal.Copy(output.Data,result,0,result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            Marshal.Copy(new byte[input.Length],0,source.Data,input.Length);
            Marshal.FreeHGlobal(source.Data); CryptographicOperations.ZeroMemory(input);
        }
    }
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input,string? description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

public static class ControlClient
{
    public static Task<ControlReply> SendAsync(ControlRequest request,CancellationToken cancellation=default) =>
        SendToPipeAsync(Product.PipeName+(Environment.GetEnvironmentVariable("CXPRINTER_DEBUG_SERVICE")=="1" ? ".Debug" : ""),request,cancellation);

    public static async Task<ControlReply> SendToPipeAsync(string pipeName,ControlRequest request,CancellationToken cancellation=default)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(3000,deadline.Token);
        var payload=JsonSerializer.SerializeToUtf8Bytes(request,Product.Json);
        if (payload.Length>65536) throw new InvalidDataException("设置数据过大。");
        await pipe.WriteAsync(BitConverter.GetBytes(payload.Length),deadline.Token);
        await pipe.WriteAsync(payload,deadline.Token); await pipe.FlushAsync(deadline.Token);
        var length=new byte[4]; await StreamReadExtensions.ReadExactlyAsync(pipe,length,deadline.Token);
        var count=BitConverter.ToInt32(length);
        if (count is < 1 or > 4_194_304) throw new InvalidDataException("服务返回无效的数据长度。");
        var data=new byte[count]; await StreamReadExtensions.ReadExactlyAsync(pipe,data,deadline.Token);
        return JsonSerializer.Deserialize<ControlReply>(data,Product.Json) ?? throw new InvalidDataException("服务返回无效的数据。");
    }
}
