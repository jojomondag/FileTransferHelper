using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileTransferHelper.Services;

public sealed class RemoteTreeCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ConcurrentDictionary<string, List<string>> _directories = new(StringComparer.Ordinal);
    private string? _host;

    public void BindHost(string host)
    {
        _host = host.Trim();
        _directories.Clear();

        var path = CachePathForHost(_host);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<RemoteTreeCacheDocument>(File.ReadAllText(path), JsonOptions);
            if (document?.Directories is null)
            {
                return;
            }

            foreach (var (directoryPath, names) in document.Directories)
            {
                if (string.IsNullOrWhiteSpace(directoryPath) || names is null)
                {
                    continue;
                }

                _directories[PosixPath.Normalize(directoryPath)] = names
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (Exception exc) when (exc is IOException or JsonException or UnauthorizedAccessException)
        {
            LogWriter.Write(AppPaths.TransferLogPath, $"Kunde inte läsa träd-cache för {_host}: {exc.Message}");
        }
    }

    public IReadOnlyList<string>? TryGetSubdirectories(string path)
    {
        if (_host is null)
        {
            return null;
        }

        var normalized = PosixPath.Normalize(path);
        return _directories.TryGetValue(normalized, out var names) ? names : null;
    }

    public void SetSubdirectories(string path, IReadOnlyList<string> names)
    {
        if (_host is null)
        {
            return;
        }

        _directories[PosixPath.Normalize(path)] = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Invalidate(string path)
    {
        if (_host is null)
        {
            return;
        }

        var normalized = PosixPath.Normalize(path);
        _directories.TryRemove(normalized, out _);
    }

    public void Save()
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            var document = new RemoteTreeCacheDocument
            {
                Host = _host,
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Directories = _directories.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal)
            };

            File.WriteAllText(CachePathForHost(_host), JsonSerializer.Serialize(document, JsonOptions));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            LogWriter.Write(AppPaths.TransferLogPath, $"Kunde inte spara träd-cache för {_host}: {exc.Message}");
        }
    }

    public static string CachePathForHost(string host)
    {
        var safeHost = host.Trim().Replace('.', '_').Replace(':', '_');
        return Path.Combine(AppPaths.RepositoryDirectory, $"remote-tree-{safeHost}.json");
    }

    private sealed class RemoteTreeCacheDocument
    {
        public string Host { get; set; } = "";

        public string UpdatedAt { get; set; } = "";

        public Dictionary<string, List<string>> Directories { get; set; } = new(StringComparer.Ordinal);
    }
}
