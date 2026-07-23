using System.Buffers.Binary;
using System.Numerics;
using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

internal sealed record ScriptCameraState(
    Vector3? Position = null,
    Vector3? Target = null,
    Vector3? Forward = null,
    float? Distance = null,
    float? VerticalFieldOfViewDegrees = null,
    float? YawDegrees = null,
    float? PitchDegrees = null,
    float? RollDegrees = null,
    int AngleDurationMs = 0,
    bool UseShortestPath = true,
    int TargetDurationMs = 0,
    Vector3? TargetOffset = null,
    Vector3? PositionOffset = null)
{
    public bool HasViewValue => Position is not null || Target is not null || Forward is not null
        || Distance is not null || VerticalFieldOfViewDegrees is not null
        || YawDegrees is not null || PitchDegrees is not null || RollDegrees is not null;
}

internal static class ScriptCameraStateResolver
{
    public static ScriptCameraState Resolve(DecompiledFunction function, int selectedInstructionIndex)
    {
        ArgumentNullException.ThrowIfNull(function);
        var path = FindFirstPath(function, selectedInstructionIndex);
        var state = new ScriptCameraState();
        foreach (var instructionIndex in path)
            state = Apply(function.Instructions[instructionIndex], state);
        return state;
    }

    private static IReadOnlyList<int> FindFirstPath(DecompiledFunction function, int target)
    {
        if (target < 0 || target >= function.Instructions.Count) return Array.Empty<int>();
        var path = new List<int>();
        var visited = new HashSet<int>();
        return Visit(0) ? path : Array.Empty<int>();

        bool Visit(int index)
        {
            if (index < 0 || index >= function.Instructions.Count || !visited.Add(index)) return false;
            path.Add(index);
            if (index == target) return true;
            foreach (var successor in Successors(function, index))
                if (Visit(successor)) return true;
            path.RemoveAt(path.Count - 1);
            return false;
        }
    }

    private static IEnumerable<int> Successors(DecompiledFunction function, int index)
    {
        var instruction = function.Instructions[index];
        var fallthrough = index + 1;
        var localTargets = instruction.Jumps
            .Where(value => value.TargetFunctionIndex == function.Index && value.TargetInstructionIndex >= 0)
            .Select(value => value.TargetInstructionIndex)
            .Distinct()
            .ToArray();
        if (instruction.Opcode == 3)
        {
            foreach (var target in localTargets) yield return target;
            yield break;
        }
        // Conditional control flow uses the sequential branch first. This is the
        // documented deterministic policy until expression evaluation is available.
        if (fallthrough < function.Instructions.Count) yield return fallthrough;
        foreach (var target in localTargets)
            if (target != fallthrough) yield return target;
    }

