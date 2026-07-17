namespace ED8Editor.Scene;

public sealed record SceneOutlinerGroup(
    string Name,
    string ElementTypeName,
    IReadOnlyList<SceneElementSelection> Elements);

public sealed class SceneOutlinerBuilder
{
    private static readonly (SceneElementKind Kind, string Name, string ElementTypeName)[] Groups =
    {
        (SceneElementKind.Prop, "Props", "Prop"),
        (SceneElementKind.EntryVolume, "Entry / TP volumes", "Entry / TP volume"),
        (SceneElementKind.GroupVolume, "Group volumes", "Group volume"),
        (SceneElementKind.LookPoint, "Look points", "Look point"),
        (SceneElementKind.Camera, "Cameras", "Camera"),
        (SceneElementKind.Sound, "Sounds", "Sound"),
        (SceneElementKind.Light, "Lights", "Light"),
    };

    public IReadOnlyList<SceneOutlinerGroup> Build(IEnumerable<EditableSceneElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var selections = elements.Select(element => element.Selection).ToArray();
        return Groups
            .Select(group => new SceneOutlinerGroup(
                group.Name,
                group.ElementTypeName,
                selections
                    .Where(selection => selection.Kind == group.Kind)
                    .OrderBy(selection => selection.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(selection => selection.SourceIndex)
                    .ToArray()))
            .Where(group => group.Elements.Count != 0)
            .ToArray();
    }
}
