using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Momentum5.App.Shell;
using Momentum5.Core.Settings;

namespace Momentum5.App.ViewModels;

/// <summary>Base for the Advanced page view models: delivers <see cref="IHeadsetState.Changed"/> snapshots on the UI thread.</summary>
public abstract class ShellViewModelBase : ObservableObject
{
	protected ShellViewModelBase(IUiDispatcher dispatcher, IHeadsetState state)
	{
		Dispatcher = dispatcher;
		State = state;
		State.Changed += (_, snapshot) => Dispatcher.Post(() => OnSnapshot(snapshot));
	}

	protected IUiDispatcher Dispatcher { get; }

	protected IHeadsetState State { get; }

	protected HeadsetSnapshot Snapshot => State.Current;

	protected virtual void OnSnapshot(HeadsetSnapshot snapshot)
	{
	}

	protected void Post(Action action) => Dispatcher.Post(action);
}
