using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FileTransferHelper.Models;

public partial class RemoteTreeNode : ObservableObject
{
    public RemoteTreeNode(string name, string fullPath, bool isPlaceholder = false)
    {
        Name = name;
        FullPath = fullPath;
        IsPlaceholder = isPlaceholder;

        if (!isPlaceholder)
        {
            Children.Add(CreatePlaceholder());
        }
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsPlaceholder { get; }

    public bool IsLoaded { get; set; }

    public ObservableCollection<RemoteTreeNode> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isDropTarget;

    public bool ShowClosedFolder => !IsDropTarget;

    public bool ShowOpenFolder => IsDropTarget;

    partial void OnIsDropTargetChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowClosedFolder));
        OnPropertyChanged(nameof(ShowOpenFolder));
    }

    public Func<RemoteTreeNode, Task>? ExpandHandler { get; set; }

    public Action? LayoutChangedHandler { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !IsLoaded && !IsPlaceholder && ExpandHandler is not null)
        {
            _ = ExpandHandler(this);
        }

        LayoutChangedHandler?.Invoke();
    }

    public static RemoteTreeNode CreatePlaceholder() => new("", "", isPlaceholder: true);
}
