using System.Numerics;
using ZombiesVsPlants2.SaveEditor.Rton;

namespace ZombiesVsPlants2.SaveEditor.Editor;

internal sealed class ProfileView
{
    public required int Index { get; init; }
    public required RtonObject Container { get; init; }
    public required RtonObject Data { get; init; }

    public string Name => GetString("n") ?? $"Profile {Index + 1}";
    public string? ActivityLabel => GetString("l");
    public int UnlockedPlantCount => GetUnlockedPlantIds().Count;
    public int PliEntryCount => GetArray("pli")?.Items.Count ?? 0;
    public int PlantStatCount => GetArray("plis")?.Items.Count ?? 0;

    public RtonValue? GetValue(string field) => Data.FindValue(field);

    public RtonArray? GetArray(string field)
    {
        RtonValue? value = GetValue(field);
        return value?.Kind == RtonValueKind.Array ? value.AsArray() : null;
    }

    public string? GetString(string field)
    {
        RtonValue? value = GetValue(field);
        return value?.Kind == RtonValueKind.String ? value.AsString() : null;
    }

    public BigInteger? GetInteger(string field)
    {
        RtonValue? value = GetValue(field);
        return value?.Kind is RtonValueKind.SignedInteger or RtonValueKind.UnsignedInteger
            ? value.AsInteger()
            : null;
    }

    public void SetString(string field, string value)
    {
        RtonValue target = GetValue(field) ?? throw new InvalidDataException($"Profile field '{field}' is missing.");
        target.SetString(value);
    }

    public void SetInteger(string field, BigInteger value)
    {
        RtonValue target = GetValue(field) ?? throw new InvalidDataException($"Profile field '{field}' is missing.");
        target.SetInteger(value);
    }

    public IReadOnlyList<PlantStatView> GetPlantStats()
    {
        RtonArray? array = GetArray("plis");
        if (array is null)
        {
            return [];
        }

        List<PlantStatView> result = [];
        for (int index = 0; index < array.Items.Count; index++)
        {
            RtonValue item = array.Items[index];
            if (item.Kind != RtonValueKind.Object)
            {
                continue;
            }

            RtonObject record = item.AsObject();
            RtonValue? plantId = record.FindValue("p");
            if (plantId?.Kind is not (RtonValueKind.SignedInteger or RtonValueKind.UnsignedInteger))
            {
                continue;
            }

            result.Add(new PlantStatView { Index = index, Record = record });
        }

        return result;
    }

    public bool IsPlantUnlocked(BigInteger plantId) => GetUnlockedPlantIds().Contains(plantId);

    public bool UnlockPlant(BigInteger plantId)
    {
        if (plantId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(plantId), "A plant ID cannot be negative.");
        }

        RtonValue ownershipValue = GetValue("p")
            ?? throw new InvalidDataException("Profile field 'p' is missing, so plant ownership cannot be edited.");
        if (ownershipValue.Kind != RtonValueKind.Array)
        {
            throw new InvalidDataException("Profile field 'p' is not an array.");
        }

        RtonArray ownership = ownershipValue.AsArray();
        if (ownership.Items.Any(item => TryGetInteger(item, out BigInteger value) && value == plantId))
        {
            return false;
        }

