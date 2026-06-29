using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FileTransferHelper.Models;
using FileTransferHelper.Services;
using FileTransferHelper.ViewModels;

namespace FileTransferHelper.Views;

public partial class MainWindow : Window
{
    private PointerPressedEventArgs? _localDragPressEvent;
    private Point _localDragPressPoint;
    private InputElement? _localDragElement;
    private Func<IReadOnlyList<string>>? _localDragPathCollector;
    private bool _localDragStarted;
    private PixelRect? _normalWindowBounds;
    private RemoteTreeNode? _remoteDropTargetTreeNode;
    private RemoteEntry? _remoteDropTargetEntry;
    private TreeViewItem? _remoteDropTargetTreeItem;
    private ListBoxItem? _remoteDropTargetListItem;
    private bool _localTreeModifierSelection;
    private bool _remoteTreeModifierSelection;
    private LocalTreeNode? _lastSelectedLocalTreeNode;
    private RemoteTreeNode? _lastSelectedRemoteTreeNode;
    private LocalTreeNode? _localTreePendingNode;
    private RemoteTreeNode? _remoteTreePendingNode;
    private KeyModifiers _localTreePendingModifiers;
    private KeyModifiers _remoteTreePendingModifiers;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
        DataContextChanged += MainWindow_DataContextChanged;
        LocalTree.SelectionChanged += LocalTree_SelectionChanged;
        RemoteTree.SelectionChanged += RemoteTree_SelectionChanged;
        LocalTree.AddHandler(InputElement.PointerPressedEvent, LocalTree_PointerPressed, RoutingStrategies.Tunnel);
        RemoteTree.AddHandler(InputElement.PointerPressedEvent, RemoteTree_PointerPressed, RoutingStrategies.Tunnel);
        Opened += MainWindow_Opened;
        Loaded += MainWindow_Loaded;
        PositionChanged += (_, _) => CaptureNormalWindowBounds();
        SizeChanged += (_, _) => CaptureNormalWindowBounds();

        ConfigureLocalDragSource(LocalList, TryBeginLocalListDrag);
        ConfigureLocalDragSource(LocalTree, TryBeginLocalTreeDrag);

        AddHandler(InputElement.PointerMovedEvent, LocalDragSource_PointerMoved, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerReleasedEvent, LocalDragSource_PointerReleased, RoutingStrategies.Tunnel);

        LocalList.DoubleTapped += LocalList_DoubleTapped;

        ConfigureRemoteDropTarget(ConnectionPanel);
        ConfigureRemoteDropTarget(RemotePanel);
        ConfigureRemoteDropTarget(RemotePathBorder);
        ConfigureRemoteDropTarget(RemoteExplorerGrid);
        ConfigureRemoteDropTarget(RemoteList);
        ConfigureRemoteDropTarget(RemoteTree);

        ConfigureRemoteContextSelection(RemoteTree);
        ConfigureRemoteContextSelection(RemoteList);

