# Release checklist

How Momentum5 gets from a green build to a tagged GitHub release. Companion
docs: [README](../README.md) · [ARCHITECTURE](ARCHITECTURE.md) ·
[ADR-001 deployment](adr/ADR-001-deployment.md) ·
[verification matrix](verification-matrix.md) ·
[PROGRESS](plan/PROGRESS.md) · [ci.yml](../.github/workflows/ci.yml) ·
[release.yml](../.github/workflows/release.yml).

> **The user does all git commits, tags and pushes.** Agents never touch git
> (agent output contract, [ref/05](plan/ref/05-stack.md)). Everything below
> that mentions `git` is a user step.

## Versioning (semver policy)

- `0.MINOR.PATCH` for every release cut while **any non-X bucket has not yet
  passed V-01 on hardware**. A 0.x release may therefore ship with unfilled
  verification-matrix rows, as long as (a) the manual regression subset below
  passes and (b) the accepted gaps are named in the release notes. Minor bump
  for a landed lane or feature area, patch bump for fixes only.
- `1.0.0` is cut only after **every non-X bucket passes V-01 on hardware**
  (the full [verification matrix](verification-matrix.md) filled, and every
  earlier FAIL either fixed or documented in
  [ERRATA](plan/ERRATA.md)).
- The X track never gates a version number. X-track features (DevMode, the
  DevTools raw console, forget pairings, factory reset, the firmware update
  stages) keep their in-app **"experimental" labels** in 0.x and 1.x; a label
  is removed only after the feature's hardware round passes and an ADR says
  so. In particular, 1.0.0 does not imply the firmware update flow has been
  validated against real hardware (that stays BLOCKED-HW on R-06/X-12 until
  the OTA capture exists).
- Version sources: the git tag (`v*`) names the release and the portable zips;
  the MSIX package version is `Package.appxmanifest` `Identity/Version` and is
  bumped **manually** in the release commit (CI never rewrites the manifest).
  The .NET `Version` property is not used for releases.

## Changelog format

`CHANGELOG.md` at the repo root, Keep a Changelog style (keepachangelog.com):
newest release first, ISO dates, `Added / Changed / Fixed / Removed` verbs in
the entries — but the section structure is **one section per bucket ID** per
release, so every change stays traceable to the plan:

```markdown
# Changelog

## [0.1.0] - 2026-10-XX
### I-02 (packaging)
- Added: self-contained portable zips (win-x64, win-arm64) bundling m5ctl.exe;
  full-trust MSIX builds in CI (unsigned) per ADR-001.
### I-04 (release)
- Added: this checklist, semver policy, manual HW regression subset.
### U-17 (tray)
- Fixed: tray icon re-appears after explorer restart.
...
```

Bucket IDs and their one-line scopes come from
[INDEX](plan/INDEX.md)/[PROGRESS](plan/PROGRESS.md). The GitHub release body is
started from `--generate-notes` (auto-linked PRs) and then edited by the user
to carry the same changelog section — that edit is a checklist item below.

## Tag and release steps

The user, in order (run from the repo root, on `main`):

```bash
git checkout main && git pull
# 1. fill the manual HW regression subset below, commit the matrix updates
# 2. write the CHANGELOG.md section for the new version
# 3. bump Package.appxmanifest Identity/Version to <major>.<minor>.<patch>.0
git add CHANGELOG.md docs/verification-matrix.md Momentum5.App/Package.appxmanifest
git commit -m "release: v0.1.0"
git tag -a v0.1.0 -m "v0.1.0"
git push origin main v0.1.0
```

What happens next, without further user input:

- `release.yml` triggers on the pushed tag (`v*`). Note that the workflow file
  runs from the **tagged commit**, so it must be committed before tagging (it
  is — it landed with I-02; do not tag commits older than that).
- On `windows-latest` (same runner and .NET 11 preview setup as
  [ci.yml](../.github/workflows/ci.yml)) it builds four artifacts:
  - `Momentum5_<tag>_win-x64_portable.zip` and
    `Momentum5_<tag>_win-arm64_portable.zip` — self-contained, unpackaged,
    bundling `m5ctl` (publish profiles `win-x64`/`win-arm64`, ADR-001);
  - unsigned MSIX packages for x64 and ARM64 (packaged, framework-dependent).
- It uploads the artifacts to the workflow run, then creates the GitHub
  release (`gh release create --generate-notes`) and attaches the files
  (`gh release upload --clobber`). Re-running the workflow on the same tag is
  idempotent — the release is created only if missing and files are clobbered.
- Watch the run at
  `https://github.com/<owner>/<repo>/actions/workflows/release.yml`.

Still the user's, after the workflow finishes:

