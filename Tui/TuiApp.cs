using System.Globalization;
using System.Numerics;
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
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("The interactive TUI requires terminal input. Use --inspect or --self-test for non-interactive operation.");
            return 2;
        }

        // With no explicit path, prefer a nearby pp.dat for double-click workflows.
        string? path = initialPath is null ? FindAutomaticSavePath() : NormalizePath(initialPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            ConsoleUi.Clear();
            ConsoleUi.WriteTitle("Zombies vs Plants 2 Save Editor");
            path = ConsoleUi.PromptOptional("Enter the path to pp.dat, or drag the file into this window and press Enter");
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
            or NotSupportedException)
        {
            ConsoleUi.Error(exception.Message);
            return 1;
        }
    }

    private int RunLoop()
    {
        while (true)
        {
            IReadOnlyList<ProfileView> profiles = SaveDataNavigator.GetProfiles(_session.Document);
            List<string> items = profiles.Select(FormatProfileMenuItem).ToList();
            int openIndex = items.Count;
            items.Add("[Open] Load another pp.dat file");
            int saveIndex = items.Count;
            items.Add("[Save] Overwrite the current file and create a timestamped backup");
            int saveAsIndex = items.Count;
            items.Add("[Save As] Write to a new pp.dat file");
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
                $"Structure: RTON v{_session.Document.Version} | {profiles.Count} profiles | {_session.Document.Metadata.ObjectCount:N0} objects"
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
            items.Add("Browse and edit a plant record");
            int onePlantIndex = items.Count;
            items.Add("Edit a record by plant ID");
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
            ConsoleUi.Notice("This profile has no plis plant records.");
            return;
        }

        ConsoleUi.Clear();
        ConsoleUi.WriteTitle($"Bulk Edit Plant Records: {profile.Name}");
        Console.WriteLine($"This will apply to {records.Count} records. Values are raw save fields; level l may be zero-based in some versions.");
        Console.WriteLine("Press Enter without typing to leave each field unchanged.");
        BigInteger? level = PromptIntegerInRange("Raw level value (l)", null, -1, int.MaxValue, pauseOnError: false);
        BigInteger? mastery = PromptSafeInteger("Mastery value (m)", null, pauseOnError: false);
        BigInteger? experience = PromptSafeInteger("Experience/shard value (x)", null, pauseOnError: false);
        if (level is null && mastery is null && experience is null)
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
                    if (level is not null && record.GetInteger("l") is not null)
                    {
                        record.SetInteger("l", level.Value);
                    }

                    if (mastery is not null && record.GetInteger("m") is not null)
                    {
                        record.SetInteger("m", mastery.Value);
                    }

                    if (experience is not null && record.GetInteger("x") is not null)
                    {
                        record.SetInteger("x", experience.Value);
                    }
                }
            },
            $"Updated {records.Count} plant records.");
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
                ConsoleUi.Notice("This profile has no plis plant records.");
                return;
            }

            List<string> items = records.Select(record =>
                $"ID {record.PlantId,-6} | Level {record.StoredLevel?.ToString() ?? "—",-6} | "
                + $"Mastery {record.Mastery?.ToString() ?? "—",-8} | Experience/Shards {record.Experience?.ToString() ?? "—"}").ToList();
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
            PlantStatView record = records[selected];
            EditPlantRecord(objectIndex, record.Index, record.PlantId);
        }
    }

    private void EditOnePlant(int objectIndex)
    {
        ProfileView profile = GetProfile(objectIndex);
        string? input = ConsoleUi.PromptOptional("Enter the plant ID (p)");
        if (input is null)
        {
            return;
        }

        if (!BigInteger.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger plantId)
            || plantId < 0)
        {
            ConsoleUi.Error("The plant ID must be a non-negative integer.");
            return;
        }

        IReadOnlyList<PlantStatView> matches = profile.GetPlantStats().Where(record => record.PlantId == plantId).ToList();
        if (matches.Count == 0)
        {
            ConsoleUi.Notice($"Plant ID {plantId} was not found in this profile.");
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
                $"Found {matches.Count} records with the same ID",
                matches.Select(record => $"Record index {record.Index} | l={record.StoredLevel} | m={record.Mastery} | x={record.Experience}").ToList());
            if (selected < 0)
            {
                return;
            }

            selectedRecord = matches[selected];
        }

        EditPlantRecord(objectIndex, selectedRecord.Index, plantId);
    }

    private void EditPlantRecord(int objectIndex, int recordIndex, BigInteger plantId)
    {
        int selection = 0;
        while (true)
        {
            PlantStatView record = GetProfile(objectIndex).GetPlantStats().First(item => item.Index == recordIndex);
            IReadOnlyList<string> items =
            [
                $"Edit raw level value (l), current: {record.StoredLevel}",
                $"Edit mastery value (m), current: {record.Mastery}",
                $"Edit experience/shard value (x), current: {record.Experience}",
                "Back"
            ];
            int selected = ConsoleUi.Select(
                $"Plant ID {plantId} | Record Index {recordIndex}",
                items,
                ["Values are shown as stored; no level offset is applied."],
                initialSelection: selection);
            if (selected < 0 || selected == 3)
            {
                return;
            }

            selection = selected;
            string field = selected switch { 0 => "l", 1 => "m", _ => "x" };
            string label = selected switch { 0 => "raw level value", 1 => "mastery value", _ => "experience/shard value" };
            string current = record.GetInteger(field)?.ToString(CultureInfo.InvariantCulture) ?? "—";
            BigInteger? value = selected == 0
                ? PromptIntegerInRange($"Enter the new {label}", current, -1, int.MaxValue)
                : PromptSafeInteger($"Enter the new {label}", current);
            if (value is null)
            {
                continue;
            }

            ApplyChange(
                document => GetProfile(document, objectIndex).GetPlantStats()
                    .First(item => item.Index == recordIndex)
                    .SetInteger(field, value.Value),
                $"Plant ID {plantId}: {label} was set to {value.Value:N0}.");
        }
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
            $"{(result.Value.IsEditable ? "[Editable]" : "[Read-only]")} {result.Path} = {result.Value.ToDisplayString(70)}").ToList();
        int selected = ConsoleUi.Select(
            title,
            items,
            [results.Count == 300 ? "Showing the first 300 results. Use a more specific search term to narrow the list." : $"Found {results.Count} results."]);
        if (selected < 0)
        {
            return;
        }

        ScalarReference scalar = results[selected];
        if (!scalar.Value.IsEditable)
        {
            ConsoleUi.Notice($"{scalar.Path} uses special RTON type 0x{scalar.Value.TypeCode:X2}. It is read-only to protect the file structure.");
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
            or NotSupportedException)
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

        string? path = NormalizePath(ConsoleUi.PromptOptional("Enter another pp.dat path, or drag the file into this window and press Enter"));
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
            or NotSupportedException)
        {
            ConsoleUi.Error(exception.Message);
        }
    }

    private void SaveAs()
    {
        string directory = Path.GetDirectoryName(_session.Path) ?? Environment.CurrentDirectory;
        string suggested = Path.Combine(directory, "pp.edited.dat");
        string? path = ConsoleUi.PromptOptional("Enter the Save As path", suggested);
        path = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
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
            or NotSupportedException)
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
            or NotSupportedException)
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
            $"Name: {profile.Name} | Label: {profile.LoginLabel ?? "—"}",
            resources,
            $"Plants: {profile.PliEntryCount} pli entries | {profile.PlantStatCount} plis level/mastery records"
        ];
    }

    private static string FormatProfileMenuItem(ProfileView profile)
    {
        string coins = profile.GetInteger("c")?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        string gems = profile.GetInteger("g")?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        return $"#{profile.Index + 1,-2} {ConsoleUi.Truncate(profile.Name, 34),-34} | Coins {coins} | Gems {gems} | Plants {profile.PlantStatCount}";
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
        string? input = ConsoleUi.PromptOptional(
            prompt + $" (allowed: {minimum.ToString("N0", CultureInfo.InvariantCulture)} to {maximum.ToString("N0", CultureInfo.InvariantCulture)})",
            current);
        if (input is null)
        {
            return null;
        }

        if (!BigInteger.TryParse(input.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger value)
            || value < minimum
            || value > maximum)
        {
            if (pauseOnError)
            {
                ConsoleUi.Error($"Enter an integer from {minimum:N0} to {maximum:N0}.");
            }
            else
            {
                Console.WriteLine("Invalid input; this field will remain unchanged.");
            }

            return null;
        }

        return value;
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

    private sealed record CurrencyDefinition(string Label, string Field);
}
