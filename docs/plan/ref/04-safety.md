# ref/04 — Safety doctrine

The sponsor wants every feature, including dangerous ones. Safety = typed, logged, gated — not hidden.

## Classes (enum `SafetyClass` in Core)
| Class | Rule |
|---|---|
| Read | always allowed (if trust ≥ Unknown) |
| Write | allowed when trust ≥ Compatible; read-after-write mandatory |
| Gated | like Write + explicit user action in UI + per-command capability flag |
| Dangerous | Track X only: DevMode + single-use `ConsentToken` + pre/post frame log |
| Blocked | refused; reachable only from DevTools console with per-command override re-typed each session |
| Unknown | GET-style probing only from DevTools (never writes) |

## Firmware trust tiers (replaces the old exact-match 6.21.5 gate)
- **Verified**: exactly 6.21.5 (hardware-captured) → all Write/Gated allowed.
- **Compatible**: same major.minor, patch greater → Write allowed, Gated needs DevMode, banner "firmware newer than verified".
- **Unknown**: anything else → read-only, banner; DevMode can override per session.
- After V-01 matrix passes on a new firmware, add it to `M5Profile.Verified` (one line).

## Model gate
After `S:1206`, name must start with `M5AEBT`, else disconnect with "unsupported device". No generic Sennheiser mode.

## Rules
1. Every outgoing frame passes `ISafetyGate` in the pipeline — no bypass API.
2. Sendable ⇒ command-type bits (ref/00 §2).
3. Never send an upgrade/VM command that was not observed in a captured official session (R-06).
4. Every Dangerous execution logs intent → result; destructive ones show a recovery info bar.
5. UI pages (other than Advanced/DevTools) cannot construct Dangerous/Blocked requests: enforced by an Architecture test that scans App for `GaiaCatalog.Blocked|Dangerous` usage outside `Advanced/`.
