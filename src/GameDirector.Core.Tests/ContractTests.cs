using GameDirector.Client;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

public class DirectorContractTests
{
    [Fact] public void CaptureGeometryBoundsAreSingleSourced()
    {
        Assert.Null(CaptureContract.CheckGeometry(FilmDefaults.FrameRate, FilmDefaults.Width, FilmDefaults.Height));
        Assert.Null(CaptureContract.CheckGeometry(60, 3840, 2160));
        Assert.NotNull(CaptureContract.CheckGeometry(0, 1280, 720));
        Assert.NotNull(CaptureContract.CheckGeometry(61, 1280, 720));
        Assert.NotNull(CaptureContract.CheckGeometry(24, 63, 720));
        Assert.NotNull(CaptureContract.CheckGeometry(24, 1280, 721)); // odd
        Assert.NotNull(CaptureContract.CheckGeometry(24, 3842, 2160));
        Assert.Throws<ArgumentException>(() => CaptureContract.RequireGeometry(24, 1280, 719));
    }

    [Fact] public void BridgeEndpointsDeriveFromPorts()
    {
        Assert.Equal(BridgeDefaults.LoopbackEndpoint(BridgeDefaults.UnityPort), BridgeDefaults.UnityEndpoint);
        Assert.Equal(BridgeDefaults.LoopbackEndpoint(BridgeDefaults.ThreePort), BridgeDefaults.ThreeEndpoint);
        Assert.Equal(BridgeDefaults.LoopbackEndpoint(BridgeDefaults.WorkbenchPort), BridgeDefaults.WorkbenchEndpoint);
        Assert.StartsWith("http://" + BridgeDefaults.LoopbackHost + ":", BridgeDefaults.UnityEndpoint);
    }

    [Fact] public void PreviewDerivesHalfEvenFloorClamped()
    {
        PreviewPolicy.Derive(1280, 720, out var w, out var h);
        Assert.Equal((640, 360), (w, h));
        PreviewPolicy.Derive(1278, 718, out w, out h); // half of odd source rounds to even
        Assert.Equal((640, 360), (w, h));
        PreviewPolicy.Derive(96, 96, out w, out h); // never below the capture floor
        Assert.Equal((CaptureContract.MinDimension, CaptureContract.MinDimension), (w, h));
    }

    [Fact] public void FilmLimitsFeedDerivedCeilings()
    {
        Assert.Equal(5 * 1024 * 1024, FilmLimits.MaxAudioImportBytes);
        Assert.True(FilmLimits.MaxScriptBytes < FilmLimits.MaxAudioImportBytes);
        Assert.True(FilmLimits.MaxOutputSeconds <= CaptureContract.MaxTakeSeconds);
    }
}

public class ShotVocabularyTests
{
    [Fact] public void BuiltInShotsMatchCompilerExpectations()
    {
        // The compiler requires a destination anchor exactly for the moving
        // shots that need one; the registry is the only statement of that rule.
        Assert.Equal(new[] { "lockoff", "dolly", "crane", "orbit", "tracking" }, ShotVocabulary.ShotIds);
        Assert.True(ShotVocabulary.RequiresTarget("dolly"));
        Assert.True(ShotVocabulary.RequiresTarget("crane"));
        Assert.False(ShotVocabulary.RequiresTarget("lockoff"));
        Assert.False(ShotVocabulary.RequiresTarget("orbit"));
        Assert.False(ShotVocabulary.RequiresTarget("tracking"));
        Assert.Null(ShotVocabulary.RequiresTarget("game-custom-move")); // manifest-declared custom
    }

    [Fact] public void FrameCoverageIsOrderedAndDefaulted()
    {
        Assert.Equal(new[] { "extreme-closeup", "closeup", "medium", "full", "wide" }, ShotVocabulary.FrameIds);
        Assert.True(ShotVocabulary.Coverage("extreme-closeup") > ShotVocabulary.Coverage("closeup"));
        Assert.True(ShotVocabulary.Coverage("closeup") > ShotVocabulary.Coverage("medium"));
        Assert.True(ShotVocabulary.Coverage("medium") > ShotVocabulary.Coverage("full"));
        Assert.True(ShotVocabulary.Coverage("full") > ShotVocabulary.Coverage("wide"));
        Assert.Equal(ShotVocabulary.Coverage("wide"), ShotVocabulary.Coverage("unregistered"));
        Assert.Equal(ShotVocabulary.DefaultShotId, ShotVocabulary.FindShot(ShotVocabulary.DefaultShotId).Id);
        Assert.Equal(ShotVocabulary.DefaultFrameId, ShotVocabulary.FindFrame(ShotVocabulary.DefaultFrameId).Id);
    }

