using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace GameDirector.Unity.Editor
{
    public sealed class DirectorServiceException : Exception
    {
        public readonly int Status;
        public readonly JObject Detail;
        public DirectorServiceException(int status, JObject detail) : base(DirectorUi.ServerMessage((string)(detail["messageCode"] ?? detail["code"]), (string)detail["error"] ?? DirectorUi.T("requestFailed"), detail["messageArgs"])) { Status = status; Detail = detail; }
    }

    // Both Editor surfaces use this client. Authoring data stays in the asset;
    // accepted revisions and retry identities are tracked separately from Unity Undo.
    public static class DirectorWorkbenchClient
    {
        public static string StoreId { get; private set; } = "";
        private static string token, tokenAddress;
        internal static void UseDescriptor(string address, string instanceToken) { tokenAddress = address.TrimEnd('/'); token = instanceToken; }
        private static string Token => Environment.GetEnvironmentVariable("GAMEDIRECTOR_WORKBENCH") != null
            ? Environment.GetEnvironmentVariable("GAMEDIRECTOR_WORKBENCH_TOKEN") : Address.TrimEnd('/') == tokenAddress ? token : null;
        public static string Address
        {
            get => Environment.GetEnvironmentVariable("GAMEDIRECTOR_WORKBENCH") ?? EditorPrefs.GetString("GameDirector.WorkbenchAddress", GameDirector.Core.Dsl.BridgeDefaults.WorkbenchEndpoint);
            set { RequireLocal(value); EditorPrefs.SetString("GameDirector.WorkbenchAddress", value.TrimEnd('/')); }
        }
        public static string ProjectId => "unity-" + Hash128.Compute(Path.GetFullPath(Application.dataPath));
        public static string BridgeAddress
        {
            get => Environment.GetEnvironmentVariable("GAMEDIRECTOR_BRIDGE") ?? EditorPrefs.GetString("GameDirector.Bridge." + ProjectId, GameDirector.Core.Dsl.BridgeDefaults.UnityEndpoint);
            set { RequireLocal(value); EditorPrefs.SetString("GameDirector.Bridge." + ProjectId, value.TrimEnd('/')); }
        }
        public static void RequireLocal(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != GameDirector.Core.Dsl.BridgeDefaults.LoopbackHost || uri.UserInfo != "" ||
                uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "") throw new ArgumentException(DirectorUi.T("localAddress", GameDirector.Core.Dsl.BridgeDefaults.WorkbenchEndpoint));
        }
        public static async Task<JToken> Call(string path, object body = null, CancellationToken ct = default)
        {
            if (path != "health" && Token == null && Environment.GetEnvironmentVariable("GAMEDIRECTOR_WORKBENCH") == null)
                await DirectorWorkbenchLauncher.Discover(GameDirector.Core.Dsl.GameDirectorPaths.ServiceDescriptor(), ct);
            var address = Address; RequireLocal(address);
            using (var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(path == "health" ? 3 : 190) })
            {
                var credential = Token;
                if (!string.IsNullOrEmpty(credential)) client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
                using (var response = body == null ? await client.GetAsync(address + "/api/" + path, ct) :
                    await client.PostAsync(address + "/api/" + path, new StringContent(BridgeJson.Serialize(body), Encoding.UTF8, "application/json"), ct))
                {
                    var text = await response.Content.ReadAsStringAsync();
                    JToken result;
                    try { result = JToken.Parse(text); } catch (Newtonsoft.Json.JsonException) { result = new JObject { ["error"] = DirectorUi.T("httpFailure", (int)response.StatusCode) }; }
                    if (!response.IsSuccessStatusCode) throw new DirectorServiceException((int)response.StatusCode, result as JObject ?? new JObject { ["error"] = text });
                    return result;
                }
            }
        }
        public static async Task Register(CancellationToken ct = default, AssetInventory inventory = null)
        {
            var health = await Call("health", null, ct);
            if ((string)health["product"] != GameDirector.Core.Dsl.WorkbenchService.Product || (int?)health["protocolVersion"] != GameDirector.Core.Dsl.WorkbenchService.ProtocolVersion)
                throw new InvalidOperationException(DirectorUi.T("matchingWorkbench", GameDirector.Core.Dsl.WorkbenchService.ProtocolVersion));
            StoreId = (string)health["storeId"];
            await Call("projects", new { id = ProjectId, name = Application.productName, engine = "Unity",
                sourceRoot = Path.GetDirectoryName(Application.dataPath), endpoint = BridgeAddress, inventory }, ct);
        }
        public static string DocumentPath(DirectorFilmAsset asset)
        {
            if (string.IsNullOrEmpty(asset.DocumentId)) throw new InvalidOperationException(DirectorUi.T("saveFilmFirst"));
            return "projects/" + ProjectId + "/documents/" + asset.DocumentId;
        }
        public static async Task OpenBrowser(DirectorFilmAsset asset = null, CancellationToken ct = default)
        {
            await DirectorWorkbenchLauncher.EnsureStarted(ct);
            var reply = await Call("session/code", new { }, ct);
            Application.OpenURL(Address.TrimEnd('/') + "/?code=" + Uri.EscapeDataString((string)reply["code"]) + "&project=" + Uri.EscapeDataString(ProjectId) + (asset == null ? "" : "&document=" + Uri.EscapeDataString(asset.DocumentId)));
        }
        public static async Task<JToken> Operation(string path, JObject body, CancellationToken ct = default)
        {
            // Kept across assembly reload. Unknown outcomes retain their exact input.
            var key = "GameDirector.Pending." + Hash128.Compute(StoreId + "/" + path);
            var signature = body.ToString(Newtonsoft.Json.Formatting.None);
            var saved = EditorPrefs.GetString(key, "");
            var pending = saved == "" ? new JObject { ["signature"] = signature, ["requestId"] = Guid.NewGuid().ToString("N") } : JObject.Parse(saved);
            if ((string)pending["signature"] != signature) throw new InvalidOperationException(DirectorUi.T("unknownOperation"));
            EditorPrefs.SetString(key, pending.ToString());
            var request = (JObject)body.DeepClone(); request["requestId"] = pending["requestId"];
            try { var result = await Call(path, request, ct); EditorPrefs.DeleteKey(key); return result; }
            catch (DirectorServiceException ex) when (ex.Status >= 400 && ex.Status < 500) { EditorPrefs.DeleteKey(key); throw; }
        }
        public static JObject Pending(string path)
        {
            var key = "GameDirector.Pending." + Hash128.Compute(StoreId + "/" + path);
            var saved = EditorPrefs.GetString(key, "");
            return saved == "" ? null : JObject.Parse((string)JObject.Parse(saved)["signature"]);
        }
    }
}
