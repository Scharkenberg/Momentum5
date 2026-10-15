using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Momentum5.App.ViewModels;

namespace Momentum5.App.Controls;

/// <summary>Toggle bound to a <see cref="SettingViewModel{T}"/> with an optional description and the shared read-back warning.</summary>
public sealed partial class ToggleRow : UserControl
{
	public static readonly DependencyProperty SettingProperty = DependencyProperty.Register(nameof(Setting), typeof(SettingViewModel<bool>), typeof(ToggleRow), new PropertyMetadata(null));

	public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(ToggleRow), new PropertyMetadata(string.Empty));

	public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(ToggleRow), new PropertyMetadata(string.Empty));

	public ToggleRow() => InitializeComponent();

	public SettingViewModel<bool> Setting
	{
		get => (SettingViewModel<bool>)GetValue(SettingProperty);
		set => SetValue(SettingProperty, value);
	}

	public string Title
	{
		get => (string)GetValue(TitleProperty);
		set => SetValue(TitleProperty, value);
	}

	public string Description
	{
		get => (string)GetValue(DescriptionProperty);
		set => SetValue(DescriptionProperty, value);
	}
}