    private static ScriptCameraState Apply(DecompiledInstruction instruction, ScriptCameraState state)
    {
        state = ApplySemanticArguments(instruction.Arguments, state);
        if (instruction.Opcode != 45) return state;

        var args = instruction.Arguments;
        var name = instruction.Name;

        // Camera_SetAngles : selector 4
        if (name.Equals("Camera_SetAngles", StringComparison.OrdinalIgnoreCase))
        {
            var floats = args.Where(a => a.Kind == "scalar" && a.Type == "f32").Select(a => (float)a.FloatValue).ToArray();
            if (floats.Length >= 3)
            {
                var pitch = floats[0];
                var yaw = floats[1];
                var roll = floats[2];
                var durMs = args.LastOrDefault(a => a.Type == "s16")?.Raw is { Length: >= 2 } r
                    ? r[0] | (r[1] << 8) : 0;
                var shortestPath = args.LastOrDefault(a => a.Type == "u8" && a.Name == "shortest_path_flag")?.Raw is { Length: >= 1 } f
                    ? f[0] != 0 : true;
                return state with { PitchDegrees = pitch, YawDegrees = yaw,
                    RollDegrees = roll, AngleDurationMs = durMs, UseShortestPath = shortestPath };
            }
        }

        // Camera_LookAtPosition : selector 2 — 3×f32 position + s16 duration
        if (name.Equals("Camera_LookAtPosition", StringComparison.OrdinalIgnoreCase))
        {
            var floats = args.Where(a => a.Kind == "scalar" && a.Type == "f32").Select(a => (float)a.FloatValue).ToArray();
            if (floats.Length >= 3)
            {
                var target = new Vector3(floats[0], floats[1], floats[2]);
                var durMs = args.LastOrDefault(a => a.Type == "s16")?.Raw is { Length: >= 2 } r
                    ? r[0] | (r[1] << 8) : 0;
                return state with { Target = target, TargetDurationMs = durMs };
            }
        }

        // Camera_LookAtEntityNode : selector 3 — target_id + node_name + offset vec3 + duration
        if (name.Equals("Camera_LookAtEntityNode", StringComparison.OrdinalIgnoreCase))
        {
            var floats = args.Where(a => a.Kind == "scalar" && a.Type == "f32").Select(a => (float)a.FloatValue).ToArray();
            if (floats.Length >= 3)
            {
                var target = new Vector3(floats[0], floats[1], floats[2]);
                var durMs = args.LastOrDefault(a => a.Type == "s16")?.Raw is { Length: >= 2 } r
                    ? r[0] | (r[1] << 8) : 0;
                return state with { Target = target, TargetDurationMs = durMs };
            }
        }

        // Camera_LookAtEntityNode_Local : selector 20 — id + node + offset vec3 + duration
        if (name.Equals("Camera_LookAtEntityNode_Local", StringComparison.OrdinalIgnoreCase))
        {
            var floats = args.Where(a => a.Kind == "scalar" && a.Type == "f32").Select(a => (float)a.FloatValue).ToArray();
            if (floats.Length >= 3)
            {
                var target = new Vector3(floats[0], floats[1], floats[2]);
                var durMs = args.LastOrDefault(a => a.Type == "s16")?.Raw is { Length: >= 2 } r
                    ? r[0] | (r[1] << 8) : 0;
                return state with { Target = target, TargetDurationMs = durMs };
            }
        }

        // Camera_SetDistance : selector 5 — already handled by semantic args

        // CameraSetTarget_Relative : selector 12 - offset vec3 + duration
        if (name.Equals("CameraSetTarget_Relative", StringComparison.OrdinalIgnoreCase))
        {
            var floats = args.Where(a => a.Kind == "scalar" && a.Type == "f32").Select(a => (float)a.FloatValue).ToArray();
            if (floats.Length >= 3)
            {
                var offset = new Vector3(floats[0], floats[1], floats[2]);
                var durMs = args.LastOrDefault(a => a.Type == "s16")?.Raw is { Length: >= 2 } r
                    ? r[0] | (r[1] << 8) : 0;
                return state with { TargetOffset = offset, TargetDurationMs = durMs };
            }
        }

        if (name.Equals("CameraSetEye_Relative", StringComparison.OrdinalIgnoreCase))
        {
            var floats = args.Where(a => a.Kind == "scalar" && a.Type == "f32").Select(a => (float)a.FloatValue).ToArray();
            if (floats.Length >= 3)
            {
                var offset = new Vector3(floats[0], floats[1], floats[2]);
                var durMs = args.LastOrDefault(a => a.Type == "s16")?.Raw is { Length: >= 2 } r
                    ? r[0] | (r[1] << 8) : 0;
                return state with { PositionOffset = offset, TargetDurationMs = durMs };
            }
        }

        if (name.Equals("CameraLookAtMidpoint", StringComparison.OrdinalIgnoreCase))
        {
            var floats = args.Where(a => a.Kind == "scalar" && a.Type == "f32").Select(a => (float)a.FloatValue).ToArray();
            var floatVal = floats.Length > 0 ? floats[0] : 0f;
            var durMs = args.LastOrDefault(a => a.Type == "s16")?.Raw is { Length: >= 2 } r
                ? r[0] | (r[1] << 8) : 0;
            var midpointTarget = new Vector3(0, floatVal, 0);
            return state with { Target = midpointTarget, TargetDurationMs = durMs };
        }

        // Camera_SetFOV : selector 11 — f32 fov
        if (name.Equals("Camera_SetFOV", StringComparison.OrdinalIgnoreCase))
        {
            var fovFloat = args.FirstOrDefault(a => a.Kind == "scalar" && a.Type == "f32");
            if (fovFloat is not null && float.IsFinite((float)fovFloat.FloatValue))
                return state with { VerticalFieldOfViewDegrees = (float)fovFloat.FloatValue };
        }

        return state;
    }

