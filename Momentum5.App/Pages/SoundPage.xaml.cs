using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;

namespace Momentum5.App.Pages;

public sealed partial class SoundPage : Page
{
	public SoundPage()
	{
		Vm = new SoundViewModel(App.Services);
		InitializeComponent();
		Unloaded += (_, _) => Vm.Dispose();
	}

	public SoundViewModel Vm { get; }

	private void Presets_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SoundPresetsPage));
}
