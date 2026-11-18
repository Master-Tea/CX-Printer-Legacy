using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CxPrint.Core;

namespace CxPrint.Windows;

public sealed class SetupProgressListener : IDisposable
{
    private const string Prefix="CxPrintBridge.Setup.";
    private readonly NamedPipeServerStream _pipe;
    private SetupProgress? _latest;
    public string Name { get; }=Prefix+Guid.NewGuid().ToString("N");
    public SetupProgress? Latest=>Volatile.Read(ref _latest);
    public SetupProgressListener()
    {
        var security=new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
        foreach (var sid in new[]{WindowsIdentity.GetCurrent().User!,new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
            security.AddAccessRule(new PipeAccessRule(sid,PipeAccessRights.FullControl,AccessControlType.Allow));
        _pipe=LegacyPipeServer.Create(Name,PipeDirection.In,1,4096,4096,security);
    }
    public static bool IsValidName(string name)=>name.StartsWith(Prefix,StringComparison.Ordinal) && Guid.TryParseExact(name[Prefix.Length..],"N",out _);
    public async Task ListenAsync(IProgress<SetupProgress> progress,CancellationToken cancellation)
    {
        try
        {
            await _pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
            var prefix=new byte[4];
            while (true)
            {
                await StreamReadExtensions.ReadExactlyAsync(_pipe,prefix,cancellation).ConfigureAwait(false);
                var length=BitConverter.ToInt32(prefix);
                if (length is <1 or >8192) throw new InvalidDataException("安装进度数据无效。");
                var data=new byte[length]; await StreamReadExtensions.ReadExactlyAsync(_pipe,data,cancellation).ConfigureAwait(false);
                var value=JsonSerializer.Deserialize<SetupProgress>(data,Product.Json) ?? throw new InvalidDataException("安装进度数据为空。");
                Volatile.Write(ref _latest,value); progress.Report(value);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { }
    }
    public void Dispose()=>_pipe.Dispose();
}

public sealed class SetupProgressWriter : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;
    public bool IsConnected=>_pipe?.IsConnected==true;
    public string? Error { get; private set; }
    private SetupProgressWriter(NamedPipeClientStream? pipe)=>_pipe=pipe;
    public static async Task<SetupProgressWriter> ConnectAsync(string? name)
    {
        if (name is null) return new(null);
        if (!SetupProgressListener.IsValidName(name)) throw new ArgumentException("安装进度管道名称无效。");
        // Never allow the unelevated UI to impersonate the elevated installer.
        var pipe=new NamedPipeClientStream(".",name,PipeDirection.Out,PipeOptions.Asynchronous,TokenImpersonationLevel.Anonymous);
        try { await pipe.ConnectAsync(3000).ConfigureAwait(false); return new(pipe); }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        { pipe.Dispose(); return new(null) { Error=ex.Message }; }
    }
    public async Task ReportAsync(SetupProgress value)
    {
        var pipe=_pipe;
        if (pipe is null) return;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var data=JsonSerializer.SerializeToUtf8Bytes(value,Product.Json);
            if (data.Length>8192) data=JsonSerializer.SerializeToUtf8Bytes(value with { Message=value.Message[..Math.Min(value.Message.Length,800)] },Product.Json);
            await pipe.WriteAsync(BitConverter.GetBytes(data.Length),timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(data,timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        { Error=ex.Message; pipe.Dispose(); _pipe=null; }
    }
    public async ValueTask DisposeAsync()
    {
        if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
