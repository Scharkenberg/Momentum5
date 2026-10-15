using Momentum5.Core.Settings;

namespace Momentum5.Client;

/// <summary>Atomically swapped immutable <see cref="HeadsetSnapshot"/>; <see cref="Changed"/> fires after the swap.</summary>
public sealed class HeadsetStateStore : IHeadsetState
{
	private HeadsetSnapshot _current = HeadsetSnapshot.Empty;

	public HeadsetSnapshot Current => Volatile.Read(ref _current);

	public event EventHandler<HeadsetSnapshot>? Changed;

	public void Apply<T>(SettingKey<T> key, T value)
	{
		ArgumentNullException.ThrowIfNull(key);
		Apply(key.Name, key.Index, value);
	}

	/// <summary>Sets (or, for null, removes) one value.</summary>
	public void Apply(string name, int index, object? value) => Update(s => s.WithValue(name, index, value));

	public void Remove(string name, int index) => Apply(name, index, null);

	public void SetSession(SessionInfo? session) => Update(s => s.WithSession(session));

	/// <summary>Disconnect: forget all values, keep the last session marker.</summary>
	public void Reset() => Update(s => HeadsetSnapshot.Empty.WithSession(s.Session));

	/// <summary>Mirrors pushes from <paramref name="hub"/> into the store until the returned handle is disposed.</summary>
	public IDisposable Attach(NotificationHub hub)
	{
		ArgumentNullException.ThrowIfNull(hub);
		return new Subscription(this, hub);
	}

	private void Update(Func<HeadsetSnapshot, HeadsetSnapshot> change)
	{
		HeadsetSnapshot next;
		while (true)
		{
			var seen = Volatile.Read(ref _current);
			next = change(seen);
			if (ReferenceEquals(Interlocked.CompareExchange(ref _current, next, seen), seen))
			{
				break;
			}
		}

		Changed?.Invoke(this, next);
	}

	private sealed class Subscription : IDisposable
	{
		private readonly HeadsetStateStore _store;
		private readonly NotificationHub _hub;

		public Subscription(HeadsetStateStore store, NotificationHub hub)
		{
			_store = store;
			_hub = hub;
			_hub.SettingChanged += OnChanged;
		}

		public void Dispose() => _hub.SettingChanged -= OnChanged;

		private void OnChanged(object? sender, Momentum5.Protocol.SettingNotification n) => _store.Apply(n.Name, n.Index, n.Value);
	}
}
