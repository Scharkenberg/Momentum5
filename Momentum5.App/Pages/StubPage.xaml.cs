using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Momentum5.App.Pages;

public sealed partial class StubPage : Page
{
	public StubPage() => InitializeComponent();

	protected override void OnNavigatedTo(NavigationEventArgs e) => Title.Text = e.Parameter as string ?? string.Empty;
}
