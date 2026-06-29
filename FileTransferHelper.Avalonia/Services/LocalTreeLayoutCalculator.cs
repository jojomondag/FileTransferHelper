using FileTransferHelper.Models;

namespace FileTransferHelper.Services;

public static class LocalTreeLayoutCalculator
{
    private const double IndentPerLevel = 12;
    private const double RowChromeWidth = 30;
    private const double DefaultCharWidth = 6.5;
    private const double WideCharWidth = 7.3;

    public static double MeasurePaneWidth(IEnumerable<LocalTreeNode> roots, double minWidth, double maxWidth)
    {
        var widest = minWidth;
        foreach (var root in roots)
        {
            WalkVisible(root, 0, ref widest);
        }

        return Math.Clamp(widest, minWidth, maxWidth);
    }

    private static void WalkVisible(LocalTreeNode node, int depth, ref double widest)
    {
        if (node.IsPlaceholder)
        {
            return;
        }

        var rowWidth = depth * IndentPerLevel + RowChromeWidth + EstimateTextWidth(node.Name);
        widest = Math.Max(widest, rowWidth);

        if (!node.IsExpanded)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            WalkVisible(child, depth + 1, ref widest);
        }
    }

    private static double EstimateTextWidth(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0.0;
        foreach (var character in text)
        {
            width += character switch
            {
                'i' or 'l' or 'j' or 't' or 'f' or '.' or '-' => 4.5,
                'm' or 'w' or 'M' or 'W' or '@' => 9.0,
                _ when char.IsUpper(character) => WideCharWidth,
                _ when character > 127 => WideCharWidth,
                _ => DefaultCharWidth
            };
        }

        return width;
    }
}
