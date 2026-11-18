using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace CxPrint.Windows;

public static class LegacyPipeServer
{
    // .NET 6 rejects FILE_FLAG_FIRST_PIPE_INSTANCE in PipeOptions. Create the first
    // Windows handle natively, retaining both first-instance protection and the caller's ACL.
    public static NamedPipeServerStream Create(string name, PipeDirection direction, int maxInstances,
        int inBufferSize, int outBufferSize, PipeSecurity security, bool firstInstance = true)
    {
        if (!firstInstance)
            return NamedPipeServerStreamAcl.Create(name, direction, maxInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, inBufferSize, outBufferSize, security);
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains('/') || name.Contains(':'))
            throw new ArgumentException("无效的本地管道名称。", nameof(name));
        if (direction is not (PipeDirection.In or PipeDirection.Out or PipeDirection.InOut))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (maxInstances is < 1 or > 254 || inBufferSize < 0 || outBufferSize < 0)
            throw new ArgumentOutOfRangeException(nameof(maxInstances));
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = pinned.AddrOfPinnedObject(),
                InheritHandle = 0
            };
            const uint firstPipeInstance = 0x00080000, overlapped = 0x40000000;
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, (uint)direction | firstPipeInstance | overlapped,
                0, (uint)maxInstances, (uint)outBufferSize, (uint)inBufferSize, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "无法创建本地通信管道：" + new Win32Exception(error).Message);
            }
            try { return new NamedPipeServerStream(direction, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { pinned.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);
}
