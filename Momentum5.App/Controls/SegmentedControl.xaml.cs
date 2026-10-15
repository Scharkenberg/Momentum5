using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Momentum5.App.Controls;

/// <summary>Equal-width segmented selector (the official app's Adaptive | Custom | Off). <see cref="Options"/> is comma separated; bind <see cref="SelectedIndex"/> two-way.</summary>
public sealed partial class SegmentedControl : UserControl
{
	public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(nameof(Options), typeof(string), typeof(SegmentedControl), new PropertyMetadata(string.Empty, (d, _) => ((SegmentedControl)d).Rebuild()));

	public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(SegmentedControl), new PropertyMetadata(-1, (d, _) => ((SegmentedControl)d).Sync()));

	public static readonly DependencyProperty DenseProperty = DependencyProperty.Register(nameof(Dense), typeof(bool), typeof(SegmentedControl), new PropertyMetadata(false, (d, _) => ((SegmentedControl)d).Rebuild()));

	public SegmentedControl() => InitializeComponent();

	/// <summary>Shorter segments for compact layouts.</summary>
	public bool Dense
	{
		get => (bool)GetValue(DenseProperty);
		set => SetValue(DenseProperty, value);
	}

	public string Options
	{
		get => (string)GetValue(OptionsProperty);
		set => SetValue(OptionsProperty, value);
	}

	public int SelectedIndex
	{
		get => (int)GetValue(SelectedIndexProperty);
		set => SetValue(SelectedIndexProperty, value);
	}

	private void Rebuild()
	{
		Host.Children.Clear();
		Host.ColumnDefinitions.Clear();
		var items = Options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		for (var i = 0; i < items.Length; i++)
		{
			var index = i;
			Host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			var button = new ToggleButton
			{
				Content = items[i],
				HorizontalAlignment = HorizontalAlignment.Stretch,
				MinHeight = Dense ? 28 : 40,
				MinWidth = 0,
				Padding = Dense ? new Thickness(4, 0, 4, 0) : new Thickness(6, 5, 6, 6),
				CornerRadius = new CornerRadius(8),
				FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			};
			button.Click += (_, _) =>
			{
				SelectedIndex = index;
				Sync();
			};
			Grid.SetColumn(button, i);
			Host.Children.Add(button);
		}

		Sync();
	}

	private void Sync()
	{
		for (var i = 0; i < Host.Children.Count; i++)
		{
			if (Host.Children[i] is ToggleButton b)
			{
				b.IsChecked = i == SelectedIndex;
			}
		}
	}
}
