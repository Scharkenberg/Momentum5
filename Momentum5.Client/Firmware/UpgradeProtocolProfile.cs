using Momentum5.Protocol;

namespace Momentum5.Client;

/// <summary>
/// VM-upgrade message codes of the Qualcomm VMU family (docs/PROTOCOL-upgrade.md §3), as carried inside
/// VMU packets on the upgrade tunnel. Defaults are the documented v2 values from the Qualcomm GAIAControl
/// <c>vmupgradelibrary</c>; the M5's actual values are [CAP-R06] — R-06 pins them by editing this record,
/// never the state machine.
/// </summary>
public sealed record UpgradeVmuOpcodes
{
	/// <summary>Host→device: synchronize before anything else; data = the MD5-derived upgrade identifier.</summary>
	public byte SyncReq { get; init; } = 0x13;

	/// <summary>Device→host: carries the resume point, the in-progress identifier and the protocol version.</summary>
	public byte SyncCfm { get; init; } = 0x14;

	/// <summary>Host→device: request the upgrade procedure to start.</summary>
	public byte StartReq { get; init; } = 0x01;

	/// <summary>Device→host: readiness status plus the device battery level.</summary>
	public byte StartCfm { get; init; } = 0x02;

	/// <summary>Host→device: start the data transfer phase.</summary>
	public byte StartDataReq { get; init; } = 0x15;

	/// <summary>Device→host: pulls the next transfer window (byte count + file offset).</summary>
	public byte DataBytesReq { get; init; } = 0x03;

	/// <summary>Host→device: one chunk of image bytes, first payload byte = last-packet flag.</summary>
	public byte Data { get; init; } = 0x04;

	/// <summary>Host→device: ask whether the device finished validating the received image.</summary>
	public byte ValidationDoneReq { get; init; } = 0x16;

	/// <summary>Device→host: validation still running; carries a waiting time in ms.</summary>
	public byte ValidationDoneCfm { get; init; } = 0x17;

	/// <summary>Device→host: the image was received and validated.</summary>
	public byte TransferCompleteInd { get; init; } = 0x0B;

	/// <summary>Host→device: continue (or abort) past the transfer-complete point.</summary>
	public byte TransferCompleteRes { get; init; } = 0x0C;

	/// <summary>Host→device: continue (or abort) the in-progress phase after the validation reboot.</summary>
	public byte InProgressRes { get; init; } = 0x0E;

	/// <summary>Device→host: the device asks for permission to commit the upgrade.</summary>
	public byte CommitReq { get; init; } = 0x0F;

	/// <summary>Host→device: confirm (or abort) the commit.</summary>
	public byte CommitCfm { get; init; } = 0x10;

	/// <summary>Device→host: the upgrade completed — the last message of a successful session.</summary>
	public byte CompleteInd { get; init; } = 0x12;

	/// <summary>Host→device: abort the upgrade procedure.</summary>
	public byte AbortReq { get; init; } = 0x07;

	/// <summary>Device→host: confirms the abort.</summary>
	public byte AbortCfm { get; init; } = 0x08;

	/// <summary>Device→host: an error or warning; the host must echo it with <see cref="ErrorWarnRes"/>.</summary>
	public byte ErrorWarnInd { get; init; } = 0x11;

	/// <summary>Host→device: acknowledgment of <see cref="ErrorWarnInd"/> — echoes the received return code.</summary>
	public byte ErrorWarnRes { get; init; } = 0x1F;
}

/// <summary>What <see cref="UpgradeSession.ResumeAsync"/> does when the device lost its transfer progress
/// (resume point 0 with a zero window offset while bytes were previously sent — docs/PROTOCOL-upgrade.md §5).</summary>
public enum UpgradeResumePolicy
{
	/// <summary>Follow the device: re-transfer the whole image (idempotent — same MD5 sync identifier). Reference behavior.</summary>
	FollowDevice,

	/// <summary>Refuse the restart with a typed <see cref="UpgradeSessionFailure.RestartRefused"/> failure.</summary>
	FailOnRestart,
}

