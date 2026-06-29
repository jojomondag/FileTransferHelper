using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileTransferHelper.Models;
using FileTransferHelper.Services;

namespace FileTransferHelper.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly NetworkDiscoveryService _networkDiscovery;
    private readonly SftpTransferClient _sftpClient;
    private readonly LocalFileSystemClient _localFileSystem;
    private readonly UiSettingsStore _uiSettingsStore;
    private readonly RemoteTreeCacheStore _remoteTreeCache;
    private readonly IDialogService _dialogs;
    private UiSettings _uiSettings = new();
    private string _connectedHost = "";
    private double _userPreferredLocalTreeWidth = 120;
    private double _userPreferredRemoteTreeWidth = 120;

    [ObservableProperty]
    private HostCandidate? _selectedHost;

    [ObservableProperty]
    private string _username = "pi";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _remotePath = "/home/pi";

    [ObservableProperty]
    private string _status = "Redo.";

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _transferSpeedText = "";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(ShowConnectionControls))]
    private bool _isConnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    [NotifyPropertyChangedFor(nameof(ShowConnectionControls))]
    private bool _isPiConnected;

    [ObservableProperty]
    private bool _isTransferring;

    [ObservableProperty]
    private bool _isDeletingDuplicates;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedRemoteItemCommand))]
    private bool _isDeletingRemoteItem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedLocalItemCommand))]
    private bool _isDeletingLocalItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    [NotifyPropertyChangedFor(nameof(ShowConnectionControls))]
    private bool _autoConnecting;

    public string ConnectionStatusText
    {
        get
        {
            if (IsConnecting || AutoConnecting)
            {
                return "Ansluter...";
            }

            if (IsPiConnected)
            {
                if (SelectedHost is { } host)
                {
                    return $"Ansluten till {host.Name} ({host.Address})";
                }

                return string.IsNullOrWhiteSpace(_connectedHost)
                    ? "Ansluten."
                    : $"Ansluten till {_connectedHost}";
            }

            return "Ej ansluten";
        }
    }

    public bool ShowConnectionControls
    {
        get
        {
            if (IsConnecting || AutoConnecting)
            {
                return false;
            }

            if (!IsPiConnected)
            {
                return true;
            }

            var selectedAddress = SelectedHostAddress();
            return !string.Equals(selectedAddress, _connectedHost, StringComparison.OrdinalIgnoreCase);
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedRemoteEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedRemoteItemCommand))]
    private RemoteEntry? _selectedRemoteEntry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedRemoteItemCommand))]
    private RemoteTreeNode? _selectedRemoteTreeNode;

    public ObservableCollection<RemoteTreeNode> SelectedRemoteTreeNodes { get; } = [];

    [ObservableProperty]
    private double _remoteTreePaneWidth = 96;

    [ObservableProperty]
    private GridLength _remoteTreeColumnWidth = new(120, GridUnitType.Pixel);

    [ObservableProperty]
    private string _localPath = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedLocalEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedLocalItemCommand))]
    private LocalEntry? _selectedLocalEntry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedLocalItemCommand))]
    private LocalTreeNode? _selectedLocalTreeNode;

    public ObservableCollection<LocalTreeNode> SelectedLocalTreeNodes { get; } = [];

    [ObservableProperty]
    private double _localTreePaneWidth = 96;

    [ObservableProperty]
    private GridLength _localTreeColumnWidth = new(120, GridUnitType.Pixel);

    [ObservableProperty]
    private GridLength _localPanelColumnWidth = new(470, GridUnitType.Pixel);

    private int _suppressHostSelectionAutoConnect;
    private int _suppressTreeSelectionNavigation;
    private int _remoteContentsRefreshGeneration;
    private int _suppressLocalTreeSelectionNavigation;
    private int _localContentsRefreshGeneration;

    private const double RemoteTreeMinPaneWidth = 96;
    private const double RemoteTreeMaxPaneWidth = 520;
    private const double LocalTreeMinPaneWidth = 96;
    private const double LocalTreeMaxPaneWidth = 520;
    private const double MainPanelMinWidth = 280;
    private double _userPreferredLocalPanelWidth;

    private int _transferDone;
    private int _transferTotal;

    public event EventHandler? RemoteTreeLayoutChanged;
    public event EventHandler? LocalTreeLayoutChanged;

    public void RequestRemoteTreeLayoutUpdate() => ScheduleRemoteTreeLayoutUpdate();
    public void RequestLocalTreeLayoutUpdate() => ScheduleLocalTreeLayoutUpdate();

    public ObservableCollection<HostCandidate> Hosts { get; } = [];
    public ObservableCollection<LocalEntry> LocalEntries { get; } = [];
    public ObservableCollection<LocalTreeNode> LocalTreeRoots { get; } = [];
    public ObservableCollection<RemoteEntry> RemoteEntries { get; } = [];
    public ObservableCollection<RemoteTreeNode> RemoteTreeRoots { get; } = [];

    public MainWindowViewModel()
        : this(new NullDialogService(), new NetworkDiscoveryService(), new SftpTransferClient())
    {
    }

    public MainWindowViewModel(IDialogService dialogs)
        : this(dialogs, new NetworkDiscoveryService(), new SftpTransferClient())
    {
    }

    private MainWindowViewModel(IDialogService dialogs, NetworkDiscoveryService networkDiscovery, SftpTransferClient sftpClient)
    {
        _dialogs = dialogs;
        _networkDiscovery = networkDiscovery;
        _sftpClient = sftpClient;
        _localFileSystem = new LocalFileSystemClient();
        _uiSettingsStore = new UiSettingsStore();
        _remoteTreeCache = new RemoteTreeCacheStore();
        _uiSettings = _uiSettingsStore.Load();
        _userPreferredLocalTreeWidth = Math.Clamp(_uiSettings.LocalTreeColumnWidth, LocalTreeMinPaneWidth, LocalTreeMaxPaneWidth);
        _userPreferredRemoteTreeWidth = Math.Clamp(_uiSettings.RemoteTreeColumnWidth, RemoteTreeMinPaneWidth, RemoteTreeMaxPaneWidth);
        LocalTreeColumnWidth = new GridLength(_userPreferredLocalTreeWidth, GridUnitType.Pixel);
        RemoteTreeColumnWidth = new GridLength(_userPreferredRemoteTreeWidth, GridUnitType.Pixel);
        var defaultPanelWidth = Math.Max(
            MainPanelMinWidth,
            ((_uiSettings.WindowWidth > 0 ? _uiSettings.WindowWidth : 980) - 28 - 6) / 2);
        _userPreferredLocalPanelWidth = _uiSettings.LocalPanelWidth > 0
            ? _uiSettings.LocalPanelWidth
            : defaultPanelWidth;
        _userPreferredLocalPanelWidth = Math.Max(_userPreferredLocalPanelWidth, MainPanelMinWidth);
        LocalPanelColumnWidth = new GridLength(_userPreferredLocalPanelWidth, GridUnitType.Pixel);
        LocalPath = string.IsNullOrWhiteSpace(_uiSettings.LastLocalPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : _uiSettings.LastLocalPath;
        ApplyRememberedSessionPreferences();
        InitializeLocalTreeRoot();
        SelectedLocalTreeNodes.CollectionChanged += OnSelectedLocalTreeNodesChanged;
        SelectedRemoteTreeNodes.CollectionChanged += OnSelectedRemoteTreeNodesChanged;
        _ = InitializeAsync();
    }

    public bool IsLocalTreeSelectionNavigationSuppressed => _suppressLocalTreeSelectionNavigation > 0;

    public bool IsRemoteTreeSelectionNavigationSuppressed => _suppressTreeSelectionNavigation > 0;

    public string RemotePathForEntry(RemoteEntry entry)
    {
        return PosixPath.Join(string.IsNullOrWhiteSpace(RemotePath) ? "." : RemotePath.Trim(), entry.Name);
    }

    public void SaveTreeColumnWidths(double localWidth, double remoteWidth)
    {
        _userPreferredLocalTreeWidth = Math.Clamp(localWidth, LocalTreeMinPaneWidth, LocalTreeMaxPaneWidth);
        _userPreferredRemoteTreeWidth = Math.Clamp(remoteWidth, RemoteTreeMinPaneWidth, RemoteTreeMaxPaneWidth);
        _uiSettings.LocalTreeColumnWidth = _userPreferredLocalTreeWidth;
        _uiSettings.RemoteTreeColumnWidth = _userPreferredRemoteTreeWidth;
        _uiSettingsStore.Save(_uiSettings);
    }

    public void SaveLocalPanelWidth(double width)
    {
        if (width <= 0)
        {
            return;
        }

        _userPreferredLocalPanelWidth = Math.Max(width, MainPanelMinWidth);
        _uiSettings.LocalPanelWidth = _userPreferredLocalPanelWidth;
        if (Math.Abs(LocalPanelColumnWidth.Value - _userPreferredLocalPanelWidth) > 0.5)
        {
            LocalPanelColumnWidth = new GridLength(_userPreferredLocalPanelWidth, GridUnitType.Pixel);
        }

        _uiSettingsStore.Save(_uiSettings);
    }

    public void SaveUiSettings()
    {
        _uiSettings.LastLocalPath = LocalPath;
        var host = ConnectedHostAddress();
        if (!string.IsNullOrWhiteSpace(host))
        {
            _uiSettings.LastRemotePathsByHost[host] = RemotePath;
        }

        _uiSettingsStore.Save(_uiSettings);
        _remoteTreeCache.Save();
    }

    public void ApplyWindowSettings(Window window)
    {
        window.Width = Math.Max(window.MinWidth, _uiSettings.WindowWidth > 0 ? _uiSettings.WindowWidth : window.Width);
        window.Height = Math.Max(window.MinHeight, _uiSettings.WindowHeight > 0 ? _uiSettings.WindowHeight : window.Height);

        if (_uiSettings.WindowX is int x && _uiSettings.WindowY is int y)
        {
            window.Position = new PixelPoint(x, y);
        }

        if (_uiSettings.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    public void CaptureWindowSettings(Window window, PixelRect? normalBounds)
    {
        _uiSettings.IsMaximized = window.WindowState == WindowState.Maximized;

        PixelRect bounds;
        if (window.WindowState == WindowState.Normal)
        {
            bounds = new PixelRect(
                window.Position,
                new PixelSize(Math.Max(1, (int)Math.Round(window.Width)), Math.Max(1, (int)Math.Round(window.Height))));
        }
        else if (normalBounds is not null)
        {
            bounds = normalBounds.Value;
        }
        else
        {
            return;
        }

        _uiSettings.WindowX = bounds.X;
        _uiSettings.WindowY = bounds.Y;
        _uiSettings.WindowWidth = bounds.Width;
        _uiSettings.WindowHeight = bounds.Height;
    }

    partial void OnLocalPathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _uiSettings.LastLocalPath = value;
        _uiSettingsStore.Save(_uiSettings);
    }

    partial void OnRemotePathChanged(string value)
    {
        var host = ConnectedHostAddress();
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _uiSettings.LastRemotePathsByHost[host] = value;
        _uiSettingsStore.Save(_uiSettings);
    }

    public void OpenLocalTreeNode(LocalTreeNode node)
    {
        if (node.IsPlaceholder)
        {
            return;
        }

        if (!node.IsExpanded)
        {
            node.IsExpanded = true;
        }

        NavigateToLocalTreeNode(node);
    }

    public void NavigateToLocalTreeNode(LocalTreeNode node)
    {
        if (!node.IsExpanded)
        {
            node.IsExpanded = true;
        }

        LocalPath = node.FullPath;
        _ = RefreshLocalContentsOnlyAsync();
    }

    public void NavigateToLocalTreeNodeIfNeeded(LocalTreeNode node)
    {
        var targetPath = _localFileSystem.Normalize(node.FullPath);
        if (string.Equals(targetPath, _localFileSystem.Normalize(LocalPath), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        NavigateToLocalTreeNode(node);
    }

    public void WithSuppressedLocalTreeSelectionNavigation(Action action)
    {
        _suppressLocalTreeSelectionNavigation++;
        try
        {
            action();
        }
        finally
        {
            _suppressLocalTreeSelectionNavigation--;
        }
    }

    partial void OnSelectedHostChanged(HostCandidate? value)
    {
        if (value is null)
        {
            OnPropertyChanged(nameof(ConnectionStatusText));
            OnPropertyChanged(nameof(ShowConnectionControls));
            return;
        }

        ApplySavedCredentialsForHost(value);
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(ShowConnectionControls));
        if (_suppressHostSelectionAutoConnect == 0)
        {
            _ = TryConnectSilentlyAsync();
        }
    }

    partial void OnIsConnectingChanged(bool value) => OnPropertyChanged(nameof(ConnectionStatusText));

    partial void OnAutoConnectingChanged(bool value) => OnPropertyChanged(nameof(ConnectionStatusText));

    partial void OnIsPiConnectedChanged(bool value) => OnPropertyChanged(nameof(ConnectionStatusText));

    public void OpenRemoteTreeNode(RemoteTreeNode node)
    {
        if (node.IsPlaceholder)
        {
            return;
        }

        if (!node.IsExpanded)
        {
            node.IsExpanded = true;
        }

        NavigateToRemoteTreeNode(node);
    }

    public void NavigateToRemoteTreeNode(RemoteTreeNode node)
    {
        if (!node.IsExpanded)
        {
            node.IsExpanded = true;
        }

        RemotePath = node.FullPath;
        _ = RefreshRemoteContentsOnlyAsync();
    }

    public void NavigateToRemoteTreeNodeIfNeeded(RemoteTreeNode node)
    {
        var targetPath = PosixPath.Normalize(node.FullPath);
        if (string.Equals(targetPath, PosixPath.Normalize(RemotePath), StringComparison.Ordinal))
        {
            return;
        }

        NavigateToRemoteTreeNode(node);
    }

    public void WithSuppressedTreeSelectionNavigation(Action action)
    {
        _suppressTreeSelectionNavigation++;
        try
        {
            action();
        }
        finally
        {
            _suppressTreeSelectionNavigation--;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        await ConnectInternalAsync(showErrors: true);
    }

    private bool CanConnect() => !IsConnecting;

    public async Task TryConnectSilentlyAsync()
    {
        if (_sftpClient.IsConnected || IsConnecting)
        {
            return;
        }

        var host = SelectPreferredHost();
        if (host is null)
        {
            return;
        }

        ApplySavedCredentialsForHost(host);
        var password = CredentialStore.PasswordFor(host.Address);
        if (string.IsNullOrEmpty(password))
        {
            if (SelectedHost?.Address != host.Address)
            {
                SetSelectedHostWithoutAutoConnect(host);
            }

            return;
        }

        Password = password;
        if (SelectedHost?.Address != host.Address)
        {
            SetSelectedHostWithoutAutoConnect(host);
        }

        AutoConnecting = true;
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(ShowConnectionControls));
        await ConnectInternalAsync(showErrors: false);
    }

    private async Task<bool> ConnectInternalAsync(bool showErrors)
    {
        var host = SelectedHostAddress();
        var username = Username.Trim();
        var password = Password;

        if (string.IsNullOrWhiteSpace(host))
        {
            if (showErrors)
            {
                await _dialogs.ShowWarningAsync(AppPaths.AppTitle, "Välj en hittad enhet.");
            }

            return false;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            if (showErrors)
            {
                await _dialogs.ShowWarningAsync(AppPaths.AppTitle, "Ange användarnamn för Raspberry Pi.");
            }

            return false;
        }

        IsConnecting = true;
        Status = $"Ansluter till {host}...";

        try
        {
            var home = await Task.Run(() => _sftpClient.Connect(host, username, string.IsNullOrEmpty(password) ? null : password, null));
            SaveCredentialsForHost(host, username, password);
            _connectedHost = host;
            _remoteTreeCache.BindHost(host);
            _uiSettings.LastConnectedHostAddress = host;
            _uiSettings.LastConnectedUsername = username;
            _uiSettingsStore.Save(_uiSettings);
            RemoteTreeRoots.Clear();
            var savedPath = _uiSettings.LastRemotePathsByHost.TryGetValue(host, out var remembered) && !string.IsNullOrWhiteSpace(remembered)
                ? remembered
                : home;
            RemotePath = savedPath;
            Status = "Ansluten.";
            await RefreshRemoteDirsAsync();
            return true;
        }
        catch (Exception exc)
        {
            if (showErrors)
            {
                await ShowErrorAsync($"Kunde inte ansluta: {ExceptionText(exc)}");
            }
            else
            {
                LogWriter.Write(AppPaths.TransferLogPath, $"Bakgrundsanslutning misslyckades: {ExceptionText(exc)}");
            }

            return false;
        }
        finally
        {
            AutoConnecting = false;
            IsConnecting = false;
            UpdateConnectionState();
        }
    }

    [RelayCommand]
    private async Task RefreshLocalDirsAsync()
    {
        var path = string.IsNullOrWhiteSpace(LocalPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : LocalPath.Trim();
        var generation = Interlocked.Increment(ref _localContentsRefreshGeneration);
        Status = $"Läser {path}...";

        try
        {
            var (normalized, entries) = await Task.Run(() =>
            {
                var normalizedPath = _localFileSystem.Normalize(path);
                return (normalizedPath, _localFileSystem.ListEntries(normalizedPath));
            });

            if (generation != _localContentsRefreshGeneration)
            {
                return;
            }

            if (LocalTreeRoots.Count == 0)
            {
                InitializeLocalTreeRoot();
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _localContentsRefreshGeneration)
                {
                    return;
                }

                LocalPath = normalized;
                ApplyLocalEntries(entries);
            });

            await SyncLocalTreeSelectionToPathAsync(normalized);
            ScheduleLocalTreeLayoutUpdate();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _localContentsRefreshGeneration)
                {
                    return;
                }

                var dirCount = LocalEntries.Count(entry => entry.IsDirectory);
                var fileCount = LocalEntries.Count - dirCount;
                Status = $"Visar {LocalPath}. {dirCount} mappar, {fileCount} filer.";
            });
        }
        catch (Exception exc)
        {
            await ShowErrorAsync($"Kunde inte läsa lokala filer: {ExceptionText(exc)}");
        }
    }

    [RelayCommand]
    private async Task LocalHomeAsync()
    {
        LocalPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await RefreshLocalDirsAsync();
    }

    [RelayCommand]
    private async Task LocalUpAsync()
    {
        var current = string.IsNullOrWhiteSpace(LocalPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : LocalPath.Trim();
        var parent = Directory.GetParent(_localFileSystem.Normalize(current));
        LocalPath = parent?.FullName ?? Path.GetPathRoot(current) ?? current;
        await RefreshLocalDirsAsync();
    }

    public async Task EnterLocalEntryAsync(LocalEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (!entry.IsDirectory)
        {
            Status = $"Vald fil: {entry.Name}";
            return;
        }

        LocalPath = entry.FullPath;
        await RefreshLocalDirsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelectedLocalEntry))]
    private async Task OpenSelectedLocalEntryAsync()
    {
        await EnterLocalEntryAsync(SelectedLocalEntry);
    }

    private bool CanOpenSelectedLocalEntry() => SelectedLocalEntry?.IsDirectory == true;

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedLocalItem))]
    private async Task DeleteSelectedLocalItemAsync()
    {
        var targets = GetLocalDeleteTargets();
        if (targets.Count == 0)
        {
            return;
        }

        var message = targets.Count == 1
            ? targets[0].IsDirectory
                ? $"Ta bort mappen {targets[0].DisplayName} och allt innehåll från den här datorn?"
                : $"Ta bort filen {targets[0].DisplayName} från den här datorn?"
            : $"Ta bort {targets.Count} objekt och allt innehåll från den här datorn?";

        if (!await _dialogs.ConfirmAsync(AppPaths.AppTitle, message))
        {
            return;
        }

        IsDeletingLocalItem = true;
        Status = targets.Count == 1
            ? $"Tar bort {targets[0].DisplayName}..."
            : $"Tar bort {targets.Count} objekt...";

        try
        {
            foreach (var (path, isDirectory, displayName) in targets)
            {
                await Task.Run(() => _localFileSystem.DeletePath(path, isDirectory));

                var parentPath = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(parentPath))
                {
                    parentPath = Path.GetPathRoot(path) ?? path;
                }

                if (IsLocalPathInsideOrEqual(LocalPath, path))
                {
                    LocalPath = parentPath;
                }

                TryRemoveLocalTreeNode(path);
                InvalidateLocalTreeNode(parentPath);
            }

            SelectedLocalEntry = null;
            await RefreshLocalDirsAsync();
            Status = targets.Count == 1
                ? $"Tog bort {targets[0].DisplayName}."
                : $"Tog bort {targets.Count} objekt.";
        }
        catch (Exception exc)
        {
            var displayName = targets.Count == 1 ? targets[0].DisplayName : "valda objekt";
            await ShowErrorAsync($"Kunde inte ta bort {displayName}: {ExceptionText(exc)}");
        }
        finally
        {
            IsDeletingLocalItem = false;
        }
    }

    private bool CanDeleteSelectedLocalItem() =>
        !IsDeletingLocalItem && GetLocalDeleteTargets().Count > 0;

    private List<(string Path, bool IsDirectory, string DisplayName)> GetLocalDeleteTargets()
    {
        if (SelectedLocalEntry is { } entry)
        {
            var path = _localFileSystem.Normalize(entry.FullPath);
            if (IsLocalDriveRoot(path))
            {
                return [];
            }

            return [(path, entry.IsDirectory, entry.DisplayName)];
        }

        var treeNodes = SelectedLocalTreeNodes
            .Where(node => !node.IsPlaceholder)
            .ToList();
        if (treeNodes.Count == 0 && SelectedLocalTreeNode is { IsPlaceholder: false } single)
        {
            treeNodes.Add(single);
        }

        var targets = new List<(string Path, bool IsDirectory, string DisplayName)>();
        foreach (var node in treeNodes)
        {
            var path = _localFileSystem.Normalize(node.FullPath);
            if (IsLocalDriveRoot(path))
            {
                continue;
            }

            targets.Add((path, true, $"{node.Name}/"));
        }

        return targets;
    }

    private void OnSelectedLocalTreeNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        DeleteSelectedLocalItemCommand.NotifyCanExecuteChanged();

    private static bool IsLocalDriveRoot(string path)
    {
        var normalized = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var root = Path.GetPathRoot(normalized);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        return string.Equals(
            normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalPathInsideOrEqual(string currentPath, string targetPath)
    {
        var current = Path.GetFullPath(currentPath);
        var target = Path.GetFullPath(targetPath);
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryRemoveLocalTreeNode(string path)
    {
        var node = FindLocalTreeNode(path);
        if (node is null || IsLocalDriveRoot(node.FullPath))
        {
            return false;
        }

        var parentPath = Path.GetDirectoryName(node.FullPath);
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return false;
        }

        var parent = FindLocalTreeNode(parentPath);
        if (parent is null)
        {
            foreach (var root in LocalTreeRoots)
            {
                for (var i = root.Children.Count - 1; i >= 0; i--)
                {
                    var child = root.Children[i];
                    if (!child.IsPlaceholder && string.Equals(child.FullPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        root.Children.RemoveAt(i);
                        return true;
                    }
                }
            }

            return false;
        }

        for (var i = parent.Children.Count - 1; i >= 0; i--)
        {
            var child = parent.Children[i];
            if (!child.IsPlaceholder && string.Equals(child.FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                parent.Children.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    [RelayCommand]
    private async Task CreateLocalFolderAsync()
    {
        var name = await _dialogs.AskTextAsync("Ny mapp", "Mappnamn:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (name.Contains('/') || name.Contains('\\'))
        {
            await _dialogs.ShowWarningAsync(AppPaths.AppTitle, "Ange bara ett mappnamn, inte en hel sökväg.");
            return;
        }

        var parent = string.IsNullOrWhiteSpace(LocalPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : LocalPath.Trim();
        var target = Path.Combine(_localFileSystem.Normalize(parent), name);
        try
        {
            await Task.Run(() => _localFileSystem.CreateDirectory(target));
            Status = $"Skapade {target}.";
            InvalidateLocalTreeNode(parent);
            await RefreshLocalDirsAsync();
        }
        catch (Exception exc)
        {
            await ShowErrorAsync($"Kunde inte skapa mapp: {ExceptionText(exc)}");
        }
    }

    [RelayCommand]
    private async Task RefreshRemoteDirsAsync()
    {
        var path = string.IsNullOrWhiteSpace(RemotePath) ? "." : RemotePath.Trim();
        var generation = Interlocked.Increment(ref _remoteContentsRefreshGeneration);

        if (RemoteTreeRoots.Count == 0)
        {
            InitializeRemoteTreeRoot();
        }

        var cachedTreePath = path == "."
            ? (RemotePath.StartsWith('/') ? PosixPath.Normalize(RemotePath) : null)
            : PosixPath.Normalize(path);
        if (cachedTreePath is not null)
        {
            await SyncTreeSelectionToPathAsync(cachedTreePath);
            ScheduleRemoteTreeLayoutUpdate();
        }

        Status = $"Läser {path}...";

        try
        {
            var (normalized, entries) = await Task.Run(() =>
            {
                var normalizedPath = _sftpClient.Normalize(path);
                return (normalizedPath, _sftpClient.ListEntries(normalizedPath));
            });

            if (generation != _remoteContentsRefreshGeneration)
            {
                return;
            }

            if (RemoteTreeRoots.Count == 0)
            {
                InitializeRemoteTreeRoot();
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _remoteContentsRefreshGeneration)
                {
                    return;
                }

                RemotePath = normalized;
                ApplyRemoteEntries(entries);
            });

            await SyncTreeSelectionToPathAsync(normalized);
            ScheduleRemoteTreeLayoutUpdate();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _remoteContentsRefreshGeneration)
                {
                    return;
                }

                var dirCount = RemoteEntries.Count(entry => entry.IsDirectory);
                var fileCount = RemoteEntries.Count - dirCount;
                Status = $"Visar {RemotePath}. {dirCount} mappar, {fileCount} filer.";
            });

            _ = RefreshRemoteTreeBranchInBackgroundAsync(normalized);
        }
        catch (Exception exc)
        {
            await ShowErrorAsync($"Kunde inte läsa destinationen: {ExceptionText(exc)}");
        }
    }

    [RelayCommand]
    private async Task RemoteHomeAsync()
    {
        try
        {
            RemotePath = await Task.Run(() => _sftpClient.Normalize("."));
            await RefreshRemoteDirsAsync();
        }
        catch (Exception exc)
        {
            await ShowErrorAsync($"Kunde inte öppna hemkatalogen: {ExceptionText(exc)}");
        }
    }

    [RelayCommand]
    private async Task RemoteUpAsync()
    {
        var current = string.IsNullOrWhiteSpace(RemotePath) ? "." : RemotePath.Trim();
        RemotePath = PosixPath.DirectoryName(current.TrimEnd('/'));
        if (string.IsNullOrWhiteSpace(RemotePath))
        {
            RemotePath = "/";
        }

        await RefreshRemoteDirsAsync();
    }

    public async Task EnterRemoteEntryAsync(RemoteEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (!entry.IsDirectory)
        {
            Status = $"Vald fil: {entry.Name}";
            return;
        }

        RemotePath = PosixPath.Join(string.IsNullOrWhiteSpace(RemotePath) ? "." : RemotePath.Trim(), entry.Name);
        await RefreshRemoteDirsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelectedRemoteEntry))]
    private async Task OpenSelectedRemoteEntryAsync()
    {
        await EnterRemoteEntryAsync(SelectedRemoteEntry);
    }

    private bool CanOpenSelectedRemoteEntry() => SelectedRemoteEntry?.IsDirectory == true;

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedRemoteItem))]
    private async Task DeleteSelectedRemoteItemAsync()
    {
        if (!await EnsureConnectedAsync())
        {
            return;
        }

        var targets = GetRemoteDeleteTargets();
        if (targets.Count == 0)
        {
            return;
        }

        var message = targets.Count == 1
            ? targets[0].IsDirectory
                ? $"Ta bort mappen {targets[0].DisplayName} och allt innehåll från Raspberry Pi?"
                : $"Ta bort filen {targets[0].DisplayName} från Raspberry Pi?"
            : $"Ta bort {targets.Count} objekt och allt innehåll från Raspberry Pi?";

        if (!await _dialogs.ConfirmAsync(AppPaths.AppTitle, message))
        {
            return;
        }

        IsDeletingRemoteItem = true;
        Status = targets.Count == 1
            ? $"Tar bort {targets[0].DisplayName}..."
            : $"Tar bort {targets.Count} objekt...";

        try
        {
            foreach (var (path, isDirectory, displayName) in targets)
            {
                await Task.Run(() => _sftpClient.DeleteRemotePath(path, isDirectory));

                var parentPath = PosixPath.DirectoryName(path);
                if (string.IsNullOrEmpty(parentPath))
                {
                    parentPath = "/";
                }

                if (IsRemotePathInsideOrEqual(RemotePath, path))
                {
                    RemotePath = parentPath;
                }

                TryRemoveRemoteTreeNode(path);
                InvalidateTreeNode(parentPath);
            }

            SelectedRemoteEntry = null;
            await RefreshRemoteDirsAsync();
            Status = targets.Count == 1
                ? $"Tog bort {targets[0].DisplayName}."
                : $"Tog bort {targets.Count} objekt.";
        }
        catch (Exception exc)
        {
            var displayName = targets.Count == 1 ? targets[0].DisplayName : "valda objekt";
            await ShowErrorAsync($"Kunde inte ta bort {displayName}: {ExceptionText(exc)}");
        }
        finally
        {
            IsDeletingRemoteItem = false;
        }
    }

    private bool CanDeleteSelectedRemoteItem() =>
        !IsDeletingRemoteItem && GetRemoteDeleteTargets().Count > 0;

    private List<(string Path, bool IsDirectory, string DisplayName)> GetRemoteDeleteTargets()
    {
        if (SelectedRemoteEntry is { } entry)
        {
            var path = RemotePathForEntry(entry);
            if (string.IsNullOrWhiteSpace(path) || path == "/")
            {
                return [];
            }

            return [(path, entry.IsDirectory, entry.DisplayName)];
        }

        var treeNodes = SelectedRemoteTreeNodes
            .Where(node => !node.IsPlaceholder)
            .ToList();
        if (treeNodes.Count == 0 && SelectedRemoteTreeNode is { IsPlaceholder: false } single)
        {
            treeNodes.Add(single);
        }

        var targets = new List<(string Path, bool IsDirectory, string DisplayName)>();
        foreach (var node in treeNodes)
        {
            if (node.FullPath == "/")
            {
                continue;
            }

            targets.Add((node.FullPath, true, $"{node.Name}/"));
        }

        return targets;
    }

    private void OnSelectedRemoteTreeNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        DeleteSelectedRemoteItemCommand.NotifyCanExecuteChanged();

    private static bool IsRemotePathInsideOrEqual(string currentPath, string targetPath)
    {
        var current = PosixPath.Normalize(string.IsNullOrWhiteSpace(currentPath) ? "." : currentPath.Trim());
        var target = PosixPath.Normalize(targetPath);
        return string.Equals(current, target, StringComparison.Ordinal)
            || current.StartsWith($"{target}/", StringComparison.Ordinal);
    }

    private bool TryRemoveRemoteTreeNode(string path)
    {
        var node = FindTreeNode(path);
        if (node is null || node.FullPath == "/")
        {
            return false;
        }

        var parentPath = PosixPath.DirectoryName(node.FullPath);
        if (string.IsNullOrEmpty(parentPath))
        {
            parentPath = "/";
        }

        var parent = FindTreeNode(parentPath);
        if (parent is null)
        {
            return false;
        }

        for (var i = parent.Children.Count - 1; i >= 0; i--)
        {
            var child = parent.Children[i];
            if (!child.IsPlaceholder && string.Equals(child.FullPath, path, StringComparison.Ordinal))
            {
                parent.Children.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    [RelayCommand]
    private async Task CreateRemoteFolderAsync()
    {
        var name = await _dialogs.AskTextAsync("Ny mapp", "Mappnamn:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (name.Contains('/') || name.Contains('\\'))
        {
            await _dialogs.ShowWarningAsync(AppPaths.AppTitle, "Ange bara ett mappnamn, inte en hel sökväg.");
            return;
        }

        var target = PosixPath.Join(string.IsNullOrWhiteSpace(RemotePath) ? "." : RemotePath.Trim(), name);
        try
        {
            await Task.Run(() => _sftpClient.MakeDirectory(target));
            Status = $"Skapade {target}.";
            InvalidateTreeNode(string.IsNullOrWhiteSpace(RemotePath) ? "." : RemotePath.Trim());
            await RefreshRemoteDirsAsync();
        }
        catch (Exception exc)
        {
            await ShowErrorAsync($"Kunde inte skapa mapp: {ExceptionText(exc)}");
        }
    }

    [RelayCommand]
    private async Task FindRemoteDuplicatesAsync()
    {
        var path = string.IsNullOrWhiteSpace(RemotePath) ? "." : RemotePath.Trim();
        IsDeletingDuplicates = true;
        Status = $"Söker dubbletter under {path}...";

        try
        {
            var (normalized, duplicates) = await Task.Run(() =>
            {
                var normalizedPath = _sftpClient.Normalize(path);
                return (normalizedPath, _sftpClient.FindDuplicateFiles(normalizedPath));
            });

            if (duplicates.Count == 0)
            {
                Status = $"Inga dubbletter hittades under {normalized}.";
                await _dialogs.ShowInfoAsync(AppPaths.AppTitle, "Inga dubbletter hittades.");
                return;
            }

            var root = normalized.TrimEnd('/');
            var examples = duplicates.Take(8)
                .Select(duplicate =>
                {
                    var prefix = $"{root}/";
                    return duplicate.StartsWith(prefix, StringComparison.Ordinal) ? duplicate[prefix.Length..] : duplicate;
                })
                .ToList();
            var more = duplicates.Count <= examples.Count ? "" : $"{Environment.NewLine}...och {duplicates.Count - examples.Count} till.";
            var confirmed = await _dialogs.ConfirmAsync(
                AppPaths.AppTitle,
                $"Ta bort dessa dubblettfiler från Raspberry Pi?{Environment.NewLine}{Environment.NewLine}Antal: {duplicates.Count}{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, examples)}{more}");

            if (confirmed)
            {
                await DeleteRemoteDuplicatesAsync(normalized, duplicates);
            }
            else
            {
                Status = "Borttagning av dubbletter avbröts.";
            }
        }
        catch (Exception exc)
        {
            await ShowErrorAsync($"Kunde inte söka dubbletter: {ExceptionText(exc)}");
        }
        finally
        {
            IsDeletingDuplicates = false;
        }
    }

    public async Task<bool> EnsureConnectedAsync()
    {
        if (_sftpClient.IsConnected)
        {
            return true;
        }

        var host = SelectPreferredHost();
        if (host is null)
        {
            await _dialogs.ShowWarningAsync(
                AppPaths.AppTitle,
                "Hittade ingen Raspberry Pi. Kontrollera att enheten är påslagen och att SSH är aktiverat.");
            return false;
        }

        SelectedHost = host;
        ApplySavedCredentialsForHost(host);

        if (string.IsNullOrEmpty(Password))
        {
            var entered = await _dialogs.AskPasswordAsync(
                "Anslut till Raspberry Pi",
                $"Lösenord för {Username}@{host.Address}:");
            if (string.IsNullOrEmpty(entered))
            {
                return false;
            }

            Password = entered;
        }

        await ConnectInternalAsync(showErrors: true);
        return _sftpClient.IsConnected;
    }

    private async Task OnRemoteFoldersPreparedAsync(string parentPath)
    {
        if (!_sftpClient.IsConnected)
        {
            return;
        }

        try
        {
            var normalizedParent = await Task.Run(() => _sftpClient.Normalize(parentPath));
            InvalidateTreeNode(normalizedParent);

            if (PathsReferToSameRemoteDirectory(RemotePath, normalizedParent))
            {
                await RefreshRemoteContentsOnlyAsync();
            }
            else
            {
                _ = RefreshRemoteTreeBranchInBackgroundAsync(normalizedParent);
            }
        }
        catch (Exception exc)
        {
            LogWriter.Write(AppPaths.TransferLogPath, $"Kunde inte uppdatera fjärrlistan efter mappskapande: {exc.Message}");
        }
    }

    private bool PathsReferToSameRemoteDirectory(string left, string right)
    {
        try
        {
            var normalizedLeft = _sftpClient.Normalize(left);
            var normalizedRight = _sftpClient.Normalize(right);
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public async Task TransferPathsAsync(IReadOnlyList<string> localPaths, string? remoteDestination = null)
    {
        if (localPaths.Count == 0)
        {
            return;
        }

        if (!await EnsureConnectedAsync())
        {
            return;
        }

        var destination = string.IsNullOrWhiteSpace(remoteDestination) ? RemotePath.Trim() : remoteDestination.Trim();
        if (string.IsNullOrWhiteSpace(destination))
        {
            await _dialogs.ShowWarningAsync(AppPaths.AppTitle, "Ingen destinationsmapp vald på Raspberry Pi.");
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsTransferring = true;
            ProgressValue = 0;
            ProgressText = "";
            TransferSpeedText = "";
            Status = "Förbereder överföring...";
        });

        try
        {
            await Task.Run(() => _sftpClient.UploadPaths(
                localPaths,
                destination,
                (done, total, filename, action) =>
                    Dispatcher.UIThread.Post(() => SetTransferProgress(done, total, filename, action)),
                message => Dispatcher.UIThread.Post(() => Status = message),
                update => Dispatcher.UIThread.Post(() => SetUploadProgress(update)),
                parentPath => Dispatcher.UIThread.Post(() => _ = OnRemoteFoldersPreparedAsync(parentPath))));

            await RefreshRemoteDirsAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ProgressValue = 100;
                Status = "Överföringen är klar.";
            });
            await Task.Delay(1500);
        }
        catch (Exception exc)
        {
            var detail = ExceptionText(exc);
            LogWriter.Write(AppPaths.TransferLogPath, $"Överföring misslyckades i GUI-worker: {detail}");
            LogWriter.Write(AppPaths.TransferLogPath, exc.ToString());
            await ShowErrorAsync($"Överföring misslyckades: {detail}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsTransferring = false;
                TransferSpeedText = "";
            });
        }
    }

    private HostCandidate? SelectPreferredHost()
    {
        if (Hosts.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_uiSettings.LastConnectedHostAddress))
        {
            var remembered = Hosts.FirstOrDefault(host =>
                string.Equals(host.Address, _uiSettings.LastConnectedHostAddress, StringComparison.OrdinalIgnoreCase));
            if (remembered is not null)
            {
                return remembered;
            }
        }

        return SelectedHost ?? Hosts.FirstOrDefault();
    }

    private async Task InitializeAsync()
    {
        var cached = _networkDiscovery.CachedHosts();
        if (cached.Count > 0)
        {
            SetHosts(cached, triggerAutoConnect: false);
        }

        await RefreshLocalDirsAsync();
        await TryConnectSilentlyAsync();
        await Task.Delay(350);
        await DiscoverHostsAsync();
    }

    private async Task DiscoverHostsAsync()
    {
        try
        {
            var hosts = await _networkDiscovery.DiscoverAsync(
                message => LogWriter.Write(AppPaths.DiscoveryLogPath, message));
            SetHosts(hosts);
            if (!_sftpClient.IsConnected)
            {
                await TryConnectSilentlyAsync();
            }
        }
        catch (Exception exc)
        {
            LogWriter.Write(AppPaths.DiscoveryLogPath, $"Upptäckt misslyckades: {exc.Message}");
        }
    }

    private void SetHosts(IReadOnlyList<HostCandidate> hosts, bool triggerAutoConnect = true)
    {
        var previousAddress = SelectedHost?.Address;

        Hosts.Clear();
        foreach (var host in hosts)
        {
            Hosts.Add(host);
        }

        if (Hosts.Count == 0)
        {
            return;
        }

        var target = Hosts.FirstOrDefault(host =>
                string.Equals(host.Address, _uiSettings.LastConnectedHostAddress, StringComparison.OrdinalIgnoreCase))
            ?? Hosts.FirstOrDefault(host => host.Address == previousAddress)
            ?? Hosts[0];

        if (SelectedHost?.Address != target.Address)
        {
            if (!triggerAutoConnect)
            {
                SetSelectedHostWithoutAutoConnect(target);
            }
            else
            {
                SelectedHost = target;
            }

            return;
        }

        ApplySavedCredentialsForHost(target);
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(ShowConnectionControls));
        if (triggerAutoConnect)
        {
            _ = TryConnectSilentlyAsync();
        }
    }

    private async Task DeleteRemoteDuplicatesAsync(string path, IReadOnlyList<string> duplicates)
    {
        IsDeletingDuplicates = true;
        Status = $"Tar bort {duplicates.Count} dubblettfil(er)...";

        try
        {
            var (deleted, failed) = await Task.Run(() => _sftpClient.DeleteFiles(duplicates));
            await RefreshRemoteDirsAsync();
            if (failed.Count > 0)
            {
                Status = $"Tog bort {deleted.Count} dubblettfil(er). {failed.Count} misslyckades.";
                await _dialogs.ShowWarningAsync(
                    AppPaths.AppTitle,
                    $"Tog bort {deleted.Count} dubblettfil(er), men {failed.Count} kunde inte tas bort. Se transfer.log för detaljer.");
            }
            else
            {
                Status = $"Tog bort {deleted.Count} dubblettfil(er) under {path}.";
                await _dialogs.ShowInfoAsync(AppPaths.AppTitle, $"Tog bort {deleted.Count} dubblettfil(er).");
            }
        }
        catch (Exception exc)
        {
            await ShowErrorAsync($"Kunde inte ta bort dubbletter: {ExceptionText(exc)}");
        }
        finally
        {
            IsDeletingDuplicates = false;
        }
    }

    private string ConnectedHostAddress()
    {
        return string.IsNullOrWhiteSpace(_connectedHost) ? SelectedHostAddress() : _connectedHost;
    }

    private string SelectedHostAddress()
    {
        return SelectedHost?.Address ?? string.Empty;
    }

    private HostCandidate? SelectedHostCandidate()
    {
        return SelectedHost;
    }

    private void ApplySavedCredentialsForHost(HostCandidate host)
    {
        if (!string.IsNullOrWhiteSpace(host.Username))
        {
            Username = host.Username;
        }
        else if (string.Equals(host.Address, _uiSettings.LastConnectedHostAddress, StringComparison.OrdinalIgnoreCase)
                 && !string.IsNullOrWhiteSpace(_uiSettings.LastConnectedUsername))
        {
            Username = _uiSettings.LastConnectedUsername;
        }
        else if (string.IsNullOrWhiteSpace(Username))
        {
            Username = "pi";
        }

        Password = CredentialStore.PasswordFor(host.Address);
    }

    private void ApplyRememberedSessionPreferences()
    {
        if (!string.IsNullOrWhiteSpace(_uiSettings.LastConnectedUsername))
        {
            Username = _uiSettings.LastConnectedUsername;
        }

        if (!string.IsNullOrWhiteSpace(_uiSettings.LastConnectedHostAddress))
        {
            var password = CredentialStore.PasswordFor(_uiSettings.LastConnectedHostAddress);
            if (!string.IsNullOrEmpty(password))
            {
                Password = password;
            }
        }
    }

    private void SetSelectedHostWithoutAutoConnect(HostCandidate host)
    {
        _suppressHostSelectionAutoConnect++;
        try
        {
            SelectedHost = host;
        }
        finally
        {
            _suppressHostSelectionAutoConnect--;
        }
    }

    private void SaveCredentialsForHost(string address, string username, string password)
    {
        if (!string.IsNullOrEmpty(password))
        {
            CredentialStore.SavePassword(address, password);
        }

        var updatedHosts = new List<HostCandidate>();
        var matched = false;
        foreach (var host in Hosts)
        {
            if (host.Address == address)
            {
                updatedHosts.Add(host with { Username = username, UseKey = false, KeyPath = "" });
                matched = true;
            }
            else
            {
                updatedHosts.Add(host);
            }
        }

        if (!matched)
        {
            updatedHosts.Add(new HostCandidate(address, address, "manuell", username));
        }

        _networkDiscovery.SaveCache(updatedHosts);
        SetHosts(_networkDiscovery.CachedHosts());
    }

    private void UpdateConnectionState()
    {
        IsPiConnected = _sftpClient.IsConnected;
    }

    private void SetTransferProgress(int done, int total, string filename, string action)
    {
        _transferDone = done;
        _transferTotal = total;
        ProgressText = total == 0 ? "" : $"{done}/{total}";

        if (total > 1)
        {
            ProgressValue = total == 0 ? 100 : (done / (double)total) * 100;
        }
        else if (action == "skipped")
        {
            ProgressValue = 100;
        }

        Status = action switch
        {
            "skipped" => $"Hoppar över redan överförd fil: {filename}",
            "renamed" => $"Skickar utan att skriva över: {filename}",
            _ => $"Skickar: {filename}"
        };
    }

    private void SetUploadProgress(UploadProgressUpdate update)
    {
        ProgressValue = update.Percent;

        var sizeProgress = update.TotalBytes > 0
            ? $"{TransferSpeedMeter.FormatSize((long)update.UploadedBytes)} / {TransferSpeedMeter.FormatSize(update.TotalBytes)}"
            : "";

        ProgressText = _transferTotal > 1 && !string.IsNullOrEmpty(sizeProgress)
            ? $"{_transferDone}/{_transferTotal} · {sizeProgress}"
            : sizeProgress;

        TransferSpeedText = TransferSpeedMeter.FormatBytesPerSecond(update.BytesPerSecond);

        var speedSuffix = string.IsNullOrEmpty(TransferSpeedText) ? "" : $" — {TransferSpeedText}";
        Status = update.Percent > 0
            ? $"{update.Label} ({update.Percent}%){speedSuffix}"
            : update.Label;
    }

    private async Task RefreshRemoteContentsOnlyAsync()
    {
        if (!_sftpClient.IsConnected)
        {
            return;
        }

        var path = string.IsNullOrWhiteSpace(RemotePath) ? "." : RemotePath.Trim();
        var generation = Interlocked.Increment(ref _remoteContentsRefreshGeneration);
        Status = $"Läser {path}...";

        try
        {
            var (normalized, entries) = await Task.Run(() =>
            {
                var normalizedPath = _sftpClient.Normalize(path);
                return (normalizedPath, _sftpClient.ListEntries(normalizedPath));
            });

            if (generation != _remoteContentsRefreshGeneration)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _remoteContentsRefreshGeneration)
                {
                    return;
                }

                RemotePath = normalized;
                ApplyRemoteEntries(entries);

                var dirCount = RemoteEntries.Count(entry => entry.IsDirectory);
                var fileCount = RemoteEntries.Count - dirCount;
                Status = $"Visar {RemotePath}. {dirCount} mappar, {fileCount} filer.";
            });
        }
        catch (Exception exc)
        {
            await ShowErrorAsync($"Kunde inte läsa destinationen: {ExceptionText(exc)}");
        }
    }

    private void ApplyRemoteEntries(IReadOnlyList<RemoteEntry> entries)
    {
        RemoteEntries.Clear();
        foreach (var entry in entries)
        {
            RemoteEntries.Add(entry);
        }
    }

    private void InitializeRemoteTreeRoot()
    {
        RemoteTreeRoots.Clear();
        var root = CreateTreeNode("/", "/");
        RemoteTreeRoots.Add(root);
        root.IsExpanded = true;
        UpdateRemoteTreePaneWidth();
    }

    private RemoteTreeNode CreateTreeNode(string name, string fullPath)
    {
        var node = new RemoteTreeNode(name, fullPath);
        node.ExpandHandler = EnsureTreeNodeChildrenAsync;
        node.LayoutChangedHandler = ScheduleRemoteTreeLayoutUpdate;
        return node;
    }

    private async Task EnsureTreeNodeChildrenAsync(RemoteTreeNode node)
    {
        if (node.IsLoaded || node.IsPlaceholder || !_sftpClient.IsConnected)
        {
            return;
        }

        if (TryPopulateRemoteTreeNodeFromCache(node))
        {
            node.IsLoaded = true;
            ScheduleRemoteTreeLayoutUpdate();
            return;
        }

        await LoadRemoteTreeNodeFromSftpAsync(node);
    }

    private bool TryPopulateRemoteTreeNodeFromCache(RemoteTreeNode node)
    {
        var cached = _remoteTreeCache.TryGetSubdirectories(node.FullPath);
        if (cached is null)
        {
            return false;
        }

        ApplyRemoteTreeChildren(node, cached);
        return true;
    }

    private async Task LoadRemoteTreeNodeFromSftpAsync(RemoteTreeNode node)
    {
        try
        {
            var directories = await Task.Run(() => _sftpClient.ListSubdirectories(node.FullPath));
            ApplyRemoteTreeChildren(node, directories);
            node.IsLoaded = true;
            _remoteTreeCache.SetSubdirectories(node.FullPath, directories);
            _remoteTreeCache.Save();
            ScheduleRemoteTreeLayoutUpdate();
        }
        catch (Exception exc)
        {
            await ShowErrorAsync($"Kunde inte läsa mappträd: {ExceptionText(exc)}");
        }
    }

    private void ApplyRemoteTreeChildren(RemoteTreeNode node, IReadOnlyList<string> directories)
    {
        var existingByName = node.Children
            .Where(child => !child.IsPlaceholder)
            .ToDictionary(child => child.Name, StringComparer.OrdinalIgnoreCase);

        var desiredNames = directories.ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = node.Children.Count - 1; i >= 0; i--)
        {
            var child = node.Children[i];
            if (child.IsPlaceholder || !desiredNames.Contains(child.Name))
            {
                node.Children.RemoveAt(i);
            }
        }

        foreach (var directory in directories)
        {
            if (!existingByName.ContainsKey(directory))
            {
                var childPath = PosixPath.Join(node.FullPath, directory);
                node.Children.Add(CreateTreeNode(directory, childPath));
            }
        }
    }

    private async Task RefreshRemoteTreeBranchInBackgroundAsync(string path)
    {
        if (!_sftpClient.IsConnected || RemoteTreeRoots.Count == 0)
        {
            return;
        }

        var nodes = CollectRemoteTreeNodesOnPath(path);
        if (nodes.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(nodes.Select(async node =>
            {
                try
                {
                    var directories = await Task.Run(() => _sftpClient.ListSubdirectories(node.FullPath));
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ApplyRemoteTreeChildren(node, directories);
                        node.IsLoaded = true;
                    });
                    _remoteTreeCache.SetSubdirectories(node.FullPath, directories);
                }
                catch (Exception exc)
                {
                    LogWriter.Write(AppPaths.TransferLogPath, $"Bakgrundssynk av {node.FullPath} misslyckades: {exc.Message}");
                }
            }));

            _remoteTreeCache.Save();
            ScheduleRemoteTreeLayoutUpdate();
        }
        catch (Exception exc)
        {
            LogWriter.Write(AppPaths.TransferLogPath, $"Bakgrundssynk av träd misslyckades: {exc.Message}");
        }
    }

    private List<RemoteTreeNode> CollectRemoteTreeNodesOnPath(string path)
    {
        var nodes = new List<RemoteTreeNode>();
        if (RemoteTreeRoots.Count == 0)
        {
            return nodes;
        }

        var normalized = PosixPath.Normalize(path);
        var current = RemoteTreeRoots[0];
        nodes.Add(current);

        var builtPath = current.FullPath;
        foreach (var segment in PathSegments(normalized))
        {
            builtPath = builtPath == "/"
                ? $"/{segment}"
                : PosixPath.Join(builtPath, segment);

            var child = current.Children.FirstOrDefault(candidate =>
                !candidate.IsPlaceholder &&
                string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (child is null)
            {
                break;
            }

            current = child;
            nodes.Add(current);
        }

        return nodes;
    }

    private void ScheduleRemoteTreeLayoutUpdate()
    {
        Dispatcher.UIThread.Post(ApplyMeasuredTreeWidth);
        Dispatcher.UIThread.Post(ApplyMeasuredTreeWidth, DispatcherPriority.Loaded);
    }

    private void UpdateRemoteTreePaneWidth()
    {
        ApplyMeasuredTreeWidth();
        RemoteTreeLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyMeasuredTreeWidth()
    {
        var needed = RemoteTreeLayoutCalculator.MeasurePaneWidth(
            RemoteTreeRoots,
            RemoteTreeMinPaneWidth,
            RemoteTreeMaxPaneWidth);

        RemoteTreePaneWidth = needed;
        var targetWidth = Math.Clamp(Math.Max(_userPreferredRemoteTreeWidth, needed), RemoteTreeMinPaneWidth, RemoteTreeMaxPaneWidth);
        if (Math.Abs(RemoteTreeColumnWidth.Value - targetWidth) > 0.5)
        {
            RemoteTreeColumnWidth = new GridLength(targetWidth, GridUnitType.Pixel);
        }
    }

    private async Task SyncTreeSelectionToPathAsync(string path)
    {
        if (RemoteTreeRoots.Count == 0)
        {
            return;
        }

        var normalized = PosixPath.Normalize(path);
        var current = RemoteTreeRoots[0];
        current.IsExpanded = true;
        await EnsureTreeNodeChildrenAsync(current);

        var builtPath = current.FullPath;
        foreach (var segment in PathSegments(normalized))
        {
            builtPath = builtPath == "/"
                ? $"/{segment}"
                : PosixPath.Join(builtPath, segment);

            var child = current.Children.FirstOrDefault(candidate =>
                !candidate.IsPlaceholder &&
                string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));

            if (child is null)
            {
                child = CreateTreeNode(segment, builtPath);
                current.Children.Add(child);
            }

            current = child;
            current.IsExpanded = true;
            await EnsureTreeNodeChildrenAsync(current);
        }

        var selected = string.Equals(current.FullPath, normalized, StringComparison.Ordinal)
            ? current
            : FindTreeNode(normalized) ?? current;

        WithSuppressedTreeSelectionNavigation(() =>
        {
            SelectedRemoteTreeNode = selected;
        });
        UpdateRemoteTreePaneWidth();
    }

    private void InvalidateTreeNode(string path)
    {
        var normalized = PosixPath.Normalize(path);
        var node = FindTreeNode(normalized);
        if (node is null)
        {
            return;
        }

        node.IsLoaded = false;
        node.Children.Clear();
        node.Children.Add(RemoteTreeNode.CreatePlaceholder());
        _remoteTreeCache.Invalidate(PosixPath.Normalize(path));
        _remoteTreeCache.Save();
    }

    private RemoteTreeNode? FindTreeNode(string path)
    {
        var normalized = PosixPath.Normalize(path);
        foreach (var root in RemoteTreeRoots)
        {
            var found = FindTreeNodeRecursive(root, normalized);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static RemoteTreeNode? FindTreeNodeRecursive(RemoteTreeNode node, string path)
    {
        if (node.IsPlaceholder)
        {
            return null;
        }

        if (string.Equals(node.FullPath, path, StringComparison.Ordinal))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var found = FindTreeNodeRecursive(child, path);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static IEnumerable<string> PathSegments(string normalizedPath)
    {
        if (normalizedPath == "/")
        {
            yield break;
        }

        foreach (var segment in normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return segment;
        }
    }

    private async Task RefreshLocalContentsOnlyAsync()
    {
        var path = string.IsNullOrWhiteSpace(LocalPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : LocalPath.Trim();
        var generation = Interlocked.Increment(ref _localContentsRefreshGeneration);
        Status = $"Läser {path}...";

        try
        {
            var (normalized, entries) = await Task.Run(() =>
            {
                var normalizedPath = _localFileSystem.Normalize(path);
                return (normalizedPath, _localFileSystem.ListEntries(normalizedPath));
            });

            if (generation != _localContentsRefreshGeneration)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _localContentsRefreshGeneration)
                {
                    return;
                }

                LocalPath = normalized;
                ApplyLocalEntries(entries);

                var dirCount = LocalEntries.Count(entry => entry.IsDirectory);
                var fileCount = LocalEntries.Count - dirCount;
                Status = $"Visar {LocalPath}. {dirCount} mappar, {fileCount} filer.";
            });
        }
        catch (Exception exc)
        {
            await ShowErrorAsync($"Kunde inte läsa lokala filer: {ExceptionText(exc)}");
        }
    }

    private void ApplyLocalEntries(IReadOnlyList<LocalEntry> entries)
    {
        LocalEntries.Clear();
        foreach (var entry in entries)
        {
            LocalEntries.Add(entry);
        }
    }

    private void InitializeLocalTreeRoot()
    {
        LocalTreeRoots.Clear();
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
        {
            var path = drive.RootDirectory.FullName;
            var name = drive.Name.TrimEnd('\\');
            if (string.IsNullOrEmpty(name))
            {
                name = path;
            }

            LocalTreeRoots.Add(CreateLocalTreeNode(name, path));
        }

        UpdateLocalTreePaneWidth();
    }

    private LocalTreeNode CreateLocalTreeNode(string name, string fullPath)
    {
        var node = new LocalTreeNode(name, fullPath);
        node.ExpandHandler = EnsureLocalTreeNodeChildrenAsync;
        node.LayoutChangedHandler = ScheduleLocalTreeLayoutUpdate;
        return node;
    }

    private async Task EnsureLocalTreeNodeChildrenAsync(LocalTreeNode node)
    {
        if (node.IsLoaded || node.IsPlaceholder)
        {
            return;
        }

        try
        {
            var directories = await Task.Run(() => _localFileSystem.ListSubdirectories(node.FullPath));
            node.Children.Clear();
            foreach (var directory in directories)
            {
                var childPath = Path.Combine(node.FullPath, directory);
                node.Children.Add(CreateLocalTreeNode(directory, childPath));
            }

            node.IsLoaded = true;
            ScheduleLocalTreeLayoutUpdate();
        }
        catch (Exception exc)
        {
            await ShowErrorAsync($"Kunde inte läsa lokalt mappträd: {ExceptionText(exc)}");
        }
    }

    private void ScheduleLocalTreeLayoutUpdate()
    {
        Dispatcher.UIThread.Post(ApplyMeasuredLocalTreeWidth);
        Dispatcher.UIThread.Post(ApplyMeasuredLocalTreeWidth, DispatcherPriority.Loaded);
    }

    private void UpdateLocalTreePaneWidth()
    {
        ApplyMeasuredLocalTreeWidth();
        LocalTreeLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyMeasuredLocalTreeWidth()
    {
        var needed = LocalTreeLayoutCalculator.MeasurePaneWidth(
            LocalTreeRoots,
            LocalTreeMinPaneWidth,
            LocalTreeMaxPaneWidth);

        LocalTreePaneWidth = needed;
        var targetWidth = Math.Clamp(Math.Max(_userPreferredLocalTreeWidth, needed), LocalTreeMinPaneWidth, LocalTreeMaxPaneWidth);
        if (Math.Abs(LocalTreeColumnWidth.Value - targetWidth) > 0.5)
        {
            LocalTreeColumnWidth = new GridLength(targetWidth, GridUnitType.Pixel);
        }
    }

    private async Task SyncLocalTreeSelectionToPathAsync(string path)
    {
        if (LocalTreeRoots.Count == 0)
        {
            return;
        }

        var normalized = _localFileSystem.Normalize(path);
        var rootPath = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(rootPath))
        {
            return;
        }

        var normalizedRoot = _localFileSystem.Normalize(rootPath);
        var current = LocalTreeRoots.FirstOrDefault(root =>
            string.Equals(_localFileSystem.Normalize(root.FullPath), normalizedRoot, StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            return;
        }

        current.IsExpanded = true;
        await EnsureLocalTreeNodeChildrenAsync(current);

        foreach (var segment in LocalPathSegments(normalized))
        {
            var child = current.Children.FirstOrDefault(candidate =>
                !candidate.IsPlaceholder &&
                string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));

            var childPath = Path.Combine(current.FullPath, segment);
            if (child is null)
            {
                child = CreateLocalTreeNode(segment, childPath);
                current.Children.Add(child);
            }

            current = child;
            current.IsExpanded = true;
            await EnsureLocalTreeNodeChildrenAsync(current);
        }

        var selected = string.Equals(_localFileSystem.Normalize(current.FullPath), normalized, StringComparison.OrdinalIgnoreCase)
            ? current
            : FindLocalTreeNode(normalized) ?? current;

        WithSuppressedLocalTreeSelectionNavigation(() =>
        {
            SelectedLocalTreeNode = selected;
        });
        UpdateLocalTreePaneWidth();
    }

    private void InvalidateLocalTreeNode(string path)
    {
        var node = FindLocalTreeNode(_localFileSystem.Normalize(path));
        if (node is null)
        {
            return;
        }

        node.IsLoaded = false;
        node.Children.Clear();
        node.Children.Add(LocalTreeNode.CreatePlaceholder());
    }

    private LocalTreeNode? FindLocalTreeNode(string path)
    {
        var normalized = _localFileSystem.Normalize(path);
        foreach (var root in LocalTreeRoots)
        {
            var found = FindLocalTreeNodeRecursive(root, normalized);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static LocalTreeNode? FindLocalTreeNodeRecursive(LocalTreeNode node, string path)
    {
        if (node.IsPlaceholder)
        {
            return null;
        }

        if (string.Equals(Path.GetFullPath(node.FullPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var found = FindLocalTreeNodeRecursive(child, path);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static IEnumerable<string> LocalPathSegments(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath);
        var root = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(root))
        {
            yield break;
        }

        var remainder = normalized[root.Length..].TrimStart('\\', '/');
        if (string.IsNullOrEmpty(remainder))
        {
            yield break;
        }

        foreach (var segment in remainder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            yield return segment;
        }
    }

    private async Task ShowErrorAsync(string errorText)
    {
        var message = string.IsNullOrWhiteSpace(errorText) ? "Ett okänt fel inträffade. Se transfer.log." : errorText;
        Status = message;
        await _dialogs.ShowErrorAsync(AppPaths.AppTitle, message);
    }

    private static string ExceptionText(Exception exc)
    {
        return string.IsNullOrWhiteSpace(exc.Message) ? exc.GetType().Name : exc.Message.Trim();
    }

    public void Dispose()
    {
        SaveUiSettings();
        _sftpClient.Dispose();
    }

    private sealed class NullDialogService : IDialogService
    {
        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;
        public Task ShowWarningAsync(string title, string message) => Task.CompletedTask;
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<string?> AskTextAsync(string title, string prompt) => Task.FromResult<string?>(null);
        public Task<string?> AskPasswordAsync(string title, string prompt) => Task.FromResult<string?>(null);
    }
}
