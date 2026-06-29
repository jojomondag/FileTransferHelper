using FileTransferHelper.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace FileTransferHelper.Services;

public sealed class SftpTransferClient : IDisposable
{
    private readonly object _syncRoot = new();
    private SshClient? _sshClient;
    private SftpClient? _sftpClient;
    private ConnectionParams? _connectionParams;

    public bool IsConnected => _sftpClient?.IsConnected == true;

    public string Connect(string host, string username, string? password, string? keyPath)
    {
        lock (_syncRoot)
        {
            _connectionParams = new ConnectionParams(host, username, password, keyPath);
            ConnectCurrent();
            return Normalize(".");
        }
    }

    public void Close()
    {
        lock (_syncRoot)
        {
            try
            {
                _sftpClient?.Dispose();
                _sshClient?.Dispose();
            }
            finally
            {
                _sftpClient = null;
                _sshClient = null;
            }
        }
    }

    public string Normalize(string path)
    {
        lock (_syncRoot)
        {
            RequireSftp();
            path = string.IsNullOrWhiteSpace(path) ? "." : PosixPath.NormalizeSlashes(path.Trim());
            if (path == ".")
            {
                return _sftpClient!.WorkingDirectory;
            }

            return path.StartsWith("/", StringComparison.Ordinal)
                ? PosixPath.Normalize(path)
                : PosixPath.Normalize(PosixPath.Join(_sftpClient!.WorkingDirectory, path));
        }
    }

    public IReadOnlyList<RemoteEntry> ListEntries(string path)
    {
        lock (_syncRoot)
        {
            RequireSftp();
            var entries = new List<RemoteEntry>();
            IReadOnlyList<Renci.SshNet.Sftp.ISftpFile> remoteItems;
            try
            {
                remoteItems = _sftpClient!.ListDirectory(path)
                    .Where(item => item.Name is not "." and not "..")
                    .ToList();
            }
            catch (Exception exc)
            {
                LogException($"Kunde inte lista fjärrmapp: {path}", exc);
                throw;
            }

            foreach (var item in remoteItems)
            {
                entries.Add(new RemoteEntry(item.Name, item.IsDirectory, item.Length, PosixPath.Join(path, item.Name)));
            }

            var dirCount = entries.Count(entry => entry.IsDirectory);
            var fileCount = entries.Count - dirCount;
            Log($"Listade fjärrmapp {path}: {dirCount} mappar, {fileCount} filer");
            return entries
                .OrderBy(item => !item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyList<string> ListSubdirectories(string path)
    {
        lock (_syncRoot)
        {
            RequireSftp();
            try
            {
                return _sftpClient!.ListDirectory(path)
                    .Where(item => item.IsDirectory && item.Name is not "." and not "..")
                    .Select(item => item.Name)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception exc)
            {
                LogException($"Kunde inte lista undermappar: {path}", exc);
                throw;
            }
        }
    }

    public StorageInfo? GetStorageInfo(string path)
    {
        lock (_syncRoot)
        {
            RequireSftp();
            RequireSsh();
            var normalized = Normalize(path);
            var command = _sshClient!.RunCommand($"df -Pk -- {ShellQuote(normalized)}");
            if (command.ExitStatus != 0)
            {
                Log($"Kunde inte läsa fjärrutrymme för {normalized}: {command.Error.Trim()}");
                return null;
            }

            var line = command.Result
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4
                || !long.TryParse(parts[1], out var totalBlocks)
                || !long.TryParse(parts[3], out var availableBlocks))
            {
                Log($"Kunde inte tolka fjärrutrymme för {normalized}: {line}");
                return null;
            }

            const long blockSize = 1024;
            return new StorageInfo(availableBlocks * blockSize, totalBlocks * blockSize);
        }
    }

    public DirectoryContentSummary CountDirectoryContent(string path)
    {
        lock (_syncRoot)
        {
            RequireSftp();
            var normalized = Normalize(path);
            var directDirectories = 0;
            var directFiles = 0;
            var subdirectories = 0;
            var nestedFiles = 0;
            var directBytes = 0L;
            var nestedBytes = 0L;

            IReadOnlyList<Renci.SshNet.Sftp.ISftpFile> entries;
            try
            {
                entries = _sftpClient!.ListDirectory(normalized)
                    .Where(item => item.Name is not "." and not "..")
                    .ToList();
            }
            catch
            {
                return new DirectoryContentSummary(0, 0, 0, 0);
            }

            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    directDirectories++;
                    CountNestedRemoteContent(PosixPath.Join(normalized, entry.Name), ref subdirectories, ref nestedFiles, ref nestedBytes);
                }
                else
                {
                    directFiles++;
                    directBytes += entry.Length;
                }
            }

            return new DirectoryContentSummary(
                directDirectories,
                directFiles,
                subdirectories,
                nestedFiles,
                directBytes,
                nestedBytes);
        }
    }

