using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CxPrint.Core;

public sealed record DigestSecret(string Md5, string Sha256)
{
    public static DigestSecret Create(string username, string password)
    {
        Validate(username,password);
        var input = Encoding.UTF8.GetBytes($"{username}:{Product.Realm}:{password}");
        try { return new(Convert.ToHexString(MD5.HashData(input)).ToLowerInvariant(), Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()); }
        finally { CryptographicOperations.ZeroMemory(input); }
    }
    public static void Validate(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 64 || username.Any(c=>c==':' || char.IsControl(c)))
            throw new ArgumentException("用户名需为 1–64 个字符，不能包含冒号或控制字符。");
        if (password.Length is < 8 or > 256 || password.Any(char.IsControl))
            throw new ArgumentException("密码需为 8–256 个字符，不能包含控制字符。");
    }
}

// Stateless signed nonces, bounded lifetime and nonce-count replay protection (RFC 7616).
public sealed partial class DigestAuthentication
{
    private readonly byte[] _nonceKey = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string,(uint Count,long Expires)> _counts = new();
    private readonly TimeProvider _time;
    public DigestAuthentication(TimeProvider? time = null) => _time = time ?? TimeProvider.System;
    public string Challenge(string algorithm = "SHA-256") =>
        $"Digest realm=\"{Product.Realm}\", nonce=\"{NewNonce()}\", algorithm={algorithm}, qop=\"auth\", charset=UTF-8";
    private string NewNonce()
    {
        var payload = $"{_time.GetUtcNow().ToUnixTimeSeconds()}:{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}";
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(payload+":"+Sign(payload)));
    }
    private string Sign(string payload) => Convert.ToHexString(HMACSHA256.HashData(_nonceKey,Encoding.ASCII.GetBytes(payload)));
    private bool NonceValid(string nonce)
    {
        try
        {
            var raw = Encoding.ASCII.GetString(Convert.FromBase64String(nonce)); var parts=raw.Split(':');
            if (parts.Length!=3 || !long.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out var timestamp)) return false;
            var age=_time.GetUtcNow().ToUnixTimeSeconds()-timestamp;
            return age is >= 0 and <= 300 && Equal(parts[2],Sign(parts[0]+":"+parts[1]));
        }
        catch (FormatException) { return false; }
    }
    public bool Verify(string? header, string method, string requestTarget, string username, DigestSecret secret)
    {
        if (header is null || !header.StartsWith("Digest ",StringComparison.OrdinalIgnoreCase) || header.Length>8192) return false;
        var fields=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Fields().Matches(header[7..]))
            if (!fields.TryAdd(match.Groups[1].Value,match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value)) return false;
        string Get(string key) => fields.GetValueOrDefault(key,"");
        var algorithm=Get("algorithm"); if (algorithm.Length==0) algorithm="MD5";
        if (Get("username")!=username || Get("realm")!=Product.Realm || Get("uri")!=requestTarget || Get("qop")!="auth" ||
            !NonceValid(Get("nonce")) || Get("cnonce").Length is < 1 or > 256 ||
            Get("nc").Length!=8 || !uint.TryParse(Get("nc"),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var count) || count==0)
            return false;
        if (algorithm is not ("MD5" or "SHA-256")) return false;
        string Hash(string s) => Convert.ToHexString(algorithm=="MD5" ? MD5.HashData(Encoding.UTF8.GetBytes(s)) : SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        var expected=Hash($"{(algorithm=="MD5" ? secret.Md5 : secret.Sha256)}:{Get("nonce")}:{Get("nc")}:{Get("cnonce")}:auth:{Hash(method+":"+requestTarget)}");
        if (!Equal(Get("response"),expected)) return false;
        var now=_time.GetUtcNow().ToUnixTimeSeconds();
        foreach (var entry in _counts) if (entry.Value.Expires<now) _counts.TryRemove(entry.Key,out _);
        if (_counts.Count>10000) return false;
        var key=Get("nonce")+":"+Get("cnonce");
        while (true)
        {
            if (_counts.TryGetValue(key,out var prior))
            {
                if (count<=prior.Count) return false;
                if (_counts.TryUpdate(key,(count,now+300),prior)) return true;
            }
            else if (_counts.TryAdd(key,(count,now+300))) return true;
        }
    }
    private static bool Equal(string a,string b) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a),Encoding.ASCII.GetBytes(b));
    private static readonly Regex FieldRegex=new("(?:^|,)\\s*([a-zA-Z][a-zA-Z0-9_-]*)\\s*=\\s*(?:\"([^\"\\r\\n]*)\"|([^,\\s]+))",RegexOptions.CultureInvariant|RegexOptions.Compiled);
    private static Regex Fields()=>FieldRegex;
}
