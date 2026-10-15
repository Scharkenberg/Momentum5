using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;

namespace Momentum5.App.Pages;

public sealed partial class MultipointPage : Page
{
	public MultipointPage()
	{
		Vm = new MultipointViewModel(App.Services);
		InitializeComponent();
		Unloaded += (_, _) => Vm.Dispose();
		_ = Vm.RefreshAsync();
	}

	public MultipointViewModel Vm { get; }
}
