using System.Text.RegularExpressions;
using GameDirector.Core.Dsl;

namespace GameDirector.Godot;

internal static class ProjectPreflight
{
    internal static void RequireSceneScriptsAbsent(string project, string stage)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Inspect(string path) {
            if (!seen.Add(path)) return;
            var extension = Path.GetExtension(path);
            if (extension is ".scn" or ".res") throw new BridgeRequestException(BridgeErrors.StateConflict, path + ": binary scene/resource cannot be audited before loading; use text resources");
            if (extension is not (".tscn" or ".tres")) return;
            var text = File.ReadAllText(path);
            if (Regex.IsMatch(text, "(?m)^\\[(?:ext_resource|sub_resource)[^\\n]*type\\s*=\\s*\"(?:Script|GDScript|CSharpScript)\""))
                throw new BridgeRequestException(BridgeErrors.StateConflict, path + ": stage script resource is not allowed (refused before import/static initializers)");
            foreach (Match row in Regex.Matches(text, "(?m)^\\[ext_resource[^\\n]*path\\s*=\\s*\"([^\"]+)\"")) {
                var resource = row.Groups[1].Value;
                if (!resource.StartsWith("res://", StringComparison.Ordinal)) throw new BridgeRequestException(BridgeErrors.StateConflict, path + ": only project-local resource references are allowed");
                Inspect(Path.Combine(project, resource[6..]));
            }
        }
        Inspect(Path.Combine(project, stage[6..]));
    }
    // Autoloads run before the worker's _ready. Refuse them before launching Godot, not after an audit can already be too late.
    internal static void RequireNoAutoload(string project)
    {
        var section = "";
        foreach (var raw in File.ReadLines(Path.Combine(project, "project.godot"))) {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            if (section == "autoload") throw new BridgeRequestException(BridgeErrors.StateConflict, "project.godot/autoload: no autoload clocks or scripts are allowed in an owned stage project");
        }
    }
}
