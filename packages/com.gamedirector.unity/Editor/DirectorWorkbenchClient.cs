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
        public DirectorServiceException(int status, JObject detail) : base((string)detail["error"] ?? "Workbench request failed") { Status = status; Detail = detail; }
    }

    // Both Editor surfaces use this client. Authoring data stays in the asset;
    // accepted revisions and retry identities are tracked separately from Unity Undo.
    public static class DirectorWorkbenchClient
    {
        public static string StoreId { get; private set; } = "";
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
                uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "") throw new ArgumentException("Use a local address such as " + GameDirector.Core.Dsl.BridgeDefaults.WorkbenchEndpoint + ".");
        }
        public static async Task<JToken> Call(string path, object body = null, CancellationToken ct = default)
        {
            var address = Address; RequireLocal(address);
            using (var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(path == "health" ? 3 : 190) })
            using (var response = body == null ? await client.GetAsync(address + "/api/" + path, ct) :
                await client.PostAsync(address + "/api/" + path, new StringContent(BridgeJson.Serialize(body), Encoding.UTF8, "application/json"), ct))
            {
                var text = await response.Content.ReadAsStringAsync();
                var result = JToken.Parse(text);
                if (!response.IsSuccessStatusCode) throw new DirectorServiceException((int)response.StatusCode, result as JObject ?? new JObject { ["error"] = text });
                return result;
            }
        }
        public static async Task Register(CancellationToken ct = default)
        {
            var health = await Call("health", null, ct);
            if ((string)health["product"] != "GameDirector" || (int?)health["protocolVersion"] != 1)
                throw new InvalidOperationException("Install a matching GameDirector Workbench (document protocol 1).");
            StoreId = (string)health["storeId"];
            await Call("projects", new { id = ProjectId, name = Application.productName, engine = "Unity",
                sourceRoot = Path.GetDirectoryName(Application.dataPath), endpoint = BridgeAddress }, ct);
        }
        public static string DocumentPath(DirectorFilmAsset asset)
        {
            if (string.IsNullOrEmpty(asset.DocumentId)) throw new InvalidOperationException("Save this film as a Unity asset first.");
            return "projects/" + ProjectId + "/documents/" + asset.DocumentId;
        }
        public static void OpenBrowser(DirectorFilmAsset asset = null)
        {
            RequireLocal(Address);
            Application.OpenURL(Address + "/?project=" + Uri.EscapeDataString(ProjectId) + (asset == null ? "" : "&document=" + asset.DocumentId));
        }
        public static async Task<JToken> Operation(string path, JObject body, CancellationToken ct = default)
        {
            // Kept across assembly reload. Unknown outcomes retain their exact input.
            var key = "GameDirector.Pending." + Hash128.Compute(StoreId + "/" + path);
            var signature = body.ToString(Newtonsoft.Json.Formatting.None);
            var saved = EditorPrefs.GetString(key, "");
            var pending = saved == "" ? new JObject { ["signature"] = signature, ["requestId"] = Guid.NewGuid().ToString("N") } : JObject.Parse(saved);
            if ((string)pending["signature"] != signature) throw new InvalidOperationException("The previous operation has an unknown result. Retry it with the original inputs before submitting different inputs.");
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
