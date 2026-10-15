using Momentum5.Protocol;

namespace Momentum5.Client;

public sealed record NotificationOptions
{
	public IReadOnlyList<RegistrationEntry> Registrations { get; init; } = Registration.OfficialColdStart;

	/// <summary>When a Sennheiser registration fails, try the same feature once under the Qualcomm vendor.</summary>
	public bool VendorFallback { get; init; }
}

/// <summary>Registers for pushes and turns unsolicited frames into typed events. Unknown pushes are never dropped.</summary>
public sealed class NotificationHub
{
	private readonly CommandPipeline _pipeline;
	private readonly Action<string>? _log;
	private readonly List<RegistrationEntry> _registered = [];
	private bool _attached = true;

	public NotificationHub(CommandPipeline pipeline, Action<string>? log = null)
	{
		ArgumentNullException.ThrowIfNull(pipeline);
		_pipeline = pipeline;
		_log = log;
		_pipeline.Unsolicited += OnUnsolicited;
	}

	public event EventHandler<SettingNotification>? SettingChanged;

	public event EventHandler<UnknownNotification>? UnknownReceived;

	public IReadOnlyList<RegistrationEntry> Registered => _registered;

	/// <summary>Sequential registration; a failing feature is logged and skipped. Link failures propagate.</summary>
	public async Task RegisterAsync(NotificationOptions? options, CancellationToken ct)
	{
		options ??= new NotificationOptions();
		foreach (var entry in options.Registrations)
		{
			if (await TryRegisterAsync(entry, ct).ConfigureAwait(false))
			{
				continue;
			}

			if (options.VendorFallback && entry.Vendor == GaiaVendor.Sennheiser)
			{
				await TryRegisterAsync(new RegistrationEntry(GaiaVendor.Qualcomm, entry.Feature), ct).ConfigureAwait(false);
			}
		}
	}

	/// <summary>Stops decoding and forgets registrations (the headset forgets them when the link closes).</summary>
	public void Detach()
	{
		if (_attached)
		{
			_attached = false;
			_pipeline.Unsolicited -= OnUnsolicited;
		}

		_registered.Clear();
	}

	private async Task<bool> TryRegisterAsync(RegistrationEntry entry, CancellationToken ct)
	{
		try
		{
			await _pipeline.ExecuteAsync(
				entry.Vendor, Registration.RegisterCommand, new byte[] { entry.Feature }, null, ct).ConfigureAwait(false);
			_registered.Add(entry);
			return true;
		}
		catch (GaiaProtocolException ex)
		{
			_log?.Invoke($"Registration {entry.Vendor}:{entry.Feature:X2} rejected: {ex.Message}");
		}
		catch (TimeoutException ex)
		{
			_log?.Invoke($"Registration {entry.Vendor}:{entry.Feature:X2} timed out: {ex.Message}");
		}

		return false;
	}

	private void OnUnsolicited(object? sender, GaiaFrame frame)
	{
		if (GaiaWord.Kind(frame.Command) != GaiaMessageKind.Notification)
		{
			UnknownReceived?.Invoke(this, new UnknownNotification(frame.Vendor, frame.Command, frame.Payload));
			return;
		}

		foreach (var notification in NotificationMap.Decode(frame))
		{
			switch (notification)
			{
				case SettingNotification setting:
					SettingChanged?.Invoke(this, setting);
					break;
				case UnknownNotification unknown:
					UnknownReceived?.Invoke(this, unknown);
					break;
			}
		}
	}
}
