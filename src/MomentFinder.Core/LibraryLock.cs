namespace MomentFinder;

/// <summary>
/// One program at a time works on a library: two would analyze the same photos twice, write the search data over
/// each other and back the same folders up twice. The lock is a file in the data folder held open while the program
/// runs; the system lets it go when the program ends, also after a crash.
/// </summary>
public sealed class LibraryLock : IDisposable
{
    private readonly FileStream _file;

    private LibraryLock(FileStream file) => _file = file;

    /// <summary>The lock of the library in this data folder, or null when another program has it.</summary>
    public static LibraryLock? TryAcquire(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        try
        {
            var file = new FileStream(Path.Combine(dataDir, "library.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            file.SetLength(0);
            using (var w = new StreamWriter(file, leaveOpen: true)) w.Write($"{Environment.MachineName} {Environment.ProcessId}");
            file.Flush();
            return new LibraryLock(file);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _file.Dispose();
}
