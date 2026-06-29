using CommunityToolkit.Mvvm.ComponentModel;

namespace FileTransferHelper.Models;

public sealed partial class RemoteEntry : ObservableObject
{
    public RemoteEntry(string name, bool isDirectory, long size = 0, string fullPath = "")
    {
        Name = name;
        IsDirectory = isDirectory;
        Size = size;
        FullPath = fullPath;
    }

    public string Name { get; }

    public bool IsDirectory { get; }

    public long Size { get; }

    public string FullPath { get; }

    public string DisplayName => IsDirectory ? $"{Name}/" : Name;

    [ObservableProperty]
    private bool _isDropTarget;

    public bool ShowClosedFolder => IsDirectory && !IsDropTarget;

    public bool ShowOpenFolder => IsDirectory && IsDropTarget;

    partial void OnIsDropTargetChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowClosedFolder));
        OnPropertyChanged(nameof(ShowOpenFolder));
    }
}
