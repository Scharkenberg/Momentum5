using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;

namespace Momentum5.App.Pages;

public sealed partial class NoiseControlPage : Page
{
	public NoiseControlPage()
	{
		Vm = new NoiseViewModel(App.Services);
		InitializeComponent();
		Unloaded += (_, _) => Vm.Dispose();
	}

	public NoiseViewModel Vm { get; }
}
