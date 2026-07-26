using System.Numerics;

namespace ED8Editor.Viewer;

/// <summary>
/// Resolves OP54's authored vector using the mode branches observed in
/// FUN_0065b610. Mode -511 depends on a live engine-global field object and
/// cannot be resolved from a standalone script state.
/// </summary>
internal static class ScriptEntityMoveModeResolver
{
    public static bool TryResolveTarget(
        short mode,
        ScriptEntityState entity,
        Vector3 authored,
        out Vector3 target)
    {
        switch (mode)
        {
            case -2:
            case -509:
                if (!entity.HasPosition)
                {
                    target = default;
                    return false;
                }
                target = entity.Position + authored;
                return true;

            case -512:
                if (!entity.HasPosition)
                {
                    target = default;
                    return false;
                }
                var yaw = entity.YawDegrees * MathF.PI / 180f;
                var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
                target = entity.Position + forward * authored.Z;
                return true;

            case -511:
                target = default;
                return false;

            default:
                target = authored;
                return true;
        }
    }
}
