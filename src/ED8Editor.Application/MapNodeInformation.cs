using System.Text;
using System.Text.RegularExpressions;

namespace ED8Editor.Application;

/// <summary>
/// The node information file that sits beside a map, giving each of its collision
/// nodes a parameter.
///
/// The parameter says what the surface is made of. The executable turns it into one
/// of four effect files under data/effects/system — foot01, foot09, foot10, foot12 —
/// which it loads by "system\%s.eff"; those four names are compiled into ed8.exe, in
/// a table of five slots two of which name the same file, so the set cannot be
/// extended without touching the executable.
///
/// Which parameter picks which effect is NOT established here. Writing 8 on a node,
/// the value r0510 states for its own, produced no snow in game — so the value is
/// not the index into that table of five, and anything this type says about the
/// meaning of a number would be invention. What it does instead is carry the number
/// faithfully and, through <see cref="MapNodeInformationCatalog"/>, say which shipped
/// maps use it, which is a fact rather than a guess.
/// </summary>
public sealed class MapNodeInformation
{
    /// <summary>Sixteen of each are declared whether the map has them or not.</summary>
    private const int Declared = 16;

    private static readonly Regex Entry = new(
        @"name=""(?<node>C[ASK][0-9]+)""\s+param0=""(?<value>[0-9]+)""",
        RegexOptions.Compiled);

    private readonly Dictionary<string, int> parameters = new(StringComparer.Ordinal);

    /// <summary>Every node the file names, with its parameter.</summary>
    public IReadOnlyDictionary<string, int> Parameters => parameters;

    /// <summary>The collision nodes whose parameter is a chosen value.</summary>
    public IEnumerable<string> ChosenNodes => parameters.Keys
        .Where(value => value.StartsWith("CA", StringComparison.Ordinal))
        .OrderBy(value => value, StringComparer.Ordinal);

    public int this[string node]
    {
        get => parameters.TryGetValue(node, out var found) ? found : 0;
        set => parameters[node] = value;
    }

    public bool Names(string node) => parameters.ContainsKey(node);

    public void Remove(string node) => parameters.Remove(node);

    public static MapNodeInformation Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var found = new MapNodeInformation();
        if (!File.Exists(path)) return found;
        foreach (Match one in Entry.Matches(File.ReadAllText(path)))
        {
            found.parameters[one.Groups["node"].Value] =
                int.Parse(one.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return found;
    }

    /// <summary>
    /// The file as the game writes it: the two identity tables first — CS## takes its
    /// own index, CK## takes zero — then the nodes whose parameter was chosen.
    /// </summary>
    public string Write()
    {
        const string Newline = "\r\n";
        var text = new StringBuilder();
        text.Append('﻿').Append("<!-- daeノード情報ファイル -->").Append(Newline);
        text.Append("<node_infomation>").Append(Newline);
        for (var at = 0; at < Declared; at++)
        {
            var node = $"CS{at:00}";
            var value = parameters.TryGetValue(node, out var stated) ? stated : at;
            text.Append($"  <node_param name=\"{node}\" param0=\"{value}\" />").Append(Newline);
        }
        text.Append(Newline);
        for (var at = 0; at < Declared; at++)
        {
            var node = $"CK{at:00}";
            var value = parameters.TryGetValue(node, out var stated) ? stated : 0;
            text.Append($"  <node_param name=\"{node}\" param0=\"{value}\" />").Append(Newline);
        }
        text.Append(Newline);
        foreach (var node in ChosenNodes)
        {
            text.Append($"  <node_param name=\"{node}\" param0=\"{parameters[node]}\" />")
                .Append(Newline);
        }
        text.Append(Newline).Append("</node_infomation>").Append(Newline);
        return text.ToString();
    }

    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllText(path, Write(), new UTF8Encoding(false));
    }

    /// <summary>Where a map's file sits under the game directory.</summary>
    public static string PathOf(string gameDirectory, string mapName)
        => Path.Combine(gameDirectory, "data", "map", mapName, mapName + ".inf");
}

/// <summary>
/// What the shipped files state, so a value can be chosen by what the game already
/// does with it rather than by a meaning nobody has established.
/// </summary>
public sealed record MapNodeParameterUse(int Value, IReadOnlyList<string> Maps);

public static class MapNodeInformationCatalog
{
    /// <summary>
    /// Every parameter the shipped maps give a CA node, and which maps give it.
    ///
    /// CS and CK are left out on purpose: measured over all 1202 shipped files, CS##
    /// always takes its own index and CK## always takes zero, so neither carries a
    /// choice worth offering.
    /// </summary>
    public static IReadOnlyList<MapNodeParameterUse> ChosenValues(string gameDirectory)
    {
        ArgumentNullException.ThrowIfNull(gameDirectory);
        var maps = Path.Combine(gameDirectory, "data", "map");
        var found = new SortedDictionary<int, List<string>>();
        if (!Directory.Exists(maps)) return Array.Empty<MapNodeParameterUse>();
        foreach (var file in Directory.EnumerateFiles(maps, "*.inf", SearchOption.AllDirectories))
        {
            var read = MapNodeInformation.Read(file);
            var name = Path.GetFileNameWithoutExtension(file);
            foreach (var node in read.ChosenNodes)
            {
                if (!found.TryGetValue(read[node], out var users))
                {
                    users = new List<string>();
                    found[read[node]] = users;
                }
                if (!users.Contains(name, StringComparer.OrdinalIgnoreCase)) users.Add(name);
            }
        }
        return found
            .Select(pair => new MapNodeParameterUse(pair.Key, pair.Value))
            .ToArray();
    }
}
