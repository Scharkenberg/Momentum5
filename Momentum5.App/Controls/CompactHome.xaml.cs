using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;

namespace Momentum5.App.Controls;

/// <summary>The compact quick controls (noise, switches, EQ preset, connected devices).</summary>
public sealed partial class CompactHome : UserControl
{
	public static readonly DependencyProperty VmProperty = DependencyProperty.Register(nameof(Vm), typeof(DashboardViewModel), typeof(CompactHome), new PropertyMetadata(null));

	public static readonly DependencyProperty PresetsProperty = DependencyProperty.Register(nameof(Presets), typeof(PresetsViewModel), typeof(CompactHome), new PropertyMetadata(null));

	public CompactHome() => InitializeComponent();

	public DashboardViewModel Vm
	{
		get => (DashboardViewModel)GetValue(VmProperty);
		set => SetValue(VmProperty, value);
	}

	public PresetsViewModel Presets
	{
		get => (PresetsViewModel)GetValue(PresetsProperty);
		set => SetValue(PresetsProperty, value);
	}

	/// <summary>Height the content needs (used by hosts to fit their window).</summary>
	public double ContentHeight => Stack.ActualHeight;

	private async void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (e.AddedItems.Count > 0 && e.AddedItems[0] is PresetItem item && Presets is { } presets)
		{
			await presets.ApplyAsync(item.Preset);
		}
	}
}
