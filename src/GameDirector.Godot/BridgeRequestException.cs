using GameDirector.Core.Dsl;

namespace GameDirector.Godot;

public sealed class BridgeRequestException(BridgeErrorDefinition error, string message) : InvalidOperationException(message)
{
    public BridgeErrorDefinition Error { get; } = error;
    public static BridgeErrorDefinition Classify(Exception ex) => ex is BridgeRequestException known ? known.Error : BridgeErrors.Internal;
}
