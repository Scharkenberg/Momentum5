using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Momentum5.App.Pages.Advanced;

/// <summary>
/// Advanced hub (X-02, DevMode-gated by the shell navigation entry): top navigation to the GAIA console (X-03),
/// parametric EQ (X-04), touch buttons (X-05), factory reset (X-08) and firmware (X-13).
/// </summary>
public sealed partial class AdvancedPage : Page
{
	private static readonly Dictionary<string, System.Type> SubPages = new()
	{
		["console"] = typeof(ConsolePage),
		["peq"] = typeof(ParametricEqPage),
		["mmi"] = typeof(MmiPage),
		["reset"] = typeof(FactoryResetPage),
		["firmware"] = typeof(FirmwarePage),
	};

	public AdvancedPage()
	{
		InitializeComponent();
		SubNav.Loaded += (_, _) =>
		{
			if (SubNav.SelectedItem is null)
			{
				SubNav.SelectedItem = SubNav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();
			}
		};
	}

	private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
	{
		if (args.SelectedItem is NavigationViewItem { Tag: string tag } && SubPages.TryGetValue(tag, out var page))
		{
			SubFrame.Navigate(page);
		}
	}
}
