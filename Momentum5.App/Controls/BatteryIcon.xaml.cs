using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Momentum5.App.Controls;

/// <summary>Crisp battery gauge: outline, proportional fill (green while charging, red when low) and a bolt while charging.</summary>
public sealed partial class BatteryIcon : UserControl
{
	private const double MaxFill = 18.667;
	private static readonly SolidColorBrush Green = new(Windows.UI.Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F));
	private static readonly SolidColorBrush Red = new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B));

	public static readonly DependencyProperty PercentProperty =
		DependencyProperty.Register(nameof(Percent), typeof(int), typeof(BatteryIcon), new PropertyMetadata(0, OnChanged));

	public static readonly DependencyProperty ChargingProperty =
		DependencyProperty.Register(nameof(Charging), typeof(bool), typeof(BatteryIcon), new PropertyMetadata(false, OnChanged));

	public static readonly DependencyProperty HasValueProperty =
		DependencyProperty.Register(nameof(HasValue), typeof(bool), typeof(BatteryIcon), new PropertyMetadata(false, OnChanged));

	public BatteryIcon()
	{
		InitializeComponent();
		Loaded += (_, _) => Update();
		_ = RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Update());
	}

	public int Percent
	{
		get => (int)GetValue(PercentProperty);
		set => SetValue(PercentProperty, value);
	}

	public bool Charging
	{
		get => (bool)GetValue(ChargingProperty);
		set => SetValue(ChargingProperty, value);
	}

	public bool HasValue
	{
		get => (bool)GetValue(HasValueProperty);
		set => SetValue(HasValueProperty, value);
	}

	private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((BatteryIcon)d).Update();

	private void Update()
	{
		var fg = Foreground ?? new SolidColorBrush(Colors.White);
		var percent = Math.Clamp(Percent, 0, 100);
		Body.BorderBrush = fg;
		Cap.Background = fg;
		Body.Opacity = Cap.Opacity = HasValue ? 1 : 0.5;
		Fill.Width = HasValue ? Math.Round(Math.Max(percent > 0 ? 2 : 0, MaxFill * percent / 100.0)) : 0;
		Fill.Background = Charging && HasValue ? Green : percent <= 15 && HasValue ? Red : fg;
		Bolt.Visibility = Charging && HasValue ? Visibility.Visible : Visibility.Collapsed;
	}
}
