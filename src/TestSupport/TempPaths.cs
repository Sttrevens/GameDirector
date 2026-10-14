// Linked only into test assemblies. xUnit disposes each test instance even on
// failure; register before fixture setup so failed setup is owned as well.
public class TempPaths : IDisposable
{
    private readonly List<string> paths = new();
    public string TempPath(string prefix, string suffix = "")
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + suffix);
        paths.Add(path); return path; // Some tests require a not-yet-created path.
    }
    public virtual void Dispose()
    {
        var errors = new List<Exception>();
        foreach (var path in paths)
        {
            try { Remove(path); }
            catch (Exception error) { Console.Error.WriteLine("FAIL: temp cleanup: " + path); errors.Add(error); }
        }
        paths.Clear();
        if (errors.Count != 0) throw new AggregateException("Test temp cleanup failed.", errors);
    }
    private static void Remove(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        // Unlink before targets, without ever traversing symlinks/junctions.
        if ((attributes & FileAttributes.Directory) == 0) File.Delete(path);
        else
        {
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                foreach (var entry in Directory.EnumerateFileSystemEntries(path)) Remove(entry);
            Directory.Delete(path);
        }
    }
}
