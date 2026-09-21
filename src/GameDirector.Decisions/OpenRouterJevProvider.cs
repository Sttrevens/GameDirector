using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace GameDirector.Decisions;

/// <summary>Connection settings for the decisions endpoint. Independent from the
/// generative plan provider: its own endpoint, model and memory-only key.</summary>
public sealed record DecisionProviderSettings(string Endpoint, string Model, string ApiKey)
{
    public static DecisionProviderSettings Official(string apiKey) =>
        new(OpenRouterJevProvider.DefaultEndpoint, OpenRouterJevProvider.DefaultModel, apiKey);
}

/// <summary>IDecisionProvider over the OpenRouter alpha decisions endpoint
/// (official contract: POST {model,state,questions} → {answers}). One bounded
/// attempt per Decide call: no automatic retry, no redirect following, explicit
/// timeout and request/response size caps. Local paths must already be scrubbed
/// from the request; the provider also refuses payloads containing path-like
/// fragments as a final guard.</summary>
public sealed class OpenRouterJevProvider : IDecisionProvider
{
    public const string DefaultEndpoint = "https://openrouter.ai/api/alpha/decisions";
    public const string DefaultModel = "typesafe/jev-1.13";
    public const string Id = "openrouter-jev";

    private readonly DecisionProviderSettings settings;
    private readonly HttpMessageHandler? handler;
    private readonly TimeSpan timeout;

    public string ProviderId => Id;

    public OpenRouterJevProvider(DecisionProviderSettings settings, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        this.settings = Validate(settings);
        this.handler = handler;
        this.timeout = timeout ?? TimeSpan.FromSeconds(DecisionPolicyBounds.TimeoutSeconds);
    }

    public static DecisionProviderSettings Validate(DecisionProviderSettings settings)
    {
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri) || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("Provide an HTTPS decisions endpoint. HTTP is allowed only for a local endpoint.");
        if (string.IsNullOrWhiteSpace(settings.Model) || settings.Model.Length > DecisionPolicyBounds.MaxModelChars)
            throw new ArgumentException("Provide a decision model id (up to " + DecisionPolicyBounds.MaxModelChars + " characters).");
        if (settings.ApiKey != null && settings.ApiKey.Length > DecisionPolicyBounds.MaxApiKeyChars)
            throw new ArgumentException("API key is too long.");
        return settings;
    }

    public async Task<IReadOnlyList<DecisionAnswer>> Decide(DecisionRequest request, CancellationToken ct)
    {
        var body = JevWire.SerializeRequest(JevWire.BuildRequest(request));
        if (Encoding.UTF8.GetByteCount(body) > DecisionPolicyBounds.MaxRequestBytes)
            throw new DecisionProviderException("Decision request exceeds " + DecisionPolicyBounds.MaxRequestBytes + " bytes; offer fewer candidates.");
        var path = LocalPathScrubber.FindPathLike(body);
        if (path != null)
            throw new DecisionProviderException("Refusing to send a decision payload containing a local path fragment: " + path);

        using var http = handler == null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = timeout;
        http.MaxResponseContentBufferSize = DecisionPolicyBounds.MaxResponseBytes;
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            // One attempt. A decision is not idempotent-priced on the provider side;
            // callers reuse persisted audit records instead of re-asking.
            response = await http.PostAsync(settings.Endpoint, content, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DecisionProviderException("Decision provider did not answer within " + (int)timeout.TotalSeconds + " seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new DecisionProviderException("Decision provider request failed: " + ex.Message, ex);
        }
        using (response)
        {
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                throw new DecisionProviderException("Decision provider attempted a redirect (HTTP " + (int)response.StatusCode + "); redirects are not followed.");
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new DecisionProviderException("Decision provider rejected the API key (HTTP " + (int)response.StatusCode + ").");
            if (!response.IsSuccessStatusCode)
                throw new DecisionProviderException("Decision provider returned HTTP " + (int)response.StatusCode + ". No automatic retry was made.");
            string text;
            try { text = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false); }
            catch (HttpRequestException ex) { throw new DecisionProviderException("Decision response could not be read within the size cap.", ex); }
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(text); }
            catch (Exception ex) { throw new DecisionProviderException("Decision provider returned malformed JSON.", ex); }
            return JevWire.ParseAnswers(parsed, request);
        }
    }
}