        ConfigureLocalContextSelection(LocalTree);
        ConfigureLocalContextSelection(LocalList);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty)
        {
            CaptureNormalWindowBounds();
        }
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void MainWindow_Opened(object? sender, EventArgs e)
    {
        ViewModel?.ApplyWindowSettings(this);
        CaptureNormalWindowBounds();
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        ViewModel?.RequestRemoteTreeLayoutUpdate();
        ViewModel?.RequestLocalTreeLayoutUpdate();
    }

    private async void PasswordBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is null)
        {
            return;
        }

        e.Handled = true;
        if (ViewModel.ConnectCommand.CanExecute(null))
        {
            await ViewModel.ConnectCommand.ExecuteAsync(null);
        }
    }

    private void MainWindow_DataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.RemoteTreeLayoutChanged += ViewModel_RemoteTreeLayoutChanged;
            ViewModel.LocalTreeLayoutChanged += ViewModel_LocalTreeLayoutChanged;
        }
    }

    private void ViewModel_RemoteTreeLayoutChanged(object? sender, EventArgs e)
    {
        ScrollSelectedRemoteTreeItemIntoView();
    }

    private void ViewModel_LocalTreeLayoutChanged(object? sender, EventArgs e)
    {
        ScrollSelectedLocalTreeItemIntoView();
    }

    private void LocalTree_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Control source)
        {
            return;
        }

        if (source.FindAncestorOfType<ToggleButton>() is { Name: "PART_ExpandCollapseChevron" })
        {
            return;
        }

        var treeItem = source.FindAncestorOfType<TreeViewItem>();
        if (treeItem?.DataContext is not LocalTreeNode { IsPlaceholder: false } clickedNode)
        {
            return;
        }

        var modifiers = e.KeyModifiers;
        _localTreeModifierSelection = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Shift);
        
        if (_localTreeModifierSelection)
        {
            _localTreePendingNode = clickedNode;
            _localTreePendingModifiers = modifiers;
        }
        else
        {
            _localTreePendingNode = null;
            _lastSelectedLocalTreeNode = clickedNode;
        }
    }

    private void RemoteTree_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Control source)
        {
            return;
        }

        if (source.FindAncestorOfType<ToggleButton>() is { Name: "PART_ExpandCollapseChevron" })
        {
            return;
        }

        var treeItem = source.FindAncestorOfType<TreeViewItem>();
        if (treeItem?.DataContext is not RemoteTreeNode { IsPlaceholder: false } clickedNode)
        {
            return;
        }

        var modifiers = e.KeyModifiers;
        _remoteTreeModifierSelection = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Shift);
        
        if (_remoteTreeModifierSelection)
        {
            _remoteTreePendingNode = clickedNode;
            _remoteTreePendingModifiers = modifiers;
        }
        else
        {
            _remoteTreePendingNode = null;
            _lastSelectedRemoteTreeNode = clickedNode;
        }
    }

    private List<LocalTreeNode> CollectAllVisibleLocalTreeNodes()
    {
        var result = new List<LocalTreeNode>();
        if (ViewModel?.LocalTreeRoots is null)
        {
            return result;
        }

        foreach (var root in ViewModel.LocalTreeRoots)
        {
            CollectVisibleLocalNodes(root, result);
        }

        return result;
    }

    private void CollectVisibleLocalNodes(LocalTreeNode node, List<LocalTreeNode> result)
    {
        if (node.IsPlaceholder)
        {
            return;
        }

        result.Add(node);

        if (node.IsExpanded)
        {
            foreach (var child in node.Children)
            {
                CollectVisibleLocalNodes(child, result);
            }
        }
    }

    private List<RemoteTreeNode> CollectAllVisibleRemoteTreeNodes()
    {
        var result = new List<RemoteTreeNode>();
        if (ViewModel?.RemoteTreeRoots is null)
        {
            return result;
        }

        foreach (var root in ViewModel.RemoteTreeRoots)
        {
            CollectVisibleRemoteNodes(root, result);
        }

        return result;
    }

    private void CollectVisibleRemoteNodes(RemoteTreeNode node, List<RemoteTreeNode> result)
    {
        if (node.IsPlaceholder)
        {
            return;
        }

        result.Add(node);

        if (node.IsExpanded)
        {
            foreach (var child in node.Children)
            {
                CollectVisibleRemoteNodes(child, result);
            }
        }
    }

    private void LocalTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.SelectedLocalEntry = null;

        // Handle multi-selection with Ctrl/Shift
        if (_localTreePendingNode is not null)
        {
            var clickedNode = _localTreePendingNode;
            var modifiers = _localTreePendingModifiers;
            _localTreePendingNode = null;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var selectedItems = LocalTree.SelectedItems;
                if (selectedItems is null)
                {
                    return;
                }

                if (modifiers.HasFlag(KeyModifiers.Control))
                {
                    // Control: toggle the clicked item
                    if (selectedItems.Contains(clickedNode))
                    {
                        selectedItems.Remove(clickedNode);
                    }
                    else
                    {
                        if (!selectedItems.Contains(clickedNode))
                        {
                            selectedItems.Add(clickedNode);
                        }
                    }
                    _lastSelectedLocalTreeNode = clickedNode;
                }
                else if (modifiers.HasFlag(KeyModifiers.Shift))
                {
                    // Shift: select range
                    var lastNode = _lastSelectedLocalTreeNode ?? selectedItems.OfType<LocalTreeNode>().FirstOrDefault();
                    if (lastNode is not null)
                    {
                        var allNodes = CollectAllVisibleLocalTreeNodes();
                        var startIndex = allNodes.IndexOf(lastNode);
                        var endIndex = allNodes.IndexOf(clickedNode);

                        if (startIndex != -1 && endIndex != -1)
                        {
                            if (startIndex > endIndex)
                            {
                                (startIndex, endIndex) = (endIndex, startIndex);
                            }

                            selectedItems.Clear();
                            for (int i = startIndex; i <= endIndex; i++)
                            {
                                selectedItems.Add(allNodes[i]);
                            }
                        }
                    }
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
            
            return;
        }

        if (LocalTree.SelectedItem is LocalTreeNode selected
            && !ReferenceEquals(ViewModel.SelectedLocalTreeNode, selected))
        {
            ViewModel.WithSuppressedLocalTreeSelectionNavigation(() =>
                ViewModel.SelectedLocalTreeNode = selected);
        }

        if (!_localTreeModifierSelection
            && !ViewModel.IsLocalTreeSelectionNavigationSuppressed
            && LocalTree.SelectedItem is LocalTreeNode { IsPlaceholder: false } navigateNode)
        {
            ViewModel.NavigateToLocalTreeNodeIfNeeded(navigateNode);
        }

        _localTreeModifierSelection = false;
        ScrollSelectedLocalTreeItemIntoView();
    }

    private void RemoteTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.SelectedRemoteEntry = null;

        // Handle multi-selection with Ctrl/Shift
        if (_remoteTreePendingNode is not null)
        {
            var clickedNode = _remoteTreePendingNode;
            var modifiers = _remoteTreePendingModifiers;
            _remoteTreePendingNode = null;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var selectedItems = RemoteTree.SelectedItems;
                if (selectedItems is null)
                {
                    return;
                }

                if (modifiers.HasFlag(KeyModifiers.Control))
                {
                    // Control: toggle the clicked item
                    if (selectedItems.Contains(clickedNode))
                    {
                        selectedItems.Remove(clickedNode);
                    }
                    else
                    {
                        if (!selectedItems.Contains(clickedNode))
                        {
                            selectedItems.Add(clickedNode);
                        }
                    }
                    _lastSelectedRemoteTreeNode = clickedNode;
                }
                else if (modifiers.HasFlag(KeyModifiers.Shift))
                {
                    // Shift: select range
                    var lastNode = _lastSelectedRemoteTreeNode ?? selectedItems.OfType<RemoteTreeNode>().FirstOrDefault();
                    if (lastNode is not null)
                    {
                        var allNodes = CollectAllVisibleRemoteTreeNodes();
                        var startIndex = allNodes.IndexOf(lastNode);
                        var endIndex = allNodes.IndexOf(clickedNode);

                        if (startIndex != -1 && endIndex != -1)
                        {
                            if (startIndex > endIndex)
                            {
                                (startIndex, endIndex) = (endIndex, startIndex);
                            }

                            selectedItems.Clear();
                            for (int i = startIndex; i <= endIndex; i++)
                            {
                                selectedItems.Add(allNodes[i]);
                            }
                        }
                    }
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
            
            return;
        }

        if (RemoteTree.SelectedItem is RemoteTreeNode selected
            && !ReferenceEquals(ViewModel.SelectedRemoteTreeNode, selected))
        {
            ViewModel.WithSuppressedTreeSelectionNavigation(() =>
                ViewModel.SelectedRemoteTreeNode = selected);
        }

        if (!_remoteTreeModifierSelection
            && !ViewModel.IsRemoteTreeSelectionNavigationSuppressed
            && RemoteTree.SelectedItem is RemoteTreeNode { IsPlaceholder: false } navigateNode)
        {
            ViewModel.NavigateToRemoteTreeNodeIfNeeded(navigateNode);
        }

        _remoteTreeModifierSelection = false;
        ScrollSelectedRemoteTreeItemIntoView();
    }

    private IReadOnlyList<string>? TryBeginLocalListDrag(PointerPressedEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>()?.DataContext is not LocalEntry entry)
        {
            return null;
        }

        if (LocalList.SelectedItems?.Contains(entry) != true)
        {
            LocalList.SelectedItem = entry;
        }

        var paths = CollectLocalDragPaths();
        return paths.Count > 0 ? paths : null;
    }

    private IReadOnlyList<string>? TryBeginLocalTreeDrag(PointerPressedEventArgs e)
    {
        if (e.Source is Control source && source.FindAncestorOfType<ToggleButton>() is { Name: "PART_ExpandCollapseChevron" })
        {
            return null;
        }

        if ((e.Source as Control)?.FindAncestorOfType<TreeViewItem>()?.DataContext is not LocalTreeNode { IsPlaceholder: false } node)
        {
            return null;
        }

        var selectedPaths = LocalTree.SelectedItems?
            .OfType<LocalTreeNode>()
            .Where(treeNode => !treeNode.IsPlaceholder && Directory.Exists(treeNode.FullPath))
            .Select(treeNode => treeNode.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        if (selectedPaths.Count > 0)
        {
            return selectedPaths;
        }

        return Directory.Exists(node.FullPath) ? [node.FullPath] : null;
    }

    private void ConfigureLocalDragSource(InputElement element, Func<PointerPressedEventArgs, IReadOnlyList<string>?> beginDrag)
    {
        element.AddHandler(InputElement.PointerPressedEvent, (sender, e) =>
        {
            if (!e.GetCurrentPoint(element).Properties.IsLeftButtonPressed)
            {
                return;
            }

            var paths = beginDrag(e);
            if (paths is null || paths.Count == 0)
            {
                return;
            }

            _localDragPressEvent = e;
            _localDragPressPoint = e.GetPosition(this);
            _localDragElement = element;
            _localDragPathCollector = element == LocalTree
                ? () => paths
                : CollectLocalDragPaths;

            _localDragStarted = false;
        }, RoutingStrategies.Tunnel);
    }

    private async void LocalDragSource_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_localDragPressEvent is null || _localDragElement is null || _localDragPathCollector is null || _localDragStarted)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ResetLocalDragState();
            return;
        }

        var position = e.GetPosition(this);
        var delta = position - _localDragPressPoint;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
        {
            return;
        }

        var paths = _localDragPathCollector();
        if (paths.Count == 0)
        {
            ResetLocalDragState();
            return;
        }

        _localDragStarted = true;
        var trigger = _localDragPressEvent;
        ResetLocalDragState();

        var dataTransfer = new DataTransfer();
        dataTransfer.Add(DataTransferItem.Create(DragDropFormats.LocalPathsDataFormat, string.Join('\n', paths)));
        await DragDrop.DoDragDropAsync(trigger, dataTransfer, DragDropEffects.Copy);
        _localDragStarted = false;
        ClearRemoteDropHighlights();
    }

    private void LocalDragSource_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_localDragPressEvent is null)
        {
            return;
        }

        ResetLocalDragState();
    }

    private void ResetLocalDragState()
    {
        _localDragPressEvent = null;
        _localDragElement = null;
        _localDragPathCollector = null;
        _localDragStarted = false;
    }

    private List<string> CollectLocalDragPaths()
    {
        var selected = LocalList.SelectedItems?.OfType<LocalEntry>().Select(entry => entry.FullPath).ToList() ?? [];
        if (selected.Count > 0)
        {
            return selected;
        }

        return LocalList.SelectedItem is LocalEntry entry ? [entry.FullPath] : [];
    }

    private void ConfigureLocalContextSelection(InputElement target)
    {
        target.AddHandler(InputElement.PointerPressedEvent, LocalItem_PointerPressed, RoutingStrategies.Tunnel);
    }

    private void LocalItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || !e.GetCurrentPoint(sender as Visual).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (e.Source is not Control source)
        {
            return;
        }

        if (source.FindAncestorOfType<TreeViewItem>()?.DataContext is LocalTreeNode { IsPlaceholder: false } treeNode)
        {
            ViewModel.SelectedLocalEntry = null;
            if (LocalTree.SelectedItems?.Contains(treeNode) != true)
            {
                LocalTree.SelectedItem = treeNode;
            }

            return;
        }

        if (source.FindAncestorOfType<ListBoxItem>()?.DataContext is LocalEntry entry)
        {
            ViewModel.SelectedLocalEntry = entry;
        }
    }

    private void ConfigureRemoteContextSelection(InputElement target)
    {
        target.AddHandler(InputElement.PointerPressedEvent, RemoteItem_PointerPressed, RoutingStrategies.Tunnel);
    }

    private void RemoteItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || !e.GetCurrentPoint(sender as Visual).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (e.Source is not Control source)
        {
            return;
        }

        if (source.FindAncestorOfType<TreeViewItem>()?.DataContext is RemoteTreeNode { IsPlaceholder: false } treeNode)
        {
            ViewModel.SelectedRemoteEntry = null;
            if (RemoteTree.SelectedItems?.Contains(treeNode) != true)
            {
                RemoteTree.SelectedItem = treeNode;
            }

            return;
        }

        if (source.FindAncestorOfType<ListBoxItem>()?.DataContext is RemoteEntry entry)
        {
            ViewModel.SelectedRemoteEntry = entry;
        }
    }

    private void ConfigureRemoteDropTarget(InputElement target)
    {
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, RemoteDropTarget_DragOver);
        target.AddHandler(DragDrop.DropEvent, RemoteDropTarget_Drop);
    }

    private void RemoteDropTarget_DragOver(object? sender, DragEventArgs e)
    {
        if (!ContainsLocalDragPaths(e))
        {
            ClearRemoteDropHighlights();
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        UpdateRemoteDropHighlight(e);
    }

    private void UpdateRemoteDropHighlight(DragEventArgs e)
    {
        RemoteTreeNode? treeNode = null;
        RemoteEntry? entry = null;
        TreeViewItem? treeItem = null;
        ListBoxItem? listItem = null;

        if (e.Source is Control source)
        {
            treeItem = source.FindAncestorOfType<TreeViewItem>();
            if (treeItem?.DataContext is RemoteTreeNode { IsPlaceholder: false } node)
            {
                treeNode = node;
            }
            else
            {
                listItem = source.FindAncestorOfType<ListBoxItem>();
                if (listItem?.DataContext is RemoteEntry listEntry && listEntry.IsDirectory)
                {
                    entry = listEntry;
                }
            }
        }

        if (ReferenceEquals(treeNode, _remoteDropTargetTreeNode) && ReferenceEquals(entry, _remoteDropTargetEntry))
        {
            return;
        }

        ClearRemoteDropHighlights();

        if (treeNode is not null)
        {
            treeNode.IsDropTarget = true;
            _remoteDropTargetTreeNode = treeNode;
            if (treeItem is not null)
            {
                treeItem.Classes.Add("drop-target");
                _remoteDropTargetTreeItem = treeItem;
            }
        }
        else if (entry is not null)
        {
            entry.IsDropTarget = true;
            _remoteDropTargetEntry = entry;
            if (listItem is not null)
            {
                listItem.Classes.Add("drop-target");
                _remoteDropTargetListItem = listItem;
            }
        }
    }

    private void ClearRemoteDropHighlights()
    {
        if (_remoteDropTargetTreeNode is not null)
        {
            _remoteDropTargetTreeNode.IsDropTarget = false;
            _remoteDropTargetTreeNode = null;
        }

        if (_remoteDropTargetEntry is not null)
        {
            _remoteDropTargetEntry.IsDropTarget = false;
            _remoteDropTargetEntry = null;
        }

        if (_remoteDropTargetTreeItem is not null)
        {
            _remoteDropTargetTreeItem.Classes.Remove("drop-target");
            _remoteDropTargetTreeItem = null;
        }

        if (_remoteDropTargetListItem is not null)
        {
            _remoteDropTargetListItem.Classes.Remove("drop-target");
            _remoteDropTargetListItem = null;
        }
    }

    private async void RemoteDropTarget_Drop(object? sender, DragEventArgs e)
    {
        ClearRemoteDropHighlights();

        if (ViewModel is null || !TryReadLocalDragPaths(e, out var paths))
        {
            return;
        }

        e.Handled = true;
        var destination = ResolveRemoteDropDestination(e);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        await ViewModel.TransferPathsAsync(paths, destination);
    }

    private string? ResolveRemoteDropDestination(DragEventArgs e)
    {
        if (e.Source is not Control source || ViewModel is null)
        {
            return ViewModel?.RemotePath;
        }

        if (source.FindAncestorOfType<TreeViewItem>()?.DataContext is RemoteTreeNode { IsPlaceholder: false } treeNode)
        {
            return treeNode.FullPath;
        }

        if (source.FindAncestorOfType<ListBoxItem>()?.DataContext is RemoteEntry entry && entry.IsDirectory)
        {
            return ViewModel.RemotePathForEntry(entry);
        }

        return ViewModel.RemotePath;
    }

    private static bool ContainsLocalDragPaths(DragEventArgs e)
    {
        return e.DataTransfer.Contains(DragDropFormats.LocalPathsDataFormat);
    }

    private static bool TryReadLocalDragPaths(DragEventArgs e, out List<string> paths)
    {
        paths = [];
        var payload = e.DataTransfer.TryGetValue(DragDropFormats.LocalPathsDataFormat);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        paths = payload
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return paths.Count > 0;
    }

    private void LocalTreeItem_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source && source.FindAncestorOfType<ToggleButton>() is { Name: "PART_ExpandCollapseChevron" })
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        if (e.Source is not Control tapped || tapped.FindAncestorOfType<TreeViewItem>() is not { DataContext: LocalTreeNode node })
        {
            return;
        }

        if (ReferenceEquals(LocalTree.SelectedItem, node))
        {
            ViewModel?.NavigateToLocalTreeNode(node);
        }
    }

    private void RemoteTreeItem_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source && source.FindAncestorOfType<ToggleButton>() is { Name: "PART_ExpandCollapseChevron" })
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        if (e.Source is not Control tapped || tapped.FindAncestorOfType<TreeViewItem>() is not { DataContext: RemoteTreeNode node })
        {
            return;
        }

        if (ReferenceEquals(RemoteTree.SelectedItem, node))
        {
            ViewModel?.NavigateToRemoteTreeNode(node);
        }
    }

    private void ScrollSelectedLocalTreeItemIntoView()
    {
        if (LocalTree.SelectedItem is null)
        {
            return;
        }

        LocalTree.ScrollIntoView(LocalTree.SelectedItem);
    }

    private void ScrollSelectedRemoteTreeItemIntoView()
    {
        if (RemoteTree.SelectedItem is null)
        {
            return;
        }

        RemoteTree.ScrollIntoView(RemoteTree.SelectedItem);
    }

    private async void LocalList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control source)
        {
            return;
        }

        if (source.FindAncestorOfType<ListBoxItem>()?.DataContext is not LocalEntry { IsDirectory: true } entry)
        {
            return;
        }

        if (ViewModel is not null)
        {
            await ViewModel.EnterLocalEntryAsync(entry);
            e.Handled = true;
        }
    }

    private async void RemoteItem_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: RemoteEntry entry })
        {
            return;
        }

        if (ViewModel is not null)
        {
            await ViewModel.EnterRemoteEntryAsync(entry);
            e.Handled = true;
        }
    }

    private async void LocalPathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel is not null)
        {
            await ViewModel.RefreshLocalDirsCommand.ExecuteAsync(null);
        }
    }

    private async void RemotePathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel is not null)
        {
            await ViewModel.RefreshRemoteDirsCommand.ExecuteAsync(null);
        }
    }

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        SaveTreeColumnWidthsFromLayout();
        SaveLocalPanelWidthFromLayout();
        CaptureNormalWindowBounds();
        ViewModel?.CaptureWindowSettings(this, _normalWindowBounds);
        ViewModel?.SaveUiSettings();
        ViewModel?.Dispose();
    }

    private void CaptureNormalWindowBounds()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        _normalWindowBounds = new PixelRect(
            Position,
            new PixelSize(Math.Max(1, (int)Math.Round(Width)), Math.Max(1, (int)Math.Round(Height))));
    }

    private void LocalTreeSplitter_DragCompleted(object? sender, VectorEventArgs e)
    {
        SaveTreeColumnWidthsFromLayout();
    }

    private void RemoteTreeSplitter_DragCompleted(object? sender, VectorEventArgs e)
    {
        SaveTreeColumnWidthsFromLayout();
    }

    private void MainPanelSplitter_DragCompleted(object? sender, VectorEventArgs e)
    {
        SaveLocalPanelWidthFromLayout();
    }

    private void SaveTreeColumnWidthsFromLayout()
    {
        if (ViewModel is null)
        {
            return;
        }

        var localWidth = LocalExplorerGrid.ColumnDefinitions[0].ActualWidth;
        var remoteWidth = RemoteExplorerGrid.ColumnDefinitions[0].ActualWidth;
        if (localWidth > 0 && remoteWidth > 0)
        {
            ViewModel.SaveTreeColumnWidths(localWidth, remoteWidth);
        }
    }

    private void SaveLocalPanelWidthFromLayout()
    {
        if (ViewModel is null)
        {
            return;
        }

        var panelWidth = MainPanelsGrid.ColumnDefinitions[0].ActualWidth;
        if (panelWidth > 0)
        {
            ViewModel.SaveLocalPanelWidth(panelWidth);
        }
    }
}
