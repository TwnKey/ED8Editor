namespace ED8Editor.Application;

/// <summary>
/// Guesses which bone of an imported rig is which bone of the game's, from
/// names alone.
///
/// This exists because the game's own names turn out to already be a common
/// convention: <c>Hips, Spine, LeftUpLeg, LeftLeg, LeftFoot, LeftArm,
/// LeftForeArm, LeftHand, LeftShoulder, Head</c> is the 3ds Max Biped chain,
/// which is also what Mixamo exports under a <c>mixamorig:</c> prefix. So the
/// common case — a humanoid rig from any of the usual pipelines — resolves
/// itself once the prefix and the side notation are normalised; what is left
/// over after that is the short list a person should actually look at.
/// </summary>
public static class Cs1RigNameMapper
{
    /// <summary>
    /// Namespaces and rig prefixes met in practice, stripped before anything
    /// else is compared. Order matters: longer, more specific prefixes first.
    /// </summary>
    private static readonly string[] Prefixes =
    {
        "mixamorig:", "mixamorig_", "mixamorig",
        "armature|", "rig|", "root|",
        "def-", "def_", "org-", "org_",
        "bip01_", "bip01 ", "bip001_", "bip001 ",
        "b_", "j_",
    };

    /// <summary>
    /// Canonical game name to every other spelling seen in the wild for it —
    /// both written in the form <see cref="Normalise"/> produces, which puts a
    /// side at the FRONT, so a sided entry reads <c>leftupperarm</c> and never
    /// <c>upperarml</c>. Entries naming a left side are mirrored to the right
    /// automatically; the table is written once.
    ///
    /// Deliberately modest: the aim is the common rigs, not an exhaustive
    /// catalogue, and a name this misses falls to the manual list rather than
    /// being mapped wrong. Genuinely ambiguous words are left out on purpose —
    /// several rigs call the upper arm "shoulder" while the game uses that for
    /// the clavicle, so nothing here claims to know which one an author meant.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> Aliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["hips"] = new[] { "pelvis", "root", "cog" },
            ["spine"] = new[] { "spine1", "chest", "torso" },
            ["head"] = new[] { "headtop", "headtopend" },
            ["leftarm"] = new[] { "leftupperarm" },
            ["leftforearm"] = new[] { "leftlowerarm", "leftelbow" },
            ["lefthand"] = new[] { "leftwrist" },
            ["leftshoulder"] = new[] { "leftclavicle", "leftcollar" },
            ["leftupleg"] = new[] { "leftthigh", "leftupperleg" },
            ["leftleg"] = new[] { "leftshin", "leftcalf", "leftlowerleg", "leftknee" },
            ["leftfoot"] = new[] { "leftankle" },
            ["lefttoe"] = new[] { "lefttoebase", "lefttoe0" },
        };

    /// <summary>The pairing chosen for one source bone.</summary>
    public sealed record Mapping(string SourceName, string? TargetName, bool ByAlias);

    /// <summary>
    /// One pairing per source name, best guess first. <see cref="Mapping.TargetName"/>
    /// is null where nothing plausible was found — left for a person to fill in
    /// rather than guessed at, since a wrong guess here drives a joint with the
    /// wrong rotation, and that is worse than leaving it still.
    /// </summary>
    public static IReadOnlyList<Mapping> AutoMap(
        IReadOnlyList<string> sourceNames, IReadOnlyList<string> targetNames)
    {
        ArgumentNullException.ThrowIfNull(sourceNames);
        ArgumentNullException.ThrowIfNull(targetNames);

        var targetByNormalised = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in targetNames)
        {
            targetByNormalised.TryAdd(Normalise(target), target);
        }
        var aliasToTarget = BuildAliasIndex(targetByNormalised);

        var taken = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<Mapping>();
        foreach (var source in sourceNames)
        {
            var key = Normalise(source);
            if (targetByNormalised.TryGetValue(key, out var exact) && taken.Add(exact))
            {
                found.Add(new Mapping(source, exact, ByAlias: false));
                continue;
            }
            if (aliasToTarget.TryGetValue(key, out var byAlias) && taken.Add(byAlias))
            {
                found.Add(new Mapping(source, byAlias, ByAlias: true));
                continue;
            }
            found.Add(new Mapping(source, null, ByAlias: false));
        }
        return found;
    }

    private static Dictionary<string, string> BuildAliasIndex(
        IReadOnlyDictionary<string, string> targetByNormalised)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (canonical, spellings) in Aliases)
        {
            Add(canonical, spellings);
            if (canonical.StartsWith("left", StringComparison.Ordinal))
            {
                Add(Mirror(canonical), spellings.Select(Mirror).ToArray());
            }
        }
        return index;

        void Add(string canonical, IReadOnlyList<string> spellings)
        {
            if (!targetByNormalised.TryGetValue(canonical, out var target)) return;
            foreach (var spelling in spellings)
            {
                // Never let an alias shadow a game bone that spells itself the
                // same way: the exact match is always the better answer.
                if (targetByNormalised.ContainsKey(spelling)) continue;
                index.TryAdd(spelling, target);
            }
        }
    }

    /// <summary>
    /// A normalised left-side name written for the right — the alias table is
    /// written once and answers for both sides.
    /// </summary>
    private static string Mirror(string normalisedLeft) =>
        normalisedLeft.StartsWith("left", StringComparison.Ordinal)
            ? "right" + normalisedLeft["left".Length..]
            : normalisedLeft;

    /// <summary>
    /// A name reduced to what actually identifies the bone: no rig namespace,
    /// no separators, no case, and a side folded to a single leading or
    /// trailing letter however the source spelled it — <c>Left</c>, <c>L</c>,
    /// <c>.L</c>, a leading <c>L_</c> — so <c>mixamorig:LeftForeArm</c> and
    /// <c>lower_arm_L</c> both reduce to a form the alias table already knows.
    /// </summary>
    private static string Normalise(string name)
    {
        var text = name.Trim();
        foreach (var prefix in Prefixes)
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            text = text[prefix.Length..];
            break;
        }

        var side = ' ';
        if (text.Length > 2 && text[^2] is '.' or '_' or '-'
            && text[^1] is 'l' or 'L' or 'r' or 'R')
        {
            side = char.ToLowerInvariant(text[^1]);
            text = text[..^2];
        }
        else if (text.Length > 2 && text[1] is '_' or '-'
                 && text[0] is 'l' or 'L' or 'r' or 'R')
        {
            side = char.ToLowerInvariant(text[0]);
            text = text[2..];
        }

        var stripped = new string(text.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant).ToArray());

        // A side spelled as a whole word inside the stripped name, rather than
        // as the short suffix or prefix the two checks above look for.
        if (side == ' ')
        {
            if (stripped.StartsWith("left", StringComparison.Ordinal)) { side = 'l'; stripped = stripped[4..]; }
            else if (stripped.StartsWith("right", StringComparison.Ordinal)) { side = 'r'; stripped = stripped[5..]; }
            else if (stripped.EndsWith("left", StringComparison.Ordinal)) { side = 'l'; stripped = stripped[..^4]; }
            else if (stripped.EndsWith("right", StringComparison.Ordinal)) { side = 'r'; stripped = stripped[..^5]; }
        }

        return side == 'l' ? "left" + stripped
            : side == 'r' ? "right" + stripped
            : stripped;
    }
}
