using System.Globalization;
using ED8Editor.Application;
using ED8Editor.Decompiler;

namespace ED8Editor.Viewer;

internal sealed class TableEditorForm : Form
{
    private readonly ScriptEditorDocument document;
    private readonly int functionIndex;
    private readonly IReadOnlyList<MonsterTableChoice> monsterChoices;
    private readonly List<BattleMapAssetEntry> battleMapAssets;
    private readonly Func<IWin32Window, BattleMapAssetEntry?>? createBattleMapAsset;
    private readonly IReadOnlyList<BattleScenarioEntry> battleScenarios;
    private readonly Action<int>? openBattleScript;
    private readonly int? focusedEncounterIndex;
    private readonly Label header = new() { Dock = DockStyle.Top, Height = 32, Padding = new Padding(8, 0, 0, 0) };
    private readonly DataGridView fields = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    private readonly TabControl editorTabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage encounterTab = new("Encounter");
    private readonly TabPage nativeFieldsTab = new("Native fields");
    private readonly DataGridView encounterSlots = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };
    private readonly Label encounterSummary = new()
    {
        Dock = DockStyle.Top,
        Height = 58,
        Padding = new Padding(8, 4, 8, 4),
        AutoEllipsis = true,
    };
    private readonly ComboBox battleScenarioList = new()
    {
        Width = 112,
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems,
        DisplayMember = nameof(BattleScenarioEntry.Label),
    };
    private readonly NumericUpDown battleFlags = HeaderNumber();
    private readonly NumericUpDown battleBgm1 = HeaderNumber();
    private readonly NumericUpDown battleBgm2 = HeaderNumber();
    private readonly Button applyButton = new() { Text = "Apply", AutoSize = true };
    private readonly Button closeButton = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Button addEncounterButton = new() { Text = "Add encounter", AutoSize = true, Visible = false };
    private readonly Button duplicateEncounterButton = new() { Text = "Duplicate encounter", AutoSize = true, Visible = false };
    private readonly Button deleteEncounterButton = new() { Text = "Delete encounter", AutoSize = true, Visible = false };
    private readonly Button createBattleMapButton = new()
    {
        Text = "New battle map .inf…",
        AutoSize = true,
        Visible = false,
    };
    private readonly Button openBattleScriptButton = new()
    {
        Text = "Open battle script…",
        AutoSize = true,
        Visible = false,
    };

    public TableEditorForm(
        ScriptEditorDocument document,
        int functionIndex,
        IReadOnlyList<MonsterTableChoice>? monsterChoices = null,
        int? selectedEncounterIndex = null,
        IReadOnlyList<BattleMapAssetEntry>? battleMapAssets = null,
        Func<IWin32Window, BattleMapAssetEntry?>? createBattleMapAsset = null,
        IReadOnlyList<BattleScenarioEntry>? battleScenarios = null,
        Action<int>? openBattleScript = null)
    {
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        this.functionIndex = functionIndex;
        this.monsterChoices = monsterChoices ?? Array.Empty<MonsterTableChoice>();
        this.battleMapAssets = battleMapAssets?.ToList() ?? new List<BattleMapAssetEntry>();
        this.createBattleMapAsset = createBattleMapAsset;
        this.battleScenarios = battleScenarios ?? Array.Empty<BattleScenarioEntry>();
        this.openBattleScript = openBattleScript;
        focusedEncounterIndex = selectedEncounterIndex;
        Text = "Encounter editor";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(680, 420);
        ClientSize = new Size(860, 620);
        ShowInTaskbar = false;

        fields.Columns.Add(new DataGridViewTextBoxColumn { Name = "index", HeaderText = "#", FillWeight = 12, ReadOnly = true });
        fields.Columns.Add(new DataGridViewTextBoxColumn { Name = "field", HeaderText = "Field", FillWeight = 35, ReadOnly = true });
        fields.Columns.Add(new DataGridViewTextBoxColumn { Name = "type", HeaderText = "Type", FillWeight = 18, ReadOnly = true });
        fields.Columns.Add(new DataGridViewTextBoxColumn { Name = "value", HeaderText = "Value", FillWeight = 80 });
        encounterSlots.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "slot",
            HeaderText = "Slot",
            FillWeight = 16,
            ReadOnly = true,
        });
        encounterSlots.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "monster",
            HeaderText = "Enemy",
            FillWeight = 72,
        });
        encounterSlots.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "chance",
            HeaderText = "Spawn chance %",
            FillWeight = 28,
        });
        var encounterHeader = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(5, 3, 0, 0),
            WrapContents = false,
        };
        AddHeaderEditor(encounterHeader, "Battle script", battleScenarioList);
        AddHeaderEditor(encounterHeader, "Flags", battleFlags);
        AddHeaderEditor(encounterHeader, "BGM 1", battleBgm1);
        AddHeaderEditor(encounterHeader, "BGM 2", battleBgm2);
        encounterTab.Controls.Add(encounterSlots);
        encounterTab.Controls.Add(encounterSummary);
        encounterTab.Controls.Add(encounterHeader);
        nativeFieldsTab.Controls.Add(fields);
        editorTabs.TabPages.Add(encounterTab);
        editorTabs.TabPages.Add(nativeFieldsTab);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(6),
        };
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(applyButton);
        var structureButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            Padding = new Padding(6, 3, 0, 0),
        };
        structureButtons.Controls.Add(addEncounterButton);
        structureButtons.Controls.Add(duplicateEncounterButton);
        structureButtons.Controls.Add(deleteEncounterButton);
        structureButtons.Controls.Add(createBattleMapButton);
        structureButtons.Controls.Add(openBattleScriptButton);
        Controls.Add(editorTabs);
        Controls.Add(buttons);
        Controls.Add(structureButtons);
        Controls.Add(header);
        CancelButton = closeButton;
        applyButton.Click += (_, _) => ApplyChanges();
        addEncounterButton.Click += (_, _) => RunStructureEdit(AddEncounter);
        duplicateEncounterButton.Click += (_, _) => RunStructureEdit(DuplicateEncounter);
        deleteEncounterButton.Click += (_, _) => RunStructureEdit(DeleteEncounter);
        createBattleMapButton.Click += (_, _) => CreateBattleMapAsset();
        openBattleScriptButton.Click += (_, _) => OpenBattleScript();
        fields.SelectionChanged += (_, _) => UpdateEncounterButtons();
        fields.DataError += (_, eventArgs) => eventArgs.ThrowException = false;
        encounterSlots.DataError += (_, eventArgs) => eventArgs.ThrowException = false;
        LoadTable();
        if (selectedEncounterIndex is { } encounterIndex)
            SelectEncounter(encounterIndex);
    }

    public event EventHandler? TableChanged;

    private void LoadTable()
    {
        var function = document.Snapshot.Functions.ElementAtOrDefault(functionIndex);
        if (function?.Table is not { } table)
            throw new InvalidOperationException("The selected function is not a parsed table.");
        Text = table.Kind == "CreateMonsters"
            ? $"Encounter editor — {function.Name}"
            : $"Table editor — {function.Name}";
        header.Text = $"{function.Name} — "
            + (table.Kind == "CreateMonsters" ? "Encounters" : table.Kind)
            + (table.IsStale ? " (stale/malformed)" : string.Empty);
        if (focusedEncounterIndex is { } focused
            && CreateMonstersTableReader.TryRead(table, out var focusedTable)
            && focusedTable?.Encounters.ElementAtOrDefault(focused) is { } focusedEncounter)
        {
            header.Text = $"{function.Name} — Encounter {focusedEncounter.Id} only";
        }
        applyButton.Enabled = !table.IsStale;
        var isCreateMonsters = !table.IsStale && table.Kind == "CreateMonsters";
        addEncounterButton.Visible = isCreateMonsters && focusedEncounterIndex is null;
        duplicateEncounterButton.Visible = isCreateMonsters && focusedEncounterIndex is null;
        deleteEncounterButton.Visible = isCreateMonsters;
        createBattleMapButton.Visible = isCreateMonsters && createBattleMapAsset is not null;
        openBattleScriptButton.Visible = isCreateMonsters && openBattleScript is not null;
        encounterTab.Parent = isCreateMonsters ? editorTabs : null;
        if (isCreateMonsters && !editorTabs.TabPages.Contains(encounterTab))
            editorTabs.TabPages.Insert(0, encounterTab);
        fields.Rows.Clear();
        var visibleFieldIndices = FocusedFieldIndices(table);
        foreach (var field in table.Fields.Where(field =>
                     (table.Kind != "CreateMonsters" || field.Type is not ("fill" or "bytes"))
                     && (visibleFieldIndices is null || visibleFieldIndices.Contains(field.Index))))
        {
            var rowIndex = fields.Rows.Add(
                field.Index,
                DescribeField(table, field),
                field.Type,
                FormatValue(field));
            var row = fields.Rows[rowIndex];
            row.Tag = field;
            if (table.IsStale)
            {
                row.Cells["value"].ReadOnly = true;
                row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
            }
            else if (isCreateMonsters && field.Index == table.Fields[0].Index
                     && field.Type == "string")
            {
                ConfigureBattleMapCell(row, field.Text ?? string.Empty);
            }
            else if (TryGetMonsterSlot(table, field, out _)
                     && field.Type == "string")
            {
                var choices = monsterChoices;
                var assetId = field.Text ?? string.Empty;
                if (!choices.Any(value =>
                        value.AssetId.Equals(assetId, StringComparison.OrdinalIgnoreCase)))
                {
                    choices = choices.Append(new MonsterTableChoice(
                        assetId,
                        string.IsNullOrEmpty(assetId) ? "(empty slot)" : $"Unknown monster ({assetId})",
                        string.Empty)).ToArray();
                }
                row.Cells["value"] = new DataGridViewComboBoxCell
                {
                    DataSource = choices,
                    DisplayMember = nameof(MonsterTableChoice.Label),
                    ValueMember = nameof(MonsterTableChoice.AssetId),
                    Value = assetId,
                    DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
                    FlatStyle = FlatStyle.Flat,
                };
            }
        }
        PopulateEncounterView(table);
        UpdateEncounterButtons();
    }

    private void PopulateEncounterView(DecompiledTable table)
    {
        encounterSlots.Rows.Clear();
        if (!CreateMonstersTableReader.TryRead(table, out var parsed)
            || parsed is null)
        {
            encounterSummary.Text = string.Empty;
            return;
        }
        var descriptor = parsed.HeaderFields.Count > 2
            ? unchecked((uint)parsed.HeaderFields[2].IntValue)
            : 0;
        var scenarioId = descriptor & 0xffff;
        var flags = descriptor >> 16;
        var bgm1 = parsed.HeaderFields.Count > 3 ? parsed.HeaderFields[3].IntValue : 0;
        var bgm2 = parsed.HeaderFields.Count > 4 ? parsed.HeaderFields[4].IntValue : 0;
        encounterSummary.Text =
            $"Battle map: {parsed.MapAsset}    Scenario: btl{scenarioId:0000}    "
            + $"Flags: 0x{flags:X4}    BGM: {bgm1} / {bgm2}";
        PopulateBattleScenarioList((int)scenarioId);
        battleFlags.Value = flags;
        battleBgm1.Value = Math.Clamp(bgm1, 0, ushort.MaxValue);
        battleBgm2.Value = Math.Clamp(bgm2, 0, ushort.MaxValue);

        var selected = focusedEncounterIndex is { } focused
            ? parsed.Encounters.ElementAtOrDefault(focused)
            : SelectedEncounter(parsed) ?? parsed.Encounters.FirstOrDefault();
        if (selected is null) return;
        encounterSummary.Text += $"    Encounter ID: {selected.Id}";
        for (var slot = 0; slot < 8; slot++)
        {
            var asset = selected.MonsterAssets[slot];
            var choices = monsterChoices.ToList();
            if (choices.All(value =>
                    !value.AssetId.Equals(asset, StringComparison.OrdinalIgnoreCase)))
            {
                choices.Insert(0, new MonsterTableChoice(
                    asset,
                    string.IsNullOrWhiteSpace(asset) ? "(empty slot)" : $"Unknown ({asset})",
                    string.Empty));
            }
            var rowIndex = encounterSlots.Rows.Add(
                slot,
                asset,
                selected.Weights[slot]);
            var row = encounterSlots.Rows[rowIndex];
            row.Tag = selected;
            row.Cells["monster"] = new DataGridViewComboBoxCell
            {
                DataSource = choices,
                DisplayMember = nameof(MonsterTableChoice.Label),
                ValueMember = nameof(MonsterTableChoice.AssetId),
                Value = asset,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
                FlatStyle = FlatStyle.Flat,
            };
        }
    }

    private HashSet<int>? FocusedFieldIndices(DecompiledTable table)
    {
        if (focusedEncounterIndex is not { } encounterIndex
            || !CreateMonstersTableReader.TryRead(table, out var parsed)
            || parsed is null)
        {
            return null;
        }
        var encounter = parsed.Encounters.ElementAtOrDefault(encounterIndex);
        if (encounter is null) return null;
        return encounter.SourceFields
            .Select(value => value.Index)
            .Append(parsed.HeaderFields[0].Index)
            .ToHashSet();
    }

    private void ConfigureBattleMapCell(DataGridViewRow row, string assetId)
    {
        var choices = battleMapAssets.ToList();
        if (!choices.Any(value =>
                value.AssetId.Equals(assetId, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new BattleMapAssetEntry(
                assetId,
                string.Empty,
                string.Empty,
                false,
                Array.Empty<string>()));
        }
        choices.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.AssetId, right.AssetId));
        row.Cells["value"] = new DataGridViewComboBoxCell
        {
            DataSource = choices,
            DisplayMember = nameof(BattleMapAssetEntry.Label),
            ValueMember = nameof(BattleMapAssetEntry.AssetId),
            Value = assetId,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle = FlatStyle.Flat,
        };
    }

    private void CreateBattleMapAsset()
    {
        if (createBattleMapAsset?.Invoke(this) is not { } created) return;
        if (!battleMapAssets.Any(value =>
                value.AssetId.Equals(created.AssetId, StringComparison.OrdinalIgnoreCase)))
        {
            battleMapAssets.Add(created);
        }
        var table = document.Snapshot.Functions[functionIndex].Table;
        if (table is null) return;
        var row = fields.Rows.Cast<DataGridViewRow>().FirstOrDefault(value =>
            value.Tag is TableField field && field.Index == table.Fields[0].Index);
        if (row is null) return;
        ConfigureBattleMapCell(row, created.AssetId);
    }

    private void OpenBattleScript()
    {
        if (TryReadSelectedBattleScenarioId(out var scenarioId))
            openBattleScript?.Invoke(scenarioId);
    }

    private void AddEncounter()
    {
        var table = ReadCreateMonsters();
        var id = table.Encounters.Count == 0 ? 0 : table.Encounters.Max(value => value.Id) + 1;
        CreateMonstersTableEditor.AddEncounter(
            document, functionIndex, table.Encounters.Count, id,
            Array.Empty<string>(), Array.Empty<int>());
        ReloadAfterStructureChange();
    }

    private void DuplicateEncounter()
    {
        var table = ReadCreateMonsters();
        var selected = SelectedEncounter(table);
        if (selected is null) return;
        var id = table.Encounters.Max(value => value.Id) + 1;
        CreateMonstersTableEditor.DuplicateEncounter(
            document, functionIndex, selected.Index, selected.Index + 1, id);
        ReloadAfterStructureChange();
    }

    private void DeleteEncounter()
    {
        var table = ReadCreateMonsters();
        var selected = SelectedEncounter(table);
        if (selected is null) return;
        if (MessageBox.Show(
                this,
                $"Delete encounter {selected.Id}?",
                "Delete CreateMonsters encounter",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }
        CreateMonstersTableEditor.RemoveEncounter(document, functionIndex, selected.Index);
        ReloadAfterStructureChange();
    }

    private void ReloadAfterStructureChange()
    {
        LoadTable();
        TableChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RunStructureEdit(Action edit)
    {
        try
        {
            edit();
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or OverflowException)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Cannot edit CreateMonsters",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private CreateMonstersTable ReadCreateMonsters()
    {
        var table = document.Snapshot.Functions[functionIndex].Table;
        if (table is null || !CreateMonstersTableReader.TryRead(table, out var parsed) || parsed is null)
            throw new InvalidOperationException("The selected function is not a valid CreateMonsters table.");
        return parsed;
    }

    private CreateMonstersEncounter? SelectedEncounter(CreateMonstersTable table)
    {
        if (fields.CurrentRow?.Tag is not TableField field) return null;
        return table.Encounters.FirstOrDefault(value =>
            value.SourceFields.Any(source => source.Index == field.Index));
    }

    private void SelectEncounter(int encounterIndex)
    {
        var table = ReadCreateMonsters();
        var encounter = table.Encounters.ElementAtOrDefault(encounterIndex);
        if (encounter is null) return;
        var firstField = encounter.SourceFields.FirstOrDefault(value => value.Type != "fill");
        var row = fields.Rows.Cast<DataGridViewRow>().FirstOrDefault(value =>
            value.Tag is TableField field && field.Index == firstField?.Index);
        if (row is null) return;
        fields.CurrentCell = row.Cells["value"];
    }

    private void UpdateEncounterButtons()
    {
        if (!duplicateEncounterButton.Visible && !deleteEncounterButton.Visible) return;
        try
        {
            var selected = SelectedEncounter(ReadCreateMonsters());
            duplicateEncounterButton.Enabled = selected is not null;
            deleteEncounterButton.Enabled = selected is not null;
        }
        catch (InvalidOperationException)
        {
            duplicateEncounterButton.Enabled = false;
            deleteEncounterButton.Enabled = false;
        }
    }

    private void ApplyChanges()
    {
        try
        {
            foreach (DataGridViewRow row in fields.Rows)
            {
                if (row.Tag is not TableField field) continue;
                var text = row.Cells["value"].Value?.ToString() ?? string.Empty;
                switch (field.Type)
                {
                    case "u8":
                        document.SetTableInteger(functionIndex, field.Index,
                            byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture));
                        break;
                    case "s16":
                        document.SetTableInteger(functionIndex, field.Index,
                            short.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture));
                        break;
                    case "s32":
                        document.SetTableInteger(functionIndex, field.Index,
                            int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture));
                        break;
                    case "f32":
                        document.SetTableFloat(functionIndex, field.Index,
                            float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
                        break;
                    case "string":
                        document.SetTableText(functionIndex, field.Index, text);
                        break;
                    case "bytes":
                        var bytes = ParseHex(text);
                        if (bytes.Length != field.Raw.Length)
                            throw new FormatException(
                                $"Field {field.Index} must remain exactly {field.Raw.Length} bytes long.");
                        document.SetTableBytes(functionIndex, field.Index, bytes);
                        break;
                }
            }
            ApplyEncounterHeader();
            ApplyEncounterSlots();
            LoadTable();
            TableChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException
            or ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "Invalid table value",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ApplyEncounterHeader()
    {
        if (!editorTabs.TabPages.Contains(encounterTab)) return;
        var table = ReadCreateMonsters();
        if (table.HeaderFields.Count < 5) return;
        if (!TryReadSelectedBattleScenarioId(out var scenarioId))
            throw new FormatException("Select a valid btl#### battle script.");
        var descriptor = (uint)scenarioId
            | ((uint)battleFlags.Value << 16);
        document.SetTableInteger(
            functionIndex,
            table.HeaderFields[2].Index,
            unchecked((int)descriptor));
        document.SetTableInteger(
            functionIndex,
            table.HeaderFields[3].Index,
            (int)battleBgm1.Value);
        document.SetTableInteger(
            functionIndex,
            table.HeaderFields[4].Index,
            (int)battleBgm2.Value);
    }

    private void ApplyEncounterSlots()
    {
        foreach (DataGridViewRow row in encounterSlots.Rows)
        {
            if (row.Tag is not CreateMonstersEncounter encounter
                || row.Cells["slot"].Value is not { } slotValue
                || !int.TryParse(slotValue.ToString(), out var slot)
                || slot is < 0 or > 7)
            {
                continue;
            }
            // The record has alternating fixed-string/fill fields, followed by
            // the eight independent probability bytes.
            var monsterField = encounter.SourceFields[1 + slot * 2];
            var chanceField = encounter.SourceFields[17 + slot];
            document.SetTableText(
                functionIndex,
                monsterField.Index,
                row.Cells["monster"].Value?.ToString() ?? string.Empty);
            document.SetTableInteger(
                functionIndex,
                chanceField.Index,
                byte.Parse(
                    row.Cells["chance"].Value?.ToString() ?? "0",
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture));
        }
    }

    private static string FormatValue(TableField field) => field.Type switch
    {
        "string" => field.Text ?? string.Empty,
        "f32" => field.FloatValue.ToString("G9", CultureInfo.InvariantCulture),
        "bytes" or "fill" => Convert.ToHexString(field.Raw),
        _ => field.IntValue.ToString(CultureInfo.InvariantCulture),
    };

    private static byte[] ParseHex(string text)
    {
        var compact = new string(text.Where(value => !char.IsWhiteSpace(value) && value != '-').ToArray());
        if (compact.Length % 2 != 0) throw new FormatException("Hexadecimal byte fields require an even number of digits.");
        return Convert.FromHexString(compact);
    }

    private static NumericUpDown HeaderNumber() => new()
    {
        Minimum = 0,
        Maximum = ushort.MaxValue,
        Width = 72,
    };

    private static void AddHeaderEditor(
        FlowLayoutPanel panel,
        string label,
        Control editor)
    {
        panel.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Padding = new Padding(6, 4, 0, 0),
        });
        panel.Controls.Add(editor);
    }

    private void PopulateBattleScenarioList(int scenarioId)
    {
        battleScenarioList.BeginUpdate();
        try
        {
            battleScenarioList.Items.Clear();
            battleScenarioList.Items.AddRange(battleScenarios.Cast<object>().ToArray());
            var selected = battleScenarios.FirstOrDefault(value => value.Id == scenarioId);
            if (selected is not null)
                battleScenarioList.SelectedItem = selected;
            else
                battleScenarioList.Text = $"btl{scenarioId:0000}";
        }
        finally
        {
            battleScenarioList.EndUpdate();
        }
    }

    private bool TryReadSelectedBattleScenarioId(out int scenarioId)
    {
        if (battleScenarioList.SelectedItem is BattleScenarioEntry selected)
        {
            scenarioId = selected.Id;
            return true;
        }
        var text = battleScenarioList.Text.Trim();
        if (text.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
            text = Path.GetFileNameWithoutExtension(text);
        if (text.StartsWith("btl", StringComparison.OrdinalIgnoreCase))
            text = text[3..];
        return int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out scenarioId)
            && scenarioId is >= 0 and <= ushort.MaxValue;
    }

    private static string DescribeField(DecompiledTable table, TableField field)
    {
        if (table.Kind != "CreateMonsters") return $"Field {field.Index}";
        if (field.Index == 0) return "Map asset";
        if (field.Index == 2) return "Battle scenario ID / flags";
        if (field.Index == 3) return "Battle BGM 1";
        if (field.Index == 4) return "Battle BGM 2";
        if (field.Index == 7) return "Battle UI/profile ID";
        if (field.Index is >= 5 and <= 8) return $"Unknown battle header {field.Index - 4}";
        if (!CreateMonstersTableReader.TryRead(table, out var parsed) || parsed is null)
            return $"Field {field.Index}";
        foreach (var encounter in parsed.Encounters)
        {
            var local = encounter.SourceFields.ToList().FindIndex(value => value.Index == field.Index);
            if (local < 0) continue;
            if (local == 0) return $"Encounter {encounter.Index}: ID";
            if (local is >= 1 and <= 16)
                return $"Encounter {encounter.Index}: monster {(local - 1) / 2}";
            if (local is >= 17 and <= 24)
                return $"Encounter {encounter.Index}: spawn chance % {local - 17}";
            return $"Encounter {encounter.Index}: auxiliary data";
        }
        return "Trailer data";
    }

    private static bool TryGetMonsterSlot(
        DecompiledTable table,
        TableField field,
        out int slot)
    {
        slot = -1;
        if (!CreateMonstersTableReader.TryRead(table, out var parsed) || parsed is null)
            return false;
        foreach (var encounter in parsed.Encounters)
        {
            var local = encounter.SourceFields.ToList().FindIndex(value =>
                value.Index == field.Index);
            if (local is >= 1 and <= 16 && local % 2 == 1)
            {
                slot = (local - 1) / 2;
                return true;
            }
        }
        return false;
    }
}
