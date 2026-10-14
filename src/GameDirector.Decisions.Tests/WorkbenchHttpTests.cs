using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Workbench;

namespace GameDirector.Decisions.Tests;

// Runs the real Program.cs (including strict binding and exception middleware),
// not a test copy of its routes. Only the engine is the existing loopback fixture.
public class WorkbenchHttpTests
{
    [Fact]
    public async Task HttpFailuresKeepTheirMeaningAndUnknownsAreLoggedNotLeaked()
    {
        await using var server = await WorkbenchProcess.Start();
        await server.Call("projects", new StudioProject { Id = "proj", Name = "Test", Endpoint = "http://127.0.0.1:1" });
        var engine = await server.Call("projects/proj/connect", new { }, 503);
        Assert.Equal("engine_unavailable", engine["code"]!.GetValue<string>());
        Assert.True(engine["retryable"]!.GetValue<bool>());
        Assert.Contains("engine", engine["error"]!.GetValue<string>());
        var missing = await server.Call("jobs/absent", expected: 404);
        Assert.Equal("not_found", missing["code"]!.GetValue<string>());
        Assert.False(missing["retryable"]!.GetValue<bool>());
        var provider = await server.Call("direct", new DirectRequest("proj", "portrait", null, false, "direct-1"), 412);
        Assert.Equal("provider_not_configured", provider["code"]!.GetValue<string>());
        Assert.False(provider["retryable"]!.GetValue<bool>());

        const string document = "projects/proj/documents/film";
        var film = new JsonObject { ["version"] = 1, ["title"] = "Test", ["scenes"] = new JsonArray(), ["inventedFeature"] = true };
        var binding = await server.Call(document, new { expectedRevision = 0, requestId = "bad", film }, 422);
        Assert.Equal("invalid_input", binding["code"]!.GetValue<string>());
        Assert.False(binding["retryable"]!.GetValue<bool>());
        Assert.Contains("inventedFeature", binding["error"]!.GetValue<string>());
        film.Remove("inventedFeature");
        await server.Call(document, new { expectedRevision = 0, requestId = "save-1", film });
        var conflict = await server.Call(document, new { expectedRevision = 0, requestId = "save-2", film }, 409);
        Assert.Equal("document_conflict", conflict["code"]!.GetValue<string>());
        Assert.Equal(1, conflict["current"]!["revision"]!.GetValue<int>());
        var revision = await server.Call(document, new { requestId = "save-3", film }, 428);
        Assert.Equal("revision_required", revision["code"]!.GetValue<string>());

        var file = Path.Combine(server.Root, "documents", "proj", "film", "000000000001.json");
        var damaged = JsonNode.Parse(File.ReadAllText(file))!; damaged["film"]!["title"] = "tampered";
        File.WriteAllText(file, damaged.ToJsonString());
        var error = await server.Call(document, expected: 500);
        Assert.Equal("store_integrity", error["code"]!.GetValue<string>());
        Assert.False(error["retryable"]!.GetValue<bool>());
        Assert.Equal("Document history is inconsistent; original files have been preserved.", error["error"]!.GetValue<string>());
        Assert.DoesNotContain("IOException", error.ToJsonString());
        Assert.DoesNotContain(server.Root, error.ToJsonString());
        // ILogger must get the exception object as well as its message, so the
        // actual console log has both the diagnostic text and stack frames.
        await server.WaitForLog(entry => entry.Contains("GameDirector.Workbench.FilmDocuments.ParseRevision"));
        Assert.Contains("Document history is inconsistent", server.Log);
        Assert.Contains("StoreIntegrityFault", server.Log);
        Assert.Contains("GameDirector.Workbench.FilmDocuments.ParseRevision", server.Log);
    }

