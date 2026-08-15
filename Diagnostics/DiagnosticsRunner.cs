using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using ZombiesVsPlants2.SaveEditor.Editor;
using ZombiesVsPlants2.SaveEditor.Rton;

namespace ZombiesVsPlants2.SaveEditor.Diagnostics;

internal static class DiagnosticsRunner
{
    public static int Inspect(string path)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(fullPath);
        RtonDocument document = RtonCodec.Decode(bytes);
        IReadOnlyList<ProfileView> profiles = SaveDataNavigator.GetProfiles(document);

        Console.WriteLine("Zombies vs Plants 2 Save Inspector");
        Console.WriteLine($"File: {fullPath}");
        Console.WriteLine($"Size: {bytes.Length:N0} bytes");
        Console.WriteLine($"SHA-256: {Convert.ToHexString(SHA256.HashData(bytes))}");
        Console.WriteLine($"RTON: v{document.Version}, consumed {document.Metadata.BytesConsumed:N0} bytes");
        Console.WriteLine($"Structure: {document.Metadata.ObjectCount:N0} objects, {document.Metadata.ArrayCount:N0} arrays, maximum depth {document.Metadata.MaximumDepth}");
        Console.WriteLine($"Profiles: {profiles.Count}");
        foreach (ProfileView profile in profiles)
        {
            Console.WriteLine(
                $"  #{profile.Index + 1} {profile.Name} | c={Format(profile.GetInteger("c"))} g={Format(profile.GetInteger("g"))} "
                + $"t={Format(profile.GetInteger("t"))} m={Format(profile.GetInteger("m"))} pf={Format(profile.GetInteger("pf"))} "
                + $"| pli={profile.PliEntryCount} plis={profile.PlantStatCount}");
        }

