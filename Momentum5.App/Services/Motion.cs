using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Momentum5.App.Services;

/// <summary>Attached motion helpers: <c>Motion.Reveal</c> fades and slides an element in (staggered by its index in the parent panel) once.</summary>
public static class Motion
{
	public static readonly DependencyProperty RevealProperty =
		DependencyProperty.RegisterAttached("Reveal", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnRevealChanged));

	public static bool GetReveal(DependencyObject element) => (bool)element.GetValue(RevealProperty);

	public static void SetReveal(DependencyObject element, bool value) => element.SetValue(RevealProperty, value);

	private static void OnRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is FrameworkElement element && e.NewValue is true)
		{
			element.Loaded -= OnLoaded;
			element.Loaded += OnLoaded;
		}
	}

	private static void OnLoaded(object sender, RoutedEventArgs e)
	{
		var element = (FrameworkElement)sender;
		element.Loaded -= OnLoaded;
		try
		{
			var index = element.Parent is Panel panel ? Math.Clamp(panel.Children.IndexOf(element), 0, 8) : 0;
			ElementCompositionPreview.SetIsTranslationEnabled(element, true);
			var visual = ElementCompositionPreview.GetElementVisual(element);
			var compositor = visual.Compositor;
			var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
			var delay = TimeSpan.FromMilliseconds(index * 35);

			var fade = compositor.CreateScalarKeyFrameAnimation();
			fade.InsertKeyFrame(0f, 0f);
			fade.InsertKeyFrame(1f, 1f, ease);
			fade.Duration = TimeSpan.FromMilliseconds(220);
			fade.DelayTime = delay;
			fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

			var slide = compositor.CreateScalarKeyFrameAnimation();
			slide.InsertKeyFrame(0f, 10f);
			slide.InsertKeyFrame(1f, 0f, ease);
			slide.Duration = TimeSpan.FromMilliseconds(260);
			slide.DelayTime = delay;
			slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

			visual.StartAnimation("Opacity", fade);
			visual.StartAnimation("Translation.Y", slide);
		}
		catch
		{
			// Motion is cosmetic; never let it break a page.
		}
	}
}
