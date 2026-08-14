using System.Security.Cryptography;
using ZombiesVsPlants2.SaveEditor.Rton;

namespace ZombiesVsPlants2.SaveEditor.Editor;

internal sealed class EditorSession
{
    private const int MaximumUndoEntries = 12;
    private readonly List<byte[]> _undoHistory = [];
    private byte[] _currentBytes;
    private byte[] _savedBytes;

    private EditorSession(string path, RtonDocument document, byte[] bytes)
    {
        Path = path;
        Document = document;
        _currentBytes = bytes;
        _savedBytes = bytes.ToArray();
    }

    public string Path { get; private set; }
    public RtonDocument Document { get; private set; }
    public bool IsDirty => !_currentBytes.AsSpan().SequenceEqual(_savedBytes);
    public bool CanUndo => _undoHistory.Count > 0;
    public int CurrentByteLength => _currentBytes.Length;
    public string CurrentSha256 => Convert.ToHexString(SHA256.HashData(_currentBytes));

    public static EditorSession Load(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(fullPath);
        RtonDocument document = RtonCodec.Decode(bytes);
        return new EditorSession(fullPath, document, bytes);
    }

    public bool ApplyChange(Action<RtonDocument> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        byte[] before = _currentBytes;
        try
        {
            change(Document);
            byte[] after = RtonCodec.Encode(Document);
            RtonDocument validatedDocument = RtonCodec.Decode(after);
            if (after.AsSpan().SequenceEqual(before))
            {
                Document = validatedDocument;
                _currentBytes = after;
                return false;
            }

            _undoHistory.Add(before);
            if (_undoHistory.Count > MaximumUndoEntries)
            {
                _undoHistory.RemoveAt(0);
            }

            Document = validatedDocument;
            _currentBytes = after;
            return true;
        }
        catch
        {
            Document = RtonCodec.Decode(before);
            _currentBytes = before;
            throw;
        }
    }

    public bool Undo()
    {
        if (_undoHistory.Count == 0)
        {
            return false;
        }

        int index = _undoHistory.Count - 1;
        byte[] bytes = _undoHistory[index];
        _undoHistory.RemoveAt(index);
        Document = RtonCodec.Decode(bytes);
        _currentBytes = bytes;
        return true;
    }

    public SaveResult Save(string targetPath, bool createBackup)
    {
        return Save(targetPath, createBackup, beforeCommit: null);
    }

    internal SaveResult Save(
        string targetPath,
        bool createBackup,
        Action? beforeCommit,
        Action? afterTargetCheck = null)
    {
        string fullPath = System.IO.Path.GetFullPath(targetPath);
        if (Directory.Exists(fullPath))
        {
            throw new ArgumentException("The output path must include a file name, not a directory.", nameof(targetPath));
        }

        string? directory = System.IO.Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The output path must include a file name.", nameof(targetPath));
        }

        Directory.CreateDirectory(directory);
        byte[]? expectedTargetBytes = CaptureTarget(fullPath);
        bool isCurrentPath = string.Equals(fullPath, Path, StringComparison.OrdinalIgnoreCase);
        if (isCurrentPath)
        {
            EnsureExpectedTarget(expectedTargetBytes, _savedBytes, isCurrentPath: true);
        }
        else if (expectedTargetBytes is not null && !createBackup)
        {
            throw new ExternalSaveConflictException(
                "The output file now exists. Confirm the overwrite again so its current contents can be backed up.");
        }

        _ = RtonCodec.Decode(_currentBytes);

        string temporaryPath = System.IO.Path.Combine(
            directory,
            $".{System.IO.Path.GetFileName(fullPath)}.temporary-{Guid.NewGuid():N}");
        string? backupPath = null;

