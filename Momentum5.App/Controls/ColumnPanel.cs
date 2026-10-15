using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Momentum5.App.Controls;

/// <summary>
/// Page layout panel. Children stack vertically at a capped, centered width. Consecutive children marked
/// <see cref="FlowProperty"/> (cards) reflow into 1–3 balanced columns as the window gets wider, so wide windows
/// fill their sides instead of leaving dead space. A lone card spans the full width.
/// </summary>
public sealed partial class ColumnPanel : Panel
{
	public static readonly DependencyProperty MaxColumnWidthProperty =
		DependencyProperty.Register(nameof(MaxColumnWidth), typeof(double), typeof(ColumnPanel), new PropertyMetadata(1500.0, OnLayoutPropertyChanged));

	public static readonly DependencyProperty MinColumnWidthProperty =
		DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(ColumnPanel), new PropertyMetadata(440.0, OnLayoutPropertyChanged));

	public static readonly DependencyProperty SpacingProperty =
		DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(ColumnPanel), new PropertyMetadata(16.0, OnLayoutPropertyChanged));

	public static readonly DependencyProperty FlowProperty =
		DependencyProperty.RegisterAttached("Flow", typeof(bool), typeof(ColumnPanel), new PropertyMetadata(false, OnFlowChanged));

	private readonly List<Rect> _rects = [];
	private FrameworkElement? _host;
	private bool _narrow;
	private double _width;

	public ColumnPanel()
	{
		Loaded += (_, _) =>
		{
			_host = Parent as FrameworkElement;
			if (_host is not null)
			{
				_host.SizeChanged += (_, _) => UpdateNarrow();
			}

			UpdateNarrow();
		};
	}

	/// <summary>Below this host width the page margin and card padding shrink so narrow windows keep their content.</summary>
	private void UpdateNarrow()
	{
		var narrow = (_host?.ActualWidth ?? double.PositiveInfinity) < 560;
		if (narrow == _narrow)
		{
			return;
		}

		_narrow = narrow;
		ClearValue(MarginProperty);
		if (narrow)
		{
			var b = Margin;
			Margin = new Thickness(Math.Min(b.Left, 12), Math.Min(b.Top, 8), Math.Min(b.Right, 12), Math.Min(b.Bottom, 20));
		}

		foreach (var child in Children)
		{
			if (child is Border border)
			{
				if (narrow)
				{
					border.Padding = new Thickness(Math.Min(border.Padding.Left, 14), Math.Min(border.Padding.Top, 14), Math.Min(border.Padding.Right, 14), Math.Min(border.Padding.Bottom, 14));
				}
				else
				{
					border.ClearValue(Border.PaddingProperty);
				}
			}
		}

		InvalidateMeasure();
	}

	public double MaxColumnWidth
	{
		get => (double)GetValue(MaxColumnWidthProperty);
		set => SetValue(MaxColumnWidthProperty, value);
	}

	public double MinColumnWidth
	{
		get => (double)GetValue(MinColumnWidthProperty);
		set => SetValue(MinColumnWidthProperty, value);
	}

	public double Spacing
	{
		get => (double)GetValue(SpacingProperty);
		set => SetValue(SpacingProperty, value);
	}

	public static bool GetFlow(DependencyObject element) => (bool)element.GetValue(FlowProperty);

	public static void SetFlow(DependencyObject element, bool value) => element.SetValue(FlowProperty, value);

	private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ColumnPanel)d).InvalidateMeasure();

	private static void OnFlowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is FrameworkElement { Parent: ColumnPanel panel })
		{
			panel.InvalidateMeasure();
		}
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		var max = Math.Max(1, MaxColumnWidth);
		var width = double.IsInfinity(availableSize.Width) ? max : Math.Min(availableSize.Width, max);
		var gap = Math.Max(0, Spacing);
		_width = width;
		_rects.Clear();
		for (var k = 0; k < Children.Count; k++)
		{
			_rects.Add(default);
		}

		double y = 0;
		var i = 0;
		while (i < Children.Count)
		{
			var child = Children[i];
			if (child.Visibility == Visibility.Collapsed)
			{
				i++;
				continue;
			}

			if (!GetFlow(child))
			{
				child.Measure(new Size(width, double.PositiveInfinity));
				var h = child.DesiredSize.Height;
				_rects[i] = new Rect(0, y, width, h);
				y += h + gap;
				i++;
				continue;
			}

			var group = new List<int>();
			while (i < Children.Count && (Children[i].Visibility == Visibility.Collapsed || GetFlow(Children[i])))
			{
				if (Children[i].Visibility != Visibility.Collapsed)
				{
					group.Add(i);
				}

				i++;
			}

			var columns = group.Count == 1 ? 1 : Math.Clamp((int)((width + gap) / (Math.Max(1, MinColumnWidth) + gap)), 1, Math.Min(3, group.Count));
			var columnWidth = (width - ((columns - 1) * gap)) / columns;
			var heights = new double[columns];
			foreach (var index in group)
			{
				var column = 0;
				for (var c = 1; c < columns; c++)
				{
					if (heights[c] < heights[column])
					{
						column = c;
					}
				}

				Children[index].Measure(new Size(columnWidth, double.PositiveInfinity));
				var h = Children[index].DesiredSize.Height;
				_rects[index] = new Rect(column * (columnWidth + gap), y + heights[column], columnWidth, h);
				heights[column] += h + gap;
			}

			var tallest = 0.0;
			foreach (var height in heights)
			{
				tallest = Math.Max(tallest, height);
			}

			y += tallest;
		}

		return new Size(width, Math.Max(0, y - gap));
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		var x0 = Math.Max(0, (finalSize.Width - _width) / 2);
		for (var i = 0; i < Children.Count && i < _rects.Count; i++)
		{
			var r = _rects[i];
			Children[i].Arrange(new Rect(x0 + r.X, r.Y, r.Width, r.Height));
		}

		return finalSize;
	}
}