    [Fact] public void EasesAreExhaustiveForTheRig()
    {
        Assert.Equal(new[] { "linear", "in", "out", "inOut" }, ShotVocabulary.Eases);
        Assert.True(ShotVocabulary.ValidEase("inOut"));
        Assert.False(ShotVocabulary.ValidEase("bounce"));
    }

    [Fact] public void CompilerTakesTargetRequirementFromVocabulary()
    {
        // A manifest-declared custom shot type compiles without a destination
        // anchor: its semantics belong to the engine that registered it.
        var manifest = TestAssets.Manifest();
        manifest.ShotTypes.Add("game-move");
        var asset = TestAssets.ValidTimeline();
        asset.Cues[0].Shot.Type = "game-move";
        asset.Cues[0].Shot.To = null;
        Assert.False(TimelineCompiler.Compile(asset, manifest).HasErrors);
        // A built-in moving shot without "to" still fails, from the same table.
        asset.Cues[0].Shot.Type = "crane";
        Assert.True(TimelineCompiler.Compile(asset, manifest).HasErrors);
        // Unknown eases fail against the vocabulary, not a local list.
        asset.Cues[0].Shot.Type = "lockoff";
        asset.Cues[0].Shot.Ease = "bounce";
        Assert.True(TimelineCompiler.Compile(asset, manifest).HasErrors);
    }
}

public class SubtitleRendererTests
{
    private static string Srt(params FilmSubtitle[] entries) => SubtitleRenderer.ToSrt(entries);

    [Fact] public void TimestampsAreSrtShaped()
    {
        Assert.Equal("00:00:01,500", SubtitleRenderer.FormatTimestamp(1.5));
        Assert.Equal("01:02:03,250", SubtitleRenderer.FormatTimestamp(3723.25));
    }

    [Fact] public void MarkupAndControlCharactersAreLiteral()
    {
        var srt = Srt(new FilmSubtitle { Start = 0, End = 1, Text = "<i>{\\an8}注入</i> & \"引号\" '撇号'\u0007" });
        Assert.Contains("&lt;i&gt;(\\an8)注入&lt;/i&gt; &amp; \"引号\" '撇号'", srt);
        Assert.DoesNotContain("<i>", srt);
        Assert.DoesNotContain("{", srt);
        // Culture-sensitive substring search ignores control characters;
        // assert on code points instead.
        Assert.DoesNotContain(srt, c => char.IsControl(c) && c != '\n');
    }

    [Fact] public void BlankLinesCannotTerminateAnEntryEarly()
    {
        var srt = Srt(new FilmSubtitle { Start = 0, End = 1, Text = "第一行\r\n\n\n第二行" });
        Assert.Contains("第一行\n第二行", srt);
        // One caption = one numbered block: exactly one "-->" line.
        Assert.Equal(1, srt.Split("-->").Length - 1);
    }
}

public class MediaFormatsTests
{
    [Fact] public void ImportAndNormalizeShareOneFormatTable()
    {
        foreach (var ext in new[] { ".wav", ".mp3", ".m4a", ".ogg", ".flac" })
        {
            var format = MediaFormats.RequireAudio("sound" + ext);
            Assert.Equal(ext, format.Extension);
            Assert.False(string.IsNullOrWhiteSpace(format.Demuxer));
        }
        Assert.Null(MediaFormats.FindAudio("clip.mp4"));
        Assert.Throws<ArgumentException>(() => MediaFormats.RequireAudio("notes.txt"));
        // m4a hardening: external references must stay disabled.
        Assert.Contains("-enable_drefs", MediaFormats.RequireAudio("a.m4a").InputHardening);
        Assert.Equal(FilmLimits.MaxAudioImportBytes, MediaFormats.MaxImportBytes);
    }
}

public class MediaToolResolutionTests
{
    [Fact] public void ResolutionPrefersEnvironmentOverride()
    {
        var tool = MediaTools.Resolve("ffmpeg");
        var overridePath = Environment.GetEnvironmentVariable("GAMEDIRECTOR_FFMPEG");
        if (overridePath != null && File.Exists(overridePath))
            Assert.Equal(overridePath, tool.Path);
        Assert.Equal("ffmpeg", tool.Name);
        Assert.False(string.IsNullOrWhiteSpace(tool.Source));
    }
}
