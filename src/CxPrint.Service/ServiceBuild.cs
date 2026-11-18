using System.Reflection;
using System.Security.Cryptography;

namespace CxPrint.Service;

internal static class ServiceBuild
{
    internal static string Version { get; }=typeof(ServiceBuild).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ServiceBuild).Assembly.GetName().Version?.ToString() ?? "unknown";
    internal static string Sha256 { get; }=ReadHash();
    private static string ReadHash()
    {
        try
        {
            using var file=File.OpenRead(typeof(ServiceBuild).Assembly.Location);
            using var hash=SHA256.Create();
            return Convert.ToHexString(hash.ComputeHash(file)).ToLowerInvariant();
        }
        catch (IOException) { return "unavailable"; }
        catch (UnauthorizedAccessException) { return "unavailable"; }
    }
}
