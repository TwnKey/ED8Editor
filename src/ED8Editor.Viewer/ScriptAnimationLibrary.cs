using System.Globalization;
using System.Text;
using ED8Editor.Decompiler;
using ED8Editor.Tables;

namespace ED8Editor.Viewer;

internal sealed record ScriptCharacterDefinition(
    int CharacterId,
    string DisplayName,
    string ModelAssetId,
    string FieldAnimationAssetId,
    string AnimationScript,
    string FacialAssetId);

/// <summary>
/// Resolves an entity's external ANI script from its OP19 Script File or the
/// verified NameTableData animation-script field in t_name.tbl.
/// </summary>
internal sealed class ScriptAnimationLibrary
{
    private const string NameTableCategory = "NameTableData";
    private const string AnimationScriptField = "unknown_string_3";

    private readonly string instructionDefinitionsPath;
    private readonly string primaryAniDirectory;
    private readonly string fallbackAniDirectory;
    private readonly Dictionary<int, string> scriptsByCharacter = new();
    private readonly Dictionary<int, ScriptCharacterDefinition> characters = new();
    private readonly Dictionary<string, string> scriptsByModelAsset =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> facialAssetsByModel =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> facialPatterns =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> ambiguousModelAssets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> localizedNamesByModelAsset =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> ambiguousLocalizedModelAssets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DecompiledScript?> scripts =
        new(StringComparer.OrdinalIgnoreCase);
    public string NameTableDiagnostics { get; private set; } = "t_name.tbl was not inspected";

    public ScriptAnimationLibrary(
        string gameDataPath,
        string? scenarioPath,
        string? instructionDefinitionsPath)
    {
        if (string.IsNullOrWhiteSpace(gameDataPath))
            throw new ArgumentException("Game data path is required.", nameof(gameDataPath));
        this.instructionDefinitionsPath = Path.GetFullPath(
            instructionDefinitionsPath ?? ScriptDecompiler.DefaultInstructionsPath);
        var preferUs = PathContainsSegment(scenarioPath, "dat_us");
        var preferredLocale = preferUs ? "dat_us" : "dat";
        var fallbackLocale = preferUs ? "dat" : "dat_us";
        primaryAniDirectory = Path.Combine(gameDataPath, "scripts", "ani", preferredLocale);
        fallbackAniDirectory = Path.Combine(gameDataPath, "scripts", "ani", fallbackLocale);
        LoadNameTable(gameDataPath, preferredLocale, fallbackLocale);
        LoadLocalizedNames(gameDataPath);
        LoadFacialPatterns();
    }

    public IReadOnlyList<string> GetFunctionNames(ScriptEntityState entity)
        => entity.IsPlaceholder
            ? Array.Empty<string>()
            : GetFunctionNames(
                entity.EntityId, entity.ScriptFile, entity.AssetId);

    public IReadOnlyList<string> GetFunctionNames(
        int entityId,
        string? scriptFile = null,
        string? modelAssetId = null)
        => TryGetScript(entityId, scriptFile, modelAssetId, out var script)
            ? script.Functions.Where(value => value.IsCode)
                .Select(value => value.Name)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

    public bool TryGetCharacter(
        int characterId,
        out ScriptCharacterDefinition definition)
        => characters.TryGetValue(characterId, out definition!);

    public string ResolveFacialAsset(int characterId, string? modelAssetId)
        => characters.TryGetValue(characterId, out var character)
            && !string.IsNullOrWhiteSpace(character.FacialAssetId)
                ? character.FacialAssetId
                : string.IsNullOrWhiteSpace(modelAssetId)
                    ? string.Empty
                    : facialAssetsByModel.GetValueOrDefault(modelAssetId) ?? string.Empty;

