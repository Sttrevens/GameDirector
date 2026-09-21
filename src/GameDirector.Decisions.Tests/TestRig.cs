using GameDirector.Client;
using GameDirector.Core.Dsl;
using GameDirector.Decisions;

namespace GameDirector.Decisions.Tests;

internal static class TestRig
{
    public static CapabilityManifest Manifest(string sourceRoot) => new()
    {
        Game = "TestGame",
        GameVersion = "0",
        ShotTypes = new List<string> { "lockoff", "dolly", "orbit", "tracking", "crane" },
        FrameTypes = new List<string> { "extreme-closeup", "closeup", "medium", "full", "wide" },
        Roles = new List<RoleDescriptor>
        {
            new() { Id = "hero", Kind = "player", DefaultActor = "streamer", PresentAtStart = true },
            new() { Id = "bigguai", Kind = "monster", DefaultActor = "bigguai", PresentAtStart = false },
        },
        Actors = new List<ActorDescriptor>
        {
            // Extra TakeNN clips give tests enough distinct actor/clip pairs to
            // build valid catalogs past the bounded candidate set.
            new() { Id = "streamer", Clips = new List<string> { "Idle", "Walk", "Cheer" }.Concat(Enumerable.Range(1, 25).Select(i => "Take" + i.ToString("00"))).ToList() },
            new() { Id = "bigguai", Clips = new List<string> { "Idle", "RageExpose" } },
        },
        Locations = new List<LocationDescriptor>
        {
            new() { Id = "loc_gate", Space = "test", Position = new float[] { 0, 0, 0 } },
            new() { Id = "loc_stage", Space = "test", Position = new float[] { 10, 0, 0 } },
            new() { Id = "cam_north", Space = "test", Position = new float[] { 0, 3, -8 } },
        },
        Audio = new List<AudioDescriptor> { new() { Id = "bgm_dark", Kind = "bgm" } },
        Capabilities = new Dictionary<string, string>
        {
            [CapabilityKeys.DirectorMode] = DirectorModes.OfflineSandbox,
            [CapabilityKeys.PresentationSourceFingerprint] = "test-fingerprint",
            [CapabilityKeys.ProjectSourceRoot] = sourceRoot,
        },
    };

    /// <summary>A two-shot film with audio and subtitles that compiles against Manifest.</summary>
    public static FilmPlan ValidFilm() => new()
    {
        Title = "Test film",
        Scenes = new List<FilmScene>
        {
            new()
            {
                Id = "scene-01",
                Performance = new List<Cue> { new() { T = 0, Type = CueTypes.ActorAnim, Role = "hero", Clip = "Idle" } },
                Shots = new List<FilmShot>
                {
                    new() { Id = "shot-01", Start = 0, End = 3, Purpose = "establish the space", Camera = new ShotSpec { Type = "lockoff", Subject = "hero", Frame = "full", From = "cam_north" } },
                    new() { Id = "shot-02", Start = 3, End = 6, Purpose = "hold on the hero", Camera = new ShotSpec { Type = "lockoff", Subject = "hero", Frame = "medium", From = "cam_north" } },
                },
            },
        },
        Audio = new List<FilmAudioCue> { new() { Id = "cue-01", MediaId = new string('a', 64), Bus = "sfx", At = 0, Duration = 2, SourceStart = 0, Volume = 1 } },
        Subtitles = new List<FilmSubtitle> { new() { Start = 0.5, End = 2, Text = "hello" } },
    };

    public static PerformanceCatalog Catalog(string manifestHash) => new()
    {
        ManifestSha256 = manifestHash,
        Performances = new List<PerformanceEntry>
        {
            new()
            {
                Actor = "streamer", Clip = "Cheer", Meaning = "release of tension after success",
                DurationSeconds = 2.5, UsefulFor = new List<string> { "celebration", "relief" },
                Evidence = new List<PerformanceEvidence> { new() { Kind = "observation", Source = "scout-take-1", Start = 0, End = 2.5 } },
            },
            // No claims and no evidence: a scaffold placeholder, never eligible.
            new() { Actor = "streamer", Clip = "Idle" },
        },
    };

    /// <summary>Length-prefixed id of the Cheer catalog entry: actor/clip pairs
    /// may contain the separator, so ids are unambiguous by construction.</summary>
    public const string CheerCandidateId = "8:streamer/Cheer";

    public static DecisionRequest SampleRequest() => new(
        "typesafe/jev-1.13",
        new Dictionary<string, string> { ["intent"] = "pick one" },
        new[]
        {
            new DecisionQuestion("cam", DecisionQuestionKind.Choice, "Which camera?", new[]
            {
                new DecisionCandidate("cand-a", "Static wide from the north"),
                new DecisionCandidate("cand-b", "Slow dolly toward the gate"),
                new DecisionCandidate(DecisionPolicyBounds.NoneCandidateId, DecisionPolicyBounds.NoneCandidateDescription),
            }),
        });

    public static DecisionAnswer ChoiceAnswer(string questionId, string choice, double confidence) =>
        new(questionId, DecisionQuestionKind.Choice, choice, confidence, null, null,
            new Dictionary<string, double> { [choice] = confidence });

    /// <summary>IDecisionProvider test double: programmed answers or failure, call counted.</summary>
    public sealed class FakeProvider : IDecisionProvider
    {
        private readonly Func<DecisionRequest, CancellationToken, Task<IReadOnlyList<DecisionAnswer>>> behavior;
        public int Calls;
        public DecisionRequest? LastRequest;
        public FakeProvider(Func<DecisionRequest, CancellationToken, Task<IReadOnlyList<DecisionAnswer>>> behavior) => this.behavior = behavior;
        public FakeProvider(IReadOnlyList<DecisionAnswer> answers) : this((_, _) => Task.FromResult(answers)) { }
        public string ProviderId => "fake-jev";
        public Task<IReadOnlyList<DecisionAnswer>> Decide(DecisionRequest request, CancellationToken ct) { Calls++; LastRequest = request; return behavior(request, ct); }
    }

    /// <summary>HttpMessageHandler test double capturing requests and queueing responses.</summary>
    public sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> behavior;
        public int Calls;
        public string? LastBody;
        public HttpRequestMessage? LastRequest;
        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> behavior) => this.behavior = behavior;
        public RecordingHandler(HttpResponseMessage response) : this((_, _) => Task.FromResult(response)) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; LastRequest = request;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await behavior(request, cancellationToken);
        }
    }

    public static HttpResponseMessage Json(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}
