using System.Numerics;
using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

/// <summary>
/// Écrit les propriétés de la caméra actuelle dans les opérandes typés d'une instruction.
/// Mapping inverse de ScriptCameraStateResolver.Apply.
/// </summary>
internal static class CameraPropertyWriter
{
    public static IReadOnlyList<(int ArgIndex, string Value)> WriteOperands(
        DecompiledInstruction instruction, ScriptCameraSnapshot snapshot,
        ScriptCameraState? beforeState = null)
    {
        var writes = new List<(int, string)>();
        var name = instruction.Name;

        // Camera_SetDistance : distance absolue
        if (name.Equals("Camera_SetDistance", StringComparison.OrdinalIgnoreCase))
            WriteFloat(writes, instruction, 2, snapshot.Distance);

        // Camera_SetAngles : pitch/yaw/roll absolus
        else if (name.Equals("Camera_SetAngles", StringComparison.OrdinalIgnoreCase))
        {
            WriteFloat(writes, instruction, 2, snapshot.PitchDegrees);
            WriteFloat(writes, instruction, 3, snapshot.YawDegrees);
            WriteFloat(writes, instruction, 4, 0f); // roll
        }

        // Camera_SetFOV : FOV
        else if (name.Equals("Camera_SetFOV", StringComparison.OrdinalIgnoreCase))
            WriteFloat(writes, instruction, 2, snapshot.VerticalFieldOfViewDegrees);

        // Camera_LookAtPosition : target position
        else if (name.Equals("Camera_LookAtPosition", StringComparison.OrdinalIgnoreCase))
        {
            WriteFloat(writes, instruction, 2, snapshot.Target.X);
            WriteFloat(writes, instruction, 3, snapshot.Target.Y);
            WriteFloat(writes, instruction, 4, snapshot.Target.Z);
        }

        // Camera_LookAtEntityNode : offset depuis l'entité (position relative)
        else if (name.Equals("Camera_LookAtEntityNode", StringComparison.OrdinalIgnoreCase))
        {
            WriteFloat(writes, instruction, 5, snapshot.Target.X);
            WriteFloat(writes, instruction, 6, snapshot.Target.Y);
            WriteFloat(writes, instruction, 7, snapshot.Target.Z);
        }

        // Camera_LookAtEntityNodeRelative : idem
        else if (name.Equals("Camera_LookAtEntityNodeRelative", StringComparison.OrdinalIgnoreCase))
        {
            WriteFloat(writes, instruction, 3, snapshot.Target.X);
            WriteFloat(writes, instruction, 4, snapshot.Target.Y);
            WriteFloat(writes, instruction, 5, snapshot.Target.Z);
        }

        // Camera_SetTarget_Relative : offset par rapport au target avant
        else if (name.Equals("Camera_SetTarget_Relative", StringComparison.OrdinalIgnoreCase)
            && beforeState is not null)
        {
            var beforeTarget = beforeState.Target ?? snapshot.Target;
            WriteFloat(writes, instruction, 2, snapshot.Target.X - beforeTarget.X);
            WriteFloat(writes, instruction, 3, snapshot.Target.Y - beforeTarget.Y);
            WriteFloat(writes, instruction, 4, snapshot.Target.Z - beforeTarget.Z);
        }

        // Camera_SetEye_Relative : offset par rapport a la position avant
        else if (name.Equals("Camera_SetEye_Relative", StringComparison.OrdinalIgnoreCase)
            && beforeState is not null)
        {
            var beforePos = beforeState.Position ?? snapshot.Position;
            WriteFloat(writes, instruction, 2, snapshot.Position.X - beforePos.X);
            WriteFloat(writes, instruction, 3, snapshot.Position.Y - beforePos.Y);
            WriteFloat(writes, instruction, 4, snapshot.Position.Z - beforePos.Z);
        }

        // Camera_ZoomBy : delta distance
        else if (name.Equals("Camera_ZoomBy", StringComparison.OrdinalIgnoreCase)
            && beforeState is not null)
        {
            var beforeDist = beforeState.Distance ?? snapshot.Distance;
            WriteFloat(writes, instruction, 2, snapshot.Distance - beforeDist);
        }

        // Camera_RotateBy : delta pitch/yaw/roll
        else if (name.Equals("Camera_RotateBy", StringComparison.OrdinalIgnoreCase)
            && beforeState is not null)
        {
            var bYaw = beforeState.YawDegrees ?? snapshot.YawDegrees;
            var bPitch = beforeState.PitchDegrees ?? snapshot.PitchDegrees;
            WriteFloat(writes, instruction, 2, snapshot.PitchDegrees - bPitch);
            WriteFloat(writes, instruction, 3, snapshot.YawDegrees - bYaw);
            WriteFloat(writes, instruction, 4, 0f);
        }

        // Camera_AddYaw : delta yaw
        else if (name.Equals("Camera_AddYaw", StringComparison.OrdinalIgnoreCase)
            && beforeState is not null)
        {
            var bYaw = beforeState.YawDegrees ?? snapshot.YawDegrees;
            WriteFloat(writes, instruction, 2, snapshot.YawDegrees - bYaw);
        }

        // Camera_LookAtMidpoint : vertical offset
        else if (name.Equals("Camera_LookAtMidpoint", StringComparison.OrdinalIgnoreCase))
            WriteFloat(writes, instruction, 4, snapshot.Target.Y);

        // Camera_AlignToEntity : pitch abs + yaw offset (si beforeState dispo)
        else if (name.Equals("Camera_AlignToEntity", StringComparison.OrdinalIgnoreCase))
        {
            WriteFloat(writes, instruction, 3, snapshot.PitchDegrees);
            var bYaw = beforeState?.YawDegrees ?? snapshot.YawDegrees;
            WriteFloat(writes, instruction, 4, snapshot.YawDegrees - bYaw);
            WriteFloat(writes, instruction, 5, 0f);
        }

        return writes;
    }

    private static void WriteFloat(List<(int, string)> writes, DecompiledInstruction instruction, int argIndex, float value)
    {
        if (argIndex < instruction.Arguments.Count
            && instruction.Arguments[argIndex].Kind == "scalar"
            && instruction.Arguments[argIndex].Type == "f32")
        {
            writes.Add((argIndex, value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture)));
        }
    }
}
