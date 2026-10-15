# Momentum5 implementation plan (v2) — agent entry point

Goal: Windows 11 app + `m5ctl` CLI for the Sennheiser MOMENTUM 5 Wireless (M5AEBT) with full Smart Control Plus parity (incl. gated dangerous features). RFCOMM GAIA v3 is the control plane; BLE complements (presence, explorer, optional GATT-GAIA). Scope lock: M5 only.

## How an agent works (read only this page + one bucket)
1. Open `INDEX.md`; take the first bucket whose Deps are DONE in `PROGRESS.md` (L1 lane). A second agent may take an eligible **L2** bucket (research/hardware) in parallel — never more than 2 agents at once.
2. Read: this page, `buckets/<ID>.md`, the `Read:` refs listed there (each ≤ 100 lines), and only the repo files named in `Touch:`/`Read:`.
3. Implement only `Do:`; touch only `Touch:` paths. Bucket sizes: S ≤ ~120 LOC, M ≤ ~300 LOC.
4. Check `Done when:`. Hardware items marked HW/user → set status `BLOCKED-HW` (not DONE) until the user confirms.
5. Output contract: final file contents per touched file + one-line commit message `feat(<track>): <ID> <summary>`. No commentary unless blocked. **The user does all commits/pushes.** Update only your row in `PROGRESS.md`.
6. Don't compile or shim WinUI/WinRT in a Linux container; verify against Microsoft Learn. **Do not build/test, the user does that too.**
7. Contradiction found in `ref/`? Fix the ref table in place, append to `ERRATA.md`, rerun the owning bucket's tests.

## Tracks
A foundation · P protocol (pure) · S simulation · T transports · C client/session · K CLI · U WinUI · X dangerous/advanced (gated) · R research (L2) · I infra/docs · V verification.

## Reference chunks (`ref/`)
`00-protocol` frame/command algebra/session rules · `01-commands` catalog source table · `02-golden` verified byte vectors · `03-architecture` projects, rules, contracts · `04-safety` classes/trust tiers/gates · `05-stack` verified package pins + conventions · `06-windows-bt` RFCOMM/BLE/HRESULT notes · `07-research-lane` APK/capture/Frida/firmware playbook · `08-decisions` corrections vs v1, gates G1–G4, unknowns.

## Milestones
- **G1** RFCOMM spike (T-03) · **G2** selftest on M5 (K-03) · **M0 first light** = U-04 · **MVP** = U-07 (ANC, transparency, EQ, presets) · **v1.0** = U-20 + I-02 (all non-dangerous features) · **v1.x** = Track X + experimental transports.
