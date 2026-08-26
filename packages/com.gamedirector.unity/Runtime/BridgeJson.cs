using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace GameDirector.Unity
{
    /// <summary>
    /// Wire JSON settings for the bridge. CamelCase to match the DSL contract
    /// used by the dotnet side (GameDirector.Client.DslJson). Newtonsoft reads
    /// case-insensitively, so inbound TimelineAsset JSON just works.
    /// </summary>
    public static class BridgeJson
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented
        };

        public static string Serialize(object dto) => JsonConvert.SerializeObject(dto, Settings);
        public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);
    }
}
