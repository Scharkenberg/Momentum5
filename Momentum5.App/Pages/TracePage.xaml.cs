using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;
using Momentum5.Client;
using Windows.ApplicationModel.DataTransfer;

namespace Momentum5.App.Pages;

public sealed partial class TracePage : Page
{
	public TracePage()
	{
		Vm = new TraceViewModel(App.Services);
		InitializeComponent();
		Unloaded += (_, _) => Vm.Dispose();
	}

	public TraceViewModel Vm { get; }

	private System.Collections.Generic.List<TraceEntry> Selection() =>
		List.SelectedItems.Count > 0 ? [.. List.SelectedItems.OfType<TraceEntry>().OrderBy(e => e.Sequence)] : [.. Vm.Rows];

	private static void Copy(string text)
	{
		var data = new DataPackage();
		data.SetText(text);
		Clipboard.SetContent(data);
	}

	private void CopyLines_Click(object sender, RoutedEventArgs e) => Copy(TraceViewModel.ToLineBlock(Selection()));

	private void CopyHex_Click(object sender, RoutedEventArgs e) => Copy(TraceViewModel.ToHexBlock(Selection()));
}
