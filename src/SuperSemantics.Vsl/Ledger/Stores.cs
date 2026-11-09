using System.Text;

namespace SuperSemantics.Vsl;

/// <summary>Where a <see cref="VerbaLedger"/> keeps its entries.</summary>
public interface ILedgerStore
{
    /// <summary>Seals <paramref name="entry"/> at the end of the chain and returns the sealed entry.</summary>
    LedgerEntry Append(LedgerEntry entry);

    /// <summary>Every entry, in chain order.</summary>
    IEnumerable<LedgerEntry> AllEntries();

    /// <summary>The entries of one identity, in chain order.</summary>
    IEnumerable<LedgerEntry> EntriesForIdentity(string identityKey);

    /// <summary>The last entry, or null for an empty chain.</summary>
    LedgerEntry? LastEntry();
}

/// <summary>An in-memory store, for tests and short-lived processes.</summary>
public sealed class InMemoryLedgerStore : ILedgerStore
{
    private readonly List<LedgerEntry> _entries = [];
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public LedgerEntry Append(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            var last = _entries.Count > 0 ? _entries[^1] : null;
            var sealedEntry = LedgerEntry.Seal(entry, last is null ? 0 : last.Sequence + 1, last?.EntryHash ?? LedgerWire.GenesisHash);
            _entries.Add(sealedEntry);
            return sealedEntry;
        }
    }

    /// <inheritdoc />
    public IEnumerable<LedgerEntry> AllEntries()
    {
        lock (_lock)
        {
            return _entries.ToArray();
        }
    }

    /// <inheritdoc />
    public IEnumerable<LedgerEntry> EntriesForIdentity(string identityKey) =>
        AllEntries().Where(e => e.IdentityKey == identityKey);

    /// <inheritdoc />
    public LedgerEntry? LastEntry()
    {
        lock (_lock)
        {
            return _entries.Count > 0 ? _entries[^1] : null;
        }
    }
}

/// <summary>
/// One JSON object per line, appended only (never rewritten). The file format is vsl-core's
/// <c>JsonlLedgerStore</c> format, byte for byte, so a ledger written by a .NET agent can be read and
/// verified by the Python package, and the other way round.
/// </summary>
/// <remarks>
/// <para>Appending holds a lock on a sibling <c>.lock</c> file for the whole
/// read-last-entry-then-write step, so two stores pointed at the same file (in one process or several,
/// .NET or Python) cannot race and corrupt the chain. On Windows the lock is a byte-range lock on the
/// lock file's first byte, the lock vsl-core takes with <c>msvcrt.locking</c>; elsewhere it is an
/// exclusive <c>flock</c>, the lock vsl-core takes with <c>fcntl.flock</c>.</para>
/// <para>Pass <c>fsync: true</c> in production, so a write survives a crash right after it returns.</para>
/// </remarks>
public sealed class JsonlLedgerStore : ILedgerStore
{
    private const int TailChunkBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly Lock _lock = new();

    /// <summary>Opens (or creates) the ledger file.</summary>
    public JsonlLedgerStore(string path, bool fsync = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = System.IO.Path.GetFullPath(path);
        Fsync = fsync;
        if (!File.Exists(Path))
        {
            using (File.Open(Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) { }
        }
    }

    /// <summary>The ledger file.</summary>
    public string Path { get; }

    /// <summary>Whether each append is flushed to disk before returning.</summary>
    public bool Fsync { get; }

    /// <inheritdoc />
    public IEnumerable<LedgerEntry> AllEntries()
    {
        string text;
        lock (_lock)
        {
            text = ReadAllShared();
        }

        var entries = new List<LedgerEntry>();
        foreach (var raw in text.Split(['\n', '\r']))
        {
            var line = raw.Trim();
            if (line.Length > 0)
            {
                entries.Add(LedgerEntry.FromJson(line));
            }
        }

        return entries;
    }

    /// <inheritdoc />
    public IEnumerable<LedgerEntry> EntriesForIdentity(string identityKey) =>
        AllEntries().Where(e => e.IdentityKey == identityKey);

    /// <summary>Reads backwards from the end of the file, so the cost depends on the last line's length,
    /// not the file's.</summary>
    public LedgerEntry? LastEntry()
    {
        string? line;
        lock (_lock)
        {
            line = LastNonBlankLine();
        }

        return line is null ? null : LedgerEntry.FromJson(line);
    }

    /// <inheritdoc />
    public LedgerEntry Append(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            using var crossProcess = CrossProcessFileLock.Acquire(Path + ".lock");
            var last = LastEntry();
            var sealedEntry = LedgerEntry.Seal(entry, last is null ? 0 : last.Sequence + 1, last?.EntryHash ?? LedgerWire.GenesisHash);
            var line = PythonJson.SerializeDefault(sealedEntry.ToDictionary()) + "\n";
            using (var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                var bytes = Utf8NoBom.GetBytes(line);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: Fsync);
            }

            return sealedEntry;
        }
    }

    private string ReadAllShared()
    {
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private string? LastNonBlankLine()
    {
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var position = stream.Length;
        var tail = Array.Empty<byte>();
        while (position > 0)
        {
            var step = (int)Math.Min(TailChunkBytes, position);
            position -= step;
            stream.Seek(position, SeekOrigin.Begin);
            var chunk = new byte[step];
            stream.ReadExactly(chunk);
            tail = [.. chunk, .. tail];

            var end = tail.Length;
            while (end > 0 && IsWhitespace(tail[end - 1])) end--;
            if (end == 0) continue;

            var start = end - 1;
            while (start >= 0 && tail[start] != (byte)'\n' && tail[start] != (byte)'\r') start--;
            start++;
            if (start > 0 || position == 0)
            {
                return Utf8NoBom.GetString(tail, start, end - start).Trim();
            }
        }

        return null;
    }

    private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0b or 0x0c;
}

/// <summary>An OS lock on a sibling lock file, compatible with the lock vsl-core takes.</summary>
internal sealed class CrossProcessFileLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly bool _byteRange;

    private CrossProcessFileLock(FileStream stream, bool byteRange)
    {
        _stream = stream;
        _byteRange = byteRange;
    }

    public static CrossProcessFileLock Acquire(string lockPath, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var delay = 1;
        while (true)
        {
            FileStream? stream = null;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // Byte-range lock on byte 0, the same lock msvcrt.locking takes; the byte must exist.
                    stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length == 0)
                    {
                        stream.WriteByte(0);
                        stream.Flush();
                    }

                    stream.Lock(0, 1);
                    return new CrossProcessFileLock(stream, byteRange: true);
                }

                // On Unix .NET takes an exclusive flock for FileShare.None, the lock fcntl.flock takes.
                stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new CrossProcessFileLock(stream, byteRange: false);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                stream?.Dispose();
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 50);
            }
        }
    }

    public void Dispose()
    {
        if (_byteRange && OperatingSystem.IsWindows())
        {
            try
            {
                _stream.Unlock(0, 1);
            }
            catch (IOException)
            {
                // Closing the handle below releases the lock anyway.
            }
        }

        _stream.Dispose();
    }
}
