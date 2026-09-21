using System.Diagnostics;

internal static class TestDirectoryLinks
{
    // Junctions exercise Windows reparse-point protection without requiring the
    // symbolic-link privilege (or machine-wide Developer Mode).
    public static void Create(string path, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(path, target); return; }
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + path.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { process.Kill(true); throw new IOException("Timed out creating test junction"); }
        if (process.ExitCode != 0) throw new IOException(stderr.GetAwaiter().GetResult());
        stdout.GetAwaiter().GetResult();
    }
}
