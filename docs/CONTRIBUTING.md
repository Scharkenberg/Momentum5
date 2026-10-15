# Contributing

The canonical workflow is [docs/plan/README.md](plan/README.md); this page
restates it for contributors and coding agents and adds the repo's house
rules. Companion docs: [README](../README.md) · [ARCHITECTURE](ARCHITECTURE.md)
· [SAFETY](SAFETY.md).

All work is organized as **buckets** (small, self-contained tasks with an
explicit file list) tracked in the plan:

- [docs/plan/INDEX.md](plan/INDEX.md) — the bucket index, in execution order.
- [docs/plan/buckets/](plan/buckets/) — one spec per bucket (`Do:`, `Touch:`,
  `Read:`, `Done when:`).
- [docs/plan/PROGRESS.md](plan/PROGRESS.md) — per-bucket status:
  `TODO · DOING · BLOCKED-HW · SKIPPED` (plus `DONE` once landed).
- [docs/plan/ERRATA.md](plan/ERRATA.md) — corrections to the reference docs,
  append-only.

## The bucket workflow

1. **Pick a bucket.** Open [INDEX.md](plan/INDEX.md) and take the first
   bucket whose `Deps` are DONE in [PROGRESS.md](plan/PROGRESS.md) (lane L1).
   A second agent may take an eligible **L2** bucket (research/hardware) in
   parallel — never more than 2 agents at once. Honor the statuses in
   PROGRESS.md; do not start work whose dependencies are not DONE.
2. **Read exactly what the spec lists.** The plan README, the bucket's
   `buckets/<ID>.md`, the `Read:` reference chunks (each ≤ 100 lines), and
   only the repo files named in `Touch:`/`Read:`. Facts come from files you
   actually read — never invent APIs, versions, or protocol behavior.
3. **Implement only `Do:`; touch only `Touch:` paths.** One bucket per turn.
   Scope limits: **S ≤ ~120 LOC, M ≤ ~300 LOC.** If the bucket outgrows its
   size, stop and surface the split instead of silently expanding.
4. **Check `Done when:`.** Items marked HW/user are set to `BLOCKED-HW`
   (not DONE) until the user confirms on hardware. Hardware-unverified facts
   must be marked as such in docs and code comments.
5. **Output contract.** The deliverable is the final content of every touched
   file plus a one-line suggested commit message
   `feat(<track>): <ID> <summary>` (tracks: A P S T C K U X R I V). No
   commentary unless blocked. **The user does all commits and pushes — agents
   never commit.** Update only your own row in PROGRESS.md; touch nothing
   else in that file.

## No builds in the dev container

Do not compile, run, or test in the agent/Linux container, and never shim or
stub WinUI/WinRT types to make something compile there. If a Windows SDK is
available, the bucket's build/test command may be run; otherwise **verify by
inspection**:

- Re-check every referenced member/type against the real source files.
- Check brace/paren/bracket balance with a comment- and string-aware pass.
- Byte-level indentation check (below), no CRLF, UTF-8 without BOM, no
   trailing whitespace.
- `git status` delta must equal exactly the intended file set.
- WinUI/WinRT API usage is verified against Microsoft Learn documentation.

The user builds and runs the full solution on Windows (see
[README](../README.md) — install section).

## ERRATA protocol

If you find a contradiction in a `docs/plan/ref/` chunk (or any doc that
other buckets depend on): fix the ref table **in place**, append one line to
[ERRATA.md](plan/ERRATA.md) in the format `date · bucket · what changed`, and
rerun the owning bucket's tests. No separate "fix" buckets. Protocol facts
are tagged with evidence levels in
[docs/plan/ref/00-protocol.md](plan/ref/00-protocol.md) — keep those tags
intact when editing.

## House style

C# (matches `Directory.Build.props` and every existing file):

- File-scoped namespaces; namespace matches the project's existing files
  (e.g. everything under `Momentum5.Client` uses `namespace Momentum5.Client;`
  regardless of folder).
- **Tab indentation** in all C# files. `sealed` by default. Nullable enabled.
- `TimeProvider` for any time logic (injectable for tests); no `async void`
  outside event handlers; no exceptions in decode paths (return failure).
- `ConfigureAwait(false)` on every await in library code.
- XML docs on public members; hex in logs/tests uppercase and space-separated.
- Client code contains no WinRT/UI/DI registrations (see
  [ARCHITECTURE](ARCHITECTURE.md) dependency rules).

XAML: keep the WinUI template style the pages already use (4-space
indentation, attribute-per-line); make minimal edits, never reformat a page
wholesale.

### The tab-rewrite pitfall

The tab-indented C# files are the reason for this repo's most repeated tool
note: **search-and-replace "Edit" style tools silently rewrite tabs as
spaces** when they touch a tab-indented file, which breaks the byte-level
style contract above. Rules:

- **New files: use a full-file write** (Write tool / heredoc), never an
  edit-in-place.
- **Patches to existing files: use byte-exact replacement** — e.g. a short
  Python script that reads the file as bytes, replaces an exact byte string,
  and writes it back. Do not use tools that re-emit the file from a parsed
  representation.
- After any edit, verify: `grep -P '^\t'` finds tab-indented lines, a search
  for `^        ` (8 spaces) finds none, and the diff contains no
  whitespace-only churn.

## Worklog

Agent sessions record what they did in the orchestrator's worklog
(`/home/z/my-project/worklog.md`, outside the repo): one section per task
with a `---` separator, the task ID, the concrete steps, and a stage summary
(files, key decisions, suggested commit message). Append it immediately
after finishing the file work — it is the crash-recovery record.
