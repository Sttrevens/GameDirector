using System.Reflection;
namespace GameDirector.Client;
public static class DirectorProduct {
    public static string Guide() {
        using var stream=typeof(DirectorProduct).Assembly.GetManifestResourceStream("GameDirector.DirectingGuide")!;
        return new StreamReader(stream).ReadToEnd();
    }
    public static async Task<object> Doctor(string? endpoint=null,CancellationToken ct=default) {
        var checks=new List<object>();bool ok=true;
        var tools=MediaTools.Resolutions().Select(tool => new { name=tool.Name, path=tool.Path, source=tool.Source }).ToArray();
        try{await MediaTools.EnsureEncoding(MediaProfile.Default,true,ct);checks.Add(new{name="media",ok=true,detail=MediaProfile.Default.VideoCodec+", subtitles and ffprobe available"});}
        catch(Exception ex){ok=false;checks.Add(new{name="media",ok=false,detail=ex.Message});}
        if(endpoint!=null) {
            try{using var c=new DirectorBridgeClient(endpoint);var m=await c.GetManifestAsync(ct);
                var configured=m.Roles?.Count>0 && m.ShotTypes?.Count>0 && m.Capabilities?.GetValueOrDefault(Core.Dsl.CapabilityKeys.DirectorMode)==Core.Dsl.DirectorModes.OfflineSandbox;
                if(!configured)ok=false;
                checks.Add(new{name="bridge",ok=configured,game=m.Game,detail="Live manifest presence does not establish scene/visual compatibility"});}
            catch(Exception ex){ok=false;checks.Add(new{name="bridge",ok=false,detail=ex.Message});}
        }
        return new{ok,version=typeof(DirectorProduct).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            platform=System.Runtime.InteropServices.RuntimeInformation.OSDescription,tools,checks,
            next="Read gd guide. Configure an offline scene, record a scout and inspect its output."};
    }
}