    public void MakeDirectory(string path)
    {
        RequireSftp();
        MkdirP(path);
    }

    public IReadOnlyList<string> FindDuplicateFiles(string remoteRoot)
    {
        RequireSftp();
        remoteRoot = Normalize(remoteRoot);
        var remoteFiles = IndexRemoteTree(remoteRoot);
        var duplicates = new List<string>();

        foreach (var (remotePath, info) in remoteFiles)
        {
            var directory = PosixPath.DirectoryName(remotePath);
            var filename = PosixPath.FileName(remotePath);
            var extension = Path.GetExtension(filename);
            var stem = Path.GetFileNameWithoutExtension(filename);
            var match = System.Text.RegularExpressions.Regex.Match(stem, @"^(.+) \(([1-9]\d*)\)$");
            if (!match.Success)
            {
                continue;
            }

            var originalPath = PosixPath.Join(directory, $"{match.Groups[1].Value}{extension}");
            if (remoteFiles.TryGetValue(originalPath, out var originalInfo) && originalInfo.Size == info.Size)
            {
                duplicates.Add(remotePath);
            }
        }

        Log($"Hittade {duplicates.Count} dubblettfil(er) under {remoteRoot}");
        return duplicates.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public (IReadOnlyList<string> Deleted, IReadOnlyList<(string File, string Error)> Failed) DeleteFiles(IReadOnlyList<string> remoteFiles)
    {
        RequireSftp();
        var deleted = new List<string>();
        var failed = new List<(string File, string Error)>();

        foreach (var remoteFile in remoteFiles)
        {
            try
            {
                _sftpClient!.DeleteFile(remoteFile);
                deleted.Add(remoteFile);
                Log($"Tog bort dubblettfil: {remoteFile}");
            }
            catch (Exception exc)
            {
                var error = ErrorText(exc);
                failed.Add((remoteFile, error));
                Log($"Kunde inte ta bort dubblettfil {remoteFile}: {error}");
            }
        }

        return (deleted, failed);
    }

    public void DeleteRemotePath(string remotePath, bool isDirectory)
    {
        RequireSftp();
        remotePath = Normalize(remotePath);

        if (isDirectory)
        {
            DeleteDirectoryRecursive(remotePath);
            Log($"Tog bort mapp: {remotePath}");
            return;
        }

        _sftpClient!.DeleteFile(remotePath);
        Log($"Tog bort fil: {remotePath}");
    }

    private void DeleteDirectoryRecursive(string remoteDir)
    {
        foreach (var entry in _sftpClient!.ListDirectory(remoteDir).Where(item => item.Name is not "." and not ".."))
        {
            var childPath = PosixPath.Join(remoteDir, entry.Name);
            if (entry.IsDirectory)
            {
                DeleteDirectoryRecursive(childPath);
            }
            else
            {
                _sftpClient.DeleteFile(childPath);
            }
        }

        _sftpClient.DeleteDirectory(remoteDir);
    }

    public void UploadPaths(
        IReadOnlyList<string> localPaths,
        string remoteDestination,
        Action<int, int, string, string> progress,
        Action<string>? status = null,
        Action<UploadProgressUpdate>? uploadProgress = null,
        Action<string>? foldersPrepared = null,
        CancellationToken cancellationToken = default)
    {
        RequireSftp();
        cancellationToken.ThrowIfCancellationRequested();
        Log("");
        Log("=== Ny överföring ===");
        Log($"Destination från GUI: {remoteDestination}");
        foreach (var path in localPaths)
        {
            Log($"Lokalt val: {path}");
        }

        try
        {
            remoteDestination = Normalize(remoteDestination);
            Log($"Normaliserad destination: {remoteDestination}");
            MkdirP(remoteDestination);
        }
        catch (Exception exc)
        {
            LogException("Kunde inte förbereda destination", exc);
            throw new InvalidOperationException($"Kunde inte förbereda destinationen {remoteDestination}: {ErrorText(exc)}", exc);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (PrepareRemoteFolderStructure(localPaths, remoteDestination, status))
        {
            foldersPrepared?.Invoke(remoteDestination);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Log("Planerar överföring genom att läsa fjärrmappar först...");
        status?.Invoke("Kontrollerar vilka filer som redan finns på Raspberry Pi...");
        var plan = BuildTransferPlan(localPaths, remoteDestination, status);
        var totalFiles = plan.Count;
        var skipped = plan.Count(item => item.Action == "skipped");
        var renamed = plan.Count(item => item.Action == "renamed");
        var uploads = plan.Count(item => item.Action is "uploaded" or "renamed");
        Log($"Plan klar. Totalt={totalFiles}, skicka={uploads}, hoppa över={skipped}, döp om={renamed}");

        var done = 0;
        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Action == "skipped")
            {
                Log($"Hoppar över befintlig fil med samma storlek: {item.LocalPath} -> {item.RemotePath}");
                done++;
                progress(done, totalFiles, item.DisplayName, item.Action);
                continue;
            }

            progress(done, totalFiles, item.DisplayName, item.Action);
            UploadPlannedFile(item, status, uploadProgress, cancellationToken);
            done++;
            progress(done, totalFiles, item.DisplayName, item.Action);
        }

        Log($"Överföring klar. Behandlade={done}, hoppade över={skipped}, döpte om={renamed}");
    }

    public void DownloadPaths(
        IReadOnlyList<string> remotePaths,
        string localDestination,
        Action<int, int, string, string> progress,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        RequireSftp();
        Directory.CreateDirectory(localDestination);

        var files = new List<(string RemotePath, string LocalPath, string DisplayName)>();
        foreach (var remotePath in remotePaths)
        {
            var normalized = Normalize(remotePath);
            var name = PosixPath.FileName(normalized.TrimEnd('/'));
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "root";
            }

            CollectDownloadFiles(normalized, Path.Combine(localDestination, name), name, files);
        }

        var done = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(file.LocalPath) ?? localDestination);
            status?.Invoke($"Hämtar: {file.DisplayName}");
            progress(done, files.Count, file.DisplayName, "downloaded");
            using var output = File.Create(file.LocalPath);
            _sftpClient!.DownloadFile(file.RemotePath, output);
            done++;
            progress(done, files.Count, file.DisplayName, "downloaded");
        }
    }

    public void Dispose() => Close();

    private void ConnectCurrent()
    {
        if (_connectionParams is null)
        {
            throw new InvalidOperationException("Saknar sparade anslutningsuppgifter för återanslutning.");
        }

        Close();
        var connectionInfo = BuildConnectionInfo(_connectionParams);
        var sshClient = new SshClient(connectionInfo);
        var sftpClient = new SftpClient(connectionInfo);
        sshClient.Connect();
        sftpClient.Connect();
        _sshClient = sshClient;
        _sftpClient = sftpClient;
    }

    private static ConnectionInfo BuildConnectionInfo(ConnectionParams parameters)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(parameters.KeyPath))
        {
            methods.Add(new PrivateKeyAuthenticationMethod(parameters.Username, new PrivateKeyFile(parameters.KeyPath)));
        }

        if (!string.IsNullOrEmpty(parameters.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(parameters.Username, parameters.Password));
        }

        if (methods.Count == 0)
        {
            methods.Add(new NoneAuthenticationMethod(parameters.Username));
        }

        var connectionInfo = new ConnectionInfo(parameters.Host, AppPaths.SshPort, parameters.Username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(8),
            MaxSessions = 10,
            RetryAttempts = 1
        };

        return connectionInfo;
    }

    private IReadOnlyList<(string RemoteFile, long Size)> ListFilesUnder(string remoteDir, string relativeDir)
    {
        var files = new List<(string RemoteFile, long Size)>();
        IReadOnlyList<Renci.SshNet.Sftp.ISftpFile> entries;
        try
        {
            entries = _sftpClient!.ListDirectory(remoteDir)
                .Where(item => item.Name is not "." and not "..")
                .ToList();
        }
        catch (Exception exc)
        {
            Log($"Kunde inte läsa undermapp {remoteDir}: {ErrorText(exc)}");
            return files;
        }

        foreach (var entry in entries)
        {
            var remotePath = PosixPath.Join(remoteDir, entry.Name);
            var relativePath = PosixPath.Join(relativeDir, entry.Name);
            if (entry.IsDirectory)
            {
                files.AddRange(ListFilesUnder(remotePath, relativePath));
            }
            else
            {
                files.Add((relativePath, entry.Length));
            }
        }

        return files;
    }

    private void CollectDownloadFiles(
        string remotePath,
        string localPath,
        string displayName,
        List<(string RemotePath, string LocalPath, string DisplayName)> files)
    {
        var attributes = _sftpClient!.GetAttributes(remotePath);
        if (!attributes.IsDirectory)
        {
            files.Add((remotePath, localPath, displayName));
            return;
        }

        Directory.CreateDirectory(localPath);
        foreach (var entry in _sftpClient.ListDirectory(remotePath).Where(item => item.Name is not "." and not ".."))
        {
            CollectDownloadFiles(
                PosixPath.Join(remotePath, entry.Name),
                Path.Combine(localPath, entry.Name),
                PosixPath.Join(displayName, entry.Name),
                files);
        }
    }

    private List<TransferItem> BuildTransferPlan(IReadOnlyList<string> localPaths, string remoteDestination, Action<string>? status)
    {
        var plan = new List<TransferItem>();
        var remoteCache = new Dictionary<string, RemoteFileInfo>(StringComparer.Ordinal);

        foreach (var localPath in localPaths)
        {
            if (Directory.Exists(localPath))
            {
                var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(localPath));
                var remoteRoot = PosixPath.Join(remoteDestination, directoryName);
                foreach (var item in IndexRemoteTree(remoteRoot, status))
                {
                    remoteCache[item.Key] = item.Value;
                }

                foreach (var child in Directory.EnumerateFiles(localPath, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(localPath, child);
                    var remoteDir = Path.GetDirectoryName(relative) is { } relativeDir && relativeDir != "."
                        ? PosixPath.Join(remoteRoot, relativeDir.Replace('\\', '/'))
                        : remoteRoot;
                    var remoteFile = PosixPath.Join(remoteDir, Path.GetFileName(child));
                    plan.Add(PlannedItem(child, remoteFile, remoteCache));
                }
            }
            else
            {
                foreach (var item in IndexRemoteDirectory(remoteDestination))
                {
                    remoteCache[item.Key] = item.Value;
                }

                var remoteFile = PosixPath.Join(remoteDestination, Path.GetFileName(localPath));
                plan.Add(PlannedItem(localPath, remoteFile, remoteCache));
            }
        }

        return plan;
    }

    private bool PrepareRemoteFolderStructure(IReadOnlyList<string> localPaths, string remoteDestination, Action<string>? status)
    {
        var preparedAny = false;

        foreach (var localPath in localPaths)
        {
            if (!Directory.Exists(localPath))
            {
                continue;
            }

            preparedAny = true;
            var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(localPath));
            var remoteRoot = PosixPath.Join(remoteDestination, directoryName);
            status?.Invoke($"Skapar mappen {directoryName}/ på Raspberry Pi...");
            MkdirP(remoteRoot);

            foreach (var directory in Directory.EnumerateDirectories(localPath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(localPath, directory).Replace('\\', '/');
                var remoteDir = PosixPath.Join(remoteRoot, relative);
                MkdirP(remoteDir);
            }
        }

        return preparedAny;
    }

    private TransferItem PlannedItem(string localPath, string remoteFile, Dictionary<string, RemoteFileInfo> remoteCache)
    {
        var localSize = new FileInfo(localPath).Length;
        if (remoteCache.TryGetValue(remoteFile, out var remoteInfo) && remoteInfo.Size == localSize)
        {
            return new TransferItem(localPath, remoteFile, "skipped", Path.GetFileName(localPath));
        }

        if (remoteInfo is not null)
        {
            var uniquePath = UniqueRemotePath(remoteFile, remoteCache);
            return new TransferItem(localPath, uniquePath, "renamed", Path.GetFileName(localPath));
        }

        return new TransferItem(localPath, remoteFile, "uploaded", Path.GetFileName(localPath));
    }

    private void UploadPlannedFile(
        TransferItem item,
        Action<string>? status,
        Action<UploadProgressUpdate>? uploadProgress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MkdirP(PosixPath.DirectoryName(item.RemotePath));
        if (item.Action == "renamed")
        {
            Log($"Befintlig fil skiljer sig; skriver inte över. Skickar som nytt namn: {item.LocalPath} -> {item.RemotePath}");
        }
        else
        {
            Log($"Skickar fil: {item.LocalPath} -> {item.RemotePath}");
        }

        var statusLabel = item.Action == "renamed"
            ? $"Skickar utan att skriva över: {item.DisplayName}"
            : $"Skickar: {item.DisplayName}";
        status?.Invoke(statusLabel);

        for (var attempt = 1; attempt <= AppPaths.TransferRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var input = File.OpenRead(item.LocalPath);
                var fileSize = new FileInfo(item.LocalPath).Length;
                var speedMeter = new TransferSpeedMeter();
                var lastPercent = -1;
                var lastReportTick = 0L;
                uploadProgress?.Invoke(new UploadProgressUpdate(statusLabel, 0, 0, 0, fileSize));
                _sftpClient!.UploadFile(input, item.RemotePath, true, uploadedBytes =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var percent = fileSize <= 0 ? 100 : (int)(uploadedBytes * 100 / (ulong)fileSize);
                    var nowTick = Environment.TickCount64;
                    if (percent == lastPercent && nowTick - lastReportTick < 250)
                    {
                        return;
                    }

                    lastPercent = percent;
                    lastReportTick = nowTick;
                    var bytesPerSecond = speedMeter.Update(uploadedBytes);
                    uploadProgress?.Invoke(new UploadProgressUpdate(statusLabel, percent, bytesPerSecond, uploadedBytes, fileSize));
                });
                var fileInfo = new FileInfo(item.LocalPath);
                _sftpClient.SetLastAccessTime(item.RemotePath, fileInfo.LastAccessTime);
                _sftpClient.SetLastWriteTime(item.RemotePath, fileInfo.LastWriteTime);
                return;
            }
            catch (Exception exc)
            {
                if (exc is OperationCanceledException || cancellationToken.IsCancellationRequested)
                {
                    Log($"Överföring avbruten under fil: {item.LocalPath} -> {item.RemotePath}");
                    throw new OperationCanceledException(cancellationToken);
                }

                if (!IsConnectionError(exc) || attempt >= AppPaths.TransferRetryAttempts)
                {
                    LogException($"Misslyckades med fil: {item.LocalPath} -> {item.RemotePath}", exc);
                    throw new InvalidOperationException($"Kunde inte skicka {Path.GetFileName(item.LocalPath)} till {item.RemotePath}: {ErrorText(exc)}", exc);
                }

                LogException($"Anslutningen bröts under fil: {item.LocalPath} -> {item.RemotePath}. Försök {attempt}/{AppPaths.TransferRetryAttempts}", exc);
                status?.Invoke($"Anslutningen bröts. Väntar {AppPaths.TransferRetryDelaySeconds}s och återansluter ({attempt}/{AppPaths.TransferRetryAttempts})...");
                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(AppPaths.TransferRetryDelaySeconds)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                Reconnect(status);
            }
        }
    }

    private Dictionary<string, RemoteFileInfo> IndexRemoteTree(string remoteRoot, Action<string>? status = null)
    {
        var index = new Dictionary<string, RemoteFileInfo>(StringComparer.Ordinal);
        IndexRemoteTreeInto(remoteRoot, index, status);
        Log($"Indexerade {index.Count} befintliga fjärrfiler under {remoteRoot}");
        return index;
    }

    private void IndexRemoteTreeInto(string remoteDir, Dictionary<string, RemoteFileInfo> index, Action<string>? status)
    {
        status?.Invoke($"Kontrollerar befintliga filer i {remoteDir}...");

        IReadOnlyList<Renci.SshNet.Sftp.ISftpFile> entries;
        try
        {
            entries = _sftpClient!.ListDirectory(remoteDir)
                .Where(item => item.Name is not "." and not "..")
                .ToList();
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            var remotePath = PosixPath.Join(remoteDir, entry.Name);
            if (entry.IsDirectory)
            {
                IndexRemoteTreeInto(remotePath, index, status);
            }
            else
            {
                index[remotePath] = new RemoteFileInfo(entry.Length, entry.LastWriteTime);
            }
        }
    }

    private void CountNestedRemoteContent(string remoteDir, ref int subdirectories, ref int nestedFiles, ref long nestedBytes)
    {
        subdirectories++;
        IReadOnlyList<Renci.SshNet.Sftp.ISftpFile> entries;
        try
        {
            entries = _sftpClient!.ListDirectory(remoteDir)
                .Where(item => item.Name is not "." and not "..")
                .ToList();
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            var remotePath = PosixPath.Join(remoteDir, entry.Name);
            if (entry.IsDirectory)
            {
                CountNestedRemoteContent(remotePath, ref subdirectories, ref nestedFiles, ref nestedBytes);
            }
            else
            {
                nestedFiles++;
                nestedBytes += entry.Length;
            }
        }
    }

    private Dictionary<string, RemoteFileInfo> IndexRemoteDirectory(string remoteDir)
    {
        var index = new Dictionary<string, RemoteFileInfo>(StringComparer.Ordinal);
        try
        {
            foreach (var entry in _sftpClient!.ListDirectory(remoteDir).Where(item => item.Name is not "." and not ".." && !item.IsDirectory))
            {
                index[PosixPath.Join(remoteDir, entry.Name)] = new RemoteFileInfo(entry.Length, entry.LastWriteTime);
            }
        }
        catch
        {
            return index;
        }

        Log($"Indexerade {index.Count} befintliga fjärrfiler i {remoteDir}");
        return index;
    }

    private static string UniqueRemotePath(string remoteFile, Dictionary<string, RemoteFileInfo> remoteCache)
    {
        var directory = PosixPath.DirectoryName(remoteFile);
        var filename = PosixPath.FileName(remoteFile);
        var extension = Path.GetExtension(filename);
        var stem = Path.GetFileNameWithoutExtension(filename);
        var counter = 1;

        while (true)
        {
            var candidate = PosixPath.Join(directory, $"{stem} ({counter}){extension}");
            if (!remoteCache.ContainsKey(candidate))
            {
                remoteCache[candidate] = new RemoteFileInfo(-1, DateTime.MinValue);
                return candidate;
            }

            counter++;
        }
    }

    private void Reconnect(Action<string>? status = null)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= AppPaths.TransferRetryAttempts; attempt++)
        {
            try
            {
                Log($"Återansluter SSH/SFTP, försök {attempt}/{AppPaths.TransferRetryAttempts}");
                status?.Invoke($"Återansluter till Raspberry Pi ({attempt}/{AppPaths.TransferRetryAttempts})...");
                ConnectCurrent();
                Log("Återanslutning lyckades");
                return;
            }
            catch (Exception exc)
            {
                lastError = exc;
                LogException($"Återanslutning misslyckades, försök {attempt}", exc);
                Thread.Sleep(TimeSpan.FromSeconds(AppPaths.TransferRetryDelaySeconds));
            }
        }

        throw new InvalidOperationException($"Kunde inte återansluta: {ErrorText(lastError)}");
    }

    private void MkdirP(string remotePath)
    {
        RequireSftp();
        var normalized = PosixPath.Normalize(remotePath);
        if (normalized is "" or ".")
        {
            return;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = normalized.StartsWith("/", StringComparison.Ordinal) ? "/" : ".";
        foreach (var part in parts)
        {
            current = current == "/" ? $"/{part}" : PosixPath.Join(current, part);
            if (_sftpClient!.Exists(current))
            {
                continue;
            }

            Log($"Skapar fjärrmapp: {current}");
            try
            {
                _sftpClient.CreateDirectory(current);
            }
            catch (Exception exc)
            {
                LogException($"Kunde inte skapa fjärrmapp: {current}", exc);
                throw new InvalidOperationException($"Kunde inte skapa fjärrmappen {current}: {ErrorText(exc)}", exc);
            }
        }
    }

    private void RequireSftp()
    {
        if (_sftpClient is null || !_sftpClient.IsConnected)
        {
            throw new InvalidOperationException("Inte ansluten till Raspberry Pi.");
        }
    }

    private void RequireSsh()
    {
        if (_sshClient is null || !_sshClient.IsConnected)
        {
            throw new InvalidOperationException("Inte ansluten till Raspberry Pi.");
        }
    }

    private static string ShellQuote(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    private static bool IsConnectionError(Exception exc)
    {
        var text = $"{exc.GetType().Name}: {exc.Message}".ToLowerInvariant();
        var connectionMarkers = new[]
        {
            "10054",
            "connection reset",
            "connection aborted",
            "connection closed",
            "socket",
            "eof",
            "server connection dropped",
            "no existing session",
            "not open"
        };

        return exc is SshException or IOException or ObjectDisposedException ||
               connectionMarkers.Any(text.Contains);
    }

    private static void Log(string message) => LogWriter.Write(AppPaths.TransferLogPath, message);

    private static void LogException(string context, Exception exc)
    {
        Log($"{context}: {ErrorText(exc)}");
        Log(exc.ToString());
    }

    private static string ErrorText(Exception? exc)
    {
        return exc is null ? "okänt fel" : string.IsNullOrWhiteSpace(exc.Message) ? exc.GetType().Name : exc.Message.Trim();
    }

    private sealed record ConnectionParams(string Host, string Username, string? Password, string? KeyPath);

    private sealed record RemoteFileInfo(long Size, DateTime Modified);
}
