using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GameDirector.Godot;

/// <summary>Engine-local editor recovery identity; not an EngineContract capability.
/// A fresh authenticated challenge binds the launch secret to PID and project.
/// PID alone never establishes ownership; the secret is never sent on argv or HTTP.</summary>
public sealed record GodotDockOwner(string Token, string Project)
{
    public static string TokenEnvironment => GodotDefaults.OwnerEnvironment;
    public static string LogOption => GodotDefaults.LogOption;
    public static TextWriter OpenLog(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        // The dock creates its private directory; do not chmod arbitrary caller-owned parents.
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(path, options);
        try {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return TextWriter.Synchronized(new StreamWriter(stream) { AutoFlush = true });
        } catch { stream.Dispose(); throw; }
    }
    public static string NormalizeProject(string project)
    {
        var path = project.Replace('\\', '/');
        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
            path = char.ToUpperInvariant(path[0]) + path[1..];
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0) return "/";
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "/" : trimmed;
    }
    private byte[] Digest(string challenge)
    {
        if (string.IsNullOrWhiteSpace(challenge)) throw new ArgumentException("dock owner challenge is required");
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(Token), Encoding.UTF8.GetBytes(
            challenge + "\n" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n" + NormalizeProject(Project)));
    }
    public bool Verify(string challenge, string proof)
    {
        if (string.IsNullOrWhiteSpace(challenge) || proof.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(Digest(challenge), Convert.FromHexString(proof)); }
        catch (FormatException) { return false; }
    }
    public object Prove(string challenge) => new { pid = Environment.ProcessId, project = NormalizeProject(Project),
        proof = Convert.ToHexString(Digest(challenge)).ToLowerInvariant() };
}
