namespace GameDirector.Workbench;

public sealed record FolderRequest(string? Path);

// The local Workbench owns filesystem access. Browser directory-upload controls
// cannot supply an absolute project path and would unnecessarily read game files.
public static class ProjectFolders
{
    public static object Browse(FolderRequest request)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = string.IsNullOrWhiteSpace(request.Path) ? home : request.Path;
        if (!System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择完整的文件夹路径。");
        var folder = new DirectoryInfo(System.IO.Path.GetFullPath(path));
        if (!folder.Exists) throw new ArgumentException("文件夹不存在或无法访问，请返回主目录重新选择。");
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var children = folder.EnumerateDirectories("*", options).Where(d => !d.Name.StartsWith('.')).Take(2001).ToArray();
        var volumes = OperatingSystem.IsWindows() ? DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName) : new[] { System.IO.Path.GetPathRoot(home)! };
        var roots = new[] { home }.Concat(volumes).Distinct().ToArray();
        return new {
            path = folder.FullName, name = folder.Name, parent = folder.Parent?.FullName, home, roots,
            directories = children.Take(2000).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => new { name = d.Name, path = d.FullName }),
            truncated = children.Length > 2000
        };
    }
}
