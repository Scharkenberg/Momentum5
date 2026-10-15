namespace Momentum5.Core.Settings;

public interface ISettingService
{
	ValueTask<T> GetAsync<T>(SettingKey<T> key, CancellationToken ct);

	/// <summary>gate → set → ack → re-get → compare.</summary>
	ValueTask<SetResult<T>> SetAsync<T>(SettingKey<T> key, T value, CancellationToken ct);

	ValueTask RefreshAsync(SettingGroup group, CancellationToken ct);
}

public interface IHeadsetState
{
	HeadsetSnapshot Current { get; }

	event EventHandler<HeadsetSnapshot>? Changed;
}
