using System;
using GameDirector.Core.Dsl;
namespace GameDirector.Unity {
// Only a site that knows the cause may classify a bridge failure. Do not
// infer caller mistakes from ArgumentException/InvalidOperationException.
public sealed class BridgeRequestException : InvalidOperationException {
    public BridgeErrorDefinition Error { get; }
    public int Status => Error.Status;
    public string Code => Error.Code;
    public BridgeRequestException(BridgeErrorDefinition error,string message) : base(message) { Error=error; }
    public static BridgeErrorDefinition Classify(Exception error) => error is BridgeRequestException known?known.Error:BridgeErrors.Internal;
    internal static T Read<T>(string json) {
        try {
            var value=BridgeJson.Deserialize<T>(json);
            if(value==null)throw new BridgeRequestException(BridgeErrors.InvalidRequest,"request is required");
            return value;
        } catch(Newtonsoft.Json.JsonException ex) { throw new BridgeRequestException(BridgeErrors.InvalidRequest,ex.Message); }
    }
}
}
