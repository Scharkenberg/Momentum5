using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;

namespace Momentum5.App.Pages;

public sealed partial class SpatialPage : Page
{
	public SpatialPage()
	{
		Vm = new SpatialViewModel(App.Services);
		InitializeComponent();
		Unloaded += (_, _) => Vm.Dispose();
	}

	public SpatialViewModel Vm { get; }
}
