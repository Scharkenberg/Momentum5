using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Momentum5.App.Controls;

/// <summary>Lays children out in one row of equal-width cells that always fit the available width (e.g. EQ band sliders).</summary>
public sealed partial class EvenRowPanel : Panel
{
	public static readonly DependencyProperty MaxItemWidthProperty =
		DependencyProperty.Register(nameof(MaxItemWidth), typeof(double), typeof(EvenRowPanel), new PropertyMetadata(72.0, (d, _) => ((EvenRowPanel)d).InvalidateMeasure()));

	public double MaxItemWidth
	{
		get => (double)GetValue(MaxItemWidthProperty);
		set => SetValue(MaxItemWidthProperty, value);
	}

	private double Cell(double available)
	{
		var n = Math.Max(1, Children.Count);
		var w = double.IsInfinity(available) ? MaxItemWidth : available / n;
		return Math.Min(MaxItemWidth, w);
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		var cell = Cell(availableSize.Width);
		double h = 0;
		foreach (var child in Children)
		{
			child.Measure(new Size(cell, availableSize.Height));
			h = Math.Max(h, child.DesiredSize.Height);
		}

		return new Size(cell * Children.Count, h);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		var cell = Cell(finalSize.Width);
		var x = Math.Max(0, (finalSize.Width - cell * Children.Count) / 2);
		foreach (var child in Children)
		{
			child.Arrange(new Rect(x, 0, cell, finalSize.Height));
			x += cell;
		}

		return finalSize;
	}
}
