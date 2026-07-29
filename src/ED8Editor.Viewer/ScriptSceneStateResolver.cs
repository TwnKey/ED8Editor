using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

internal sealed record ScriptEntityState(
    int EntityId,
    string AssetId,
    string DisplayName,
    string InitialAnimation,
    int EntityType,
    int Flags,
    Vector3 Position,
    float YawDegrees,
    float Scale,
    float CollisionHeight,
    float CollisionRadius,
    string ScriptFile,
    string InitFunction,
    int ScriptArgument,
    int UnknownBehavior,
    int UnknownParameter1,
    int UnknownParameter2,
    int UnknownParameter3,
    IReadOnlyList<Vector3> PendingWaypoints,
    ScriptEntityMotion? Motion = null,
    IReadOnlyDictionary<int, ScriptEntityAnimation>? AnimationSlots = null,
    IReadOnlyDictionary<string, ScriptEntityAttachment>? Attachments = null,
    IReadOnlyDictionary<int, string>? EffectSlots = null,
    IReadOnlyDictionary<int, ScriptEffectInstance>? Effects = null,
    bool HasSpawnDefinition = true,
    bool HasPosition = true,
    string ReferenceSymbol = "",
    bool IsPlaceholder = false,
    bool IsExecutable = true,
    IReadOnlyList<string>? AnimationBanks = null,
    string FacialAssetId = "",
    ScriptFacialExpression? FacialExpression = null);

/// <summary>
/// A playing effect. OP39 selector 10 loads an .eff into one of an owner's
/// slots, selector 12 starts it as a numbered instance — anchored to an entity
/// and one of its nodes, or placed in the world — and selectors 11/13/14/16 take
/// it down. An instance with no scripted end keeps playing.
/// </summary>
internal sealed record ScriptEffectInstance(
    int Instance,
    int Slot,
    string EffectPath,
    int AnchorEntityId,
    string AnchorNode,
    Vector3 Position,
    Vector3 RotationDegrees,
    Vector3 Scale,
    int StartFrame,
    ScriptEffectSpace Space = ScriptEffectSpace.World);

internal enum ScriptEffectSpace
{
    World,
    Camera,
}

/// <summary>
/// Something hanging from one of an actor's skeleton nodes. OP37 attaches a model
/// to a node (selector 0) or clears it (selector 1) with a local placement, and
/// OP32_0 shows or hides whatever hangs there — that is how a script draws a
/// weapon, puts it away, or swaps it for an umbrella.
/// </summary>
internal sealed record ScriptEntityAttachment(
    string AttachPoint,
    string ModelAssetId,
    bool Visible,
    Vector3 Offset,
    Vector3 RotationDegrees,
    Vector3 Scale);

internal sealed record ScriptEntityMotion(
    int Subtype,
    float Speed,
    int AnimationState,
    int Flags,
    IReadOnlyList<Vector3> Path,
    int StartFrame,
    int DurationFrames,
    float JumpHeight = 0f)
{
    public int EndFrame => checked(StartFrame + DurationFrames);

    /// <summary>
    /// Facing while the path is walked. The engine steers a moving actor along
    /// its own movement (it stores the normalised direction and turns towards
    /// it), so the heading follows the segment being travelled and stays on the
    /// last one once the move is over.
    /// </summary>
    public float? HeadingAt(float frame)
    {
        if (Path.Count < 2) return null;
        var progress = DurationFrames <= 0
            ? 1f
            : Math.Clamp((frame - StartFrame) / DurationFrames, 0f, 1f);
        var totalLength = 0f;
        for (var index = 1; index < Path.Count; index++)
            totalLength += Vector3.Distance(Path[index - 1], Path[index]);
        if (totalLength <= 0f) return null;
        var remaining = totalLength * progress;
        for (var index = 1; index < Path.Count; index++)
        {
            var segment = Path[index] - Path[index - 1];
            var segmentLength = segment.Length();
            if (remaining > segmentLength && index < Path.Count - 1)
            {
                remaining -= segmentLength;
                continue;
            }
            return segmentLength <= 1e-4f
                ? null
                : MathF.Atan2(segment.X, segment.Z) * 180f / MathF.PI;
        }
        return null;
    }

    public Vector3 PositionAt(float frame)
    {
        if (Path.Count == 0) return Vector3.Zero;
        if (Path.Count == 1 || DurationFrames <= 0) return Path[^1];
        var progress = Math.Clamp((frame - StartFrame) / DurationFrames, 0f, 1f);
        var totalLength = 0f;
        for (var index = 1; index < Path.Count; index++)
            totalLength += Vector3.Distance(Path[index - 1], Path[index]);
        if (totalLength <= 0f) return Path[^1];
        var remaining = totalLength * progress;
        for (var index = 1; index < Path.Count; index++)
        {
            var start = Path[index - 1];
            var end = Path[index];
            var segmentLength = Vector3.Distance(start, end);
            if (remaining <= segmentLength)
            {
                var position = segmentLength > 0f
                    ? Vector3.Lerp(start, end, remaining / segmentLength)
                    : end;
                return JumpHeight == 0f
                    ? position
                    : position + Vector3.UnitY
                        * (4f * MathF.Abs(JumpHeight) * progress * (1f - progress));
            }
            remaining -= segmentLength;
        }
        return Path[^1];
    }
}

internal sealed record ScriptEntityAnimation(
    int Slot,
    string Name,
    bool Loop,
    int Flag2,
    int Flag3,
    int Flag4,
    int Flag5,
    float BlendTime,
    float TimeParameter1,
    float TimeParameter2,
    float TimeParameter3,
    int StartFrame,
    bool HoldFinalFrame = false);

internal sealed record ScriptPropAnimation(
    string PropName,
    string AnimationName,
    int StartFrame,
    bool HoldFinalFrame);

internal sealed record UnresolvedScriptCall(
    int CallerFunctionIndex,
    int InstructionIndex,
    int Variant,
    string FunctionName);

internal sealed record ScriptSceneState(
    ScriptCameraState Camera,
    IReadOnlyDictionary<int, ScriptEntityState> Entities,
    IReadOnlyDictionary<string, ScriptPropAnimation> PropAnimations,
    IReadOnlyList<UnresolvedScriptCall> UnresolvedCalls,
    int? EnvironmentProfile);

internal sealed record ScriptSceneTimelinePoint(
    int Frame,
    int FunctionIndex,
    int InstructionIndex,
    DecompiledInstruction Instruction,
    ScriptSceneState Before,
    ScriptSceneState After,
    int? SubjectEntityId = null,
    bool IsExternalScript = false);

internal sealed record ScriptSceneTimeline(
    string FunctionName,
    ScriptSceneState InitialState,
    IReadOnlyList<ScriptSceneTimelinePoint> Points,
    int DurationFrames,
    bool LoopPlayback = true);

/// <summary>
/// Replays the deterministic first control-flow path used by the editor. Local calls
/// execute synchronously and mutate the same camera/entity state before returning.
/// </summary>
internal static class ScriptSceneStateResolver
{
    private const int MaximumExecutedInstructions = 100_000;
    private const int MaximumCallDepth = 64;
    private static readonly Encoding ScriptEncoding = CreateScriptEncoding();

    public static ScriptSceneState Resolve(
        DecompiledScript script,
        DecompiledFunction selectedFunction,
        int selectedInstructionIndex,
        ScriptAnimationLibrary? animationLibrary = null,
        DecompiledScript? systemScript = null,
        ScriptSubject? subject = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(selectedFunction);

        var execution = new Execution(script, animationLibrary, systemScript, subject);
        execution.LoadInitialEntities(selectedFunction);
        var path = ScriptCameraStateResolver.FindFirstPath(
            selectedFunction, selectedInstructionIndex);
        execution.ExecutePath(
            script,
            selectedFunction,
            path,
            CreateCallStack(selectedFunction));
        return execution.Snapshot();
    }

    public static ScriptSceneState ResolveBefore(
        DecompiledScript script,
        DecompiledFunction selectedFunction,
        int selectedInstructionIndex,
        ScriptAnimationLibrary? animationLibrary = null,
        DecompiledScript? systemScript = null,
        ScriptSubject? subject = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(selectedFunction);

        var path = ScriptCameraStateResolver.FindFirstPath(
            selectedFunction, selectedInstructionIndex);
        var exclusivePath = path.TakeWhile(index => index != selectedInstructionIndex).ToArray();
        var execution = new Execution(script, animationLibrary, systemScript, subject);
        execution.LoadInitialEntities(selectedFunction);
        execution.ExecutePath(
            script,
            selectedFunction,
            exclusivePath,
            CreateCallStack(selectedFunction));
        return execution.Snapshot();
    }

    public static ScriptSceneTimeline? BuildCallTimeline(
        DecompiledScript script,
        DecompiledFunction caller,
        DecompiledInstruction callInstruction,
        ScriptAnimationLibrary? animationLibrary = null,
        DecompiledScript? systemScript = null,
        ScriptSubject? subject = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(callInstruction);
        if (callInstruction.Opcode != 2) return null;

        var execution = new Execution(script, animationLibrary, systemScript, subject);
        if (!execution.TryResolveCallTarget(
                script, callInstruction, out var targetScript, out var target))
        {
            return null;
        }
        execution.LoadInitialEntities(caller);
        var callerPath = ScriptCameraStateResolver.FindFirstPath(caller, callInstruction.Index);
        execution.ExecutePath(
            script,
            caller,
            callerPath.TakeWhile(index => index != callInstruction.Index),
            CreateCallStack(caller));
        // Settle the past first: the snapshot the preview starts from must be the
        // scene as it stands, not as it stood before its last movement.
        execution.BeginTimeline();
        var initialState = execution.Snapshot();
        execution.ExecuteFunction(
            targetScript,
            target,
            CreateCallStack(caller, target));
        return execution.CreateTimeline(target.Name, initialState);
    }

    public static ScriptSceneTimeline? BuildAnimationCallTimeline(
        DecompiledScript script,
        DecompiledFunction caller,
        DecompiledInstruction callInstruction,
        ScriptAnimationLibrary? animationLibrary,
        DecompiledScript? systemScript = null,
        ScriptSubject? subject = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(callInstruction);
        if (callInstruction.Opcode != 47 || animationLibrary is null) return null;

        var execution = new Execution(script, animationLibrary, systemScript, subject);
        execution.LoadInitialEntities(caller);
        var callerPath = ScriptCameraStateResolver.FindFirstPath(caller, callInstruction.Index);
        execution.ExecutePath(
            script,
            caller,
            callerPath.TakeWhile(index => index != callInstruction.Index),
            CreateCallStack(caller));
        if (!execution.CanResolveAnimationCall(callInstruction)) return null;
        // Settle the past first: the snapshot the preview starts from must be the
        // scene as it stands, not as it stood before its last movement.
        execution.BeginTimeline();
        var initialState = execution.Snapshot();
        execution.ExecuteInstructionForTimeline(
            script, caller, callInstruction, CreateCallStack(caller));
        return execution.CreateTimeline(
            Execution.ReadArgumentString(callInstruction.Arguments[2]),
            initialState);
    }