/// <summary>
/// Every parameter of the VM-upgrade session that a real M5 OTA capture can pin (docs/PROTOCOL-upgrade.md §8 —
/// bucket X-11). The R-06 capture (BLOCKED-HW) edits this record, not the state machine: defaults are the
/// documented Qualcomm GAIAControl VM-upgrade values plus the research dossier's GAIA v3 tunnel words, and every
/// default is provisional. Words are full 16-bit GAIA words; a <c>null</c> word means "not observed — never send"
/// (ref/04 rule 3), and <see cref="UpgradeSession"/> fails with
/// <see cref="UpgradeSessionFailure.UnobservedCommand"/> rather than inventing bytes.
/// </summary>
public sealed record UpgradeProtocolProfile
{
	/// <summary>Vendor namespace of the upgrade tunnel: Qualcomm 0x001D [REF].</summary>
	public GaiaVendor UpgradeVendor { get; init; } = GaiaVendor.Qualcomm;

	/// <summary>Tunnel connect word (GAIA v3 0x0C00, the v2 VM_UPGRADE_CONNECT) [REF word, CAP-R06 on-M5 use].</summary>
	public ushort? TunnelConnectWord { get; init; } = 0x0C00;

	/// <summary>Tunnel disconnect word (0x0C01, v2 VM_UPGRADE_DISCONNECT) [REF word, CAP-R06].</summary>
	public ushort? TunnelDisconnectWord { get; init; } = 0x0C01;

	/// <summary>Tunnel control word (0x0C02, v2 VM_UPGRADE_CONTROL) — the VMU packet carrier [REF word, CAP-R06].</summary>
	public ushort? TunnelControlWord { get; init; } = 0x0C02;

	/// <summary>Tunnel data word (0x0C03, v2 VM_UPGRADE_DATA). The Qualcomm sample sends every VMU packet, data
	/// included, on the control word; the M5 app may use this word for UPGRADE_DATA instead [CAP-R06].</summary>
	public ushort? TunnelDataWord { get; init; } = 0x0C03;

	/// <summary>Send UPGRADE_DATA packets on <see cref="TunnelDataWord"/> instead of the control word (default false,
	/// reference behavior) [CAP-R06].</summary>
	public bool DataPacketsOnDataWord { get; init; }

	/// <summary>Push word the device uses to deliver its VMU packets (0x0C80) [REF word]; null = do not listen for pushes
	/// (replies must then ride the control ACK payload or the session times out).</summary>
	public ushort? TunnelNotificationWord { get; init; } = 0x0C80;

	/// <summary>The 0x0C80 payload starts with an event/feature byte before the VMU packet, like every indexed
	/// Sennheiser push in this repo and the v2 EVENT_NOTIFICATION layout [INFER, CAP-R06].</summary>
	public bool NotificationCarriesEventByte { get; init; } = true;

	/// <summary>Feature id to register (RegisterNotification, upgrade vendor) so the tunnel push flows; null = send no
	/// registration. Likely 0x06 (the 0x0Cxx family) [INFER, CAP-R06].</summary>
	public byte? TunnelNotificationFeature { get; init; }

	/// <summary>VMU replies ride the ACK payload of the control word instead of the push channel (default false — the
	/// reference delivers device VMU packets as notifications) [INFER, CAP-R06].</summary>
	public bool RepliesInControlAck { get; init; }

	/// <summary>Vendor of the firmware version re-read word (Sennheiser 0x0495) [REF m4.json].</summary>
	public GaiaVendor VersionReadVendor { get; init; } = GaiaVendor.Sennheiser;

	/// <summary>Firmware version re-read word (Service_SystemReleaseVersion 0x1201, response = 3 × u16BE) [REF m4.json];
	/// null = skip the Verify phase (recorded as a transition note).</summary>
	public ushort? VersionReadWord { get; init; } = 0x1201;

	/// <summary>All VMU message codes — the "control payload bytes" of the capture. Defaults: v2 reference values [CAP-R06].</summary>
	public UpgradeVmuOpcodes Opcodes { get; init; } = new();

	/// <summary>Action byte meaning "continue" in TRANSFER_COMPLETE_RES / IN_PROGRESS_RES / COMMIT_CFM [REF].</summary>
	public byte ContinueAction { get; init; }

	/// <summary>Action byte meaning "abort" in the same messages [REF].</summary>
	public byte AbortAction { get; init; } = 0x01;

	/// <summary>Bytes of MD5(image) carried as the upgrade identifier in UPGRADE_SYNC_REQ — the reference sends the
	/// last 4 bytes [REF, CAP-R06].</summary>
	public int SyncIdByteCount { get; init; } = 4;

