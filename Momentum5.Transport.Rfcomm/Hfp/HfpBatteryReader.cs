using System.Globalization;
using Momentum5.Core.Transport;
using Windows.Devices.Enumeration.Pnp;

namespace Momentum5.Transport.Rfcomm.Hfp;

/// <summary>
/// Battery percent Windows keeps on the headset's Hands-Free devnode (<c>{104EA319-6E3C-4A49-AB25-2E9B1A11E929},2</c>).
/// Read through <see cref="PnpObject"/> (no native code); null when the devnode or property is absent.
/// </summary>
public sealed class HfpBatteryReader : IBatteryFallback
{
	public const string BatteryProperty = "{104EA319-6E3C-4A49-AB25-2E9B1A11E929} 2";

	public async ValueTask<int?> TryReadPercentAsync(ulong address, CancellationToken ct)
	{
		var mac = address.ToString("X12", CultureInfo.InvariantCulture);
		var nodes = await PnpObject.FindAllAsync(PnpObjectType.Device, [BatteryProperty]).AsTask(ct).ConfigureAwait(false);
		foreach (var node in nodes)
		{
			if (!node.Id.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)
				|| !node.Id.Contains(mac, StringComparison.OrdinalIgnoreCase)
				|| !node.Properties.TryGetValue(BatteryProperty, out var raw) || raw is null)
			{
				continue;
			}

			try
			{
				var percent = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
				if (percent is >= 0 and <= 100)
				{
					return percent;
				}
			}
			catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
			{
				// not a number: try the next devnode
			}
		}

		return null;
	}
}
