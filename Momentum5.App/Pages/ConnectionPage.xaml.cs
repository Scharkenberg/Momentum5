using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;

namespace Momentum5.App.Pages;

public sealed partial class ConnectionPage : Page
{
	public ConnectionPage()
	{
		Vm = new ConnectionViewModel(App.Services);
		InitializeComponent();
		Unloaded += (_, _) => Vm.Dispose();
	}

	public ConnectionViewModel Vm { get; }
}
