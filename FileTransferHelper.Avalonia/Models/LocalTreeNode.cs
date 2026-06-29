using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FileTransferHelper.Models;

public partial class LocalTreeNode : ObservableObject
{
    public LocalTreeNode(string name, string fullPath, bool isPlaceholder = false)
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

    public ObservableCollection<LocalTreeNode> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    public Func<LocalTreeNode, Task>? ExpandHandler { get; set; }

    public Action? LayoutChangedHandler { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !IsLoaded && !IsPlaceholder && ExpandHandler is not null)
        {
            _ = ExpandHandler(this);
        }

        LayoutChangedHandler?.Invoke();
    }

    public static LocalTreeNode CreatePlaceholder() => new("", "", isPlaceholder: true);
}
