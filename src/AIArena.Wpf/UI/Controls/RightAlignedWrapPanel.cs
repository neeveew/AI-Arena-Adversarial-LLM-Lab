using System.Windows;
using System.Windows.Controls;

namespace AIArena.Wpf.Controls;

/// <summary>
/// Preserves logical child order while right-aligning every wrapped horizontal line.
/// Stock WrapPanel only right-aligns the panel itself, so a constrained final line
/// otherwise begins at the left edge.
/// </summary>
internal sealed class RightAlignedWrapPanel : WrapPanel
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Orientation != Orientation.Horizontal || InternalChildren.Count == 0)
        {
            return base.ArrangeOverride(finalSize);
        }

        var availableWidth = double.IsFinite(finalSize.Width)
            ? Math.Max(0, finalSize.Width)
            : DesiredSize.Width;
        var lineStart = 0;
        var lineWidth = 0d;
        var lineHeight = 0d;
        var y = 0d;

        for (var index = 0; index < InternalChildren.Count; index++)
        {
            var childSize = ChildSlotSize(InternalChildren[index]);
            if (lineWidth > 0 && lineWidth + childSize.Width > availableWidth)
            {
                ArrangeLine(lineStart, index, y, lineWidth, lineHeight, availableWidth);
                y += lineHeight;
                lineStart = index;
                lineWidth = 0;
                lineHeight = 0;
            }

            lineWidth += childSize.Width;
            lineHeight = Math.Max(lineHeight, childSize.Height);
        }

        ArrangeLine(lineStart, InternalChildren.Count, y, lineWidth, lineHeight, availableWidth);
        return finalSize;
    }

    private Size ChildSlotSize(UIElement child)
    {
        return new Size(
            double.IsNaN(ItemWidth) ? child.DesiredSize.Width : ItemWidth,
            double.IsNaN(ItemHeight) ? child.DesiredSize.Height : ItemHeight);
    }

    private void ArrangeLine(
        int start,
        int end,
        double y,
        double lineWidth,
        double lineHeight,
        double availableWidth)
    {
        var x = Math.Max(0, availableWidth - lineWidth);
        for (var index = start; index < end; index++)
        {
            var child = InternalChildren[index];
            var childSize = ChildSlotSize(child);
            child.Arrange(new Rect(x, y, childSize.Width, lineHeight));
            x += childSize.Width;
        }
    }
}
