using FileTransferHelper.Models;

namespace FileTransferHelper.Services;

public sealed class LocalFileSystemClient
{
    public string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.GetFullPath(path.Trim());
    }

    public IReadOnlyList<LocalEntry> ListEntries(string path)
    {
        var normalized = Normalize(path);
        if (!Directory.Exists(normalized))
        {
            return [];
        }

        var entries = new List<LocalEntry>();
        foreach (var directory in Directory.EnumerateDirectories(normalized))
        {
            entries.Add(new LocalEntry(Path.GetFileName(directory), true, 0, directory));
        }

        foreach (var file in Directory.EnumerateFiles(normalized))
        {
            var info = new FileInfo(file);
            entries.Add(new LocalEntry(info.Name, false, info.Length, info.FullName));
        }

        return entries
            .OrderBy(entry => entry.IsDirectory ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> ListSubdirectories(string path)
    {
        var normalized = Normalize(path);
        if (!Directory.Exists(normalized))
        {
            return [];
        }

        return Directory.EnumerateDirectories(normalized)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(Normalize(path));

    public void DeletePath(string path, bool isDirectory)
    {
        path = Normalize(path);
        if (isDirectory)
        {
            Directory.Delete(path, recursive: true);
            return;
        }

        File.Delete(path);
    }
}
