using System;
using Microsoft.UI.Dispatching;

namespace Momentum5.App.Services;

/// <summary>Marshals work to the UI thread so view models never touch DispatcherQueue directly.</summary>
public interface IUiDispatcher
{
	bool HasThreadAccess { get; }

	/// <summary>Runs inline on the UI thread, otherwise enqueues.</summary>
	void Post(Action action);
}

public sealed class DispatcherQueueUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
	public bool HasThreadAccess => queue.HasThreadAccess;

	public void Post(Action action)
	{
		ArgumentNullException.ThrowIfNull(action);
		if (queue.HasThreadAccess)
		{
			action();
		}
		else
		{
			queue.TryEnqueue(() => action());
		}
	}
}
