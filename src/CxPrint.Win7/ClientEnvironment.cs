using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace CxPrint.Win7;

internal static class ClientEnvironment
{
    internal const int MediumIntegrity = 0x2000;
    internal const string RestrictedMessage = "当前程序正以低完整性权限运行，Windows 会阻止它连接后台服务及写入普通用户日志。" +
        "\r\n\r\n请关闭此窗口，将完整 ZIP 重新解压到普通用户目录（例如桌面新建的文件夹），再运行 CX Printer.exe。" +
        "不要直接从沙盒或受限制的构建目录启动；已有后台服务无需反复安装。";

    internal static int IntegrityLevel()
    {
        using var identity = WindowsIdentity.GetCurrent();
        const int tokenIntegrityLevel = 25;
        GetTokenInformation(identity.AccessToken, tokenIntegrityLevel, IntPtr.Zero, 0, out var length);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(identity.AccessToken, tokenIntegrityLevel, buffer, length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            return int.Parse(sid[(sid.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static string? StartupProblem() => IntegrityLevel() < MediumIntegrity ? RestrictedMessage : null;

    internal static object Describe()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new
        {
            Version = typeof(Program).Assembly.GetName().Version?.ToString(),
            OS = Environment.OSVersion.ToString(), Runtime = Environment.Version.ToString(),
            Executable = Application.ExecutablePath, UserSid = identity.User?.Value,
            IntegrityLevel = IntegrityLevel(),
            Elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
        };
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, int informationLength, out int returnLength);
}