	/// <summary>Maximum image bytes per UPGRADE_DATA packet. 250 = the v2 BR/EDR cap (GAIA max payload 254 minus the
	/// VMU header and the last-packet flag) [REF, CAP-R06 for the M5's actual chunk size].</summary>
	public int MaxChunkFileBytes { get; init; } = 250;

	/// <summary>Per-frame timeout for tunnel commands (the version read needs ~10 s on M5) [INFER].</summary>
	public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);

	/// <summary>How long to wait for a device VMU reply (push or ACK-carried) before failing with ReplyTimeout [INFER].</summary>
	public TimeSpan VmuReplyTimeout { get; init; } = TimeSpan.FromSeconds(10);

	/// <summary>How long to wait for UPGRADE_COMPLETE_IND after confirming the commit (flash write time) [INFER].</summary>
	public TimeSpan CommitCompleteTimeout { get; init; } = TimeSpan.FromSeconds(60);

	/// <summary>Upper bound honored for the device-provided IS_VALIDATION_DONE_CFM waiting time [INFER].</summary>
	public TimeSpan MaxValidationWait { get; init; } = TimeSpan.FromSeconds(60);

	/// <summary>Inter-chunk pacing between UPGRADE_DATA frames; zero = none (the reference relies on device-pulled flow
	/// control) [CAP-R06].</summary>
	public TimeSpan InterChunkDelay { get; init; }

	/// <summary>UPGRADE_START_CFM "not ready" retries before failing with DeviceNotReady [REF: 5 attempts].</summary>
	public int StartNotReadyRetries { get; init; } = 5;

	/// <summary>Delay between "not ready" retries [REF: 2 s].</summary>
	public TimeSpan StartNotReadyRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

	/// <summary>Poll interval while waiting for the supervisor to re-establish the link after a device reboot [INFER].</summary>
	public TimeSpan ReconnectPollInterval { get; init; } = TimeSpan.FromSeconds(1);

	/// <summary>Total budget for the post-reboot reconnect before failing with ReconnectFailed [INFER].</summary>
	public TimeSpan ReconnectTimeout { get; init; } = TimeSpan.FromMinutes(2);

	/// <summary>Host-side battery floor: the session starts only above this percentage (bucket rule: battery > 30 %).</summary>
	public int BatteryFloorPercent { get; init; } = 30;

	/// <summary>What to do when the device lost its transfer progress (docs/PROTOCOL-upgrade.md §5).</summary>
	public UpgradeResumePolicy ResumePolicy { get; init; } = UpgradeResumePolicy.FollowDevice;

	/// <summary>Validates the tunables that must be usable at run time (misuse fails fast, like UpdateCheckOptions.Timeout).</summary>
	public void Validate()
	{
#pragma warning disable CA2208 // property names are the meaningful argument for a record validator
		if (MaxChunkFileBytes is < 1 or > ushort.MaxValue - 4)
		{
			throw new ArgumentException("MaxChunkFileBytes must be 1..65531 (a VMU packet with header and last-packet flag must fit a GAIA payload).", nameof(MaxChunkFileBytes));
		}

		if (SyncIdByteCount is < 1 or > 16)
		{
			throw new ArgumentException("SyncIdByteCount must be 1..16 (a slice of the 16-byte MD5 digest).", nameof(SyncIdByteCount));
		}

		if (CommandTimeout <= TimeSpan.Zero || VmuReplyTimeout <= TimeSpan.Zero || CommitCompleteTimeout <= TimeSpan.Zero
			|| MaxValidationWait <= TimeSpan.Zero || StartNotReadyRetryDelay <= TimeSpan.Zero
			|| ReconnectPollInterval <= TimeSpan.Zero || ReconnectTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentException("All upgrade profile timeouts must be positive.", nameof(CommandTimeout));
		}

		if (InterChunkDelay < TimeSpan.Zero)
		{
			throw new ArgumentException("InterChunkDelay must not be negative.", nameof(InterChunkDelay));
		}

		if (StartNotReadyRetries < 1)
		{
			throw new ArgumentException("StartNotReadyRetries must be at least 1.", nameof(StartNotReadyRetries));
		}

		if (BatteryFloorPercent is < 0 or > 100)
		{
			throw new ArgumentException("BatteryFloorPercent must be 0..100.", nameof(BatteryFloorPercent));
		}
#pragma warning restore CA2208
	}
}