    [Fact]
    public async Task EveryJobEndpointReturnsLegacyStateStringsAndAuthoritativeActions()
    {
        await using var rig = await ServiceRig.Start(null);
        await using var server = await WorkbenchProcess.Start();
        await server.Call("projects", rig.Studio.Project("proj"));
        var film = FilmCompiler.Starter(rig.Manifest);
        var job = await server.Call("jobs", new ProductionRequest { ProjectId = "proj", RequestId = "job-1", Film = film });
        AssertActions(job);
        var id = job["id"]!.GetValue<string>();
        AssertActions(await server.Call("jobs/" + id));
        AssertActions(await server.Call("jobs/" + id + "/cancel", new { }));
        AssertActions(await server.Call("jobs/" + id + "/resume", new { }));
        var state = await server.Call("state");
        foreach (var saved in state["jobs"]!.AsArray()) AssertActions(saved!);
        const string document = "projects/proj/documents/film";
        await server.Call(document, new SaveFilmDocument(0, "save", film));
        AssertActions(await server.Call(document + "/produce", new ProduceFilmDocument("document-job", 1)));
        // A persisted proposal bypasses the paid provider exactly as a real retry
        // does; Direct's optional job must have the same DTO contract too.
        var request = new DirectRequest("proj", "portrait", null, true, "direct-job");
        var directory = Path.Combine(server.Root, "proposals", "proj"); Directory.CreateDirectory(directory);
        DslJson.SaveAtomic(Path.Combine(directory, request.RequestId + ".json"), new { requestHash = ContentIdentity.Of(request), film });
        AssertActions((await server.Call("direct", request))["job"]!);
    }

