using System.Numerics;
using System.Globalization;
using ED8Editor.Core;

namespace ED8Editor.Scene;

[Flags]
public enum SceneTransformCapabilities
{
    None = 0,
    Translate = 1,
    Rotate = 2,
    Scale = 4,
    All = Translate | Rotate | Scale,
}

public sealed record SceneTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public Matrix4x4 ToMatrix()
        => Matrix4x4.CreateScale(Scale)
            * Matrix4x4.CreateFromQuaternion(Rotation)
            * Matrix4x4.CreateTranslation(Position);

    public static SceneTransform FromMapTransform(MapTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return new SceneTransform(transform.Position, transform.Rotation, transform.Scale);
    }
}

public sealed record EditableSceneElement(
    SceneElementSelection Selection,
    SceneTransformCapabilities Capabilities,
    SceneTransform Transform);

public sealed class EditorSceneDocument
{
    private readonly EditorSession session;
    private readonly Dictionary<SceneElementKey, ElementState> elements = new();
    private readonly Dictionary<int, SceneModelInstance> modelInstances;
    private readonly Dictionary<int, MapProp> props;
    private readonly Stack<EditCommand> undoCommands = new();
    private readonly Stack<EditCommand> redoCommands = new();

    public EditorSceneDocument(EditorSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        modelInstances = new EditorSceneFactory().Create(session).ToDictionary(value => value.Id);
        props = session.Map?.Props.ToDictionary(value => value.SourceIndex) ?? new Dictionary<int, MapProp>();
        foreach (var instance in modelInstances.Values)
        {
            var sourceProp = session.Map?.Props.FirstOrDefault(value => value.SourceIndex == instance.Id);
            if (sourceProp is not null)
            {
                Add(new SceneElementSelection(SceneElementKind.Prop, instance.Id, instance.Name), SceneTransformCapabilities.All,
                    SceneTransform.FromMapTransform(sourceProp.Transform));
                continue;
            }
            if (!Matrix4x4.Decompose(instance.Transform, out var scale, out var rotation, out var position))
            {
                throw new InvalidDataException($"Scene transform for prop {instance.Id} cannot be decomposed.");
            }
            Add(new SceneElementSelection(SceneElementKind.Prop, instance.Id, instance.Name), SceneTransformCapabilities.All,
                new SceneTransform(position, Quaternion.Normalize(rotation), scale));
        }
        if (session.Map is null) return;
        foreach (var prop in session.Map.Props)
        {
            var key = new SceneElementKey(SceneElementKind.Prop, prop.SourceIndex);
            if (elements.ContainsKey(key)) continue;
            Add(new SceneElementSelection(SceneElementKind.Prop, prop.SourceIndex, prop.Name), SceneTransformCapabilities.All,
                SceneTransform.FromMapTransform(prop.Transform));
        }
        foreach (var volume in session.Map.Volumes)
        {
            Add(
                new SceneElementSelection(
                    volume.Kind == MapVolumeKind.Entry ? SceneElementKind.EntryVolume : SceneElementKind.GroupVolume,
                    volume.SourceIndex,
                    volume.Name),
                SceneTransformCapabilities.All,
                SceneTransform.FromMapTransform(volume.Transform));
        }
        foreach (var point in session.Map.Points)
        {
            Add(new SceneElementSelection(SceneElementKind.LookPoint, point.SourceIndex, point.Name),
                SceneTransformCapabilities.Translate, IdentityAt(point.Position));
        }
        foreach (var camera in session.Map.Cameras)
        {
            Add(new SceneElementSelection(SceneElementKind.Camera, camera.SourceIndex, camera.Name),
                SceneTransformCapabilities.Translate, IdentityAt(camera.Eye));
        }
        foreach (var sound in session.Map.Sounds)
        {
            Add(new SceneElementSelection(SceneElementKind.Sound, sound.SourceIndex, sound.SoundName),
                SceneTransformCapabilities.Translate, IdentityAt(sound.Position));
        }
        foreach (var light in session.Map.Lights)
        {
            Add(new SceneElementSelection(SceneElementKind.Light, light.SourceIndex, $"Light {light.SourceIndex}"),
                SceneTransformCapabilities.Translate, IdentityAt(light.Position));
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<EditableSceneElement> Elements
        => elements.Values.Select(value => value.ToPublic()).ToArray();

    public bool CanUndo => undoCommands.Count != 0;
    public bool CanRedo => redoCommands.Count != 0;

    public EditableSceneElement? Find(SceneElementSelection selection)
        => elements.TryGetValue(SceneElementKey.From(selection), out var state) ? state.ToPublic() : null;

    public MapProp? FindProp(SceneElementSelection selection)
        => selection.Kind == SceneElementKind.Prop && props.TryGetValue(selection.SourceIndex, out var prop) ? prop : null;

    public bool ApplyPropAttributes(SceneElementSelection selection, IReadOnlyDictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (selection.Kind != SceneElementKind.Prop || !props.TryGetValue(selection.SourceIndex, out var before)) return false;
        var updated = new Dictionary<string, string>(attributes, StringComparer.Ordinal);
        foreach (var protectedName in new[] { "asset", "name", "pos", "rot", "scl" })
        {
            if (before.SourceAttributes.TryGetValue(protectedName, out var value)) updated[protectedName] = value;
        }
        var flags = ParseOptionalFlags(updated.GetValueOrDefault("flag"));
        var after = before with { Flags = flags, SourceAttributes = updated };
        if (before == after) return true;
        props[selection.SourceIndex] = after;
        undoCommands.Push(new EditCommand(
            () => props[selection.SourceIndex] = before,
            () => props[selection.SourceIndex] = after));
        redoCommands.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool ApplyTransform(SceneElementSelection selection, SceneTransform transform)
    {
        if (!TryValidateUpdate(selection, transform, out var state)) return false;
        if (state.Transform == transform) return true;
        var key = SceneElementKey.From(selection);
        var before = state.Transform;
        state.Transform = transform;
        undoCommands.Push(new EditCommand(
            () => elements[key].Transform = before,
            () => elements[key].Transform = transform));
        redoCommands.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool PreviewTransform(SceneElementSelection selection, SceneTransform transform)
    {
        if (!TryValidateUpdate(selection, transform, out var state)) return false;
        state.Transform = transform;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool CommitPreview(SceneElementSelection selection, SceneTransform originalTransform)
    {
        var key = SceneElementKey.From(selection);
        if (!elements.TryGetValue(key, out var state) || state.Transform == originalTransform) return false;
        ValidateTransform(originalTransform);
        var after = state.Transform;
        undoCommands.Push(new EditCommand(
            () => elements[key].Transform = originalTransform,
            () => elements[key].Transform = after));
        redoCommands.Clear();
        return true;
    }

    public bool Undo()
    {
        if (!undoCommands.TryPop(out var command)) return false;
        command.Undo();
        redoCommands.Push(command);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (!redoCommands.TryPop(out var command)) return false;
        command.Redo();
        undoCommands.Push(command);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public IReadOnlyList<SceneModelInstance> CreateModelInstances()
        => modelInstances.Values
            .Select(instance => instance with
            {
                Transform = elements[new SceneElementKey(SceneElementKind.Prop, instance.Id)].Transform.ToMatrix(),
            })
            .ToArray();

    public SceneElementSelection AddPropFromTemplate(
        SceneElementSelection templateSelection,
        string assetId,
        string name,
        CpuModel model)
    {
        if (templateSelection.Kind != SceneElementKind.Prop) throw new ArgumentException("A prop template is required.", nameof(templateSelection));
        if (!props.TryGetValue(templateSelection.SourceIndex, out var template)) throw new ArgumentException("Template prop does not exist.", nameof(templateSelection));
        if (string.IsNullOrWhiteSpace(assetId)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(assetId));
        ArgumentNullException.ThrowIfNull(model);
        var id = props.Count == 0 ? 0 : props.Keys.Max() + 1;
        var transform = elements[SceneElementKey.From(templateSelection)].Transform;
        var sourceAttributes = new Dictionary<string, string>(template.SourceAttributes, StringComparer.Ordinal)
        {
            ["asset"] = assetId,
            ["name"] = name,
        };
        var prop = template with
        {
            SourceIndex = id,
            AssetId = assetId,
            Name = name,
            Transform = ToMapTransform(template.Transform, transform),
            SourceAttributes = sourceAttributes,
        };
        var selection = new SceneElementSelection(SceneElementKind.Prop, id, name);
        var element = new ElementState(selection, SceneTransformCapabilities.All, transform);
        var instance = new SceneModelInstance(id, assetId, name, model, transform.ToMatrix());
        AddPropState(prop, element, instance);
        undoCommands.Push(new EditCommand(
            () => RemovePropState(id),
            () => AddPropState(prop, element, instance)));
        redoCommands.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        return selection;
    }

    public SceneElementSelection AddProp(
        string assetId,
        string name,
        CpuModel model,
        Vector3 position,
        OpsNewPropProfile? profile = null)
    {
        profile ??= OpsNewPropProfile.Neutral;
        var id = props.Count == 0 ? 0 : props.Keys.Max() + 1;
        var prop = profile.Create(id, assetId, name, model, position);
        var selection = new SceneElementSelection(SceneElementKind.Prop, id, name);
        var transform = SceneTransform.FromMapTransform(prop.Transform);
        var element = new ElementState(selection, SceneTransformCapabilities.All, transform);
        var instance = new SceneModelInstance(id, assetId, name, model, transform.ToMatrix());
        AddPropState(prop, element, instance);
        undoCommands.Push(new EditCommand(
            () => RemovePropState(id),
            () => AddPropState(prop, element, instance)));
        redoCommands.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        return selection;
    }

    public bool DeleteProp(SceneElementSelection selection)
    {
        if (selection.Kind != SceneElementKind.Prop
            || !props.TryGetValue(selection.SourceIndex, out var prop)
            || !elements.TryGetValue(SceneElementKey.From(selection), out var element)) return false;
        modelInstances.TryGetValue(selection.SourceIndex, out var instance);
        RemovePropState(selection.SourceIndex);
        undoCommands.Push(new EditCommand(
            () => AddPropState(prop, element, instance),
            () => RemovePropState(selection.SourceIndex)));
        redoCommands.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public MapScene? CreateMapSnapshot()
    {
        var map = session.Map;
        if (map is null) return null;
        return map with
        {
            Props = props.Values.OrderBy(prop => prop.SourceIndex).Select(prop => prop with
            {
                Transform = ToMapTransform(prop.Transform, GetTransform(SceneElementKind.Prop, prop.SourceIndex)),
            }).ToArray(),
            Volumes = map.Volumes.Select(volume => volume with
            {
                Transform = ToMapTransform(
                    volume.Transform,
                    GetTransform(
                        volume.Kind == MapVolumeKind.Entry ? SceneElementKind.EntryVolume : SceneElementKind.GroupVolume,
                        volume.SourceIndex)),
            }).ToArray(),
            Points = map.Points.Select(point => point with
            {
                Position = GetTransform(SceneElementKind.LookPoint, point.SourceIndex).Position,
            }).ToArray(),
            Cameras = map.Cameras.Select(camera =>
            {
                var position = GetTransform(SceneElementKind.Camera, camera.SourceIndex).Position;
                var translation = position - camera.Eye;
                return camera with { Eye = position, LookAt = camera.LookAt + translation };
            }).ToArray(),
            Sounds = map.Sounds.Select(sound => sound with
            {
                Position = GetTransform(SceneElementKind.Sound, sound.SourceIndex).Position,
            }).ToArray(),
            Lights = map.Lights.Select(light => light with
            {
                Position = GetTransform(SceneElementKind.Light, light.SourceIndex).Position,
            }).ToArray(),
        };
    }

    private void Add(SceneElementSelection selection, SceneTransformCapabilities capabilities, SceneTransform transform)
    {
        ValidateTransform(transform);
        elements.Add(SceneElementKey.From(selection), new ElementState(selection, capabilities, transform));
    }

    private void AddPropState(MapProp prop, ElementState element, SceneModelInstance? instance)
    {
        props.Add(prop.SourceIndex, prop);
        elements.Add(new SceneElementKey(SceneElementKind.Prop, prop.SourceIndex), element);
        if (instance is not null) modelInstances.Add(prop.SourceIndex, instance);
    }

    private void RemovePropState(int id)
    {
        props.Remove(id);
        elements.Remove(new SceneElementKey(SceneElementKind.Prop, id));
        modelInstances.Remove(id);
    }

    private SceneTransform GetTransform(SceneElementKind kind, int sourceIndex)
        => elements[new SceneElementKey(kind, sourceIndex)].Transform;

    private bool TryValidateUpdate(SceneElementSelection selection, SceneTransform transform, out ElementState state)
    {
        ValidateTransform(transform);
        if (!elements.TryGetValue(SceneElementKey.From(selection), out state!)) return false;
        var changedPosition = transform.Position != state.Transform.Position;
        var changedRotation = transform.Rotation != state.Transform.Rotation;
        var changedScale = transform.Scale != state.Transform.Scale;
        if (changedPosition && !state.Capabilities.HasFlag(SceneTransformCapabilities.Translate)
            || changedRotation && !state.Capabilities.HasFlag(SceneTransformCapabilities.Rotate)
            || changedScale && !state.Capabilities.HasFlag(SceneTransformCapabilities.Scale))
        {
            return false;
        }
        return true;
    }

    private static MapTransform ToMapTransform(MapTransform source, SceneTransform transform)
        => source with { Position = transform.Position, Rotation = transform.Rotation, Scale = transform.Scale };

    private static SceneTransform IdentityAt(Vector3 position)
        => new(position, Quaternion.Identity, Vector3.One);

    private static void ValidateTransform(SceneTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (!IsFinite(transform.Position) || !IsFinite(transform.Scale)
            || !float.IsFinite(transform.Rotation.X) || !float.IsFinite(transform.Rotation.Y)
            || !float.IsFinite(transform.Rotation.Z) || !float.IsFinite(transform.Rotation.W)
            || transform.Rotation.LengthSquared() == 0f)
        {
            throw new ArgumentException("Scene transform must contain finite position, rotation and scale values.", nameof(transform));
        }
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static uint? ParseOptionalFlags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var style = NumberStyles.Integer;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
            style = NumberStyles.AllowHexSpecifier;
        }
        if (!uint.TryParse(value, style, CultureInfo.InvariantCulture, out var flags))
        {
            throw new ArgumentException($"Invalid OPS flag value '{value}'.", nameof(value));
        }
        return flags;
    }

    private readonly record struct SceneElementKey(SceneElementKind Kind, int SourceIndex)
    {
        public static SceneElementKey From(SceneElementSelection selection)
            => new(selection.Kind, selection.SourceIndex);
    }

    private sealed class ElementState
    {
        public ElementState(SceneElementSelection selection, SceneTransformCapabilities capabilities, SceneTransform transform)
        {
            Selection = selection;
            Capabilities = capabilities;
            Transform = transform;
        }

        public SceneElementSelection Selection { get; }
        public SceneTransformCapabilities Capabilities { get; }
        public SceneTransform Transform { get; set; }

        public EditableSceneElement ToPublic() => new(Selection, Capabilities, Transform);
    }

    private sealed record EditCommand(Action Undo, Action Redo);
}
