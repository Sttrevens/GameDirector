using GameDirector.Client;
using GameDirector.Core.Dsl;

namespace GameDirector.Workbench;

/// <summary>One generative planning round: a brief and the film to revise.</summary>
public sealed record PlanGenerationRequest(string Brief, FilmPlan? CurrentFilm);

/// <summary>Everything a plan generator may compose from: the live capability
/// manifest, the project's imported media, optional evidence-backed performance
/// context, and a validation callback returning the film's first validation
/// error (or null). Generators never touch storage or the bridge directly.</summary>
public sealed record PlanGenerationContext(
    CapabilityManifest Manifest,
    IReadOnlyList<MediaAsset> Media,
    string? PerformanceEvidence,
    Func<FilmPlan, string?> Validate);

/// <summary>Generates one FilmPlan from a brief. Extracted from the API
/// director's endpoint orchestration so the model conversation (HTTP, prompt,
/// repair) is a separately testable responsibility. Implementations may make
/// bounded repair attempts; they never persist or submit production.</summary>
public interface IPlanGenerator
{
    Task<FilmPlan> Generate(PlanGenerationRequest request, PlanGenerationContext context, CancellationToken ct);
}
