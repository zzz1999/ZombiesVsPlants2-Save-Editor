using ZombiesVsPlants2.SaveEditor.Editor;

namespace ZombiesVsPlants2.SaveEditor.Diagnostics;

internal static class SaveRegressionFixtures
{
    public static void Run()
    {
        byte[] minimalSave =
        [
            (byte)'R', (byte)'T', (byte)'O', (byte)'N',
            0x01, 0x00, 0x00, 0x00,
            0xFF,
            (byte)'D', (byte)'O', (byte)'N', (byte)'E'
        ];
        Run(minimalSave);
    }

    public static void Run(byte[] validSave)
    {
        ArgumentNullException.ThrowIfNull(validSave);
        string root = Path.Combine(Path.GetTempPath(), $"ZvpEditorSaveFixtures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string sourcePath = Path.Combine(root, "source.dat");
            File.WriteAllBytes(sourcePath, validSave);

            VerifyUnexpectedExistingTargetIsPreserved(sourcePath, root);
            VerifyAppearingTargetIsPreserved(sourcePath, root);
            VerifyChangedTargetIsPreserved(sourcePath, root);
            VerifyFinalCheckRaceIsRestored(sourcePath, root);
            VerifyChangedSourceIsPreserved(sourcePath, root, validSave);
            VerifySuccessfulNewTarget(sourcePath, root, validSave);
            VerifySuccessfulInPlaceSaveWithoutBackup(sourcePath, root, validSave);
            VerifySuccessfulReplacementAndBackup(sourcePath, root, validSave);

            Require(
                !Directory.EnumerateFiles(root, ".*.temporary-*", SearchOption.TopDirectoryOnly).Any(),
                "Save operations must clean up every sibling temporary file.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyUnexpectedExistingTargetIsPreserved(string sourcePath, string root)
    {
        byte[] externalBytes = [0x01, 0x02, 0x03];
        string targetPath = Path.Combine(root, "unexpected-existing.dat");
        File.WriteAllBytes(targetPath, externalBytes);
        EditorSession session = EditorSession.Load(sourcePath);

        ExpectConflict(() => session.Save(targetPath, createBackup: false));

        Require(
            File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(externalBytes),
            "An unexpected existing Save As target must not be overwritten.");
    }

    private static void VerifyAppearingTargetIsPreserved(string sourcePath, string root)
    {
        byte[] externalBytes = [0x11, 0x22, 0x33];
        string targetPath = Path.Combine(root, "appearing.dat");
        EditorSession session = EditorSession.Load(sourcePath);

        ExpectConflict(() => session.Save(
            targetPath,
            createBackup: false,
            () => File.WriteAllBytes(targetPath, externalBytes)));

        Require(
            File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(externalBytes),
            "A target that appears during staging must not be overwritten.");
    }

    private static void VerifyChangedTargetIsPreserved(string sourcePath, string root)
    {
        byte[] originalTarget = [0x41, 0x42, 0x43];
        byte[] externalBytes = [0x51, 0x52, 0x53];
        string targetPath = Path.Combine(root, "changed-target.dat");
        File.WriteAllBytes(targetPath, originalTarget);
        EditorSession session = EditorSession.Load(sourcePath);

        ExpectConflict(() => session.Save(
            targetPath,
            createBackup: true,
            () => File.WriteAllBytes(targetPath, externalBytes)));

        Require(
            File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(externalBytes),
            "A Save As target changed during staging must not be overwritten.");
        Require(
            !Directory.EnumerateFiles(root, "changed-target.backup-*.dat").Any(),
            "A rejected save must not create a stale backup.");
    }

    private static void VerifyChangedSourceIsPreserved(string sourcePath, string root, byte[] validSave)
    {
        byte[] externalBytes = [0x61, 0x62, 0x63];
        EditorSession session = EditorSession.Load(sourcePath);

        ExpectConflict(() => session.Save(
            sourcePath,
            createBackup: true,
            () => File.WriteAllBytes(sourcePath, externalBytes)));

        Require(
            File.ReadAllBytes(sourcePath).AsSpan().SequenceEqual(externalBytes),
            "An in-place target changed during staging must not be overwritten.");
        File.WriteAllBytes(sourcePath, validSave);
    }

    private static void VerifyFinalCheckRaceIsRestored(string sourcePath, string root)
    {
        byte[] originalTarget = [0x81, 0x82, 0x83];
        byte[] externalBytes = [0x91, 0x92, 0x93];
        string targetPath = Path.Combine(root, "final-gap.dat");
        File.WriteAllBytes(targetPath, originalTarget);
        EditorSession session = EditorSession.Load(sourcePath);

        ExpectConflict(() => session.Save(
            targetPath,
            createBackup: true,
            beforeCommit: null,
            afterTargetCheck: () => File.WriteAllBytes(targetPath, externalBytes)));

        Require(
            File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(externalBytes),
            "A target changed after the final check must be restored after the atomic replacement detects it.");
        Require(
            !Directory.EnumerateFiles(root, "final-gap.backup-*.dat").Any(),
            "Conflict recovery must not leave a misleading successful-save backup.");
        Require(
            !Directory.EnumerateFiles(root, ".final-gap.dat.rollback-*").Any(),
            "Conflict recovery must remove the rolled-back editor version.");
    }

    private static void VerifySuccessfulNewTarget(string sourcePath, string root, byte[] validSave)
    {
        string targetPath = Path.Combine(root, "new-target.dat");
        EditorSession session = EditorSession.Load(sourcePath);
        SaveResult result = session.Save(targetPath, createBackup: false);

        Require(result.BackupPath is null, "A new target must not create a backup.");
        Require(
            File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(validSave),
            "A new target must contain the exact session bytes.");
    }

    private static void VerifySuccessfulReplacementAndBackup(string sourcePath, string root, byte[] validSave)
    {
        byte[] originalTarget = [0x71, 0x72, 0x73];
        string targetPath = Path.Combine(root, "replace-target.dat");
        File.WriteAllBytes(targetPath, originalTarget);
        EditorSession session = EditorSession.Load(sourcePath);
        SaveResult result = session.Save(targetPath, createBackup: true);

        string backupPath = result.BackupPath
            ?? throw new InvalidDataException("Replacing a target must return a backup path.");
        Require(File.Exists(backupPath), "Replacing a target must create its backup atomically.");
        Require(
            File.ReadAllBytes(backupPath).AsSpan().SequenceEqual(originalTarget),
            "The backup must contain the exact replaced target bytes.");
        Require(
            File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(validSave),
            "The replacement target must contain the exact session bytes.");
    }

    private static void VerifySuccessfulInPlaceSaveWithoutBackup(string sourcePath, string root, byte[] validSave)
    {
        EditorSession session = EditorSession.Load(sourcePath);
        SaveResult result = session.Save(sourcePath, createBackup: false);

        Require(result.BackupPath is null, "An in-place save may omit a persistent backup when requested.");
        Require(
            File.ReadAllBytes(sourcePath).AsSpan().SequenceEqual(validSave),
            "An in-place save without a backup must retain the exact session bytes.");
        Require(
            !Directory.EnumerateFiles(root, ".source.dat.displaced-*").Any(),
            "A successful save without a backup must remove its transient recovery copy.");
    }

    private static void ExpectConflict(Action action)
    {
        bool rejected = false;
        try
        {
            action();
        }
        catch (ExternalSaveConflictException)
        {
            rejected = true;
        }

        Require(rejected, "The simulated concurrent file-system change must be rejected.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
