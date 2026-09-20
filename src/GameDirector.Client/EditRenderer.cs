using System.Text;
using System.Text.Json.Nodes;

namespace GameDirector.Client;

public sealed class EditPlan
{
    public int Version { get; set; }=1;
    public int FrameRate { get; set; }=GameDirector.Core.Dsl.FilmDefaults.FrameRate;
    public int Width { get; set; }=GameDirector.Core.Dsl.FilmDefaults.Width;
    public int Height { get; set; }=GameDirector.Core.Dsl.FilmDefaults.Height;
    public Dictionary<string,string> Sources { get; set; }=new();
    public List<EditRange> Ranges { get; set; }=new();
    public string? Grade { get; set; }
    public string? Music { get; set; }
    public double MusicVolume { get; set; }=.8;
    public List<EditAudioCue> Audio { get; set; } = new();
    public string? Subtitles { get; set; }
}
public sealed class EditAudioCue
{
    public string Source { get; set; } = "";
    public double At { get; set; }
    public double SourceStart { get; set; }
    public double Duration { get; set; }
    public double Volume { get; set; } = 1;
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
}
public sealed class EditRange
{
    public string Source { get; set; }="";
    public double Start { get; set; }
    public double End { get; set; }
    public string Reason { get; set; }="";
    public string Beat { get; set; }="";
}

