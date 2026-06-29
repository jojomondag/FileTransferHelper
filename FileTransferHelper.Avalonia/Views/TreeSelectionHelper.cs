using System.Collections;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace FileTransferHelper.Views;

internal static class TreeSelectionHelper
{
    private static readonly SolidColorBrush MarqueeFill = new(Color.FromArgb(64, 0, 120, 215));
    private static readonly SolidColorBrush MarqueeStroke = new(Color.FromArgb(255, 0, 120, 215));
    private static readonly SolidColorBrush ListSelectionFill = new(Color.FromArgb(160, 0, 120, 215));

    public static void SyncSelectionVisuals(TreeView tree)
    {
        var selectedItems = tree.SelectedItems;
        if (selectedItems is null)
        {
            return;
        }

        foreach (var container in tree.GetRealizedContainers())
        {
            if (container is not TreeViewItem treeViewItem)
            {
                continue;
            }

            var item = tree.TreeItemFromContainer(treeViewItem);
            if (item is null)
            {
                continue;
            }

            var isSelected = selectedItems.Contains(item);
            if (SelectingItemsControl.GetIsSelected(treeViewItem) != isSelected)
            {
                SelectingItemsControl.SetIsSelected(treeViewItem, isSelected);
            }
        }
    }

    public static void SyncSelectionVisuals(ListBox list)
    {
        var selectedItems = list.SelectedItems;
        if (selectedItems is null)
        {
            return;
        }

        foreach (var container in list.GetRealizedContainers())
        {
            if (container is not ListBoxItem listBoxItem)
            {
                continue;
            }

            var item = listBoxItem.DataContext;
            if (item is null)
            {
                continue;
            }

            var isSelected = selectedItems.Contains(item);
            if (SelectingItemsControl.GetIsSelected(listBoxItem) != isSelected)
            {
                SelectingItemsControl.SetIsSelected(listBoxItem, isSelected);
            }

            SetListItemSelectionSurface(listBoxItem, isSelected);
        }
    }

    public static Rect NormalizeRect(Point start, Point end)
    {
        var x = Math.Min(start.X, end.X);
        var y = Math.Min(start.Y, end.Y);
        var width = Math.Abs(end.X - start.X);
        var height = Math.Abs(end.Y - start.Y);
        return new Rect(x, y, width, height);
    }

    public static void UpdateMarqueeRectangle(Canvas canvas, ref Rectangle? shape, Rect rect)
    {
        shape ??= new Rectangle
        {
            Stroke = MarqueeStroke,
            StrokeThickness = 1,
            Fill = MarqueeFill,
            IsHitTestVisible = false,
        };

        if (!canvas.Children.Contains(shape))
        {
            canvas.Children.Add(shape);
        }

        Canvas.SetLeft(shape, rect.X);
        Canvas.SetTop(shape, rect.Y);
        shape.Width = Math.Max(1, rect.Width);
        shape.Height = Math.Max(1, rect.Height);
        shape.IsVisible = rect.Width >= 1 || rect.Height >= 1;
    }

    public static void ClearMarqueeRectangle(ref Rectangle? shape)
    {
        if (shape is null)
        {
            return;
        }

        shape.IsVisible = false;
    }

    public static IList SelectItemsIntersectingMarquee<TNode>(TreeView tree, Rect marqueeRect)
        where TNode : class
    {
        var selectedItems = tree.SelectedItems ?? throw new InvalidOperationException("TreeView.SelectedItems is null.");
        selectedItems.Clear();

        TNode? lastSelected = null;
        foreach (var container in tree.GetRealizedContainers())
        {
            if (container is not TreeViewItem treeViewItem)
            {
                continue;
            }

            if (treeViewItem.DataContext is not TNode node)
            {
                continue;
            }

            var itemRect = GetTreeItemBoundsInTree(treeViewItem, tree);
            if (itemRect is null || !marqueeRect.Intersects(itemRect.Value))
            {
                continue;
            }

            selectedItems.Add(node);
            lastSelected = node;
        }

        if (lastSelected is not null && selectedItems.Count == 1)
        {
            tree.SelectedItem = lastSelected;
        }

        SyncSelectionVisuals(tree);
        return selectedItems;
    }

    public static IList SelectItemsIntersectingMarquee<TItem>(ListBox list, Rect marqueeRect)
        where TItem : class
    {
        var selectedItems = list.SelectedItems ?? throw new InvalidOperationException("ListBox.SelectedItems is null.");
        selectedItems.Clear();

        TItem? lastSelected = null;
        foreach (var container in list.GetRealizedContainers())
        {
            if (container is not ListBoxItem listBoxItem)
            {
                continue;
            }

            if (listBoxItem.DataContext is not TItem item)
            {
                continue;
            }

            var itemRect = GetListItemBoundsInList(listBoxItem, list);
            if (itemRect is null || !marqueeRect.Intersects(itemRect.Value))
            {
                continue;
            }

            selectedItems.Add(item);
            lastSelected = item;
        }

        if (lastSelected is not null && selectedItems.Count == 1)
        {
            list.SelectedItem = lastSelected;
        }

        SyncSelectionVisuals(list);
        return selectedItems;
    }

    private static Rect? GetTreeItemBoundsInTree(TreeViewItem item, TreeView tree)
    {
        var topLeft = item.TranslatePoint(new Point(0, 0), tree);
        if (topLeft is null)
        {
            return null;
        }

        return new Rect(topLeft.Value, item.Bounds.Size);
    }

    private static Rect? GetListItemBoundsInList(ListBoxItem item, ListBox list)
    {
        var topLeft = item.TranslatePoint(new Point(0, 0), list);
        if (topLeft is null)
        {
            return null;
        }

        return new Rect(topLeft.Value, item.Bounds.Size);
    }

    private static void SetListItemSelectionSurface(ListBoxItem item, bool isSelected)
    {
        if (isSelected)
        {
            item.Classes.Add("marquee-selected");
        }
        else
        {
            item.Classes.Remove("marquee-selected");
        }

        var surface = item.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("selection-surface"));
        if (surface is not null)
        {
            surface.Background = isSelected ? ListSelectionFill : Brushes.Transparent;
            surface.InvalidateVisual();
        }

        item.InvalidateVisual();
    }
}