        RtonValue template = ownership.Items.FirstOrDefault(IsIntegerValue)
            ?? GetPlantStats().FirstOrDefault(record => record.PlantId == plantId)?.Record.FindValue("p")
            ?? throw new InvalidDataException($"No integer template is available for plant ID {plantId}.");
        RtonValue newOwnershipEntry = CloneInteger(template);
        newOwnershipEntry.SetInteger(plantId);
        ownership.Items.Add(newOwnershipEntry);
        return true;
    }

    private HashSet<BigInteger> GetUnlockedPlantIds()
    {
        HashSet<BigInteger> result = [];
        RtonArray? ownership = GetArray("p");
        if (ownership is null)
        {
            return result;
        }

        foreach (RtonValue item in ownership.Items)
        {
            if (TryGetInteger(item, out BigInteger value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static bool IsIntegerValue(RtonValue value) =>
        value.Kind is RtonValueKind.SignedInteger or RtonValueKind.UnsignedInteger;

    private static bool TryGetInteger(RtonValue value, out BigInteger result)
    {
        if (IsIntegerValue(value))
        {
            result = value.AsInteger();
            return true;
        }

        result = default;
        return false;
    }

    private static RtonValue CloneInteger(RtonValue value) => new()
    {
        TypeCode = value.TypeCode,
        Kind = value.Kind,
        Data = value.Kind switch
        {
            RtonValueKind.SignedInteger => (long)value.Data,
            RtonValueKind.UnsignedInteger => (ulong)value.Data,
            _ => throw new InvalidDataException("A plant ownership entry must use an integer RTON type.")
        }
    };
}

internal sealed class PlantStatView
{
    public required int Index { get; init; }
    public required RtonObject Record { get; init; }

    public BigInteger PlantId => GetRequiredInteger("p");
    public bool IsImitater => PlantId == new BigInteger(32);
    public PlantDefinition? Definition =>
        PlantCatalog.TryGet(PlantId, out PlantDefinition? definition) ? definition : null;
    public bool HasCatalogData => Definition is not null;
    public int? MaximumLevel => Definition?.MaximumLevel;
    public bool SupportsMastery => Definition?.SupportsMastery ?? true;
    public int? MaximumMastery => Definition?.MaximumMastery;
    public BigInteger? StoredLevel => GetInteger("l");
    public BigInteger? VisibleLevel
    {
        get
        {
            // The save stores player-visible Level N as l = N - 1; fresh records may keep -1 but still start at Level 1.
            BigInteger? storedLevel = StoredLevel;
            return storedLevel is null ? null : BigInteger.Max(BigInteger.One, storedLevel.Value + BigInteger.One);
        }
    }

    public BigInteger? SeedPackets => GetInteger("x");
    public BigInteger? Mastery => GetInteger("m");

    public BigInteger? GetInteger(string field)
    {
        RtonValue? value = Record.FindValue(field);
        return value?.Kind is RtonValueKind.SignedInteger or RtonValueKind.UnsignedInteger
            ? value.AsInteger()
            : null;
    }

    public void SetInteger(string field, BigInteger value)
    {
        if (IsImitater && field == "l")
        {
            throw new InvalidOperationException("Imitater has fixed Level 1 and does not support level edits.");
        }

        if (field == "m")
        {
            PlantDefinition? definition = Definition;
            if (definition is not null && !definition.SupportsMastery)
            {
                throw new InvalidOperationException($"{definition.Name} does not support mastery progression.");
            }

            int maximumMastery = definition?.MaximumMastery ?? PlantCatalog.DefaultMaximumMastery;
            if (value < 0 || value > maximumMastery)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    $"Mastery must be from 0 to {maximumMastery}.");
            }
        }

        if (field == "x" && (value < 0 || value > PlantCatalog.MaximumSeedPackets))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Seed Packets must be from 0 to {PlantCatalog.MaximumSeedPackets}.");
        }

        RtonValue target = Record.FindValue(field)
            ?? throw new InvalidDataException($"Plant record field '{field}' is missing.");
        target.SetInteger(value);
    }

    public void SetVisibleLevel(BigInteger value)
    {
        if (IsImitater)
        {
            throw new InvalidOperationException("Imitater has fixed Level 1 and does not support level edits.");
        }

        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A player-visible plant level must be at least 1.");
        }

        if (Definition is PlantDefinition definition && value > definition.MaximumLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Level for {definition.Name} must be from 1 to {definition.MaximumLevel}.");
        }

        SetInteger("l", value - BigInteger.One);
    }

    private BigInteger GetRequiredInteger(string field) =>
        GetInteger(field) ?? throw new InvalidDataException($"Plant record field '{field}' is not an integer.");
}

internal static class SaveDataNavigator
{
    public static IReadOnlyList<ProfileView> GetProfiles(RtonDocument document)
    {
        RtonValue? objectsValue = document.Root.FindValue("objects");
        if (objectsValue?.Kind != RtonValueKind.Array)
        {
            return [];
        }

        List<ProfileView> profiles = [];
        RtonArray objects = objectsValue.AsArray();
        for (int index = 0; index < objects.Items.Count; index++)
        {
            RtonValue item = objects.Items[index];
            if (item.Kind != RtonValueKind.Object)
            {
                continue;
            }

            RtonObject container = item.AsObject();
            RtonValue? dataValue = container.FindValue("objdata");
            if (dataValue?.Kind != RtonValueKind.Object)
            {
                continue;
            }

            profiles.Add(new ProfileView
            {
                Index = index,
                Container = container,
                Data = dataValue.AsObject()
            });
        }

        return profiles;
    }

    public static IReadOnlyList<ScalarReference> SearchScalars(
        RtonValue value,
        string rootPath,
        string query,
        int maximumResults = 300)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        List<ScalarReference> results = [];
        SearchRecursive(value, rootPath, query, maximumResults, results);
        return results;
    }

    public static RtonValue WrapRoot(RtonObject root) => new()
    {
        TypeCode = 0x85,
        Kind = RtonValueKind.Object,
        Data = root
    };

    private static void SearchRecursive(
        RtonValue value,
        string path,
        string query,
        int maximumResults,
        List<ScalarReference> results)
    {
        if (results.Count >= maximumResults)
        {
            return;
        }

        if (value.Kind == RtonValueKind.Object)
        {
            foreach (RtonProperty property in value.AsObject().Properties)
            {
                SearchRecursive(property.Value, AppendProperty(path, property.Key.Text), query, maximumResults, results);
                if (results.Count >= maximumResults)
                {
                    return;
                }
            }

            return;
        }

        if (value.Kind == RtonValueKind.Array)
        {
            RtonArray array = value.AsArray();
            for (int index = 0; index < array.Items.Count; index++)
            {
                SearchRecursive(array.Items[index], $"{path}[{index}]", query, maximumResults, results);
                if (results.Count >= maximumResults)
                {
                    return;
                }
            }

            return;
        }

        string display = value.ToDisplayString(240);
        if (path.Contains(query, StringComparison.OrdinalIgnoreCase)
            || display.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new ScalarReference(path, value));
        }
    }

    private static string AppendProperty(string path, string property)
    {
        bool simple = property.Length > 0
            && (char.IsLetter(property[0]) || property[0] == '_')
            && property.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');
        if (simple)
        {
            return $"{path}.{property}";
        }

        string escaped = property.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        return $"{path}['{escaped}']";
    }
}

internal sealed record ScalarReference(string Path, RtonValue Value);
