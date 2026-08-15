using System.Globalization;
using System.Numerics;
using System.Text;
using ZombiesVsPlants2.SaveEditor.Editor;
using ZombiesVsPlants2.SaveEditor.Rton;

namespace ZombiesVsPlants2.SaveEditor.Tui;

internal sealed class TuiApp
{
    private static readonly CurrencyDefinition[] CurrencyFields =
    [
        new("Coins", "c"),
        new("Gems", "g"),
        new("Gauntlets", "t"),
        new("Mints", "m"),
        new("Fuel", "pf")
    ];

    private EditorSession _session;
    private int _mainSelection;

    private TuiApp(EditorSession session) => _session = session;

    public static int Run(string? initialPath)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("The interactive TUI requires terminal input and output. Use --inspect or --self-test for non-interactive operation.");
            return 2;
        }

        // With no explicit path, prefer a nearby pp.dat for double-click workflows.
        string? path = initialPath is null ? FindAutomaticSavePath() : NormalizePath(initialPath);
        while (true)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                ConsoleUi.Clear();
                ConsoleUi.WriteTitle("Zombies vs Plants 2 Save Editor");
                path = ConsoleUi.PromptOptional("Enter the path to an RTON file, or drag the file into this window and press Enter");
                path = NormalizePath(path);
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return 0;
            }

            try
            {
                EditorSession session = EditorSession.Load(path);
                return new TuiApp(session).RunLoop();
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or ArgumentException
                or NotSupportedException
                or OverflowException)
            {
                ConsoleUi.Error(exception.Message);
                path = null;
            }
        }
    }

    private int RunLoop()
    {
        while (true)
        {
            IReadOnlyList<ProfileView> profiles = SaveDataNavigator.GetProfiles(_session.Document);
            List<string> items = profiles.Select(FormatProfileMenuItem).ToList();
            int rtonBrowserIndex = items.Count;
            items.Add("[RTON Browser] Browse objects and arrays, rename keys, and edit scalar values");
            int openIndex = items.Count;
            items.Add("[Open] Load another RTON file");
            int saveIndex = items.Count;
            items.Add("[Save] Overwrite the current file and create a timestamped backup");
            int saveAsIndex = items.Count;
            items.Add("[Save As] Write to a new RTON file");
            int advancedIndex = items.Count;
            items.Add("[Advanced] Search and edit any scalar field");
            int undoIndex = items.Count;
            items.Add(_session.CanUndo ? "[Undo] Revert the last change" : "[Undo] No changes to undo");
            int reloadIndex = items.Count;
            items.Add("[Reload] Discard unsaved changes and read the file from disk");
            int quitIndex = items.Count;
            items.Add("[Quit]");

            IReadOnlyList<string> header =
            [
                $"File: {_session.Path}",
                $"Status: {(_session.IsDirty ? "unsaved changes" : "saved")} | {_session.CurrentByteLength:N0} bytes | SHA-256 {_session.CurrentSha256[..16]}...",
                $"Structure: RTON v{_session.Document.Version} | {profiles.Count} profiles | "
                    + $"{_session.Document.Metadata.ObjectCount:N0} objects | {_session.Document.Metadata.ArrayCount:N0} arrays | "
                    + $"depth {_session.Document.Metadata.MaximumDepth}"
            ];

            _mainSelection = Math.Clamp(_mainSelection, 0, items.Count - 1);
            int selected = ConsoleUi.Select(
                "Zombies vs Plants 2 Save Editor",
                items,
                header,
                "Up/Down: select | Enter: confirm | Esc: quit",
                _mainSelection);
            if (selected < 0)
            {
                selected = quitIndex;
            }

            _mainSelection = selected;
            if (selected < profiles.Count)
            {
                EditProfile(profiles[selected].Index);
            }
            else if (selected == rtonBrowserIndex)
            {
                BrowseRtonDocument();
            }
            else if (selected == openIndex)
            {
                OpenOtherFile();
            }
            else if (selected == saveIndex)
            {
                SaveCurrent();
            }
            else if (selected == saveAsIndex)
            {
                SaveAs();
            }
            else if (selected == advancedIndex)
            {
                AdvancedSearch(SaveDataNavigator.WrapRoot(_session.Document.Root), "$", "Global Field Search");
            }
            else if (selected == undoIndex)
            {
                if (_session.Undo())
                {
                    ConsoleUi.Success("The last change was undone.");
                }
                else
                {
                    ConsoleUi.Notice("There are no changes to undo.");
                }
            }
            else if (selected == reloadIndex)
            {
                Reload();
            }
            else if (selected == quitIndex)
            {
                if (!_session.IsDirty || ConsoleUi.Confirm("There are unsaved changes. Quit and discard them?"))
                {
                    return 0;
                }
            }
        }
    }

    private void EditProfile(int objectIndex)
    {
        int selection = 0;
        while (true)
        {
            ProfileView profile = GetProfile(objectIndex);
            List<string> items =
            [
                "Edit profile name (n)"
            ];
            items.AddRange(CurrencyFields.Select(field => $"Edit {field.Label} ({field.Field})"));
            int allCurrenciesIndex = items.Count;
            items.Add("Set all five resources to one value");
            int batchPlantsIndex = items.Count;
            items.Add("Bulk edit all plant records");
            int browsePlantsIndex = items.Count;
            items.Add("Browse, unlock, and edit a plant record");
            int onePlantIndex = items.Count;
            items.Add("Unlock or edit a record by plant ID or English name");
            int advancedIndex = items.Count;
            items.Add("Search any field in this profile");
            int backIndex = items.Count;
            items.Add("Back to profile list");

            IReadOnlyList<string> header = BuildProfileHeader(profile);
            selection = Math.Clamp(selection, 0, items.Count - 1);
            int selected = ConsoleUi.Select(
                $"Edit Profile #{objectIndex + 1}: {profile.Name}",
                items,
                header,
                "Up/Down: select | Enter: confirm | Esc: back",
                selection);
            if (selected < 0 || selected == backIndex)
            {
                return;
            }

            selection = selected;
            if (selected == 0)
            {
                EditProfileName(objectIndex);
            }
            else if (selected >= 1 && selected <= CurrencyFields.Length)
            {
                EditCurrency(objectIndex, CurrencyFields[selected - 1]);
            }
            else if (selected == allCurrenciesIndex)
            {
                EditAllCurrencies(objectIndex);
            }
            else if (selected == batchPlantsIndex)
            {
                EditAllPlantStats(objectIndex);
            }
            else if (selected == browsePlantsIndex)
            {
                BrowsePlants(objectIndex);
            }
            else if (selected == onePlantIndex)
            {
                EditOnePlant(objectIndex);
            }
            else if (selected == advancedIndex)
            {
                ProfileView current = GetProfile(objectIndex);
                RtonValue profileRoot = new() { TypeCode = 0x85, Kind = RtonValueKind.Object, Data = current.Data };
                AdvancedSearch(profileRoot, $"$.objects[{objectIndex}].objdata", "Profile Field Search");
            }
        }
    }

    private void EditProfileName(int objectIndex)
    {
        ProfileView profile = GetProfile(objectIndex);
        string? value = ConsoleUi.PromptStringEdit("Enter the new profile name", profile.Name);
        if (value is null)
        {
            return;
        }

        if (value.Length > 128)
        {
            ConsoleUi.Error("The profile name cannot exceed 128 characters.");
            return;
        }

        ApplyChange(
            document => GetProfile(document, objectIndex).SetString("n", value),
            $"The profile name was changed to \"{value}\".");
    }

    private void EditCurrency(int objectIndex, CurrencyDefinition currency)
    {
        ProfileView profile = GetProfile(objectIndex);
        string current = profile.GetInteger(currency.Field)?.ToString(CultureInfo.InvariantCulture) ?? "—";
        BigInteger? value = PromptSafeInteger($"Enter the new {currency.Label} amount", current);
        if (value is null)
        {
            return;
        }

        ApplyChange(
            document => GetProfile(document, objectIndex).SetInteger(currency.Field, value.Value),
            $"{currency.Label} was set to {value.Value:N0}.");
    }

    private void EditAllCurrencies(int objectIndex)
    {
        BigInteger? value = PromptSafeInteger("Enter one amount for all five resources", null);
        if (value is null)
        {
            return;
        }

        ApplyChange(
            document =>
            {
                ProfileView profile = GetProfile(document, objectIndex);
                foreach (CurrencyDefinition field in CurrencyFields)
                {
                    profile.SetInteger(field.Field, value.Value);
                }
            },
            $"All five resources were set to {value.Value:N0}.");
    }

    private void EditAllPlantStats(int objectIndex)
    {
        ProfileView profile = GetProfile(objectIndex);
        IReadOnlyList<PlantStatView> records = profile.GetPlantStats();
        if (records.Count == 0)
        {
            ConsoleUi.Notice("This profile has no plant progression records.");
            return;
        }

        ConsoleUi.Clear();
        ConsoleUi.WriteTitle($"Bulk Edit Plant Records: {profile.Name}");
        Console.WriteLine($"This will apply to {records.Count} records. Imitater Level and Mastery are skipped because its progression is fixed.");
        Console.WriteLine("Levels are player-visible values. Each known plant is clamped to its catalog maximum.");
        Console.WriteLine("Mastery is skipped for plants that do not support it; unknown plant IDs remain editable without per-plant catalog restrictions.");
        Console.WriteLine("Press Enter without typing to leave each field unchanged.");
        BigInteger? level = PromptIntegerInRange("Player-visible Level", null, 1, int.MaxValue, pauseOnError: false);
        BigInteger? mastery = PromptIntegerInRange("Mastery", null, 0, PlantCatalog.DefaultMaximumMastery, pauseOnError: false);
        BigInteger? seedPackets = PromptIntegerInRange(
            "Seed Packets (x)",
            null,
            BigInteger.Zero,
            PlantCatalog.MaximumSeedPackets,
            pauseOnError: false);
        if (level is null && mastery is null && seedPackets is null)
        {
            return;
        }

        if (!ConsoleUi.Confirm($"Apply these changes to {records.Count} plant records?"))
        {
            return;
        }

        ApplyChange(
            document =>
            {
                foreach (PlantStatView record in GetProfile(document, objectIndex).GetPlantStats())
                {
                    // Save variants may omit individual fields, so update only fields that exist.
                    if (!record.IsImitater && level is not null && record.GetInteger("l") is not null)
                    {
                        BigInteger targetLevel = record.MaximumLevel is int maximumLevel
                            ? BigInteger.Min(level.Value, new BigInteger(maximumLevel))
                            : level.Value;
                        record.SetVisibleLevel(targetLevel);
                    }

                    if (!record.IsImitater && mastery is not null && record.SupportsMastery && record.GetInteger("m") is not null)
                    {
                        record.SetInteger("m", mastery.Value);
                    }

                    if (seedPackets is not null && record.GetInteger("x") is not null)
                    {
                        record.SetInteger("x", seedPackets.Value);
                    }
                }
            },
            "Updated plant records using catalog-aware Level, Mastery, and Seed Packets limits.");
    }

    private void BrowsePlants(int objectIndex)
    {
        int selection = 0;
        while (true)
        {
            ProfileView profile = GetProfile(objectIndex);
            IReadOnlyList<PlantStatView> records = profile.GetPlantStats();
            if (records.Count == 0)
            {
                ConsoleUi.Notice("This profile has no plant progression records.");
                return;
            }

            // Display order is independent from the raw plis index used for edits.
            IReadOnlyList<PlantStatView> displayRecords = records
                .OrderBy(record => PlantCatalog.DisplayName(record.PlantId), StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.PlantId)
                .ThenBy(record => record.Index)
                .ToList();
            List<string> items = displayRecords.Select(record =>
            {
                bool unlocked = profile.IsPlantUnlocked(record.PlantId);
                string ownership = unlocked ? "Unlocked" : "Locked";
                string catalogStatus = record.HasCatalogData ? string.Empty : " | Catalog data unavailable";
                return $"{FormatPlantName(record.PlantId)} | {ownership,-8} | "
                    + $"Level {FormatPlantLevel(record),-12} | "
                    + $"Mastery {FormatPlantMastery(record),-8} | "
                    + $"Seed Packets {record.SeedPackets?.ToString() ?? "—"}{catalogStatus}";
            }).ToList();
            int backIndex = items.Count;
            items.Add("Back");
            selection = Math.Clamp(selection, 0, items.Count - 1);
            int selected = ConsoleUi.Select(
                $"Plant Records: {profile.Name}",
                items,
                [$"Found {records.Count} records. Select one to edit its fields."],
                initialSelection: selection);
            if (selected < 0 || selected == backIndex)
            {
                return;
            }

            selection = selected;
            // Record.Index is the raw plis array index and remains stable across scalar edits.
            PlantStatView record = displayRecords[selected];
            EditPlantRecord(objectIndex, record.Index, record.PlantId);
        }
    }

    private void EditOnePlant(int objectIndex)
    {
        ProfileView profile = GetProfile(objectIndex);
        string? input = ConsoleUi.PromptOptional("Enter a plant ID (p) or English name");
        if (input is null)
        {
            return;
        }

        IReadOnlyList<PlantStatView> records = profile.GetPlantStats();
        IReadOnlyList<PlantStatView> matches;
        if (BigInteger.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger requestedPlantId))
        {
            if (requestedPlantId < 0)
            {
                ConsoleUi.Error("The plant ID must be a non-negative integer.");
                return;
            }

            matches = records.Where(record => record.PlantId == requestedPlantId).ToList();
        }
        else
        {
            string query = input.Trim();
            matches = records
                .Where(record => PlantCatalog.DisplayName(record.PlantId).Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(record => PlantCatalog.DisplayName(record.PlantId), StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.PlantId)
                .ToList();
        }

        if (matches.Count == 0)
        {
            ConsoleUi.Notice($"No plant record matched '{input}'.");
            return;
        }

        PlantStatView selectedRecord;
        if (matches.Count == 1)
        {
            selectedRecord = matches[0];
        }
        else
        {
            int selected = ConsoleUi.Select(
                $"Found {matches.Count} matching records",
                matches.Select(record =>
                    $"{FormatPlantName(record.PlantId)} | Record index {record.Index} | "
                    + $"Level={FormatPlantLevel(record)} | Mastery={FormatPlantMastery(record)} | "
                    + $"Seed Packets (x)={record.SeedPackets}").ToList());
            if (selected < 0)
            {
                return;
            }

            selectedRecord = matches[selected];
        }

        BigInteger plantId = selectedRecord.PlantId;
        EditPlantRecord(objectIndex, selectedRecord.Index, plantId);
    }

    private void EditPlantRecord(int objectIndex, int recordIndex, BigInteger plantId)
    {
        int selection = 0;
        while (true)
        {
            ProfileView profile = GetProfile(objectIndex);
            PlantStatView record = profile.GetPlantStats().First(item => item.Index == recordIndex);
            bool unlocked = profile.IsPlantUnlocked(plantId);
            string levelDisplay = FormatPlantLevel(record);
            string levelItem = record.IsImitater
                ? "Level: 1 (fixed for Imitater)"
                : record.MaximumLevel is int maximumLevel
                    ? $"Edit Level, current: {levelDisplay} (max {maximumLevel})"
                    : $"Edit Level, current: {levelDisplay} (catalog data unavailable)";
            string masteryItem = !record.SupportsMastery
                ? "Mastery: N/A (not supported)"
                : record.MaximumMastery is int maximumMastery
                    ? $"Edit Mastery, current: {record.Mastery} (max {maximumMastery})"
                    : $"Edit Mastery, current: {record.Mastery} (catalog data unavailable; max 200)";
            List<string> items =
            [
                levelItem,
                masteryItem,
                $"Edit Seed Packets (x), current: {record.SeedPackets} (max {PlantCatalog.MaximumSeedPackets:N0})"
            ];
            int unlockIndex = items.Count;
            items.Add(unlocked ? "Plant ownership: Unlocked" : "Unlock this plant");
            int backIndex = items.Count;
            items.Add("Back");
            int selected = ConsoleUi.Select(
                $"{FormatPlantName(plantId)} | Record Index {recordIndex}",
                items,
                [
                    $"Ownership: {(unlocked ? "Unlocked" : "Locked")} | Level: {levelDisplay} | Mastery: {FormatPlantMastery(record)}",
                    FormatPlantCatalogStatus(record)
                ],
                initialSelection: selection);
            if (selected < 0 || selected == backIndex)
            {
                return;
            }

            selection = selected;
            if (selected == unlockIndex)
            {
                if (unlocked)
                {
                    ConsoleUi.Notice("This plant is already unlocked.");
                    continue;
                }

                if (!ConsoleUi.Confirm($"Unlock {FormatPlantName(plantId)}?"))
                {
                    continue;
                }

                ApplyChange(
                    document =>
                    {
                        _ = GetProfile(document, objectIndex).UnlockPlant(plantId);
                    },
                    $"{FormatPlantName(plantId)} was unlocked. Level, mastery, and Seed Packets were left unchanged.");
                continue;
            }

            if (record.IsImitater && selected == 0)
            {
                ConsoleUi.Notice("Imitater has fixed Level 1 and does not support level edits.");
                continue;
            }

            if (!record.SupportsMastery && selected == 1)
            {
                ConsoleUi.Notice($"{PlantCatalog.DisplayName(plantId)} does not support mastery progression.");
                continue;
            }

            string field = selected switch { 0 => "l", 1 => "m", _ => "x" };
            string label = selected switch { 0 => "Level", 1 => "Mastery", _ => "Seed Packets" };
            BigInteger? currentValue = record.GetInteger(field);
            if (currentValue is null)
            {
                ConsoleUi.Notice($"This plant record does not contain field '{field}'.");
                continue;
            }

            string current = selected == 0
                ? record.VisibleLevel?.ToString(CultureInfo.InvariantCulture) ?? "1"
                : currentValue.Value.ToString(CultureInfo.InvariantCulture);
            BigInteger? value = selected switch
            {
                0 => PromptIntegerInRange(
                    "Enter the new player-visible Level",
                    current,
                    1,
                    record.MaximumLevel ?? int.MaxValue),
                1 => PromptIntegerInRange(
                    "Enter the new Mastery",
                    current,
                    0,
                    record.MaximumMastery ?? PlantCatalog.DefaultMaximumMastery),
                _ => PromptIntegerInRange(
                    "Enter the new Seed Packets",
                    current,
                    BigInteger.Zero,
                    PlantCatalog.MaximumSeedPackets)
            };
            if (value is null)
            {
                continue;
            }

            ApplyChange(
                document =>
                {
                    PlantStatView currentRecord = GetProfile(document, objectIndex).GetPlantStats()
                        .First(item => item.Index == recordIndex);
                    if (selected == 0)
                    {
                        currentRecord.SetVisibleLevel(value.Value);
                    }
                    else
                    {
                        currentRecord.SetInteger(field, value.Value);
                    }
                },
                $"{FormatPlantName(plantId)}: {label} was set to {value.Value:N0}.");
        }
    }

    private void BrowseRtonDocument()
    {
        List<RtonPathStep> path = [];
        List<int> selections = [0];

        while (true)
        {
            RtonValue container;
            try
            {
                container = ResolveRtonPath(_session.Document, path);
            }
            catch (InvalidDataException exception)
            {
                ConsoleUi.Error(exception.Message);
                return;
            }

            string breadcrumb = GetRtonBreadcrumb(_session.Document, path);
            if (container.Kind == RtonValueKind.Object)
            {
                RtonObject currentObject = container.AsObject();
                int backIndex = currentObject.Properties.Count;
                IReadOnlyList<string> items = new LazyMenuList(
                    currentObject.Properties.Count,
                    index =>
                    {
                        RtonProperty property = currentObject.Properties[index];
                        string key = EscapeRtonText(property.Key.Text, escapePathSyntax: true);
                        return $"{index,4}  {ConsoleUi.Truncate(key, 42),-42}  {FormatRtonBrowserValue(property.Value)}";
                    },
                    path.Count == 0 ? "Back to main menu" : "Back to parent container");

                selections[path.Count] = Math.Clamp(selections[path.Count], 0, items.Count - 1);
                int selected = ConsoleUi.Select(
                    "RTON Browser: Object",
                    items,
                    [
                        $"Breadcrumb: {breadcrumb}",
                        $"{currentObject.Properties.Count:N0} properties | Select a property to open it, edit its value, or rename its key."
                    ],
                    "Up/Down: select | Enter: property actions | Esc: parent",
                    selections[path.Count]);
                if (selected < 0 || selected == backIndex)
                {
                    if (path.Count == 0)
                    {
                        return;
                    }

                    path.RemoveAt(path.Count - 1);
                    selections.RemoveAt(selections.Count - 1);
                    continue;
                }

                selections[path.Count] = selected;
                RtonPathStep selectedStep = RtonPathStep.ObjectProperty(selected);
                IReadOnlyList<RtonPathStep> valuePath = AppendRtonPath(path, selectedStep);
                RtonProperty property = currentObject.Properties[selected];
                bool isContainer = property.Value.Kind is RtonValueKind.Object or RtonValueKind.Array;
                List<string> actions = [];
                int openIndex = -1;
                if (isContainer)
                {
                    openIndex = actions.Count;
                    actions.Add($"Open {property.Value.Kind}");
                }

                int editIndex = -1;
                if (!isContainer)
                {
                    editIndex = actions.Count;
                    actions.Add(property.Value.IsEditable
                        ? "Edit scalar value"
                        : $"Scalar value is read-only (RTON type 0x{property.Value.TypeCode:X2})");
                }

                int renameIndex = actions.Count;
                actions.Add(property.Key.IsEditable
                    ? "Rename key"
                    : $"Key is read-only (RTON type 0x{property.Key.TypeCode:X2})");
                int cancelIndex = actions.Count;
                actions.Add("Back to object");

                int action = ConsoleUi.Select(
                    "RTON Property",
                    actions,
                    [
                        $"Breadcrumb: {GetRtonBreadcrumb(_session.Document, valuePath)}",
                        $"Key: {EscapeRtonText(property.Key.Text, escapePathSyntax: true)} | RTON type 0x{property.Key.TypeCode:X2}",
                        $"Value: {FormatRtonBrowserValue(property.Value)}"
                    ]);
                if (action < 0 || action == cancelIndex)
                {
                    continue;
                }

                if (action == openIndex)
                {
                    path.Add(selectedStep);
                    selections.Add(0);
                }
                else if (action == editIndex)
                {
                    if (property.Value.IsEditable)
                    {
                        EditRtonScalar(valuePath);
                    }
                    else
                    {
                        ConsoleUi.Notice($"This scalar uses special RTON type 0x{property.Value.TypeCode:X2} and is read-only.");
                    }
                }
                else if (action == renameIndex)
                {
                    if (property.Key.IsEditable)
                    {
                        RenameRtonObjectKey(path, selected);
                    }
                    else
                    {
                        ConsoleUi.Notice($"This key uses special RTON type 0x{property.Key.TypeCode:X2} and is read-only.");
                    }
                }
            }
            else if (container.Kind == RtonValueKind.Array)
            {
                RtonArray currentArray = container.AsArray();
                int backIndex = currentArray.Items.Count;
                IReadOnlyList<string> items = new LazyMenuList(
                    currentArray.Items.Count,
                    index => $"[{index,4}]  {FormatRtonBrowserValue(currentArray.Items[index])}",
                    path.Count == 0 ? "Back to main menu" : "Back to parent container");

                selections[path.Count] = Math.Clamp(selections[path.Count], 0, items.Count - 1);
                int selected = ConsoleUi.Select(
                    "RTON Browser: Array",
                    items,
                    [
                        $"Breadcrumb: {breadcrumb}",
                        $"{currentArray.Items.Count:N0} items | declared capacity {currentArray.DeclaredCapacity:N0} | "
                            + "Containers open directly; editable scalars open a value editor."
                    ],
                    "Up/Down: select | Enter: open or edit | Esc: parent",
                    selections[path.Count]);
                if (selected < 0 || selected == backIndex)
                {
                    if (path.Count == 0)
                    {
                        return;
                    }

                    path.RemoveAt(path.Count - 1);
                    selections.RemoveAt(selections.Count - 1);
                    continue;
                }

                selections[path.Count] = selected;
                RtonPathStep selectedStep = RtonPathStep.ArrayItem(selected);
                IReadOnlyList<RtonPathStep> valuePath = AppendRtonPath(path, selectedStep);
                RtonValue selectedValue = currentArray.Items[selected];
                if (selectedValue.Kind is RtonValueKind.Object or RtonValueKind.Array)
                {
                    path.Add(selectedStep);
                    selections.Add(0);
                }
                else if (selectedValue.IsEditable)
                {
                    EditRtonScalar(valuePath);
                }
                else
                {
                    ConsoleUi.Notice($"This array item uses special RTON type 0x{selectedValue.TypeCode:X2} and is read-only.");
                }
            }
            else
            {
                ConsoleUi.Error($"The browser path resolved to {container.Kind}, not a container.");
                return;
            }
        }
    }

    private void RenameRtonObjectKey(IReadOnlyList<RtonPathStep> objectPath, int propertyIndex)
    {
        RtonObject currentObject = ResolveRtonPath(_session.Document, objectPath).AsObject();
        if (propertyIndex < 0 || propertyIndex >= currentObject.Properties.Count)
        {
            ConsoleUi.Error("The selected property no longer exists.");
            return;
        }

        RtonProperty currentProperty = currentObject.Properties[propertyIndex];
        string? newName = ConsoleUi.PromptStringEdit(
            "Enter the new key name",
            EscapeRtonText(currentProperty.Key.Text, escapePathSyntax: true));
        if (newName is null)
        {
            return;
        }

        ApplyChange(
            document => ResolveRtonPath(document, objectPath).AsObject().RenameProperty(propertyIndex, newName),
            $"Renamed the key to \"{EscapeRtonText(newName, escapePathSyntax: true)}\".");
    }

    private void EditRtonScalar(IReadOnlyList<RtonPathStep> valuePath)
    {
        RtonValue current = ResolveRtonPath(_session.Document, valuePath);
        string breadcrumb = GetRtonBreadcrumb(_session.Document, valuePath);
        switch (current.Kind)
        {
            case RtonValueKind.Boolean:
                {
                    string? input = ConsoleUi.PromptOptional(
                        $"Enter true/false or 1/0 for {breadcrumb}",
                        current.ToDisplayString());
                    if (input is null)
                    {
                        return;
                    }

                    bool? parsed = input.ToLowerInvariant() switch
                    {
                        "true" or "1" or "yes" or "y" => true,
                        "false" or "0" or "no" or "n" => false,
                        _ => null
                    };
                    if (parsed is null)
                    {
                        ConsoleUi.Error("The value is not a recognized Boolean.");
                        return;
                    }

                    ApplyChange(
                        document => ResolveRtonPath(document, valuePath).SetBoolean(parsed.Value),
                        $"Updated {breadcrumb}.");
                    break;
                }
            case RtonValueKind.SignedInteger:
            case RtonValueKind.UnsignedInteger:
                {
                    string? input = ConsoleUi.PromptOptional(
                        $"Enter the new integer for {breadcrumb}",
                        current.ToDisplayString());
                    if (input is null)
                    {
                        return;
                    }

                    if (!BigInteger.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger parsed))
                    {
                        ConsoleUi.Error("The input is not a valid integer.");
                        return;
                    }

                    ApplyChange(
                        document => ResolveRtonPath(document, valuePath).SetInteger(parsed),
                        $"Updated {breadcrumb}.");
                    break;
                }
            case RtonValueKind.FloatingPoint:
                {
                    string? input = ConsoleUi.PromptOptional(
                        $"Enter the new floating-point value for {breadcrumb}",
                        current.ToDisplayString());
                    if (input is null)
                    {
                        return;
                    }

                    if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                        || !double.IsFinite(parsed))
                    {
                        ConsoleUi.Error("The input is not a finite floating-point number.");
                        return;
                    }

                    ApplyChange(
                        document => ResolveRtonPath(document, valuePath).SetFloatingPoint(parsed),
                        $"Updated {breadcrumb}.");
                    break;
                }
            case RtonValueKind.String:
                {
                    string? input = ConsoleUi.PromptStringEdit(
                        $"Enter the new string for {breadcrumb}",
                        EscapeRtonText(current.AsString(), escapePathSyntax: false));
                    if (input is null)
                    {
                        return;
                    }

                    ApplyChange(
                        document => ResolveRtonPath(document, valuePath).SetString(input),
                        $"Updated {breadcrumb}.");
                    break;
                }
            default:
                ConsoleUi.Notice($"This value uses RTON type 0x{current.TypeCode:X2} and is read-only.");
                break;
        }
    }

    private static RtonValue ResolveRtonPath(RtonDocument document, IReadOnlyList<RtonPathStep> path)
    {
        RtonValue current = SaveDataNavigator.WrapRoot(document.Root);
        foreach (RtonPathStep step in path)
        {
            if (step.FromObjectProperty)
            {
                if (current.Kind != RtonValueKind.Object)
                {
                    throw new InvalidDataException("The RTON navigation path expected an object.");
                }

                IReadOnlyList<RtonProperty> properties = current.AsObject().Properties;
                if (step.Index < 0 || step.Index >= properties.Count)
                {
                    throw new InvalidDataException("An object property in the RTON navigation path no longer exists.");
                }

                current = properties[step.Index].Value;
            }
            else
            {
                if (current.Kind != RtonValueKind.Array)
                {
                    throw new InvalidDataException("The RTON navigation path expected an array.");
                }

                IReadOnlyList<RtonValue> items = current.AsArray().Items;
                if (step.Index < 0 || step.Index >= items.Count)
                {
                    throw new InvalidDataException("An array item in the RTON navigation path no longer exists.");
                }

                current = items[step.Index];
            }
        }

        return current;
    }

    private static string GetRtonBreadcrumb(RtonDocument document, IReadOnlyList<RtonPathStep> path)
    {
        StringBuilder breadcrumb = new("$");
        RtonValue current = SaveDataNavigator.WrapRoot(document.Root);
        foreach (RtonPathStep step in path)
        {
            if (step.FromObjectProperty)
            {
                if (current.Kind != RtonValueKind.Object
                    || step.Index < 0
                    || step.Index >= current.AsObject().Properties.Count)
                {
                    throw new InvalidDataException("An object property in the RTON breadcrumb no longer exists.");
                }

                RtonProperty property = current.AsObject().Properties[step.Index];
                breadcrumb.Append(FormatRtonKeyPathSegment(property.Key.Text));
                current = property.Value;
            }
            else
            {
                if (current.Kind != RtonValueKind.Array
                    || step.Index < 0
                    || step.Index >= current.AsArray().Items.Count)
                {
                    throw new InvalidDataException("An array item in the RTON breadcrumb no longer exists.");
                }

                breadcrumb.Append('[').Append(step.Index).Append(']');
                current = current.AsArray().Items[step.Index];
            }
        }

        return breadcrumb.ToString();
    }

    private static string FormatRtonKeyPathSegment(string key)
    {
        bool isIdentifier = key.Length > 0
            && (char.IsLetter(key[0]) || key[0] == '_')
            && key.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');
        if (isIdentifier)
        {
            return "." + key;
        }

        string escaped = EscapeRtonText(key, escapePathSyntax: true);
        return $"[\"{escaped}\"]";
    }

    private static string EscapeRtonText(string value, bool escapePathSyntax)
    {
        StringBuilder escaped = new(value.Length);
        foreach (char character in value)
        {
            switch (character)
            {
                case '\r':
                    escaped.Append("\\r");
                    break;
                case '\n':
                    escaped.Append("\\n");
                    break;
                case '\t':
                    escaped.Append("\\t");
                    break;
                case '\\' when escapePathSyntax:
                    escaped.Append("\\\\");
                    break;
                case '\"' when escapePathSyntax:
                    escaped.Append("\\\"");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        escaped.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        escaped.Append(character);
                    }

                    break;
            }
        }

        return escaped.ToString();
    }

    private static string FormatRtonBrowserValue(RtonValue value)
    {
        string summary = EscapeRtonText(value.ToDisplayString(90), escapePathSyntax: false);
        return value.Kind switch
        {
            RtonValueKind.Object or RtonValueKind.Array => $"{value.Kind} 0x{value.TypeCode:X2} | {summary}",
            RtonValueKind.Special => $"Special 0x{value.TypeCode:X2} | {summary}",
            _ => $"{value.Kind} 0x{value.TypeCode:X2} | {summary}"
        };
    }

    private static IReadOnlyList<RtonPathStep> AppendRtonPath(
        IReadOnlyList<RtonPathStep> path,
        RtonPathStep step)
    {
        List<RtonPathStep> result = new(path.Count + 1);
        result.AddRange(path);
        result.Add(step);
        return result;
    }

    private void AdvancedSearch(RtonValue root, string rootPath, string title)
    {
        string? query = ConsoleUi.PromptOptional("Enter a field path or value to search for, such as .c, plis, or Mastery");
        if (query is null)
        {
            return;
        }

        IReadOnlyList<ScalarReference> results = SaveDataNavigator.SearchScalars(root, rootPath, query, 300);
        if (results.Count == 0)
        {
            ConsoleUi.Notice("No matching fields were found.");
            return;
        }

        List<string> items = results.Select(result =>
            $"{(IsAdvancedScalarEditable(result) ? "[Editable]" : "[Read-only]")} {result.Path} = {result.Value.ToDisplayString(70)}").ToList();
        int selected = ConsoleUi.Select(
            title,
            items,
            [results.Count == 300 ? "Showing the first 300 results. Use a more specific search term to narrow the list." : $"Found {results.Count} results."]);
        if (selected < 0)
        {
            return;
        }

        ScalarReference scalar = results[selected];
        if (!IsAdvancedScalarEditable(scalar))
        {
            string reason = IsProtectedPlantPath(scalar.Path)
                ? "It is managed by the plant editor so ownership and progression limits stay consistent; use the plant browser instead."
                : $"It uses special RTON type 0x{scalar.Value.TypeCode:X2}.";
            ConsoleUi.Notice($"{scalar.Path} is read-only. {reason}");
            return;
        }

        EditScalar(scalar);
    }

    private void EditScalar(ScalarReference scalar)
    {
        RtonValue value = scalar.Value;
        switch (value.Kind)
        {
            case RtonValueKind.Boolean:
                {
                    string? input = ConsoleUi.PromptOptional("Enter true/false or 1/0", value.ToDisplayString());
                    if (input is null)
                    {
                        return;
                    }

                    bool? parsed = input.ToLowerInvariant() switch
                    {
                        "true" or "1" or "yes" or "y" => true,
                        "false" or "0" or "no" or "n" => false,
                        _ => null
                    };
                    if (parsed is null)
                    {
                        ConsoleUi.Error("The value is not a recognized Boolean.");
                        return;
                    }

                    ApplyChange(_ => value.SetBoolean(parsed.Value), $"Updated {scalar.Path}.");
                    break;
                }
            case RtonValueKind.SignedInteger:
            case RtonValueKind.UnsignedInteger:
                {
                    string? input = ConsoleUi.PromptOptional("Enter the new integer", value.ToDisplayString());
                    if (input is null)
                    {
                        return;
                    }

                    if (!BigInteger.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger parsed))
                    {
                        ConsoleUi.Error("The input is not a valid integer.");
                        return;
                    }

                    ApplyChange(_ => value.SetInteger(parsed), $"Updated {scalar.Path}.");
                    break;
                }
            case RtonValueKind.FloatingPoint:
                {
                    string? input = ConsoleUi.PromptOptional("Enter the new floating-point value", value.ToDisplayString());
                    if (input is null)
                    {
                        return;
                    }

                    if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                        || !double.IsFinite(parsed))
                    {
                        ConsoleUi.Error("The input is not a finite floating-point number.");
                        return;
                    }

                    ApplyChange(_ => value.SetFloatingPoint(parsed), $"Updated {scalar.Path}.");
                    break;
                }
            case RtonValueKind.String:
                {
                    string? input = ConsoleUi.PromptStringEdit("Enter the new string", value.AsString());
                    if (input is null)
                    {
                        return;
                    }

                    ApplyChange(_ => value.SetString(input), $"Updated {scalar.Path}.");
                    break;
                }
        }
    }

    private static bool IsAdvancedScalarEditable(ScalarReference scalar) =>
        scalar.Value.IsEditable && !IsProtectedPlantPath(scalar.Path);

    internal static bool IsProtectedPlantPath(string path)
    {
        const string marker = ".objdata.";
        int markerIndex = path.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        string relativePath = path[(markerIndex + marker.Length)..];
        if (relativePath.StartsWith("p[", StringComparison.Ordinal)
            && relativePath.EndsWith(']')
            && !relativePath.Contains('.'))
        {
            return true;
        }

        if ((relativePath.StartsWith("pli[", StringComparison.Ordinal)
                || relativePath.StartsWith("tltep[", StringComparison.Ordinal))
            && relativePath.EndsWith(".p", StringComparison.Ordinal))
        {
            return true;
        }

        return relativePath.StartsWith("plis[", StringComparison.Ordinal)
            && (relativePath.EndsWith(".p", StringComparison.Ordinal)
                || relativePath.EndsWith(".l", StringComparison.Ordinal)
                || relativePath.EndsWith(".m", StringComparison.Ordinal)
                || relativePath.EndsWith(".x", StringComparison.Ordinal));
    }

    private void SaveCurrent()
    {
        try
        {
            SaveResult result = _session.Save(_session.Path, createBackup: true);
            string message = $"Saved: {result.Path}\nSize: {result.ByteLength:N0} bytes\nSHA-256: {result.Sha256}";
            if (result.BackupPath is not null)
            {
                message += $"\nOriginal file backup: {result.BackupPath}";
            }

            ConsoleUi.Success(message);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException
            or OverflowException)
        {
            ConsoleUi.Error(exception.Message);
        }
    }

    private void OpenOtherFile()
    {
        if (_session.IsDirty && !ConsoleUi.Confirm("There are unsaved changes. Discard them and open another file?"))
        {
            return;
        }

        string? path = NormalizePath(ConsoleUi.PromptOptional("Enter another RTON file path, or drag the file into this window and press Enter"));
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            _session = EditorSession.Load(path);
            _mainSelection = 0;
            ConsoleUi.Success("The file was loaded without making any changes.");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException
            or OverflowException)
        {
            ConsoleUi.Error(exception.Message);
        }
    }

    private void SaveAs()
    {
        string directory = Path.GetDirectoryName(_session.Path) ?? Environment.CurrentDirectory;
        string extension = Path.GetExtension(_session.Path);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".dat";
        }

        string baseName = Path.GetFileNameWithoutExtension(_session.Path);
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = "save";
        }

        string suggested = Path.Combine(directory, $"{baseName}.edited{extension}");
        string? path = ConsoleUi.PromptWithDefault(
            "Enter the Save As path (relative paths use the current save directory)",
            suggested);
        path = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            path = Path.IsPathFullyQualified(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(path, directory);
            if (Directory.Exists(path))
            {
                throw new ArgumentException("The Save As path must include a file name, not a directory.");
            }

            bool exists = File.Exists(path);
            if (exists && !ConsoleUi.Confirm("The target file already exists. Back it up and overwrite it?"))
            {
                return;
            }

            SaveResult result = _session.Save(path, createBackup: exists);
            string message = $"Saved: {result.Path}\nSize: {result.ByteLength:N0} bytes\nSHA-256: {result.Sha256}";
            if (result.BackupPath is not null)
            {
                message += $"\nPrevious target backup: {result.BackupPath}";
            }

            ConsoleUi.Success(message);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException
            or OverflowException)
        {
            ConsoleUi.Error(exception.Message);
        }
    }

    private void Reload()
    {
        if (_session.IsDirty && !ConsoleUi.Confirm("Discard all unsaved changes and reload the file?"))
        {
            return;
        }

        try
        {
            _session = EditorSession.Load(_session.Path);
            ConsoleUi.Success("The file was reloaded from disk.");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException
            or OverflowException)
        {
            ConsoleUi.Error(exception.Message);
        }
    }

    private void ApplyChange(Action<RtonDocument> action, string successMessage)
    {
        try
        {
            if (_session.ApplyChange(action))
            {
                ConsoleUi.Success(successMessage + "\nThe change is not on disk yet. Choose Save or Save As from the main menu.");
            }
            else
            {
                ConsoleUi.Notice("The new value matches the current value, so nothing changed.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException or InvalidDataException)
        {
            ConsoleUi.Error(exception.Message);
        }
    }

    private ProfileView GetProfile(int objectIndex) => GetProfile(_session.Document, objectIndex);

    private static ProfileView GetProfile(RtonDocument document, int objectIndex) =>
        SaveDataNavigator.GetProfiles(document).FirstOrDefault(profile => profile.Index == objectIndex)
        ?? throw new InvalidDataException($"Profile object index {objectIndex} was not found.");

    private static IReadOnlyList<string> BuildProfileHeader(ProfileView profile)
    {
        string resources = string.Join(" | ", CurrencyFields.Select(field =>
            $"{field.Label} {profile.GetInteger(field.Field)?.ToString("N0", CultureInfo.InvariantCulture) ?? "—"}"));
        return
        [
            $"Name: {profile.Name} | Activity (l): {profile.ActivityLabel ?? "—"}",
            resources,
            $"Plants: {profile.UnlockedPlantCount} unlocked | {profile.PlantStatCount} progression records | {profile.PliEntryCount} auxiliary entries"
        ];
    }

    private static string FormatProfileMenuItem(ProfileView profile)
    {
        string coins = profile.GetInteger("c")?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        string gems = profile.GetInteger("g")?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        return $"#{profile.Index + 1,-2} {ConsoleUi.Truncate(profile.Name, 34),-34} | Coins {coins} | Gems {gems} | Unlocked {profile.UnlockedPlantCount} | Records {profile.PlantStatCount}";
    }

    private static string FormatPlantName(BigInteger plantId) =>
        $"{PlantCatalog.DisplayName(plantId)} (ID {plantId.ToString(CultureInfo.InvariantCulture)})";

    private static string FormatPlantLevel(PlantStatView record)
    {
        if (record.IsImitater)
        {
            return "1";
        }

        return record.VisibleLevel?.ToString(CultureInfo.InvariantCulture) ?? "—";
    }

    private static string FormatPlantMastery(PlantStatView record) => !record.SupportsMastery
        ? "N/A"
        : record.Mastery?.ToString(CultureInfo.InvariantCulture) ?? "—";

    private static string FormatPlantCatalogStatus(PlantStatView record)
    {
        if (record.Definition is not PlantDefinition definition)
        {
            return "Catalog data unavailable for this plant ID; Level and Mastery remain editable with generic limits.";
        }

        string mastery = definition.SupportsMastery
            ? definition.MaximumMastery.ToString(CultureInfo.InvariantCulture)
            : "N/A";
        return $"Catalog limits: Level {definition.MaximumLevel} | Mastery {mastery}";
    }

    private static BigInteger? PromptSafeInteger(
        string prompt,
        string? current,
        bool pauseOnError = true) =>
        PromptIntegerInRange(prompt, current, BigInteger.Zero, new BigInteger(int.MaxValue), pauseOnError);

    private static BigInteger? PromptIntegerInRange(
        string prompt,
        string? current,
        BigInteger minimum,
        BigInteger maximum,
        bool pauseOnError = true)
    {
        while (true)
        {
            string? input = ConsoleUi.PromptOptional(
                prompt + $" (allowed: {minimum.ToString("N0", CultureInfo.InvariantCulture)} to {maximum.ToString("N0", CultureInfo.InvariantCulture)})",
                current);
            if (input is null)
            {
                return null;
            }

            if (BigInteger.TryParse(input.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger value)
                && value >= minimum
                && value <= maximum)
            {
                return value;
            }

            if (pauseOnError)
            {
                ConsoleUi.Error($"Enter an integer from {minimum:N0} to {maximum:N0}.");
            }
            else
            {
                Console.WriteLine("Invalid input; try again or press Enter to leave this field unchanged.");
            }
        }
    }

    private static string? FindAutomaticSavePath()
    {
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            string candidate = Path.GetFullPath(Path.Combine(directory, "pp.dat"));
            if (seenPaths.Add(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalized = path.Trim();
        if (normalized.Length >= 2 && normalized[0] == '"' && normalized[^1] == '"')
        {
            normalized = normalized[1..^1];
        }

        return Environment.ExpandEnvironmentVariables(normalized);
    }

    private readonly record struct RtonPathStep(bool FromObjectProperty, int Index)
    {
        public static RtonPathStep ObjectProperty(int index) => new(true, index);
        public static RtonPathStep ArrayItem(int index) => new(false, index);
    }

    private sealed class LazyMenuList(int contentCount, Func<int, string> formatter, string finalItem)
        : IReadOnlyList<string>
    {
        public int Count { get; } = checked(contentCount + 1);

        public string this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return index == contentCount ? finalItem : formatter(index);
            }
        }

        public IEnumerator<string> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed record CurrencyDefinition(string Label, string Field);
}
