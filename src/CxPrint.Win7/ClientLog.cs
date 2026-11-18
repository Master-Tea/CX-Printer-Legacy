using System.Text;

namespace CxPrint.Win7;

internal static class ClientLog
{
    internal static string? Append(string name, string content) => Save(name, content, append: true);
    internal static string? Write(string name, string content) => Save(name, content, append: false);

    private static string? Save(string name, string content, bool append)
    {
        foreach (var directory in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CX Printer Legacy", "Logs"),
            AppContext.BaseDirectory,
            Path.Combine(Path.GetTempPath(), "CX Printer Legacy", "Logs")
        })
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, name);
                if (append) File.AppendAllText(path, content, Encoding.UTF8);
                else File.WriteAllText(path, content, Encoding.UTF8);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { /* Try the next location; never replace the original failure with a logging failure. */ }
        }
        return null;
    }
}