    [Theory]
    [InlineData("GET", "state")]
    [InlineData("GET", "provider")]
    [InlineData("POST", "provider")]
    [InlineData("GET", "media/install")]
    [InlineData("POST", "media/install")]
    [InlineData("DELETE", "media/install")]
    [InlineData("POST", "folders")]
    [InlineData("POST", "projects")]
    [InlineData("GET", "projects/absent/documents")]
    [InlineData("POST", "direct")]
    [InlineData("GET", "jobs/absent/video")]
    [InlineData("GET", "doctor")]
    [InlineData("GET", "storage")]
    [InlineData("GET", "decisions/provider")]
    [InlineData("GET", "unknown")]
    public async Task LocalProcessesWithoutCredentialsCannotReadOrAct(string method, string path)
    {
        await using var server = await WorkbenchProcess.Start();
        using var response = await server.Send(path, method, bearer: false);
        Assert.Equal(401, (int)response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("unauthorized", body["code"]!.GetValue<string>());
        Assert.False(body["retryable"]!.GetValue<bool>());
        Assert.Contains("gd workbench --open", body["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task HealthAndStaticFilesStayPublicButWebAdmissionStillPrecedesAuth()
    {
        await using var server = await WorkbenchProcess.Start();
        using var health = await server.Send("health", bearer: false);
        Assert.Equal(200, (int)health.StatusCode);
        foreach (var path in new[] { "/", "/app.js", "/style.css" }) {
            using var file = await server.Send(path, bearer: false);
            Assert.Equal(200, (int)file.StatusCode);
        }
        foreach (var header in new[] { "Host", "Origin", "Sec-Fetch-Site" }) {
            using var response = await server.Send("state", bearer: false, header: (header, header == "Sec-Fetch-Site" ? "cross-site" : header == "Host" ? "foreign.invalid" : "https://foreign.invalid"));
            Assert.Equal(403, (int)response.StatusCode);
        }
    }

    [Fact]
    public async Task BearerIsRequiredAndNeverAppearsInResponsesOrLogs()
    {
        await using var server = await WorkbenchProcess.Start();
        Assert.Equal(64, server.Token.Length);
        Assert.Equal(32, Convert.FromHexString(server.Token).Length);
        await server.Call("state"); await server.Call("provider"); await server.Call("media/install");
        await server.Call("projects", new StudioProject { Id = "auth", Name = "Auth", Endpoint = "http://127.0.0.1:1" });
        foreach (var wrong in new[] { "", "wrong", new string('0', 64), server.Token[..63], server.Token + "x" }) {
            using var response = await server.Send("state", credential: wrong);
            Assert.Equal(401, (int)response.StatusCode);
        }
        using var query = await server.Send("state?token=not-a-credential", bearer: false);
        Assert.Equal(401, (int)query.StatusCode);
        Assert.DoesNotContain(server.Token, server.Log);
    }

    [Fact]
    public async Task BrowserCodeRequiresBearerAndIsSingleUseWithStrictHttpOnlyCookie()
    {
        await using var server = await WorkbenchProcess.Start();
        using var denied = await server.Send("session/code", "POST", bearer: false);
        Assert.Equal(401, (int)denied.StatusCode);
        var mint = await server.Call("session/code", new { });
        var code = mint["code"]!.GetValue<string>();
        using var exchange = await server.Send("session", "POST", bearer: false, body: new { code });
        Assert.Equal(200, (int)exchange.StatusCode);
        var cookie = Assert.Single(exchange.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie.ToLowerInvariant()); Assert.Contains("samesite=strict", cookie.ToLowerInvariant()); Assert.Contains("path=/", cookie.ToLowerInvariant());
        cookie = cookie.Split(';')[0];
        using var state = await server.Send("state", bearer: false, cookie: cookie);
        Assert.Equal(200, (int)state.StatusCode);
        using var replay = await server.Send("session", "POST", bearer: false, body: new { code });
        Assert.Equal(401, (int)replay.StatusCode);
        using var cookieMint = await server.Send("session/code", "POST", bearer: false, cookie: cookie);
        Assert.Equal(401, (int)cookieMint.StatusCode);
        using var invalid = await server.Send("session", "POST", bearer: false, body: new { code = "invalid" });
        Assert.Equal(401, (int)invalid.StatusCode);
        Assert.False(invalid.Headers.Contains("Set-Cookie"));
        using var forged = await server.Send("state", bearer: false, cookie: cookie + "x");
        Assert.Equal(401, (int)forged.StatusCode);
        await using var other = await WorkbenchProcess.Start();
        Assert.NotEqual(server.Token, other.Token);
        using var prior = await other.Send("state", bearer: false, cookie: cookie);
        Assert.Equal(401, (int)prior.StatusCode);
        Assert.DoesNotContain(server.Token, server.Log);
    }

    [Fact]
    public async Task TrustedClientBrowserUrlContainsOnlyAnExchangeableCode()
    {
        await using var server = await WorkbenchProcess.Start();
        var connection = new WorkbenchConnection(server.Endpoint, server.Token);
        var url = await connection.BrowserUrl();
        Assert.DoesNotContain(server.Token, url);
        var uri = new Uri(url); Assert.Equal(server.Endpoint.Authority, uri.Authority);
        Assert.StartsWith("?code=", uri.Query);
        using var exchange = await server.Send("session", "POST", bearer: false, body: new { code = uri.Query[6..] });
        Assert.Equal(200, (int)exchange.StatusCode);
        Assert.DoesNotContain(server.Token, server.Log);
    }

    [Fact]
    public async Task BareOpenFlagMintsABrowserCodeThroughTheAuthenticatedApi()
    {
        await using var server = await WorkbenchProcess.Start(open: true);
        var prefix = "Open the Workbench: " + server.Endpoint.ToString().TrimEnd('/') + "/?code=";
        await server.WaitForLog(entry => entry.Contains("HTTP: POST /api/session/code") && entry.Contains(prefix));
        var log = server.Log;
        Assert.Contains("HTTP: POST /api/session/code", log);
        Assert.DoesNotContain(server.Token, log);
        var line = Assert.Single(log.Split('\n').Where(l => l.StartsWith("Open the Workbench: ", StringComparison.Ordinal)));
        Assert.StartsWith(prefix, line);
        var code = line[prefix.Length..];
        Assert.False(string.IsNullOrWhiteSpace(code)); Assert.NotEqual(server.Token, code);
        using var exchange = await server.Send("session", "POST", bearer: false, body: new { code = Uri.UnescapeDataString(code) });
        Assert.Equal(200, (int)exchange.StatusCode);
        Assert.DoesNotContain(server.Token, server.Log);
    }

    [Fact]
    public async Task DescriptorIsOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows()) return; // Windows inherits the per-user LOCALAPPDATA ACL.
        await using var server = await WorkbenchProcess.Start();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(server.Root, ".service.json")));
    }

