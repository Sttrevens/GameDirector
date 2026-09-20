using GameDirector.Client;
using GameDirector.Core.Dsl;

namespace GameDirector.Workbench;

public sealed class FilmDocument
{
    public int Version { get; set; } = 1;
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
public sealed class DocumentConflict(FilmDocument current) : InvalidOperationException("这份作品已在另一处更新。你的修改仍保留，请比较后再保存。")
{
    public FilmDocument Current { get; } = current;
}
public sealed class RevisionRequired() : InvalidOperationException("请更新客户端：保存作品必须提供读取时的版本，才能保护其他窗口的修改。");

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
    private FilmDocument[] History(string projectId, string id)
    {
        var directory = DirectoryFor(projectId, id);
        var values = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal)
            .Select(DslJson.Load<FilmDocument>).ToArray() : Array.Empty<FilmDocument>();
        for (var i = 0; i < values.Length; i++)
        {
            var d = values[i];
            if (d.Version != 1 || d.ProjectId != projectId || d.Id != id || d.Revision != i + 1 || d.ParentRevision != i ||
                d.Film == null || d.ContentHash != FilmCompiler.Hash(d.Film))
                throw new IOException("Document history is inconsistent; original files have been preserved.");
        }
        // Older workspaces remain readable, without overwriting their original draft.
        var legacy = studio.StorePath("projects", projectId + ".draft");
        if (values.Length == 0 && id == "default" && File.Exists(legacy))
        {
            var film = DslJson.Load<FilmPlan>(legacy);
            return new[] { new FilmDocument { ProjectId = projectId, Id = id, Revision = 1, RequestId = "legacy-import",
                ContentHash = FilmCompiler.Hash(film), Film = film, UpdatedAt = File.GetLastWriteTimeUtc(legacy) } };
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
            return ids.Select(id => Read(projectId, id)).Where(d => d.Revision > 0)
                .OrderByDescending(d => d.UpdatedAt).Select(d => (object)new { d.Id, d.ProjectId, d.Revision, title = d.Film!.Title, d.UpdatedAt }).ToArray();
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
        var hash = FilmCompiler.Hash(film);
        lock (gate)
        {
            var history = History(projectId, id);
            var prior = history.FirstOrDefault(d => d.RequestId == input.RequestId);
            if (prior != null)
            {
                if (prior.ParentRevision != input.ExpectedRevision || prior.ContentHash != hash)
                    throw new InvalidOperationException("This save requestId already belongs to different inputs.");
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
            DslJson.SaveAtomic(Path.Combine(directory, next.Revision.ToString("D12") + ".json"), next);
            return Copy(next);
        }
    }
    public static FilmPlan ProductionFilm(FilmDocument document, bool preview)
    {
        var film = document.Film ?? throw new InvalidOperationException("Save a film document before production.");
        if (preview)
        {
            PreviewPolicy.Derive(film.Width, film.Height, out var width, out var height);
            film.Width = width; film.Height = height;
        }
        return film;
    }
}
