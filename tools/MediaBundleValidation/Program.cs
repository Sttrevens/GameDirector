using System.Text.Json;
using GameDirector.Client;

// Real-media integration check. Only writes a new caller-selected output tree;
// does not install into the user's active media folder or change resolution.
if (args.Length != 3) throw new ArgumentException("Usage: <new-output-folder> <ffmpeg-path> <ffprobe-path>");
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Use a fresh validation output folder.");
var input = Path.Combine(root, "input");
Directory.CreateDirectory(input);
var files = new Dictionary<string, object>();
foreach (var (name, source) in new[] { ("ffmpeg", args[1]), ("ffprobe", args[2]) })
{
    var file = MediaBundleInstaller.ToolFileName(name);
    var destination = Path.Combine(input, file);
    File.Copy(Path.GetFullPath(source), destination);
    files[name] = new { path = file, sha256 = MediaTools.Hash(destination) };
}
var descriptor = Path.Combine(input, "bundle.json");
await File.WriteAllTextAsync(descriptor, JsonSerializer.Serialize(new {
    schemaVersion = 1, rid = DistributionManifest.CurrentRid(), files,
    source = "Existing local media installation; validation copy only", license = "See source installation"
}));
var target = Path.Combine(root, "installed");
var log = new List<string>();
var receipt = await MediaBundleInstaller.Install(descriptor, DistributionManifest.CurrentRid(),
    MediaDistributions.LoadBundled().Unit, target, log.Add, CancellationToken.None);
await MediaTools.EnsureEncoding(MediaProfile.Default, true,
    Path.Combine(target, MediaBundleInstaller.ToolFileName("ffmpeg")),
    Path.Combine(target, MediaBundleInstaller.ToolFileName("ffprobe")));
var result = JsonSerializer.Serialize(new { passed = true, stagedRealCapability = true,
    activatedRealCapability = true, receipt, log }, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(root, "result.json"), result);
Console.WriteLine(result);
