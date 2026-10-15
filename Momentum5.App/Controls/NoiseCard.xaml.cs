using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;

namespace Momentum5.App.Controls;

/// <summary>The official app's noise-control card: mode, ANC/transparency slider and anti-wind. Shared by the dashboard and the noise page.</summary>
public sealed partial class NoiseCard : UserControl
{
	public static readonly DependencyProperty VmProperty = DependencyProperty.Register(nameof(Vm), typeof(NoiseViewModel), typeof(NoiseCard), new PropertyMetadata(null));

	public NoiseCard() => InitializeComponent();

	public NoiseViewModel Vm
	{
		get => (NoiseViewModel)GetValue(VmProperty);
		set => SetValue(VmProperty, value);
	}
}