    private static ScriptCameraState ApplySemanticArguments(
        IReadOnlyList<InstructionArgument> arguments,
        ScriptCameraState state)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var first = arguments[index];
            var span = Math.Max(1, first.SemSpan);
            var group = arguments.Skip(index).Take(span).ToArray();
            var semantic = first.Sem == "camera" ? first.SemArg : first.Sem?.StartsWith("camera:") == true
                ? first.Sem[7..]
                : null;
            if (semantic is not null)
            {
                if (semantic is "position" or "pos" && TryReadVector3(group, out var position))
                    state = state with { Position = position };
                else if (semantic == "target" && TryReadVector3(group, out var target))
                    state = state with { Target = target };
                else if (semantic == "forward" && TryReadVector3(group, out var forward))
                    state = state with { Forward = forward };
                else if (semantic == "distance" && TryReadFloat(group, out var distance))
                    state = state with { Distance = distance };
                else if (semantic is "fov" or "fov-degrees" && TryReadFloat(group, out var fov))
                    state = state with { VerticalFieldOfViewDegrees = fov };
                else if (semantic == "yaw-degrees" && TryReadFloat(group, out var yaw))
                    state = state with { YawDegrees = yaw };
                else if (semantic == "pitch-degrees" && TryReadFloat(group, out var pitch))
                    state = state with { PitchDegrees = pitch };
            }
            index += group.Length - 1;
        }
        return state;
    }

    private static bool TryReadDistance(IReadOnlyList<InstructionArgument> arguments, out float value)
    {
        var typed = arguments.FirstOrDefault(argument => argument.Kind == "scalar" && argument.Type == "f32");
        if (typed is not null)
        {
            value = (float)typed.FloatValue;
            return float.IsFinite(value) && value > 0f;
        }
        var raw = arguments.FirstOrDefault(argument => argument.Kind == "bytes" && argument.Raw.Length == 7)?.Raw;
        if (raw is not null)
        {
            value = ReadSingle(raw, 1);
            return float.IsFinite(value) && value > 0f;
        }
        value = 0f;
        return false;
    }

    private static bool TryReadPosition(
        IReadOnlyList<InstructionArgument> arguments,
        int packedLength,
        int packedOffset,
        out Vector3 value)
    {
        var floats = arguments.Where(argument => argument.Kind == "scalar" && argument.Type == "f32").ToArray();
        if (floats.Length >= 3)
        {
            value = new Vector3((float)floats[0].FloatValue, (float)floats[1].FloatValue, (float)floats[2].FloatValue);
            return IsFinite(value);
        }
        var raw = arguments.FirstOrDefault(argument =>
            argument.Kind == "bytes" && argument.Raw.Length == packedLength)?.Raw;
        if (raw is not null)
        {
            value = new Vector3(
                ReadSingle(raw, packedOffset),
                ReadSingle(raw, packedOffset + 4),
                ReadSingle(raw, packedOffset + 8));
            return IsFinite(value);
        }
        value = default;
        return false;
    }

    private static bool TryReadEulerAngles(
        IReadOnlyList<InstructionArgument> arguments,
        out float pitchDegrees,
        out float yawDegrees,
        out float rollDegrees,
        out int durationMs,
        out bool useShortestPath)
    {
        useShortestPath = true;
        durationMs = 0;
        var floats = arguments.Where(argument =>
            argument.Kind == "scalar" && argument.Type == "f32").ToArray();
        if (floats.Length >= 3)
        {
            pitchDegrees = (float)floats[0].FloatValue;
            yawDegrees = (float)floats[1].FloatValue;
            rollDegrees = (float)floats[2].FloatValue;
            return float.IsFinite(pitchDegrees) && float.IsFinite(yawDegrees);
        }
        if (floats.Length >= 2)
        {
            pitchDegrees = (float)floats[0].FloatValue;
            yawDegrees = (float)floats[1].FloatValue;
            rollDegrees = 0f;
            return float.IsFinite(pitchDegrees) && float.IsFinite(yawDegrees);
        }

        // Raw: [EaseFlag_8] [Pitch_32f] [Yaw_32f] [Roll_32f] [Duration_16ms] [ShortestPathFlag_8]
        var raw = arguments.FirstOrDefault(argument =>
            argument.Kind == "bytes" && argument.Raw.Length >= 16)?.Raw;
        if (raw is not null)
        {
            pitchDegrees = ReadSingle(raw, 1);
            yawDegrees = ReadSingle(raw, 5);
            rollDegrees = ReadSingle(raw, 9);
            durationMs = raw[13] | (raw[14] << 8);
            useShortestPath = raw[15] != 0;
            return float.IsFinite(pitchDegrees) && float.IsFinite(yawDegrees);
        }
        pitchDegrees = 0f;
        yawDegrees = 0f;
        rollDegrees = 0f;
        return false;
    }

    private static bool TryReadTargetWithDuration(
        IReadOnlyList<InstructionArgument> arguments,
        int packedLength,
        int packedOffset,
        out Vector3 target,
        out int durationMs)
    {
        durationMs = 0;
        target = default;
        var floats = arguments.Where(a => a.Kind == "scalar" && a.Type == "f32").ToArray();
        if (floats.Length >= 3)
        {
            target = new Vector3((float)floats[0].FloatValue, (float)floats[1].FloatValue, (float)floats[2].FloatValue);
            return IsFinite(target);
        }
        var raw = arguments.FirstOrDefault(a => a.Kind == "bytes" && a.Raw.Length == packedLength)?.Raw;
        if (raw is not null && packedOffset + 14 <= raw.Length)
        {
            target = new Vector3(
                ReadSingle(raw, packedOffset),
                ReadSingle(raw, packedOffset + 4),
                ReadSingle(raw, packedOffset + 8));
            durationMs = raw[packedOffset + 12] | (raw[packedOffset + 13] << 8);
            return IsFinite(target);
        }
        return false;
    }

    private static bool TryReadScalarOrPackedFloat(
        IReadOnlyList<InstructionArgument> arguments,
        int packedLength,
        int packedOffset,
        out float value)
    {
        var scalar = arguments.FirstOrDefault(argument =>
            argument.Kind == "scalar" && argument.Type == "f32");
        if (scalar is not null)
        {
            value = (float)scalar.FloatValue;
            return float.IsFinite(value);
        }
        var raw = arguments.FirstOrDefault(argument =>
            argument.Kind == "bytes" && argument.Raw.Length == packedLength)?.Raw;
        if (raw is not null)
        {
            value = ReadSingle(raw, packedOffset);
            return float.IsFinite(value);
        }
        value = 0f;
        return false;
    }

    private static bool TryReadVector3(IReadOnlyList<InstructionArgument> arguments, out Vector3 value)
    {
        if (arguments.Count == 3 && arguments.All(argument => argument.Kind == "scalar" && argument.Type == "f32"))
        {
            value = new Vector3((float)arguments[0].FloatValue, (float)arguments[1].FloatValue, (float)arguments[2].FloatValue);
            return IsFinite(value);
        }
        value = default;
        return false;
    }

    private static bool TryReadFloat(IReadOnlyList<InstructionArgument> arguments, out float value)
    {
        if (arguments.Count == 1 && arguments[0].Kind == "scalar" && arguments[0].Type == "f32")
        {
            value = (float)arguments[0].FloatValue;
            return float.IsFinite(value);
        }
        value = 0f;
        return false;
    }

    private static float ReadSingle(byte[] bytes, int offset)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)));

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