    private static void AssertActions(JsonNode job)
    {
        var state = Enum.Parse<ProductionJobState>(job["state"]!.GetValue<string>());
        Assert.Equal(ProductionJobFlow.Actions(state), job["actions"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray());
    }

    internal sealed class WorkbenchProcess : IAsyncDisposable
    {
        private readonly TempPaths temp = new();
        public string Root { get; }
        private WorkbenchProcess() { Root = temp.TempPath("gd-http-"); }
        private readonly ConcurrentQueue<string> log = new();
        public string Log => string.Join("\n", log);
        // Deadlines, never iteration counts: the budget is TestBudget.Hang whatever the
        // machine is doing, a passing wait resolves on the event, and a wait that gives
        // up reports the child's own log instead of failing on a stale snapshot.
        private static async Task Until(Func<bool> done, Func<string> why)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.Elapsed < TestBudget.Hang) await Task.Delay(20);
            Assert.True(done(), why());
        }
        public Task WaitForLog(Func<string, bool> seen) => Until(() => seen(Log), () => "Workbench child never logged the expected state:\n" + Log);
        public string Token { get; private set; } = "";
        public Uri Endpoint => new(http.BaseAddress!, "/");
        private Process? process;
        private HttpClient http = null!;
        public static async Task<WorkbenchProcess> Start(bool open = false)
        {
            var server = new WorkbenchProcess();
            try
            {
                var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var arg in new[] { typeof(Studio).Assembly.Location, "--port", "0", "--data", server.Root }) info.ArgumentList.Add(arg);
                if (open) info.ArgumentList.Add("--open");
                info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production"; // binder failures must not depend on Development
                info.Environment[BrowserLauncher.Variable] = "none";
                server.process = Process.Start(info)!;
                server.process.OutputDataReceived += (_, e) => { if (e.Data != null) server.log.Enqueue(e.Data); };
                server.process.ErrorDataReceived += (_, e) => { if (e.Data != null) server.log.Enqueue(e.Data); };
                server.process.BeginOutputReadLine(); server.process.BeginErrorReadLine();
                var descriptor = Path.Combine(server.Root, ".service.json");
                await Until(() => File.Exists(descriptor) || server.process.HasExited, () => server.Log);
                Assert.True(File.Exists(descriptor), server.Log);
                var record = JsonNode.Parse(File.ReadAllText(descriptor))!;
                var address = record["address"]!.GetValue<string>();
                server.Token = record["token"]?.GetValue<string>() ?? "";
                server.http = new HttpClient(new HttpClientHandler { UseProxy = false, UseCookies = false }) { BaseAddress = new Uri(address + "/api/") };
                return server;
            }
            catch { await server.DisposeAsync(); throw; }
        }
        public async Task<JsonNode> Call(string path, object? body = null, int expected = 200)
        {
            using var response = await Send(path, body == null ? "GET" : "POST", body: body);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True((int)response.StatusCode == expected, path + ": expected " + expected + ", got " + (int)response.StatusCode + " " + text);
            return JsonNode.Parse(text)!;
        }
        public async Task<HttpResponseMessage> Send(string path, string method = "GET", bool bearer = true, string? credential = null, string? cookie = null, object? body = null, (string Key, string Value)? header = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method != "GET") request.Content = JsonContent.Create(body ?? new { });
            if (bearer && (credential ?? Token) != "") request.Headers.Authorization = new("Bearer", credential ?? Token);
            if (cookie != null) request.Headers.Add("Cookie", cookie);
            if (header != null) request.Headers.Add(header.Value.Key, header.Value.Value);
            var response = await http.SendAsync(request);
            if (Token != "") Assert.DoesNotContain(Token, await response.Content.ReadAsStringAsync());
            return response;
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                http?.Dispose();
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                    finally { process.Dispose(); }
                }
            }
            finally { temp.Dispose(); }
        }
    }
}
