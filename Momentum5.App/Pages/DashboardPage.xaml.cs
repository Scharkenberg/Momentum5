using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;

namespace Momentum5.App.Pages;

public sealed partial class DashboardPage : Page
{
	public DashboardPage()
	{
		Vm = new DashboardViewModel(App.Services);
		InitializeComponent();
		Unloaded += (_, _) => Vm.Dispose();
	}

	public DashboardViewModel Vm { get; }

	private bool _heroNarrow;

	/// <summary>Narrow hero: smaller badge, battery ring moves under the name.</summary>
	private void Hero_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		var narrow = e.NewSize.Width < 520;
		if (narrow == _heroNarrow)
		{
			return;
		}

		_heroNarrow = narrow;
		Hero.Padding = new Thickness(narrow ? 12 : 16);
		HeroGrid.ColumnSpacing = narrow ? 0 : 16;
		HeroGrid.RowSpacing = narrow ? 8 : 0;
		HeroBadge.Width = HeroBadge.Height = narrow ? 40 : 52;
		RingBox.Width = RingBox.Height = Ring.Width = Ring.Height = narrow ? 60 : 72;
		Grid.SetRow(HeroRing, narrow ? 1 : 0);
		Grid.SetColumn(HeroRing, narrow ? 0 : 1);
		Grid.SetColumnSpan(HeroRing, narrow ? 2 : 1);
		HeroRing.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Center;
	}

	private void Manage_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(MultipointPage));

	private void Sound_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SoundPage));

	private void Immersive_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SpatialPage));
}
