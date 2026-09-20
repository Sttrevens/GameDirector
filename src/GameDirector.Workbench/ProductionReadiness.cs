using GameDirector.Client;

namespace GameDirector.Workbench;

public sealed record ReadinessCheck(string Id, bool Ready, string Message, string Action, string ActionId = "");
public sealed record ReadinessReport(bool Ready, IReadOnlyList<ReadinessCheck> Checks);

public static class ProductionReadiness
{
    public static async Task<ReadinessReport> Check(Studio studio, string projectId, FilmPlan film, CancellationToken ct)
    {
        var checks = new List<ReadinessCheck>();
        try
        {
            await studio.Validate(new ProductionRequest { ProjectId = projectId, Film = film }, ct);
            checks.Add(new("performance", true, "角色、镜头、声音素材与拍摄环境可用。", ""));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        { checks.Add(new("performance", false, ex.Message, "在 Unity 准备离线拍摄场景并进入播放；检查提示中的角色、动作或机位。")); }
        try
        {
            await MediaTools.EnsureEncoding(film.Subtitles.Count > 0, ct);
            checks.Add(new("media", true, "影片编码" + (film.Subtitles.Count > 0 ? "与字幕输出" : "") + "可用。", ""));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var tools = string.Join("；", MediaTools.Resolutions().Select(t => t.Name + " → " + t.Path + "（" + t.Source + "）"));
            var installable = MediaToolInstaller.Options().Count > 0;
            checks.Add(new("media", false, "影片编码组件不可用：" + ex.Message,
                (installable ? "可一键安装兼容组件，或" : "") + "在工作台运行组件中配置兼容的 FFmpeg 和 ffprobe，再重新检查。当前解析：" + tools,
                installable ? "install-media" : ""));
        }
        return new(checks.All(c => c.Ready), checks);
    }
}
