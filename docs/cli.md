# m5ctl

`m5ctl [--simulate] [--json] [--address AA:BB:CC:DD:EE:FF] [--name MOMENTUM] <command>`

Global options apply to every command. `--simulate` uses the built-in fake M5 (no hardware). Headset names are user-editable, so
selection is: `--address`, else name contains `--name`, else the only connected paired device.

| Command | What it does |
|---|---|
| `list` | paired Bluetooth devices |
| `status` | connect, refresh everything, print the snapshot |
| `watch` | live push notifications until Ctrl+C |
| `get <key> [index]` | read one setting |
| `set <key> [index] <value>` | write, then verify by reading back |
| `eq get` / `eq band <i> <dB>` / `eq set a,b,…` | equalizer (batch write rolls back on failure) |
| `keys` | every setting: type, access (`rw`/`ro`/`wo`/`push`), safety, enum values |
| `matrix [--out file]` | verification matrix (markdown) |
| `probe` | G1 hardware spike |

Aliases: `anc` (on/off), `anc adaptive on|off`, `anc antiwind off|max|auto`, `transparency`, `bass`, `immersive`, `headtracking`,
`touch`, `wear`, `battery`, `name`, `sidetone`, `prompt`, `soundmode`, `autooff`. Any name from `keys` works (case-insensitive;
indexed ones as `eqband 3` or `eqband[3]`). Negative numbers need `--`: `m5ctl eq band 3 -- -6`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success (a verify mismatch still exits 0 and prints a note; `--json` has `"verified": false`) |
| 1 | connection or device error, connect timeout (10 s), unsupported device |
| 2 | usage error or invalid value |
| 3 | refused by safety policy (Gated/Dangerous/Blocked keys, or read-only firmware) |

## JSON shapes

`status --json` (schema 1, keys sorted, indexed keys as `Name[i]`):

```json
{
  "schema": 1,
  "session": { "model": "M5AEBT Black", "firmware": "6.21.5", "serial": "…", "trust": "Verified", "transport": "rfcomm-gaia", "features": ["S:09 v1"] },
  "values": { "AncEnabled": true, "BatteryPercent": 70, "EqBand[0]": 0.0 }
}
```

`get`/`set --json`: `{"key":"AncEnabled","value":true,"verified":true}` (`index` present for indexed keys, `verified` only for `set`).
`watch --json`: one object per line: `{"event":"changed","key":…,"index":-1,"value":…}` or `{"event":"unknown","vendor":…,"command":"XXXX","payload":"HEX"}`.
`list --json`: `{"schema":1,"devices":[{"name":…,"address":…,"connected":…}]}`. `keys --json`: `{"schema":1,"keys":[{name,type,access,safety,indexed,group,values?}]}`.
