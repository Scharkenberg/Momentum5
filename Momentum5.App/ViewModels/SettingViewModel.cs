using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Momentum5.App.Services;
using Momentum5.Core.Settings;

namespace Momentum5.App.ViewModels;

/// <summary>
/// One setting as UI state. Bind in one line: <c>IsOn="{x:Bind Vm.Anc.Value, Mode=TwoWay}" IsEnabled="{x:Bind Vm.Anc.IsEnabled, Mode=OneWay}"</c>.
/// Writing <see cref="Value"/> sends the change; a failure reverts to the last confirmed value; an unverified read-back sets <see cref="Warning"/>.
/// </summary>
public sealed class SettingViewModel<T> : ViewModelBase
{
	private readonly SettingKey<T> _key;
	private readonly ISettingService _service;
	private readonly Action<string>? _error;
	private T _value = default!;
	private T _confirmed = default!;
	private bool _isBusy;
	private bool _isAvailable;
	private bool _isWritable;
	private bool _suppress;
	private string? _warning;
	private bool _pendingApply;
	private CancellationTokenSource? _debounce;

	public SettingViewModel(SettingKey<T> key, ISettingService service, IHeadsetState state, IUiDispatcher ui, Action<string>? error = null)
		: base(state, ui)
	{
		ArgumentNullException.ThrowIfNull(key);
		ArgumentNullException.ThrowIfNull(service);
		_key = key;
		_service = service;
		_error = error;
		SetCommand = new AsyncRelayCommand<T>(v => ApplyAsync(v!));
		OnStateChanged(state.Current);
	}

	public T Value
	{
		get => _value;
		set
		{
			if (_suppress || EqualityComparer<T>.Default.Equals(_value, value))
			{
				return;
			}

			_value = value;
			OnPropertyChanged();
			_ = ApplyAsync(value);
		}
	}

	/// <summary>Delay between the last slider movement and the write (used by <see cref="NumericValue"/>).</summary>
	public int DebounceMs { get; init; } = 300;

	/// <summary>Slider-friendly view of <see cref="Value"/> for numeric T (int, byte...); writes are debounced.</summary>
	public double NumericValue
	{
		get => Convert.ToDouble(_value, CultureInfo.InvariantCulture);
		set
		{
			var target = (T)Convert.ChangeType(Math.Round(value), typeof(T), CultureInfo.InvariantCulture);
			if (_suppress || EqualityComparer<T>.Default.Equals(_value, target))
			{
				return;
			}

			_value = target;
			OnPropertyChanged();
			OnPropertyChanged(nameof(Value));
			_pendingApply = true;
			_debounce?.Cancel();
			var cts = _debounce = new CancellationTokenSource();
			_ = DebouncedApplyAsync(target, cts.Token);
		}
	}

	public bool IsWritable => _isWritable;

	public bool IsBusy
	{
		get => _isBusy;
		private set
		{
			if (SetProperty(ref _isBusy, value))
			{
				OnPropertyChanged(nameof(IsEnabled));
			}
		}
	}

	/// <summary>The headset reported this key.</summary>
	public bool IsAvailable
	{
		get => _isAvailable;
		private set
		{
			if (SetProperty(ref _isAvailable, value))
			{
				OnPropertyChanged(nameof(IsEnabled));
			}
		}
	}

	/// <summary>Available, writable (trust tier is not Unknown) and not mid-write.</summary>
	public bool IsEnabled => _isAvailable && _isWritable && !_isBusy;

	/// <summary>Sticky until the next verified write: the headset answered with a different value than written.</summary>
	public string? Warning
	{
		get => _warning;
		private set => SetProperty(ref _warning, value);
	}

	public IAsyncRelayCommand<T> SetCommand { get; }

	protected override void OnStateChanged(HeadsetSnapshot snapshot)
	{
		_isWritable = snapshot.Session is { Trust: not TrustTier.Unknown };
		IsAvailable = snapshot.TryGet(_key, out var current);
		OnPropertyChanged(nameof(IsEnabled));
		if (IsAvailable && !_isBusy && !_pendingApply)
		{
			_confirmed = current;
			SetSilently(current);
		}
	}

	private async Task DebouncedApplyAsync(T value, CancellationToken ct)
	{
		try
		{
			await Task.Delay(DebounceMs, ct).ConfigureAwait(true);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		await ApplyAsync(value).ConfigureAwait(true);
	}

	private async Task ApplyAsync(T value)
	{
		_pendingApply = false;
		if (!IsEnabled)
		{
			SetSilently(_confirmed);
			return;
		}

		var previous = _confirmed;
		IsBusy = true;
		try
		{
			var result = await _service.SetAsync(_key, value, CancellationToken.None).ConfigureAwait(true);
			_confirmed = result.Actual;
			SetSilently(result.Actual);
			Warning = result.Verified ? null : $"The headset reports {result.Actual} instead of {value}.";
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			SetSilently(previous);
			_error?.Invoke($"{_key.Name}: {ex.Message}");
		}
		finally
		{
			IsBusy = false;
		}
	}

	private void SetSilently(T value)
	{
		_suppress = true;
		try
		{
			if (!EqualityComparer<T>.Default.Equals(_value, value))
			{
				_value = value;
				OnPropertyChanged(nameof(Value));
				OnPropertyChanged(nameof(NumericValue));
			}
		}
		finally
		{
			_suppress = false;
		}
	}
}