    public static ScriptSceneTimeline? BuildPropAnimationTimeline(
        DecompiledScript script,
        DecompiledFunction caller,
        DecompiledInstruction instruction,
        ScriptAnimationLibrary? animationLibrary = null,
        DecompiledScript? systemScript = null,
        ScriptSubject? subject = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(instruction);
        if (instruction.Opcode != 69 || instruction.Arguments.Count < 2) return null;

        var execution = new Execution(script, animationLibrary, systemScript, subject);
        execution.LoadInitialEntities(caller);
        var callerPath = ScriptCameraStateResolver.FindFirstPath(caller, instruction.Index);
        execution.ExecutePath(
            script,
            caller,
            callerPath.TakeWhile(index => index != instruction.Index),
            CreateCallStack(caller));
        // Settle the past first: the snapshot the preview starts from must be the
        // scene as it stands, not as it stood before its last movement.
        execution.BeginTimeline();
        var initialState = execution.Snapshot();
        execution.ExecuteInstructionForTimeline(
            script, caller, instruction, CreateCallStack(caller));
        return execution.CreateTimeline(
            Execution.ReadArgumentString(instruction.Arguments[1]),
            initialState) with
        {
            // A prop animation command is a one-shot state transition. Once the
            // clip ends, the engine keeps the resulting node transforms until a
            // later prop-animation command replaces them.
            LoopPlayback = false,
        };
    }

    public static ScriptSceneTimeline? BuildMovementTimeline(
        DecompiledScript script,
        DecompiledFunction caller,
        DecompiledInstruction instruction,
        ScriptAnimationLibrary? animationLibrary = null,
        DecompiledScript? systemScript = null,
        ScriptSubject? subject = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(instruction);
        if (instruction.Opcode != 54) return null;

        var execution = new Execution(script, animationLibrary, systemScript, subject);
        execution.LoadInitialEntities(caller);
        var callerPath = ScriptCameraStateResolver.FindFirstPath(caller, instruction.Index);
        execution.ExecutePath(
            script,
            caller,
            callerPath.TakeWhile(index => index != instruction.Index),
            CreateCallStack(caller));
        // Settle the past first: the snapshot the preview starts from must be the
        // scene as it stands, not as it stood before its last movement.
        execution.BeginTimeline();
        var initialState = execution.Snapshot();
        execution.ExecuteInstructionForTimeline(
            script, caller, instruction, CreateCallStack(caller));
        return execution.CreateTimeline(instruction.Name, initialState);
    }

    /// <summary>
    /// Plays a whole function, from its entry to its end: every instruction of
    /// the path the replay takes becomes a timeline point, so the scene runs the
    /// way the script writes it — waits, movements, animations and effects in
    /// their own order. The entities the scene starts with are the ones the
    /// function inherits, exactly as when a single instruction is inspected.
    /// </summary>
    /// <param name="preferredInstruction">
    /// The block the reader is looking at. A fork the script itself decides is
    /// followed as written; one whose condition the replay cannot know used to
    /// fall through blindly, which is how a scene played a branch the reader was
    /// not even looking at. With a block selected, the branch it sits on wins.
    /// </param>
    public static ScriptSceneTimeline? BuildFunctionTimeline(
        DecompiledScript script,
        DecompiledFunction function,
        ScriptAnimationLibrary? animationLibrary = null,
        DecompiledScript? systemScript = null,
        ScriptSubject? subject = null,
        int? preferredInstruction = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(function);
        if (!function.IsCode) return null;

        var execution = new Execution(script, animationLibrary, systemScript, subject)
        {
            PreferredInstruction = preferredInstruction,
            PreferredFunctionIndex = function.Index,
        };
        execution.LoadInitialEntities(function);
        execution.BeginTimeline();
        var initialState = execution.Snapshot();
        execution.ExecuteFunction(script, function, CreateCallStack(function));
        return execution.CreateTimeline(function.Name, initialState);
    }

    private static HashSet<DecompiledFunction> CreateCallStack(
        params DecompiledFunction[] functions)
        => new(functions, ReferenceEqualityComparer.Instance);

    internal static string ReadInstructionString(InstructionArgument argument)
        => Execution.ReadArgumentString(argument);

