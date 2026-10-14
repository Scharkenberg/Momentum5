// Momentum5.Core/IRawProtocolProbe.cs
namespace Momentum5.Core.Protocol;

public interface IRawProtocolProbe
{
	event EventHandler<string>? LogLine;

	byte[] BuildReadRequest(Guid serviceUuid, Guid characteristicUuid);
	byte[] BuildWriteRequest(Guid serviceUuid, Guid characteristicUuid, byte[] payload);
	void Observe(string source, byte[] payload);
}