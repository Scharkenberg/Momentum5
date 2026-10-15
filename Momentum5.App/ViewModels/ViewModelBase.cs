using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Momentum5.App.Services;
using Momentum5.Core.Settings;

namespace Momentum5.App.ViewModels;

/// <summary>Base for view models bound to the headset state; <see cref="OnStateChanged"/> always runs on the UI thread.</summary>
public abstract class ViewModelBase : ObservableObject, IDisposable
{
	private readonly IHeadsetState _state;
	private readonly UiCoalescer _coalescer;
	private bool _disposed;

	protected ViewModelBase(IHeadsetState state, IUiDispatcher ui)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(ui);
		_state = state;
		Ui = ui;
		_coalescer = new UiCoalescer(ui, () =>
		{
			if (!_disposed)
			{
				OnStateChanged(_state.Current);
			}
		});
		_state.Changed += OnChanged;
	}

	protected IUiDispatcher Ui { get; }

	protected abstract void OnStateChanged(HeadsetSnapshot snapshot);

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_state.Changed -= OnChanged;
		OnDisposed();
	}

	protected virtual void OnDisposed()
	{
	}

	private void OnChanged(object? sender, HeadsetSnapshot snapshot) => _coalescer.Request();
}
