namespace ED8Editor.Viewer;

internal enum ScriptEntityResolution
{
    Concrete,
    Contextual,
    Placeholder,
    NonExecutable,
}

internal sealed record ScriptEntityReference(
    int RawId,
    string Symbol,
    ScriptEntityResolution Resolution,
    int? ConcreteEntityId = null);

internal static class ScriptEntityReferences
{
    /// <summary>The script's own actor: every ANI or craft script talks to -2.</summary>
    public const int SelfEntityId = -2;

    public const string PlaceholderAssetId = "__ED8_ENTITY_PLACEHOLDER__";
    public const string PlaceholderSourceAssetId = "C_PLY000";

    public static ScriptEntityReference Resolve(int rawId, int? selfEntityId = null)
    {
        if (rawId == -2)
        {
            return selfEntityId is { } self
                ? new ScriptEntityReference(
                    rawId, "Entity_Self", ScriptEntityResolution.Concrete, self)
                : new ScriptEntityReference(
                    rawId, "Entity_Self", ScriptEntityResolution.Contextual);
        }
        return rawId switch
        {
            -4 => Placeholder(rawId, "UNKNOWN7"),
            -3 => Placeholder(rawId, "Entity_Null"),
            -3993 => Placeholder(rawId, "UNKNOWN4"),
            -5 => Placeholder(rawId, "Battle_AreaTarget1"),
            -6 => Placeholder(rawId, "Battle_AreaTarget2"),
            -9 => Placeholder(rawId, "Battle_Attacker"),
            -20 => Placeholder(rawId, "UNKNOWN"),
            -10 => Placeholder(rawId, "TacticalLinkPartner"),
            -4080 => Placeholder(rawId, "UNKNOWN5"),
            -4079 => Placeholder(rawId, "UNKNOWN6"),
            -23 => Placeholder(rawId, "Combat_AllyByCondition"),
            >= -4096 and <= -4090 => Placeholder(
                rawId, $"PartyMember_{rawId + 4096}"),
            >= -4064 and <= -4061 => Placeholder(
                rawId, $"UNKNOWN2_{rawId + 4064}"),
            >= -4058 and <= -4055 => Placeholder(
                rawId, $"UNKNOWN3_{rawId + 4058}"),
            _ => new ScriptEntityReference(
                rawId,
                $"Entity_{rawId}",
                ScriptEntityResolution.Concrete,
                rawId),
        };
    }

    public static string DisplayName(int rawId)
        => Resolve(rawId).Symbol;

    private static ScriptEntityReference Placeholder(int rawId, string symbol)
        => new(rawId, symbol, ScriptEntityResolution.Placeholder);
}
