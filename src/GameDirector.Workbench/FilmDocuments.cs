using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Core.Dsl;

namespace GameDirector.Workbench;

public sealed class FilmDocument
{
    /// <summary>Stored-record format: 2 hashes the stored film JSON tree with
    /// ContentIdentity; 1 (legacy, read-only) hashed the re-serialized POCO.</summary>
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;
    public string ProjectId { get; set; } = "";
    public string Id { get; set; } = "";
    public long Revision { get; set; }
    public long ParentRevision { get; set; }
    public string RequestId { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public FilmPlan? Film { get; set; }
}

public sealed record SaveFilmDocument(long? ExpectedRevision, string RequestId, FilmPlan Film);
public sealed record ProduceFilmDocument(string RequestId, long Revision, bool Preview = false);
public sealed record CheckFilmDocument(long Revision, bool Preview = false);
public sealed class DocumentConflict(FilmDocument current) : InvalidOperationException("This film was updated elsewhere. Your edits are kept; compare revisions before saving.")
{
    public FilmDocument Current { get; } = current;
}
public sealed class RevisionRequired() : InvalidOperationException("Update your client: saving a film requires the revision you read to protect edits from other windows.");

// The Studio owns the store lock. Each accepted revision is one atomic file:
// content, parent and retry receipt commit together, with no mutable head pointer.
public sealed class FilmDocuments(Studio studio)
{
    private readonly object gate = new();
    private string DirectoryFor(string projectId, string id)
    {
        studio.Project(projectId);
        if (!FilmCompiler.ValidId(id)) throw new ArgumentException("Invalid document ID.");
        return studio.StorePath("documents", projectId, id);
    }
    private static FilmDocument Copy(FilmDocument d) => DslJson.Deserialize<FilmDocument>(DslJson.Serialize(d));

