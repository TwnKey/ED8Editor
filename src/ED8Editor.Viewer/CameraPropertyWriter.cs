using System.Globalization;
using System.Numerics;
using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

internal sealed record CameraOperandWrite(InstructionArgument Argument, string Value);

internal sealed record CameraCaptureResult(
    IReadOnlyList<CameraOperandWrite> Writes,
    IReadOnlyList<string> Components,
    IReadOnlyList<string> UnavailableComponents)
{
    public bool CanCapture => Writes.Count > 0;
}

/// <summary>
/// Converts explicit camera semantics from the instruction registry into operand
/// updates. The registry is the source of truth: unrelated f32 operands are never
/// inferred from an opcode name or operand position.
/// </summary>
internal static class CameraPropertyWriter
{
    public static CameraCaptureResult Capture(
        DecompiledInstruction instruction,
        ScriptCameraSnapshot snapshot,
        ScriptSceneState? beforeScene)
    {
        var writes = new List<CameraOperandWrite>();
        var components = new List<string>();
        var unavailable = new List<string>();

        var beforeCamera = MaterializeCamera(beforeScene);
        for (var index = 0; index < instruction.Arguments.Count; index++)
        {
            var span = Math.Max(1, instruction.Arguments[index].SemSpan);
            var group = instruction.Arguments.Skip(index).Take(span).ToArray();
            var semantic = ReadCameraSemantic(group[0]);
            if (semantic is not null)
            {
                if (ScriptSemanticValueConverter.TryWriteCamera(
                    group, snapshot, beforeCamera, out var component, out var values))
                {
                    AddWrites(writes, group, values);
                    components.Add(component);
                }
                else if (TryWriteEntityRelative(
                    instruction, group, semantic, snapshot, beforeScene,
                    out component, out values))
                {
                    AddWrites(writes, group, values);
                    components.Add(component);
                }
                else
                {
                    unavailable.Add(DescribeSemantic(semantic));
                }
            }
            index += group.Length - 1;
        }

        return new CameraCaptureResult(
            writes,
            components.Distinct(StringComparer.Ordinal).ToArray(),
            unavailable.Distinct(StringComparer.Ordinal).ToArray());
    }


    private static ScriptCameraState? MaterializeCamera(ScriptSceneState? scene)
    {
        if (scene is null) return null;
        var state = scene.Camera;
        var target = state.Target;
        if (target is null
            && state.TargetEntityId is { } entityId
            && scene.Entities.TryGetValue(entityId, out var entity))
        {
            var center = entity.Position;
            if (state.SecondaryTargetEntityId is { } secondId
                && scene.Entities.TryGetValue(secondId, out var second))
            {
                center = (center + second.Position) * 0.5f;
            }
            var offset = state.TargetEntityOffset ?? Vector3.Zero;
            if (state.TargetOffsetUsesEntityRotation)
            {
                offset = Vector3.Transform(
                    offset,
                    Quaternion.CreateFromAxisAngle(
                        Vector3.UnitY, entity.YawDegrees * MathF.PI / 180f));
            }
            target = center + offset;
        }
        if (target is { } targetBeforeOffset && state.TargetOffset is { } targetOffset)
            target = targetBeforeOffset + targetOffset;

        var forward = state.Forward;
        var yaw = state.YawDegrees;
        var pitch = state.PitchDegrees;
        var roll = state.RollDegrees;
        if (forward is { } initialDirection && initialDirection.LengthSquared() > 1e-8f)
        {
            var authored = ScriptCameraOrbit.FromViewDirection(initialDirection);
            yaw ??= authored.YawDegrees;
            pitch ??= authored.PitchDegrees;
        }
        if (state.AlignEntityId is { } alignEntityId
            && state.AlignYawOffsetDegrees is { } alignYawOffset
            && scene.Entities.TryGetValue(alignEntityId, out var alignEntity))
        {
            yaw = alignEntity.YawDegrees + alignYawOffset;
        }
        if (state.AngleDeltaDegrees is { } angleDelta)
        {
            if (pitch is not null) pitch += angleDelta.X;
            if (yaw is not null) yaw += angleDelta.Y;
            if (roll is not null) roll += angleDelta.Z;
        }
        if (yaw is { } yawDegrees && pitch is { } pitchDegrees)
            forward = ScriptCameraOrbit.ViewDirection(pitchDegrees, yawDegrees);

        var distance = state.Distance;
        if (distance is not null && state.DistanceDelta is { } distanceDelta)
            distance += distanceDelta;
        if (distance is null && state.Position is { } knownPosition && target is { } knownTarget)
            distance = Vector3.Distance(knownPosition, knownTarget);

        var position = state.Position;
        if (position is null
            && target is { } resolvedTarget
            && forward is { } resolvedForward
            && distance is { } resolvedDistance)
        {
            position = resolvedTarget - resolvedForward * resolvedDistance;
        }
        if (position is { } positionBeforeOffset && state.PositionOffset is { } positionOffset)
            position = positionBeforeOffset + positionOffset;

        return state with
        {
            Position = position,
            Target = target,
            Forward = forward,
            Distance = distance,
            YawDegrees = yaw,
            PitchDegrees = pitch,
            RollDegrees = roll,
        };
    }