    public string ExpandFacialPattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return pattern;
        var result = new StringBuilder(pattern.Length);
        for (var index = 0; index < pattern.Length;)
        {
            if (pattern[index] != '[')
            {
                result.Append(pattern[index++]);
                continue;
            }
            var end = pattern.IndexOf(']', index + 1);
            if (end < 0)
            {
                result.Append(pattern.AsSpan(index));
                break;
            }
            var name = pattern[(index + 1)..end];
            if (facialPatterns.TryGetValue(name, out var expansion))
                result.Append(expansion);
            else
                result.Append(pattern.AsSpan(index, end - index + 1));
            index = end + 1;
        }
        return result.ToString();
    }

    public string ResolveDisplayName(string? modelAssetId, string fallback)
        => string.IsNullOrWhiteSpace(modelAssetId)
            || ambiguousLocalizedModelAssets.Contains(modelAssetId)
            || !localizedNamesByModelAsset.TryGetValue(modelAssetId, out var localized)
                ? fallback
                : localized;

    public bool TryGetFunction(
        ScriptEntityState entity,
        string functionName,
        out DecompiledScript script,
        out DecompiledFunction function)
    {
        script = null!;
        function = null!;
        if (entity.IsPlaceholder
            || string.IsNullOrWhiteSpace(functionName)
            || !TryGetScript(
                entity.EntityId, entity.ScriptFile, entity.AssetId, out script))
            return false;
        function = script.Functions.FirstOrDefault(value =>
            value.IsCode && value.Name.Equals(functionName, StringComparison.Ordinal))!;
        return function is not null;
    }

    private bool TryGetScript(
        int entityId,
        string? explicitScriptFile,
        string? modelAssetId,
        out DecompiledScript script)
    {
        script = null!;
        // OP19's Script File is the actor's attached scenario script, not
        // necessarily its ANI program. The exact model-to-ANI association in
        // t_name.tbl therefore takes precedence whenever it is unambiguous.
        var scriptName = ResolveModelAnimationScript(modelAssetId)
            ?? scriptsByCharacter.GetValueOrDefault(entityId)
            ?? (!string.IsNullOrWhiteSpace(explicitScriptFile)
                ? explicitScriptFile
                : null);
        if (string.IsNullOrWhiteSpace(scriptName)) return false;
        scriptName = Path.GetFileNameWithoutExtension(scriptName);
        if (scripts.TryGetValue(scriptName, out var cached))
        {
            script = cached!;
            return cached is not null;
        }

        var path = ResolveAniPath(scriptName);
        if (path is null)
        {
            scripts[scriptName] = null;
            return false;
        }
        try
        {
            script = ScriptDecompiler.Decompile(path, instructionDefinitionsPath);
            scripts[scriptName] = script;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Could not decompile ANI script '{path}': {exception}");
            scripts[scriptName] = null;
            return false;
        }
    }

    private string? ResolveModelAnimationScript(string? modelAssetId)
        => string.IsNullOrWhiteSpace(modelAssetId)
            || ambiguousModelAssets.Contains(modelAssetId)
                ? null
                : scriptsByModelAsset.GetValueOrDefault(modelAssetId);

    private string? ResolveAniPath(string scriptName)
    {
        var fileName = scriptName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
            ? scriptName
            : scriptName + ".dat";
        var primary = Path.Combine(primaryAniDirectory, fileName);
        if (File.Exists(primary)) return primary;
        var fallback = Path.Combine(fallbackAniDirectory, fileName);
        return File.Exists(fallback) ? fallback : null;
    }

    private void LoadNameTable(string gameDataPath, string preferredLocale, string fallbackLocale)
    {
        var path = new[] { preferredLocale, fallbackLocale }
            .Select(locale => Path.Combine(gameDataPath, "text", locale, "t_name.tbl"))
            .FirstOrDefault(File.Exists);
        if (path is null)
        {
            NameTableDiagnostics = "t_name.tbl was not found";
            return;
        }
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var locale = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
            var tableEncoding = locale.Equals("dat", StringComparison.OrdinalIgnoreCase)
                ? Encoding.GetEncoding(
                    932,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback)
                : new UTF8Encoding(false, true);
            var codec = new Cs1TableRecordCodec(textEncoding: tableEncoding);
            var document = Cs1TableDocument.Read(path);
            var nameEntries = document.Entries.Where(value =>
                value.Category.Equals(NameTableCategory, StringComparison.Ordinal)).ToArray();
            foreach (var entry in nameEntries)
            {
                var values = codec.Decode(entry);
                if (values is null) continue;
                var fields = values.ToDictionary(
                    value => value.Field.Name, value => value.Value, StringComparer.Ordinal);
                if (!fields.TryGetValue("character", out var characterText)
                    || !int.TryParse(
                        characterText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var character))
                {
                    continue;
                }
                fields.TryGetValue("name", out var displayName);
                fields.TryGetValue("unknown_string_1", out var modelAsset);
                fields.TryGetValue("unknown_string_2", out var fieldAnimationAsset);
                fields.TryGetValue(AnimationScriptField, out var scriptName);
                fields.TryGetValue("unknown_string_4", out var facialAsset);
                var definition = new ScriptCharacterDefinition(
                    character,
                    displayName ?? string.Empty,
                    NormalizeNullableName(modelAsset),
                    NormalizeNullableName(fieldAnimationAsset),
                    NormalizeNullableName(scriptName),
                    NormalizeNullableName(facialAsset));
                characters.TryAdd(character, definition);
                AddFacialAsset(definition.ModelAssetId, definition.FacialAssetId);
                AddFacialAsset(definition.FieldAnimationAssetId, definition.FacialAssetId);
                if (!string.IsNullOrWhiteSpace(definition.AnimationScript))
                {
                    scriptsByCharacter.TryAdd(character, definition.AnimationScript);
                    AddModelAnimationScript(
                        definition.ModelAssetId, definition.AnimationScript);
                    AddModelAnimationScript(
                        definition.FieldAnimationAssetId, definition.AnimationScript);
                }
            }
            NameTableDiagnostics =
                $"{path}: {nameEntries.Length} NameTableData entries, {characters.Count} decoded";
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or ArgumentException or FormatException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Could not read animation mappings from '{path}': {exception}");
            NameTableDiagnostics = $"{path}: {exception.GetType().Name}: {exception.Message}";
        }
    }

    private void AddModelAnimationScript(string modelAssetId, string scriptName)
    {
        if (string.IsNullOrWhiteSpace(modelAssetId)
            || ambiguousModelAssets.Contains(modelAssetId))
        {
            return;
        }
        if (!scriptsByModelAsset.TryGetValue(modelAssetId, out var existing))
        {
            scriptsByModelAsset.Add(modelAssetId, scriptName);
            return;
        }
        if (existing.Equals(scriptName, StringComparison.OrdinalIgnoreCase)) return;
        scriptsByModelAsset.Remove(modelAssetId);
        ambiguousModelAssets.Add(modelAssetId);
    }

    private void AddFacialAsset(string modelAssetId, string facialAssetId)
    {
        if (string.IsNullOrWhiteSpace(modelAssetId)
            || string.IsNullOrWhiteSpace(facialAssetId))
        {
            return;
        }
        facialAssetsByModel.TryAdd(modelAssetId, facialAssetId);
    }

    private void LoadFacialPatterns()
    {
        var path = new[]
            {
                Path.Combine(primaryAniDirectory, "face.dat"),
                Path.Combine(fallbackAniDirectory, "face.dat"),
            }
            .FirstOrDefault(File.Exists);
        if (path is null) return;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = Encoding.GetEncoding(932);
            var script = ScriptDecompiler.Decompile(path, instructionDefinitionsPath);
            foreach (var function in script.Functions.Where(value =>
                         value.Name.StartsWith("FC_", StringComparison.OrdinalIgnoreCase)
                         && value.RawData is { Length: > 0 }))
            {
                var value = encoding.GetString(function.RawData!).TrimEnd('\0');
                facialPatterns.TryAdd(function.Name[3..], value);
            }
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Could not read facial patterns from '{path}': {exception}");
        }
    }

    private void LoadLocalizedNames(string gameDataPath)
    {
        var path = Path.Combine(gameDataPath, "text", "dat_us", "t_name.tbl");
        if (!File.Exists(path)) return;
        try
        {
            var codec = new Cs1TableRecordCodec(textEncoding: new UTF8Encoding(false, true));
            var document = Cs1TableDocument.Read(path);
            foreach (var entry in document.Entries.Where(value =>
                         value.Category.Equals(NameTableCategory, StringComparison.Ordinal)))
            {
                var values = codec.Decode(entry);
                if (values is null) continue;
                var fields = values.ToDictionary(
                    value => value.Field.Name, value => value.Value, StringComparer.Ordinal);
                if (!fields.TryGetValue("name", out var displayName)
                    || string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }
                fields.TryGetValue("unknown_string_1", out var modelAsset);
                fields.TryGetValue("unknown_string_2", out var fieldAnimationAsset);
                AddLocalizedModelName(NormalizeNullableName(modelAsset), displayName);
                AddLocalizedModelName(NormalizeNullableName(fieldAnimationAsset), displayName);
            }
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or ArgumentException or FormatException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Could not read localized character names from '{path}': {exception}");
        }
    }

    private void AddLocalizedModelName(string modelAssetId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(modelAssetId)
            || ambiguousLocalizedModelAssets.Contains(modelAssetId))
        {
            return;
        }
        if (!localizedNamesByModelAsset.TryGetValue(modelAssetId, out var existing))
        {
            localizedNamesByModelAsset.Add(modelAssetId, displayName);
            return;
        }
        if (existing.Equals(displayName, StringComparison.Ordinal)) return;
        localizedNamesByModelAsset.Remove(modelAssetId);
        ambiguousLocalizedModelAssets.Add(modelAssetId);
    }

    private static string NormalizeNullableName(string? value)
        => string.IsNullOrWhiteSpace(value)
            || value.Equals("null", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : value;

    private static bool PathContainsSegment(string? path, string segment)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Path.GetFullPath(path).Split(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(value => value.Equals(segment, StringComparison.OrdinalIgnoreCase));
    }
}
