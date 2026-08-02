using System.Globalization;
using System.Numerics;
using System.Text;

namespace ED8Editor.Application;

/// <summary>
/// What a new map is set to. Every one of these is a number an author can see and
/// change, which is the point: a map copied from another carries settings nobody
/// can account for.
/// </summary>
/// <param name="EntrySize">
/// How big the box is that the player arrives in and leaves through.
/// </param>
/// <param name="Skybox">
/// The sky the map stands under, named by its package. The game ships a set of
/// them — O_S00SKY00 is the one 81 of its maps use, O_S00SKY01 an evening, and so
/// on up to SKY12 — and a map declares one as an object called <c>sky</c> with
/// flag 0x1. Empty means no sky at all, which is what an interior does.
/// </param>
public sealed record MapSettings(
    Vector3 EntryPosition,
    Vector3 EntrySize,
    float FogNear = 5f,
    float FogFar = 150f,
    Vector3 FogColour = default,
    Vector3 Ambient = default,
    float CameraDistance = 6f,
    float CameraFieldOfView = 30f,
    string Skybox = "O_S00SKY00")
{
    public static MapSettings Default => new(
        Vector3.Zero,
        new Vector3(2f, 3f, 2f),
        FogColour: new Vector3(0.9f, 0.9f, 0.9f),
        Ambient: new Vector3(0.55f, 0.55f, 0.55f));

    /// <summary>
    /// The same settings, with the arrival point put where the model actually is.
    ///
    /// Leaving it at the origin looks like an empty map: a model whose geometry
    /// runs from Z -465 to +34 has nothing at zero, so the camera arrives beside
    /// it looking at the entry box and nothing else. The middle of the model in X
    /// and Z, a little above its lowest point, is somewhere a player can be.
    ///
    /// It is the bounding box, not the floor — on a model with a pit in the middle
    /// this puts the player over the pit. Moving the box afterwards is one edit in
    /// the scene view.
    /// </summary>
    public MapSettings ArrivingIn(Vector3 lowest, Vector3 highest)
    {
        var middle = (lowest + highest) / 2f;
        return this with
        {
            EntryPosition = new Vector3(middle.X, lowest.Y + EntrySize.Y, middle.Z),
        };
    }
}