- **Sign the MSIX** (CI has no certificate; a self-signed CI cert would be
  trusted by nobody — see ADR-001). With your own code-signing certificate:

  ```powershell
  signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 <file>
  ```

  where `<file>` is each `.msix` in `dist` (CI names them
  `Momentum5.App_<version>_<arch>__<publisher-hash>.msix`, derived from the
  manifest `Identity`). A self-signed test certificate is fine for your own
  machines; a real certificate is required for wider distribution.
- **Edit the release body** to the changelog section (the auto notes are only
  a PR list).
- **Run the clean-VM verification** (below) — this is the I-02 done-when gate
  and it cannot run in CI.

## Manual hardware regression subset

Run on the real headset before **every** tag (G2: `m5ctl selftest`; fill
Result/Date/Firmware in [verification-matrix.md](verification-matrix.md) via
`m5ctl matrix`). Any FAIL blocks the release and becomes an
[ERRATA](plan/ERRATA.md) entry. This is the safety-relevant subset (identity
and battery reads, the Gated writes, the most-used Write rows); the **full**
matrix is the 1.0.0 gate. Rows are copied from the current matrix —
GET/SET are the command words.

| Key | GET | SET | Safety | Why it gates a release |
|---|---|---|---|---|
| Model | 1206 | - | Read | handshake model gate; a wrong value breaks every session |
| Firmware | 1201 | - | Read | device info and firmware-update prechecks |
| BatteryPercent | 0603 | - | Read | dashboard value and the >= 30 % firmware-update battery precheck |
| AutoOff | 0601 | 0600 | Write | power management write (u16 payload) |
| BatteryProtection | 060B | 060A | Write | battery health toggle |
| AncEnabled | 1A05 | 1A04 | Write | most-used write path: SET ack, re-GET, compare |
| AncLevel | 1A03 | 1A02 | Write | ANC mode value write |
| AncAntiWind | 1A01 | 1A00 | Write | read-back is lossy (Auto/Max both read 1) — needs the captured semantic |
| TransparencyStatus | 1805 | 1804 | Write | transparency toggle write |
| SoundMode | 0804 | 0803 | Write | multi-byte SET payload |
| EqBand[i] | 1002 | 1001 | Write | 8-band batch write with rollback |
| WearDetection | 0401 | 0400 | Write | on/off-head behavior toggle |
| TouchControlsEnabled | 1607 | 1606 | Write | touch-lock polarity (1 = locked) |
| LocalName | 2802 | 2801 | Gated | device rename — persisted, visible to every paired host |
| LowLatency | 0818 | 0817 | Gated | unverified write on 6.21.5; must stay gated |
| ConnectPaired[i] | - | 1402 | Gated | multipoint connect-by-index — changes link state |

## v0.1.0 first release — pending first execution

This section has never been executed. Work through it top to bottom and check
items off in the commit that tags `v0.1.0`; when the last item is done, mark
the section "executed <date>" and PROGRESS I-04 closes fully.

- [ ] Full solution build green locally: `dotnet build Momentum5.slnx -c
      Release -p:Platform=x64` (and once with `-p:Platform=ARM64`).
- [ ] Tests green: `dotnet test --solution Momentum5.slnx -c Release
      -p:Platform=x64`.
- [ ] G2 selftest green on the headset: `m5ctl selftest`.
- [ ] Manual HW regression subset (above) filled and committed; remaining
      unfilled V-01 rows listed by name as accepted gaps in the release notes.
- [ ] `CHANGELOG.md` created with the `[0.1.0]` section (format above).
- [ ] `Momentum5.App/Package.appxmanifest` `Identity/Version` set to
      `0.1.0.0`.
- [ ] README "Install" section updated from "no packaged release yet" to point
      at the release artifacts.
- [ ] Commit, tag `v0.1.0`, push (commands above) — user only.
- [ ] `release.yml` run green; the release carries 4 artifacts
      (2 zips, 2 unsigned MSIX); zip sizes recorded in the release notes
      (ADR-001 consequence).
- [ ] MSIX signed with the user's certificate (signtool, above).
- [ ] Clean Windows 11 26H2 VM, **MSIX**: install, launch, tray icon appears,
      device picker finds the M5, RFCOMM session comes up, one ANC write
      verified by re-read.
- [ ] Clean Windows 11 26H2 VM, **zip**: extract, run unpackaged, tray and
      RFCOMM work, `m5ctl status` from the bundled `m5ctl` works.
- [ ] Release body edited to the changelog section.

The two VM rows are the I-02 "done when" condition (MSIX installs, zip runs
unpackaged, tray + RFCOMM work in both). Signing plus the clean-VM checks are
impossible in CI by construction (ADR-001).
