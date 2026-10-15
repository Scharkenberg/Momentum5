using System.Collections.Concurrent;
using Momentum5.Core.Settings;
using Momentum5.Protocol;
using Momentum5.Protocol.Bindings;

namespace Momentum5.Client;

/// <summary>get / set / verify over the active session. A user write bumps a per-key generation so stale refreshes never overwrite it.</summary>
public sealed partial class SettingService(IActiveSessionProvider sessions, HeadsetStateStore store, Action<string>? log = null) : ISettingService
{
	private readonly ConcurrentDictionary<(string Name, int Slot), long> _generations = new();

	public async ValueTask<T> GetAsync<T>(SettingKey<T> key, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(key);
		var binding = Require(key);
		var frame = await FetchAsync(Active(), binding, key.Index, ct).ConfigureAwait(false);
		return binding.Format.TryDecode(frame.Payload.Span, key.Index, out var value)
			? value
			: throw new GaiaDecodeException(key.Name, GaiaFormatter.Hex(frame.Payload.Span));
	}

	/// <summary>Gate → SET → ack → re-GET → compare. Write-only keys report <c>Verified = true</c> with the written value.</summary>
	public async ValueTask<SetResult<T>> SetAsync<T>(SettingKey<T> key, T value, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(key);
		var binding = Require(key);
		var session = Active();
		if (!binding.TryEncodeSet(value, key.Index, out var payload, out var failure))
		{
			throw new ArgumentException(failure, nameof(value));
		}

		await SendSetAsync(session, binding, key.Index, payload, ct).ConfigureAwait(false);
		if (binding.GetCmd is null)
		{
			return new SetResult<T>(true, value);
		}

		var actual = await GetAsync(key, ct).ConfigureAwait(false);
		var verified = SameOnWire(binding, key.Index, value, actual);
		if (binding.RefreshTogether && binding.Group is SettingGroup.Anc or SettingGroup.Transparency)
		{
			await RefreshCoupledAsync(ct).ConfigureAwait(false);
		}

		return new SetResult<T>(verified, actual);
	}

	private static SettingBinding<T> Require<T>(SettingKey<T> key) =>
		GaiaCatalog.Registry.Find(key) ?? throw new ArgumentException($"No binding for {key.Name}.", nameof(key));

	private static bool SameOnWire<T>(SettingBinding<T> binding, int index, T expected, T actual) =>
		binding.TryEncodeResponse(expected, index, out var a) && binding.TryEncodeResponse(actual, index, out var b)
			? a.AsSpan().SequenceEqual(b)
			: Equals(expected, actual);

	private static int Slot(ISettingBinding binding, int index) => binding.Indexed ? index : -1;

	private static List<ISettingBinding> Siblings(ISettingBinding binding, int index)
	{
		var result = new List<ISettingBinding>();
		if (binding.GetCmd is not { } get)
		{
			result.Add(binding);
			return result;
		}

		var selector = binding.BuildGetPayload(index);
		foreach (var other in GaiaCatalog.Registry.ByCommand(binding.Vendor, get))
		{
			if (other.GetCmd is { } g && GaiaWord.AsCommand(g) == GaiaWord.AsCommand(get)
				&& other.TryParseGet(selector, out _) && !result.Contains(other))
			{
				result.Add(other);
			}
		}

		if (!result.Contains(binding))
		{
			result.Add(binding);
		}

		return result;
	}

	private ActiveSession Active() => sessions.Current ?? throw new NotConnectedException();

	private long Generation(ISettingBinding binding, int index) => _generations.GetValueOrDefault((binding.Name, Slot(binding, index)));

	private void Bump(ISettingBinding binding, int index) =>
		_generations.AddOrUpdate((binding.Name, Slot(binding, index)), 1, static (_, v) => v + 1);

	private async ValueTask SendSetAsync(ActiveSession session, ISettingBinding binding, int index, byte[] payload, CancellationToken ct)
	{
		foreach (var sibling in Siblings(binding, index))
		{
			Bump(sibling, index);
		}

		Bump(binding, index);
		try
		{
			await session.Pipeline.ExecuteAsync(
				binding.Vendor, binding.SetCmd!.Value, payload, PipelineExtensions.OptionsFor(binding), ct).ConfigureAwait(false);
		}
		catch (GaiaProtocolException ex) when (binding.MapError(ex.Reason, index) is { } mapped)
		{
			throw mapped;
		}
	}

	/// <summary>One GET; every key sharing that GET word is decoded from the reply and stored unless a newer write happened meanwhile.</summary>
	private async ValueTask<GaiaFrame> FetchAsync(ActiveSession session, ISettingBinding binding, int index, CancellationToken ct)
	{
		var siblings = Siblings(binding, index);
		var before = siblings.Select(s => Generation(s, index)).ToArray();
		GaiaFrame frame;
		try
		{
			frame = await session.Pipeline.GetFrameAsync(binding, index, ct).ConfigureAwait(false);
		}
		catch (GaiaProtocolException ex) when (binding.MapError(ex.Reason, index) is { } mapped)
		{
			throw mapped;
		}

		for (var i = 0; i < siblings.Count; i++)
		{
			var sibling = siblings[i];
			if (Generation(sibling, index) != before[i])
			{
				log?.Invoke($"Stale refresh of {sibling.Name} ignored.");
				continue;
			}

			if (sibling.TryDecodeResponse(frame.Payload.Span, index, out var value) && value is not null)
			{
				store.Apply(sibling.Name, Slot(sibling, index), value);
			}
			else
			{
				log?.Invoke($"{sibling.Name}: reply did not decode ({GaiaFormatter.Hex(frame.Payload.Span)}).");
			}
		}

		return frame;
	}
}
