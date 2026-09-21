using System.Diagnostics;

namespace GameDirector.Client;

/// <summary>Launch the sibling native apps shipped next to gd in a full
/// distribution (gamedirector-workbench, gamedirector-mcp). The child inherits
/// this process's stdio untouched (an MCP stdio host depends on that), its
/// exit code is returned verbatim, and cancellation tears the child down.</summary>
public static class RuntimeLauncher
{
    /// <summary>Resolve a sibling app executable inside this distribution.
    /// Throws an actionable error when this gd was not shipped with that app.</summary>
    public static string ResolveSibling(string appKey)
    {
        var root = DistributionManifest.FindRoot();
        if (root == null)
            throw new InvalidOperationException(
                $"This gd does not belong to a full GameDirector distribution, so it cannot launch '{appKey}'. " +
                $"Install the full distribution for this platform (tools/package_release.py output) or set {DistributionManifest.RootOverrideVariable}.");
        var manifest = DistributionManifest.Load(Path.Combine(root, DistributionManifest.ManifestFileName));
        var executable = manifest.SiblingExecutable(appKey, root, DistributionManifest.CurrentRid());
        if (!File.Exists(executable))
            throw new InvalidOperationException(
                $"The distribution at {root} has no '{appKey}' app at {Path.GetRelativePath(root, executable)}. " +
                "Reinstall the complete distribution for this platform.");
        var runtime = RuntimeManifest.Load(Path.Combine(Path.GetDirectoryName(executable)!, DistributionManifest.RuntimeManifestFileName));
        var mismatch = runtime.CompatibilityFailure(DistributionManifest.CurrentRid(), manifest.ProtocolVersion);
        if (mismatch != null || runtime.App != appKey)
            throw new InvalidOperationException("Companion runtime is incompatible: " + (mismatch ?? "wrong application identity") + ". Reinstall the matching complete distribution.");
        runtime.ValidateDeclaredPaths();
        // The native host loads managed assemblies and runtime libraries. Its
        // own hash alone cannot verify the program that will actually execute.
        var relative = Path.GetFileName(executable);
        var failure = !runtime.Sha256.ContainsKey(relative)
            ? "entry executable is not declared in the runtime manifest: " + relative
            : runtime.IntegrityFailure(Path.GetDirectoryName(executable)!);
        if (failure != null)
            throw new InvalidOperationException("Companion runtime failed integrity verification: " + failure + ". Reinstall the complete distribution.");
        return executable;
    }

    /// <summary>Run an executable with the caller's stdio inherited. Returns the
    /// child exit code; cancellation requests a tree kill and still returns the
    /// observed exit code (143/130 style termination codes come from the child).</summary>
    public static async Task<int> Run(string executable, IEnumerable<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("could not start " + executable);
        using var registration = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { /* already exited */ }
        });
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The registration above already requested a tree kill; observe the
            // exit so the caller propagates the child's status faithfully.
            if (!process.HasExited) await process.WaitForExitAsync();
        }
        return process.ExitCode;
    }

    public static Task<int> RunSibling(string appKey, IEnumerable<string> args, CancellationToken ct) =>
        Run(ResolveSibling(appKey), args, ct);
}
