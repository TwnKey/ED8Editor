namespace ED8Editor.Viewer;

/// <summary>
/// Maps the movement-controller animation state used by entity movement opcodes
/// to the ordinary field locomotion clip selected by the game's ANI scripts.
/// Conditional ANI variants require runtime registers and are only evaluated
/// for explicit script animation calls.
/// </summary>
internal static class ScriptLocomotionAnimationCatalog
{
    /// <summary>
    /// The movement handler does not pick a clip: it calls the actor's own ANI
    /// function (AniWalk / AniRun / AniDush), which is what selects the clip for
    /// the actor's current mode — battle stance, umbrella, horse and so on.
    /// </summary>
    public static bool TryResolveAnimationFunction(int animationState, out string functionName)
    {
        functionName = animationState switch
        {
            1 => "AniWalk",
            2 => "AniRun",
            3 => "AniDush",
            _ => string.Empty,
        };
        return functionName.Length != 0;
    }

    /// <summary>
    /// Clip used when the actor has no ANI script to answer with: the plain
    /// field locomotion every character declares.
    /// </summary>
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
