namespace Momentum5.Core.Settings;

/// <summary>Immutable copy-on-write view of known headset settings.</summary>
public sealed class HeadsetSnapshot
{
	public static HeadsetSnapshot Empty { get; } = new(new Dictionary<(string, int), object>(), null);

	private readonly Dictionary<(string Name, int Index), object> _values;

	private HeadsetSnapshot(Dictionary<(string, int), object> values, SessionInfo? session)
	{
		_values = values;
		Session = session;
	}

	public SessionInfo? Session { get; }

	public int Count => _values.Count;

	public T? Get<T>(SettingKey<T> key) =>
		_values.TryGetValue((key.Name, key.Index), out var v) && v is T t ? t : default;

	public bool TryGet<T>(SettingKey<T> key, out T value)
	{
		if (_values.TryGetValue((key.Name, key.Index), out var v) && v is T t)
		{
			value = t;
			return true;
		}
		value = default!;
		return false;
	}

	public HeadsetSnapshot With<T>(SettingKey<T> key, T value)
	{
		var copy = new Dictionary<(string, int), object>(_values);
		if (value is null)
		{
			copy.Remove((key.Name, key.Index));
		}
		else
		{
			copy[(key.Name, key.Index)] = value;
		}
		return new HeadsetSnapshot(copy, Session);
	}

	public HeadsetSnapshot WithSession(SessionInfo? session) => new(new Dictionary<(string, int), object>(_values), session);
}
