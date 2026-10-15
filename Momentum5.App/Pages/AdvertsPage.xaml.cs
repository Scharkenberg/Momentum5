using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;

namespace Momentum5.App.Pages;

public sealed partial class AdvertsPage : Page
{
	public AdvertsPage()
	{
		Vm = new AdvertsViewModel(App.Services);
		InitializeComponent();
		Unloaded += (_, _) => Vm.Dispose();
	}

	public AdvertsViewModel Vm { get; }
}
