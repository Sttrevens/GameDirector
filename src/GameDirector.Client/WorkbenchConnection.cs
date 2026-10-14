using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GameDirector.Client;

// Deliberately not a record: diagnostic ToString must never print credentials.
public sealed class WorkbenchConnection(Uri endpoint, string? token)
{
    public const string UnauthorizedMessage = "Read the owning instance's token from .service.json; pair GAMEDIRECTOR_WORKBENCH with GAMEDIRECTOR_WORKBENCH_TOKEN for an explicit endpoint. To open the browser, use Unity or gd workbench --open.";
    public Uri Endpoint { get; } = endpoint;
    public string? Token { get; } = string.IsNullOrWhiteSpace(token) ? null : token;
    public override string ToString() => Endpoint.ToString();
    public HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = Endpoint, Timeout = timeout };
        if (Token != null) http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        return http; // no token: the server returns an actionable typed 401
    }
    public async Task<string> BrowserUrl(CancellationToken ct = default)
    {
        using var http = CreateClient(TimeSpan.FromSeconds(5));
        using var response = await http.PostAsJsonAsync("/api/session/code", new { }, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Workbench returned HTTP " + (int)response.StatusCode + (response.StatusCode == HttpStatusCode.Unauthorized ? ": " + UnauthorizedMessage : " while opening the browser."), null, response.StatusCode);
        var reply = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return Endpoint.ToString().TrimEnd('/') + "/?code=" + Uri.EscapeDataString(reply.GetProperty("code").GetString()!);
    }
}
