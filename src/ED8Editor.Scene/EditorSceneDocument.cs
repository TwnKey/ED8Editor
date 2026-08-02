using System.Numerics;
using System.Globalization;
using ED8Editor.Core;
using ED8Editor.Ops;

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

public sealed record SceneElementAttributes(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlySet<string> ProtectedNames);

public sealed class EditorSceneDocument
{
    private readonly EditorSession session;
    private readonly Dictionary<SceneElementKey, ElementState> elements = new();
    private readonly Dictionary<int, SceneModelInstance> modelInstances;
    private readonly Dictionary<int, MapProp> props;
    private readonly Dictionary<SceneElementKey, MapVolume> volumes;
    private readonly Dictionary<int, MapPoint> points;
    private readonly Dictionary<int, MapCameraMarker> cameras;
    private readonly Dictionary<int, MapSoundMarker> sounds;
    private readonly Dictionary<int, MapLightMarker> lights;
    private readonly Stack<EditCommand> undoCommands = new();
    private readonly Stack<EditCommand> redoCommands = new();
    private readonly OpsSpatialAttributeCodec spatialAttributeCodec = new();
    private long nextStateId;
    private long currentStateId;
    private long savedStateId;

    public EditorSceneDocument(EditorSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        modelInstances = new EditorSceneFactory().Create(session).ToDictionary(value => value.Id);
        props = session.Map?.Props.ToDictionary(value => value.SourceIndex) ?? new Dictionary<int, MapProp>();
        volumes = session.Map?.Volumes.ToDictionary(
            value => new SceneElementKey(
                value.Kind == MapVolumeKind.Entry ? SceneElementKind.EntryVolume : SceneElementKind.GroupVolume,
                value.SourceIndex)) ?? new Dictionary<SceneElementKey, MapVolume>();
        points = session.Map?.Points.ToDictionary(value => value.SourceIndex) ?? new Dictionary<int, MapPoint>();
        cameras = session.Map?.Cameras.ToDictionary(value => value.SourceIndex) ?? new Dictionary<int, MapCameraMarker>();
        sounds = session.Map?.Sounds.ToDictionary(value => value.SourceIndex) ?? new Dictionary<int, MapSoundMarker>();
        lights = session.Map?.Lights.ToDictionary(value => value.SourceIndex) ?? new Dictionary<int, MapLightMarker>();
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
    public event EventHandler? PreviewChanged;

    public IReadOnlyList<EditableSceneElement> Elements
        => elements.Values.Select(value => value.ToPublic()).ToArray();

    public bool CanUndo => undoCommands.Count != 0;
    public bool CanRedo => redoCommands.Count != 0;
    public bool IsDirty => currentStateId != savedStateId;

    public void MarkSaved()
    {
        savedStateId = currentStateId;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public EditableSceneElement? Find(SceneElementSelection selection)
        => elements.TryGetValue(SceneElementKey.From(selection), out var state) ? state.ToPublic() : null;

    /// <summary>
    /// Which asset a selected prop draws, whether it came from the map's prop list
    /// or from a model instance the scene built. Both are selected as props and
    /// their ids live in the same space, but only the first is in the prop list —
    /// so asking the prop list alone misses whatever the scene placed itself.
    /// </summary>
    public string? FindAssetId(SceneElementSelection selection)
    {
        if (selection.Kind != SceneElementKind.Prop) return null;
        if (props.TryGetValue(selection.SourceIndex, out var prop)) return prop.AssetId;
        return modelInstances.TryGetValue(selection.SourceIndex, out var instance)
            ? instance.AssetId
            : null;
    }

    public MapProp? FindProp(SceneElementSelection selection)
        => selection.Kind == SceneElementKind.Prop && props.TryGetValue(selection.SourceIndex, out var prop) ? prop : null;

    public MapCameraMarker? FindCamera(SceneElementSelection selection)
    {
        if (selection.Kind != SceneElementKind.Camera
            || !cameras.TryGetValue(selection.SourceIndex, out var camera)
            || !elements.TryGetValue(SceneElementKey.From(selection), out var element)) return null;
        var translation = element.Transform.Position - camera.Eye;
        return camera with { Eye = element.Transform.Position, LookAt = camera.LookAt + translation };
    }

    public bool PreviewCameraLookAt(SceneElementSelection selection, Vector3 lookAt)
    {
        if (selection.Kind != SceneElementKind.Camera || !IsFinite(lookAt)
            || !cameras.TryGetValue(selection.SourceIndex, out var camera)
            || !elements.TryGetValue(SceneElementKey.From(selection), out var element)) return false;
        var translation = element.Transform.Position - camera.Eye;
        cameras[selection.SourceIndex] = camera with { LookAt = lookAt - translation };
        PreviewChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool CommitCameraLookAtPreview(SceneElementSelection selection, Vector3 originalLookAt)
    {
        if (selection.Kind != SceneElementKind.Camera || !IsFinite(originalLookAt)
            || !cameras.TryGetValue(selection.SourceIndex, out var after)
            || !elements.TryGetValue(SceneElementKey.From(selection), out var element)) return false;
        var translation = element.Transform.Position - after.Eye;
        var currentLookAt = after.LookAt + translation;
        if (currentLookAt == originalLookAt) return false;
        var before = after with { LookAt = originalLookAt - translation };
        PushCommand(
            () => cameras[selection.SourceIndex] = before,
            () => cameras[selection.SourceIndex] = after);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public SceneElementAttributes? FindElementAttributes(SceneElementSelection selection)
    {
        var key = SceneElementKey.From(selection);
        return selection.Kind switch
        {
            SceneElementKind.Prop when props.TryGetValue(selection.SourceIndex, out var prop)
                => AttributeSet(prop.SourceAttributes, "asset", "name", "pos", "rot", "scl"),
            SceneElementKind.EntryVolume or SceneElementKind.GroupVolume when volumes.TryGetValue(key, out var volume)
                => AttributeSet(spatialAttributeCodec.GetEditableAttributes(volume with
                {
                    Transform = ToMapTransform(volume.Transform, elements[key].Transform),
                })),
            SceneElementKind.LookPoint when points.TryGetValue(selection.SourceIndex, out var point)
                => AttributeSet(spatialAttributeCodec.GetEditableAttributes(point with
                {
                    Position = elements[key].Transform.Position,
                })),
            SceneElementKind.Camera when cameras.TryGetValue(selection.SourceIndex, out var camera)
                => AttributeSet(
                    spatialAttributeCodec.GetEditableAttributes(FindCamera(selection)!),
                    "no"),
            SceneElementKind.Sound when sounds.TryGetValue(selection.SourceIndex, out var sound)
                => AttributeSet(spatialAttributeCodec.GetEditableAttributes(sound with
                {
                    Position = elements[key].Transform.Position,
                })),
            SceneElementKind.Light when lights.TryGetValue(selection.SourceIndex, out var light)
                => AttributeSet(spatialAttributeCodec.GetEditableAttributes(light with
                {
                    Position = elements[key].Transform.Position,
                })),
            _ => null,
        };
    }

    public bool ApplyElementAttributes(
        SceneElementSelection selection,
        IReadOnlyDictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (selection.Kind == SceneElementKind.Prop) return ApplyPropAttributes(selection, attributes);
        var key = SceneElementKey.From(selection);
        Action undo;
        Action redo;
        IReadOnlyDictionary<string, string> beforeAttributes;
        IReadOnlyDictionary<string, string> afterAttributes;
        switch (selection.Kind)
        {
            case SceneElementKind.EntryVolume:
            case SceneElementKind.GroupVolume:
                if (!volumes.TryGetValue(key, out var beforeVolume)) return false;
                var afterVolume = spatialAttributeCodec.Apply(beforeVolume, attributes);
                beforeAttributes = beforeVolume.SourceAttributes;
                afterAttributes = afterVolume.SourceAttributes;
                undo = () =>
                {
                    volumes[key] = beforeVolume;
                    SetElementName(key, beforeVolume.Name);
                    SetElementTransform(key, SceneTransform.FromMapTransform(beforeVolume.Transform));
                };
                redo = () =>
                {
                    volumes[key] = afterVolume;
                    SetElementName(key, afterVolume.Name);
                    SetElementTransform(key, SceneTransform.FromMapTransform(afterVolume.Transform));
                };
                break;
            case SceneElementKind.LookPoint:
                if (!points.TryGetValue(selection.SourceIndex, out var beforePoint)) return false;
                var afterPoint = spatialAttributeCodec.Apply(beforePoint, attributes);
                beforeAttributes = beforePoint.SourceAttributes;
                afterAttributes = afterPoint.SourceAttributes;
                undo = () =>
                {
                    points[selection.SourceIndex] = beforePoint;
                    SetElementName(key, beforePoint.Name);
                    SetElementTransform(key, IdentityAt(beforePoint.Position));
                };
                redo = () =>
                {
                    points[selection.SourceIndex] = afterPoint;
                    SetElementName(key, afterPoint.Name);
                    SetElementTransform(key, IdentityAt(afterPoint.Position));
                };
                break;
            case SceneElementKind.Camera:
                if (!cameras.TryGetValue(selection.SourceIndex, out var beforeCamera)) return false;
                var afterCamera = spatialAttributeCodec.Apply(beforeCamera, attributes);
                beforeAttributes = beforeCamera.SourceAttributes;
                afterAttributes = afterCamera.SourceAttributes;
                undo = () =>
                {
                    cameras[selection.SourceIndex] = beforeCamera;
                    SetElementTransform(key, IdentityAt(beforeCamera.Eye));
                };
                redo = () =>
                {
                    cameras[selection.SourceIndex] = afterCamera;
                    SetElementTransform(key, IdentityAt(afterCamera.Eye));
                };
                break;
            case SceneElementKind.Sound:
                if (!sounds.TryGetValue(selection.SourceIndex, out var beforeSound)) return false;
                var afterSound = spatialAttributeCodec.Apply(beforeSound, attributes);
                beforeAttributes = beforeSound.SourceAttributes;
                afterAttributes = afterSound.SourceAttributes;
                undo = () =>
                {
                    sounds[selection.SourceIndex] = beforeSound;
                    SetElementName(key, beforeSound.SoundName);
                    SetElementTransform(key, IdentityAt(beforeSound.Position));
                };
                redo = () =>
                {
                    sounds[selection.SourceIndex] = afterSound;
                    SetElementName(key, afterSound.SoundName);
                    SetElementTransform(key, IdentityAt(afterSound.Position));
                };
                break;
            case SceneElementKind.Light:
                if (!lights.TryGetValue(selection.SourceIndex, out var beforeLight)) return false;
                var afterLight = spatialAttributeCodec.Apply(beforeLight, attributes);
                beforeAttributes = beforeLight.SourceAttributes;
                afterAttributes = afterLight.SourceAttributes;
                undo = () =>
                {
                    lights[selection.SourceIndex] = beforeLight;
                    SetElementTransform(key, IdentityAt(beforeLight.Position));
                };
                redo = () =>
                {
                    lights[selection.SourceIndex] = afterLight;
                    SetElementTransform(key, IdentityAt(afterLight.Position));
                };
                break;
            default:
                return false;
        }
        if (AttributesEqual(beforeAttributes, afterAttributes)) return true;
        redo();
        PushCommand(undo, redo);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void SetElementName(SceneElementKey key, string name)
    {
        if (elements.TryGetValue(key, out var state))
            state.Selection = state.Selection with { Name = name };
    }

    private void SetElementTransform(SceneElementKey key, SceneTransform transform)
    {
        if (elements.TryGetValue(key, out var state)) state.Transform = transform;
    }

    public bool ApplyPropAttributes(SceneElementSelection selection, IReadOnlyDictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        OpsSpatialAttributeCodec.ValidateAttributeNames(attributes);
        if (selection.Kind != SceneElementKind.Prop || !props.TryGetValue(selection.SourceIndex, out var before)) return false;
        var updated = new Dictionary<string, string>(attributes, StringComparer.Ordinal);
        foreach (var protectedName in new[] { "asset", "name", "pos", "rot", "scl" })
        {
            if (before.SourceAttributes.TryGetValue(protectedName, out var value)) updated[protectedName] = value;
        }
        var flags = ParseOptionalFlags(updated.GetValueOrDefault("flag"));
        var after = before with { Flags = flags, SourceAttributes = updated };
        if (before.Flags == after.Flags && AttributesEqual(before.SourceAttributes, after.SourceAttributes)) return true;
        props[selection.SourceIndex] = after;
        PushCommand(
            () => props[selection.SourceIndex] = before,
            () => props[selection.SourceIndex] = after);
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
        PushCommand(
            () => elements[key].Transform = before,
            () => elements[key].Transform = transform);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool PreviewTransform(SceneElementSelection selection, SceneTransform transform)
    {
        if (!TryValidateUpdate(selection, transform, out var state)) return false;
        state.Transform = transform;
        PreviewChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool CommitPreview(SceneElementSelection selection, SceneTransform originalTransform)
    {
        var key = SceneElementKey.From(selection);
        if (!elements.TryGetValue(key, out var state) || state.Transform == originalTransform) return false;
        ValidateTransform(originalTransform);
        var after = state.Transform;
        PushCommand(
            () => elements[key].Transform = originalTransform,
            () => elements[key].Transform = after);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Undo()
    {
        if (!undoCommands.TryPop(out var command)) return false;
        command.Undo();
        currentStateId = command.BeforeStateId;
        redoCommands.Push(command);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (!redoCommands.TryPop(out var command)) return false;
        command.Redo();
        currentStateId = command.AfterStateId;
        undoCommands.Push(command);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public IReadOnlyList<SceneModelInstance> CreateModelInstances()
        => modelInstances.Values
            .Select(instance => instance with
            {
                Transform = elements[new SceneElementKey(SceneElementKind.Prop, instance.Id)].Transform.ToMatrix(),
                MaterialDiffuse = props.TryGetValue(instance.Id, out var prop) ? prop.MaterialDiffuse : instance.MaterialDiffuse,
                MaterialEmission = props.TryGetValue(instance.Id, out prop) ? prop.MaterialEmission : instance.MaterialEmission,
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
        name = CreateUniquePropName(name);
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
        var instance = new SceneModelInstance(id, assetId, name, model, transform.ToMatrix(), prop.MaterialDiffuse, prop.MaterialEmission);
        AddPropState(prop, element, instance);
        PushCommand(
            () => RemovePropState(id),
            () => AddPropState(prop, element, instance));
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
        name = CreateUniquePropName(name);
        var id = props.Count == 0 ? 0 : props.Keys.Max() + 1;
        var prop = profile.Create(id, assetId, name, model, position);
        var selection = new SceneElementSelection(SceneElementKind.Prop, id, name);
        var transform = SceneTransform.FromMapTransform(prop.Transform);
        var element = new ElementState(selection, SceneTransformCapabilities.All, transform);
        var instance = new SceneModelInstance(id, assetId, name, model, transform.ToMatrix(), prop.MaterialDiffuse, prop.MaterialEmission);
        AddPropState(prop, element, instance);
        PushCommand(
            () => RemovePropState(id),
            () => AddPropState(prop, element, instance));
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
        PushCommand(
            () => AddPropState(prop, element, instance),
            () => RemovePropState(selection.SourceIndex));
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public SceneElementSelection DuplicateElement(SceneElementSelection selection)
    {
        if (selection.Kind == SceneElementKind.Prop)
        {
            throw new ArgumentException("Props require their loaded model when duplicated.", nameof(selection));
        }
        var sourceKey = SceneElementKey.From(selection);
        if (!elements.TryGetValue(sourceKey, out var sourceElement))
        {
            throw new ArgumentException("Scene element does not exist.", nameof(selection));
        }
        var id = NextSourceIndex(selection.Kind);
        var displayName = CreateUniqueElementName(
            selection.Kind, sourceElement.Selection.Name);
        var duplicatedSelection = new SceneElementSelection(selection.Kind, id, displayName);
        var duplicatedElement = new ElementState(duplicatedSelection, sourceElement.Capabilities, sourceElement.Transform);
        Action addState;
        Action removeState;
        switch (selection.Kind)
        {
            case SceneElementKind.EntryVolume:
            case SceneElementKind.GroupVolume:
            {
                var source = volumes[sourceKey];
                var attributes = WithAttribute(source.SourceAttributes, "name", displayName);
                var duplicate = source with { SourceIndex = id, Name = displayName, SourceAttributes = attributes };
                var key = SceneElementKey.From(duplicatedSelection);
                addState = () => { volumes.Add(key, duplicate); elements.Add(key, duplicatedElement); };
                removeState = () => { volumes.Remove(key); elements.Remove(key); };
                break;
            }
            case SceneElementKind.LookPoint:
            {
                var source = points[selection.SourceIndex];
                var attributes = WithAttribute(source.SourceAttributes, "name", displayName);
                var duplicate = source with { SourceIndex = id, Name = displayName, SourceAttributes = attributes };
                addState = () => { points.Add(id, duplicate); elements.Add(SceneElementKey.From(duplicatedSelection), duplicatedElement); };
                removeState = () => { points.Remove(id); elements.Remove(SceneElementKey.From(duplicatedSelection)); };
                break;
            }
            case SceneElementKind.Camera:
            {
                var source = cameras[selection.SourceIndex];
                var duplicate = source with { SourceIndex = id };
                addState = () => { cameras.Add(id, duplicate); elements.Add(SceneElementKey.From(duplicatedSelection), duplicatedElement); };
                removeState = () => { cameras.Remove(id); elements.Remove(SceneElementKey.From(duplicatedSelection)); };
                break;
            }
            case SceneElementKind.Sound:
            {
                var duplicate = sounds[selection.SourceIndex] with { SourceIndex = id };
                addState = () => { sounds.Add(id, duplicate); elements.Add(SceneElementKey.From(duplicatedSelection), duplicatedElement); };
                removeState = () => { sounds.Remove(id); elements.Remove(SceneElementKey.From(duplicatedSelection)); };
                break;
            }
            case SceneElementKind.Light:
            {
                var duplicate = lights[selection.SourceIndex] with { SourceIndex = id };
                addState = () => { lights.Add(id, duplicate); elements.Add(SceneElementKey.From(duplicatedSelection), duplicatedElement); };
                removeState = () => { lights.Remove(id); elements.Remove(SceneElementKey.From(duplicatedSelection)); };
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(selection));
        }
        addState();
        PushCommand(removeState, addState);
        Changed?.Invoke(this, EventArgs.Empty);
        return duplicatedSelection;
    }

    public SceneElementSelection AddSpatialElement(
        OpsSpatialCreationProfile profile,
        Vector3 position,
        IReadOnlyDictionary<string, string> inputs)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!IsFinite(position)) throw new ArgumentOutOfRangeException(nameof(position));
        ArgumentNullException.ThrowIfNull(inputs);
        var id = NextSourceIndex(profile.Kind);
        var name = CreateUniqueElementName(profile.Kind, profile.CreateBaseName(inputs));
        var draft = profile.Create(id, name, position, inputs);
        if (draft.Selection.Kind != profile.Kind || draft.Selection.SourceIndex != id)
        {
            throw new InvalidDataException($"OPS creation profile '{profile.Id}' returned an inconsistent element.");
        }
        var key = SceneElementKey.From(draft.Selection);
        var element = new ElementState(draft.Selection, draft.Capabilities, draft.Transform);
        Action addState;
        Action removeState;
        if (draft.Volume is not null)
        {
            addState = () => { volumes.Add(key, draft.Volume); elements.Add(key, element); };
            removeState = () => { volumes.Remove(key); elements.Remove(key); };
        }
        else if (draft.Point is not null)
        {
            addState = () => { points.Add(id, draft.Point); elements.Add(key, element); };
            removeState = () => { points.Remove(id); elements.Remove(key); };
        }
        else if (draft.Camera is not null)
        {
            addState = () => { cameras.Add(id, draft.Camera); elements.Add(key, element); };
            removeState = () => { cameras.Remove(id); elements.Remove(key); };
        }
        else if (draft.Sound is not null)
        {
            addState = () => { sounds.Add(id, draft.Sound); elements.Add(key, element); };
            removeState = () => { sounds.Remove(id); elements.Remove(key); };
        }
        else if (draft.Light is not null)
        {
            addState = () => { lights.Add(id, draft.Light); elements.Add(key, element); };
            removeState = () => { lights.Remove(id); elements.Remove(key); };
        }
        else
        {
            throw new InvalidDataException($"OPS creation profile '{profile.Id}' returned no spatial entity.");
        }
        addState();
        PushCommand(removeState, addState);
        Changed?.Invoke(this, EventArgs.Empty);
        return draft.Selection;
    }

    public bool DeleteElement(SceneElementSelection selection)
    {
        if (selection.Kind == SceneElementKind.Prop) return DeleteProp(selection);
        var key = SceneElementKey.From(selection);
        if (!elements.TryGetValue(key, out var element)) return false;
        Action addState;
        Action removeState;
        switch (selection.Kind)
        {
            case SceneElementKind.EntryVolume:
            case SceneElementKind.GroupVolume:
                if (!volumes.TryGetValue(key, out var volume)) return false;
                addState = () => { volumes.Add(key, volume); elements.Add(key, element); };
                removeState = () => { volumes.Remove(key); elements.Remove(key); };
                break;
            case SceneElementKind.LookPoint:
                if (!points.TryGetValue(selection.SourceIndex, out var point)) return false;
                addState = () => { points.Add(selection.SourceIndex, point); elements.Add(key, element); };
                removeState = () => { points.Remove(selection.SourceIndex); elements.Remove(key); };
                break;
            case SceneElementKind.Camera:
                if (!cameras.TryGetValue(selection.SourceIndex, out var camera)) return false;
                addState = () => { cameras.Add(selection.SourceIndex, camera); elements.Add(key, element); };
                removeState = () => { cameras.Remove(selection.SourceIndex); elements.Remove(key); };
                break;
            case SceneElementKind.Sound:
                if (!sounds.TryGetValue(selection.SourceIndex, out var sound)) return false;
                addState = () => { sounds.Add(selection.SourceIndex, sound); elements.Add(key, element); };
                removeState = () => { sounds.Remove(selection.SourceIndex); elements.Remove(key); };
                break;
            case SceneElementKind.Light:
                if (!lights.TryGetValue(selection.SourceIndex, out var light)) return false;
                addState = () => { lights.Add(selection.SourceIndex, light); elements.Add(key, element); };
                removeState = () => { lights.Remove(selection.SourceIndex); elements.Remove(key); };
                break;
            default:
                return false;
        }
        removeState();
        PushCommand(addState, removeState);
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
            Volumes = volumes.Values.OrderBy(volume => volume.Kind).ThenBy(volume => volume.SourceIndex).Select(volume => volume with
            {
                Transform = ToMapTransform(
                    volume.Transform,
                    GetTransform(
                        volume.Kind == MapVolumeKind.Entry ? SceneElementKind.EntryVolume : SceneElementKind.GroupVolume,
                        volume.SourceIndex)),
            }).ToArray(),
            Points = points.Values.OrderBy(point => point.SourceIndex).Select(point => point with
            {
                Position = GetTransform(SceneElementKind.LookPoint, point.SourceIndex).Position,
            }).ToArray(),
            Cameras = cameras.Values.OrderBy(camera => camera.SourceIndex).Select(camera =>
            {
                var position = GetTransform(SceneElementKind.Camera, camera.SourceIndex).Position;
                var translation = position - camera.Eye;
                return camera with { Eye = position, LookAt = camera.LookAt + translation };
            }).ToArray(),
            Sounds = sounds.Values.OrderBy(sound => sound.SourceIndex).Select(sound => sound with
            {
                Position = GetTransform(SceneElementKind.Sound, sound.SourceIndex).Position,
            }).ToArray(),
            Lights = lights.Values.OrderBy(light => light.SourceIndex).Select(light => light with
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

    private void PushCommand(Action undo, Action redo)
    {
        var beforeStateId = currentStateId;
        var afterStateId = ++nextStateId;
        undoCommands.Push(new EditCommand(undo, redo, beforeStateId, afterStateId));
        redoCommands.Clear();
        currentStateId = afterStateId;
    }

    private string CreateUniquePropName(string requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(requestedName));
        var existingNames = props.Values.Select(prop => prop.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existingNames.Contains(requestedName)) return requestedName;
        for (var suffix = 1; suffix < int.MaxValue; suffix++)
        {
            var candidate = $"{requestedName}_{suffix:000}";
            if (!existingNames.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException($"Cannot create a unique prop name from '{requestedName}'.");
    }

    private int NextSourceIndex(SceneElementKind kind)
    {
        var ids = elements.Keys.Where(key => key.Kind == kind).Select(key => key.SourceIndex);
        return ids.Any() ? ids.Max() + 1 : 0;
    }

    private string CreateUniqueElementName(SceneElementKind kind, string requestedName)
    {
        var names = elements.Values
            .Where(element => element.Selection.Kind == kind)
            .Select(element => element.Selection.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(requestedName)) return requestedName;
        for (var suffix = 1; suffix < int.MaxValue; suffix++)
        {
            var candidate = $"{requestedName}_{suffix:000}";
            if (!names.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException($"Cannot create a unique element name from '{requestedName}'.");
    }

    private static IReadOnlyDictionary<string, string> WithAttribute(
        IReadOnlyDictionary<string, string> source,
        string name,
        string value)
    {
        var attributes = new Dictionary<string, string>(source, StringComparer.Ordinal) { [name] = value };
        return attributes;
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

    private static bool AttributesEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static SceneElementAttributes AttributeSet(
        IReadOnlyDictionary<string, string> attributes,
        params string[] protectedNames)
        => new(attributes, protectedNames.ToHashSet(StringComparer.Ordinal));

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

        public SceneElementSelection Selection { get; set; }
        public SceneTransformCapabilities Capabilities { get; }
        public SceneTransform Transform { get; set; }

        public EditableSceneElement ToPublic() => new(Selection, Capabilities, Transform);
    }

    private sealed record EditCommand(Action Undo, Action Redo, long BeforeStateId, long AfterStateId);
}
