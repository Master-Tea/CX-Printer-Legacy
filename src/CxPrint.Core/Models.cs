using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CxPrint.Core;

public static class Product
{
    private static readonly long StartedTimestamp=System.Diagnostics.Stopwatch.GetTimestamp();
    public static readonly DateTimeOffset StartedAt=DateTimeOffset.UtcNow;
    public static int UptimeSeconds=>(int)Math.Clamp(ElapsedSince(StartedTimestamp).TotalSeconds,1,int.MaxValue);
    public static TimeSpan ElapsedSince(long timestamp)=>TimeSpan.FromSeconds((double)(System.Diagnostics.Stopwatch.GetTimestamp()-timestamp)/System.Diagnostics.Stopwatch.Frequency);
    public const string ServiceName = "CxPrintBridge";
    public const string PipeName = "CxPrintBridge.Control.v1";
    public const string Realm = "CX Printer";
    public const int Port = 8631;
    public const long MaxDocumentBytes = 256L * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string PrinterId(string name) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16].ToLowerInvariant();
}

// IPP media dimensions and margins are hundredths of a millimetre, never inches.
public sealed record MediaSize(string Key, string DisplayName, int Width, int Height, int WindowsPaperId,
    int Left = 635, int Top = 635, int Right = 635, int Bottom = 635)
{
    public string Description => $"{DisplayName} · {Width / 100.0:0.##} × {Height / 100.0:0.##} mm";
    public static string Keyword(int width, int height)
    {
        var known = new (int W, int H, string Key)[]
        {
            (21000,29700,"iso_a4_210x297mm"), (29700,42000,"iso_a3_297x420mm"),
            (14800,21000,"iso_a5_148x210mm"), (10500,14800,"iso_a6_105x148mm"),
            (17600,25000,"iso_b5_176x250mm"), (18200,25700,"jis_b5_182x257mm"),
            (21590,27940,"na_letter_8.5x11in"), (21590,35560,"na_legal_8.5x14in"),
            (27940,43180,"na_ledger_11x17in"), (18415,26670,"na_executive_7.25x10.5in"),
            (10000,14800,"jpn_hagaki_100x148mm"), (11000,22000,"iso_dl_110x220mm"),
            (16200,22900,"iso_c5_162x229mm"), (10478,24130,"na_number-10_4.125x9.5in"),
            (10160,15240,"na_index-4x6_4x6in"), (12700,17780,"na_5x7_5x7in")
        };
        foreach (var size in known)
            if (Math.Abs(width - size.W) <= 25 && Math.Abs(height - size.H) <= 25) return size.Key;
        return string.Create(CultureInfo.InvariantCulture, $"custom_cx_{width / 100.0:0.##}x{height / 100.0:0.##}mm");
    }
}

public sealed record PrinterInfo(string Id, string Name, string Driver, string Port, bool Color, bool Duplex,
    IReadOnlyList<MediaSize> Media, string DefaultMedia, string? Error = null)
{
    public PrinterSupplies? Supplies { get; init; }
    public bool Usable => Error is null && Media.Count > 0;
    public bool IsPdfTest => IsMicrosoftPdfDriver(Driver);
    public static bool IsMicrosoftPdfDriver(string driver)=>driver.Equals("Microsoft Print To PDF",StringComparison.OrdinalIgnoreCase);
    public string Summary => Error ?? $"{(IsPdfTest ? "PDF 测试 · " : "")}{Media.Count} 种纸张 · {(Color ? "彩色" : "黑白")} · {(Duplex ? "双面" : "单面")}{(IsPdfTest ? " · 自动保存文件" : "")}";
}

