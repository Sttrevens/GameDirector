using System.Diagnostics;

namespace GameDirector.Client;

public static class BrowserLauncher
{
    public const string Variable = "GAMEDIRECTOR_BROWSER";
    public static void Open(string url, TextWriter output)
    {
        if (string.Equals(Environment.GetEnvironmentVariable(Variable)?.Trim(), "none", StringComparison.OrdinalIgnoreCase)) {
            // The URL carries only a short-lived, single-use code, never the Bearer token.
            output.WriteLine("Open the Workbench: " + url); return;
        }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