/// <summary>
/// Writes a map's <c>.ops</c> from nothing.
///
/// An <c>.ops</c> is UTF-8 XML — the editor reads and writes every field of one
/// already — so there is no reason a new map should inherit another's camera, fog
/// and lighting without being told. Everything here is stated.
///
/// What a map needs before it will come up: a camera, a fog range, a light, and at
/// least one entry box for the player to stand in. The rest of the sections the
/// format allows are written empty, which is what a map with nothing in it looks
/// like.
/// </summary>
public static class MinimalOpsWriter
{
    public static byte[] Write(string mapName, string modelAsset, MapSettings settings)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            throw new ArgumentException("A map needs a name.", nameof(mapName));
        }
        ArgumentNullException.ThrowIfNull(settings);

        var text = new StringBuilder();
        text.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n\r\n");
        text.Append("<Ops version=\"1\">\r\n");
        text.Append("\t<MapSetting>\r\n");
        text.Append($"\t\t<DefaultCamera near=\"0.1\" far=\"1000\" rot=\"16, 0, 0\""
            + $" dist=\"{Number(settings.CameraDistance)}\""
            + $" fov=\"{Number(settings.CameraFieldOfView)}\" offset=\"0, 0.8, 0\" />\r\n");
        text.Append("\t\t<Shadow horizontal=\"40\" vertical=\"0\" upstair=\"3.5\" />\r\n");
        text.Append("\t\t<Reflection enable=\"0\" height=\"0\" />\r\n");
        text.Append("\t\t<UnderWater enable=\"0\" />\r\n");
        text.Append("\t\t<UnderWaterDrawChr enable=\"0\" />\r\n");
        text.Append("\t\t<Minimap height1=\"3\" height2=\"8\" renderScale=\"1\" />\r\n");
        text.Append("\t\t<FilterDepth enable=\"0\" value=\"2.5\" />\r\n");
        text.Append("\t\t<FilterAngle enable=\"0\" />\r\n");
        text.Append("\t\t<MapColor>\r\n");
        text.Append("\t\t\t<Type type=\"default\" />\r\n");
        text.Append($"\t\t\t<Fog near=\"{Number(settings.FogNear)}\" far=\"{Number(settings.FogFar)}\""
            + $" color=\"{Vector(settings.FogColour)}\" />\r\n");
        text.Append($"\t\t\t<DefaultLight ambient=\"{Vector(settings.Ambient)}, 1\""
            + $" position=\"8, 8, 0\" color=\"{Vector(settings.Ambient)}, 1\""
            + $" hsAmbientSky=\"{Vector(settings.Ambient)}, 1\""
            + $" hsAmbientGnd=\"{Vector(settings.Ambient)}, 1\""
            + " hsAmbientAxis=\"0, 10, 0\" />\r\n");
        text.Append("\t\t</MapColor>\r\n");
        text.Append("\t</MapSetting>\r\n");
        text.Append("\t<MapCameras>\r\n\t</MapCameras>\r\n");

        // The map's own model, at the origin and unturned: where its geometry sits
        // is the model's business, not the map's.
        text.Append("\t<MapObjects>\r\n");
        text.Append($"\t\t<AssetObject asset=\"{modelAsset}\" name=\"map\" flag=\"0x3\""
            + " clipGroup=\"0\" clipFarDistance=\"-1\" pos=\"0, 0, 0\" rot=\"0, 0, 0\""
            + " scl=\"1, 1, 1\" skyboxFactor=\"0\" materialDiffuse=\"1, 1, 1, 1\""
            + " materialEmission=\"0, 0, 0\" />\r\n");
        if (settings.Skybox.Length != 0)
        {
            // The sky is an object like any other, and stays one: changing it later
            // is editing this asset in the scene view, not a feature of its own.
            text.Append($"\t\t<AssetObject asset=\"{settings.Skybox}\" name=\"sky\" flag=\"0x1\""
                + " clipGroup=\"0\" clipFarDistance=\"-1\" pos=\"0, 0, 0\" rot=\"0, 0, 0\""
                + " scl=\"1, 1, 1\" skyboxFactor=\"0\" materialDiffuse=\"1, 1, 1, 1\""
                + " materialEmission=\"0, 0, 0\" />\r\n");
        }
        text.Append("\t</MapObjects>\r\n");

        // One box to stand in. Without it the map has no way in at all, so this is
        // the least a map can be rather than a convenience.
        text.Append("\t<Entrys>\r\n");
        text.Append($"\t\t<EntryBox name=\"default\" next=\"{mapName}\" entry=\"default\""
            + $" placeid=\"0\" flag=\"0x0\" pos=\"{Vector(settings.EntryPosition)},  0, 0, 0,"
            + $"  {Vector(settings.EntrySize)}\" distance=\"1\" cameraDir=\"-1\""
            + " entryType=\"0\" markPos=\"0, 0, 0\" />\r\n");
        text.Append("\t</Entrys>\r\n");

        foreach (var empty in new[]
                 { "LookPoints", "Occluders", "GroupBoxes", "Lights", "MapSounds", "MapEffects" })
        {
            text.Append($"\t<{empty}>\r\n\t</{empty}>\r\n");
        }
        text.Append("</Ops>\r\n");

        // The game's own files carry a byte-order mark, so this one does too.
        return new UTF8Encoding(true).GetBytes(text.ToString());
    }

    private static string Number(float value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Vector(Vector3 value)
        => $"{Number(value.X)}, {Number(value.Y)}, {Number(value.Z)}";
}
