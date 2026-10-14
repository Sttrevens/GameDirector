using GameDirector.Client;

namespace GameDirector.Workbench;

public sealed record MediaImport(string Name, string Base64, string? Description = null);
public sealed record MediaDescription(string? Description);
public sealed record MediaAsset(string Id, string Name, string Extension, double Duration, long Bytes)
{
    /// <summary>Optional curator-authored description (what the sound is for).
    /// Decision matching judges names and descriptions; the bytes are content-hashed.</summary>
    public string? Description { get; set; }
}

public static class MediaLibrary
{
    public const int MaxDescriptionChars = 500;

    public static MediaAsset Describe(Studio studio, string project, string id, string? description)
    {
        Resolve(studio, project, id); // proves the stored bytes still match the id
        if (description != null && description.Length > MaxDescriptionChars) throw new ArgumentException("Description limit is " + MaxDescriptionChars + " characters.");
        var meta = studio.StorePath("media", project, id + ".json");
        var asset = StoredJson.Load<MediaAsset>(meta);
        asset.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        DslJson.SaveAtomic(meta, asset);
        return asset;
    }
    public static IEnumerable<MediaAsset> List(Studio studio, string project)
    {
        studio.Project(project);
        var root = studio.StorePath("media", project);
        return Directory.Exists(root) ? Directory.GetFiles(root, "*.json").Select(StoredJson.Load<MediaAsset>).ToArray() : Array.Empty<MediaAsset>();
    }
    public static async Task<MediaAsset> Import(Studio studio, string project, MediaImport input, CancellationToken ct)
    {
        studio.Project(project);
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200 || Path.GetFileName(input.Name) != input.Name) throw new ArgumentException("Use a filename without a path.");
        var extension = MediaFormats.RequireAudio(input.Name).Extension;
        if (input.Base64 == null) throw new InvalidInputFault("Base64 audio bytes are required.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(input.Base64); }
        catch (FormatException) { throw new InvalidInputFault("Audio bytes must be valid base64."); }
        if (bytes.Length == 0 || bytes.Length > MediaFormats.MaxImportBytes) throw new ArgumentException("Audio import limit is " + MediaFormats.MaxImportBytes / (1024 * 1024) + " MiB per file.");
        var id = "";
        var root = studio.StorePath("media", project); Directory.CreateDirectory(root);

        var temp = studio.StorePath("media", project, "import-" + Guid.NewGuid().ToString("N") + extension);
        await File.WriteAllBytesAsync(temp, bytes, ct);
        try
        {
            using var decoded = new NormalizedAudio();
            (string File, double Duration) sound;
            try { sound = await decoded.Read(temp, ct); }
            catch (Exception ex) when (ex is not (MediaFailureException or StateConflictException or EngineUnavailableException) && (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException))
            { throw new MediaFailureFault("Audio decoding failed; check the media tools and server log.", ex); }
            id = MediaTools.Hash(sound.File); extension = ".wav";
            var path = studio.StorePath("media", project, id + ".json");
            if (File.Exists(path)) { Resolve(studio, project, id); return StoredJson.Load<MediaAsset>(path); }
            var asset = new MediaAsset(id, input.Name, extension, sound.Duration, new FileInfo(sound.File).Length);
            if (input.Description != null && input.Description.Length > MaxDescriptionChars) throw new ArgumentException("Description limit is " + MaxDescriptionChars + " characters.");
            asset.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
            var destination = studio.StorePath("media", project, id + extension);
            try { File.Copy(sound.File, destination); } catch (IOException) when (File.Exists(destination) && MediaTools.Hash(destination) == id) { }
            DslJson.SaveAtomic(path, asset); return asset;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string Resolve(Studio studio, string project, string id)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id ?? "", "^[a-f0-9]{64}$")) throw new ArgumentException("Invalid media ID.");
        var meta = studio.StorePath("media", project, id + ".json");
        if (!File.Exists(meta)) throw new ArgumentException("Audio must be imported before use: " + id);
        var asset = StoredJson.Load<MediaAsset>(meta);
        if (MediaFormats.FindAudio(asset.Extension) == null) throw new StoreIntegrityFault("Invalid stored media type.");
        var file = studio.StorePath("media", project, id + asset.Extension);
        if (MediaTools.Hash(file) != id) throw new StoreIntegrityFault("Stored audio changed: " + id);
        return file;
    }
    public static void Validate(Studio studio, string project, FilmPlan film)
    {
        var assets = List(studio, project).ToDictionary(a => a.Id);
        foreach (var cue in film.Audio)
        {
            Resolve(studio, project, cue.MediaId);
            if (!assets.TryGetValue(cue.MediaId, out var asset) || !MediaFormats.AudioWindowFitsSource(cue.SourceStart,cue.Duration,asset.Duration,film.FrameRate))
                throw new ArgumentException("Audio cue exceeds imported source duration: " + cue.Id);
        }
    }
}
