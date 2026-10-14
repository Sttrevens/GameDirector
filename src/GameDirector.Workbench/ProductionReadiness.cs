using GameDirector.Client;

namespace GameDirector.Workbench;

// Codes and arguments are additive; existing clients keep the English fallback.
public sealed record ReadinessCheck(string Id, bool Ready, string Message, string Action, string ActionId = "",
    string Code = "", string ActionCode = "", object? MessageArgs = null);
public sealed record ReadinessReport(bool Ready, IReadOnlyList<ReadinessCheck> Checks);

public static class ProductionReadiness
{
    public static async Task<ReadinessReport> Check(Studio studio, string projectId, FilmPlan film, CancellationToken ct)
    {
        var checks = new List<ReadinessCheck>();
        try
        {
            await studio.Validate(new ProductionRequest { ProjectId = projectId, Film = film }, ct);
            checks.Add(new("performance", true, "Assets and stage are ready.", "", Code: "performance_ready"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var fault = Faults.Of(ex);
            checks.Add(new("performance", false, fault.Message,
                "Prepare an offline stage in Unity and enter Play Mode. Check the reported roles, actions and camera anchors.",
                Code: fault.MessageCode ?? fault.Code, ActionCode: "performance_action"));
        }
        try
        {
            await MediaTools.EnsureEncoding(film.Subtitles.Count > 0, ct);
            checks.Add(new("media", true, film.Subtitles.Count > 0 ? "Film encoding and captions are ready." : "Film encoding is ready.", "",
                Code: film.Subtitles.Count > 0 ? "encoding_captions_ready" : "encoding_ready"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var tools = string.Join("; ", MediaTools.Resolutions().Select(t => t.Name + " → " + t.Path + " (" + t.Source + ")"));
            var installable = MediaToolInstaller.Options().Count > 0;
            checks.Add(new("media", false, "Film encoding tools are unavailable: " + ex.Message,
                (installable ? "Install compatible tools here, or configure FFmpeg and ffprobe manually." : "Configure compatible FFmpeg and ffprobe, then check again.") + " Current resolution: " + tools,
                installable ? "install-media" : "", "media_unavailable", installable ? "media_setup_install" : "media_setup", new { detail = ex.Message, tools }));
        }
        return new(checks.All(c => c.Ready), checks);
    }
}
