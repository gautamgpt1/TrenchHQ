using TrenchHQ.Infrastructure.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Foundation;

namespace TrenchHQ.Controls
{
    public sealed partial class WrapPanel : Panel
    {
        public static readonly DependencyProperty HorizontalSpacingProperty =
            DependencyProperty.Register(
                nameof(HorizontalSpacing),
                typeof(double),
                typeof(WrapPanel),
                new PropertyMetadata(0d, OnLayoutPropertyChanged));

        public static readonly DependencyProperty VerticalSpacingProperty =
            DependencyProperty.Register(
                nameof(VerticalSpacing),
                typeof(double),
                typeof(WrapPanel),
                new PropertyMetadata(0d, OnLayoutPropertyChanged));

        public static readonly DependencyProperty OrientationProperty =
            DependencyProperty.Register(
                nameof(Orientation),
                typeof(Orientation),
                typeof(WrapPanel),
                new PropertyMetadata(Orientation.Horizontal, OnLayoutPropertyChanged));

        public static readonly DependencyProperty FirstLineEndInsetProperty =
            DependencyProperty.Register(
                nameof(FirstLineEndInset),
                typeof(double),
                typeof(WrapPanel),
                new PropertyMetadata(0d, OnLayoutPropertyChanged));

        public double HorizontalSpacing
        {
            get => (double)GetValue(HorizontalSpacingProperty);
            set => SetValue(HorizontalSpacingProperty, value);
        }

        public double VerticalSpacing
        {
            get => (double)GetValue(VerticalSpacingProperty);
            set => SetValue(VerticalSpacingProperty, value);
        }

        public Orientation Orientation
        {
            get => (Orientation)GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        public double FirstLineEndInset
        {
            get => (double)GetValue(FirstLineEndInsetProperty);
            set => SetValue(FirstLineEndInsetProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            return Orientation == Orientation.Vertical
                ? MeasureVertical(availableSize)
                : MeasureHorizontal(availableSize);
        }

        private Size MeasureHorizontal(Size availableSize)
        {
            var maxWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
            double lineWidth = 0;
            double lineHeight = 0;
            double totalWidth = 0;
            double totalHeight = 0;
            var firstLine = true;

            foreach (var child in Children)
            {
                child.Measure(availableSize);
                var childSize = child.DesiredSize;

                var nextWidth = lineWidth > 0
                    ? lineWidth + HorizontalSpacing + childSize.Width
                    : childSize.Width;

                var lineLimit = firstLine
                    ? Math.Max(1, maxWidth - Math.Max(0, FirstLineEndInset))
                    : maxWidth;
                if (nextWidth > lineLimit && lineWidth > 0)
                {
                    totalWidth = Math.Max(totalWidth, lineWidth + (firstLine ? Math.Max(0, FirstLineEndInset) : 0));
                    totalHeight += lineHeight + (totalHeight > 0 ? VerticalSpacing : 0);
                    lineWidth = childSize.Width;
                    lineHeight = childSize.Height;
                    firstLine = false;
                }
                else
                {
                    lineWidth = nextWidth;
                    lineHeight = Math.Max(lineHeight, childSize.Height);
                }
            }

            if (lineWidth > 0)
            {
                totalWidth = Math.Max(totalWidth, lineWidth + (firstLine ? Math.Max(0, FirstLineEndInset) : 0));
                totalHeight += lineHeight + (totalHeight > 0 ? VerticalSpacing : 0);
            }

            return new Size(totalWidth, totalHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            return Orientation == Orientation.Vertical
                ? ArrangeVertical(finalSize)
                : ArrangeHorizontal(finalSize);
        }

        private Size ArrangeHorizontal(Size finalSize)
        {
            double lineWidth = 0;
            double lineHeight = 0;
            double offsetX = 0;
            double offsetY = 0;
            var firstLine = true;

            foreach (var child in Children)
            {
                var childSize = child.DesiredSize;
                var nextWidth = lineWidth > 0
                    ? lineWidth + HorizontalSpacing + childSize.Width
                    : childSize.Width;

                var lineLimit = firstLine
                    ? Math.Max(1, finalSize.Width - Math.Max(0, FirstLineEndInset))
                    : finalSize.Width;
                if (nextWidth > lineLimit && lineWidth > 0)
                {
                    offsetX = 0;
                    offsetY += lineHeight + VerticalSpacing;
                    lineWidth = childSize.Width;
                    lineHeight = childSize.Height;
                    firstLine = false;
                }
                else
                {
                    lineWidth = nextWidth;
                    lineHeight = Math.Max(lineHeight, childSize.Height);
                }

                child.Arrange(new Rect(offsetX, offsetY, childSize.Width, childSize.Height));
                offsetX += childSize.Width + HorizontalSpacing;
            }

            return finalSize;
        }

        private Size MeasureVertical(Size availableSize)
        {
            var maxHeight = double.IsInfinity(availableSize.Height) ? double.MaxValue : availableSize.Height;
            double columnWidth = 0;
            double columnHeight = 0;
            double totalWidth = 0;
            double totalHeight = 0;

            foreach (var child in Children)
            {
                child.Measure(availableSize);
                var childSize = child.DesiredSize;
                var nextHeight = columnHeight > 0
                    ? columnHeight + VerticalSpacing + childSize.Height
                    : childSize.Height;

                if (nextHeight > maxHeight && columnHeight > 0)
                {
                    totalWidth += columnWidth + (totalWidth > 0 ? HorizontalSpacing : 0);
                    totalHeight = Math.Max(totalHeight, columnHeight);
                    columnWidth = childSize.Width;
                    columnHeight = childSize.Height;
                }
                else
                {
                    columnWidth = Math.Max(columnWidth, childSize.Width);
                    columnHeight = nextHeight;
                }
            }

            if (columnHeight > 0)
            {
                totalWidth += columnWidth + (totalWidth > 0 ? HorizontalSpacing : 0);
                totalHeight = Math.Max(totalHeight, columnHeight);
            }

            return new Size(totalWidth, totalHeight);
        }

        private Size ArrangeVertical(Size finalSize)
        {
            double columnWidth = 0;
            double columnHeight = 0;
            double offsetX = 0;
            double offsetY = 0;

            foreach (var child in Children)
            {
                var childSize = child.DesiredSize;
                var nextHeight = columnHeight > 0
                    ? columnHeight + VerticalSpacing + childSize.Height
                    : childSize.Height;

                if (nextHeight > finalSize.Height && columnHeight > 0)
                {
                    offsetX += columnWidth + HorizontalSpacing;
                    offsetY = 0;
                    columnWidth = childSize.Width;
                    columnHeight = childSize.Height;
                }
                else
                {
                    columnWidth = Math.Max(columnWidth, childSize.Width);
                    columnHeight = nextHeight;
                }

                child.Arrange(new Rect(offsetX, offsetY, childSize.Width, childSize.Height));
                offsetY += childSize.Height + VerticalSpacing;
            }

            return finalSize;
        }

        private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs _)
        {
            if (d is WrapPanel panel)
            {
                panel.InvalidateMeasure();
                panel.InvalidateArrange();
            }
        }
    }
}
