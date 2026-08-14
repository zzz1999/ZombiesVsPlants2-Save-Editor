using System.Numerics;
using ZombiesVsPlants2.SaveEditor.Editor;
using ZombiesVsPlants2.SaveEditor.Rton;
using ZombiesVsPlants2.SaveEditor.Tui;

namespace ZombiesVsPlants2.SaveEditor.Diagnostics;

internal static class EditorRegressionFixtures
{
    public static void Run()
    {
        VerifyCatalogSnapshot();
        VerifyPlantProgressionLimits();
        VerifyOwnershipEdit();
        VerifyProtectedPlantPaths();
    }

    private static void VerifyCatalogSnapshot()
    {
        Require(PlantCatalog.Count == 213, "The plant catalog must contain the complete verified snapshot.");
        Require(
            PlantCatalog.TryGet(2, out PlantDefinition? peashooter)
            && peashooter is not null
            && peashooter.Name == "Peashooter"
            && peashooter.MaximumLevel == 20
            && peashooter.MaximumMastery == 200,
            "The Peashooter catalog entry is incorrect.");
        Require(
            PlantCatalog.TryGet(32, out PlantDefinition? imitater)
            && imitater is not null
            && imitater.Name == "Imitater"
            && imitater.MaximumLevel == 1
            && !imitater.SupportsMastery,
            "The Imitater catalog entry is incorrect.");
        Require(
            PlantCatalog.TryGet(234, out PlantDefinition? thornWhip)
            && thornWhip?.Name == "Thorn Whip",
            "The newest verified plant name is missing.");
        Require(
            PlantCatalog.TryGet(29, out PlantDefinition? emPeach)
            && emPeach?.Name == "E.M.Peach"
            && PlantCatalog.TryGet(207, out PlantDefinition? buduhBoom)
            && buduhBoom?.Name == "Bud’uh Boom",
            "Plant names must match the verified reference spelling.");
        Require(!PlantCatalog.TryGet(235, out _), "Unverified future plant IDs must retain the unknown fallback.");
    }

    private static void VerifyPlantProgressionLimits()
    {
        PlantStatView peashooter = CreatePlantRecord(2, storedLevel: 0, seedPackets: 0, mastery: 0);
        peashooter.SetVisibleLevel(20);
        Require(peashooter.StoredLevel == 19 && peashooter.VisibleLevel == 20, "Visible Level 20 must be stored as l=19.");
        RequireThrows<ArgumentOutOfRangeException>(() => peashooter.SetVisibleLevel(21));

        peashooter.SetInteger("x", PlantCatalog.MaximumSeedPackets);
        Require(peashooter.SeedPackets == PlantCatalog.MaximumSeedPackets, "The maximum Seed Packets value must be accepted.");
        RequireThrows<ArgumentOutOfRangeException>(
            () => peashooter.SetInteger("x", new BigInteger(PlantCatalog.MaximumSeedPackets) + BigInteger.One));

        PlantStatView powerLily = CreatePlantRecord(38, storedLevel: 0, seedPackets: 0, mastery: 0);
        Require(!powerLily.SupportsMastery, "Power Lily must not expose Mastery progression.");
        RequireThrows<InvalidOperationException>(() => powerLily.SetInteger("m", 1));

        PlantStatView imitater = CreatePlantRecord(32, storedLevel: -1, seedPackets: 0, mastery: 0);
        Require(imitater.VisibleLevel == 1 && !imitater.SupportsMastery, "Imitater must display fixed Level 1 and no Mastery.");
        RequireThrows<InvalidOperationException>(() => imitater.SetVisibleLevel(1));
        RequireThrows<InvalidOperationException>(() => imitater.SetInteger("m", 1));
    }

    private static void VerifyOwnershipEdit()
    {
        RtonArray ownership = new() { DeclaredCapacity = 1 };
        ownership.Items.Add(IntegerValue(2));

        PlantStatView lockedPlant = CreatePlantRecord(13, storedLevel: 4, seedPackets: 27, mastery: 3);
        RtonArray plantStats = new() { DeclaredCapacity = 1 };
        plantStats.Items.Add(ObjectValue(lockedPlant.Record));

        RtonObject data = new();
        data.Properties.Add(Property("p", ArrayValue(ownership)));
        data.Properties.Add(Property("plis", ArrayValue(plantStats)));
        ProfileView profile = new() { Index = 0, Container = new RtonObject(), Data = data };

        Require(!profile.IsPlantUnlocked(13), "The ownership fixture must begin locked.");
        Require(profile.UnlockPlant(13), "Unlocking a locked plant must append its ID.");
        Require(profile.IsPlantUnlocked(13) && ownership.Items.Count == 2, "Unlocking must update only the ownership array.");
        Require(!profile.UnlockPlant(13), "Unlocking the same plant twice must be idempotent.");
        Require(
            lockedPlant.StoredLevel == 4 && lockedPlant.SeedPackets == 27 && lockedPlant.Mastery == 3,
            "Unlocking must not change Level, Seed Packets, or Mastery.");
    }

    private static void VerifyProtectedPlantPaths()
    {
        string prefix = "$.objects[0].objdata";
        string[] protectedPaths =
        [
            $"{prefix}.p[0]",
            $"{prefix}.pli[0].p",
            $"{prefix}.plis[0].p",
            $"{prefix}.plis[0].l",
            $"{prefix}.plis[0].m",
            $"{prefix}.plis[0].x",
            $"{prefix}.tltep[0].p"
        ];
        Require(
            protectedPaths.All(TuiApp.IsProtectedPlantPath),
            "Advanced Search must not bypass plant ownership or progression editors.");
        Require(
            !TuiApp.IsProtectedPlantPath($"{prefix}.m")
            && !TuiApp.IsProtectedPlantPath($"{prefix}.plis[0].other"),
            "Unrelated profile fields must remain available to Advanced Search.");
    }

    private static PlantStatView CreatePlantRecord(int id, int storedLevel, int seedPackets, int mastery)
    {
        RtonObject record = new();
        record.Properties.Add(Property("p", IntegerValue(id)));
        record.Properties.Add(Property("l", IntegerValue(storedLevel)));
        record.Properties.Add(Property("x", IntegerValue(seedPackets)));
        record.Properties.Add(Property("m", IntegerValue(mastery)));
        return new PlantStatView { Index = 0, Record = record };
    }

    private static RtonProperty Property(string key, RtonValue value) => new()
    {
        Key = new RtonStringToken
        {
            TypeCode = 0x81,
            Text = key,
            OriginalText = key
        },
        Value = value
    };

    private static RtonValue IntegerValue(long value) => new()
    {
        TypeCode = value == 0 ? (byte)0x21 : (byte)0x24,
        Kind = RtonValueKind.SignedInteger,
        Data = value
    };

    private static RtonValue ArrayValue(RtonArray value) => new()
    {
        TypeCode = 0x86,
        Kind = RtonValueKind.Array,
        Data = value
    };

    private static RtonValue ObjectValue(RtonObject value) => new()
    {
        TypeCode = 0x85,
        Kind = RtonValueKind.Object,
        Data = value
    };

    private static void RequireThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidDataException($"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
