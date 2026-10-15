using Momentum5.Core.Settings;

namespace Momentum5.Protocol.Bindings;

public static class BindingDefaults
{
	public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
}

/// <summary>Non-generic view of a <see cref="SettingBinding{T}"/> for registries, pipeline and the fake device.</summary>
public interface ISettingBinding
{
	string Name { get; }

	Type ValueType { get; }

	GaiaVendor Vendor { get; }

	SettingGroup Group { get; }

	/// <summary>Safety of the SET. GET and push are always Read.</summary>
	SafetyClass Safety { get; }

	ushort? GetCmd { get; }

	ushort? SetCmd { get; }

	ushort? PushCmd { get; }

	bool Indexed { get; }

	TimeSpan Timeout { get; }

	TimeSpan BusyGrace { get; }

	TrustTier MinTrust { get; }

	/// <summary>After a write, re-read the whole group (ANC and transparency are coupled).</summary>
	bool RefreshTogether { get; }

	IReadOnlyList<object> SampleValues { get; }

	// ---- host side ----
	byte[] BuildGetPayload(int index);

	bool TryDecodeResponse(ReadOnlySpan<byte> payload, int index, out object? value);

	bool TryEncodeSet(object? value, int index, out byte[] payload, out string? failure);

	Exception? MapError(byte reason, int index);

	// ---- device side (FakeM5) ----
	bool TryParseGet(ReadOnlySpan<byte> payload, out int index);

	bool TryDecodeSet(ReadOnlySpan<byte> payload, out int index, out object? value);

	bool TryEncodeResponse(object? value, int index, out byte[] payload);
}

public sealed record SettingBinding<T> : ISettingBinding
{
	public required string Name { get; init; }

	public required GaiaVendor Vendor { get; init; }

	public required SettingGroup Group { get; init; }

	public required SafetyClass Safety { get; init; }

	/// <summary>GET response / push format.</summary>
	public required IPayloadFormat<T> Format { get; init; }

	/// <summary>SET payload format; defaults to <see cref="Format"/>.</summary>
	public IPayloadFormat<T>? SetFormat { get; init; }

	public ushort? GetCmd { get; init; }

	public ushort? SetCmd { get; init; }

	public ushort? PushCmd { get; init; }

	/// <summary>Fixed GET request bytes (before the index byte, if indexed).</summary>
	public ReadOnlyMemory<byte> GetSelector { get; init; }

	/// <summary>GET appends the index byte; SET is prefixed with it.</summary>
	public bool Indexed { get; init; }

	public TimeSpan Timeout { get; init; } = BindingDefaults.Timeout;

	public TimeSpan BusyGrace { get; init; }

	public TrustTier MinTrust { get; init; } = TrustTier.Compatible;

	public bool RefreshTogether { get; init; }

	public Func<byte, int, Exception?>? ErrorMapper { get; init; }

	public IReadOnlyList<T> Samples { get; init; } = [];

	public Type ValueType => typeof(T);

	public IReadOnlyList<object> SampleValues => [.. Samples.Select(s => (object)s!)];

	public byte[] BuildGetPayload(int index) =>
		Indexed ? [.. GetSelector.Span, (byte)index] : GetSelector.ToArray();

	public bool TryDecodeResponse(ReadOnlySpan<byte> payload, int index, out object? value)
	{
		if (Format.TryDecode(payload, index, out var decoded))
		{
			value = decoded;
			return true;
		}

		value = null;
		return false;
	}

	public bool TryEncodeSet(object? value, int index, out byte[] payload, out string? failure)
	{
		payload = [];
		if (SetCmd is null)
		{
			failure = $"{Name} is read-only.";
			return false;
		}

		if (value is not T typed)
		{
			failure = $"{Name} expects {typeof(T).Name}.";
			return false;
		}

		if (Indexed && index is < 0 or > 255)
		{
			failure = $"{Name} index {index} out of range.";
			return false;
		}

		if (!(SetFormat ?? Format).TryEncode(typed, index, out var body, out failure))
		{
			return false;
		}

		payload = Indexed ? [(byte)index, .. body] : body;
		failure = null;
		return true;
	}

	public Exception? MapError(byte reason, int index) => ErrorMapper?.Invoke(reason, index);

	public bool TryParseGet(ReadOnlySpan<byte> payload, out int index)
	{
		var selector = GetSelector.Span;
		var need = selector.Length + (Indexed ? 1 : 0);
		index = -1;
		if (payload.Length != need || !payload[..selector.Length].SequenceEqual(selector))
		{
			return false;
		}

		if (Indexed)
		{
			index = payload[^1];
		}

		return true;
	}

