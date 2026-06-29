using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileTransferHelper.Models;
using FileTransferHelper.Services;
using Renci.SshNet.Common;

namespace FileTransferHelper.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly NetworkDiscoveryService _networkDiscovery;
    private readonly SftpTransferClient _sftpClient;
    private readonly Dictionary<string, SftpTransferClient> _connectedSftpClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _remoteHomePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedLocalDirectory> _localDirectoryCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedRemoteDirectory> _remoteDirectoryCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConnectionInputSnapshot> _connectionInputSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly LocalFileSystemClient _localFileSystem;
    private readonly UiSettingsStore _uiSettingsStore;
    private readonly RemoteTreeCacheStore _remoteTreeCache;
    private readonly IDialogService _dialogs;
    private UiSettings _uiSettings = new();
    private string _connectedHost = "";
    private double _userPreferredLocalTreeWidth = 120;
    private double _userPreferredRemoteTreeWidth = 120;
    private CancellationTokenSource? _transferCancellation;
    private readonly HashSet<string> _connectingHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _autoReconnectCancellations = new(StringComparer.OrdinalIgnoreCase);
    private bool _canPersistDeviceSelection;

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
    private string _leftStatus = "Vänster: redo.";

    [ObservableProperty]
    private string _rightStatus = "Höger: redo.";

    [ObservableProperty]
    private string _leftStorageStatus = "";

    [ObservableProperty]
    private string _rightStorageStatus = "";

    [ObservableProperty]
    private bool _isConnectionPanelExpanded;

    [ObservableProperty]
    private bool _isConnectionSecretsVisible;

    [ObservableProperty]
    private bool _isConnectionAddressVisible = true;

    [ObservableProperty]
    private bool _isConnectionUsernameVisible;

    [ObservableProperty]
    private bool _isConnectionPasswordVisible;

    private bool _suppressConnectionVisibilitySync;

    partial void OnIsConnectionSecretsVisibleChanged(bool value)
    {
        if (_suppressConnectionVisibilitySync)
        {
            return;
        }

        _suppressConnectionVisibilitySync = true;
        IsConnectionAddressVisible = value;
        IsConnectionUsernameVisible = value;
        IsConnectionPasswordVisible = value;
        _suppressConnectionVisibilitySync = false;
        PropagateConnectionVisibilityToEntries();
    }

    partial void OnIsConnectionAddressVisibleChanged(bool value)
    {
        OnConnectionColumnVisibilityChanged();
    }

    partial void OnIsConnectionUsernameVisibleChanged(bool value)
    {
        OnConnectionColumnVisibilityChanged();
    }

    partial void OnIsConnectionPasswordVisibleChanged(bool value)
    {
        OnConnectionColumnVisibilityChanged();
    }

    private void OnConnectionColumnVisibilityChanged()
    {
        if (!_suppressConnectionVisibilitySync)
        {
            _suppressConnectionVisibilitySync = true;
            IsConnectionSecretsVisible = IsConnectionAddressVisible
                                         && IsConnectionUsernameVisible
                                         && IsConnectionPasswordVisible;
            _suppressConnectionVisibilitySync = false;
        }

        PropagateConnectionVisibilityToEntries();
    }

    private void PropagateConnectionVisibilityToEntries()
    {
        foreach (var entry in ConnectionManagerEntries)
        {
            entry.IsAddressVisible = IsConnectionAddressVisible;
            entry.IsUsernameVisible = IsConnectionUsernameVisible;
            entry.IsPasswordVisible = IsConnectionPasswordVisible;
        }
    }

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _transferSpeedText = "";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isConnecting;

    partial void OnIsConnectingChanged(bool value)
    {
        RefreshConnectionManagerEntries();
    }

    [ObservableProperty]
    private bool _isPiConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelTransferCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedLocalToRemoteCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveSelectedLocalToRemoteCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedRemoteToLocalCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveSelectedRemoteToLocalCommand))]
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
    private bool _autoConnecting;

    partial void OnAutoConnectingChanged(bool value)
    {
        RefreshConnectionManagerEntries();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedRemoteEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedRemoteItemCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedRemoteToLocalCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveSelectedRemoteToLocalCommand))]
    private RemoteEntry? _selectedRemoteEntry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedRemoteItemCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedRemoteToLocalCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveSelectedRemoteToLocalCommand))]
    private RemoteTreeNode? _selectedRemoteTreeNode;

    public ObservableCollection<RemoteEntry> SelectedRemoteEntries { get; } = [];

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
    [NotifyCanExecuteChangedFor(nameof(CopySelectedLocalToRemoteCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveSelectedLocalToRemoteCommand))]
    private LocalEntry? _selectedLocalEntry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedLocalItemCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedLocalToRemoteCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveSelectedLocalToRemoteCommand))]
    private LocalTreeNode? _selectedLocalTreeNode;

    public ObservableCollection<LocalEntry> SelectedLocalEntries { get; } = [];

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
    private const string LocalDeviceId = "local";

    public event EventHandler? RemoteTreeLayoutChanged;
    public event EventHandler? LocalTreeLayoutChanged;

    public void RequestRemoteTreeLayoutUpdate() => ScheduleRemoteTreeLayoutUpdate();
    public void RequestLocalTreeLayoutUpdate() => ScheduleLocalTreeLayoutUpdate();

    public ObservableCollection<HostCandidate> Hosts { get; } = [];
    public ObservableCollection<ConnectedDevice> ConnectedDevices { get; } = [];
    public ObservableCollection<ConnectionManagerEntry> ConnectionManagerEntries { get; } = [];
    public ObservableCollection<LocalEntry> LocalEntries { get; } = [];
    public ObservableCollection<LocalTreeNode> LocalTreeRoots { get; } = [];
    public ObservableCollection<RemoteEntry> RemoteEntries { get; } = [];
    public ObservableCollection<RemoteTreeNode> RemoteTreeRoots { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftPanelTitle))]
    private ConnectedDevice? _selectedLeftDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightPanelTitle))]
    private ConnectedDevice? _selectedRightDevice;

    public string LeftPanelTitle => SelectedLeftDevice?.IsLocal == false
        ? $"Filer från {SelectedLeftDevice.Name}"
        : "Filer från den här datorn";

    public string RightPanelTitle => SelectedRightDevice?.IsLocal == true
        ? "Destination på den här datorn"
        : SelectedRightDevice is null
            ? "Destination"
            : $"Destination på {SelectedRightDevice.Name}";

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
        var localDevice = new ConnectedDevice(LocalDeviceId, "Den här datorn", Environment.MachineName, true);
        ConnectedDevices.Add(localDevice);
        SelectedLeftDevice = localDevice;
        SelectedRightDevice = localDevice;
        LocalPath = string.IsNullOrWhiteSpace(_uiSettings.LastLocalPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : _uiSettings.LastLocalPath;
        ApplyRememberedSessionPreferences();
        InitializeLocalTreeRoot();
        SelectedLocalEntries.CollectionChanged += OnSelectedLocalEntriesChanged;
        SelectedLocalTreeNodes.CollectionChanged += OnSelectedLocalTreeNodesChanged;
        SelectedRemoteEntries.CollectionChanged += OnSelectedRemoteEntriesChanged;
        SelectedRemoteTreeNodes.CollectionChanged += OnSelectedRemoteTreeNodesChanged;
        _canPersistDeviceSelection = true;
        _ = InitializeAsync();
    }

    public bool IsLocalTreeSelectionNavigationSuppressed => _suppressLocalTreeSelectionNavigation > 0;

    public bool IsRemoteTreeSelectionNavigationSuppressed => _suppressTreeSelectionNavigation > 0;

    public string RemotePathForEntry(RemoteEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.FullPath))
        {
            return entry.FullPath;
        }

        return SelectedRightDevice?.IsLocal == true
            ? Path.Combine(string.IsNullOrWhiteSpace(RemotePath) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : RemotePath.Trim(), entry.Name)
            : PosixPath.Join(string.IsNullOrWhiteSpace(RemotePath) ? "." : RemotePath.Trim(), entry.Name);
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
        if (SelectedLeftDevice?.IsLocal == false)
        {
            _uiSettings.LastRemotePathsByHost[SelectedLeftDevice.Id] = LocalPath;
        }
        else
        {
            _uiSettings.LastLocalPath = LocalPath;
        }

        _uiSettings.LastLeftDeviceId = SelectedLeftDevice?.Id ?? "";
        _uiSettings.LastRightDeviceId = SelectedRightDevice?.Id ?? "";
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

        if (SelectedLeftDevice?.IsLocal == false)
        {
            _uiSettings.LastRemotePathsByHost[SelectedLeftDevice.Id] = value;
        }
        else
        {
            _uiSettings.LastLocalPath = value;
        }

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

    partial void OnSelectedLeftDeviceChanged(ConnectedDevice? value)
    {
        if (_canPersistDeviceSelection && value is not null)
        {
            _uiSettings.LastLeftDeviceId = value.Id;
            _uiSettingsStore.Save(_uiSettings);
        }

        SelectedLocalEntry = null;
        SelectedLocalEntries.Clear();
        SelectedLocalTreeNode = null;
        SelectedLocalTreeNodes.Clear();
        InitializeLocalTreeRoot();
        if (value?.IsLocal == false && TryGetSftpClient(value.Id, out var client))
        {
            LocalPath = _uiSettings.LastRemotePathsByHost.TryGetValue(value.Id, out var remembered) && !string.IsNullOrWhiteSpace(remembered)
                ? NormalizeRememberedRemotePath(
                    client,
                    remembered,
                    _remoteHomePaths.TryGetValue(value.Id, out var rememberedHome) ? rememberedHome : client.Normalize("."))
                : _remoteHomePaths.TryGetValue(value.Id, out var home)
                    ? home
                    : client.Normalize(".");
        }
        else if (string.IsNullOrWhiteSpace(LocalPath) || LocalPath.StartsWith("/", StringComparison.Ordinal))
        {
            LocalPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        _ = RefreshLocalDirsAsync();
        OnPropertyChanged(nameof(LeftPanelTitle));
    }

    partial void OnSelectedRightDeviceChanged(ConnectedDevice? value)
    {
        if (_canPersistDeviceSelection && value is not null)
        {
            _uiSettings.LastRightDeviceId = value.Id;
            _uiSettingsStore.Save(_uiSettings);
        }

        SelectedRemoteEntry = null;
        SelectedRemoteEntries.Clear();
        SelectedRemoteTreeNode = null;
        SelectedRemoteTreeNodes.Clear();
        RemoteTreeRoots.Clear();
        if (value?.IsLocal == true)
        {
            RemotePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (value is not null && TryGetSftpClient(value.Id, out var client))
        {
            _remoteTreeCache.BindHost(value.Id);
            RemotePath = _uiSettings.LastRemotePathsByHost.TryGetValue(value.Id, out var remembered) && !string.IsNullOrWhiteSpace(remembered)
                ? NormalizeRememberedRemotePath(
                    client,
                    remembered,
                    _remoteHomePaths.TryGetValue(value.Id, out var rememberedHome) ? rememberedHome : client.Normalize("."))
                : _remoteHomePaths.TryGetValue(value.Id, out var home)
                    ? home
                    : client.Normalize(".");
        }

        _ = RefreshRemoteDirsAsync();
        OnPropertyChanged(nameof(RightPanelTitle));
    }

    partial void OnSelectedLocalEntryChanged(LocalEntry? value)
    {
        _ = UpdateSelectedLocalStorageStatusAsync();
    }

    partial void OnSelectedLocalTreeNodeChanged(LocalTreeNode? value)
    {
        _ = UpdateSelectedLocalStorageStatusAsync();
    }

    partial void OnSelectedRemoteEntryChanged(RemoteEntry? value)
    {
        _ = UpdateSelectedRemoteStorageStatusAsync();
    }

    partial void OnSelectedRemoteTreeNodeChanged(RemoteTreeNode? value)
    {
        _ = UpdateSelectedRemoteStorageStatusAsync();
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
        var targetPath = LeftSftpClient() is not null ? PosixPath.Normalize(node.FullPath) : _localFileSystem.Normalize(node.FullPath);
        var currentPath = LeftSftpClient() is not null ? PosixPath.Normalize(LocalPath) : _localFileSystem.Normalize(LocalPath);
        var comparison = LeftSftpClient() is not null ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.Equals(targetPath, currentPath, comparison))
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
            return;
        }

        ApplySavedCredentialsForHost(value);
        if (_suppressHostSelectionAutoConnect == 0)
        {
            _ = TryConnectSilentlyAsync();
        }
    }

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
        var targetPath = SelectedRightDevice?.IsLocal == true ? _localFileSystem.Normalize(node.FullPath) : PosixPath.Normalize(node.FullPath);
        var currentPath = SelectedRightDevice?.IsLocal == true ? _localFileSystem.Normalize(RemotePath) : PosixPath.Normalize(RemotePath);
        var comparison = SelectedRightDevice?.IsLocal == true ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(targetPath, currentPath, comparison))
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
        CancelAutoReconnect(SelectedHostAddress());
        await ConnectInternalAsync(showErrors: true);
    }

    [RelayCommand]
    private async Task ConnectConnectionEntryAsync(ConnectionManagerEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        var host = Hosts.FirstOrDefault(candidate =>
            string.Equals(candidate.Address, entry.Address, StringComparison.OrdinalIgnoreCase));
        if (host is null)
        {
            host = new HostCandidate(entry.Name, entry.Address, entry.Source, entry.UsernameInput);
            Hosts.Add(host);
            RefreshConnectionManagerEntries();
        }

        SetSelectedHostWithoutAutoConnect(host);
        Username = entry.UsernameInput.Trim();
        Password = entry.PasswordInput;
        CancelAutoReconnect(host.Address);
        await ConnectInternalAsync(showErrors: true);
    }

    private bool CanConnect() => !IsConnecting;

    public async Task TryConnectSilentlyAsync()
    {
        if (IsConnecting)
        {
            return;
        }

        EnsureRememberedHostsInHosts();
        var rememberedHosts = RememberedAutoConnectHosts()
            .Select(address => Hosts.FirstOrDefault(host => string.Equals(host.Address, address, StringComparison.OrdinalIgnoreCase))
                               ?? new HostCandidate(address, address, "sparad", RememberedUsernameForHost(address)))
            .ToList();

        if (rememberedHosts.Count == 0)
        {
            return;
        }

        foreach (var host in rememberedHosts)
        {
            if (IsHostConnected(host.Address) || _connectingHosts.Contains(host.Address))
            {
                continue;
            }

            var username = UsernameForHost(host);
            var password = CredentialStore.PasswordFor(host.Address);
            if (string.IsNullOrEmpty(password))
            {
                RefreshConnectionManagerEntries();
                continue;
            }

            var reconnectCancellation = new CancellationTokenSource();
            _autoReconnectCancellations[host.Address] = reconnectCancellation;
            _ = ConnectHostInternalAsync(
                host,
                username,
                password,
                showErrors: false,
                keepRetrying: true,
                cancellationToken: reconnectCancellation.Token,
                restoreFallbackToRight: false,
                updateGlobalConnecting: false);
        }
    }

    private async Task<bool> ConnectInternalAsync(
        bool showErrors,
        bool keepRetrying = false,
        CancellationToken cancellationToken = default)
    {
        var host = SelectedHostAddress();
        var username = Username.Trim();
        var password = Password;
        var candidate = SelectedHostCandidate() ?? new HostCandidate(host, host, "manuell", username);
        return await ConnectHostInternalAsync(
            candidate,
            username,
            password,
            showErrors,
            keepRetrying,
            cancellationToken,
            restoreFallbackToRight: true,
            updateGlobalConnecting: true);
    }

    private async Task<bool> ConnectHostInternalAsync(
        HostCandidate hostCandidate,
        string username,
        string password,
        bool showErrors,
        bool keepRetrying = false,
        CancellationToken cancellationToken = default,
        bool restoreFallbackToRight = false,
        bool updateGlobalConnecting = false)
    {
        var host = hostCandidate.Address;
        username = username.Trim();

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

        if (updateGlobalConnecting)
        {
            IsConnecting = true;
        }

        _connectingHosts.Add(host);
        AutoConnecting = _autoReconnectCancellations.Count > 0;
        Status = $"Ansluter till {host}...";
        RefreshConnectionManagerEntries();

        try
        {
            var (client, home) = await ConnectWithRetryAsync(host, username, password, showErrors, keepRetrying, cancellationToken);
            _connectedSftpClients[host] = client;
            _remoteHomePaths[host] = home;
            var deviceName = hostCandidate.Name;
            var connectedDevice = ConnectedDevices.FirstOrDefault(device =>
                !device.IsLocal && string.Equals(device.Id, host, StringComparison.OrdinalIgnoreCase));
            if (connectedDevice is null)
            {
                connectedDevice = new ConnectedDevice(host, deviceName, host, false);
                ConnectedDevices.Add(connectedDevice);
            }
            RefreshConnectionManagerEntries();
            SaveCredentialsForHost(host, username, password);
            _connectedHost = host;
            _uiSettings.LastConnectedHostAddress = host;
            _uiSettings.LastConnectedUsername = username;
            RememberAutoConnectHost(host, username);
            _uiSettingsStore.Save(_uiSettings);
            var savedPath = _uiSettings.LastRemotePathsByHost.TryGetValue(host, out var remembered) && !string.IsNullOrWhiteSpace(remembered)
                ? NormalizeRememberedRemotePath(client, remembered, home)
                : home;
            RestoreConnectedDeviceSelection(connectedDevice, restoreFallbackToRight);
            var refreshedPanel = false;
            if (SelectedRightDevice?.IsLocal == false && string.Equals(SelectedRightDevice.Id, host, StringComparison.OrdinalIgnoreCase))
            {
                _remoteTreeCache.BindHost(host);
                RemoteTreeRoots.Clear();
                RemotePath = savedPath;
                await RefreshRemoteDirsAsync();
                refreshedPanel = true;
            }

            if (SelectedLeftDevice?.IsLocal == false && string.Equals(SelectedLeftDevice.Id, host, StringComparison.OrdinalIgnoreCase))
            {
                LocalTreeRoots.Clear();
                LocalPath = savedPath;
                await RefreshLocalDirsAsync();
                refreshedPanel = true;
            }

            if (!refreshedPanel && restoreFallbackToRight)
            {
                _remoteTreeCache.BindHost(host);
                RemoteTreeRoots.Clear();
                RemotePath = savedPath;
                SelectedRightDevice = connectedDevice;
                await RefreshRemoteDirsAsync();
            }

            Status = "Ansluten.";
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
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
            _connectingHosts.Remove(host);
            CancelAutoReconnect(host, cancelToken: false);
            AutoConnecting = _autoReconnectCancellations.Count > 0;
            if (updateGlobalConnecting)
            {
                IsConnecting = false;
            }

            UpdateConnectionState();
        }
    }

    private async Task<(SftpTransferClient Client, string Home)> ConnectWithRetryAsync(
        string host,
        string username,
        string password,
        bool showErrors,
        bool keepRetrying,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var maxAttempts = keepRetrying ? int.MaxValue : showErrors ? 3 : AppPaths.TransferRetryAttempts;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = new SftpTransferClient();
            try
            {
                Status = attempt == 1
                    ? $"Ansluter till {host}..."
                    : keepRetrying
                        ? $"Återansluter till {host} (försök {attempt})..."
                        : $"Återförsöker anslutning till {host} ({attempt}/{maxAttempts})...";
                RefreshConnectionManagerEntries();

                var home = await Task.Run(() => client.Connect(host, username, string.IsNullOrEmpty(password) ? null : password, null), cancellationToken);
                return (client, home);
            }
            catch (OperationCanceledException)
            {
                client.Close();
                throw;
            }
            catch (Exception exc)
            {
                lastError = exc;
                client.Close();
                if (!ShouldRetryConnection(exc) || attempt >= maxAttempts)
                {
                    throw;
                }

                LogWriter.Write(AppPaths.TransferLogPath, $"Anslutning till {host} misslyckades, försöker igen {attempt + 1}/{maxAttempts}: {ExceptionText(exc)}");
                await Task.Delay(TimeSpan.FromSeconds(AppPaths.TransferRetryDelaySeconds), cancellationToken);
            }
        }

        throw lastError ?? new InvalidOperationException("Kunde inte ansluta.");
    }

    private void CancelAutoReconnect(string? host = null, bool cancelToken = true)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            foreach (var cancellation in _autoReconnectCancellations.Values)
            {
                if (cancelToken)
                {
                    cancellation.Cancel();
                }

                cancellation.Dispose();
            }

            _autoReconnectCancellations.Clear();
        }
        else if (_autoReconnectCancellations.Remove(host, out var cancellation))
        {
            if (cancelToken)
            {
                cancellation.Cancel();
            }

            cancellation.Dispose();
        }

        AutoConnecting = _autoReconnectCancellations.Count > 0;
    }

    private bool IsHostConnected(string host)
    {
        return _connectedSftpClients.TryGetValue(host, out var client) && client.IsConnected;
    }

    private IReadOnlyList<string> RememberedAutoConnectHosts()
    {
        return _uiSettings.AutoConnectHostAddresses
            .Concat(string.IsNullOrWhiteSpace(_uiSettings.LastConnectedHostAddress)
                ? []
                : [_uiSettings.LastConnectedHostAddress])
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void RememberAutoConnectHost(string host, string username)
    {
        if (!_uiSettings.AutoConnectHostAddresses.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            _uiSettings.AutoConnectHostAddresses.Add(host);
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            _uiSettings.UsernamesByHost[host] = username;
        }
    }

    private string RememberedUsernameForHost(string host)
    {
        if (_uiSettings.UsernamesByHost.TryGetValue(host, out var remembered)
            && !string.IsNullOrWhiteSpace(remembered))
        {
            return remembered;
        }

        return string.Equals(host, _uiSettings.LastConnectedHostAddress, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(_uiSettings.LastConnectedUsername)
            ? _uiSettings.LastConnectedUsername
            : "pi";
    }

    private string UsernameForHost(HostCandidate host)
    {
        return !string.IsNullOrWhiteSpace(host.Username)
            ? host.Username
            : RememberedUsernameForHost(host.Address);
    }

    private static bool ShouldRetryConnection(Exception exc)
    {
        if (exc is SshAuthenticationException)
        {
            return false;
        }

        var text = $"{exc.GetType().Name}: {exc.Message}".ToLowerInvariant();
        return text.Contains("timeout", StringComparison.Ordinal)
               || text.Contains("timed out", StringComparison.Ordinal)
               || text.Contains("refused", StringComparison.Ordinal)
               || text.Contains("closed", StringComparison.Ordinal)
               || text.Contains("reset", StringComparison.Ordinal)
               || text.Contains("network", StringComparison.Ordinal)
               || text.Contains("tempor", StringComparison.Ordinal)
               || text.Contains("no route", StringComparison.Ordinal)
               || text.Contains("host", StringComparison.Ordinal);
    }

    private static string NormalizeRememberedRemotePath(SftpTransferClient client, string remembered, string home)
    {
        var value = PosixPath.NormalizeSlashes(remembered.Trim());
        if (string.IsNullOrWhiteSpace(value) || value == ".")
        {
            return home;
        }

        try
        {
            if (value.StartsWith("~/", StringComparison.Ordinal))
            {
                return client.Normalize(PosixPath.Join(home, value[2..]));
            }

            if (value.StartsWith("home/", StringComparison.OrdinalIgnoreCase))
            {
                return client.Normalize($"/{value}");
            }

            return client.Normalize(value);
        }
        catch
        {
            return home;
        }
    }

    [RelayCommand]
    private async Task RefreshLocalDirsAsync()
    {
        var path = string.IsNullOrWhiteSpace(LocalPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : LocalPath.Trim();
        var generation = Interlocked.Increment(ref _localContentsRefreshGeneration);
        var displayedCached = TryShowCachedLocalDirectory(path, generation);
        var leftDeviceId = SelectedLeftDevice?.Id;
        var leftClient = LeftSftpClient();

        try
        {
            var (normalized, entries, storageInfo, contentSummary) = await Task.Run<(string Normalized, IReadOnlyList<LocalEntry> Entries, StorageInfo? StorageInfo, DirectoryContentSummary? ContentSummary)>(() =>
            {
                if (leftClient is { } client)
                {
                    var normalizedRemotePath = client.Normalize(path);
                    var remoteEntries = client.ListEntries(normalizedRemotePath)
                        .Select(entry => new LocalEntry(entry.Name, entry.IsDirectory, entry.Size, entry.FullPath))
                        .ToList();
                    return (normalizedRemotePath, (IReadOnlyList<LocalEntry>)remoteEntries, client.GetStorageInfo(normalizedRemotePath), (DirectoryContentSummary?)null);
                }

                var normalizedLocalPath = _localFileSystem.Normalize(path);
                return (normalizedLocalPath, _localFileSystem.ListEntries(normalizedLocalPath), _localFileSystem.GetStorageInfo(normalizedLocalPath), (DirectoryContentSummary?)null);
            });

            if (generation != _localContentsRefreshGeneration || !IsSameSelectedLeftDevice(leftDeviceId))
            {
                return;
            }

            if (LocalTreeRoots.Count == 0)
            {
                InitializeLocalTreeRoot();
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _localContentsRefreshGeneration || !IsSameSelectedLeftDevice(leftDeviceId))
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
                var directListingBytes = SumDirectFileBytes(entries.Select(entry => (entry.IsDirectory, entry.Size)));
                LeftStatus = FormatPanelStatus(LeftPanelStatusName(), dirCount, fileCount, contentSummary, directListingBytes);
                LeftStorageStatus = FormatStorageBadge(storageInfo);
            });

            CacheLocalDirectory(normalized, entries, storageInfo, contentSummary);
            StartLocalContentSummaryRefresh(normalized, entries, storageInfo, generation);
        }
        catch (Exception exc)
        {
            await ShowErrorAsync($"Kunde inte läsa lokala filer: {ExceptionText(exc)}");
        }
    }

    [RelayCommand]
    private async Task LocalHomeAsync()
    {
        LocalPath = LeftSftpClient() is { } client
            ? client.Normalize(".")
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await RefreshLocalDirsAsync();
    }

    [RelayCommand]
    private async Task LocalUpAsync()
    {
        var current = string.IsNullOrWhiteSpace(LocalPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : LocalPath.Trim();
        if (LeftSftpClient() is not null)
        {
            LocalPath = PosixPath.DirectoryName(current.TrimEnd('/'));
            if (string.IsNullOrWhiteSpace(LocalPath))
            {
                LocalPath = "/";
            }
        }
        else
        {
            var parent = Directory.GetParent(_localFileSystem.Normalize(current));
            LocalPath = parent?.FullName ?? Path.GetPathRoot(current) ?? current;
        }

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

    [RelayCommand]
    private async Task OpenLocalInExplorerAsync()
    {
        if (LeftSftpClient() is not null)
        {
            Status = "Utforskaren kan bara öppna lokala mappar.";
            return;
        }

        var path = SelectedLocalEntry?.FullPath
                   ?? SelectedLocalTreeNode?.FullPath
                   ?? LocalPath;
        await OpenPathInSystemExplorerAsync(path);
    }

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
            var remoteClient = LeftSftpClient();
            foreach (var (path, isDirectory, displayName) in targets)
            {
                if (remoteClient is not null)
                {
                    await Task.Run(() => remoteClient.DeleteRemotePath(path, isDirectory));
                }
                else
                {
                    await Task.Run(() => _localFileSystem.DeletePath(path, isDirectory));
                }

                var parentPath = remoteClient is not null ? PosixPath.DirectoryName(path) : Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(parentPath))
                {
                    parentPath = remoteClient is not null ? "/" : Path.GetPathRoot(path) ?? path;
                }

                if (remoteClient is not null
                    ? IsRemotePathInsideOrEqual(LocalPath, path)
                    : IsLocalPathInsideOrEqual(LocalPath, path))
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
        var selectedEntries = SelectedLocalEntries.ToList();
        if (selectedEntries.Count == 0 && SelectedLocalEntry is { } selectedEntry)
        {
            selectedEntries.Add(selectedEntry);
        }

        if (selectedEntries.Count > 0)
        {
            var entryTargets = new List<(string Path, bool IsDirectory, string DisplayName)>();
            foreach (var localEntry in selectedEntries.DistinctBy(localEntry => localEntry.FullPath))
            {
                var path = LeftSftpClient() is not null ? PosixPath.Normalize(localEntry.FullPath) : _localFileSystem.Normalize(localEntry.FullPath);
                if (LeftSftpClient() is null && IsLocalDriveRoot(path))
                {
                    continue;
                }

                entryTargets.Add((path, localEntry.IsDirectory, localEntry.DisplayName));
            }

            return entryTargets;
        }

        if (SelectedLocalEntry is { } entry)
        {
            var path = LeftSftpClient() is not null ? PosixPath.Normalize(entry.FullPath) : _localFileSystem.Normalize(entry.FullPath);
            if (LeftSftpClient() is null && IsLocalDriveRoot(path))
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
            var path = LeftSftpClient() is not null ? PosixPath.Normalize(node.FullPath) : _localFileSystem.Normalize(node.FullPath);
            if (LeftSftpClient() is null && IsLocalDriveRoot(path))
            {
                continue;
            }

            targets.Add((path, true, $"{node.Name}/"));
        }

        return targets;
    }

    private void OnSelectedLocalEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DeleteSelectedLocalItemCommand.NotifyCanExecuteChanged();
        CopySelectedLocalToRemoteCommand.NotifyCanExecuteChanged();
        MoveSelectedLocalToRemoteCommand.NotifyCanExecuteChanged();
        _ = UpdateSelectedLocalStorageStatusAsync();
    }

    private void OnSelectedLocalTreeNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DeleteSelectedLocalItemCommand.NotifyCanExecuteChanged();
        CopySelectedLocalToRemoteCommand.NotifyCanExecuteChanged();
        MoveSelectedLocalToRemoteCommand.NotifyCanExecuteChanged();
        _ = UpdateSelectedLocalStorageStatusAsync();
    }

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
        var remoteClient = LeftSftpClient();
        var target = remoteClient is not null
            ? PosixPath.Join(parent, name)
            : Path.Combine(_localFileSystem.Normalize(parent), name);
        try
        {
            if (remoteClient is not null)
            {
                await Task.Run(() => remoteClient.MakeDirectory(target));
            }
            else
            {
                await Task.Run(() => _localFileSystem.CreateDirectory(target));
            }
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
        var displayedCached = TryShowCachedRemoteDirectory(path, generation);

        if (RemoteTreeRoots.Count == 0)
        {
            InitializeRemoteTreeRoot();
        }

        var cachedTreePath = SelectedRightDevice?.IsLocal == true
            ? null
            : path == "."
                ? (RemotePath.StartsWith('/') ? PosixPath.Normalize(RemotePath) : null)
                : PosixPath.Normalize(path);
        if (cachedTreePath is not null)
        {
            await SyncTreeSelectionToPathAsync(cachedTreePath);
            ScheduleRemoteTreeLayoutUpdate();
        }

        try
        {
            var (normalized, entries, storageInfo, contentSummary) = await Task.Run(() =>
            {
                if (SelectedRightDevice?.IsLocal == true)
                {
                    var normalizedPath = _localFileSystem.Normalize(path);
                    var localEntries = _localFileSystem.ListEntries(normalizedPath)
                        .Select(entry => new RemoteEntry(entry.Name, entry.IsDirectory, entry.Size, entry.FullPath))
                        .ToList();
                    return (normalizedPath, (IReadOnlyList<RemoteEntry>)localEntries, _localFileSystem.GetStorageInfo(normalizedPath), (DirectoryContentSummary?)null);
                }

                var client = RightSftpClient() ?? _sftpClient;
                var normalizedRemotePath = client.Normalize(path);
                return (normalizedRemotePath, client.ListEntries(normalizedRemotePath), client.GetStorageInfo(normalizedRemotePath), (DirectoryContentSummary?)null);
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
                var directListingBytes = SumDirectFileBytes(entries.Select(entry => (entry.IsDirectory, entry.Size)));
                RightStatus = FormatPanelStatus(RightPanelStatusName(), dirCount, fileCount, contentSummary, directListingBytes);
                RightStorageStatus = FormatStorageBadge(storageInfo);
            });

            CacheRemoteDirectory(normalized, entries, storageInfo, contentSummary);
            StartRemoteContentSummaryRefresh(normalized, entries, storageInfo, generation);

            _ = RefreshRemoteTreeBranchInBackgroundAsync(normalized);
        }
        catch (Exception exc)
        {
            if (HandleTransientRemoteReadFailure(path, exc))
            {
                return;
            }

            if (await TryRecoverRemoteDestinationAsync(path, exc))
            {
                return;
            }

            await ShowErrorAsync($"Kunde inte läsa destinationen: {ExceptionText(exc)}");
        }
    }

    [RelayCommand]
    private async Task RemoteHomeAsync()
    {
        try
        {
            RemotePath = SelectedRightDevice?.IsLocal == true
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : await Task.Run(() => (RightSftpClient() ?? _sftpClient).Normalize("."));
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
        var current = string.IsNullOrWhiteSpace(RemotePath)
            ? SelectedRightDevice?.IsLocal == true ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : "."
            : RemotePath.Trim();
        if (SelectedRightDevice?.IsLocal == true)
        {
            var parent = Directory.GetParent(_localFileSystem.Normalize(current));
            RemotePath = parent?.FullName ?? Path.GetPathRoot(current) ?? current;
        }
        else
        {
            RemotePath = PosixPath.DirectoryName(current.TrimEnd('/'));
        }

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

        RemotePath = RemotePathForEntry(entry);
        await RefreshRemoteDirsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelectedRemoteEntry))]
    private async Task OpenSelectedRemoteEntryAsync()
    {
        await EnterRemoteEntryAsync(SelectedRemoteEntry);
    }

    private bool CanOpenSelectedRemoteEntry() => SelectedRemoteEntry?.IsDirectory == true;

    [RelayCommand]
    private async Task OpenRemoteInExplorerAsync()
    {
        if (SelectedRightDevice?.IsLocal != true)
        {
            Status = "Utforskaren kan bara öppna lokala mappar.";
            return;
        }

        var path = SelectedRemoteEntry is not null
            ? RemotePathForEntry(SelectedRemoteEntry)
            : SelectedRemoteTreeNode?.FullPath ?? RemotePath;
        await OpenPathInSystemExplorerAsync(path);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedRemoteItem))]
    private async Task DeleteSelectedRemoteItemAsync()
    {
        if (SelectedRightDevice?.IsLocal != true && !await EnsureConnectedAsync())
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
            var remoteClient = RightSftpClient();
            foreach (var (path, isDirectory, displayName) in targets)
            {
                if (SelectedRightDevice?.IsLocal == true)
                {
                    await Task.Run(() => _localFileSystem.DeletePath(path, isDirectory));
                }
                else
                {
                    var client = remoteClient ?? _sftpClient;
                    await Task.Run(() => client.DeleteRemotePath(path, isDirectory));
                }

                var parentPath = SelectedRightDevice?.IsLocal == true ? Path.GetDirectoryName(path) : PosixPath.DirectoryName(path);
                if (string.IsNullOrEmpty(parentPath))
                {
                    parentPath = SelectedRightDevice?.IsLocal == true ? Path.GetPathRoot(path) ?? path : "/";
                }

                if (SelectedRightDevice?.IsLocal == true
                    ? IsLocalPathInsideOrEqual(RemotePath, path)
                    : IsRemotePathInsideOrEqual(RemotePath, path))
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
        var selectedEntries = SelectedRemoteEntries.ToList();
        if (selectedEntries.Count == 0 && SelectedRemoteEntry is { } selectedEntry)
        {
            selectedEntries.Add(selectedEntry);
        }

        if (selectedEntries.Count > 0)
        {
            var entryTargets = new List<(string Path, bool IsDirectory, string DisplayName)>();
            foreach (var remoteEntry in selectedEntries.DistinctBy(remoteEntry => remoteEntry.Name))
            {
                var path = RemotePathForEntry(remoteEntry);
                if (string.IsNullOrWhiteSpace(path) || path == "/")
                {
                    continue;
                }

                entryTargets.Add((path, remoteEntry.IsDirectory, remoteEntry.DisplayName));
            }

            return entryTargets;
        }

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

    private void OnSelectedRemoteEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DeleteSelectedRemoteItemCommand.NotifyCanExecuteChanged();
        CopySelectedRemoteToLocalCommand.NotifyCanExecuteChanged();
        MoveSelectedRemoteToLocalCommand.NotifyCanExecuteChanged();
        _ = UpdateSelectedRemoteStorageStatusAsync();
    }

    private void OnSelectedRemoteTreeNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DeleteSelectedRemoteItemCommand.NotifyCanExecuteChanged();
        CopySelectedRemoteToLocalCommand.NotifyCanExecuteChanged();
        MoveSelectedRemoteToLocalCommand.NotifyCanExecuteChanged();
        _ = UpdateSelectedRemoteStorageStatusAsync();
    }

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

        var target = SelectedRightDevice?.IsLocal == true
            ? Path.Combine(string.IsNullOrWhiteSpace(RemotePath) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : RemotePath.Trim(), name)
            : PosixPath.Join(string.IsNullOrWhiteSpace(RemotePath) ? "." : RemotePath.Trim(), name);
        try
        {
            if (SelectedRightDevice?.IsLocal == true)
            {
                await Task.Run(() => _localFileSystem.CreateDirectory(target));
            }
            else
            {
                var client = RightSftpClient() ?? _sftpClient;
                await Task.Run(() => client.MakeDirectory(target));
            }
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
                var client = RightSftpClient() ?? _sftpClient;
                var normalizedPath = client.Normalize(path);
                return (normalizedPath, client.FindDuplicateFiles(normalizedPath));
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
        if (RightSftpClient() is not null)
        {
            return true;
        }

        var host = SelectedRightDevice?.IsLocal == false
            ? Hosts.FirstOrDefault(candidate => string.Equals(candidate.Address, SelectedRightDevice.Id, StringComparison.OrdinalIgnoreCase))
              ?? new HostCandidate(SelectedRightDevice.Name, SelectedRightDevice.Id, "sparad", RememberedUsernameForHost(SelectedRightDevice.Id))
            : SelectPreferredHost();
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
        return RightSftpClient() is not null;
    }

    private async Task OnRemoteFoldersPreparedAsync(string parentPath)
    {
        if (SelectedRightDevice?.IsLocal == true)
        {
            await RefreshRemoteContentsOnlyAsync();
            return;
        }

        var client = RightSftpClient() ?? _sftpClient;
        if (!client.IsConnected)
        {
            return;
        }

        try
        {
            var normalizedParent = await Task.Run(() => client.Normalize(parentPath));
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
            if (SelectedRightDevice?.IsLocal == true)
            {
                var normalizedLocalLeft = _localFileSystem.Normalize(left);
                var normalizedLocalRight = _localFileSystem.Normalize(right);
                return string.Equals(normalizedLocalLeft, normalizedLocalRight, StringComparison.OrdinalIgnoreCase);
            }

            var client = RightSftpClient() ?? _sftpClient;
            var normalizedLeft = client.Normalize(left);
            var normalizedRight = client.Normalize(right);
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTransferSelectedLocalToRemote))]
    private async Task CopySelectedLocalToRemoteAsync()
    {
        var sources = GetLocalTransferSources();
        await TransferSourcesToDeviceAsync(sources, RemotePath, SelectedRightDevice, deleteSourceAfterCopy: false);
    }

    [RelayCommand(CanExecute = nameof(CanTransferSelectedLocalToRemote))]
    private async Task MoveSelectedLocalToRemoteAsync()
    {
        var sources = GetLocalTransferSources();
        await TransferSourcesToDeviceAsync(sources, RemotePath, SelectedRightDevice, deleteSourceAfterCopy: true);
    }

    private bool CanTransferSelectedLocalToRemote() =>
        !IsTransferring && GetLocalTransferSources().Count > 0;

    [RelayCommand(CanExecute = nameof(CanTransferSelectedRemoteToLocal))]
    private async Task CopySelectedRemoteToLocalAsync()
    {
        var sources = GetRemoteTransferSources();
        await TransferSourcesToDeviceAsync(sources, LocalPath, SelectedLeftDevice, deleteSourceAfterCopy: false);
    }

    [RelayCommand(CanExecute = nameof(CanTransferSelectedRemoteToLocal))]
    private async Task MoveSelectedRemoteToLocalAsync()
    {
        var sources = GetRemoteTransferSources();
        await TransferSourcesToDeviceAsync(sources, LocalPath, SelectedLeftDevice, deleteSourceAfterCopy: true);
    }

    private bool CanTransferSelectedRemoteToLocal() =>
        !IsTransferring && GetRemoteTransferSources().Count > 0;

    private List<TransferSource> GetLocalTransferSources()
    {
        var sourceDeviceId = SelectedLeftDevice?.Id ?? LocalDeviceId;
        return GetLocalDeleteTargets()
            .Select(target => new TransferSource(target.Path, target.IsDirectory, target.DisplayName, sourceDeviceId))
            .ToList();
    }

    private List<TransferSource> GetRemoteTransferSources()
    {
        var sourceDeviceId = SelectedRightDevice?.Id ?? LocalDeviceId;
        return GetRemoteDeleteTargets()
            .Select(target => new TransferSource(target.Path, target.IsDirectory, target.DisplayName, sourceDeviceId))
            .ToList();
    }

    private async Task TransferSourcesToDeviceAsync(
        IReadOnlyList<TransferSource> sources,
        string destinationPath,
        ConnectedDevice? destinationDevice,
        bool deleteSourceAfterCopy)
    {
        if (sources.Count == 0)
        {
            return;
        }

        var transferred = await TransferPathsToDeviceAsync(
            sources.Select(source => source.Path).ToList(),
            destinationPath,
            destinationDevice,
            sources[0].SourceDeviceId);

        if (!transferred || !deleteSourceAfterCopy)
        {
            return;
        }

        try
        {
            await DeleteTransferSourcesAsync(sources);

            if (sources.Any(source => string.Equals(source.SourceDeviceId, SelectedLeftDevice?.Id ?? LocalDeviceId, StringComparison.OrdinalIgnoreCase)))
            {
                await RefreshLocalDirsAsync();
            }

            if (sources.Any(source => string.Equals(source.SourceDeviceId, SelectedRightDevice?.Id ?? LocalDeviceId, StringComparison.OrdinalIgnoreCase)))
            {
                await RefreshRemoteDirsAsync();
            }

            Status = sources.Count == 1
                ? $"Flyttade {sources[0].DisplayName}."
                : $"Flyttade {sources.Count} objekt.";
        }
        catch (Exception exc)
        {
            await ShowErrorAsync($"Kopieringen blev klar, men originalet kunde inte tas bort: {ExceptionText(exc)}");
        }
    }

    private async Task DeleteTransferSourcesAsync(IReadOnlyList<TransferSource> sources)
    {
        foreach (var source in sources)
        {
            if (string.Equals(source.SourceDeviceId, LocalDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() => _localFileSystem.DeletePath(source.Path, source.IsDirectory));
                continue;
            }

            if (!TryGetSftpClient(source.SourceDeviceId, out var client))
            {
                throw new InvalidOperationException($"Kunde inte hitta anslutningen för {source.SourceDeviceId}.");
            }

            await Task.Run(() => client.DeleteRemotePath(source.Path, source.IsDirectory));
        }
    }

    public async Task TransferPathsAsync(IReadOnlyList<string> localPaths, string? remoteDestination = null, string? sourceDeviceId = null)
    {
        await TransferPathsToDeviceAsync(
            localPaths,
            string.IsNullOrWhiteSpace(remoteDestination) ? RemotePath : remoteDestination,
            SelectedRightDevice,
            sourceDeviceId);
    }

    private async Task<bool> TransferPathsToDeviceAsync(
        IReadOnlyList<string> localPaths,
        string? destinationPath,
        ConnectedDevice? destinationDevice,
        string? sourceDeviceId = null)
    {
        if (localPaths.Count == 0)
        {
            return false;
        }

        if (IsTransferring)
        {
            await _dialogs.ShowInfoAsync(AppPaths.AppTitle, "En överföring pågår redan.");
            return false;
        }

        var destinationIsLocal = destinationDevice?.IsLocal != false;
        SftpTransferClient? destinationClient = null;
        if (!destinationIsLocal)
        {
            if (destinationDevice is null || !TryGetSftpClient(destinationDevice.Id, out destinationClient))
            {
                await _dialogs.ShowWarningAsync(AppPaths.AppTitle, "Ingen ansluten destinationsenhet vald.");
                return false;
            }
        }

        var destination = string.IsNullOrWhiteSpace(destinationPath) ? "" : destinationPath.Trim();
        if (string.IsNullOrWhiteSpace(destination))
        {
            await _dialogs.ShowWarningAsync(AppPaths.AppTitle, "Ingen destinationsmapp vald.");
            return false;
        }

        var effectiveSourceDeviceId = string.IsNullOrWhiteSpace(sourceDeviceId)
            ? LocalDeviceId
            : sourceDeviceId;
        if (!string.Equals(effectiveSourceDeviceId, LocalDeviceId, StringComparison.OrdinalIgnoreCase)
            && !TryGetSftpClient(effectiveSourceDeviceId, out _))
        {
            await _dialogs.ShowWarningAsync(AppPaths.AppTitle, "Ingen ansluten källenhet vald.");
            return false;
        }

        _transferCancellation?.Dispose();
        _transferCancellation = new CancellationTokenSource();
        var cancellationToken = _transferCancellation.Token;

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
            var stagedDirectory = "";
            var sourcePaths = localPaths;
            try
            {
                if (!string.Equals(effectiveSourceDeviceId, LocalDeviceId, StringComparison.OrdinalIgnoreCase)
                    && TryGetSftpClient(effectiveSourceDeviceId, out var sourceClient))
                {
                    stagedDirectory = Path.Combine(Path.GetTempPath(), "FileTransferHelper", Guid.NewGuid().ToString("N"));
                    await Task.Run(() => sourceClient.DownloadPaths(
                        localPaths,
                        stagedDirectory,
                        (done, total, filename, action) =>
                            Dispatcher.UIThread.Post(() => SetTransferProgress(done, total, filename, action)),
                        message => Dispatcher.UIThread.Post(() => Status = message),
                        cancellationToken));
                    sourcePaths = Directory.GetFileSystemEntries(stagedDirectory).ToList();
                }

                if (destinationIsLocal)
                {
                    await Task.Run(() => CopyLocalPaths(
                        sourcePaths,
                        destination,
                        (done, total, filename, action) =>
                            Dispatcher.UIThread.Post(() => SetTransferProgress(done, total, filename, action)),
                        message => Dispatcher.UIThread.Post(() => Status = message),
                        cancellationToken));
                }
                else
                {
                    var refreshPreparedRemoteFolders = ReferenceEquals(destinationDevice, SelectedRightDevice)
                        || string.Equals(destinationDevice?.Id, SelectedRightDevice?.Id, StringComparison.OrdinalIgnoreCase);
                    await Task.Run(() => destinationClient!.UploadPaths(
                        sourcePaths,
                        destination,
                        (done, total, filename, action) =>
                            Dispatcher.UIThread.Post(() => SetTransferProgress(done, total, filename, action)),
                        message => Dispatcher.UIThread.Post(() => Status = message),
                        update => Dispatcher.UIThread.Post(() => SetUploadProgress(update)),
                        refreshPreparedRemoteFolders
                            ? parentPath => Dispatcher.UIThread.Post(() => _ = OnRemoteFoldersPreparedAsync(parentPath))
                            : null,
                        cancellationToken));
                }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(stagedDirectory) && Directory.Exists(stagedDirectory))
                {
                    try
                    {
                        Directory.Delete(stagedDirectory, recursive: true);
                    }
                    catch (Exception exc)
                    {
                        LogWriter.Write(AppPaths.TransferLogPath, $"Kunde inte ta bort temporär överföringsmapp {stagedDirectory}: {exc.Message}");
                    }
                }
            }

            if (ReferenceEquals(destinationDevice, SelectedLeftDevice)
                || string.Equals(destinationDevice?.Id, SelectedLeftDevice?.Id, StringComparison.OrdinalIgnoreCase))
            {
                await RefreshLocalDirsAsync();
            }

            if (ReferenceEquals(destinationDevice, SelectedRightDevice)
                || string.Equals(destinationDevice?.Id, SelectedRightDevice?.Id, StringComparison.OrdinalIgnoreCase))
            {
                await RefreshRemoteDirsAsync();
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ProgressValue = 100;
                Status = "Överföringen är klar.";
            });
            await Task.Delay(1500);
            return true;
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Status = "Överföringen avbröts.";
                TransferSpeedText = "";
            });
            return false;
        }
        catch (Exception exc)
        {
            var detail = ExceptionText(exc);
            LogWriter.Write(AppPaths.TransferLogPath, $"Överföring misslyckades i GUI-worker: {detail}");
            LogWriter.Write(AppPaths.TransferLogPath, exc.ToString());
            await ShowErrorAsync($"Överföring misslyckades: {detail}");
            return false;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsTransferring = false;
                TransferSpeedText = "";
            });
            _transferCancellation?.Dispose();
            _transferCancellation = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelTransfer))]
    private void CancelTransfer()
    {
        if (!IsTransferring)
        {
            return;
        }

        Status = "Avbryter överföring...";
        _transferCancellation?.Cancel();
    }

    private bool CanCancelTransfer() => IsTransferring;

    private static void CopyLocalPaths(
        IReadOnlyList<string> sourcePaths,
        string destination,
        Action<int, int, string, string> progress,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var files = new List<(string Source, string Target, string DisplayName)>();

        foreach (var sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(sourcePath))
            {
                var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath));
                var targetRoot = UniqueLocalPath(Path.Combine(destination, rootName));
                Directory.CreateDirectory(targetRoot);
                foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(sourcePath, file);
                    files.Add((file, Path.Combine(targetRoot, relative), Path.Combine(rootName, relative)));
                }
            }
            else if (File.Exists(sourcePath))
            {
                var target = UniqueLocalPath(Path.Combine(destination, Path.GetFileName(sourcePath)));
                files.Add((sourcePath, target, Path.GetFileName(sourcePath)));
            }
        }

        var done = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            status?.Invoke($"Kopierar: {file.DisplayName}");
            progress(done, files.Count, file.DisplayName, "uploaded");
            Directory.CreateDirectory(Path.GetDirectoryName(file.Target) ?? destination);
            File.Copy(file.Source, file.Target, overwrite: false);
            done++;
            progress(done, files.Count, file.DisplayName, "uploaded");
        }
    }

    private async Task OpenPathInSystemExplorerAsync(string? path)
    {
        try
        {
            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var normalized = _localFileSystem.Normalize(string.IsNullOrWhiteSpace(path) ? fallback : path);
            var target = normalized;
            var arguments = "";

            if (File.Exists(normalized))
            {
                arguments = $"/select,\"{normalized}\"";
            }
            else if (Directory.Exists(normalized))
            {
                arguments = $"\"{normalized}\"";
            }
            else
            {
                var parent = Path.GetDirectoryName(normalized);
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                {
                    Status = $"Kunde inte öppna i Utforskaren: {normalized}";
                    return;
                }

                target = parent;
                arguments = $"\"{parent}\"";
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true
            });

            Status = $"Öppnade {target} i Utforskaren.";
        }
        catch (Exception exc)
        {
            await ShowErrorAsync($"Kunde inte öppna i Utforskaren: {ExceptionText(exc)}");
        }
    }

    private static string UniqueLocalPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? "";
        var filename = Path.GetFileName(path);
        var extension = Path.GetExtension(filename);
        var stem = Path.GetFileNameWithoutExtension(filename);
        for (var counter = 1; ; counter++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({counter}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private HostCandidate? SelectPreferredHost()
    {
        if (Hosts.Count == 0)
        {
            return string.IsNullOrWhiteSpace(_uiSettings.LastConnectedHostAddress)
                ? null
                : new HostCandidate(
                    _uiSettings.LastConnectedHostAddress,
                    _uiSettings.LastConnectedHostAddress,
                    "sparad",
                    _uiSettings.LastConnectedUsername);
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

    private void RestoreConnectedDeviceSelection(ConnectedDevice connectedDevice, bool fallbackToRight)
    {
        var restoredAnyPanel = false;

        if (string.Equals(_uiSettings.LastLeftDeviceId, connectedDevice.Id, StringComparison.OrdinalIgnoreCase))
        {
            SelectedLeftDevice = connectedDevice;
            restoredAnyPanel = true;
        }

        if (string.Equals(_uiSettings.LastRightDeviceId, connectedDevice.Id, StringComparison.OrdinalIgnoreCase))
        {
            SelectedRightDevice = connectedDevice;
            restoredAnyPanel = true;
        }

        if (!restoredAnyPanel && fallbackToRight && SelectedRightDevice?.IsLocal == true)
        {
            SelectedRightDevice = connectedDevice;
        }
    }

    private async Task InitializeAsync()
    {
        var cached = _networkDiscovery.CachedHosts();
        if (cached.Count > 0)
        {
            SetHosts(cached, triggerAutoConnect: false);
        }

        EnsureRememberedHostsInHosts();
        await RefreshLocalDirsAsync();
        _ = TryConnectSilentlyAsync();
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
            EnsureRememberedHostsInHosts();
            _ = TryConnectSilentlyAsync();
        }
        catch (Exception exc)
        {
            LogWriter.Write(AppPaths.DiscoveryLogPath, $"Upptäckt misslyckades: {exc.Message}");
        }
    }

    private void EnsureRememberedHostsInHosts()
    {
        foreach (var address in RememberedAutoConnectHosts())
        {
            var remembered = Hosts.FirstOrDefault(host =>
                string.Equals(host.Address, address, StringComparison.OrdinalIgnoreCase));
            if (remembered is null)
            {
                remembered = new HostCandidate(address, address, "sparad", RememberedUsernameForHost(address));
                Hosts.Add(remembered);
            }

            if (SelectedHost is null && remembered is not null)
            {
                SetSelectedHostWithoutAutoConnect(remembered);
            }
        }

        RefreshConnectionManagerEntries();
    }

    private void SetHosts(IReadOnlyList<HostCandidate> hosts, bool triggerAutoConnect = true)
    {
        var previousAddress = SelectedHost?.Address;

        Hosts.Clear();
        foreach (var host in hosts)
        {
            Hosts.Add(host);
        }

        RefreshConnectionManagerEntries();

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
            var client = RightSftpClient() ?? _sftpClient;
            var (deleted, failed) = await Task.Run(() => client.DeleteFiles(duplicates));
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
        else if (_uiSettings.UsernamesByHost.TryGetValue(host.Address, out var rememberedUsername)
                 && !string.IsNullOrWhiteSpace(rememberedUsername))
        {
            Username = rememberedUsername;
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

        RememberAutoConnectHost(address, username);

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
        RefreshConnectionManagerEntries();
    }

    private void UpdateConnectionState()
    {
        IsPiConnected = _connectedSftpClients.Values.Any(client => client.IsConnected) || _sftpClient.IsConnected;
        RefreshConnectionManagerEntries();
    }

    private void RefreshConnectionManagerEntries()
    {
        CaptureConnectionManagerInput();
        ConnectionManagerEntries.Clear();
        var rememberedHosts = RememberedAutoConnectHosts();
        foreach (var host in Hosts)
        {
            var isConnected = IsHostConnected(host.Address);
            var isAutoReconnectTarget = rememberedHosts.Contains(host.Address, StringComparer.OrdinalIgnoreCase);
            var isConnecting = _connectingHosts.Contains(host.Address);
            var status = isConnected
                ? "Ansluten"
                : isConnecting
                    ? "Återansluter"
                    : isAutoReconnectTarget
                        ? "Väntar"
                    : HostLooksOnline(host)
                    ? "Online"
                    : "Okänd/offline";
            _connectionInputSnapshots.TryGetValue(host.Address, out var snapshot);
            var username = !string.IsNullOrWhiteSpace(snapshot.Username)
                ? snapshot.Username
                : !string.IsNullOrWhiteSpace(host.Username)
                ? host.Username
                : string.Equals(host.Address, _uiSettings.LastConnectedHostAddress, StringComparison.OrdinalIgnoreCase)
                    ? _uiSettings.LastConnectedUsername
                    : "";
            var savedPassword = CredentialStore.PasswordFor(host.Address);
            var password = !string.IsNullOrEmpty(snapshot.Password) ? snapshot.Password : savedPassword;
            var entry = new ConnectionManagerEntry(
                host.Name,
                host.Address,
                host.Source,
                status,
                username,
                password,
                isConnected,
                isConnecting,
                isAutoReconnectTarget)
            {
                IsAddressVisible = IsConnectionAddressVisible,
                IsUsernameVisible = IsConnectionUsernameVisible,
                IsPasswordVisible = IsConnectionPasswordVisible
            };
            ConnectionManagerEntries.Add(entry);
        }
    }

    private void CaptureConnectionManagerInput()
    {
        foreach (var entry in ConnectionManagerEntries)
        {
            if (entry.IsConnected)
            {
                continue;
            }

            _connectionInputSnapshots[entry.Address] = new ConnectionInputSnapshot(
                entry.UsernameInput,
                entry.PasswordInput);
        }
    }

    private static bool HostLooksOnline(HostCandidate host)
    {
        return !host.Source.Equals("sparad", StringComparison.OrdinalIgnoreCase)
               && !host.Source.Equals("manuell", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetSftpClient(string deviceId, out SftpTransferClient client)
    {
        if (_connectedSftpClients.TryGetValue(deviceId, out client!) && client.IsConnected)
        {
            return true;
        }

        client = _sftpClient;
        return string.Equals(deviceId, _connectedHost, StringComparison.OrdinalIgnoreCase) && _sftpClient.IsConnected;
    }

    private SftpTransferClient? RightSftpClient()
    {
        return SelectedRightDevice?.IsLocal == false && TryGetSftpClient(SelectedRightDevice.Id, out var client)
            ? client
            : null;
    }

    private SftpTransferClient? LeftSftpClient()
    {
        return SelectedLeftDevice?.IsLocal == false && TryGetSftpClient(SelectedLeftDevice.Id, out var client)
            ? client
            : null;
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
        if (!string.IsNullOrEmpty(TransferSpeedText))
        {
            ProgressText = string.IsNullOrEmpty(ProgressText)
                ? TransferSpeedText
                : $"{ProgressText} · {TransferSpeedText}";
        }

        Status = update.Percent > 0
            ? $"{update.Label} ({update.Percent}%)"
            : update.Label;
    }

    private async Task RefreshRemoteContentsOnlyAsync()
    {
        if (SelectedRightDevice?.IsLocal != true && RightSftpClient() is null && !_sftpClient.IsConnected)
        {
            return;
        }

        var path = string.IsNullOrWhiteSpace(RemotePath) ? "." : RemotePath.Trim();
        var generation = Interlocked.Increment(ref _remoteContentsRefreshGeneration);
        var displayedCached = TryShowCachedRemoteDirectory(path, generation);

        try
        {
            var (normalized, entries, storageInfo, contentSummary) = await Task.Run(() =>
            {
                if (SelectedRightDevice?.IsLocal == true)
                {
                    var normalizedPath = _localFileSystem.Normalize(path);
                    var localEntries = _localFileSystem.ListEntries(normalizedPath)
                        .Select(entry => new RemoteEntry(entry.Name, entry.IsDirectory, entry.Size, entry.FullPath))
                        .ToList();
                    return (normalizedPath, (IReadOnlyList<RemoteEntry>)localEntries, _localFileSystem.GetStorageInfo(normalizedPath), (DirectoryContentSummary?)null);
                }

                var client = RightSftpClient() ?? _sftpClient;
                var normalizedRemotePath = client.Normalize(path);
                return (normalizedRemotePath, client.ListEntries(normalizedRemotePath), client.GetStorageInfo(normalizedRemotePath), (DirectoryContentSummary?)null);
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
                var directListingBytes = SumDirectFileBytes(entries.Select(entry => (entry.IsDirectory, entry.Size)));
                RightStatus = FormatPanelStatus(RightPanelStatusName(), dirCount, fileCount, contentSummary, directListingBytes);
                RightStorageStatus = FormatStorageBadge(storageInfo);
            });

            CacheRemoteDirectory(normalized, entries, storageInfo, contentSummary);
            StartRemoteContentSummaryRefresh(normalized, entries, storageInfo, generation);
        }
        catch (Exception exc)
        {
            if (HandleTransientRemoteReadFailure(path, exc))
            {
                return;
            }

            if (await TryRecoverRemoteDestinationAsync(path, exc))
            {
                return;
            }

            await ShowErrorAsync($"Kunde inte läsa destinationen: {ExceptionText(exc)}");
        }
    }

    private async Task<bool> TryRecoverRemoteDestinationAsync(string failedPath, Exception exc)
    {
        if (SelectedRightDevice?.IsLocal == true)
        {
            return false;
        }

        var client = RightSftpClient() ?? _sftpClient;
        if (!client.IsConnected)
        {
            return false;
        }

        try
        {
            var home = _remoteHomePaths.TryGetValue(SelectedRightDevice?.Id ?? _connectedHost, out var rememberedHome)
                ? rememberedHome
                : await Task.Run(() => client.Normalize("."));
            var normalizedFailed = await Task.Run(() => client.Normalize(failedPath));
            if (string.Equals(normalizedFailed, home, StringComparison.Ordinal))
            {
                return false;
            }

            LogWriter.Write(AppPaths.TransferLogPath, $"Kunde inte läsa sparad destination {failedPath}: {ExceptionText(exc)}. Återgår till {home}.");
            var host = SelectedRightDevice?.Id ?? ConnectedHostAddress();
            if (!string.IsNullOrWhiteSpace(host))
            {
                _uiSettings.LastRemotePathsByHost[host] = home;
                _uiSettingsStore.Save(_uiSettings);
            }

            RemotePath = home;
            await RefreshRemoteDirsAsync();
            return true;
        }
        catch (Exception recoverExc)
        {
            LogWriter.Write(AppPaths.TransferLogPath, $"Kunde inte återställa destination efter fel på {failedPath}: {ExceptionText(recoverExc)}");
            return false;
        }
    }

    private bool HandleTransientRemoteReadFailure(string path, Exception exc)
    {
        if (!IsTransientRemoteReadException(exc))
        {
            return false;
        }

        LogWriter.Write(
            AppPaths.TransferLogPath,
            $"Tillfälligt fjärrläsningsfel för {path}: {ExceptionText(exc)}. Behåller nuvarande vy och försöker igen vid nästa refresh.");
        Status = "Tillfälligt anslutningsfel. Försöker igen i bakgrunden.";
        _ = TryConnectSilentlyAsync();
        return true;
    }

    private static bool IsTransientRemoteReadException(Exception exc)
    {
        var text = $"{exc.GetType().Name}: {exc.Message}".ToLowerInvariant();
        return text.Contains("failed to open a channel", StringComparison.Ordinal)
               || text.Contains("channel", StringComparison.Ordinal)
               || text.Contains("session", StringComparison.Ordinal)
               || text.Contains("connection was closed", StringComparison.Ordinal)
               || text.Contains("client not connected", StringComparison.Ordinal);
    }

    private void ApplyRemoteEntries(IReadOnlyList<RemoteEntry> entries)
    {
        RemoteEntries.Clear();
        foreach (var entry in entries)
        {
            RemoteEntries.Add(entry);
        }
    }

    private bool TryShowCachedRemoteDirectory(string path, int generation)
    {
        var cacheKey = RemoteDirectoryCacheKey(path);
        if (cacheKey is null || !_remoteDirectoryCache.TryGetValue(cacheKey, out var cached))
        {
            return false;
        }

        RemotePath = cached.Path;
        ApplyRemoteEntries(cached.Entries);
        var dirCount = cached.Entries.Count(entry => entry.IsDirectory);
        var fileCount = cached.Entries.Count - dirCount;
        var directListingBytes = SumDirectFileBytes(cached.Entries.Select(entry => (entry.IsDirectory, entry.Size)));
        RightStatus = FormatPanelStatus(RightPanelStatusName(), dirCount, fileCount, cached.ContentSummary, directListingBytes);
        RightStorageStatus = FormatStorageBadge(cached.StorageInfo);

        Dispatcher.UIThread.Post(() =>
        {
            if (generation == _remoteContentsRefreshGeneration)
            {
                _ = SyncTreeSelectionToPathAsync(cached.Path);
                ScheduleRemoteTreeLayoutUpdate();
            }
        });
        return true;
    }

    private string? RemoteDirectoryCacheKey(string path)
    {
        try
        {
            if (SelectedRightDevice?.IsLocal == true)
            {
                return $"local:{_localFileSystem.Normalize(path)}";
            }

            var client = RightSftpClient() ?? _sftpClient;
            if (!client.IsConnected)
            {
                return null;
            }

            var deviceId = SelectedRightDevice?.Id ?? ConnectedHostAddress();
            return $"remote:{deviceId}:{client.Normalize(path)}";
        }
        catch
        {
            return null;
        }
    }

    private void CacheRemoteDirectory(string normalizedPath, IReadOnlyList<RemoteEntry> entries, StorageInfo? storageInfo, DirectoryContentSummary? contentSummary)
    {
        var cacheKey = RemoteDirectoryCacheKey(normalizedPath);
        if (cacheKey is null)
        {
            return;
        }

        var signature = RemoteDirectorySignature(entries, storageInfo, contentSummary);
        if (_remoteDirectoryCache.TryGetValue(cacheKey, out var existing)
            && string.Equals(existing.Signature, signature, StringComparison.Ordinal))
        {
            return;
        }

        _remoteDirectoryCache[cacheKey] = new CachedRemoteDirectory(
            normalizedPath,
            entries.ToList(),
            storageInfo,
            contentSummary,
            signature);
    }

    private void StartRemoteContentSummaryRefresh(
        string normalizedPath,
        IReadOnlyList<RemoteEntry> entries,
        StorageInfo? storageInfo,
        int generation)
    {
        var entrySnapshot = entries.ToList();
        if (SelectedRightDevice?.IsLocal == true)
        {
            if (!LocalPathShouldHaveNestedSummary(normalizedPath))
            {
                return;
            }

            _ = RefreshRemoteContentSummaryAsync(
                normalizedPath,
                entrySnapshot,
                storageInfo,
                generation,
                () => _localFileSystem.CountDirectoryContent(normalizedPath));
            return;
        }

        var client = RightSftpClient() ?? _sftpClient;
        if (!client.IsConnected || !RemotePathShouldHaveNestedSummary(normalizedPath))
        {
            return;
        }

        _ = RefreshRemoteContentSummaryAsync(
            normalizedPath,
            entrySnapshot,
            storageInfo,
            generation,
            () => client.CountDirectoryContent(normalizedPath));
    }

    private async Task RefreshRemoteContentSummaryAsync(
        string normalizedPath,
        IReadOnlyList<RemoteEntry> entries,
        StorageInfo? storageInfo,
        int generation,
        Func<DirectoryContentSummary> loadSummary)
    {
        try
        {
            var summary = await Task.Run(loadSummary);
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

                var dirCount = entries.Count(entry => entry.IsDirectory);
                var fileCount = entries.Count - dirCount;
                var directListingBytes = SumDirectFileBytes(entries.Select(entry => (entry.IsDirectory, entry.Size)));
                RightStatus = FormatPanelStatus(RightPanelStatusName(), dirCount, fileCount, summary, directListingBytes);
            });

            CacheRemoteDirectory(normalizedPath, entries, storageInfo, summary);
        }
        catch (Exception exc)
        {
            LogWriter.Write(AppPaths.TransferLogPath, $"Kunde inte läsa mappsammanfattning för {normalizedPath}: {ExceptionText(exc)}");
        }
    }

    private void InitializeRemoteTreeRoot()
    {
        RemoteTreeRoots.Clear();
        if (SelectedRightDevice?.IsLocal == true)
        {
            foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
            {
                var path = drive.RootDirectory.FullName;
                var name = drive.Name.TrimEnd('\\');
                if (string.IsNullOrEmpty(name))
                {
                    name = path;
                }

                RemoteTreeRoots.Add(CreateTreeNode(name, path));
            }

            UpdateRemoteTreePaneWidth();
            return;
        }

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
        if (node.IsLoaded || node.IsPlaceholder)
        {
            return;
        }

        if (SelectedRightDevice?.IsLocal == true)
        {
            try
            {
                var directories = await Task.Run(() => _localFileSystem.ListSubdirectories(node.FullPath));
                ApplyRemoteTreeChildren(node, directories);
                node.IsLoaded = true;
                ScheduleRemoteTreeLayoutUpdate();
            }
            catch (Exception exc)
            {
                await ShowErrorAsync($"Kunde inte läsa lokalt mappträd: {ExceptionText(exc)}");
            }

            return;
        }

        if (RightSftpClient() is null && !_sftpClient.IsConnected)
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
            var client = RightSftpClient() ?? _sftpClient;
            var directories = await Task.Run(() => client.ListSubdirectories(node.FullPath));
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
                var childPath = SelectedRightDevice?.IsLocal == true
                    ? Path.Combine(node.FullPath, directory)
                    : PosixPath.Join(node.FullPath, directory);
                node.Children.Add(CreateTreeNode(directory, childPath));
            }
        }
    }

    private async Task RefreshRemoteTreeBranchInBackgroundAsync(string path)
    {
        var client = RightSftpClient() ?? _sftpClient;
        if (SelectedRightDevice?.IsLocal == true || !client.IsConnected || RemoteTreeRoots.Count == 0)
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
                    var directories = await Task.Run(() => client.ListSubdirectories(node.FullPath));
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

        if (SelectedRightDevice?.IsLocal == true)
        {
            var normalizedLocal = _localFileSystem.Normalize(path);
            var rootPath = Path.GetPathRoot(normalizedLocal);
            if (string.IsNullOrEmpty(rootPath))
            {
                return;
            }

            var currentLocal = RemoteTreeRoots.FirstOrDefault(root =>
                string.Equals(_localFileSystem.Normalize(root.FullPath), _localFileSystem.Normalize(rootPath), StringComparison.OrdinalIgnoreCase));
            if (currentLocal is null)
            {
                return;
            }

            currentLocal.IsExpanded = true;
            await EnsureTreeNodeChildrenAsync(currentLocal);
            foreach (var segment in LocalPathSegments(normalizedLocal))
            {
                var childPath = Path.Combine(currentLocal.FullPath, segment);
                var child = currentLocal.Children.FirstOrDefault(candidate =>
                    !candidate.IsPlaceholder &&
                    string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    child = CreateTreeNode(segment, childPath);
                    currentLocal.Children.Add(child);
                }

                currentLocal = child;
                currentLocal.IsExpanded = true;
                await EnsureTreeNodeChildrenAsync(currentLocal);
            }

            WithSuppressedTreeSelectionNavigation(() =>
            {
                SelectedRemoteTreeNode = currentLocal;
            });
            UpdateRemoteTreePaneWidth();
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
        InvalidateRemoteDirectoryCache(path);

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

    private void InvalidateRemoteDirectoryCache(string path)
    {
        var cacheKey = RemoteDirectoryCacheKey(path);
        if (cacheKey is not null)
        {
            _remoteDirectoryCache.Remove(cacheKey);
        }
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
        var displayedCached = TryShowCachedLocalDirectory(path, generation);
        var leftDeviceId = SelectedLeftDevice?.Id;
        var leftClient = LeftSftpClient();

        try
        {
            var (normalized, entries, storageInfo, contentSummary) = await Task.Run<(string Normalized, IReadOnlyList<LocalEntry> Entries, StorageInfo? StorageInfo, DirectoryContentSummary? ContentSummary)>(() =>
            {
                if (leftClient is { } client)
                {
                    var normalizedRemotePath = client.Normalize(path);
                    var remoteEntries = client.ListEntries(normalizedRemotePath)
                        .Select(entry => new LocalEntry(entry.Name, entry.IsDirectory, entry.Size, entry.FullPath))
                        .ToList();
                    return (normalizedRemotePath, (IReadOnlyList<LocalEntry>)remoteEntries, client.GetStorageInfo(normalizedRemotePath), (DirectoryContentSummary?)null);
                }

                var normalizedLocalPath = _localFileSystem.Normalize(path);
                return (normalizedLocalPath, _localFileSystem.ListEntries(normalizedLocalPath), _localFileSystem.GetStorageInfo(normalizedLocalPath), (DirectoryContentSummary?)null);
            });

            if (generation != _localContentsRefreshGeneration || !IsSameSelectedLeftDevice(leftDeviceId))
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _localContentsRefreshGeneration || !IsSameSelectedLeftDevice(leftDeviceId))
                {
                    return;
                }

                LocalPath = normalized;
                ApplyLocalEntries(entries);

                var dirCount = LocalEntries.Count(entry => entry.IsDirectory);
                var fileCount = LocalEntries.Count - dirCount;
                var directListingBytes = SumDirectFileBytes(entries.Select(entry => (entry.IsDirectory, entry.Size)));
                LeftStatus = FormatPanelStatus(LeftPanelStatusName(), dirCount, fileCount, contentSummary, directListingBytes);
                LeftStorageStatus = FormatStorageBadge(storageInfo);
            });

            CacheLocalDirectory(normalized, entries, storageInfo, contentSummary);
            StartLocalContentSummaryRefresh(normalized, entries, storageInfo, generation);
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

    private bool TryShowCachedLocalDirectory(string path, int generation)
    {
        var cacheKey = LocalDirectoryCacheKey(path);
        if (cacheKey is null || !_localDirectoryCache.TryGetValue(cacheKey, out var cached))
        {
            return false;
        }

        LocalPath = cached.Path;
        ApplyLocalEntries(cached.Entries);
        var dirCount = cached.Entries.Count(entry => entry.IsDirectory);
        var fileCount = cached.Entries.Count - dirCount;
        var directListingBytes = SumDirectFileBytes(cached.Entries.Select(entry => (entry.IsDirectory, entry.Size)));
        LeftStatus = FormatPanelStatus(LeftPanelStatusName(), dirCount, fileCount, cached.ContentSummary, directListingBytes);
        LeftStorageStatus = FormatStorageBadge(cached.StorageInfo);

        Dispatcher.UIThread.Post(() =>
        {
            if (generation == _localContentsRefreshGeneration)
            {
                _ = SyncLocalTreeSelectionToPathAsync(cached.Path);
                ScheduleLocalTreeLayoutUpdate();
            }
        });
        return true;
    }

    private string? LocalDirectoryCacheKey(string path)
    {
        try
        {
            if (LeftSftpClient() is { } client)
            {
                return client.IsConnected
                    ? $"remote:{SelectedLeftDevice?.Id ?? ConnectedHostAddress()}:{client.Normalize(path)}"
                    : null;
            }

            return $"local:{_localFileSystem.Normalize(path)}";
        }
        catch
        {
            return null;
        }
    }

    private void CacheLocalDirectory(string normalizedPath, IReadOnlyList<LocalEntry> entries, StorageInfo? storageInfo, DirectoryContentSummary? contentSummary)
    {
        var cacheKey = LocalDirectoryCacheKey(normalizedPath);
        if (cacheKey is null)
        {
            return;
        }

        var signature = LocalDirectorySignature(entries, storageInfo, contentSummary);
        if (_localDirectoryCache.TryGetValue(cacheKey, out var existing)
            && string.Equals(existing.Signature, signature, StringComparison.Ordinal))
        {
            return;
        }

        _localDirectoryCache[cacheKey] = new CachedLocalDirectory(
            normalizedPath,
            entries.ToList(),
            storageInfo,
            contentSummary,
            signature);
    }

    private void StartLocalContentSummaryRefresh(
        string normalizedPath,
        IReadOnlyList<LocalEntry> entries,
        StorageInfo? storageInfo,
        int generation)
    {
        var entrySnapshot = entries.ToList();
        var remoteClient = LeftSftpClient();
        if (remoteClient is not null)
        {
            if (!RemotePathShouldHaveNestedSummary(normalizedPath))
            {
                return;
            }

            _ = RefreshLocalContentSummaryAsync(
                normalizedPath,
                entrySnapshot,
                storageInfo,
                generation,
                () => remoteClient.CountDirectoryContent(normalizedPath));
            return;
        }

        if (!LocalPathShouldHaveNestedSummary(normalizedPath))
        {
            return;
        }

        _ = RefreshLocalContentSummaryAsync(
            normalizedPath,
            entrySnapshot,
            storageInfo,
            generation,
            () => _localFileSystem.CountDirectoryContent(normalizedPath));
    }

    private async Task RefreshLocalContentSummaryAsync(
        string normalizedPath,
        IReadOnlyList<LocalEntry> entries,
        StorageInfo? storageInfo,
        int generation,
        Func<DirectoryContentSummary> loadSummary)
    {
        try
        {
            var summary = await Task.Run(loadSummary);
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

                var dirCount = entries.Count(entry => entry.IsDirectory);
                var fileCount = entries.Count - dirCount;
                var directListingBytes = SumDirectFileBytes(entries.Select(entry => (entry.IsDirectory, entry.Size)));
                LeftStatus = FormatPanelStatus(LeftPanelStatusName(), dirCount, fileCount, summary, directListingBytes);
            });

            CacheLocalDirectory(normalizedPath, entries, storageInfo, summary);
        }
        catch (Exception exc)
        {
            LogWriter.Write(AppPaths.TransferLogPath, $"Kunde inte läsa mappsammanfattning för {normalizedPath}: {ExceptionText(exc)}");
        }
    }

    private static string FormatPanelStatus(string panelName, int dirCount, int fileCount, DirectoryContentSummary? contentSummary = null, long directListingBytes = 0)
    {
        if (contentSummary is null || !contentSummary.HasNestedContent)
        {
            var bytes = contentSummary?.TotalBytes ?? directListingBytes;
            var sizePart = bytes > 0 ? $", {FormatByteSize(bytes)}" : "";
            return $"{panelName}: {dirCount} mappar, {fileCount} filer{sizePart}";
        }

        return $"{panelName}: {dirCount} mappar, {fileCount} filer ({FormatByteSize(contentSummary.DirectBytes)}) | i undermappar: {contentSummary.Subdirectories} mappar, {contentSummary.NestedFiles} filer ({FormatByteSize(contentSummary.NestedBytes)})";
    }

    private static string FormatSelectedFolderStatus(string folderName, DirectoryContentSummary summary)
    {
        var totalFiles = summary.DirectFiles + summary.NestedFiles;
        var totalDirs = summary.DirectDirectories + summary.Subdirectories;
        if (summary.HasNestedContent)
        {
            return $"{folderName}: {totalDirs} mappar, {totalFiles} filer, {FormatByteSize(summary.TotalBytes)}";
        }

        return $"{folderName}: {summary.DirectFiles} filer, {FormatByteSize(summary.TotalBytes)}";
    }

    private static string FormatPanelStatus(string panelName, string status)
    {
        return $"{panelName}: {status}";
    }

    private string LeftPanelStatusName()
    {
        return PanelStatusName(SelectedLeftDevice, "Vänster");
    }

    private string RightPanelStatusName()
    {
        return PanelStatusName(SelectedRightDevice, "Höger");
    }

    private static string PanelStatusName(ConnectedDevice? device, string fallback)
    {
        return string.IsNullOrWhiteSpace(device?.Name) ? fallback : device.Name;
    }

    private static string FormatStorageInfo(StorageInfo storageInfo)
    {
        return $"{FormatByteSize(storageInfo.AvailableBytes)} ledigt av {FormatByteSize(storageInfo.TotalBytes)}";
    }

    private static string FormatStorageBadge(StorageInfo? storageInfo)
    {
        return storageInfo is null ? "" : FormatStorageInfo(storageInfo);
    }

    private static bool LocalPathShouldHaveNestedSummary(string path)
    {
        try
        {
            return !IsLocalDriveRoot(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool RemotePathShouldHaveNestedSummary(string path)
    {
        return !string.Equals(PosixPath.Normalize(path), "/", StringComparison.Ordinal);
    }

    private static long SumDirectFileBytes(IEnumerable<(bool IsDirectory, long Size)> entries)
    {
        return entries.Where(entry => !entry.IsDirectory).Sum(entry => entry.Size);
    }

    private static string FormatByteSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unitIndex = 0;
        var displayValue = (double)value;
        while (displayValue >= 1024 && unitIndex < units.Length - 1)
        {
            displayValue /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value} {units[unitIndex]}"
            : $"{displayValue:0.#} {units[unitIndex]}";
    }

    private static string LocalDirectorySignature(IReadOnlyList<LocalEntry> entries, StorageInfo? storageInfo, DirectoryContentSummary? contentSummary)
    {
        return DirectorySignature(
            entries.Select(entry => (entry.Name, entry.IsDirectory, entry.Size)),
            storageInfo,
            contentSummary);
    }

    private static string RemoteDirectorySignature(IReadOnlyList<RemoteEntry> entries, StorageInfo? storageInfo, DirectoryContentSummary? contentSummary)
    {
        return DirectorySignature(
            entries.Select(entry => (entry.Name, entry.IsDirectory, entry.Size)),
            storageInfo,
            contentSummary);
    }

    private static string DirectorySignature(
        IEnumerable<(string Name, bool IsDirectory, long Size)> entries,
        StorageInfo? storageInfo,
        DirectoryContentSummary? contentSummary)
    {
        var parts = entries
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => $"{(entry.IsDirectory ? "D" : "F")}:{entry.Name}:{entry.Size}");
        var storagePart = storageInfo is null
            ? ""
            : $"|S:{storageInfo.AvailableBytes}:{storageInfo.TotalBytes}";
        var contentPart = contentSummary is null
            ? ""
            : $"|C:{contentSummary.DirectDirectories}:{contentSummary.DirectFiles}:{contentSummary.Subdirectories}:{contentSummary.NestedFiles}:{contentSummary.DirectBytes}:{contentSummary.NestedBytes}";
        return $"{string.Join("|", parts)}{storagePart}{contentPart}";
    }

    private async Task UpdateSelectedLocalStorageStatusAsync()
    {
        var selectedDirectory = TryGetSelectedLocalDirectory();
        if (selectedDirectory is null)
        {
            return;
        }

        var (path, _) = selectedDirectory.Value;
        try
        {
            var storageInfo = await Task.Run(() =>
            {
                if (LeftSftpClient() is { } client)
                {
                    return client.GetStorageInfo(path);
                }

                return _localFileSystem.GetStorageInfo(path);
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                LeftStorageStatus = FormatStorageBadge(storageInfo);
            });
        }
        catch (Exception exc)
        {
            LogWriter.Write(AppPaths.TransferLogPath, $"Kunde inte läsa lagringsutrymme för {path}: {ExceptionText(exc)}");
        }
    }

    private (string Path, string Name)? TryGetSelectedLocalDirectory()
    {
        var selectedEntries = SelectedLocalEntries.Where(entry => entry.IsDirectory).ToList();
        if (selectedEntries.Count == 1)
        {
            return (selectedEntries[0].FullPath, selectedEntries[0].DisplayName);
        }

        if (selectedEntries.Count == 0 && SelectedLocalEntry is { IsDirectory: true } selectedEntry)
        {
            return (selectedEntry.FullPath, selectedEntry.DisplayName);
        }

        var selectedNodes = SelectedLocalTreeNodes.Where(node => !node.IsPlaceholder).ToList();
        if (selectedNodes.Count == 1)
        {
            return (selectedNodes[0].FullPath, $"{selectedNodes[0].Name}/");
        }

        if (selectedNodes.Count == 0 && SelectedLocalTreeNode is { IsPlaceholder: false } selectedNode)
        {
            return (selectedNode.FullPath, $"{selectedNode.Name}/");
        }

        return null;
    }

    private async Task UpdateSelectedRemoteStorageStatusAsync()
    {
        var selectedDirectory = TryGetSelectedRemoteDirectory();
        if (selectedDirectory is null)
        {
            return;
        }

        var (path, _) = selectedDirectory.Value;
        try
        {
            var storageInfo = await Task.Run(() =>
            {
                if (SelectedRightDevice?.IsLocal == true)
                {
                    return _localFileSystem.GetStorageInfo(path);
                }

                var client = RightSftpClient() ?? _sftpClient;
                return client.GetStorageInfo(path);
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RightStorageStatus = FormatStorageBadge(storageInfo);
            });
        }
        catch (Exception exc)
        {
            LogWriter.Write(AppPaths.TransferLogPath, $"Kunde inte läsa lagringsutrymme för {path}: {ExceptionText(exc)}");
        }
    }

    private (string Path, string Name)? TryGetSelectedRemoteDirectory()
    {
        var selectedEntries = SelectedRemoteEntries.Where(entry => entry.IsDirectory).ToList();
        if (selectedEntries.Count == 1)
        {
            return (RemotePathForEntry(selectedEntries[0]), selectedEntries[0].DisplayName);
        }

        if (selectedEntries.Count == 0 && SelectedRemoteEntry is { IsDirectory: true } selectedEntry)
        {
            return (RemotePathForEntry(selectedEntry), selectedEntry.DisplayName);
        }

        var selectedNodes = SelectedRemoteTreeNodes.Where(node => !node.IsPlaceholder).ToList();
        if (selectedNodes.Count == 1)
        {
            return (selectedNodes[0].FullPath, $"{selectedNodes[0].Name}/");
        }

        if (selectedNodes.Count == 0 && SelectedRemoteTreeNode is { IsPlaceholder: false } selectedNode)
        {
            return (selectedNode.FullPath, $"{selectedNode.Name}/");
        }

        return null;
    }

    private void InitializeLocalTreeRoot()
    {
        LocalTreeRoots.Clear();
        if (LeftSftpClient() is not null)
        {
            var root = CreateLocalTreeNode("/", "/");
            LocalTreeRoots.Add(root);
            root.IsExpanded = true;
            UpdateLocalTreePaneWidth();
            return;
        }

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

        var leftDeviceId = SelectedLeftDevice?.Id;
        var leftClient = LeftSftpClient();
        var isRemoteTree = leftClient is not null;
        var nodePath = node.FullPath;

        try
        {
            var directories = isRemoteTree
                ? await Task.Run(() => leftClient!.ListSubdirectories(nodePath))
                : await Task.Run(() => _localFileSystem.ListSubdirectories(nodePath));

            if (!IsSameSelectedLeftDevice(leftDeviceId))
            {
                return;
            }

            node.Children.Clear();
            foreach (var directory in directories)
            {
                var childPath = isRemoteTree
                    ? PosixPath.Join(nodePath, directory)
                    : Path.Combine(nodePath, directory);
                node.Children.Add(CreateLocalTreeNode(directory, childPath));
            }

            node.IsLoaded = true;
            ScheduleLocalTreeLayoutUpdate();
        }
        catch (Exception exc)
        {
            if (IsSameSelectedLeftDevice(leftDeviceId))
            {
                await ShowErrorAsync($"Kunde inte läsa lokalt mappträd: {ExceptionText(exc)}");
            }
            else
            {
                LogWriter.Write(AppPaths.TransferLogPath, $"Ignorerar inaktuell lokal trädläsning för {nodePath}: {ExceptionText(exc)}");
            }
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

        if (LeftSftpClient() is not null)
        {
            var normalizedRemote = PosixPath.Normalize(path);
            var currentRemote = LocalTreeRoots[0];
            currentRemote.IsExpanded = true;
            await EnsureLocalTreeNodeChildrenAsync(currentRemote);

            var builtPath = currentRemote.FullPath;
            foreach (var segment in PathSegments(normalizedRemote))
            {
                builtPath = builtPath == "/" ? $"/{segment}" : PosixPath.Join(builtPath, segment);
                var child = currentRemote.Children.FirstOrDefault(candidate =>
                    !candidate.IsPlaceholder &&
                    string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    child = CreateLocalTreeNode(segment, builtPath);
                    currentRemote.Children.Add(child);
                }

                currentRemote = child;
                currentRemote.IsExpanded = true;
                await EnsureLocalTreeNodeChildrenAsync(currentRemote);
            }

            WithSuppressedLocalTreeSelectionNavigation(() =>
            {
                SelectedLocalTreeNode = currentRemote;
            });
            UpdateLocalTreePaneWidth();
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
        InvalidateLocalDirectoryCache(path);

        var node = FindLocalTreeNode(_localFileSystem.Normalize(path));
        if (node is null)
        {
            return;
        }

        node.IsLoaded = false;
        node.Children.Clear();
        node.Children.Add(LocalTreeNode.CreatePlaceholder());
    }

    private void InvalidateLocalDirectoryCache(string path)
    {
        var cacheKey = LocalDirectoryCacheKey(path);
        if (cacheKey is not null)
        {
            _localDirectoryCache.Remove(cacheKey);
        }
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

    private bool IsSameSelectedLeftDevice(string? deviceId)
    {
        return string.Equals(SelectedLeftDevice?.Id, deviceId, StringComparison.OrdinalIgnoreCase);
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
        CancelAutoReconnect();
        _transferCancellation?.Cancel();
        _transferCancellation?.Dispose();
        _transferCancellation = null;
        SaveUiSettings();
        foreach (var client in _connectedSftpClients.Values.Distinct())
        {
            client.Dispose();
        }

        if (!_connectedSftpClients.Values.Contains(_sftpClient))
        {
            _sftpClient.Dispose();
        }
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

    private sealed record TransferSource(string Path, bool IsDirectory, string DisplayName, string SourceDeviceId);

    private sealed record CachedLocalDirectory(
        string Path,
        IReadOnlyList<LocalEntry> Entries,
        StorageInfo? StorageInfo,
        DirectoryContentSummary? ContentSummary,
        string Signature);

    private sealed record CachedRemoteDirectory(
        string Path,
        IReadOnlyList<RemoteEntry> Entries,
        StorageInfo? StorageInfo,
        DirectoryContentSummary? ContentSummary,
        string Signature);

    private readonly record struct ConnectionInputSnapshot(string Username, string Password);
}
