using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Momentum5.App.Controls;

/// <summary>
/// Left-to-right panel that wraps onto new lines when the available width runs out, so button rows and
/// label/value pairs never overflow a narrow (360 DIP) window. A child with <c>HorizontalAlignment="Stretch"</c>
/// expands to the remaining width of its line.
/// </summary>
public sealed partial class WrapPanel : Panel
{
	public static readonly DependencyProperty HorizontalSpacingProperty =
		DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(8.0, OnChanged));

	public static readonly DependencyProperty VerticalSpacingProperty =
		DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(8.0, OnChanged));

	private readonly List<(int First, int Count, double Height)> _lines = [];

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

	private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((WrapPanel)d).InvalidateMeasure();

	protected override Size MeasureOverride(Size availableSize)
	{
		var avail = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
		_lines.Clear();
		double x = 0, lineH = 0, maxW = 0, totalH = 0;
		var first = 0;
		var count = 0;
		for (var i = 0; i < Children.Count; i++)
		{
			var child = Children[i];
			child.Measure(new Size(avail, availableSize.Height));
			if (child.Visibility == Visibility.Collapsed)
			{
				continue;
			}

			var w = Math.Min(child.DesiredSize.Width, avail);
			var h = child.DesiredSize.Height;
			if (count > 0 && x + HorizontalSpacing + w > avail)
			{
				_lines.Add((first, count, lineH));
				totalH += lineH + VerticalSpacing;
				maxW = Math.Max(maxW, x);
				x = 0;
				lineH = 0;
				first = i;
				count = 0;
			}

			x += (count > 0 ? HorizontalSpacing : 0) + w;
			lineH = Math.Max(lineH, h);
			count++;
		}

		if (count > 0)
		{
			_lines.Add((first, count, lineH));
			totalH += lineH;
			maxW = Math.Max(maxW, x);
		}

		return new Size(double.IsInfinity(availableSize.Width) ? maxW : Math.Min(availableSize.Width, maxW), totalH);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		double y = 0;
		foreach (var (first, count, height) in _lines)
		{
			double x = 0;
			var seen = 0;
			for (var i = first; i < Children.Count && seen < count; i++)
			{
				var child = Children[i];
				if (child.Visibility == Visibility.Collapsed)
				{
					continue;
				}

				seen++;
				var w = Math.Min(child.DesiredSize.Width, finalSize.Width);
				if (seen == count && child is FrameworkElement { HorizontalAlignment: HorizontalAlignment.Stretch })
				{
					w = Math.Max(w, finalSize.Width - x);
				}

				child.Arrange(new Rect(x, y, w, height));
				x += w + HorizontalSpacing;
			}

			y += height + VerticalSpacing;
		}

		return finalSize;
	}
}