	public bool TryDecodeSet(ReadOnlySpan<byte> payload, out int index, out object? value)
	{
		index = -1;
		value = null;
		if (SetCmd is null)
		{
			return false;
		}

		var body = payload;
		if (Indexed)
		{
			if (payload.Length < 1)
			{
				return false;
			}

			index = payload[0];
			body = payload[1..];
		}

		if ((SetFormat ?? Format).TryDecode(body, index, out var decoded))
		{
			value = decoded;
			return true;
		}

		return false;
	}

	public bool TryEncodeResponse(object? value, int index, out byte[] payload)
	{
		payload = [];
		return value is T typed && Format.TryEncode(typed, index, out payload, out _);
	}
}

/// <summary>Concise binding factory used by the *Bindings files.</summary>
internal static class Bind
{
	/// <summary>GET selector <c>00</c> used by AutoOff and EqAllGains.</summary>
	public static readonly byte[] ZeroSelector = [0x00];

	public static SettingBinding<T> Create<T>(
		SettingKey<T> key,
		SettingGroup group,
		ushort? get,
		ushort? set,
		IPayloadFormat<T> format,
		IPayloadFormat<T>? setFormat = null,
		ushort? push = null,
		GaiaVendor vendor = GaiaVendor.Sennheiser,
		SafetyClass? safety = null,
		IReadOnlyList<T>? samples = null,
		TimeSpan? timeout = null,
		TimeSpan? busyGrace = null,
		bool refreshTogether = false,
		bool indexed = false,
		ReadOnlyMemory<byte> selector = default,
		Func<byte, int, Exception?>? errorMapper = null)
	{
		var effective = safety ?? (set is null ? SafetyClass.Read : SafetyClass.Write);
		return new SettingBinding<T>
		{
			Name = key.Name,
			Vendor = vendor,
			Group = group,
			Safety = effective,
			Format = format,
			SetFormat = setFormat,
			GetCmd = get,
			SetCmd = set,
			PushCmd = push,
			Indexed = indexed,
			GetSelector = selector,
			Timeout = timeout ?? BindingDefaults.Timeout,
			BusyGrace = busyGrace ?? TimeSpan.Zero,
			MinTrust = effective == SafetyClass.Read ? TrustTier.Unknown : TrustTier.Compatible,
			RefreshTogether = refreshTogether,
			ErrorMapper = errorMapper,
			Samples = samples ?? [],
		};
	}

	public static SettingBinding<T> Create<T>(
		SettingKey<T> key,
		SettingGroup group,
		ushort? get,
		ushort? set,
		ICodec<T> codec,
		ushort? push = null,
		GaiaVendor vendor = GaiaVendor.Sennheiser,
		SafetyClass? safety = null,
		IReadOnlyList<T>? samples = null,
		TimeSpan? timeout = null,
		TimeSpan? busyGrace = null,
		bool refreshTogether = false) =>
		Create(
			key, group, get, set, PayloadFormat.Of(codec), null, push, vendor, safety, samples, timeout, busyGrace, refreshTogether);
}

public sealed class BindingRegistry
{
	private static readonly ISettingBinding[] None = [];

	private readonly Dictionary<string, ISettingBinding> _byName = new(StringComparer.Ordinal);
	private readonly Dictionary<(GaiaVendor, ushort), List<ISettingBinding>> _byCommand = [];
	private readonly List<ISettingBinding> _all = [];

	public IReadOnlyList<ISettingBinding> All => _all;

	public void Register(ISettingBinding binding)
	{
		ArgumentNullException.ThrowIfNull(binding);
		if (!_byName.TryAdd(binding.Name, binding))
		{
			throw new ArgumentException($"Duplicate binding '{binding.Name}'.", nameof(binding));
		}

		_all.Add(binding);
		foreach (var word in new[] { binding.GetCmd, binding.SetCmd, binding.PushCmd })
		{
			if (word is not { } w)
			{
				continue;
			}

			var key = (binding.Vendor, GaiaWord.AsCommand(w));
			if (!_byCommand.TryGetValue(key, out var list))
			{
				_byCommand[key] = list = [];
			}

			if (!list.Exists(x => ReferenceEquals(x, binding)))
			{
				list.Add(binding);
			}
		}
	}

	public ISettingBinding? ByName(string name) => _byName.GetValueOrDefault(name);

	public SettingBinding<T>? Find<T>(SettingKey<T> key) => ByName(key.Name) as SettingBinding<T>;

	/// <summary>Bindings that use this command as GET, SET or push word (type bits ignored).</summary>
	public IReadOnlyList<ISettingBinding> ByCommand(GaiaVendor vendor, ushort command) =>
		_byCommand.TryGetValue((vendor, GaiaWord.AsCommand(command)), out var list) ? list : None;
}