/// <summary>Game-neutral, inspectable EDL. Decode/grade each selected range once,
/// losslessly concatenate, then mix soundtrack and apply final typography.</summary>
public static class EditRenderer
{
    public static void Validate(EditPlan plan,IReadOnlyDictionary<string,double> durations)
    {
        if(plan.Version!=1 || GameDirector.Core.Dsl.CaptureContract.CheckGeometry(plan.FrameRate,plan.Width,plan.Height)!=null)
            throw new ArgumentException("invalid edit version, frame rate or dimensions");
        if(plan.Ranges==null || plan.Ranges.Count==0)throw new ArgumentException("edit requires ranges");
        foreach(var r in plan.Ranges) {
            if(r==null || !durations.TryGetValue(r.Source,out var duration))throw new ArgumentException("unknown source");
            if(!double.IsFinite(r.Start) || !double.IsFinite(r.End) || r.Start<0 || r.End<=r.Start || r.End>duration+.00001)
                throw new ArgumentException("range is outside source duration");
            if(Math.Abs(r.Start*plan.FrameRate-Math.Round(r.Start*plan.FrameRate))>.0001 || Math.Abs(r.End*plan.FrameRate-Math.Round(r.End*plan.FrameRate))>.0001)
                throw new ArgumentException("edit boundaries must align to output frames");
            if(string.IsNullOrWhiteSpace(r.Reason))throw new ArgumentException("each cut requires an editorial reason");
        }
        if(!double.IsFinite(plan.MusicVolume) || plan.MusicVolume<0 || plan.MusicVolume>GameDirector.Core.Dsl.FilmLimits.MaxVolume)throw new ArgumentException("invalid music volume");
    }
    public static async Task<string> Render(string planPath,string output,CancellationToken ct=default,MediaProfile? profile=null)
    {
        profile??=MediaProfile.Default;
        planPath=Path.GetFullPath(planPath);output=OutputPolicy.RequireOutput(output);
        if(Directory.Exists(output))throw new IOException("edit output exists; choose a new version directory");
        var plan=DslJson.Load<EditPlan>(planPath);var root=Path.GetDirectoryName(planPath)!;
        await MediaTools.EnsureEncoding(profile,plan.Subtitles!=null,ct);
        var sources=new Dictionary<string,string>();var durations=new Dictionary<string,double>();var audio=new Dictionary<string,bool>();
        var hashes=new Dictionary<string,string>();var receipts=new Dictionary<string,string>();
        if(plan.Sources==null)throw new ArgumentException("sources required");
        foreach(var pair in plan.Sources) {
            var file=Path.GetFullPath(Path.Combine(root,pair.Value));sources[pair.Key]=file;
            var probe=await MediaTools.Probe(file,ct);
            durations[pair.Key]=double.Parse(probe["format"]!["duration"]!.GetValue<string>(),System.Globalization.CultureInfo.InvariantCulture);
            audio[pair.Key]=probe["streams"]!.AsArray().Any(x=>x?["codec_type"]?.GetValue<string>()=="audio");
            hashes[pair.Key]=MediaTools.Hash(file);
            var receiptPath=Path.Combine(Path.GetDirectoryName(file)!,"take.json");
            if(File.Exists(receiptPath)) {
                var receipt=JsonNode.Parse(File.ReadAllText(receiptPath))!;
                if(receipt["state"]?.GetValue<string>()!="Verified" || receipt["videoSha256"]?.GetValue<string>()!=hashes[pair.Key])
                    throw new InvalidOperationException("take changed or was not verified: "+file);
                receipts[pair.Key]=receipt["takeId"]!.GetValue<string>();
            }
        }
        Validate(plan,durations);
        string? music=plan.Music==null?null:Path.GetFullPath(Path.Combine(root,plan.Music));
        string? subtitles=plan.Subtitles==null?null:Path.GetFullPath(Path.Combine(root,plan.Subtitles));
        if(music!=null && !File.Exists(music) || subtitles!=null && !File.Exists(subtitles))throw new IOException("missing music/subtitle asset");
        using var decodedAudio = new NormalizedAudio();
        var soundFiles = new List<string>(); var soundHashes = new List<string>();
        var totalDuration = plan.Ranges.Sum(r => r.End - r.Start);
        if (plan.Audio == null || plan.Audio.Count > GameDirector.Core.Dsl.FilmLimits.MaxAudioCues) throw new ArgumentException("At most " + GameDirector.Core.Dsl.FilmLimits.MaxAudioCues + " audio cues are supported.");
        foreach (var cue in plan.Audio)
        {
            if (cue == null || !double.IsFinite(cue.At) || !double.IsFinite(cue.SourceStart) || !double.IsFinite(cue.Duration) || !double.IsFinite(cue.Volume) ||
                !double.IsFinite(cue.FadeIn) || !double.IsFinite(cue.FadeOut) || cue.At < 0 || cue.SourceStart < 0 || cue.Duration <= 0 ||
                cue.At + cue.Duration > totalDuration + .00001 || cue.Volume < 0 || cue.Volume > GameDirector.Core.Dsl.FilmLimits.MaxVolume || cue.FadeIn < 0 || cue.FadeOut < 0 || cue.FadeIn + cue.FadeOut > cue.Duration)
                throw new ArgumentException("Invalid edited-film audio cue.");
            var file = Path.GetFullPath(Path.Combine(root, cue.Source));
            var sound = await decodedAudio.Read(file, ct, profile);
            if (cue.SourceStart + cue.Duration > sound.Duration + .00001) throw new ArgumentException("Audio cue exceeds decoded source samples.");
            soundFiles.Add(sound.File); soundHashes.Add(MediaTools.Hash(file));
        }
        Directory.CreateDirectory(output);Directory.CreateDirectory(OutputPolicy.Child(output,"segments"));
        DslJson.Save(OutputPolicy.Child(output,"edit.json"),plan);
        try {
            int totalFrames=0;var concat=new StringBuilder();int index=0;
            foreach(var range in plan.Ranges) {
                var frames=(int)Math.Round((range.End-range.Start)*plan.FrameRate);totalFrames+=frames;
                var duration=(double)frames/plan.FrameRate;
                var segment=OutputPolicy.Child(output,"segments",$"{index++:D3}.mov");
                // Frame pacing belongs to the output muxer. The fps filter can
                // discard the final decoded frame at EOF (including exact-CFR
                // takes); an explicit output clock preserves that last frame.
                var vf=$"setpts=PTS-STARTPTS,scale={plan.Width}:{plan.Height}:force_original_aspect_ratio=decrease,pad={plan.Width}:{plan.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1";
                if(!string.IsNullOrWhiteSpace(plan.Grade))vf+=","+plan.Grade;
                var args=new List<string>{"-v","error","-n","-ss",MediaTools.Number(range.Start),"-i",sources[range.Source]};
                if(!audio[range.Source])args.AddRange(new[]{"-f","lavfi","-i","anullsrc=r="+profile.AudioSampleRate+":cl=stereo"});
                var segmentFade=profile.SegmentFadeSeconds;
                args.AddRange(new[]{"-t",MediaTools.Number(duration),"-map","0:v:0","-map",audio[range.Source]?"0:a:0":"1:a:0","-vf",vf,"-r",plan.FrameRate.ToString(),"-fps_mode","cfr","-frames:v",frames.ToString(),"-af",$"aresample={profile.AudioSampleRate},afade=t=in:st=0:d={MediaTools.Number(segmentFade)},afade=t=out:st={MediaTools.Number(Math.Max(0,duration-segmentFade))}:d={MediaTools.Number(segmentFade)}","-c:v",profile.VideoCodec,"-preset",profile.Preset,"-crf",profile.Crf.ToString(),"-pix_fmt",profile.PixelFormat,"-c:a",profile.SegmentAudioCodec,"-ar",profile.AudioSampleRate.ToString(),"-ac",profile.AudioChannels.ToString(),"-video_track_timescale",(plan.FrameRate*1000).ToString(),segment});
                await MediaTools.Run("ffmpeg",args,ct);
                await MediaTools.Verify(segment,frames,plan.FrameRate,plan.Width,plan.Height,ct);
                // Generated simple relative paths avoid concat quoting/platform traps.
                concat.AppendLine($"file 'segments/{index-1:D3}.mov'");
                concat.AppendLine("duration "+MediaTools.Number(duration));
            }
            var list=OutputPolicy.Child(output,"concat.txt");File.WriteAllText(list,concat.ToString());
            var picture=OutputPolicy.Child(output,"assembly.mov");
            await MediaTools.Run("ffmpeg",new[]{"-v","error","-n","-f","concat","-safe","1","-i",list,"-c","copy",picture},ct);
            var final=OutputPolicy.Child(output,"final.mp4");var total=(double)totalFrames/plan.FrameRate;
            var finish=new List<string>{"-v","error","-n","-i",picture};
            var filters = new List<string>(); var mix = new List<string> { "[0:a]" }; var inputIndex = 1;
            if (music != null)
            {
                finish.AddRange(new[]{"-i",music});
                filters.Add($"[{inputIndex++}:a]volume={MediaTools.Number(plan.MusicVolume)},apad,atrim=0:{MediaTools.Number(total)},afade=t=in:d={MediaTools.Number(profile.MusicFadeInSeconds)},afade=t=out:st={MediaTools.Number(Math.Max(0,total-profile.MusicFadeOutSeconds))}:d={MediaTools.Number(profile.MusicFadeOutSeconds)}[m]"); mix.Add("[m]");
            }
            for (var n = 0; n < plan.Audio.Count; n++)
            {
                var cue = plan.Audio[n]; finish.AddRange(new[]{"-i",soundFiles[n]});
                var label = "sound" + n;
                filters.Add($"[{inputIndex++}:a]atrim=start={MediaTools.Number(cue.SourceStart)}:duration={MediaTools.Number(cue.Duration)},asetpts=PTS-STARTPTS,aresample={profile.AudioSampleRate},aformat=channel_layouts=stereo,volume={MediaTools.Number(cue.Volume)},afade=t=in:d={MediaTools.Number(cue.FadeIn)},afade=t=out:st={MediaTools.Number(cue.Duration-cue.FadeOut)}:d={MediaTools.Number(cue.FadeOut)},adelay={Math.Round(cue.At*profile.AudioSampleRate)}S:all=1[{label}]"); mix.Add("[" + label + "]");
            }
            if (filters.Count > 0)
            {
                filters.Add(string.Join("",mix)+$"amix=inputs={mix.Count}:duration=first:normalize=0,alimiter=limit={MediaTools.Number(profile.LimiterLevel)}:level=0:latency=1[a]");
                finish.AddRange(new[]{"-filter_complex",string.Join(";",filters),"-map","0:v:0","-map","[a]"});
            }
            else finish.AddRange(new[]{"-map","0:v:0","-map","0:a:0"});
            if(subtitles!=null){
                // Copy to an ASCII basename; filter quoting never consumes user paths.
                var localSub=OutputPolicy.Child(output,"titles"+Path.GetExtension(subtitles));File.Copy(subtitles,localSub);
                var escaped=MediaTools.FilterPath(localSub);
                var font=profile.SubtitleFont;
                var fonts=MediaTools.FilterPath(Path.GetDirectoryName(font.Locate())!);
                finish.AddRange(new[]{"-vf","subtitles='"+escaped+"':fontsdir='"+fonts+"':force_style='FontName="+font.FamilyName+"'","-c:v",profile.VideoCodec,"-preset",profile.Preset,"-crf",profile.Crf.ToString(),"-pix_fmt",profile.PixelFormat});
            }else finish.AddRange(new[]{"-c:v","copy"});
            if(subtitles!=null)finish.AddRange(new[]{"-r",plan.FrameRate.ToString(),"-fps_mode","cfr","-frames:v",totalFrames.ToString()});
            finish.AddRange(new[]{"-c:a",profile.FinalAudioCodec,"-b:a",profile.FinalAudioBitrate,"-t",MediaTools.Number(total),"-movflags",profile.MuxerFlags,final});
            await MediaTools.Run("ffmpeg",finish,ct);
            await MediaTools.Verify(final,totalFrames,plan.FrameRate,plan.Width,plan.Height,ct);
            var review=OutputPolicy.Child(output,"contact-sheet.jpg");
            var thumbnails=Math.Min(profile.ThumbnailMax,totalFrames); var columns=Math.Min(profile.ThumbnailColumns,thumbnails);var rows=(int)Math.Ceiling(thumbnails/(double)columns);
            await MediaTools.Run("ffmpeg",new[]{"-v","error","-n","-i",final,"-vf",$"fps={MediaTools.Number(thumbnails/total)},scale={profile.ThumbnailWidth}:-2:out_range=full,tile={columns}x{rows}","-frames:v","1","-pix_fmt","yuvj420p",review},ct);
            DslJson.Save(OutputPolicy.Child(output,"delivery.json"),new{version=1,state="Verified",video="final.mp4",videoSha256=MediaTools.Hash(final),sourceHashes=hashes,sourceTakes=receipts,
                frames=totalFrames,frameRate=plan.FrameRate,duration=total,musicSha256=music==null?null:MediaTools.Hash(music),subtitleSha256=subtitles==null?null:MediaTools.Hash(subtitles),
                subtitleFontSha256=subtitles==null?null:MediaTools.Hash(profile.SubtitleFont.Locate()),audioSourceHashes=soundHashes,editSha256=MediaTools.Hash(OutputPolicy.Child(output,"edit.json")),visualAcceptance="requires review of final.mp4 and contact-sheet.jpg"});
            return final;
        }catch(Exception ex){DslJson.Save(OutputPolicy.Child(output,"failed.json"),new{state="Failed",error=ex.Message});throw;}
    }
}
