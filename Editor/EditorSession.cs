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
            _ = RtonCodec.Decode(after);
            if (after.AsSpan().SequenceEqual(before))
            {
                _currentBytes = after;
                return false;
            }

            _undoHistory.Add(before);
            if (_undoHistory.Count > MaximumUndoEntries)
            {
                _undoHistory.RemoveAt(0);
            }

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
        string fullPath = System.IO.Path.GetFullPath(targetPath);
        string? directory = System.IO.Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("Unable to determine the output directory.");
        }

        if (string.Equals(fullPath, Path, StringComparison.OrdinalIgnoreCase))
        {
            EnsureCurrentFileWasNotChangedExternally(fullPath);
        }

        Directory.CreateDirectory(directory);
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

            if (File.Exists(fullPath) && createBackup)
            {
                backupPath = CreateAvailableBackupPath(fullPath);
                File.Copy(fullPath, backupPath, overwrite: false);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
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

    private void EnsureCurrentFileWasNotChangedExternally(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            throw new ExternalSaveConflictException(
                "The original save was deleted or moved. Reload it or save to a new file instead.");
        }

        // Compare content rather than timestamps so external replacements cannot be overwritten silently.
        byte[] diskBytes = File.ReadAllBytes(fullPath);
        if (!diskBytes.AsSpan().SequenceEqual(_savedBytes))
        {
            string diskHash = Convert.ToHexString(SHA256.HashData(diskBytes));
            throw new ExternalSaveConflictException(
                $"The save changed on disk after it was opened (current SHA-256: {diskHash}). "
                + "Reload the external version or save to a new file instead.");
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
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to allocate a unique backup file name.");
    }
}

internal sealed record SaveResult(string Path, string? BackupPath, int ByteLength, string Sha256);

internal sealed class ExternalSaveConflictException(string message) : IOException(message);