    internal static void VerifyReplaySmoke(DecompiledScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        VerifySpawnLayoutSmoke();
        VerifyMovementControlSmoke();
        VerifyEnvironmentProfileSmoke();
        VerifySceneEffectPlaybackSmoke();
        var spawnOwner = script.Functions
            .Where(value => value.IsCode)
            .SelectMany(function => function.Instructions
                .Where(instruction => instruction.Opcode == 19)
                .Select(instruction => (Function: function, Instruction: instruction)))
            .FirstOrDefault();
        if (spawnOwner.Instruction is not null)
        {
            var state = Resolve(
                script, spawnOwner.Function, spawnOwner.Instruction.Index);
            var entityId = spawnOwner.Instruction.Arguments[0].IntValue;
            var expectedAsset = Execution.ReadArgumentString(
                spawnOwner.Instruction.Arguments[1]);
            if (!state.Entities.TryGetValue(entityId, out var entity)
                || !entity.AssetId.Equals(expectedAsset, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Script entity replay did not preserve the OP19 entity ID/model pair.");
        }

        var localCall = script.Functions
            .Where(value => value.IsCode)
            .SelectMany(function => function.Instructions
                .Where(instruction => instruction.Opcode == 2)
                .Select(instruction => (Function: function, Instruction: instruction)))
            .FirstOrDefault(pair =>
            {
                var name = Execution.ReadArgumentString(
                    pair.Instruction.Arguments.First(argument => argument.Kind == "string"));
                return script.Functions.Any(candidate => candidate.IsCode && candidate.Name == name);
            });
        if (localCall.Instruction is not null)
        {
            _ = Resolve(script, localCall.Function, localCall.Instruction.Index);
            var timeline = BuildCallTimeline(
                script, localCall.Function, localCall.Instruction);
            if (timeline is null
                || timeline.DurationFrames <= 0
                || timeline.Points.Zip(
                    timeline.Points.Skip(1),
                    (left, right) => left.Frame <= right.Frame).Any(value => !value))
            {
                throw new InvalidOperationException(
                    "Script call preview did not produce a valid monotonic timeline.");
            }
        }
    }

    private static void VerifySpawnLayoutSmoke()
    {
        var spawn = new DecompiledInstruction(
            0,
            0,
            "Entity_Spawn",
            19,
            new InstructionArgument[]
            {
                ScalarArgument(0, "s16", 123, sem: "entity"),
                StringArgument(1, "C_TEST"),
                StringArgument(2, "Test actor"),
                StringArgument(3, "WAIT"),
                ScalarArgument(4, "u8", 2),
                ScalarArgument(5, "s32", 0x1234),
                ScalarArgument(6, "f32", floatValue: 1.25),
                ScalarArgument(7, "f32", floatValue: 2.5),
                ScalarArgument(8, "f32", floatValue: 3.75),
                ScalarArgument(9, "f32", floatValue: 45),
                ScalarArgument(10, "f32", floatValue: 1.5),
                ScalarArgument(11, "f32", floatValue: 1.6),
                ScalarArgument(12, "f32", floatValue: 0.3),
                StringArgument(13, "test_script"),
                StringArgument(14, "Init"),
                ScalarArgument(15, "s32", -1),
                ScalarArgument(16, "u8", 7),
                ScalarArgument(17, "s32", 8),
                ScalarArgument(18, "s32", 9),
                ScalarArgument(19, "s16", 10),
            },
            Array.Empty<JumpTarget>());
        var function = new DecompiledFunction(
            0,
            "Init",
            true,
            new[]
            {
                spawn,
                new DecompiledInstruction(
                    1, 0, "OP1", 1,
                    Array.Empty<InstructionArgument>(),
                    Array.Empty<JumpTarget>()),
            });
        var script = new DecompiledScript("SpawnLayoutSmoke", new[] { function });
        var state = Resolve(script, function, spawn.Index);
        var entity = state.Entities[123];
        if (entity.AssetId != "C_TEST"
            || entity.DisplayName != "Test actor"
            || entity.InitialAnimation != "WAIT"
            || entity.EntityType != 2
            || entity.Flags != 0x1234
            || Vector3.Distance(entity.Position, new Vector3(1.25f, 2.5f, 3.75f)) > 0.0001f
            || Math.Abs(entity.YawDegrees - 45f) > 0.0001f
            || Math.Abs(entity.Scale - 1.5f) > 0.0001f
            || Math.Abs(entity.CollisionHeight - 1.6f) > 0.0001f
            || Math.Abs(entity.CollisionRadius - 0.3f) > 0.0001f
            || entity.ScriptFile != "test_script"
            || entity.InitFunction != "Init"
            || entity.ScriptArgument != -1
            || entity.UnknownBehavior != 7
            || entity.UnknownParameter1 != 8
            || entity.UnknownParameter2 != 9
            || entity.UnknownParameter3 != 10)
        {
            throw new InvalidOperationException(
                "Entity_Spawn operands were not mapped to the verified OP19 layout.");
        }
    }

    private static void VerifyEnvironmentProfileSmoke()
    {
        var setNight = new DecompiledInstruction(
            0,
            0,
            "Environment_SetProfile",
            8,
            new[]
            {
                new InstructionArgument(
                    0,
                    "expr",
                    "expr",
                    0,
                    0,
                    Array.Empty<byte>(),
                    new[]
                    {
                        new ExprElement(0x00, "value", "push 2", 2, null),
                        new ExprElement(0x13, "operator", "nop", 0, null),
                        new ExprElement(0x01, "operator", "END", 0, null),
                    },
                    Name: "environment_profile"),
            },
            Array.Empty<JumpTarget>());
        var function = new DecompiledFunction(
            0,
            "EnvironmentSmoke",
            true,
            new[]
            {
                setNight,
                new DecompiledInstruction(
                    1, 0, "Return", 1,
                    Array.Empty<InstructionArgument>(),
                    Array.Empty<JumpTarget>()),
            });
        var state = Resolve(
            new DecompiledScript("EnvironmentSmoke", new[] { function }),
            function,
            setNight.Index);
        if (state.EnvironmentProfile != 2)
        {
            throw new InvalidOperationException(
                "Environment_SetProfile did not preserve the script profile value.");
        }
    }

    private static void VerifySceneEffectPlaybackSmoke()
    {
        const int sceneOwnerId = -3;
        const int rainSlot = 201;
        var load = new DecompiledInstruction(
            0,
            0,
            "Effect_LoadSlot",
            39,
            new InstructionArgument[]
            {
                ScalarArgument(0, "s16", sceneOwnerId, sem: "entity"),
                ScalarArgument(1, "s32", rainSlot),
                StringArgument(2, "system/rain00.eff"),
            },
            Array.Empty<JumpTarget>());
        var play = new DecompiledInstruction(
            1,
            0,
            "SceneEffect_PlayOrStop",
            73,
            new InstructionArgument[]
            {
                ScalarArgument(0, "u8", 0),
                ScalarArgument(1, "s32", rainSlot),
            },
            Array.Empty<JumpTarget>());
        var stop = new DecompiledInstruction(
            2,
            0,
            "SceneEffect_PlayOrStop",
            73,
            new InstructionArgument[]
            {
                ScalarArgument(0, "u8", 1),
                ScalarArgument(1, "s32", rainSlot),
            },
            Array.Empty<JumpTarget>());
        var function = new DecompiledFunction(
            0,
            "WeatherSmoke",
            true,
            new[]
            {
                load,
                play,
                stop,
                new DecompiledInstruction(
                    3, 0, "Return", 1,
                    Array.Empty<InstructionArgument>(),
                    Array.Empty<JumpTarget>()),
            });
        var script = new DecompiledScript("WeatherSmoke", new[] { function });

        var playing = Resolve(script, function, play.Index);
        if (!playing.Entities.TryGetValue(sceneOwnerId, out var sceneOwner)
            || sceneOwner.Effects is not { Count: 1 } effects
            || effects.Values.Single() is not { } rain
            || rain.Slot != rainSlot
            || rain.EffectPath != "system/rain00.eff"
            || rain.Space != ScriptEffectSpace.Camera)
        {
            throw new InvalidOperationException(
                "SceneEffect_PlayOrStop did not start the loaded global rain effect.");
        }

        var stopped = Resolve(script, function, stop.Index);
        if (stopped.Entities.TryGetValue(sceneOwnerId, out sceneOwner)
            && sceneOwner.Effects?.Values.Any(
                effect => effect.Space == ScriptEffectSpace.Camera) == true)
        {
            throw new InvalidOperationException(
                "SceneEffect_PlayOrStop did not stop the global scene effect.");
        }
    }

    private static void VerifyMovementControlSmoke()
    {
        const int entityId = 7;
        var waitFunction = CreateMovementSmokeFunction(
            new DecompiledInstruction(
                3, 0, "Entity_WaitMovement", 55,
                new[] { ScalarArgument(0, "s16", entityId, sem: "entity") },
                Array.Empty<JumpTarget>()));
        var waitScript = new DecompiledScript("MovementWaitSmoke", new[] { waitFunction });
        var waitExecution = new Execution(waitScript, null);
        waitExecution.BeginTimeline();
        waitExecution.ExecuteFunction(
            waitScript,
            waitFunction,
            CreateCallStack(waitFunction));
        var waitTimeline = waitExecution.CreateTimeline(waitFunction.Name, waitExecution.Snapshot());
        if (waitTimeline.DurationFrames != 360
            || waitTimeline.Points.Single(value =>
                    value.Instruction.Name == "Entity_WaitMovement").Frame != 0)
        {
            throw new InvalidOperationException(
                "Entity_WaitMovement did not wait for the active movement duration.");
        }

        var stopInstruction = new DecompiledInstruction(
            4, 0, "Entity_StopMovement", 55,
            new[] { ScalarArgument(0, "s16", entityId, sem: "entity") },
            Array.Empty<JumpTarget>());
        var stopFunction = CreateMovementSmokeFunction(
            new DecompiledInstruction(
                3, 0, "OP16", 16,
                // OP16 is authored in milliseconds. Two seconds advances the
                // 60 Hz preview by 120 frames, one third of this six-second move.
                new[] { ScalarArgument(0, "u16", 2000) },
                Array.Empty<JumpTarget>()),
            stopInstruction);
        var stopScript = new DecompiledScript("MovementStopSmoke", new[] { stopFunction });
        var stopExecution = new Execution(stopScript, null);
        stopExecution.BeginTimeline();
        stopExecution.ExecuteFunction(
            stopScript,
            stopFunction,
            CreateCallStack(stopFunction));
        var stopTimeline = stopExecution.CreateTimeline(stopFunction.Name, stopExecution.Snapshot());
        var stopped = stopTimeline.Points
            .Single(value => value.Instruction.Name == "Entity_StopMovement")
            .After.Entities[entityId];
        if (stopped.Motion is not null
            || Vector3.Distance(stopped.Position, new Vector3(2f, 0f, 0f)) > 0.0001f)
        {
            throw new InvalidOperationException(
                "Entity_StopMovement did not freeze the entity at its interpolated position.");
        }

        var wanderFunction = new DecompiledFunction(
            0,
            "Init",
            true,
            new DecompiledInstruction[]
            {
                new(
                    0, 0x120, "Entity_Wander", 56,
                    new[]
                    {
                        ScalarArgument(0, "s16", entityId, sem: "entity"),
                        ScalarArgument(1, "f32", floatValue: 10f),
                        ScalarArgument(2, "f32", floatValue: 2f),
                        ScalarArgument(3, "f32", floatValue: 20f),
                        ScalarArgument(4, "f32", floatValue: 3f),
                        ScalarArgument(5, "f32", floatValue: 1.5f),
                    },
                    Array.Empty<JumpTarget>()),
                new(
                    1, 0, "Entity_WaitMovement", 55,
                    new[] { ScalarArgument(0, "s16", entityId, sem: "entity") },
                    Array.Empty<JumpTarget>()),
                new(
                    2, 0, "OP1", 1,
                    Array.Empty<InstructionArgument>(),
                    Array.Empty<JumpTarget>()),
            });
        var wanderScript = new DecompiledScript("WanderSmoke", new[] { wanderFunction });
        var wanderExecution = new Execution(wanderScript, null);
        wanderExecution.BeginTimeline();
        wanderExecution.ExecuteFunction(
            wanderScript,
            wanderFunction,
            CreateCallStack(wanderFunction));
        var wanderTimeline = wanderExecution.CreateTimeline(
            wanderFunction.Name, wanderExecution.Snapshot());
        var wander = wanderTimeline.Points
            .Single(value => value.Instruction.Name == "Entity_Wander")
            .After.Entities[entityId];
        var wanderCenter = new Vector3(10f, 2f, 20f);
        if (wander.Motion is not { AnimationState: 1, Flags: 4 } wanderMotion
            || Vector3.Distance(wander.Position, wanderCenter) > 3.0001f
            || Math.Abs(wander.Position.Y - wanderCenter.Y) > 0.0001f
            || wanderTimeline.DurationFrames < wanderMotion.DurationFrames)
        {
            throw new InvalidOperationException(
                "Entity_Wander did not create a bounded walking movement.");
        }

        var modeFunction = new DecompiledFunction(
            0,
            "Init",
            true,
            new DecompiledInstruction[]
            {
                new(
                    0, 0, "Entity_SetPosition", 46,
                    new[]
                    {
                        ScalarArgument(0, "s16", entityId, sem: "entity"),
                        ScalarArgument(1, "f32", floatValue: 10f),
                        ScalarArgument(2, "f32", floatValue: 0f),
                        ScalarArgument(3, "f32", floatValue: 0f),
                        ScalarArgument(4, "f32", floatValue: 0f),
                    },
                    Array.Empty<JumpTarget>()),
                new(
                    1, 0, "Entity_MoveByMode", 54,
                    new[]
                    {
                        ScalarArgument(0, "s16", entityId, sem: "entity"),
                        ScalarArgument(1, "u16", ushort.MaxValue - 1),
                        ScalarArgument(2, "f32", floatValue: 1f),
                        ScalarArgument(3, "f32", floatValue: 2f),
                        ScalarArgument(4, "f32", floatValue: 3f),
                        ScalarArgument(5, "f32", floatValue: 1.5f),
                        ScalarArgument(6, "u8", 2),
                        ScalarArgument(7, "u16", 0),
                    },
                    Array.Empty<JumpTarget>()),
                new(
                    2, 0, "OP1", 1,
                    Array.Empty<InstructionArgument>(),
                    Array.Empty<JumpTarget>()),
            });
        var modeScript = new DecompiledScript("ModeMovementSmoke", new[] { modeFunction });
        var modeExecution = new Execution(modeScript, null);
        modeExecution.BeginTimeline();
        modeExecution.ExecuteFunction(
            modeScript, modeFunction, CreateCallStack(modeFunction));
        var moved = modeExecution.Snapshot().Entities[entityId];
        var movementPoint = modeExecution.CreateTimeline(
                modeFunction.Name, modeExecution.Snapshot())
            .Points.Single(value => value.Instruction.Opcode == 54);
        if (Vector3.Distance(moved.Position, new Vector3(11f, 2f, 3f)) > 0.0001f
            || moved.Motion is not { AnimationState: 2, Speed: 1.5f }
            || moved.AnimationSlots is null
            || !moved.AnimationSlots.TryGetValue(0, out var locomotion)
            || !locomotion.Name.Equals("RUN", StringComparison.Ordinal)
            || !movementPoint.After.Entities.TryGetValue(entityId, out var timelineEntity)
            || timelineEntity.AnimationSlots is null
            || !timelineEntity.AnimationSlots.TryGetValue(0, out var timelineLocomotion)
            || !timelineLocomotion.Name.Equals("RUN", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "OP54 relative movement did not preserve its target, speed, and"
                + " ordinary RUN locomotion clip.");
        }
    }

    private static DecompiledFunction CreateMovementSmokeFunction(
        params DecompiledInstruction[] trailingInstructions)
    {
        const int entityId = 7;
        var instructions = new List<DecompiledInstruction>
        {
            new(
                0, 0, "Entity_InitPath", 95,
                new[] { ScalarArgument(0, "s16", entityId, sem: "entity") },
                Array.Empty<JumpTarget>()),
            new(
                1, 0, "Entity_AddWayPoint", 95,
                new[]
                {
                    ScalarArgument(0, "s16", entityId, sem: "entity"),
                    ScalarArgument(1, "f32", floatValue: 6f),
                    ScalarArgument(2, "f32", floatValue: 0f),
                    ScalarArgument(3, "f32", floatValue: 0f),
                },
                Array.Empty<JumpTarget>()),
            new(
                2, 0, "Entity_Move", 95,
                new[]
                {
                    ScalarArgument(0, "s16", entityId, sem: "entity"),
                    ScalarArgument(1, "f32", floatValue: 1f),
                    ScalarArgument(2, "u8", 1),
                    ScalarArgument(3, "u16", 0),
                },
                Array.Empty<JumpTarget>()),
        };
        instructions.AddRange(trailingInstructions);
        instructions.Add(new DecompiledInstruction(
            instructions.Count, 0, "OP1", 1,
            Array.Empty<InstructionArgument>(),
            Array.Empty<JumpTarget>()));
        return new DecompiledFunction(0, "Init", true, instructions);
    }

    private static InstructionArgument ScalarArgument(
        int index,
        string type,
        int intValue = 0,
        double floatValue = 0,
        string? sem = null)
        => new(
            index,
            "scalar",
            type,
            intValue,
            floatValue,
            Array.Empty<byte>(),
            null,
            Sem: sem);

    private static InstructionArgument StringArgument(int index, string value)
        => new(
            index,
            "string",
            "string",
            0,
            0,
            Encoding.Latin1.GetBytes(value + '\0'),
            null,
            Name: value);

    private sealed class Execution
    {
        private static readonly ConditionalWeakTable<DecompiledScript, SpawnCatalog> SpawnCatalogs = new();
        private readonly DecompiledScript rootScript;
        private readonly ScriptAnimationLibrary? animationLibrary;
        private readonly DecompiledScript? systemScript;
        private readonly IReadOnlyDictionary<int, ScriptEntityState> spawnCatalog;
        private readonly Dictionary<int, ScriptEntityState> entities = new();
        private readonly Dictionary<string, ScriptPropAnimation> propAnimations =
            new(StringComparer.Ordinal);
        private readonly List<UnresolvedScriptCall> unresolvedCalls = new();
        private readonly ScriptVariableState variables = new();
        private List<ScriptSceneTimelinePoint>? timelinePoints;
        private int executedInstructions;
        private int elapsedFrames;
        private int? environmentProfile;

        /// <summary>The block the reader selected, used only to settle a fork.</summary>
        public int? PreferredInstruction { get; init; }

        public int PreferredFunctionIndex { get; init; } = -1;
        private ScriptCameraState camera = new();

        public Execution(
            DecompiledScript script,
            ScriptAnimationLibrary? animationLibrary,
            DecompiledScript? systemScript = null,
            ScriptSubject? subject = null)
        {
            rootScript = script;
            this.animationLibrary = animationLibrary;
            this.systemScript = systemScript;
            // An animation or craft script only ever talks to itself: bind its
            // "self" reference to the actor the game's tables say it drives, so
            // its own ANI functions and camera commands have a subject to play on.
            if (subject is not null)
            {
                entities[ScriptEntityReferences.SelfEntityId] = new ScriptEntityState(
                    ScriptEntityReferences.SelfEntityId,
                    subject.ModelAssetId,
                    subject.ScriptName,
                    string.Empty,
                    0,
                    0,
                    Vector3.Zero,
                    0f,
                    1f,
                    0f,
                    0f,
                    subject.ScriptName,
                    string.Empty,
                    0, 0, 0, 0, 0,
                    Array.Empty<Vector3>(),
                    HasSpawnDefinition: true,
                    HasPosition: true,
                    ReferenceSymbol: "Self",
                    FacialAssetId: animationLibrary?.ResolveFacialAsset(-1, subject.ModelAssetId)
                        ?? string.Empty);
            }
            spawnCatalog = SpawnCatalogs.GetValue(
                script,
                static value => new SpawnCatalog(BuildSpawnCatalog(value))).Entities;
        }

        public ScriptSceneState Snapshot() => new(
            camera,
            new Dictionary<int, ScriptEntityState>(entities),
            new Dictionary<string, ScriptPropAnimation>(
                propAnimations, StringComparer.Ordinal),
            unresolvedCalls.ToArray(),
            environmentProfile);

        /// <summary>
        /// Starts recording a preview. The clock restarts at zero, so a movement
        /// that belongs to the replayed past is settled here: leaving it in place
        /// would replay it from its first waypoint and teleport the actor back to
        /// where it stood before, often right out of frame.
        /// </summary>
        public void BeginTimeline()
        {
            foreach (var pair in entities.ToArray())
            {
                if (pair.Value.Motion is not { } motion) continue;
                entities[pair.Key] = pair.Value with
                {
                    Position = motion.PositionAt(elapsedFrames),
                    YawDegrees = motion.HeadingAt(elapsedFrames) ?? pair.Value.YawDegrees,
                    Motion = null,
                };
            }
            timelinePoints = new List<ScriptSceneTimelinePoint>();
            elapsedFrames = 0;
        }

        public void LoadInitialEntities(DecompiledFunction selectedFunction)
        {
            if (selectedFunction.Name.Equals("Init", StringComparison.OrdinalIgnoreCase))
                return;
            var init = rootScript.Functions.FirstOrDefault(value =>
                value.IsCode
                && value.Name.Equals("Init", StringComparison.OrdinalIgnoreCase));
            if (init is null) return;
            ExecuteFunction(
                rootScript,
                init,
                new HashSet<DecompiledFunction>(
                    new[] { init }, ReferenceEqualityComparer.Instance));
            camera = new ScriptCameraState();
        }

        public ScriptSceneTimeline CreateTimeline(
            string functionName,
            ScriptSceneState initialState)
        {
            var activeMotionEnd = entities.Values
                .Where(value => value.Motion is not null)
                .Select(value => value.Motion!.EndFrame)
                .DefaultIfEmpty(0)
                .Max();
            return new ScriptSceneTimeline(
                functionName,
                initialState,
                timelinePoints?.ToArray() ?? Array.Empty<ScriptSceneTimelinePoint>(),
                Math.Max(
                    1,
                    Math.Max(
                        activeMotionEnd,
                        Math.Max(
                            elapsedFrames,
                            timelinePoints is { Count: > 0 }
                                ? timelinePoints[^1].Frame + 1
                                : 0))));
        }

        public void ExecutePath(
            DecompiledScript ownerScript,
            DecompiledFunction function,
            IEnumerable<int> instructionIndices,
            HashSet<DecompiledFunction> callStack,
            int? selfEntityId = null)
        {
            foreach (var index in instructionIndices)
            {
                if (index < 0 || index >= function.Instructions.Count) continue;
                ExecuteInstruction(
                    ownerScript, function, function.Instructions[index], callStack, selfEntityId);
            }
        }

        public void ExecuteInstructionForTimeline(
            DecompiledScript ownerScript,
            DecompiledFunction function,
            DecompiledInstruction instruction,
            HashSet<DecompiledFunction> callStack)
            => ExecuteInstruction(ownerScript, function, instruction, callStack, null);

        private void ExecuteInstruction(
            DecompiledScript ownerScript,
            DecompiledFunction function,
            DecompiledInstruction instruction,
            HashSet<DecompiledFunction> callStack,
            int? selfEntityId)
        {
            if (++executedInstructions > MaximumExecutedInstructions)
                throw new InvalidOperationException(
                    "Script state replay exceeded its instruction limit.");

            var before = timelinePoints is null ? null : Snapshot();
            ApplyVariableWrite(instruction, selfEntityId);
            ApplyEnvironmentProfile(instruction, selfEntityId);
            EnsureReferencedEntities(instruction, selfEntityId);
            ApplyEntityInstruction(instruction, selfEntityId);
            ApplyFacialExpression(instruction, selfEntityId);
            ApplyPropAnimation(instruction);
            if (instruction.Opcode is 54 or 56 or 95
                && ResolveInstructionEntityId(instruction, selfEntityId) is { } animatedEntityId
                && entities.TryGetValue(animatedEntityId, out var animatedEntity)
                && animatedEntity.Motion is { } entityMotion)
            {
                ApplyLocomotionAnimation(animatedEntityId, entityMotion.AnimationState, callStack);
            }
            if (!RequiresNonExecutableEntity(instruction, selfEntityId))
            {
                camera = ResolveCameraEntityReferences(
                    ScriptCameraStateResolver.ApplyInstruction(instruction, camera),
                    selfEntityId);
            }
            if (timelinePoints is not null && IsTimelineInstruction(instruction))
            {
                timelinePoints.Add(new ScriptSceneTimelinePoint(
                    elapsedFrames,
                    function.Index,
                    instruction.Index,
                    instruction,
                    before!,
                    Snapshot(),
                    ResolveInstructionEntityId(instruction, selfEntityId),
                    !ReferenceEquals(ownerScript, rootScript)));
            }
            if (instruction.Opcode == 16)
                elapsedFrames = checked(elapsedFrames + ScriptWaitDuration.DecodePreviewFrames(
                    instruction.Arguments.FirstOrDefault()?.IntValue ?? 0));
            if (instruction.Opcode == 55
                && instruction.Name.Equals(
                    "Entity_WaitMovement", StringComparison.Ordinal)
                && ResolveInstructionEntityId(instruction, selfEntityId) is { } movingEntityId
                && entities.TryGetValue(movingEntityId, out var movingEntity)
                && movingEntity.Motion is { } motion)
            {
                elapsedFrames = Math.Max(elapsedFrames, motion.EndFrame);
            }

            if (instruction.Opcode == 47)
            {
                ExecuteAnimationFunction(instruction, callStack, selfEntityId);
                return;
            }
            if (instruction.Opcode != 2) return;
            var functionName = ReadCallFunctionName(instruction);
            if (string.IsNullOrEmpty(functionName)) return;
            var variant = instruction.Arguments.FirstOrDefault()?.IntValue ?? 0;
            if (!TryResolveCallTarget(
                    ownerScript, instruction, out var targetScript, out var target))
            {
                unresolvedCalls.Add(new UnresolvedScriptCall(
                    function.Index, instruction.Index, variant, functionName));
                return;
            }
            if (callStack.Count >= MaximumCallDepth || !callStack.Add(target))
                return;
            try
            {
                ExecuteFunction(targetScript, target, callStack, selfEntityId);
            }
            finally
            {
                callStack.Remove(target);
            }
        }

        public bool TryResolveCallTarget(
            DecompiledScript ownerScript,
            DecompiledInstruction instruction,
            out DecompiledScript targetScript,
            out DecompiledFunction target)
        {
            targetScript = instruction.Arguments.FirstOrDefault()?.IntValue == 0x0A
                ? systemScript!
                : ownerScript;
            target = null!;
            if (targetScript is null) return false;
            var functionName = ReadCallFunctionName(instruction);
            if (string.IsNullOrEmpty(functionName)) return false;
            target = targetScript.Functions.FirstOrDefault(candidate =>
                candidate.IsCode
                && candidate.Name.Equals(functionName, StringComparison.Ordinal))!;
            return target is not null;
        }

        public void ExecuteFunction(
            DecompiledScript ownerScript,
            DecompiledFunction function,
            HashSet<DecompiledFunction> callStack,
            int? selfEntityId = null)
        {
            var visited = new HashSet<int>();
            var index = 0;
            while (index >= 0
                   && index < function.Instructions.Count
                   && visited.Add(index))
            {
                var instruction = function.Instructions[index];
                ExecuteInstruction(ownerScript, function, instruction, callStack, selfEntityId);
                if (instruction.Opcode == 1) return;
                index = NextInstructionIndex(function, index, selfEntityId);
            }
        }

        /// <summary>
        /// Follows the branch the engine would take. A conditional jump whose
        /// expression is fully known is resolved exactly; anything else keeps the
        /// deterministic sequential-first policy shared with the camera resolver.
        /// </summary>
        private int NextInstructionIndex(
            DecompiledFunction function,
            int index,
            int? selfEntityId)
        {
            var instruction = function.Instructions[index];
            if (instruction.Opcode == 5
                && instruction.Arguments.FirstOrDefault(value => value.Kind == "expr")
                    is { } condition
                && ScriptExpressionEvaluator.TryEvaluate(
                    condition.Expression, variables, selfEntityId, out var value))
            {
                if (value != 0)
                {
                    return index + 1 < function.Instructions.Count ? index + 1 : -1;
                }
                var target = instruction.Jumps.FirstOrDefault(jump =>
                    jump.TargetFunctionIndex == function.Index
                    && jump.TargetInstructionIndex >= 0);
                if (target is not null) return target.TargetInstructionIndex;
                return -1;
            }
            var successors = ScriptCameraStateResolver.Successors(function, index).ToArray();
            if (successors.Length > 1
                && PreferredInstruction is { } wanted
                && function.Index == PreferredFunctionIndex)
            {
                // The reader's own block breaks the tie the script does not.
                foreach (var successor in successors)
                {
                    if (CanReach(function, successor, wanted)) return successor;
                }
            }
            return successors.FirstOrDefault(-1);
        }

        /// <summary>The block a branch leads to, or not, following the same policy.</summary>
        private static bool CanReach(DecompiledFunction function, int from, int target)
        {
            var seen = new HashSet<int>();
            var pending = new Stack<int>();
            pending.Push(from);
            while (pending.Count > 0)
            {
                var index = pending.Pop();
                if (index < 0 || index >= function.Instructions.Count || !seen.Add(index)) continue;
                if (index == target) return true;
                if (function.Instructions[index].Opcode == 1) continue;
                foreach (var successor in ScriptCameraStateResolver.Successors(function, index))
                {
                    pending.Push(successor);
                }
            }
            return false;
        }

        private void ApplyVariableWrite(
            DecompiledInstruction instruction,
            int? selfEntityId)
        {
            switch (instruction.Opcode)
            {
                case 10:
                case 18:
                {
                    if (instruction.Arguments.Count < 2) return;
                    var expression = instruction.Arguments[1];
                    variables.WriteRegister(
                        selfEntityId,
                        instruction.Arguments[0].IntValue,
                        ScriptExpressionEvaluator.TryEvaluate(
                            expression.Expression, variables, selfEntityId, out var value)
                            ? value
                            : null);
                    return;
                }
                case 12:
                case 13:
                {
                    if (instruction.Arguments.Count < 1) return;
                    variables.WriteFlag(
                        instruction.Arguments[0].IntValue, instruction.Opcode == 12);
                    return;
                }
                case 43:
                case 44:
                {
                    if (instruction.Arguments.Count < 2) return;
                    variables.WriteEntityStatus(
                        ResolveEntityId(instruction.Arguments[0].IntValue, selfEntityId),
                        instruction.Arguments[1].IntValue,
                        instruction.Opcode == 43);
                    return;
                }
            }
        }

        private void ApplyEnvironmentProfile(
            DecompiledInstruction instruction,
            int? selfEntityId)
        {
            // SET_SYS is shared by several unrelated engine globals. Slot 5 is
            // the environment profile; the value remains an expression so calls
            // and register writes replay exactly like they do for other state.
            if (instruction.Opcode != 8) return;

            // A selector-aware registry consumes slot 5 structurally and exposes
            // only the expression. Accept the former generic SET_SYS shape too,
            // so scripts opened with an older user-supplied registry still replay.
            var expression = instruction.Name.Equals(
                    "Environment_SetProfile", StringComparison.Ordinal)
                ? instruction.Arguments.FirstOrDefault(value => value.Kind == "expr")
                : instruction.Arguments.Count >= 2
                  && instruction.Arguments[0].IntValue == 5
                    ? instruction.Arguments[1]
                    : null;
            if (expression is null) return;

            if (ScriptExpressionEvaluator.TryEvaluate(
                    expression.Expression,
                    variables,
                    selfEntityId,
                    out var value))
            {
                environmentProfile = value;
            }
        }

        public bool CanResolveAnimationCall(DecompiledInstruction instruction)
        {
            if (instruction.Opcode != 47
                || instruction.Arguments.Count < 3
                || animationLibrary is null)
            {
                return false;
            }
            var entityId = ResolveEntityId(instruction.Arguments[0].IntValue, null);
            return entities.TryGetValue(entityId, out var entity)
                && animationLibrary.TryGetFunction(
                    entity,
                    ReadArgumentString(instruction.Arguments[2]),
                    out _,
                    out _);
        }

        private void ExecuteAnimationFunction(
            DecompiledInstruction instruction,
            HashSet<DecompiledFunction> callStack,
            int? selfEntityId)
        {
            if (instruction.Arguments.Count < 3 || animationLibrary is null) return;
            var entityId = ResolveEntityId(instruction.Arguments[0].IntValue, selfEntityId);
            ExecuteNamedAnimationFunction(
                entityId,
                ReadArgumentString(instruction.Arguments[2]),
                callStack,
                holdFinalFrame: timelinePoints is null);
        }

        private void ExecuteNamedAnimationFunction(
            int entityId,
            string functionName,
            HashSet<DecompiledFunction> callStack,
            bool holdFinalFrame)
        {
            if (animationLibrary is null) return;
            if (!entities.TryGetValue(entityId, out var entity)
                || !animationLibrary.TryGetFunction(
                    entity,
                    functionName,
                    out var aniScript,
                    out var function)
                || callStack.Count >= MaximumCallDepth
                || !callStack.Add(function))
            {
                return;
            }
            try
            {
                ExecuteFunction(aniScript, function, callStack, entityId);
                if (holdFinalFrame && entities.TryGetValue(entityId, out var finalEntity)
                    && finalEntity.AnimationSlots is { Count: > 0 })
                {
                    // Outside a timeline the scene is shown at rest: a one-shot
                    // clip stays on its final pose, but a looping clip (every
                    // idle) keeps playing exactly as the engine loops it.
                    entities[entityId] = finalEntity with
                    {
                        AnimationSlots = finalEntity.AnimationSlots.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value.Loop
                                ? pair.Value
                                : pair.Value with { HoldFinalFrame = true }),
                    };
                }
            }
            finally
            {
                callStack.Remove(function);
            }
        }

        /// <summary>
        /// Starts the locomotion of a moving actor the way the engine does: by
        /// running its AniWalk/AniRun/AniDush function, which resolves the clip
        /// for the actor's current mode. Only an actor with no reachable ANI
        /// script falls back to the plain field clip.
        /// </summary>
        private void ApplyLocomotionAnimation(
            int entityId,
            int animationState,
            HashSet<DecompiledFunction> callStack)
        {
            if (ScriptLocomotionAnimationCatalog.TryResolveAnimationFunction(
                    animationState, out var functionName))
            {
                var before = ReadBaseAnimationName(entityId);
                ExecuteNamedAnimationFunction(
                    entityId, functionName, callStack, holdFinalFrame: false);
                if (!string.Equals(ReadBaseAnimationName(entityId), before, StringComparison.Ordinal))
                    return;
            }
            if (ScriptLocomotionAnimationCatalog.TryResolveBaseClip(animationState, out var clipName))
                ApplyLocomotionClip(entityId, clipName);
        }

        private string? ReadBaseAnimationName(int entityId)
            => entities.TryGetValue(entityId, out var entity)
                && entity.AnimationSlots is { } slots
                && slots.TryGetValue(0, out var animation)
                    ? animation.Name
                    : null;

        private void ApplyLocomotionClip(int entityId, string clipName)
        {
            if (!entities.TryGetValue(entityId, out var entity)
                || !entity.IsExecutable)
            {
                return;
            }
            var slots = CopyAnimationSlots(entity);
            slots[0] = new ScriptEntityAnimation(
                0,
                clipName,
                Loop: true,
                Flag2: 0,
                Flag3: 0,
                Flag4: 0,
                Flag5: 0,
                BlendTime: 0.2f,
                TimeParameter1: -1f,
                TimeParameter2: -1f,
                TimeParameter3: -1f,
                StartFrame: elapsedFrames);
            entities[entityId] = entity with { AnimationSlots = slots };
        }

        private void ApplyEntityInstruction(
            DecompiledInstruction instruction,
            int? selfEntityId)
        {
            if (instruction.Opcode == 19)
            {
                ApplySpawn(instruction);
                return;
            }
            if (instruction.Opcode == 34)
            {
                ApplyAnimation(instruction, selfEntityId);
                return;
            }
            if (instruction.Opcode == 36)
            {
                ApplyAnimationBankBinding(instruction, selfEntityId);
                return;
            }
            if (instruction.Opcode == 39)
            {
                ApplyEffect(instruction, selfEntityId);
                return;
            }
            if (instruction.Opcode == 73)
            {
                ApplySceneEffectPlayback(instruction);
                return;
            }
            if (instruction.Opcode == 37)
            {
                ApplyAttachment(instruction, selfEntityId);
                return;
            }
            if (instruction.Opcode == 32)
            {
                ApplyAttachmentVisibility(instruction, selfEntityId);
                return;
            }
            if (instruction.Opcode == 46)
            {
                ApplySetPosition(instruction, selfEntityId);
                return;
            }
            if (instruction.Opcode == 54)
            {
                ApplyModeMovement(instruction, selfEntityId);
                return;
            }
            if (instruction.Opcode == 55)
            {
                ApplyMovementControl(instruction, selfEntityId);
                return;
            }
            if (instruction.Opcode == 56)
            {
                ApplyWander(instruction, selfEntityId);
                return;
            }
            if (instruction.Opcode == 95)
                ApplyMovement(instruction, selfEntityId);
        }

        private void ApplyPropAnimation(DecompiledInstruction instruction)
        {
            if (instruction.Opcode != 69 || instruction.Arguments.Count < 2) return;
            var propName = ReadArgumentString(instruction.Arguments[0]);
            var animationName = ReadArgumentString(instruction.Arguments[1]);
            if (string.IsNullOrWhiteSpace(propName)
                || string.IsNullOrWhiteSpace(animationName))
            {
                return;
            }
            propAnimations[propName] = new ScriptPropAnimation(
                propName,
                animationName,
                elapsedFrames,
                HoldFinalFrame: timelinePoints is null);
        }

        private void EnsureReferencedEntities(
            DecompiledInstruction instruction,
            int? selfEntityId)
        {
            foreach (var argument in instruction.Arguments.Where(value =>
                         string.Equals(value.Sem, "entity", StringComparison.OrdinalIgnoreCase)
                         && value.Kind == "scalar"))
            {
                var entityId = ResolveEntityId(argument.IntValue, selfEntityId);
                if (entities.ContainsKey(entityId)) continue;
                entities.Add(entityId, CreateReferencedEntity(entityId));
            }
        }

        private ScriptEntityState CreateReferencedEntity(int entityId)
        {
            var reference = ScriptEntityReferences.Resolve(entityId);
            if (reference.Resolution == ScriptEntityResolution.Concrete
                && reference.ConcreteEntityId is { } concreteId
                && spawnCatalog.TryGetValue(concreteId, out var spawned))
            {
                return spawned with
                {
                    EntityId = entityId,
                    Position = Vector3.Zero,
                    YawDegrees = 0f,
                    PendingWaypoints = Array.Empty<Vector3>(),
                    Motion = null,
                    AnimationSlots = null,
                    HasSpawnDefinition = false,
                    HasPosition = false,
                    ReferenceSymbol = reference.Symbol,
                    FacialAssetId = animationLibrary?.ResolveFacialAsset(
                        entityId, spawned.AssetId) ?? spawned.FacialAssetId,
                };
            }
            if (reference.Resolution == ScriptEntityResolution.Concrete
                && reference.ConcreteEntityId is { } characterId
                && animationLibrary?.TryGetCharacter(characterId, out var character) == true
                && !string.IsNullOrWhiteSpace(character.ModelAssetId))
            {
                return new ScriptEntityState(
                    entityId,
                    character.ModelAssetId,
                    string.IsNullOrWhiteSpace(character.DisplayName)
                        ? reference.Symbol
                        : character.DisplayName,
                    string.Empty,
                    0,
                    0,
                    Vector3.Zero,
                    0f,
                    1f,
                    0f,
                    0f,
                    character.AnimationScript,
                    string.Empty,
                    0,
                    0,
                    0,
                    0,
                    0,
                    Array.Empty<Vector3>(),
                    HasSpawnDefinition: false,
                    HasPosition: false,
                    ReferenceSymbol: reference.Symbol,
                    FacialAssetId: character.FacialAssetId);
            }
            var placeholder = reference.Resolution is ScriptEntityResolution.Placeholder
                or ScriptEntityResolution.Contextual
                || reference.Resolution == ScriptEntityResolution.Concrete;
            var executable = reference.Resolution != ScriptEntityResolution.NonExecutable;
            return new ScriptEntityState(
                entityId,
                placeholder ? ScriptEntityReferences.PlaceholderAssetId : string.Empty,
                reference.Symbol,
                string.Empty,
                0,
                0,
                Vector3.Zero,
                0f,
                1f,
                0f,
                0f,
                string.Empty,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                Array.Empty<Vector3>(),
                HasSpawnDefinition: false,
                HasPosition: false,
                ReferenceSymbol: reference.Symbol,
                IsPlaceholder: placeholder,
                IsExecutable: executable);
        }

        private void ApplySpawn(DecompiledInstruction instruction)
        {
            var arguments = instruction.Arguments;
            if (!HasSpawnLayout(arguments)) return;

            if (!TryCreateSpawnState(instruction, elapsedFrames, out var entity))
                return;
            if (animationLibrary is not null)
            {
                entity = entity with
                {
                    DisplayName = animationLibrary.ResolveDisplayName(
                        entity.AssetId, entity.DisplayName),
                    FacialAssetId = animationLibrary.ResolveFacialAsset(
                        entity.EntityId, entity.AssetId),
                };
            }
            var entityId = entity.EntityId;
            entities[entityId] = entity;
        }

        private static bool TryCreateSpawnState(
            DecompiledInstruction instruction,
            int startFrame,
            out ScriptEntityState entity)
        {
            var arguments = instruction.Arguments;
            if (!HasSpawnLayout(arguments))
            {
                entity = null!;
                return false;
            }
            var entityId = arguments[0].IntValue;
            var initialAnimation = ReadArgumentString(arguments[3]);
            entity = new ScriptEntityState(
                entityId,
                ReadArgumentString(arguments[1]),
                ReadArgumentString(arguments[2]),
                initialAnimation,
                arguments[4].IntValue,
                arguments[5].IntValue,
                new Vector3(
                    (float)arguments[6].FloatValue,
                    (float)arguments[7].FloatValue,
                    (float)arguments[8].FloatValue),
                (float)arguments[9].FloatValue,
                (float)arguments[10].FloatValue,
                (float)arguments[11].FloatValue,
                (float)arguments[12].FloatValue,
                ReadArgumentString(arguments[13]),
                ReadArgumentString(arguments[14]),
                arguments[15].IntValue,
                arguments[16].IntValue,
                arguments[17].IntValue,
                arguments[18].IntValue,
                arguments[19].IntValue,
                Array.Empty<Vector3>(),
                AnimationSlots: CreateInitialAnimations(initialAnimation, startFrame),
                ReferenceSymbol: ScriptEntityReferences.DisplayName(entityId));
            return true;
        }

        private static IReadOnlyDictionary<int, ScriptEntityAnimation>? CreateInitialAnimations(
            string animationName,
            int startFrame)
        {
            if (string.IsNullOrWhiteSpace(animationName)) return null;
            return new Dictionary<int, ScriptEntityAnimation>
            {
                [0] = new(
                    0, animationName, true,
                    0, 0, 0, 0,
                    0f, -1f, -1f, -1f,
                    startFrame),
            };
        }

        private void ApplyFacialExpression(
            DecompiledInstruction instruction,
            int? selfEntityId)
        {
            if (instruction.Opcode != 50) return;
            var entityArgumentIndex = instruction.Name.Equals(
                "OP50_other", StringComparison.Ordinal) ? 1 : 0;
            if (instruction.Arguments.Count <= entityArgumentIndex) return;
            var entityId = ResolveEntityId(
                instruction.Arguments[entityArgumentIndex].IntValue, selfEntityId);
            if (!entities.TryGetValue(entityId, out var entity)) return;
            var current = entity.FacialExpression ?? ScriptFacialExpression.Neutral;
            ScriptFacialExpression updated;
            if (instruction.Name.Equals("Entity_SetFacialFrames", StringComparison.Ordinal)
                && instruction.Arguments.Count >= 5)
            {
                updated = new ScriptFacialExpression(
                    Frame(instruction.Arguments[1].IntValue),
                    Frame(instruction.Arguments[2].IntValue),
                    Frame(instruction.Arguments[3].IntValue),
                    Frame(instruction.Arguments[4].IntValue),
                    elapsedFrames);
            }
            else if (instruction.Name.Equals("Entity_SetFacialPatterns", StringComparison.Ordinal)
                     && instruction.Arguments.Count >= 5)
            {
                updated = new ScriptFacialExpression(
                    Expand(ReadArgumentString(instruction.Arguments[1])),
                    Expand(ReadArgumentString(instruction.Arguments[2])),
                    Expand(ReadArgumentString(instruction.Arguments[3])),
                    Expand(ReadArgumentString(instruction.Arguments[4])),
                    elapsedFrames);
            }
            else if (instruction.Name.Equals("Entity_SetFacialCommand", StringComparison.Ordinal)
                     && instruction.Arguments.Count >= 2)
            {
                updated = ScriptFacialCommandParser.ApplyComposite(
                    current,
                    ReadArgumentString(instruction.Arguments[1]),
                    Expand,
                    elapsedFrames);
            }
            else if (instruction.Name.Equals("OP50_other", StringComparison.Ordinal)
                     && instruction.Arguments[0].IntValue is 10 or 11)
            {
                updated = ScriptFacialExpression.Neutral with { StartFrame = elapsedFrames };
            }
            else
            {
                return;
            }
            entities[entityId] = entity with { FacialExpression = updated };

            string Expand(string value)
                => animationLibrary?.ExpandFacialPattern(value) ?? value;
            static string Frame(int value)
                => value is >= 0 and <= 9
                    ? ((char)('0' + value)).ToString()
                    : value is >= 10 and <= 19
                        ? ((char)('A' + value - 10)).ToString()
                        : "0";
        }

        private void ApplyAnimation(
            DecompiledInstruction instruction,
            int? selfEntityId)
        {
            var arguments = instruction.Arguments;
            if (arguments.Count < 11) return;
            var entityId = ResolveEntityId(arguments[0].IntValue, selfEntityId);
            if (!entities.TryGetValue(entityId, out var entity) || !entity.IsExecutable) return;
            var name = ReadArgumentString(arguments[1]);
            if (string.IsNullOrEmpty(name)) return;
            var slots = CopyAnimationSlots(entity);
            slots[0] = new ScriptEntityAnimation(
                0,
                name,
                arguments[2].IntValue != 0,
                arguments[3].IntValue,
                arguments[4].IntValue,
                arguments[5].IntValue,
                arguments[6].IntValue,
                // The blend time is the float that follows the five flag bytes,
                // and the three time parameters follow it: reading the blend
                // from the last flag byte shifted every one of them.
                (float)arguments[7].FloatValue,
                (float)arguments[8].FloatValue,
                (float)arguments[9].FloatValue,
                (float)arguments[10].FloatValue,
                elapsedFrames);
            entities[entityId] = entity with { AnimationSlots = slots };
        }

        /// <summary>
        /// OP39, the effect opcode. Selector 10 loads an .eff file into a slot of
        /// an owner (an entity, or the map through -3), 12 starts a numbered
        /// instance of a loaded slot, and 11/13/14/16 stop or unload it. What the
        /// scene shows is therefore the set of instances started and not stopped.
        /// </summary>
        private void ApplyEffect(DecompiledInstruction instruction, int? selfEntityId)
        {
            var arguments = instruction.Arguments;
            // The selector byte names the variant in the instruction definitions
            // (Effect_LoadSlot, Effect_Play, ...): it is not one of the visible
            // operands, so the name is what tells the variants apart.
            if (arguments.Count < 2) return;
            var ownerId = ResolveEntityId(arguments[0].IntValue, selfEntityId);
            if (!entities.TryGetValue(ownerId, out var owner))
            {
                // Effects are commonly owned by the scene itself (-3), which no
                // other opcode declares as an entity: register it so its slots
                // and its playing instances have somewhere to live.
                owner = CreateReferencedEntity(ownerId);
                entities[ownerId] = owner;
            }

            switch (instruction.Name)
            {
                case "Effect_LoadSlot" when arguments.Count >= 3:
                {
                    var slots = CopyEffectSlots(owner);
                    var slot = arguments[1].IntValue;
                    var path = ReadArgumentString(arguments[2]);
                    slots[slot] = path;
                    var effects = CopyEffects(owner);
                    foreach (var key in effects
                                 .Where(pair => pair.Value.Slot == slot)
                                 .Select(pair => pair.Key)
                                 .ToArray())
                    {
                        effects[key] = effects[key] with { EffectPath = path };
                    }
                    entities[ownerId] = owner with
                    {
                        EffectSlots = slots,
                        Effects = effects,
                    };
                    return;
                }
                case "Effect_UnloadSlot":
                {
                    var slots = CopyEffectSlots(owner);
                    slots.Remove(arguments[1].IntValue);
                    // Unloading a slot takes down whatever it was playing.
                    var playing = CopyEffects(owner);
                    foreach (var key in playing
                                 .Where(pair => pair.Value.Slot == arguments[1].IntValue)
                                 .Select(pair => pair.Key)
                                 .ToArray())
                    {
                        playing.Remove(key);
                    }
                    entities[ownerId] = owner with { EffectSlots = slots, Effects = playing };
                    return;
                }
                case "Effect_Play" when arguments.Count >= 15:
                {
                    var slot = arguments[1].IntValue;
                    var effects = CopyEffects(owner);
                    var instance = arguments[14].IntValue;
                    effects[instance] = new ScriptEffectInstance(
                        instance,
                        slot,
                        owner.EffectSlots is { } loaded && loaded.TryGetValue(slot, out var path)
                            ? path
                            : string.Empty,
                        ResolveEntityId(arguments[2].IntValue, selfEntityId),
                        ReadArgumentString(arguments[4]),
                        new Vector3(
                            (float)arguments[5].FloatValue,
                            (float)arguments[6].FloatValue,
                            (float)arguments[7].FloatValue),
                        new Vector3(
                            (float)arguments[8].FloatValue,
                            (float)arguments[9].FloatValue,
                            (float)arguments[10].FloatValue),
                        new Vector3(
                            (float)arguments[11].FloatValue,
                            (float)arguments[12].FloatValue,
                            (float)arguments[13].FloatValue),
                        elapsedFrames);
                    entities[ownerId] = owner with { Effects = effects };
                    return;
                }
                case "Effect_Stop":
                case "Effect_Kill":
                case "Effect_Reset":
                {
                    var effects = CopyEffects(owner);
                    effects.Remove(arguments[1].IntValue);
                    entities[ownerId] = owner with { Effects = effects };
                    return;
                }
            }
        }

        /// <summary>
        /// OP73 selector 23 controls the one scene-wide effect instance. The
        /// handler reads an operation byte and a 32-bit scene slot. Operation 0
        /// calls the engine's global-effect constructor with that slot; every
        /// other value calls its no-argument global-effect destructor (the slot
        /// is parsed but not passed). Rain and mist use this exact path.
        /// </summary>
        private void ApplySceneEffectPlayback(DecompiledInstruction instruction)
        {
            if (!instruction.Name.Equals(
                    "SceneEffect_PlayOrStop", StringComparison.Ordinal)
                || instruction.Arguments.Count < 2)
            {
                return;
            }

            const int sceneOwnerId = -3;
            var operation = instruction.Arguments[0].IntValue;
            var slot = instruction.Arguments[1].IntValue;
            if (!entities.TryGetValue(sceneOwnerId, out var owner))
            {
                owner = CreateReferencedEntity(sceneOwnerId);
            }
            var effects = CopyEffects(owner);
            foreach (var key in effects
                         .Where(pair => pair.Value.Space == ScriptEffectSpace.Camera)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                effects.Remove(key);
            }
            if (operation != 0)
            {
                entities[sceneOwnerId] = owner with { Effects = effects };
                return;
            }

            var instanceId = SceneSlotInstanceId(slot);
            effects[instanceId] = new ScriptEffectInstance(
                instanceId,
                slot,
                owner.EffectSlots is { } slots
                    && slots.TryGetValue(slot, out var path)
                        ? path
                        : string.Empty,
                sceneOwnerId,
                string.Empty,
                Vector3.Zero,
                Vector3.Zero,
                Vector3.One,
                elapsedFrames,
                ScriptEffectSpace.Camera);
            entities[sceneOwnerId] = owner with { Effects = effects };
        }

        private static int SceneSlotInstanceId(int slot)
            => unchecked(int.MinValue + slot);

        private static Dictionary<int, string> CopyEffectSlots(ScriptEntityState entity)
            => entity.EffectSlots is null
                ? new Dictionary<int, string>()
                : new Dictionary<int, string>(entity.EffectSlots);

        private static Dictionary<int, ScriptEffectInstance> CopyEffects(ScriptEntityState entity)
            => entity.Effects is null
                ? new Dictionary<int, ScriptEffectInstance>()
                : new Dictionary<int, ScriptEffectInstance>(entity.Effects);

        /// <summary>
        /// OP37: selector 0 attaches a model to a node with a local placement,
        /// selector 1 clears that node. The placement is authored in the script's
        /// own units: position, Euler angles in degrees, then scale.
        /// </summary>
        private void ApplyAttachment(DecompiledInstruction instruction, int? selfEntityId)
        {
            var arguments = instruction.Arguments;
            if (arguments.Count < 13) return;
            var mode = arguments[0].IntValue;
            if (mode is not (0 or 1)) return;
            var entityId = ResolveEntityId(arguments[1].IntValue, selfEntityId);
            if (!entities.TryGetValue(entityId, out var entity) || !entity.IsExecutable) return;
            var model = ReadArgumentString(arguments[2]);
            var attachPoint = ReadArgumentString(arguments[3]);
            if (string.IsNullOrWhiteSpace(attachPoint)) return;
            var attachments = entity.Attachments is null
                ? new Dictionary<string, ScriptEntityAttachment>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ScriptEntityAttachment>(
                    entity.Attachments, StringComparer.OrdinalIgnoreCase);
            if (mode == 1 || string.IsNullOrWhiteSpace(model))
            {
                // Clearing a node leaves it empty rather than dropping the entry:
                // the actor's default equipment must not come back on its own.
                attachments[attachPoint] = new ScriptEntityAttachment(
                    attachPoint, string.Empty, false, Vector3.Zero, Vector3.Zero, Vector3.One);
            }
            else
            {
                attachments[attachPoint] = new ScriptEntityAttachment(
                    attachPoint,
                    model,
                    true,
                    new Vector3(
                        arguments[4].IntValue, arguments[5].IntValue, arguments[6].IntValue),
                    new Vector3(
                        arguments[7].IntValue, arguments[8].IntValue, arguments[9].IntValue),
                    new Vector3(
                        (float)arguments[9].FloatValue,
                        (float)arguments[10].FloatValue,
                        (float)arguments[11].FloatValue));
            }
            entities[entityId] = entity with { Attachments = attachments };
        }

        /// <summary>
        /// OP32_0: shows or hides what hangs from a node — the ShowEquip and
        /// EraseEquip of a character's own ANI script.
        /// </summary>
        private void ApplyAttachmentVisibility(DecompiledInstruction instruction, int? selfEntityId)
        {
            var arguments = instruction.Arguments;
            if (arguments.Count < 5 || arguments[0].IntValue != 0) return;
            var entityId = ResolveEntityId(arguments[1].IntValue, selfEntityId);
            if (!entities.TryGetValue(entityId, out var entity) || !entity.IsExecutable) return;
            var attachPoint = ReadArgumentString(arguments[3]);
            if (string.IsNullOrWhiteSpace(attachPoint)) return;
            var visible = arguments[4].IntValue != 0;
            var attachments = entity.Attachments is null
                ? new Dictionary<string, ScriptEntityAttachment>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ScriptEntityAttachment>(
                    entity.Attachments, StringComparer.OrdinalIgnoreCase);
            attachments[attachPoint] = attachments.TryGetValue(attachPoint, out var existing)
                ? existing with { Visible = visible }
                // Nothing was attached here by the script: the visibility applies
                // to the actor's default equipment, resolved when rendering.
                : new ScriptEntityAttachment(
                    attachPoint, string.Empty, visible, Vector3.Zero, Vector3.Zero, Vector3.One);
            entities[entityId] = entity with { Attachments = attachments };
        }

        private void ApplyAnimationBankBinding(
            DecompiledInstruction instruction,
            int? selfEntityId)
        {
            var arguments = instruction.Arguments;
            if (arguments.Count < 4) return;
            var operation = arguments[0].IntValue;
            if (operation is not (4 or 5)) return;
            var entityId = ResolveEntityId(arguments[1].IntValue, selfEntityId);
            if (!entities.TryGetValue(entityId, out var entity) || !entity.IsExecutable) return;
            var bank = ReadArgumentString(arguments[2]);
            if (string.IsNullOrWhiteSpace(bank)) return;
            var banks = entity.AnimationBanks?.ToList() ?? new List<string>();
            if (operation == 4)
            {
                if (!banks.Contains(bank, StringComparer.OrdinalIgnoreCase))
                    banks.Add(bank);
            }
            else
            {
                banks.RemoveAll(value => value.Equals(bank, StringComparison.OrdinalIgnoreCase));
            }
            entities[entityId] = entity with { AnimationBanks = banks };
        }

        private static Dictionary<int, ScriptEntityAnimation> CopyAnimationSlots(
            ScriptEntityState entity)
            => entity.AnimationSlots is null
                ? new Dictionary<int, ScriptEntityAnimation>()
                : new Dictionary<int, ScriptEntityAnimation>(entity.AnimationSlots);

        private static int ResolveEntityId(int entityId, int? selfEntityId)
        {
            // Dynamic IDs such as -2 (Self) stay explicit until an executing-entity
            // binding is known. This preserves their script state without inventing
            // a concrete spawned actor.
            return entityId == -2 && selfEntityId is { } self ? self : entityId;
        }

        private static ScriptCameraState ResolveCameraEntityReferences(
            ScriptCameraState state,
            int? selfEntityId)
            => state with
            {
                AlignEntityId = ResolveOptionalEntityId(state.AlignEntityId, selfEntityId),
                TargetEntityId = ResolveOptionalEntityId(state.TargetEntityId, selfEntityId),
                SecondaryTargetEntityId = ResolveOptionalEntityId(
                    state.SecondaryTargetEntityId, selfEntityId),
            };

        private static int? ResolveOptionalEntityId(int? entityId, int? selfEntityId)
            => entityId is { } value ? ResolveEntityId(value, selfEntityId) : null;

        private void ApplySetPosition(
            DecompiledInstruction instruction,
            int? selfEntityId)
        {
            var arguments = instruction.Arguments;
            if (arguments.Count < 5
                || arguments.Skip(1).Take(4).Any(value => value.Type != "f32"))
            {
                return;
            }

            var entityId = ResolveEntityId(arguments[0].IntValue, selfEntityId);
            if (!entities.TryGetValue(entityId, out var entity) || !entity.IsExecutable) return;
            var position = new Vector3(
                (float)arguments[1].FloatValue,
                (float)arguments[2].FloatValue,
                (float)arguments[3].FloatValue);
            var yawDegrees = (float)arguments[4].FloatValue;
            if (!IsFinite(position) || !float.IsFinite(yawDegrees)) return;

            entities[entityId] = entity with
            {
                Position = position,
                YawDegrees = yawDegrees,
                PendingWaypoints = Array.Empty<Vector3>(),
                Motion = null,
                HasPosition = true,
            };
        }

        private void ApplyModeMovement(
            DecompiledInstruction instruction,
            int? selfEntityId)
        {
            var arguments = instruction.Arguments;
            var isJump = instruction.Name is "Entity_JumpAbsolute" or "Entity_JumpRelative";
            if (arguments.Count < 8
                || arguments.Skip(isJump ? 1 : 2).Take(isJump ? 5 : 4)
                    .Any(value => value.Type != "f32"))
            {
                return;
            }

            var entityId = ResolveEntityId(arguments[0].IntValue, selfEntityId);
            if (!entities.TryGetValue(entityId, out var entity) || !entity.IsExecutable)
                return;
            var mode = isJump
                ? (short)(instruction.Name == "Entity_JumpAbsolute" ? -510 : -509)
                : unchecked((short)arguments[1].IntValue);
            var positionIndex = isJump ? 1 : 2;
            var authored = new Vector3(
                (float)arguments[positionIndex].FloatValue,
                (float)arguments[positionIndex + 1].FloatValue,
                (float)arguments[positionIndex + 2].FloatValue);
            if (!IsFinite(authored)) return;

            var jumpHeight = isJump ? (float)arguments[4].FloatValue : 0f;
            var speedIndex = isJump ? 5 : 5;
            var speed = (float)arguments[speedIndex].FloatValue;
            var animationState = arguments[speedIndex + 1].IntValue;
            var flags = arguments[speedIndex + 2].IntValue & ushort.MaxValue;
            if (!float.IsFinite(speed) || !float.IsFinite(jumpHeight)) return;
            if (!ScriptEntityMoveModeResolver.TryResolveTarget(
                    mode, entity, authored, out var target))
            {
                return;
            }

            var path = entity.HasPosition
                ? new[] { entity.Position, target }
                : new[] { target };
            var distance = CalculatePathLength(path);
            var durationFrames = speed > 0f
                ? Math.Max(0, (int)MathF.Ceiling(
                    distance / (speed * ScriptWaitDuration.SecondsPerPreviewFrame)))
                : 0;
            entities[entityId] = entity with
            {
                Position = target,
                PendingWaypoints = Array.Empty<Vector3>(),
                Motion = new ScriptEntityMotion(
                    mode,
                    speed,
                    animationState,
                    isJump ? flags | 0x20 : flags,
                    path,
                    elapsedFrames,
                    durationFrames,
                    jumpHeight),
                HasPosition = true,
            };
        }

        private void ApplyMovement(
            DecompiledInstruction instruction,
            int? selfEntityId)
        {
            var arguments = instruction.Arguments;
            if (arguments.Count < 1) return;
            var entityId = ResolveEntityId(arguments[0].IntValue, selfEntityId);
            if (!entities.TryGetValue(entityId, out var entity) || !entity.IsExecutable) return;
            switch (instruction.Name)
            {
                case "Entity_InitPath":
                    entities[entityId] = entity with
                    {
                        PendingWaypoints = new[] { entity.Position },
                        Motion = null,
                    };
                    break;
                case "Entity_AddWayPoint" when arguments.Count >= 4
                    && arguments.Skip(1).Take(3).All(value => value.Type == "f32"):
                {
                    var waypoint = new Vector3(
                        (float)arguments[1].FloatValue,
                        (float)arguments[2].FloatValue,
                        (float)arguments[3].FloatValue);
                    if (!IsFinite(waypoint)) return;
                    // The engine stores at most nine points. OP95_2 appends a path point;
                    // it does not itself commit the actor position.
                    var waypoints = entity.PendingWaypoints.Take(8).Append(waypoint).ToArray();
                    entities[entityId] = entity with { PendingWaypoints = waypoints };
                    break;
                }
                case "Entity_Move" or "Entity_Move2" when arguments.Count >= 4:
                {
                    var subtype = instruction.Name == "Entity_Move" ? 0 : 3;
                    var speed = (float)arguments[1].FloatValue;
                    var animationState = arguments[2].IntValue;
                    var authoredFlags = arguments[3].IntValue & ushort.MaxValue;
                    var effectiveFlags = authoredFlags | (subtype == 0 ? 0x08 : 0x18);
                    var path = entity.PendingWaypoints.Count >= 2
                        ? entity.PendingWaypoints.Take(9).ToArray()
                        : new[] { entity.Position };
                    var distance = CalculatePathLength(path);
                    var durationFrames = speed > 0f && float.IsFinite(speed)
                        ? Math.Max(0, (int)MathF.Ceiling(
                            distance / (speed * ScriptWaitDuration.SecondsPerPreviewFrame)))
                        : 0;
                    var finalPosition = path[^1];
                    entities[entityId] = entity with
                    {
                        Motion = new ScriptEntityMotion(
                            subtype,
                            speed,
                            animationState,
                            effectiveFlags,
                            path,
                            elapsedFrames,
                            durationFrames),
                        Position = finalPosition,
                        HasPosition = true,
                    };
                    break;
                }
            }
        }

        private void ApplyMovementControl(
            DecompiledInstruction instruction,
            int? selfEntityId)
        {
            var arguments = instruction.Arguments;
            if (arguments.Count < 1
                || !instruction.Name.Equals(
                    "Entity_StopMovement", StringComparison.Ordinal))
            {
                return;
            }
            var entityId = ResolveEntityId(arguments[0].IntValue, selfEntityId);
            if (!entities.TryGetValue(entityId, out var entity)
                || !entity.IsExecutable
                || entity.Motion is not { } motion)
            {
                return;
            }

            entities[entityId] = entity with
            {
                Position = motion.PositionAt(elapsedFrames),
                PendingWaypoints = Array.Empty<Vector3>(),
                Motion = null,
                HasPosition = true,
            };
        }

        private void ApplyWander(
            DecompiledInstruction instruction,
            int? selfEntityId)
        {
            var arguments = instruction.Arguments;
            if (arguments.Count < 6
                || arguments.Skip(1).Any(value => value.Type != "f32"))
            {
                return;
            }
            var entityId = ResolveEntityId(arguments[0].IntValue, selfEntityId);
            if (!entities.TryGetValue(entityId, out var entity) || !entity.IsExecutable) return;
            var center = new Vector3(
                (float)arguments[1].FloatValue,
                (float)arguments[2].FloatValue,
                (float)arguments[3].FloatValue);
            var radius = (float)arguments[4].FloatValue;
            var speed = (float)arguments[5].FloatValue;
            if (!IsFinite(center)
                || !float.IsFinite(radius)
                || !float.IsFinite(speed)
                || radius < 0f)
            {
                return;
            }

            var start = entity.HasPosition ? entity.Position : center;
            var random = new ScriptPreviewRandom(
                rootScript.SceneName, instruction.Offset, instruction.Index, entityId);
            var destination = center;
            for (var attempt = 0; attempt < 9; attempt++)
            {
                var sampledRadius = random.Next(radius);
                var angle = random.Next(360f) * MathF.PI / 180f;
                destination = center + new Vector3(
                    MathF.Cos(angle) * sampledRadius,
                    0f,
                    MathF.Sin(angle) * sampledRadius);
                if (Vector3.Distance(start, destination) >= 1f) break;
            }

            var path = new[] { start, destination };
            var distance = Vector3.Distance(start, destination);
            var durationFrames = speed > 0f
                ? Math.Max(0, (int)MathF.Ceiling(
                    distance / (speed * ScriptWaitDuration.SecondsPerPreviewFrame)))
                : 0;
            entities[entityId] = entity with
            {
                Position = destination,
                PendingWaypoints = Array.Empty<Vector3>(),
                Motion = new ScriptEntityMotion(
                    56,
                    speed,
                    AnimationState: 1,
                    Flags: 4,
                    path,
                    elapsedFrames,
                    durationFrames),
                HasPosition = true,
            };
        }

        private static float CalculatePathLength(IReadOnlyList<Vector3> path)
        {
            var length = 0f;
            for (var index = 1; index < path.Count; index++)
                length += Vector3.Distance(path[index - 1], path[index]);
            return length;
        }

        private static bool HasSpawnLayout(IReadOnlyList<InstructionArgument> arguments)
            => arguments.Count >= 20
               && arguments[0].Type == "s16"
               && arguments[1].Kind == "string"
               && arguments[2].Kind == "string"
               && arguments[3].Kind == "string"
               && arguments.Skip(6).Take(7).All(value => value.Type == "f32");

        private static IReadOnlyDictionary<int, ScriptEntityState> BuildSpawnCatalog(
            DecompiledScript script)
        {
            var catalog = new Dictionary<int, ScriptEntityState>();
            foreach (var instruction in script.Functions
                         .Where(value => value.IsCode)
                         .SelectMany(value => value.Instructions)
                         .Where(value => value.Opcode == 19))
            {
                if (TryCreateSpawnState(instruction, 0, out var entity))
                    catalog.TryAdd(entity.EntityId, entity);
            }
            return catalog;
        }

        private sealed record SpawnCatalog(
            IReadOnlyDictionary<int, ScriptEntityState> Entities);

        private static bool RequiresNonExecutableEntity(
            DecompiledInstruction instruction,
            int? selfEntityId)
            => instruction.Arguments.Any(argument =>
                argument.Kind == "scalar"
                && string.Equals(argument.Sem, "entity", StringComparison.OrdinalIgnoreCase)
                && ScriptEntityReferences.Resolve(
                    argument.IntValue, selfEntityId).Resolution
                    == ScriptEntityResolution.NonExecutable);

        internal static string ReadCallFunctionName(DecompiledInstruction instruction)
            => instruction.Arguments.FirstOrDefault(argument => argument.Kind == "string") is { } value
                ? ReadArgumentString(value)
                : string.Empty;

        internal static string ReadArgumentString(InstructionArgument argument)
            => ScriptEncoding.GetString(argument.Raw).TrimEnd('\0');

        private static int? ResolveInstructionEntityId(
            DecompiledInstruction instruction,
            int? selfEntityId)
        {
            var argument = instruction.Arguments.FirstOrDefault(value =>
                string.Equals(value.Sem, "entity", StringComparison.OrdinalIgnoreCase));
            return argument is null
                ? null
                : ResolveEntityId(argument.IntValue, selfEntityId);
        }

        /// <summary>
        /// The instructions the preview can show something for: a point carries a
        /// snapshot, so only what changes the scene earns one. Effects, equipment
        /// and expressions were modelled after this list was written and belong
        /// in it — without them the playback never stops on an effect.
        /// </summary>
        private static bool IsTimelineInstruction(DecompiledInstruction instruction)
            => instruction.Opcode is 2 or 8 or 16 or 19 or 32 or 34 or 35 or 36 or 37 or 39
                or 45 or 46 or 47 or 50 or 54 or 55 or 56 or 69 or 73 or 95;

        private static bool IsFinite(Vector3 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static Encoding CreateScriptEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    private struct ScriptPreviewRandom
    {
        private uint state;

        public ScriptPreviewRandom(
            string sceneName,
            int instructionOffset,
            int instructionIndex,
            int entityId)
        {
            state = 2166136261;
            foreach (var value in sceneName)
                Mix(ref state, value);
            Mix(ref state, instructionOffset);
            Mix(ref state, instructionIndex);
            Mix(ref state, entityId);
            if (state == 0) state = 0x6D2B79F5;
        }

        public float Next(float maximum)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return maximum * ((state >> 8) / 16777216f);
        }

        private static void Mix(ref uint hash, int value)
        {
            hash ^= unchecked((uint)value);
            hash *= 16777619;
        }
    }
}