        return 0;
    }

    public static int RoundTrip(string inputPath, string outputPath)
    {
        string fullInputPath = Path.GetFullPath(inputPath);
        string fullOutputPath = Path.GetFullPath(outputPath);
        if (string.Equals(fullInputPath, fullOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--roundtrip requires different input and output files; it never overwrites the source RTON file.");
        }

        byte[] source = File.ReadAllBytes(fullInputPath);
        RtonDocument document = RtonCodec.Decode(source);
        byte[] encoded = RtonCodec.Encode(document);
        _ = RtonCodec.Decode(encoded);
        EditorSession session = EditorSession.Load(fullInputPath);
        SaveResult saveResult = session.Save(fullOutputPath, createBackup: true);
        byte[] savedBytes = File.ReadAllBytes(fullOutputPath);
        Console.WriteLine($"Input: {source.Length:N0} bytes, {Convert.ToHexString(SHA256.HashData(source))}");
        Console.WriteLine($"Output: {savedBytes.Length:N0} bytes, {Convert.ToHexString(SHA256.HashData(savedBytes))}");
        if (saveResult.BackupPath is not null)
        {
            Console.WriteLine($"Previous output backup: {saveResult.BackupPath}");
        }

        bool identical = source.AsSpan().SequenceEqual(encoded)
            && encoded.AsSpan().SequenceEqual(savedBytes);
        Console.WriteLine($"Byte-identical: {identical}");
        return identical ? 0 : 3;
    }

    public static int SelfTest(string path)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] source = File.ReadAllBytes(fullPath);
        string sourceHash = Convert.ToHexString(SHA256.HashData(source));
        Console.WriteLine($"[TEST] {fullPath}");

        RtonDocument document = RtonCodec.Decode(source);
        Pass($"Parsed all {document.Metadata.BytesConsumed:N0} bytes");

        byte[] roundTrip = RtonCodec.Encode(document);
        Require(source.AsSpan().SequenceEqual(roundTrip), "An unmodified round trip must be byte-identical to the source");
        Pass($"Unmodified round-trip SHA-256 is identical: {sourceHash}");

        IReadOnlyList<ProfileView> profiles = SaveDataNavigator.GetProfiles(document);
        bool hasRecognizableProfile = profiles.Count > 0;
        BigInteger editedCoins = default;
        if (hasRecognizableProfile)
        {
            ProfileView first = profiles[0];
            BigInteger originalCoins = first.GetInteger("c")
                ?? throw new InvalidDataException("The test profile is missing field 'c'.");
            editedCoins = originalCoins < int.MaxValue ? originalCoins + 1 : originalCoins - 1;
            first.SetInteger("c", editedCoins);
            string editedName = first.Name + " self-test ✓";
            first.SetString("n", editedName);

            PlantStatView? plant = first.GetPlantStats().FirstOrDefault(record => !record.IsImitater);
            BigInteger? editedLevel = null;
            if (plant?.StoredLevel is BigInteger originalLevel)
            {
                editedLevel = originalLevel == -1 ? BigInteger.Zero : new BigInteger(-1);
                plant.SetInteger("l", editedLevel.Value);
            }

            byte[] editedBytes = RtonCodec.Encode(document);
            RtonDocument editedDocument = RtonCodec.Decode(editedBytes);
            ProfileView editedProfile = SaveDataNavigator.GetProfiles(editedDocument)[0];
            Require(editedProfile.GetInteger("c") == editedCoins, "Edited field 'c' must be readable after decoding");
            Require(editedProfile.Name == editedName, "An edited Unicode profile name must be readable after decoding");
            if (editedLevel is not null)
            {
                Require(editedProfile.GetPlantStats()[0].StoredLevel == editedLevel, "A negative plant level must round-trip through its ZigZag type");
            }

            Pass("Resource, Unicode name, and negative plant-level edits decode successfully");
        }
        else
        {
            Pass("No recognizable save profile was found; generic RTON validation continues");
        }

        byte[] truncated = source[..^1];
        bool truncationRejected = false;
        try
        {
            _ = RtonCodec.Decode(truncated);
        }
        catch (InvalidDataException)
        {
            truncationRejected = true;
        }

        Require(truncationRejected, "A truncated file must be rejected");
        Pass("Truncation detection works");

        byte[] onDisk = File.ReadAllBytes(fullPath);
        Require(onDisk.AsSpan().SequenceEqual(source), "The self-test must not modify the source file");
        Pass("The source file remains unchanged");

        RunCodecBoundaryTests();
        EditorRegressionFixtures.Run();
        Pass("Codec boundaries, plant catalog limits, and ownership edits pass");

        string temporaryRoot = Path.Combine(Path.GetTempPath(), $"ZvpEditorSelfTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            string temporarySave = Path.Combine(temporaryRoot, "pp.dat");
            File.WriteAllBytes(temporarySave, source);
            EditorSession session = EditorSession.Load(temporarySave);
            byte[] externalBytes;
            if (hasRecognizableProfile)
            {
                session.ApplyChange(testDocument =>
                    SaveDataNavigator.GetProfiles(testDocument)[0].SetInteger("c", editedCoins));
                Require(session.IsDirty, "A modified session must be marked as unsaved");
                Require(session.Undo(), "A modification must be undoable");
                Require(!session.IsDirty, "Undoing to the source bytes must clear the unsaved state");
                session.ApplyChange(testDocument =>
                    SaveDataNavigator.GetProfiles(testDocument)[0].SetInteger("c", editedCoins));
            }
            else
            {
                bool changed = session.ApplyChange(_ => { });
                Require(!changed, "A no-op generic RTON edit must report that no bytes changed");
                Require(!session.IsDirty, "A no-op generic RTON edit must not mark the session as unsaved");
                Require(!session.Undo(), "A no-op generic RTON edit must not create an undo entry");
            }

            SaveResult saveResult = session.Save(temporarySave, createBackup: true);
            string backupPath = saveResult.BackupPath
                ?? throw new InvalidDataException("An overwrite save must create a backup");
            Require(File.Exists(backupPath), "An overwrite save must create a backup");
            Require(File.ReadAllBytes(backupPath).AsSpan().SequenceEqual(source), "The backup must be byte-identical to the pre-save file");
            Require(!session.IsDirty, "A successful save must clear the unsaved state");
            if (hasRecognizableProfile)
            {
                ProfileView savedProfile = SaveDataNavigator.GetProfiles(RtonCodec.Decode(File.ReadAllBytes(temporarySave)))[0];
                Require(savedProfile.GetInteger("c") == editedCoins, "The atomically saved file must contain the edited value");
                externalBytes = source;
            }
            else
            {
                Require(
                    File.ReadAllBytes(temporarySave).AsSpan().SequenceEqual(source),
                    "Saving an unmodified profile-free RTON file must preserve its exact bytes");
                externalBytes = [0x01, 0x02, 0x03];
            }

            // Simulate an external writer replacing the file after this session saved it.
            File.WriteAllBytes(temporarySave, externalBytes);
            bool externalChangeRejected = false;
            try
            {
                _ = session.Save(temporarySave, createBackup: true);
            }
            catch (ExternalSaveConflictException)
            {
                externalChangeRejected = true;
            }

            Require(externalChangeRejected, "An externally modified file must not be overwritten silently");
            Require(File.ReadAllBytes(temporarySave).AsSpan().SequenceEqual(externalBytes), "Conflict detection must leave the external version unchanged");
            Pass(hasRecognizableProfile
                ? "Undo, automatic backup, atomic overwrite, and external-change conflict detection work"
                : "Profile-free RTON session loading, byte-exact saving, backup, and conflict detection work");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }

        SaveRegressionFixtures.Run(source);
        Pass("Save race detection, rollback, backup accuracy, and temporary-file cleanup pass");

        Console.WriteLine("[PASS] All self-tests passed");
        return 0;
    }

    public static int FixtureTest()
    {
        RunCodecBoundaryTests();
        EditorRegressionFixtures.Run();
        SaveRegressionFixtures.Run();
        Pass("All synthetic codec, editor, and save fixtures passed");
        return 0;
    }

    private static void RunCodecBoundaryTests()
    {
        // Synthetic wire fixtures cover encodings that ordinary save samples may not contain.
        RtonRegressionFixtures.Run();

        byte[] signalingNan = WrapRootPayload(
            0x81, 0x01, (byte)'f',
            0x22, 0x01, 0x00, 0x80, 0x7F,
            0xFF);
        Require(RoundTripsExactly(signalingNan), "An f32 signaling-NaN bit pattern must be preserved byte-for-byte");

        byte[] earlyArrayEnd = WrapRootPayload(
            0x81, 0x01, (byte)'a',
            0x86, 0xFD, 0x03, 0x01, 0xFE,
            0xFF);
        RtonDocument arrayDocument = RtonCodec.Decode(earlyArrayEnd);
        RtonArray array = arrayDocument.Root.FindValue("a")?.AsArray()
            ?? throw new InvalidDataException("The synthetic array fixture is missing field 'a'");
        Require(array.DeclaredCapacity == 3 && array.Items.Count == 1, "A standard array may terminate before its declared capacity");
        Require(RoundTripsExactly(earlyArrayEnd), "An early-terminated array must preserve its declared capacity and round-trip byte-for-byte");

        byte[] unsignedByte = WrapRootPayload(0x81, 0x01, (byte)'u', 0x0A, 0xFF, 0xFF);
        RtonDocument unsignedDocument = RtonCodec.Decode(unsignedByte);
        RtonValue unsignedValue = unsignedDocument.Root.FindValue("u")
            ?? throw new InvalidDataException("The synthetic unsigned-integer fixture is missing field 'u'");
        unsignedValue.SetInteger(300);
        RtonValue promotedUnsigned = RtonCodec.Decode(RtonCodec.Encode(unsignedDocument)).Root.FindValue("u")
            ?? throw new InvalidDataException("The promoted unsigned-integer fixture is missing field 'u'");
        Require(promotedUnsigned.Kind == RtonValueKind.UnsignedInteger && promotedUnsigned.AsInteger() == 300,
            "A narrow unsigned integer must retain unsigned semantics after promotion");

        byte[] overflowingVarInt = WrapRootPayload(
            0x81, 0x01, (byte)'v',
            0x28, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F,
            0xFF);
        RequireDecodeRejected(overflowingVarInt, "An overflowing 32-bit VarInt must be rejected");

        byte[] nonCanonicalVarInt = WrapRootPayload(
            0x81, 0x01, (byte)'v',
            0x28, 0x80, 0x00,
            0xFF);
        RequireDecodeRejected(nonCanonicalVarInt, "A non-minimal VarInt must be rejected");

        byte[] maximumVarUInt64 = WrapRootPayload(
            0x81, 0x01, (byte)'w',
            0x48,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01,
            0xFF);
        Require(RoundTripsExactly(maximumVarUInt64), "The maximum UInt64 VarInt must round-trip exactly");

        byte[] overflowingVarUInt64 = WrapRootPayload(
            0x81, 0x01, (byte)'w',
            0x48,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02,
            0xFF);
        RequireDecodeRejected(overflowingVarUInt64, "An overflowing 64-bit VarInt must be rejected");

        List<byte> tooDeepPayload = [];
        for (int depth = 0; depth < 128; depth++)
        {
            tooDeepPayload.AddRange([0x81, 0x01, (byte)'d', 0x85]);
        }

        for (int depth = 0; depth <= 128; depth++)
        {
            tooDeepPayload.Add(0xFF);
        }

        RequireDecodeRejected(WrapRootPayload([.. tooDeepPayload]), "Excessive nesting must be rejected before recursion is exhausted");
    }

    private static bool RoundTripsExactly(byte[] bytes)
    {
        byte[] encoded = RtonCodec.Encode(RtonCodec.Decode(bytes));
        return bytes.AsSpan().SequenceEqual(encoded);
    }

    private static void RequireDecodeRejected(byte[] bytes, string message)
    {
        try
        {
            _ = RtonCodec.Decode(bytes);
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidDataException($"[FAIL] {message}");
    }

    private static byte[] WrapRootPayload(params byte[] rootPayload)
    {
        List<byte> bytes =
        [
            (byte)'R', (byte)'T', (byte)'O', (byte)'N',
            0x01, 0x00, 0x00, 0x00
        ];
        bytes.AddRange(rootPayload);
        bytes.AddRange([(byte)'D', (byte)'O', (byte)'N', (byte)'E']);
        return [.. bytes];
    }

    private static void Pass(string message) => Console.WriteLine($"[PASS] {message}");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException($"[FAIL] {message}");
        }
    }

    private static string Format(BigInteger? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "—";
}