    // Integrity is verified over the stored JSON tree, independent of the reader's
    // CLR shape, so additive DSL evolution can never lock out history.
    private static StoreIntegrityFault Inconsistent() => new("Document history is inconsistent; original files have been preserved.");
    private static FilmDocument ParseRevision(string path)
    {
        var node = StoredJson.Node(path) as JsonObject ?? throw Inconsistent();
        var versionToken = node["version"];
        int? version = versionToken is JsonValue versionValue && versionValue.TryGetValue<int>(out var parsed) ? parsed : null;
        var hash = node["contentHash"] is JsonValue hashValue && hashValue.TryGetValue<string>(out var stored) ? stored : null;
        var film = node["film"];
        switch (version)
        {
            case FilmDocument.CurrentVersion:
                if (film == null || hash != ContentIdentity.OfNode(film)) throw Inconsistent();
                break;
            case 1: // legacy format, read-only
                if (film == null || hash == null || !LegacyHashMatches(film, hash)) throw Inconsistent();
                break;
            default:
                throw new StoreIntegrityFault("Unsupported document version " + (version?.ToString() ?? versionToken?.ToJsonString() ?? "missing") + "; original files have been preserved.");
        }
        return StoredJson.Diagnose(() => node.Deserialize<FilmDocument>(DslJson.Options)) ?? throw Inconsistent();
    }
    // Format 1 hashed SHA-256 of the indented DslJson serialization. Indentation used
    // Environment.NewLine, so a store moved between Windows and macOS verifies against
    // both newline variants.
    private static string LegacyHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static bool LegacyHashMatches(JsonNode film, string contentHash)
    {
        var text = JsonSerializer.Serialize(film, DslJson.Options);
        var lf = text.Replace("\r\n", "\n");
        return contentHash == LegacyHash(text) || contentHash == LegacyHash(lf) || contentHash == LegacyHash(lf.Replace("\n", "\r\n"));
    }
    private FilmDocument[] History(string projectId, string id)
    {
        var directory = DirectoryFor(projectId, id);
        var values = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal)
            .Select(ParseRevision).ToArray() : Array.Empty<FilmDocument>();
        for (var i = 0; i < values.Length; i++)
        {
            var d = values[i];
            if (d.ProjectId != projectId || d.Id != id || d.Revision != i + 1 || d.ParentRevision != i || d.Film == null)
                throw Inconsistent();
        }
        // Older workspaces remain readable, without overwriting their original draft.
        var legacy = studio.StorePath("projects", projectId + ".draft");
        if (values.Length == 0 && id == "default" && File.Exists(legacy))
        {
            var film = StoredJson.Load<FilmPlan>(legacy);
            return new[] { new FilmDocument { Version = 1, ProjectId = projectId, Id = id, Revision = 1, RequestId = "legacy-import",
                ContentHash = LegacyHash(DslJson.Serialize(film)), Film = film, UpdatedAt = File.GetLastWriteTimeUtc(legacy) } };
        }
        return values;
    }
    public FilmDocument Read(string projectId, string id, long? revision = null)
    {
        lock (gate)
        {
            var history = History(projectId, id);
            if (revision != null) return Copy(history.FirstOrDefault(d => d.Revision == revision) ?? throw new KeyNotFoundException("Document revision not found."));
            return Copy(history.LastOrDefault() ?? new FilmDocument { ProjectId = projectId, Id = id });
        }
    }
    public object[] List(string projectId)
    {
        lock (gate)
        {
            studio.Project(projectId);
            var root = studio.StorePath("documents", projectId);
            var ids = (Directory.Exists(root) ? Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>() : Array.Empty<string>()).Append("default").Distinct();
            // One damaged document must not take the whole project list down: it is
            // returned as a visible damaged entry instead, without film content.
            var entries = new List<(DateTimeOffset at, object value)>();
            foreach (var id in ids)
            {
                FilmDocument d;
                try { d = Read(projectId, id); }
                catch (Exception ex) when (ex is StoreIntegrityFault or IOException or JsonException or InvalidOperationException or FormatException)
                {
                    var directory = Path.Combine(root, id);
                    var at = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max() : default;
                    entries.Add((at, new { id, damaged = true, error = ex.Message }));
                    continue;
                }
                if (d.Revision > 0) entries.Add((d.UpdatedAt, new { d.Id, d.ProjectId, d.Revision, title = d.Film!.Title, d.UpdatedAt }));
            }
            return entries.OrderByDescending(e => e.at).Select(e => e.value).ToArray();
        }
    }
    public FilmDocument Save(string projectId, string id, SaveFilmDocument input)
    {
        if (input.ExpectedRevision == null) throw new RevisionRequired();
        if (input.ExpectedRevision < 0 || !FilmCompiler.ValidId(input.RequestId)) throw new ArgumentException("A nonnegative revision and stable requestId are required.");
        // Preserve incomplete creative drafts; shootability is checked separately.
        // Reject unsupported schema/shape instead of silently losing fields.
        var film = DslJson.Deserialize<FilmPlan>(DslJson.Serialize(input.Film));
        if (film.Version != 1 || film.Title == null || film.Title.Length > 200 || film.Scenes == null || film.Scenes.Count > FilmLimits.MaxScenes ||
            film.Audio == null || film.Audio.Count > FilmLimits.MaxAudioCues || film.Subtitles == null || film.Subtitles.Count > FilmLimits.MaxSubtitles ||
            film.Scenes.Any(s => s == null || s.Performance == null || s.Performance.Count > FilmLimits.MaxPerformanceCues || s.Shots == null ||
                s.Shots.Any(x => x == null || x.Camera == null)) || film.Scenes.Sum(s => s.Shots.Count) > FilmLimits.MaxShots)
            throw new ArgumentException("Unsupported film document shape or size.");
        // ContentHash is integrity over the film tree exactly as it will be stored;
        // retry dedupe is semantic equality through the CURRENT model on BOTH sides
        // (never the stored hash: v1 priors use the legacy algorithm), so an additive
        // DSL field cannot refuse an identical retry across an upgrade.
        var hash = ContentIdentity.Of(film);
        lock (gate)
        {
            var history = History(projectId, id);
            var prior = history.FirstOrDefault(d => d.RequestId == input.RequestId);
            if (prior != null)
            {
                if (prior.ParentRevision != input.ExpectedRevision || hash != ContentIdentity.Of(prior.Film))
                    throw new RequestConflictFault("This save requestId already belongs to different inputs.");
                return Copy(prior);
            }
            var current = history.LastOrDefault() ?? new FilmDocument { ProjectId = projectId, Id = id };
            if (current.Revision != input.ExpectedRevision) throw new DocumentConflict(Copy(current));
            var directory = DirectoryFor(projectId, id);
            Directory.CreateDirectory(directory);
            // Materialize an old draft once, leaving the source file untouched.
            if (current.Revision == 1 && !File.Exists(Path.Combine(directory, "000000000001.json")))
                DslJson.SaveAtomic(Path.Combine(directory, "000000000001.json"), current);
            var next = new FilmDocument { ProjectId = projectId, Id = id, Revision = checked(current.Revision + 1), ParentRevision = current.Revision,
                RequestId = input.RequestId, Film = film, ContentHash = hash, UpdatedAt = DateTimeOffset.UtcNow };
            // The hash binds the film subtree exactly as it will be stored.
            DslJson.SaveAtomic(Path.Combine(directory, next.Revision.ToString("D12") + ".json"), next);
            return Copy(next);
        }
    }
    public static FilmPlan ProductionFilm(FilmDocument document, bool preview)
    {
        var film = document.Film ?? throw new StateConflictFault("Save a film document before production.", retryable: false);
        if (preview)
        {
            PreviewPolicy.Derive(film.Width, film.Height, out var width, out var height);
            film.Width = width; film.Height = height;
        }
        return film;
    }
}
