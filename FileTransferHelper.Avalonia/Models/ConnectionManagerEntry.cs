using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media;

namespace FileTransferHelper.Models;

public sealed partial class ConnectionManagerEntry : ObservableObject
{
    public ConnectionManagerEntry(
        string name,
        string address,
        string source,
        string status,
        string username,
        string password,
        bool isConnected,
        bool isConnecting,
        bool isAutoReconnectTarget)
    {
        Name = name;
        Address = address;
        Source = source;
        Status = status;
        IsConnected = isConnected;
        IsConnecting = isConnecting;
        IsAutoReconnectTarget = isAutoReconnectTarget;
        UsernameInput = username;
        PasswordInput = password;
    }

    public string Name { get; }

    public string Address { get; }

    public string Source { get; }

    public string Status { get; }

    public bool IsConnected { get; }

    public bool IsConnecting { get; }

    public bool IsAutoReconnectTarget { get; }

    public bool IsDisconnected => !IsConnected;

    public bool ShowConnectAction => !IsConnected && !IsConnecting;

    public IBrush StatusBrush => IsConnected
        ? Brushes.LimeGreen
        : IsConnecting || IsAutoReconnectTarget
            ? Brushes.Goldenrod
            : Brushes.Gray;

    public bool ShowVisibleAddress => IsAddressVisible;

    public bool ShowMaskedAddress => !IsAddressVisible;

    public bool ShowVisibleReadOnlyUsername => IsConnected && IsUsernameVisible;

    public bool ShowMaskedReadOnlyUsername => IsConnected && !IsUsernameVisible;

    public bool ShowVisibleReadOnlyPassword => IsConnected && IsPasswordVisible;

    public bool ShowMaskedReadOnlyPassword => IsConnected && !IsPasswordVisible;

    public bool ShowVisibleUsernameInput => IsDisconnected;

    public bool ShowVisiblePasswordInput => IsDisconnected && IsPasswordVisible;

    public bool ShowMaskedPasswordInput => IsDisconnected && !IsPasswordVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaskedAddress))]
    [NotifyPropertyChangedFor(nameof(ShowVisibleAddress))]
    [NotifyPropertyChangedFor(nameof(ShowMaskedAddress))]
    private bool _isAddressVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaskedUsername))]
    [NotifyPropertyChangedFor(nameof(ShowVisibleReadOnlyUsername))]
    [NotifyPropertyChangedFor(nameof(ShowMaskedReadOnlyUsername))]
    private string _usernameInput = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaskedPassword))]
    [NotifyPropertyChangedFor(nameof(ShowVisibleReadOnlyPassword))]
    [NotifyPropertyChangedFor(nameof(ShowMaskedReadOnlyPassword))]
    [NotifyPropertyChangedFor(nameof(ShowVisiblePasswordInput))]
    [NotifyPropertyChangedFor(nameof(ShowMaskedPasswordInput))]
    private string _passwordInput = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVisibleReadOnlyUsername))]
    [NotifyPropertyChangedFor(nameof(ShowMaskedReadOnlyUsername))]
    [NotifyPropertyChangedFor(nameof(ShowVisibleUsernameInput))]
    private bool _isUsernameVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVisibleReadOnlyPassword))]
    [NotifyPropertyChangedFor(nameof(ShowMaskedReadOnlyPassword))]
    [NotifyPropertyChangedFor(nameof(ShowVisiblePasswordInput))]
    [NotifyPropertyChangedFor(nameof(ShowMaskedPasswordInput))]
    private bool _isPasswordVisible;

    public string MaskedAddress => string.IsNullOrEmpty(Address) ? "" : new string('•', Math.Min(Address.Length, 16));

    public string MaskedUsername => string.IsNullOrEmpty(UsernameInput) ? "" : new string('•', Math.Min(UsernameInput.Length, 12));

    public string MaskedPassword => string.IsNullOrEmpty(PasswordInput) ? "" : new string('•', Math.Min(PasswordInput.Length, 16));
}
