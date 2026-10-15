using Momentum5.Core.Settings;

namespace Momentum5.Protocol.Bindings;

/// <summary>
/// X-05 MMI (touch/button) mapping — READ-ONLY raw bindings. The official M4 device model (m4.json, research
/// lane) documents GET 1601 "sel 2 B" as [button number][pattern] → reply [button][pattern][function], SET 1600
/// as the same 3 bytes, and GET 1605 as a BOOL "is default config" reply — none of that is verified on M5
/// (ERRATA R-03: the payload format was not found statically in the M5 app; R-04 capture pending). Replies are
/// therefore surfaced as RAW bytes and there is deliberately NO SET binding for 1600: the editor unlocks only
/// after the R-04 capture decodes the format. 1604 (reset) stays a Blocked raw row and 1600 a Gated raw row in
/// <see cref="RawCommands"/> (GAIA console only).
/// </summary>
public static class MmiBindings
{
	/// <summary>
	/// Bounded probe range 0..3. The indexed binding sends GET 1601 as [00][slot] — per the M4 model that is
	/// button/zone 0 with pattern 0x00..0x03. A slot whose GET answers a GAIA error is shown as "not present";
	/// the range is widened once the R-04 capture reveals the real slot space.
	/// </summary>
	public const int SlotCount = 4;

	// First GET 1601 selector byte: button/zone 0 (m4.json "button number"; 0x00 = right side on the M4).
	private static readonly byte[] ButtonZero = [0x00];

	public static IReadOnlyList<ISettingBinding> All { get; } = Build();

	private static List<ISettingBinding> Build() =>
	[
		// GET 1601 "sel 2 B": the binding appends the slot byte after the fixed button byte. RawBytes (wrapped
		// as a format so the selector/indexed parameters exist — the PairedStatus pattern): the reply is
		// surfaced verbatim — no decode until R-04. GET-only (no SET): read-only by construction.
		Bind.Create(
			Keys.MmiConfig(0), SettingGroup.Device, 0x1601, null, PayloadFormat.Of(Codecs.RawBytes),
			selector: ButtonZero, indexed: true,
			samples: [Convert.FromHexString("000101"), Convert.FromHexString("000201")]),

		// GET 1605 (no selector): the M4 model documents a BOOL reply, not a full dump — raw here too
		// (the ImmersiveQuery shape).
		Bind.Create(Keys.MmiIsDefault, SettingGroup.Device, 0x1605, null, Codecs.RawBytes, samples: [Convert.FromHexString("01")]),
	];
}
