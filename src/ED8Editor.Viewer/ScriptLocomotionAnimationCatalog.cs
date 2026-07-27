namespace ED8Editor.Viewer;

/// <summary>
/// Maps the movement-controller animation state used by entity movement opcodes
/// to the ordinary field locomotion clip selected by the game's ANI scripts.
/// Conditional ANI variants require runtime registers and are only evaluated
/// for explicit script animation calls.
/// </summary>
internal static class ScriptLocomotionAnimationCatalog
{
    public static bool TryResolveBaseClip(int animationState, out string clipName)
    {
        clipName = animationState switch
        {
            1 => "WALK",
            2 => "RUN",
            3 => "DASH",
            _ => string.Empty,
        };
        return clipName.Length != 0;
    }
}
