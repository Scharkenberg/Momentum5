using System.Collections;
using Momentum5.Core.Settings;
using Momentum5.Protocol;
using Momentum5.Protocol.Bindings;

namespace Momentum5.Tests;

internal static class Support
{
	public static bool Same(object? a, object? b) =>
		a is IEnumerable ea && a is not string && b is IEnumerable eb
			? ea.Cast<object>().SequenceEqual(eb.Cast<object>())
			: Equals(a, b);

	public static SettingBinding<T> Of<T>(SettingKey<T> key) =>
		GaiaCatalog.Registry.Find(key) ?? throw new InvalidOperationException($"No binding for {key.Name}");

	public static T Decode<T>(SettingKey<T> key, string hex)
	{
		Assert.True(Of(key).Format.TryDecode(Hex.Parse(hex), key.Index, out var value), $"decode failed: {key.Name} <- {hex}");
		return value;
	}

	public static byte[] Set<T>(SettingKey<T> key, T value)
	{
		Assert.True(Of(key).TryEncodeSet(value, key.Index, out var payload, out var error), $"encode failed: {key.Name}: {error}");
		return payload;
	}

	public static bool TrySet<T>(SettingKey<T> key, T value) => Of(key).TryEncodeSet(value, key.Index, out _, out _);
}
