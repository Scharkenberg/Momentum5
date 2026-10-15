using Momentum5.Core.Settings;
using Momentum5.Protocol;

namespace Momentum5.Client;

public sealed record HandshakeOptions
{
	public NotificationOptions Notifications { get; init; } = new();
}

/// <summary>ref/00 §5: api version → features → model (gate) → firmware (trust) → serial → hw rev → registrations.</summary>
public sealed class Handshake(
	CommandPipeline pipeline,
	NotificationHub hub,
	SessionContext context,
	HandshakeOptions? options = null,
	Action<string>? log = null)
{
	private const int MaxFeaturePages = 4;

	/// <exception cref="UnsupportedDeviceException">Model is not an M5AEBT*.</exception>
	public async Task<SessionInfo> RunAsync(string transportId, CancellationToken ct)
	{
		options ??= new HandshakeOptions();

		await TryAsync("api version", () => pipeline.ExecuteAsync(GaiaVendor.Qualcomm, 0x0000, ReadOnlyMemory<byte>.Empty, null, ct).AsTask())
			.ConfigureAwait(false);
		var features = await ReadFeaturesAsync(ct).ConfigureAwait(false);

		var model = await pipeline.GetAsync(Keys.Model, ct).ConfigureAwait(false);
		if (!model.StartsWith(M5Profile.ModelPrefix, StringComparison.Ordinal))
		{
			throw new UnsupportedDeviceException(model);
		}

		var firmware = await pipeline.GetAsync(Keys.Firmware, ct).ConfigureAwait(false);
		var decision = M5Profile.Evaluate(model, firmware);
		context.Trust = decision.Trust;
		log?.Invoke($"{model} fw {firmware.Major}.{firmware.Minor}.{firmware.Patch}: {decision.Trust} ({decision.Reason})");

		var serial = string.Empty;
		await TryAsync("serial", async () => serial = await pipeline.GetAsync(Keys.Serial, ct).ConfigureAwait(false)).ConfigureAwait(false);
		await TryAsync("hardware revision", async () => await pipeline.GetAsync(Keys.HwRevision, ct).ConfigureAwait(false)).ConfigureAwait(false);

		var info = new SessionInfo(model, $"{firmware.Major}.{firmware.Minor}.{firmware.Patch}", serial, decision.Trust, transportId)
		{
			Features = features,
		};
		context.Info = info;

		await hub.RegisterAsync(options.Notifications, ct).ConfigureAwait(false);
		return info;
	}

	private async Task<List<string>> ReadFeaturesAsync(CancellationToken ct)
	{
		var features = new List<string>();
		ushort command = 0x0001;
		for (var page = 0; page < MaxFeaturePages; page++)
		{
			GaiaFrame? reply = null;
			var current = command;
			await TryAsync("features", async () =>
				reply = await pipeline.ExecuteAsync(GaiaVendor.Sennheiser, current, ReadOnlyMemory<byte>.Empty, null, ct).ConfigureAwait(false))
				.ConfigureAwait(false);
			if (reply is not { } frame || frame.Payload.Length == 0)
			{
				break;
			}

			var payload = frame.Payload.Span;
			for (var i = 1; i + 1 < payload.Length; i += 2)
			{
				features.Add($"S:{payload[i]:X2} v{payload[i + 1]}");
			}

			if (payload[0] == 0)
			{
				break;
			}

			command = 0x0002;
		}

		return features;
	}

	private async Task TryAsync(string what, Func<Task> action)
	{
		try
		{
			await action().ConfigureAwait(false);
		}
		catch (GaiaProtocolException ex)
		{
			log?.Invoke($"Handshake: {what} unavailable ({ex.Message}).");
		}
		catch (TimeoutException ex)
		{
			log?.Invoke($"Handshake: {what} timed out ({ex.Message}).");
		}
		catch (GaiaDecodeException ex)
		{
			log?.Invoke($"Handshake: {what} undecodable ({ex.Message}).");
		}
	}
}
