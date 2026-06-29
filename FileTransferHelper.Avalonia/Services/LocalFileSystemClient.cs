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
            var name = Path.GetFileName(directory);
            if (IsReservedWindowsDeviceName(name))
            {
                LogSkippedLocalPath(directory, "reserverat Windows-namn");
                continue;
            }

            entries.Add(new LocalEntry(name, true, 0, directory));
        }

        foreach (var file in Directory.EnumerateFiles(normalized))
        {
            var name = Path.GetFileName(file);
            if (IsReservedWindowsDeviceName(name))
            {
                LogSkippedLocalPath(file, "reserverat Windows-namn");
                continue;
            }

            try
            {
                var info = new FileInfo(file);
                entries.Add(new LocalEntry(info.Name, false, info.Length, info.FullName));
            }
            catch (Exception exc) when (IsSkippableLocalFileException(exc))
            {
                LogSkippedLocalPath(file, exc.Message);
            }
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
            .Where(directory =>
            {
                var name = Path.GetFileName(directory);
                if (!IsReservedWindowsDeviceName(name))
                {
                    return true;
                }

                LogSkippedLocalPath(directory, "reserverat Windows-namn");
                return false;
            })
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public StorageInfo? GetStorageInfo(string path)
    {
        var normalized = Normalize(path);
        var root = Path.GetPathRoot(normalized);
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var drive = new DriveInfo(root);
        if (!drive.IsReady)
        {
            return null;
        }

        return new StorageInfo(drive.AvailableFreeSpace, drive.TotalSize);
    }

    public DirectoryContentSummary CountDirectoryContent(string path)
    {
        var normalized = Normalize(path);
        var directDirectories = 0;
        var directFiles = 0;
        var subdirectories = 0;
        var nestedFiles = 0;
        var directBytes = 0L;
        var nestedBytes = 0L;

        if (!Directory.Exists(normalized))
        {
            return new DirectoryContentSummary(0, 0, 0, 0);
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(normalized))
            {
                if (IsReservedWindowsDeviceName(Path.GetFileName(directory)))
                {
                    LogSkippedLocalPath(directory, "reserverat Windows-namn");
                    continue;
                }

                directDirectories++;
                CountNestedLocalContent(directory, ref subdirectories, ref nestedFiles, ref nestedBytes);
            }

            foreach (var file in Directory.EnumerateFiles(normalized))
            {
                if (IsReservedWindowsDeviceName(Path.GetFileName(file)))
                {
                    LogSkippedLocalPath(file, "reserverat Windows-namn");
                    continue;
                }

                directFiles++;
                try
                {
                    directBytes += new FileInfo(file).Length;
                }
                catch (Exception exc) when (IsSkippableLocalFileException(exc))
                {
                    LogSkippedLocalPath(file, exc.Message);
                }
            }
        }
        catch
        {
        }

        return new DirectoryContentSummary(
            directDirectories,
            directFiles,
            subdirectories,
            nestedFiles,
            directBytes,
            nestedBytes);
    }

    private static void CountNestedLocalContent(string directory, ref int subdirectories, ref int nestedFiles, ref long nestedBytes)
    {
        subdirectories++;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (IsReservedWindowsDeviceName(Path.GetFileName(file)))
                {
                    LogSkippedLocalPath(file, "reserverat Windows-namn");
                    continue;
                }

                nestedFiles++;
                try
                {
                    nestedBytes += new FileInfo(file).Length;
                }
                catch (Exception exc) when (IsSkippableLocalFileException(exc))
                {
                    LogSkippedLocalPath(file, exc.Message);
                }
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (IsReservedWindowsDeviceName(Path.GetFileName(child)))
                {
                    LogSkippedLocalPath(child, "reserverat Windows-namn");
                    continue;
                }

                CountNestedLocalContent(child, ref subdirectories, ref nestedFiles, ref nestedBytes);
            }
        }
        catch
        {
        }
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

    private static bool IsReservedWindowsDeviceName(string? name)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(name.Trim().TrimEnd('.', ' '));
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
               || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
               || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
               || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
               || IsReservedNumberedDeviceName(stem, "COM")
               || IsReservedNumberedDeviceName(stem, "LPT");
    }

    private static bool IsReservedNumberedDeviceName(string stem, string prefix)
    {
        return stem.Length == 4
               && stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && stem[3] >= '1'
               && stem[3] <= '9';
    }

    private static bool IsSkippableLocalFileException(Exception exc)
    {
        return exc is IOException
               or UnauthorizedAccessException
               or ArgumentException
               or NotSupportedException
               or PathTooLongException;
    }

    private static void LogSkippedLocalPath(string path, string reason)
    {
        LogWriter.Write(AppPaths.TransferLogPath, $"Hoppar över lokalt objekt {path}: {reason}");
    }
}
