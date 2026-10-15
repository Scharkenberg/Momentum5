using Momentum5.Core.Settings;
using Momentum5.Protocol;
using Momentum5.Protocol.Bindings;

namespace Momentum5.Client;

public sealed partial class SettingService
{
	/// <summary>Serial GETs for every non-indexed key of the group; a failing key is logged (and removed on a protocol error), the rest continue.</summary>
	public async ValueTask RefreshAsync(SettingGroup group, CancellationToken ct)
	{
		var session = Active();
		var done = new HashSet<(GaiaVendor, ushort, string)>();
		foreach (var binding in GaiaCatalog.ByGroup(group))
		{
			if (binding.GetCmd is not { } get || binding.Indexed)
			{
				continue;
			}

			if (!done.Add((binding.Vendor, GaiaWord.AsCommand(get), Convert.ToHexString(binding.BuildGetPayload(-1)))))
			{
				continue;
			}

			await TryFetchAsync(session, binding, -1, ct).ConfigureAwait(false);
		}

		if (group == SettingGroup.Eq)
		{
			await RefreshBandFrequenciesAsync(session, ct).ConfigureAwait(false);
			await RefreshPeqBandParamsAsync(session, ct).ConfigureAwait(false);
		}
	}

	/// <summary>Initial refresh: every group.</summary>
	public async ValueTask RefreshAllAsync(CancellationToken ct)
	{
		foreach (var group in Enum.GetValues<SettingGroup>())
		{
			await RefreshAsync(group, ct).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Writes bands 0..n-1 serially (busy grace is honoured by the pipeline), then reads the table back via 1003.
	/// On a failure at band k, bands 0..k-1 are restored to their previous gains.
	/// </summary>
	public async ValueTask<SetResult<IReadOnlyList<double>>> SetEqAsync(IReadOnlyList<double> gains, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(gains);
		var session = Active();
		var table = Require(Keys.EqAllGains);
		var priorFrame = await FetchAsync(session, table, -1, ct).ConfigureAwait(false);
		if (!table.Format.TryDecode(priorFrame.Payload.Span, -1, out var before))
		{
			throw new GaiaDecodeException(Keys.EqAllGains.Name, GaiaFormatter.Hex(priorFrame.Payload.Span));
		}

		if (gains.Count != before.Count)
		{
			throw new ArgumentException($"Expected {before.Count} gains, got {gains.Count}.", nameof(gains));
		}

		var bandBinding = Require(Keys.EqBand(0));
		var written = 0;
		try
		{
			for (; written < gains.Count; written++)
			{
				await WriteBandAsync(session, bandBinding, written, gains[written], ct).ConfigureAwait(false);
			}
		}
		catch
		{
			for (var i = 0; i < written; i++)
			{
				try
				{
					await WriteBandAsync(session, bandBinding, i, before[i], CancellationToken.None).ConfigureAwait(false);
				}
				catch (Exception ex) when (ex is GaiaProtocolException or TimeoutException or Momentum5.Core.Transport.LinkException)
				{
					log?.Invoke($"EQ rollback of band {i} failed: {ex.Message}");
				}
			}

			await TryFetchAsync(session, table, -1, CancellationToken.None).ConfigureAwait(false);
			throw;
		}

		var readback = await FetchAsync(session, table, -1, ct).ConfigureAwait(false);
		if (!table.Format.TryDecode(readback.Payload.Span, -1, out var actual))
		{
			throw new GaiaDecodeException(Keys.EqAllGains.Name, GaiaFormatter.Hex(readback.Payload.Span));
		}

		var verified = actual.Count == gains.Count;
		for (var i = 0; verified && i < gains.Count; i++)
		{
			verified = Math.Abs(actual[i] - gains[i]) < 0.05;
		}

		return new SetResult<IReadOnlyList<double>>(verified, actual);
	}

	private async ValueTask WriteBandAsync(ActiveSession session, SettingBinding<double> binding, int band, double gain, CancellationToken ct)
	{
		if (!binding.TryEncodeSet(gain, band, out var payload, out var failure))
		{
			throw new ArgumentException(failure, nameof(gain));
		}

		await SendSetAsync(session, binding, band, payload, ct).ConfigureAwait(false);
		store.Apply(binding.Name, band, gain);
	}

	private async Task RefreshCoupledAsync(CancellationToken ct)
	{
		foreach (var group in new[] { SettingGroup.Anc, SettingGroup.Transparency })
		{
			await RefreshAsync(group, ct).ConfigureAwait(false);
		}
	}

	private async Task RefreshBandFrequenciesAsync(ActiveSession session, CancellationToken ct)
	{
		var config = store.Current.Get(Keys.EqConfig);
		var freq = GaiaCatalog.Registry.ByName(Keys.EqBandFreq(0).Name);
		if (config is null || freq is null)
		{
			return;
		}

		for (var band = 0; band < config.Bands; band++)
		{
			if (!store.Current.TryGet(Keys.EqBandFreq(band), out _))
			{
				await TryFetchAsync(session, freq, band, ct).ConfigureAwait(false);
			}
		}
	}

	/// <summary>
	/// X-04 read-dump: per-band Q (100D) and filter type (100F), fetched only while missing — the keys are
	/// read-only, so a landed value never needs re-reading. Rejections are logged and removed by
	/// <see cref="TryFetchAsync"/>; the parametric-EQ page latches the unsupported case (error 00/01) from
	/// its own per-band reads because this path never rethrows.
	/// </summary>
	private async Task RefreshPeqBandParamsAsync(ActiveSession session, CancellationToken ct)
	{
		var config = store.Current.Get(Keys.EqConfig);
		if (config is null)
		{
			return;
		}

		foreach (var key in BandParamKeys(config.Bands))
		{
			var binding = GaiaCatalog.Registry.ByName(key.Name);
			if (binding is null || store.Current.TryGet(key, out _))
			{
				continue;
			}

			await TryFetchAsync(session, binding, key.Index, ct).ConfigureAwait(false);
		}
	}

	private static IEnumerable<SettingKey<int>> BandParamKeys(int bands)
	{
		for (var band = 0; band < bands; band++)
		{
			yield return Keys.EqBandQ(band);
			yield return Keys.EqBandFilterType(band);
		}
	}

	private async Task TryFetchAsync(ActiveSession session, ISettingBinding binding, int index, CancellationToken ct)
	{
		try
		{
			await FetchAsync(session, binding, index, ct).ConfigureAwait(false);
		}
		catch (GaiaProtocolException ex)
		{
			log?.Invoke($"{binding.Name}: headset rejected GET ({ex.Message}).");
			store.Remove(binding.Name, binding.Indexed ? index : -1);
		}
		catch (TimeoutException ex)
		{
			log?.Invoke($"{binding.Name}: {ex.Message}");
		}
		catch (EqBandOutOfRangeException ex)
		{
			log?.Invoke(ex.Message);
		}
	}
}