    private static bool TryWriteEntityRelative(
        DecompiledInstruction instruction,
        IReadOnlyList<InstructionArgument> arguments,
        string semantic,
        ScriptCameraSnapshot snapshot,
        ScriptSceneState? beforeScene,
        out string component,
        out string[] values)
    {
        component = string.Empty;
        values = Array.Empty<string>();
        if (beforeScene is null
            || !TryGetReferencedEntity(instruction, beforeScene, out var entity))
        {
            return false;
        }

        if (semantic == "target-entity-offset")
        {
            component = "target offset from entity";
            return TryFormatVector3(arguments, snapshot.Target - entity.Position, out values);
        }
        if (semantic == "yaw-relative-entity")
        {
            component = "yaw offset from entity";
            return TryFormatFloat(arguments, snapshot.YawDegrees - entity.YawDegrees, out values);
        }
        return false;
    }

    private static bool TryGetReferencedEntity(
        DecompiledInstruction instruction,
        ScriptSceneState beforeScene,
        out ScriptEntityState entity)
    {
        var entityArgument = instruction.Arguments.FirstOrDefault(argument =>
            argument.Kind == "scalar"
            && (argument.Sem == "entity" || argument.Sem?.StartsWith("entity:", StringComparison.Ordinal) == true));
        if (entityArgument is not null
            && beforeScene.Entities.TryGetValue(entityArgument.IntValue, out var found))
        {
            entity = found;
            return true;
        }
        entity = default!;
        return false;
    }

    private static void AddWrites(
        ICollection<CameraOperandWrite> writes,
        IReadOnlyList<InstructionArgument> arguments,
        IReadOnlyList<string> values)
    {
        if (arguments.Count != values.Count)
            throw new InvalidDataException("A camera semantic span does not match its encoded value count.");
        for (var index = 0; index < arguments.Count; index++)
            writes.Add(new CameraOperandWrite(arguments[index], values[index]));
    }

    private static string? ReadCameraSemantic(InstructionArgument argument)
        => argument.Sem == "camera"
            ? argument.SemArg
            : argument.Sem?.StartsWith("camera:", StringComparison.Ordinal) == true
                ? argument.Sem[7..]
                : null;

    private static string DescribeSemantic(string semantic)
        => semantic.Replace('-', ' ');

    private static bool TryFormatVector3(
        IReadOnlyList<InstructionArgument> arguments,
        Vector3 value,
        out string[] values)
    {
        if (arguments.Count != 3
            || arguments.Any(argument => argument.Kind != "scalar" || argument.Type != "f32"))
        {
            values = Array.Empty<string>();
            return false;
        }
        values = new[] { Format(value.X), Format(value.Y), Format(value.Z) };
        return true;
    }

    private static bool TryFormatFloat(
        IReadOnlyList<InstructionArgument> arguments,
        float value,
        out string[] values)
    {
        if (arguments.Count != 1
            || arguments[0].Kind != "scalar"
            || arguments[0].Type != "f32")
        {
            values = Array.Empty<string>();
            return false;
        }
        values = new[] { Format(value) };
        return true;
    }

    private static string Format(float value)
        => value.ToString("R", CultureInfo.InvariantCulture);
}