internal sealed record ScriptCameraByteUpdate(int ArgumentIndex, byte[] Value, string Component);

internal static class ScriptCameraInstructionCodec
{
    public static IReadOnlyList<ScriptCameraByteUpdate> Capture(
        DecompiledInstruction instruction,
        ScriptCameraSnapshot snapshot)
    {
        var updates = new List<ScriptCameraByteUpdate>();
        if (instruction.Opcode != 45) return updates;
        if (instruction.Name.Equals("OP45_2", StringComparison.OrdinalIgnoreCase))
        {
            var argument = instruction.Arguments.FirstOrDefault(value => value.Kind == "bytes" && value.Raw.Length == 15);
            if (argument is not null)
            {
                var bytes = argument.Raw.ToArray();
                WriteSingle(bytes, 1, snapshot.Position.X);
                WriteSingle(bytes, 5, snapshot.Position.Y);
                WriteSingle(bytes, 9, snapshot.Position.Z);
                updates.Add(new ScriptCameraByteUpdate(argument.Index, bytes, "position"));
            }
        }
        else if (instruction.Name.Equals("OP45_4", StringComparison.OrdinalIgnoreCase))
        {
            var argument = instruction.Arguments.FirstOrDefault(value => value.Kind == "bytes" && value.Raw.Length == 16);
            if (argument is not null)
            {
                var bytes = argument.Raw.ToArray();
                WriteSingle(bytes, 1, snapshot.PitchDegrees);
                WriteSingle(bytes, 5, snapshot.YawDegrees);
                updates.Add(new ScriptCameraByteUpdate(argument.Index, bytes, "orientation"));
            }
        }
        else if (instruction.Name.Equals("OP45_5", StringComparison.OrdinalIgnoreCase))
        {
            var argument = instruction.Arguments.FirstOrDefault(value => value.Kind == "bytes" && value.Raw.Length == 7);
            if (argument is not null)
            {
                var bytes = argument.Raw.ToArray();
                WriteSingle(bytes, 1, snapshot.Distance);
                updates.Add(new ScriptCameraByteUpdate(argument.Index, bytes, "distance"));
            }
        }
        else if (instruction.Name.Equals("OP45_11", StringComparison.OrdinalIgnoreCase))
        {
            var argument = instruction.Arguments.FirstOrDefault(value => value.Kind == "bytes" && value.Raw.Length == 7);
            if (argument is not null)
            {
                var bytes = argument.Raw.ToArray();
                WriteSingle(bytes, 1, snapshot.VerticalFieldOfViewDegrees);
                updates.Add(new ScriptCameraByteUpdate(argument.Index, bytes, "field of view"));
            }
        }
        else if (instruction.Name.Equals("OP45_20", StringComparison.OrdinalIgnoreCase))
        {
            var argument = instruction.Arguments.FirstOrDefault(value => value.Kind == "bytes" && value.Raw.Length == 14);
            if (argument is not null)
            {
                var bytes = argument.Raw.ToArray();
                WriteSingle(bytes, 0, snapshot.Position.X);
                WriteSingle(bytes, 4, snapshot.Position.Y);
                WriteSingle(bytes, 8, snapshot.Position.Z);
                updates.Add(new ScriptCameraByteUpdate(argument.Index, bytes, "position"));
            }
        }
        return updates;
    }

    private static void WriteSingle(byte[] bytes, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));
}
