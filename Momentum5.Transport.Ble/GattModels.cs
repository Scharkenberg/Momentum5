using System.Text.Json.Serialization;

namespace Momentum5.Transport.Ble;

public enum GattStepStatus
{
	Pass,
	Warn,
	Fail,
	Skip,
	Info,
}

public sealed record GattStep(string Name, GattStepStatus Status, string Detail, long Ms);

/// <summary>Either a Bluetooth address or a Windows device id (used when the address is unknown). Address wins when both are present.</summary>
public sealed record GattTarget(ulong? Address, string? DeviceId, string? Label = null)
{
	public override string ToString() => Address is { } a and not 0 ? BleAddress.Format(a) : DeviceId ?? "(none)";
}

public sealed record GattNotification(Guid Service, Guid Characteristic, byte[] Value, DateTimeOffset At);

public sealed class GattCharInfo
{
	public Guid Service { get; set; }

	public Guid Uuid { get; set; }

	public string Name { get; set; } = string.Empty;

	public string Properties { get; set; } = string.Empty;

	public bool CanRead { get; set; }

	public bool CanWrite { get; set; }

	public bool CanNotify { get; set; }

	public List<string> Descriptors { get; set; } = [];

	public string? ValueHex { get; set; }

	public string? ValueText { get; set; }

	public string? Error { get; set; }
}

public sealed class GattServiceInfo
{
	public Guid Uuid { get; set; }

	public string Name { get; set; } = string.Empty;

	public string? Error { get; set; }

	public List<GattCharInfo> Characteristics { get; set; } = [];
}

public sealed class GattNotificationRecord
{
	public Guid Service { get; set; }

	public Guid Characteristic { get; set; }

	public string Hex { get; set; } = string.Empty;

	public DateTimeOffset At { get; set; }
}

public sealed class GattReport
{
	public string Target { get; set; } = string.Empty;

	public DateTimeOffset StartedAt { get; set; }

	public long DurationMs { get; set; }

	public List<GattStep> Steps { get; set; } = [];

	public List<GattServiceInfo> Services { get; set; } = [];

	public List<GattNotificationRecord> Notifications { get; set; } = [];

	[JsonIgnore]
	public bool HasFailures => Steps.Any(s => s.Status == GattStepStatus.Fail);

	public string Summary =>
		$"{Steps.Count(s => s.Status == GattStepStatus.Pass)} pass, {Steps.Count(s => s.Status == GattStepStatus.Warn)} warn, {Steps.Count(s => s.Status == GattStepStatus.Fail)} fail, {Steps.Count(s => s.Status == GattStepStatus.Skip)} skipped";
}

public sealed record GattDiagOptions
{
	/// <summary>Disconnect/reconnect rounds for the stability check; 0 skips it.</summary>
	public int Cycles { get; init; } = 5;

	/// <summary>How long to listen after subscribing to every notify/indicate characteristic; zero skips it.</summary>
	public TimeSpan Listen { get; init; } = TimeSpan.FromSeconds(5);

	/// <summary>Sends the read-class GAIA "get API version" frame if a GAIA GATT service exists (the only write the lab ever does).</summary>
	public bool GaiaProbe { get; init; }

	public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

public sealed record BleCandidate(
	ulong Address,
	string? DeviceId,
	string? Name,
	short? Rssi,
	int Seen,
	bool IsPaired,
	bool IsConnected,
	IReadOnlyList<string> ManufacturerData,
	IReadOnlyList<string> ServiceUuids,
	ulong? SwiftPairClassicAddress,
	bool IsLikelyHeadset,
	string Reason,
	string AdvertisementTypes = "")
{
	public GattTarget ToTarget() => new(Address != 0 ? Address : null, DeviceId, Name);
}

/// <summary>Reports synchronously on the calling thread (unlike <see cref="Progress{T}"/>, no thread-pool reordering).</summary>
public sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
	public void Report(T value) => handler(value);
}
