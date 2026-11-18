using System.Text.Json;
using CxPrint.Core;
using CxPrint.Windows;

namespace CxPrint.Service;

public static class Paths
{
    private static string? _debugRoot;
    public static bool DebugMode => _debugRoot is not null;
    public static string PipeName => Product.PipeName + (DebugMode ? ".Debug" : "");
    public static void UseDebugDirectory(string directory) => _debugRoot=Path.GetFullPath(directory);
    public static string Root => _debugRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"CX Printer");
    public static string Settings => Path.Combine(Root,"settings.json");
    public static string Spool => Path.Combine(Root,"Spool");
    public static string TestOutput => Path.Combine(Root,"TestOutput");
    public static string Logs => Path.Combine(Root,"Logs");
}

public sealed class StateStore
{
    private readonly object _lock=new();
    private BridgeSettings _settings;
    private readonly string _path;
    public StateStore(string? path=null)
    {
        _path=path ?? Paths.Settings;
        if (!File.Exists(_path)) throw new InvalidOperationException("尚未安装服务。请在 CX Printer 中点击“安装服务”。");
        _settings=JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(_path),Product.Json) ?? throw new InvalidDataException("设置文件损坏。");
        _settings.Validate();
    }
    public BridgeSettings Settings { get { lock (_lock) return _settings; } }
    public event Action? Changed;
    public void Apply(ApplySettings apply,IReadOnlyList<PrinterInfo> printers)
    {
        BridgeSettings next;
        lock (_lock)
        {
            foreach (var selected in apply.Printers)
            {
                var printer=printers.FirstOrDefault(p=>p.Id==selected.Id && p.Name==selected.Name && p.Usable)
                    ?? throw new ArgumentException($"打印机 {selected.Name} 不可用。");
                if (selected.DefaultMedia is not null && !printer.Media.Any(m=>m.Key==selected.DefaultMedia))
                    throw new ArgumentException($"{selected.Name} 不支持所选纸张。");
            }
            var auth=_settings.Authentication;
            var username=apply.Username.Trim();
            if (apply.NewPassword is { Length: > 0 } password)
                auth=new(apply.AuthenticationEnabled,username,WindowsSecurity.ProtectSecret(DigestSecret.Create(username,password)));
            else
            {
                if (apply.AuthenticationEnabled && (auth.ProtectedHa1 is null || username!=auth.Username))
                    throw new ArgumentException("首次开启认证或更改用户名时需要输入新密码。");
                if (username!=auth.Username && auth.ProtectedHa1 is not null)
                    throw new ArgumentException("更改用户名时需要设置新密码。若只想关闭认证，请保留原用户名。");
                auth=auth with { Enabled=apply.AuthenticationEnabled,Username=username };
            }
            next=_settings with { Printers=apply.Printers.Select(p=>p with { DisplayName=SharedPrinter.NormalizeDisplayName(p.DisplayName) }).ToList(),Authentication=auth,Enabled=apply.Enabled };
            next.Validate(); Save(next); _settings=next;
        }
        Changed?.Invoke();
    }
    private void Save(BridgeSettings settings)
    {
        var temp=_path+".tmp";
        using (var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            JsonSerializer.Serialize(file,settings,Product.Json);
        File.Move(temp,_path,true);
    }
}

public sealed class FileLog : ILoggerProvider
{
    private readonly object _gate=new();
    public ILogger CreateLogger(string categoryName) => new Sink(this,categoryName);
    public void Dispose() { }
    private void Write(string category,LogLevel level,string message,Exception? ex)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Paths.Logs);
                var path=Path.Combine(Paths.Logs,DateTime.UtcNow.ToString("yyyy-MM-dd")+".log");
                // A runaway fault cannot fill the system disk.
                if (File.Exists(path) && new FileInfo(path).Length>10*1024*1024) return;
                File.AppendAllText(path,$"{DateTimeOffset.Now:O} [{level}] {category}: {message}{Environment.NewLine}{(ex is null ? "" : ex+Environment.NewLine)}");
                foreach (var old in Directory.EnumerateFiles(Paths.Logs,"*.log").Where(p=>File.GetLastWriteTimeUtc(p)<DateTime.UtcNow.AddDays(-14))) File.Delete(old);
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private sealed class Sink(FileLog owner,string category) : ILogger
    {
        private sealed class EmptyScope : IDisposable
        {
            internal static readonly EmptyScope Instance=new();
            public void Dispose() { }
        }
        public IDisposable BeginScope<TState>(TState state) => EmptyScope.Instance;
        public bool IsEnabled(LogLevel level) => level>=LogLevel.Information;
        public void Log<TState>(LogLevel level,EventId eventId,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
        { if (IsEnabled(level)) owner.Write(category,level,formatter(state,exception),exception); }
    }
}
