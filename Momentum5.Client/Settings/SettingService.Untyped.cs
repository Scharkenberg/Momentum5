using Momentum5.Core.Settings;
using Momentum5.Protocol;
using Momentum5.Protocol.Bindings;

namespace Momentum5.Client;

public sealed partial class SettingService
{
	/// <summary>Untyped GET for descriptor-driven callers (CLI, DevTools).</summary>
	public async ValueTask<object> GetValueAsync(ISettingBinding binding, int index, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(binding);
		if (binding.GetCmd is null)
		{
			throw new InvalidOperationException($"{binding.Name} cannot be read.");
		}

		var frame = await FetchAsync(Active(), binding, index, ct).ConfigureAwait(false);
		return binding.TryDecodeResponse(frame.Payload.Span, index, out var value) && value is not null
			? value
			: throw new GaiaDecodeException(binding.Name, GaiaFormatter.Hex(frame.Payload.Span));
	}

	/// <summary>Untyped gate → SET → re-GET → compare. Write-only keys report <c>Verified = true</c> with the written value.</summary>
	public async ValueTask<(bool Verified, object Actual)> SetValueAsync(ISettingBinding binding, int index, object value, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(binding);
		ArgumentNullException.ThrowIfNull(value);
		var session = Active();
		if (!binding.TryEncodeSet(value, index, out var payload, out var failure))
		{
			throw new ArgumentException(failure, nameof(value));
		}

		await SendSetAsync(session, binding, index, payload, ct).ConfigureAwait(false);
		if (binding.GetCmd is null)
		{
			return (true, value);
		}

		var actual = await GetValueAsync(binding, index, ct).ConfigureAwait(false);
		var verified = binding.TryEncodeResponse(value, index, out var a) && binding.TryEncodeResponse(actual, index, out var b)
			? a.AsSpan().SequenceEqual(b)
			: Equals(value, actual);
		if (binding.RefreshTogether && binding.Group is SettingGroup.Anc or SettingGroup.Transparency)
		{
			await RefreshCoupledAsync(ct).ConfigureAwait(false);
		}

		return (verified, actual);
	}
}
