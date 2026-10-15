using Momentum5.Core.Transport;
using Windows.Devices.Radios;

namespace Momentum5.Transport.Rfcomm;

/// <summary>Bluetooth radio on/off via <c>Windows.Devices.Radios</c>. No Bluetooth radio counts as off.</summary>
public sealed class BluetoothRadioMonitor : IRadioMonitor, IDisposable
{
	private readonly Lock _gate = new();
	private readonly List<Radio> _radios = [];
	private Task? _init;

	public event EventHandler<bool>? Changed;

	public async ValueTask<bool> IsOnAsync(CancellationToken ct)
	{
		Task init;
		lock (_gate)
		{
			init = _init ??= InitAsync();
		}

		await init.WaitAsync(ct).ConfigureAwait(false);
		return IsOn();
	}

	public void Dispose()
	{
		lock (_gate)
		{
			foreach (var radio in _radios)
			{
				radio.StateChanged -= OnStateChanged;
			}

			_radios.Clear();
		}
	}

	private async Task InitAsync()
	{
		var radios = await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false);
		lock (_gate)
		{
			foreach (var radio in radios.Where(r => r.Kind == RadioKind.Bluetooth))
			{
				radio.StateChanged += OnStateChanged;
				_radios.Add(radio);
			}
		}
	}

	private bool IsOn()
	{
		lock (_gate)
		{
			return _radios.Exists(r => r.State == RadioState.On);
		}
	}

	private void OnStateChanged(Radio sender, object args) => Changed?.Invoke(this, IsOn());
}