public sealed record SharedPrinter(string Id, string Name, string? DefaultMedia = null, string? DisplayName = null)
{
    public const int MaxDisplayNameBytes = 63;
    private const string DisplayNameSuffix = "- CX Printer";
    [JsonIgnore] public string EffectiveDisplayName => NormalizeDisplayName(DisplayName) ?? DefaultDisplayName(Name);
    public static string? NormalizeDisplayName(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static string DefaultDisplayName(string printerName)
    {
        // A DNS-SD instance is one UTF-8 label. Keep the suffix and whole Unicode runes.
        var text = new StringBuilder();
        var source = printerName.Trim();
        var budget = MaxDisplayNameBytes - Encoding.UTF8.GetByteCount(DisplayNameSuffix);
        var discriminator = Encoding.UTF8.GetByteCount(source) > budget ? "-" + Product.PrinterId(printerName)[..6] : "";
        budget -= discriminator.Length;
        foreach (var rune in source.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > budget) break;
            text.Append(rune.ToString()); budget -= rune.Utf8SequenceLength;
        }
        return text.ToString().TrimEnd() + discriminator + DisplayNameSuffix;
    }
    public void ValidateDisplayName()
    {
        if (NormalizeDisplayName(DisplayName) is not { } name) return;
        if (name.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029'))
            throw new ArgumentException($"{Name} 的 Apple 显示名称不能包含换行或控制字符。");
        if (Encoding.UTF8.GetByteCount(name) > MaxDisplayNameBytes)
            throw new ArgumentException($"{Name} 的 Apple 显示名称过长，请缩短（最多 63 个英文字符或 21 个汉字）。");
    }
}
public sealed record AuthSettings(bool Enabled = false, string Username = "", string? ProtectedHa1 = null);
public sealed record BridgeSettings
{
    public int Version { get; init; } = 1;
    public int Port { get; init; } = Product.Port;
    public string OwnerSid { get; init; } = "";
    public bool AutoStart { get; init; } = true;
    public bool Enabled { get; init; } = true;
    public List<SharedPrinter> Printers { get; init; } = [];
    public AuthSettings Authentication { get; init; } = new();
    public void Validate()
    {
        if (Version != 1) throw new InvalidDataException("不支持此设置文件版本。");
        if (Port is < 1024 or > 65535) throw new ArgumentException("端口必须在 1024–65535 之间。");
        if (Printers.Count > 64 || Printers.Select(p => p.Id).Distinct().Count() != Printers.Count)
            throw new ArgumentException("打印机列表无效。");
        if (Printers.Any(p => p.Id != Product.PrinterId(p.Name) || p.Name.Length > 256))
            throw new ArgumentException("打印机标识与名称不匹配。");
        foreach (var printer in Printers) printer.ValidateDisplayName();
        if (Printers.Select(p => p.EffectiveDisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Printers.Count)
            throw new ArgumentException("共享打印机的 Apple 显示名称不能重复，请为每台打印机设置不同的名称。");
        if (Authentication.Enabled && (string.IsNullOrWhiteSpace(Authentication.Username) || Authentication.ProtectedHa1 is null))
            throw new ArgumentException("开启认证前需要设置打印用户名和密码。");
    }
}

public sealed record ApplySettings(List<SharedPrinter> Printers, bool AuthenticationEnabled, string Username,
    string? NewPassword = null, bool Enabled = true);
public sealed record ControlRequest(string Command, ApplySettings? Settings = null);
public sealed record JobInfo(int Id, string PrinterId, string Name, string User, int State, string Message,
    DateTimeOffset Created, int Pages = 0);
public sealed record BridgeStatus(bool Running, bool Enabled, int Port, bool AutoStart, bool AuthenticationEnabled,
    string Username, bool HasPassword, IReadOnlyList<PrinterInfo> Printers, IReadOnlyList<SharedPrinter> SharedPrinters,
    IReadOnlyList<JobInfo> Jobs, IReadOnlyList<string> Addresses, string DiscoveryStatus, string? Error = null,
    bool PrintersRefreshing = false, string? PrinterScanStatus = null, BridgeDiagnostics? Diagnostics = null);
public sealed record BridgeDiagnostics(string ServiceVersion, string ServiceSha256, string? DiscoveryError,
    IReadOnlyList<string> RecentIppRequests);
public sealed record ControlReply(bool Success, string? Error = null, BridgeStatus? Status = null);
public sealed record SetupProgress(int Percent,string Message,bool Failed = false);
