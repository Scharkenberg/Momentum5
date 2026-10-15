using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Momentum5.App.Controls;

/// <summary>Title and description on the left, switch on the right (like the official app). Bind <see cref="IsOn"/> two-way.</summary>
public sealed partial class SwitchRow : UserControl
{
	public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SwitchRow), new PropertyMetadata(string.Empty));

	public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SwitchRow), new PropertyMetadata(string.Empty));

	public static readonly DependencyProperty WarningProperty = DependencyProperty.Register(nameof(Warning), typeof(string), typeof(SwitchRow), new PropertyMetadata(string.Empty));

	public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(SwitchRow), new PropertyMetadata(false));

	public SwitchRow() => InitializeComponent();

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

	public string Warning
	{
		get => (string)GetValue(WarningProperty);
		set => SetValue(WarningProperty, value);
	}

	public bool IsOn
	{
		get => (bool)GetValue(IsOnProperty);
		set => SetValue(IsOnProperty, value);
	}
}
