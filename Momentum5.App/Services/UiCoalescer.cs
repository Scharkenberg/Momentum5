using System;
using System.Threading;

namespace Momentum5.App.Services;

/// <summary>Collapses bursts of change notifications (a headset sync raises dozens) into a single UI-thread run of <paramref name="action"/>.</summary>
public sealed class UiCoalescer(IUiDispatcher ui, Action action)
{
	private int _pending;

	public void Request()
	{
		if (Interlocked.Exchange(ref _pending, 1) == 1)
		{
			return;
		}

		ui.Post(() =>
		{
			Interlocked.Exchange(ref _pending, 0);
			action();
		});
	}
}
