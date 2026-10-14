using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using GameDirector.Client;

namespace GameDirector.Workbench;

// Instance authority comes ONLY from the owning user's descriptor. Web admission
// checks still run first, but cannot authenticate another local process/user.
public sealed class WorkbenchAuth(TimeProvider? clock = null)
{
    public const string CookieName = "gd_session";
    public string Token { get; } = RandomId();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> codes = new(StringComparer.Ordinal);
    private readonly HashSet<string> sessions = new(StringComparer.Ordinal);
    private static string RandomId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public bool HasBearer(HttpRequest request)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || header.Parameter == null) return false;
        // Compare fixed-length digests, including for truncated/oversized tokens.
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(header.Parameter)), SHA256.HashData(Encoding.UTF8.GetBytes(Token)));
    }
    public bool Allows(HttpRequest request)
    {
        var path = request.Path.Value!.TrimEnd('/');
        if (HttpMethods.IsGet(request.Method) && path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)) return true;
        // The single-use code is the credential on this one bootstrap route.
        if (HttpMethods.IsPost(request.Method) && path.Equals("/api/session", StringComparison.OrdinalIgnoreCase)) return true;
        if (HasBearer(request)) return true;
        if (path.Equals("/api/session/code", StringComparison.OrdinalIgnoreCase)) return false; // mint is Bearer-only
        lock (gate) return request.Cookies.TryGetValue(CookieName, out var id) && sessions.Contains(id);
    }
    public string MintCode()
    {
        lock (gate) {
            var now = clock.GetUtcNow();
            foreach (var key in codes.Where(c => c.Value <= now).Select(c => c.Key).ToArray()) codes.Remove(key);
            var code = RandomId(); codes.Add(code, now + TimeSpan.FromSeconds(60)); return code;
        }
    }
    public string Exchange(string? code)
    {
        lock (gate) {
            if (code == null || !codes.Remove(code, out var expiry) || expiry <= clock.GetUtcNow()) throw new UnauthorizedFault();
            var session = RandomId(); sessions.Add(session); return session;
        }
    }
    // CreateNew prevents following a planted temp link. Set mode BEFORE writing
    // any secret; rename publishes complete bytes and preserves 0600. Windows
    // inherits the user-only ACL of the per-user LOCALAPPDATA store (no custom ACL).
    public static void WriteDescriptor(string path, object descriptor)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temp, options)) {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var bytes = Encoding.UTF8.GetBytes(DslJson.Serialize(descriptor)); file.Write(bytes); file.Flush(true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed record SessionExchange(string Code);
