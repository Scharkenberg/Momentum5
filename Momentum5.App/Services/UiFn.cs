using Microsoft.UI.Xaml;

namespace Momentum5.App.Services;

/// <summary>Static helpers for <c>x:Bind</c> function bindings.</summary>
public static class UiFn
{
	public static Visibility Vis(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
}
