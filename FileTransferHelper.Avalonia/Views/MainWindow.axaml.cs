using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
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
    private LocalTreeNode? _localTreeAnchorNode;
    private RemoteTreeNode? _remoteTreeAnchorNode;
    private LocalEntry? _localListAnchorEntry;
    private RemoteEntry? _remoteListAnchorEntry;
    private bool _localTreeMarqueeActive;
    private bool _localListMarqueeActive;
    private bool _remoteTreeMarqueeActive;
    private bool _remoteListMarqueeActive;
    private bool _remoteTreeMarqueePending;
    private bool _localTreeMarqueePending;
    private bool _localListMarqueePending;
    private bool _remoteListMarqueePending;
    private bool _suppressNextLocalTreeTap;
    private LocalTreeNode? _pendingLocalTreeMultiSelectClickNode;
    private Point _localTreeMarqueePressPoint;
    private Point _localListMarqueePressPoint;
    private Point _remoteTreeMarqueePressPoint;
    private Point _remoteListMarqueePressPoint;
    private Point _localTreeMarqueeStart;
    private Point _localListMarqueeStart;
    private Point _remoteTreeMarqueeStart;
    private Point _remoteListMarqueeStart;
    private Rectangle? _localTreeMarqueeShape;
    private Rectangle? _localListMarqueeShape;
    private Rectangle? _remoteTreeMarqueeShape;
    private Rectangle? _remoteListMarqueeShape;
    private IPointer? _localTreeMarqueePointer;
    private IPointer? _localListMarqueePointer;
    private IPointer? _remoteTreeMarqueePointer;
    private IPointer? _remoteListMarqueePointer;

    private const double MarqueeDragThreshold = 4;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
        DataContextChanged += MainWindow_DataContextChanged;
        LocalTree.SelectionChanged += LocalTree_SelectionChanged;
        LocalList.SelectionChanged += LocalList_SelectionChanged;
        RemoteTree.SelectionChanged += RemoteTree_SelectionChanged;
        RemoteList.SelectionChanged += RemoteList_SelectionChanged;
        LocalTree.AddHandler(InputElement.PointerPressedEvent, LocalTree_PointerPressed, RoutingStrategies.Tunnel);
        LocalList.AddHandler(InputElement.PointerPressedEvent, LocalList_PointerPressed, RoutingStrategies.Tunnel);
        RemoteTree.AddHandler(InputElement.PointerPressedEvent, RemoteTree_PointerPressed, RoutingStrategies.Tunnel);
        RemoteList.AddHandler(InputElement.PointerPressedEvent, RemoteList_PointerPressed, RoutingStrategies.Tunnel);
        LocalTree.AddHandler(InputElement.PointerReleasedEvent, LocalTree_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        LocalList.AddHandler(InputElement.PointerReleasedEvent, LocalList_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        RemoteTree.AddHandler(InputElement.PointerReleasedEvent, RemoteTree_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        RemoteList.AddHandler(InputElement.PointerReleasedEvent, RemoteList_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
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

    private static KeyModifiers GetPointerModifiers(PointerEventArgs e) => e.KeyModifiers;

    private void LocalTree_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(LocalTree).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is not Control source)
        {
            return;
        }

        if (source.FindAncestorOfType<ToggleButton>() is { Name: "PART_ExpandCollapseChevron" })
        {
            return;
        }

        var pressPoint = e.GetPosition(LocalTree);
        if (source.FindAncestorOfType<TreeViewItem>()?.DataContext is LocalTreeNode { IsPlaceholder: false } clickedNode)
        {
            var modifiers = GetPointerModifiers(e);
            if (modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                _localTreeModifierSelection = true;
                ApplyLocalTreeModifierSelection(clickedNode, modifiers);
                TreeSelectionHelper.SyncSelectionVisuals(LocalTree);
                ScrollSelectedLocalTreeItemIntoView();
                return;
            }

            if (LocalTree.SelectedItems?.Count > 1 && LocalTree.SelectedItems.Contains(clickedNode))
            {
                e.Handled = true;
                _localTreeModifierSelection = true;
                _localTreeMarqueePending = false;
                _localTreeAnchorNode ??= clickedNode;
                _suppressNextLocalTreeTap = true;
                _pendingLocalTreeMultiSelectClickNode = clickedNode;
                TreeSelectionHelper.SyncSelectionVisuals(LocalTree);
                return;
            }

            _pendingLocalTreeMultiSelectClickNode = null;
            _localTreeModifierSelection = false;
            _localTreeAnchorNode = clickedNode;
            _localTreeMarqueePending = true;
            _localTreeMarqueePressPoint = pressPoint;
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            BeginLocalTreeMarquee(e, pressPoint);
        }
    }

    private void RemoteTree_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(RemoteTree).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is not Control source)
        {
            return;
        }

        if (source.FindAncestorOfType<ToggleButton>() is { Name: "PART_ExpandCollapseChevron" })
        {
            return;
        }

        var pressPoint = e.GetPosition(RemoteTree);
        if (source.FindAncestorOfType<TreeViewItem>()?.DataContext is RemoteTreeNode { IsPlaceholder: false } clickedNode)
        {
            var modifiers = GetPointerModifiers(e);
            if (modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                _remoteTreeModifierSelection = true;
                ApplyRemoteTreeModifierSelection(clickedNode, modifiers);
                TreeSelectionHelper.SyncSelectionVisuals(RemoteTree);
                ScrollSelectedRemoteTreeItemIntoView();
                return;
            }

            _remoteTreeModifierSelection = false;
            _remoteTreeAnchorNode = clickedNode;
            _remoteTreeMarqueePending = true;
            _remoteTreeMarqueePressPoint = pressPoint;
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            BeginRemoteTreeMarquee(e, pressPoint);
        }
    }

    private void LocalList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(LocalList).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is not Control source)
        {
            return;
        }

        var pressPoint = e.GetPosition(LocalList);
        if (source.FindAncestorOfType<ListBoxItem>()?.DataContext is LocalEntry entry)
        {
            var modifiers = GetPointerModifiers(e);
            if (modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                return;
            }

            if (LocalList.SelectedItems?.Contains(entry) == true)
            {
                return;
            }

            _localListMarqueePending = true;
            _localListMarqueePressPoint = pressPoint;
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            BeginLocalListMarquee(e, pressPoint);
        }
    }

    private void RemoteList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(RemoteList).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is not Control source)
        {
            return;
        }

        var pressPoint = e.GetPosition(RemoteList);
        if (source.FindAncestorOfType<ListBoxItem>()?.DataContext is RemoteEntry)
        {
            var modifiers = GetPointerModifiers(e);
            if (modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                return;
            }

            _remoteListMarqueePending = true;
            _remoteListMarqueePressPoint = pressPoint;
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            BeginRemoteListMarquee(e, pressPoint);
        }
    }

    private void LocalTree_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (_localTreeMarqueeActive)
        {
            return;
        }

        if (_pendingLocalTreeMultiSelectClickNode is { } pendingNode)
        {
            _pendingLocalTreeMultiSelectClickNode = null;
            _suppressNextLocalTreeTap = true;
            e.Handled = true;
            CompleteLocalTreeMultiSelectClick(pendingNode);
            return;
        }

        if (e.Source is not Control source)
        {
            return;
        }

        if (source.FindAncestorOfType<ToggleButton>() is { Name: "PART_ExpandCollapseChevron" })
        {
            return;
        }

        if (source.FindAncestorOfType<TreeViewItem>()?.DataContext is not LocalTreeNode { IsPlaceholder: false } clickedNode)
        {
            return;
        }

        var modifiers = GetPointerModifiers(e);
        if (!modifiers.HasFlag(KeyModifiers.Control) && !modifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        e.Handled = true;
    }

    private void RemoteTree_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (_remoteTreeMarqueeActive)
        {
            return;
        }

        if (e.Source is not Control source)
        {
            return;
        }

        if (source.FindAncestorOfType<ToggleButton>() is { Name: "PART_ExpandCollapseChevron" })
        {
            return;
        }

        if (source.FindAncestorOfType<TreeViewItem>()?.DataContext is not RemoteTreeNode { IsPlaceholder: false } clickedNode)
        {
            return;
        }

        var modifiers = GetPointerModifiers(e);
        if (!modifiers.HasFlag(KeyModifiers.Control) && !modifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        e.Handled = true;
    }

    private void LocalList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (_localListMarqueeActive)
        {
            EndLocalListMarquee();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        _localListMarqueePending = false;

        if (e.Source is not Control source)
        {
            return;
        }

        if (source.FindAncestorOfType<ListBoxItem>()?.DataContext is not LocalEntry clickedEntry)
        {
            return;
        }

        var modifiers = GetPointerModifiers(e);
        if (!modifiers.HasFlag(KeyModifiers.Control) && !modifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        e.Handled = true;
        ApplyLocalListModifierSelection(clickedEntry, modifiers);
        TreeSelectionHelper.SyncSelectionVisuals(LocalList);
    }

    private void RemoteList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (_remoteListMarqueeActive)
        {
            EndRemoteListMarquee();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        _remoteListMarqueePending = false;

        if (e.Source is not Control source)
        {
            return;
        }

        if (source.FindAncestorOfType<ListBoxItem>()?.DataContext is not RemoteEntry clickedEntry)
        {
            return;
        }

        var modifiers = GetPointerModifiers(e);
        if (!modifiers.HasFlag(KeyModifiers.Control) && !modifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        e.Handled = true;
        ApplyRemoteListModifierSelection(clickedEntry, modifiers);
        TreeSelectionHelper.SyncSelectionVisuals(RemoteList);
    }

    private void BeginLocalTreeMarquee(PointerPressedEventArgs e, Point startPoint)
    {
        _localTreeMarqueeActive = true;
        _localTreeMarqueePending = false;
        _localTreeMarqueeStart = startPoint;
        _localTreeModifierSelection = true;
        _localTreeMarqueePointer = e.Pointer;
        _localTreeMarqueePointer.Capture(LocalTree);
        e.Handled = true;
        UpdateLocalTreeMarquee(_localTreeMarqueeStart, _localTreeMarqueeStart);
    }

    private void BeginLocalListMarquee(PointerPressedEventArgs e, Point startPoint)
    {
        _localListMarqueeActive = true;
        _localListMarqueePending = false;
        _localListMarqueeStart = startPoint;
        _localListMarqueePointer = e.Pointer;
        _localListMarqueePointer.Capture(LocalList);
        ResetLocalDragState();
        e.Handled = true;
        UpdateLocalListMarquee(_localListMarqueeStart, _localListMarqueeStart);
    }

    private void BeginRemoteTreeMarquee(PointerPressedEventArgs e, Point startPoint)
    {
        _remoteTreeMarqueeActive = true;
        _remoteTreeMarqueePending = false;
        _remoteTreeMarqueeStart = startPoint;
        _remoteTreeModifierSelection = true;
        _remoteTreeMarqueePointer = e.Pointer;
        _remoteTreeMarqueePointer.Capture(RemoteTree);
        e.Handled = true;
        UpdateRemoteTreeMarquee(_remoteTreeMarqueeStart, _remoteTreeMarqueeStart);
    }

    private void BeginRemoteListMarquee(PointerPressedEventArgs e, Point startPoint)
    {
        _remoteListMarqueeActive = true;
        _remoteListMarqueePending = false;
        _remoteListMarqueeStart = startPoint;
        _remoteListMarqueePointer = e.Pointer;
        _remoteListMarqueePointer.Capture(RemoteList);
        e.Handled = true;
        UpdateRemoteListMarquee(_remoteListMarqueeStart, _remoteListMarqueeStart);
    }

    private void BeginLocalTreeMarqueeFromPoint(Point startPoint, IPointer pointer)
    {
        _localTreeMarqueeActive = true;
        _localTreeMarqueePending = false;
        _localTreeMarqueeStart = startPoint;
        _localTreeModifierSelection = true;
        _localTreeMarqueePointer = pointer;
        _localTreeMarqueePointer.Capture(LocalTree);
        ResetLocalDragState();
        UpdateLocalTreeMarquee(startPoint, startPoint);
    }

    private void BeginLocalListMarqueeFromPoint(Point startPoint, IPointer pointer)
    {
        _localListMarqueeActive = true;
        _localListMarqueePending = false;
        _localListMarqueeStart = startPoint;
        _localListMarqueePointer = pointer;
        _localListMarqueePointer.Capture(LocalList);
        ResetLocalDragState();
        UpdateLocalListMarquee(startPoint, startPoint);
    }

    private void BeginRemoteTreeMarqueeFromPoint(Point startPoint, IPointer pointer)
    {
        _remoteTreeMarqueeActive = true;
        _remoteTreeMarqueePending = false;
        _remoteTreeMarqueeStart = startPoint;
        _remoteTreeModifierSelection = true;
        _remoteTreeMarqueePointer = pointer;
        _remoteTreeMarqueePointer.Capture(RemoteTree);
        UpdateRemoteTreeMarquee(startPoint, startPoint);
    }

    private void BeginRemoteListMarqueeFromPoint(Point startPoint, IPointer pointer)
    {
        _remoteListMarqueeActive = true;
        _remoteListMarqueePending = false;
        _remoteListMarqueeStart = startPoint;
        _remoteListMarqueePointer = pointer;
        _remoteListMarqueePointer.Capture(RemoteList);
        UpdateRemoteListMarquee(startPoint, startPoint);
    }

    private void UpdateLocalTreeMarquee(Point start, Point current)
    {
        var rect = TreeSelectionHelper.NormalizeRect(start, current);
        TreeSelectionHelper.UpdateMarqueeRectangle(LocalTreeMarqueeCanvas, ref _localTreeMarqueeShape, rect);
        ApplyLocalTreeMarqueeSelection(rect);
    }

    private void UpdateLocalListMarquee(Point start, Point current)
    {
        var rect = TreeSelectionHelper.NormalizeRect(start, current);
        TreeSelectionHelper.UpdateMarqueeRectangle(LocalListMarqueeCanvas, ref _localListMarqueeShape, rect);
        ApplyLocalListMarqueeSelection(rect);
    }

    private void UpdateRemoteTreeMarquee(Point start, Point current)
    {
        var rect = TreeSelectionHelper.NormalizeRect(start, current);
        TreeSelectionHelper.UpdateMarqueeRectangle(RemoteTreeMarqueeCanvas, ref _remoteTreeMarqueeShape, rect);
        ApplyRemoteTreeMarqueeSelection(rect);
    }

    private void UpdateRemoteListMarquee(Point start, Point current)
    {
        var rect = TreeSelectionHelper.NormalizeRect(start, current);
        TreeSelectionHelper.UpdateMarqueeRectangle(RemoteListMarqueeCanvas, ref _remoteListMarqueeShape, rect);
        ApplyRemoteListMarqueeSelection(rect);
    }

    private void ApplyLocalTreeMarqueeSelection(Rect marqueeRect)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.SelectedLocalEntry = null;
        TreeSelectionHelper.SelectItemsIntersectingMarquee<LocalTreeNode>(LocalTree, marqueeRect);
        if (LocalTree.SelectedItems?.Count > 0
            && LocalTree.SelectedItems[^1] is LocalTreeNode lastSelected)
        {
            _localTreeAnchorNode = lastSelected;
            if (LocalTree.SelectedItems.Count == 1)
            {
                ViewModel.WithSuppressedLocalTreeSelectionNavigation(() =>
                    ViewModel.SelectedLocalTreeNode = lastSelected);
            }
        }
    }

    private void ApplyLocalListMarqueeSelection(Rect marqueeRect)
    {
        if (ViewModel is null)
        {
            return;
        }

        LocalTree.SelectedItems?.Clear();
        ViewModel.SelectedLocalTreeNode = null;

        var selectedItems = TreeSelectionHelper.SelectItemsIntersectingMarquee<LocalEntry>(LocalList, marqueeRect);
        if (selectedItems.OfType<LocalEntry>().LastOrDefault() is { } lastSelected)
        {
            _localListAnchorEntry = lastSelected;
        }

        ViewModel.SelectedLocalEntry = selectedItems.Count == 1
            ? selectedItems.OfType<LocalEntry>().FirstOrDefault()
            : null;
    }

    private void ApplyRemoteTreeMarqueeSelection(Rect marqueeRect)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.SelectedRemoteEntry = null;
        TreeSelectionHelper.SelectItemsIntersectingMarquee<RemoteTreeNode>(RemoteTree, marqueeRect);
        if (RemoteTree.SelectedItems?.Count > 0
            && RemoteTree.SelectedItems[^1] is RemoteTreeNode lastSelected)
        {
            _remoteTreeAnchorNode = lastSelected;
            if (RemoteTree.SelectedItems.Count == 1)
            {
                ViewModel.WithSuppressedTreeSelectionNavigation(() =>
                    ViewModel.SelectedRemoteTreeNode = lastSelected);
            }
        }
    }

    private void ApplyRemoteListMarqueeSelection(Rect marqueeRect)
    {
        if (ViewModel is null)
        {
            return;
        }

        RemoteTree.SelectedItems?.Clear();
        ViewModel.SelectedRemoteTreeNode = null;

        var selectedItems = TreeSelectionHelper.SelectItemsIntersectingMarquee<RemoteEntry>(RemoteList, marqueeRect);
        if (selectedItems.OfType<RemoteEntry>().LastOrDefault() is { } lastSelected)
        {
            _remoteListAnchorEntry = lastSelected;
        }

        ViewModel.SelectedRemoteEntry = selectedItems.Count == 1
            ? selectedItems.OfType<RemoteEntry>().FirstOrDefault()
            : null;
    }

    private void ApplyLocalListModifierSelection(LocalEntry clickedEntry, KeyModifiers modifiers)
    {
        if (ViewModel is null || LocalList.SelectedItems is null)
        {
            return;
        }

        LocalTree.SelectedItems?.Clear();
        ViewModel.SelectedLocalTreeNode = null;

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            var anchor = _localListAnchorEntry
                ?? LocalList.SelectedItems.OfType<LocalEntry>().FirstOrDefault()
                ?? clickedEntry;
            SelectLocalListRange(anchor, clickedEntry, addToExisting: modifiers.HasFlag(KeyModifiers.Control));
            _localListAnchorEntry ??= anchor;
            return;
        }

        if (LocalList.SelectedItems.Contains(clickedEntry))
        {
            LocalList.SelectedItems.Remove(clickedEntry);
        }
        else
        {
            LocalList.SelectedItems.Add(clickedEntry);
        }

        _localListAnchorEntry = clickedEntry;
        ViewModel.SelectedLocalEntry = LocalList.SelectedItems.Count == 1
            ? LocalList.SelectedItems.OfType<LocalEntry>().FirstOrDefault()
            : null;
    }

    private void SelectLocalListRange(LocalEntry anchor, LocalEntry clickedEntry, bool addToExisting)
    {
        if (ViewModel is null || LocalList.SelectedItems is null)
        {
            return;
        }

        var entries = ViewModel.LocalEntries;
        var startIndex = entries.IndexOf(anchor);
        var endIndex = entries.IndexOf(clickedEntry);
        if (startIndex == -1 || endIndex == -1)
        {
            return;
        }

        if (startIndex > endIndex)
        {
            (startIndex, endIndex) = (endIndex, startIndex);
        }

        if (!addToExisting)
        {
            LocalList.SelectedItems.Clear();
        }

        for (var i = startIndex; i <= endIndex; i++)
        {
            if (!LocalList.SelectedItems.Contains(entries[i]))
            {
                LocalList.SelectedItems.Add(entries[i]);
            }
        }

        ViewModel.SelectedLocalEntry = LocalList.SelectedItems.Count == 1
            ? LocalList.SelectedItems.OfType<LocalEntry>().FirstOrDefault()
            : null;
    }

    private void ApplyRemoteListModifierSelection(RemoteEntry clickedEntry, KeyModifiers modifiers)
    {
        if (ViewModel is null || RemoteList.SelectedItems is null)
        {
            return;
        }

        RemoteTree.SelectedItems?.Clear();
        ViewModel.SelectedRemoteTreeNode = null;

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            var anchor = _remoteListAnchorEntry
                ?? RemoteList.SelectedItems.OfType<RemoteEntry>().FirstOrDefault()
                ?? clickedEntry;
            SelectRemoteListRange(anchor, clickedEntry, addToExisting: modifiers.HasFlag(KeyModifiers.Control));
            _remoteListAnchorEntry ??= anchor;
            return;
        }

        if (RemoteList.SelectedItems.Contains(clickedEntry))
        {
            RemoteList.SelectedItems.Remove(clickedEntry);
        }
        else
        {
            RemoteList.SelectedItems.Add(clickedEntry);
        }

        _remoteListAnchorEntry = clickedEntry;
        ViewModel.SelectedRemoteEntry = RemoteList.SelectedItems.Count == 1
            ? RemoteList.SelectedItems.OfType<RemoteEntry>().FirstOrDefault()
            : null;
    }

    private void SelectRemoteListRange(RemoteEntry anchor, RemoteEntry clickedEntry, bool addToExisting)
    {
        if (ViewModel is null || RemoteList.SelectedItems is null)
        {
            return;
        }

        var entries = ViewModel.RemoteEntries;
        var startIndex = entries.IndexOf(anchor);
        var endIndex = entries.IndexOf(clickedEntry);
        if (startIndex == -1 || endIndex == -1)
        {
            return;
        }

        if (startIndex > endIndex)
        {
            (startIndex, endIndex) = (endIndex, startIndex);
        }

        if (!addToExisting)
        {
            RemoteList.SelectedItems.Clear();
        }

        for (var i = startIndex; i <= endIndex; i++)
        {
            if (!RemoteList.SelectedItems.Contains(entries[i]))
            {
                RemoteList.SelectedItems.Add(entries[i]);
            }
        }

        ViewModel.SelectedRemoteEntry = RemoteList.SelectedItems.Count == 1
            ? RemoteList.SelectedItems.OfType<RemoteEntry>().FirstOrDefault()
            : null;
    }

    private void EndLocalTreeMarquee()
    {
        _localTreeMarqueeActive = false;
        _localTreeMarqueePending = false;
        _localTreeMarqueePointer?.Capture(null);
        _localTreeMarqueePointer = null;
        TreeSelectionHelper.ClearMarqueeRectangle(ref _localTreeMarqueeShape);
    }

    private void EndLocalListMarquee()
    {
        _localListMarqueeActive = false;
        _localListMarqueePending = false;
        _localListMarqueePointer?.Capture(null);
        _localListMarqueePointer = null;
        TreeSelectionHelper.ClearMarqueeRectangle(ref _localListMarqueeShape);
    }

    private void EndRemoteTreeMarquee()
    {
        _remoteTreeMarqueeActive = false;
        _remoteTreeMarqueePending = false;
        _remoteTreeMarqueePointer?.Capture(null);
        _remoteTreeMarqueePointer = null;
        TreeSelectionHelper.ClearMarqueeRectangle(ref _remoteTreeMarqueeShape);
    }

    private void EndRemoteListMarquee()
    {
        _remoteListMarqueeActive = false;
        _remoteListMarqueePending = false;
        _remoteListMarqueePointer?.Capture(null);
        _remoteListMarqueePointer = null;
        TreeSelectionHelper.ClearMarqueeRectangle(ref _remoteListMarqueeShape);
    }

    private void SetLocalPrimaryTreeNode(LocalTreeNode? node)
    {
        if (node is null)
        {
            return;
        }

        LocalTree.SelectedItem = node;
        ViewModel?.WithSuppressedLocalTreeSelectionNavigation(() =>
            ViewModel.SelectedLocalTreeNode = node);
    }

    private void CompleteLocalTreeMultiSelectClick(LocalTreeNode node)
    {
        if (ViewModel is null)
        {
            return;
        }

        LocalTree.SelectedItems?.Clear();
        LocalTree.SelectedItems?.Add(node);
        LocalList.SelectedItems?.Clear();
        ViewModel.SelectedLocalEntry = null;
        _localTreeModifierSelection = false;
        _localTreeAnchorNode = node;
        SetLocalPrimaryTreeNode(node);
        TreeSelectionHelper.SyncSelectionVisuals(LocalTree);
        ViewModel.NavigateToLocalTreeNodeIfNeeded(node);
    }

    private void SetRemotePrimaryTreeNode(RemoteTreeNode? node)
    {
        if (node is null)
        {
            return;
        }

        RemoteTree.SelectedItem = node;
        ViewModel?.WithSuppressedTreeSelectionNavigation(() =>
            ViewModel.SelectedRemoteTreeNode = node);
    }

    private void ApplyLocalTreeModifierSelection(LocalTreeNode clickedNode, KeyModifiers modifiers)
    {
        var selectedItems = LocalTree.SelectedItems;
        if (selectedItems is null || ViewModel is null)
        {
            return;
        }

        ViewModel.SelectedLocalEntry = null;
        LocalList.SelectedItems?.Clear();

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            var anchor = _localTreeAnchorNode
                ?? selectedItems.OfType<LocalTreeNode>().FirstOrDefault()
                ?? clickedNode;
            var allNodes = CollectAllVisibleLocalTreeNodes();
            var startIndex = allNodes.IndexOf(anchor);
            var endIndex = allNodes.IndexOf(clickedNode);
            if (startIndex == -1 || endIndex == -1)
            {
                return;
            }

            if (startIndex > endIndex)
            {
                (startIndex, endIndex) = (endIndex, startIndex);
            }

            selectedItems.Clear();
            for (var i = startIndex; i <= endIndex; i++)
            {
                selectedItems.Add(allNodes[i]);
            }

            _localTreeAnchorNode = clickedNode;
            if (selectedItems.Count == 1)
            {
                SetLocalPrimaryTreeNode(clickedNode);
            }
        }
        else
        {
            if (selectedItems.Contains(clickedNode))
            {
                selectedItems.Remove(clickedNode);
                var nextPrimary = selectedItems.OfType<LocalTreeNode>().LastOrDefault();
                if (selectedItems.Count == 1)
                {
                    SetLocalPrimaryTreeNode(nextPrimary);
                }
                else if (selectedItems.Count == 0)
                {
                    LocalTree.SelectedItem = null;
                    ViewModel.WithSuppressedLocalTreeSelectionNavigation(() =>
                        ViewModel.SelectedLocalTreeNode = null);
                }
            }
            else
            {
                selectedItems.Add(clickedNode);
                if (selectedItems.Count == 1)
                {
                    SetLocalPrimaryTreeNode(clickedNode);
                }
            }

            _localTreeAnchorNode = clickedNode;
        }
    }

    private void ApplyRemoteTreeModifierSelection(RemoteTreeNode clickedNode, KeyModifiers modifiers)
    {
        var selectedItems = RemoteTree.SelectedItems;
        if (selectedItems is null || ViewModel is null)
        {
            return;
        }

        ViewModel.SelectedRemoteEntry = null;
        RemoteList.SelectedItems?.Clear();

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            var anchor = _remoteTreeAnchorNode
                ?? selectedItems.OfType<RemoteTreeNode>().FirstOrDefault()
                ?? clickedNode;
            var allNodes = CollectAllVisibleRemoteTreeNodes();
            var startIndex = allNodes.IndexOf(anchor);
            var endIndex = allNodes.IndexOf(clickedNode);
            if (startIndex == -1 || endIndex == -1)
            {
                return;
            }

            if (startIndex > endIndex)
            {
                (startIndex, endIndex) = (endIndex, startIndex);
            }

            selectedItems.Clear();
            for (var i = startIndex; i <= endIndex; i++)
            {
                selectedItems.Add(allNodes[i]);
            }

            _remoteTreeAnchorNode = clickedNode;
            if (selectedItems.Count == 1)
            {
                SetRemotePrimaryTreeNode(clickedNode);
            }
        }
        else
        {
            if (selectedItems.Contains(clickedNode))
            {
                selectedItems.Remove(clickedNode);
                var nextPrimary = selectedItems.OfType<RemoteTreeNode>().LastOrDefault();
                if (selectedItems.Count == 1)
                {
                    SetRemotePrimaryTreeNode(nextPrimary);
                }
                else if (selectedItems.Count == 0)
                {
                    RemoteTree.SelectedItem = null;
                    ViewModel.WithSuppressedTreeSelectionNavigation(() =>
                        ViewModel.SelectedRemoteTreeNode = null);
                }
            }
            else
            {
                selectedItems.Add(clickedNode);
                if (selectedItems.Count == 1)
                {
                    SetRemotePrimaryTreeNode(clickedNode);
                }
            }

            _remoteTreeAnchorNode = clickedNode;
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

        if (_localTreeModifierSelection
            || _localTreeMarqueeActive
            || (LocalTree.SelectedItems?.Count ?? 0) > 1)
        {
            if ((LocalTree.SelectedItems?.Count ?? 0) > 0)
            {
                LocalList.SelectedItems?.Clear();
                ViewModel.SelectedLocalEntry = null;
            }

            TreeSelectionHelper.SyncSelectionVisuals(LocalTree);
            ScrollSelectedLocalTreeItemIntoView();
            return;
        }

        if ((LocalTree.SelectedItems?.Count ?? 0) > 0)
        {
            LocalList.SelectedItems?.Clear();
        }

        ViewModel.SelectedLocalEntry = null;

        if (LocalTree.SelectedItem is LocalTreeNode selected)
        {
            if (!ReferenceEquals(ViewModel.SelectedLocalTreeNode, selected))
            {
                ViewModel.WithSuppressedLocalTreeSelectionNavigation(() =>
                    ViewModel.SelectedLocalTreeNode = selected);
            }

            _localTreeAnchorNode = selected;
        }

        if (!ViewModel.IsLocalTreeSelectionNavigationSuppressed
            && LocalTree.SelectedItems?.Count == 1
            && LocalTree.SelectedItem is LocalTreeNode { IsPlaceholder: false } navigateNode)
        {
            ViewModel.NavigateToLocalTreeNodeIfNeeded(navigateNode);
        }

        TreeSelectionHelper.SyncSelectionVisuals(LocalTree);
        ScrollSelectedLocalTreeItemIntoView();
    }

    private void LocalList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var selectedCount = LocalList.SelectedItems?.Count ?? 0;
        if (selectedCount == 0)
        {
            ViewModel.SelectedLocalEntry = null;
            TreeSelectionHelper.SyncSelectionVisuals(LocalList);
            return;
        }

        LocalTree.SelectedItems?.Clear();
        ViewModel.SelectedLocalTreeNode = null;

        if (_localListMarqueeActive || selectedCount > 1)
        {
            ViewModel.SelectedLocalEntry = null;
            TreeSelectionHelper.SyncSelectionVisuals(LocalList);
            return;
        }

        if (LocalList.SelectedItem is LocalEntry entry)
        {
            ViewModel.SelectedLocalEntry = entry;
            _localListAnchorEntry = entry;
        }

        TreeSelectionHelper.SyncSelectionVisuals(LocalList);
    }

    private void RemoteTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (_remoteTreeModifierSelection
            || _remoteTreeMarqueeActive
            || (RemoteTree.SelectedItems?.Count ?? 0) > 1)
        {
            if ((RemoteTree.SelectedItems?.Count ?? 0) > 0)
            {
                RemoteList.SelectedItems?.Clear();
                ViewModel.SelectedRemoteEntry = null;
            }

            TreeSelectionHelper.SyncSelectionVisuals(RemoteTree);
            ScrollSelectedRemoteTreeItemIntoView();
            return;
        }

        if ((RemoteTree.SelectedItems?.Count ?? 0) > 0)
        {
            RemoteList.SelectedItems?.Clear();
        }

        ViewModel.SelectedRemoteEntry = null;

        if (RemoteTree.SelectedItem is RemoteTreeNode selected)
        {
            if (!ReferenceEquals(ViewModel.SelectedRemoteTreeNode, selected))
            {
                ViewModel.WithSuppressedTreeSelectionNavigation(() =>
                    ViewModel.SelectedRemoteTreeNode = selected);
            }

            _remoteTreeAnchorNode = selected;
        }

        if (!ViewModel.IsRemoteTreeSelectionNavigationSuppressed
            && RemoteTree.SelectedItems?.Count == 1
            && RemoteTree.SelectedItem is RemoteTreeNode { IsPlaceholder: false } navigateNode)
        {
            ViewModel.NavigateToRemoteTreeNodeIfNeeded(navigateNode);
        }

        TreeSelectionHelper.SyncSelectionVisuals(RemoteTree);
        ScrollSelectedRemoteTreeItemIntoView();
    }

    private void RemoteList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var selectedCount = RemoteList.SelectedItems?.Count ?? 0;
        if (selectedCount == 0)
        {
            ViewModel.SelectedRemoteEntry = null;
            TreeSelectionHelper.SyncSelectionVisuals(RemoteList);
            return;
        }

        RemoteTree.SelectedItems?.Clear();
        ViewModel.SelectedRemoteTreeNode = null;

        if (_remoteListMarqueeActive || selectedCount > 1)
        {
            ViewModel.SelectedRemoteEntry = null;
            TreeSelectionHelper.SyncSelectionVisuals(RemoteList);
            return;
        }

        if (RemoteList.SelectedItem is RemoteEntry entry)
        {
            ViewModel.SelectedRemoteEntry = entry;
            _remoteListAnchorEntry = entry;
        }

        TreeSelectionHelper.SyncSelectionVisuals(RemoteList);
    }

    private IReadOnlyList<string>? TryBeginLocalListDrag(PointerPressedEventArgs e)
    {
        if (_localListMarqueeActive)
        {
            return null;
        }

        var modifiers = GetPointerModifiers(e);
        if (modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Shift))
        {
            return null;
        }

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

        if (_localTreeMarqueeActive
            || _localTreeMarqueePending
            || _localListMarqueeActive
            || _localListMarqueePending
            || _remoteTreeMarqueePending)
        {
            return null;
        }

        if ((e.Source as Control)?.FindAncestorOfType<TreeViewItem>()?.DataContext is not LocalTreeNode { IsPlaceholder: false } node)
        {
            return null;
        }

        var selectedNodes = LocalTree.SelectedItems?
            .OfType<LocalTreeNode>()
            .Where(treeNode => !treeNode.IsPlaceholder && (ViewModel?.SelectedLeftDevice?.IsLocal == false || Directory.Exists(treeNode.FullPath)))
            .ToList() ?? [];

        if (selectedNodes.Contains(node))
        {
            var selectedPaths = selectedNodes
                .Select(treeNode => treeNode.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (selectedPaths.Count > 0)
            {
                return selectedPaths;
            }
        }

        return ViewModel?.SelectedLeftDevice?.IsLocal == false || Directory.Exists(node.FullPath) ? [node.FullPath] : null;
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
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private async void LocalDragSource_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_localTreeMarqueeActive)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                EndLocalTreeMarquee();
                return;
            }

            UpdateLocalTreeMarquee(_localTreeMarqueeStart, e.GetPosition(LocalTree));
            return;
        }

        if (_localListMarqueeActive)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                EndLocalListMarquee();
                return;
            }

            UpdateLocalListMarquee(_localListMarqueeStart, e.GetPosition(LocalList));
            return;
        }

        if (_remoteTreeMarqueeActive)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                EndRemoteTreeMarquee();
                return;
            }

            UpdateRemoteTreeMarquee(_remoteTreeMarqueeStart, e.GetPosition(RemoteTree));
            return;
        }

        if (_remoteListMarqueeActive)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                EndRemoteListMarquee();
                return;
            }

            UpdateRemoteListMarquee(_remoteListMarqueeStart, e.GetPosition(RemoteList));
            return;
        }

        if (_localListMarqueePending && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var localPosition = e.GetPosition(LocalList);
            var localDelta = localPosition - _localListMarqueePressPoint;
            if (Math.Abs(localDelta.X) >= MarqueeDragThreshold || Math.Abs(localDelta.Y) >= MarqueeDragThreshold)
            {
                if (Math.Abs(localDelta.X) > Math.Abs(localDelta.Y))
                {
                    _localListMarqueePending = false;
                }
                else
                {
                    BeginLocalListMarqueeFromPoint(_localListMarqueePressPoint, e.Pointer);
                    UpdateLocalListMarquee(_localListMarqueeStart, localPosition);
                    return;
                }
            }
        }

        if (_remoteListMarqueePending && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var remotePosition = e.GetPosition(RemoteList);
            var remoteDelta = remotePosition - _remoteListMarqueePressPoint;
            if (Math.Abs(remoteDelta.X) >= MarqueeDragThreshold || Math.Abs(remoteDelta.Y) >= MarqueeDragThreshold)
            {
                BeginRemoteListMarqueeFromPoint(_remoteListMarqueePressPoint, e.Pointer);
                UpdateRemoteListMarquee(_remoteListMarqueeStart, remotePosition);
            }

            return;
        }

        if (_remoteTreeMarqueePending && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var remotePosition = e.GetPosition(RemoteTree);
            var remoteDelta = remotePosition - _remoteTreeMarqueePressPoint;
            if (Math.Abs(remoteDelta.X) >= MarqueeDragThreshold || Math.Abs(remoteDelta.Y) >= MarqueeDragThreshold)
            {
                BeginRemoteTreeMarqueeFromPoint(_remoteTreeMarqueePressPoint, e.Pointer);
                UpdateRemoteTreeMarquee(_remoteTreeMarqueeStart, remotePosition);
            }

            return;
        }

        if (_localTreeMarqueePending && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var localPosition = e.GetPosition(LocalTree);
            var localDelta = localPosition - _localTreeMarqueePressPoint;
            if (Math.Abs(localDelta.X) >= MarqueeDragThreshold || Math.Abs(localDelta.Y) >= MarqueeDragThreshold)
            {
                BeginLocalTreeMarqueeFromPoint(_localTreeMarqueePressPoint, e.Pointer);
                UpdateLocalTreeMarquee(_localTreeMarqueeStart, localPosition);
            }

            return;
        }

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
        if (_localDragElement == LocalTree)
        {
            _pendingLocalTreeMultiSelectClickNode = null;
            _suppressNextLocalTreeTap = true;
        }

        var trigger = _localDragPressEvent;
        ResetLocalDragState();

        var dataTransfer = new DataTransfer();
        dataTransfer.Add(DataTransferItem.Create(DragDropFormats.LocalPathsDataFormat, string.Join('\n', paths)));
        dataTransfer.Add(DataTransferItem.Create(
            DragDropFormats.SourceDeviceDataFormat,
            ViewModel?.SelectedLeftDevice?.Id ?? "local"));
        await DragDrop.DoDragDropAsync(trigger, dataTransfer, DragDropEffects.Copy);
        _localDragStarted = false;
        ClearRemoteDropHighlights();
    }

    private void LocalDragSource_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_localTreeMarqueeActive)
        {
            EndLocalTreeMarquee();
            e.Pointer.Capture(null);
            return;
        }

        if (_remoteTreeMarqueeActive)
        {
            EndRemoteTreeMarquee();
            e.Pointer.Capture(null);
            return;
        }

        if (_localListMarqueeActive)
        {
            EndLocalListMarquee();
            e.Pointer.Capture(null);
            return;
        }

        if (_remoteListMarqueeActive)
        {
            EndRemoteListMarquee();
            e.Pointer.Capture(null);
            return;
        }

        _localTreeMarqueePending = false;
        _localListMarqueePending = false;
        _remoteTreeMarqueePending = false;
        _remoteListMarqueePending = false;

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
            if (LocalList.SelectedItems?.Contains(entry) != true)
            {
                LocalList.SelectedItems?.Clear();
                LocalList.SelectedItem = entry;
            }

            ViewModel.SelectedLocalEntry = LocalList.SelectedItems?.Count == 1
                ? entry
                : null;
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
            if (RemoteList.SelectedItems?.Contains(entry) != true)
            {
                RemoteList.SelectedItems?.Clear();
                RemoteList.SelectedItem = entry;
            }

            ViewModel.SelectedRemoteEntry = RemoteList.SelectedItems?.Count == 1
                ? entry
                : null;
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
                if (listItem?.DataContext is RemoteEntry { IsDirectory: true } listEntry)
                {
                    entry = listEntry;
                }
            }
        }

        if (listItem is null)
        {
            listItem = FindListBoxItemAt(RemoteList, e.GetPosition(RemoteList));
            if (listItem?.DataContext is RemoteEntry { IsDirectory: true } hitEntry)
            {
                entry = hitEntry;
            }
        }

        if (ReferenceEquals(treeNode, _remoteDropTargetTreeNode)
            && ReferenceEquals(entry, _remoteDropTargetEntry)
            && ReferenceEquals(listItem, _remoteDropTargetListItem))
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
        else if (listItem is not null)
        {
            listItem.Classes.Add("drop-target");
            _remoteDropTargetListItem = listItem;
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

    private static ListBoxItem? FindListBoxItemAt(ListBox list, Point position)
    {
        foreach (var container in list.GetRealizedContainers())
        {
            if (container is not ListBoxItem item)
            {
                continue;
            }

            var topLeft = item.TranslatePoint(new Point(0, 0), list);
            if (topLeft is null)
            {
                continue;
            }

            var bounds = new Rect(topLeft.Value, item.Bounds.Size);
            if (bounds.Contains(position))
            {
                return item;
            }
        }

        return null;
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

        await ViewModel.TransferPathsAsync(paths, destination, ReadSourceDeviceId(e));
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

    private static string ReadSourceDeviceId(DragEventArgs e)
    {
        var payload = e.DataTransfer.TryGetValue(DragDropFormats.SourceDeviceDataFormat);
        return string.IsNullOrWhiteSpace(payload) ? "local" : payload.Trim();
    }

    private void LocalTreeItem_Tapped(object? sender, TappedEventArgs e)
    {
        if (_suppressNextLocalTreeTap)
        {
            _suppressNextLocalTreeTap = false;
            e.Handled = true;
            return;
        }

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