        // Validate a durable sibling temporary file before replacing the destination.
        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(_currentBytes);
                stream.Flush(flushToDisk: true);
            }

            byte[] verificationBytes = File.ReadAllBytes(temporaryPath);
            _ = RtonCodec.Decode(verificationBytes);
            if (!verificationBytes.AsSpan().SequenceEqual(_currentBytes))
            {
                throw new IOException("Temporary-file verification failed because the written bytes differ from memory.");
            }

            beforeCommit?.Invoke();
            if (expectedTargetBytes is null)
            {
                CommitNewTarget(temporaryPath, fullPath);
            }
            else
            {
                EnsureExpectedTarget(CaptureTarget(fullPath), expectedTargetBytes, isCurrentPath);
                afterTargetCheck?.Invoke();

                string displacedPath = createBackup
                    ? CreateAvailableBackupPath(fullPath)
                    : CreateRecoveryPath(fullPath, "displaced");
                try
                {
                    // The atomic backup lets us verify and restore a write that lands after the final target check.
                    File.Replace(temporaryPath, fullPath, displacedPath);
                }
                catch (FileNotFoundException exception)
                {
                    if (createBackup)
                    {
                        DeleteEmptyReservation(displacedPath);
                    }

                    throw new ExternalSaveConflictException(
                        "The output file was deleted or moved before the save could be committed.",
                        exception);
                }
                catch (Exception exception) when (
                    createBackup && (exception is IOException or UnauthorizedAccessException))
                {
                    DeleteEmptyReservation(displacedPath);
                    throw;
                }

                byte[] displacedBytes = File.ReadAllBytes(displacedPath);
                if (!displacedBytes.AsSpan().SequenceEqual(expectedTargetBytes))
                {
                    string? recoveryPath = RestoreDisplacedTarget(
                        fullPath,
                        displacedPath,
                        displacedBytes,
                        _currentBytes);
                    string recoveryMessage = recoveryPath is null
                        ? string.Empty
                        : $" A later version was also preserved at {recoveryPath}.";
                    throw new ExternalSaveConflictException(
                        "The output file changed during the final commit. Its displaced contents were restored."
                        + recoveryMessage);
                }

                if (createBackup)
                {
                    backupPath = displacedPath;
                }
                else
                {
                    File.Delete(displacedPath);
                }
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        Path = fullPath;
        _savedBytes = _currentBytes.ToArray();
        return new SaveResult(fullPath, backupPath, _currentBytes.Length, CurrentSha256);
    }

    public byte[] GetCurrentBytes() => _currentBytes.ToArray();

    private static byte[]? CaptureTarget(string fullPath)
    {
        try
        {
            return File.ReadAllBytes(fullPath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void EnsureExpectedTarget(
        byte[]? actualBytes,
        byte[] expectedBytes,
        bool isCurrentPath)
    {
        if (actualBytes is null)
        {
            throw new ExternalSaveConflictException(
                isCurrentPath
                    ? "The original save was deleted or moved. Reload it or save to a new file instead."
                    : "The output file was deleted or moved before the save could be committed.");
        }

        if (!actualBytes.AsSpan().SequenceEqual(expectedBytes))
        {
            string diskHash = Convert.ToHexString(SHA256.HashData(actualBytes));
            throw new ExternalSaveConflictException(
                isCurrentPath
                    ? $"The save changed on disk after it was opened (current SHA-256: {diskHash}). "
                        + "Reload the external version or save to a new file instead."
                    : $"The output file changed while the save was being prepared (current SHA-256: {diskHash}). "
                        + "Confirm the overwrite again to back up the current version.");
        }
    }

    private static void CommitNewTarget(string temporaryPath, string fullPath)
    {
        try
        {
            // The no-overwrite move is the final existence check and the commit in one file-system operation.
            File.Move(temporaryPath, fullPath, overwrite: false);
        }
        catch (IOException exception) when (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw new ExternalSaveConflictException(
                "The output path appeared while the save was being prepared and was not overwritten.",
                exception);
        }
    }

    private static string? RestoreDisplacedTarget(
        string fullPath,
        string displacedPath,
        byte[] displacedBytes,
        byte[] savedBytes)
    {
        string rollbackPath = CreateRecoveryPath(fullPath, "rollback");
        try
        {
            File.Replace(displacedPath, fullPath, rollbackPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ExternalSaveConflictException(
                $"The output file changed during the final commit. Its displaced contents remain at {displacedPath}.",
                exception);
        }

        byte[]? restoredBytes = CaptureTarget(fullPath);
        if (restoredBytes is null || !restoredBytes.AsSpan().SequenceEqual(displacedBytes))
        {
            throw new ExternalSaveConflictException(
                $"The output file changed again during conflict recovery. A displaced version remains at {rollbackPath}.");
        }

        byte[] rollbackBytes = File.ReadAllBytes(rollbackPath);
        if (rollbackBytes.AsSpan().SequenceEqual(savedBytes))
        {
            File.Delete(rollbackPath);
            return null;
        }

        return rollbackPath;
    }

    private static string CreateRecoveryPath(string targetPath, string purpose)
    {
        string directory = System.IO.Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("Unable to determine the recovery directory.");
        string fileName = System.IO.Path.GetFileName(targetPath);
        return System.IO.Path.Combine(directory, $".{fileName}.{purpose}-{Guid.NewGuid():N}");
    }

    private static void DeleteEmptyReservation(string path)
    {
        try
        {
            if (new FileInfo(path).Length == 0)
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string CreateAvailableBackupPath(string targetPath)
    {
        string directory = System.IO.Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("Unable to determine the backup directory.");
        string name = System.IO.Path.GetFileNameWithoutExtension(targetPath);
        string extension = System.IO.Path.GetExtension(targetPath);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

        for (int suffix = 0; suffix < 1000; suffix++)
        {
            string suffixText = suffix == 0 ? string.Empty : $"-{suffix}";
            string candidate = System.IO.Path.Combine(directory, $"{name}.backup-{stamp}{suffixText}{extension}");
            try
            {
                using FileStream reservation = new(
                    candidate,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Another save owns this backup name.
            }
        }

        throw new IOException("Unable to allocate a unique backup file name.");
    }
}

internal sealed record SaveResult(string Path, string? BackupPath, int ByteLength, string Sha256);

internal sealed class ExternalSaveConflictException : IOException
{
    public ExternalSaveConflictException(string message)
        : base(message)
    {
    }

    public ExternalSaveConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
