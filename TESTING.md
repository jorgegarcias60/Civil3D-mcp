# Testing record (Joshua8-AI fork)

How this fork is verified, and the last recorded results. Run everything from the
repo root on Windows with Node 18+ and the .NET 10 SDK.

## Offline (no Civil 3D needed)

### Per-process COM registration (2026-10-15)

| Check | Result |
|---|---|
| Harness net8 / net10 (`ComRegistrationChecks`, real Running Object Table, stand-in object) | Pass: display name `!Civil3DMcp.AcadApplication:<pid>`, registration found by that name, second Register a no-op, Revoke removes it, non-positive pid refused. |
| Plugin builds net8 (refs-2026) and net10 (C_References) | 0 warnings, 0 errors. |
| `npm run build`, full vitest, `docs:check`, installer check | Pass (874 tests). |

Live test pending: needs a plugin install with Civil 3D closed.

### Instance follow-ups #36, #37, #38 and #40 (2026-10-15)

Offline checks first (implementation round, before the live fixes below), then the live results on Civil 3D
2026. The installer check used temporary fake roots.

| Command | Result |
|---|---|
| `npm run build` | Exit 0. Includes the packaged UI Automation helper. |
| `npm run docs:check` | Exit 0. Generated reference current, 212 entries. |
| `dotnet build Civil3D-MCP-Plugin\Civil3DMcpPlugin.csproj -c Release --restore -p:Civil3DReferencesPath=C:\Users\jorge\AppData\Local\Civil3D-mcp\refs-2026 -p:NuGetAudit=false --source C:\Users\jorge\.nuget\packages` | Exit 0. 0 warnings, 0 errors. Repeated after net10 with the same result. |
| `dotnet build Civil3D-MCP-Plugin\Civil3DMcpPlugin.csproj -c Release --restore -p:Civil3DReferencesPath=C:\Users\jorge\PycharmProjects\Civil3D-mcp\C_References -p:TargetFramework=net10.0-windows -p:NuGetAudit=false --source C:\Users\jorge\.nuget\packages` | Exit 0. 0 warnings, 0 errors. |
| `dotnet run --project tests\RunCodeHarness\RunCodeHarness.csproj -f net8.0 -p:NuGetAudit=false -p:RestoreSources=C:\Users\jorge\.nuget\packages` | Exit 0. PASS (230 references). |
| `dotnet run --project tests\RunCodeHarness\RunCodeHarness.csproj -f net10.0 -p:NuGetAudit=false -p:RestoreSources=C:\Users\jorge\.nuget\packages` | Exit 0. PASS (238 references). |
| `powershell -NoProfile -ExecutionPolicy Bypass -File tests\powershell\install-trusted.check.ps1` | Exit 0. 0 failures. |
| `node --check scripts/live-multi-instance-smoke.mjs` | Exit 0. |
| `node --check scripts/copy-static-assets.mjs` | Exit 0. |
| `powershell -NoProfile -ExecutionPolicy Bypass -File C:\CAD-Tools\core\tools\Find-CaseCollisions.ps1 scripts\scan-instance-dialogs.ps1` | Exit 0. No case collisions. |
| `$env:LOCALAPPDATA = Join-Path $env:TEMP 'c3d-instance-followups-offline'; npx vitest run --configLoader runner --cache=false tests/instance_manager.test.ts tests/instance_dialogs.test.ts tests/multi_instance_smoke.test.ts` | Exit 0. 90 tests passed in 3 files. |
| `$env:LOCALAPPDATA = Join-Path $env:TEMP 'c3d-instance-followups-offline'; npx vitest run --configLoader runner --cache=false` | Final run: exit 0. 872 tests passed in 61 files (84.94 seconds). |

The initial full Vitest run had 32 failures (839 passed) because existing tests
read the real plugin discovery folder, which the sandbox cannot access. The
isolated rerun used the temporary `LOCALAPPDATA` above. It had 870 passes and
one transient HTTP test failure: Node rejected a random listening port as a
"bad port". The final full run passed. The initial net8 build without offline flags exited 0 with two
NU1900 audit warnings; cache-only builds had zero warnings. Harness file-symlink
checks were skipped because this account lacks the required privilege.

Coverage includes Ignore diagnostics and unknown dialogs, helper rejection of
stale process identities, timeout ownership and capacity, late attachment,
process exit, grace expiry, pending stop rules, plugin claims after seven
minutes and rejection after fifteen, orphan re-adoption, and read-only Design
Master file validation. Civil 3D 2024/2025/2027 hosts remain untested.

Live, Civil 3D 2026, scratch copies only, next to Jorge's own instance (2026-10-15):

| Check | Result |
|---|---|
| `node scripts/live-multi-instance-smoke.mjs A.dwg B.dwg --orphan` | Exit 0: independent ports/tokens/drawings, cap 2 refusal, fixed-port refusal, user-instance guard, stop refuses unsaved changes, user instance untouched, orphan discovery, cross-label refusal, cap 1, same-label re-adoption and discovery cleanup. A first run failed only because Jorge restarted his own Civil 3D mid-run; the smoke now re-reads user instances just before the guard. |
| Start on a copy with dead xrefs (#36), first try | The dialog was found but its command links expose no UIA pattern (Pane, class Button). Nothing was clicked; #37 returned `status: "starting"` at the 300 s timeout; stopping the pending launch killed only its own process. |
| #36, after the fixes | "References - Not Found Files" answered with Ignore through the button's BN_CLICKED notification (`method: "button_notify"`); start completed in about 25 s with the missing-references warning and no human click. |
| #40 | `designMaster: { pipe: "dm-mcp-117008" }` listed on Jorge's instance with the Design Master plugin loaded. |
| Full vitest with Civil 3D running | 874 passed; tests now use an empty temporary LOCALAPPDATA (previously 24 approval tests failed whenever Civil 3D was open, also on d1a0035). |

Findings fixed during the live run: TDM_CLICK_BUTTON cannot target command links (they have no control id);
the scanner script became too long for `-EncodedCommand` (spawn ENAMETOOLONG) and now runs with `-File`;
the result keeps every dialog seen during the start; windows are found by Win32 enumeration of the launched pid,
so UI Automation never queries another process.

### Latest orphan policy and routing blockers (2026-10-15)

Changes remain uncommitted in `c3d-multi-instance`. No Autodesk process was
started or driven. No real installer target or client registration was changed.

| Command | Result |
|---|---|
| `npm run build` | Exit 0. |
| `npx vitest run --configLoader runner --cache=false` | Exit 0. 859 tests passed in 60 files (85.28 seconds). |
| `npm run docs:check` | Exit 0. Generated reference current, 212 entries. |
| `dotnet build Civil3D-MCP-Plugin\Civil3DMcpPlugin.csproj -c Release --restore -p:Civil3DReferencesPath=C:\Users\jorge\AppData\Local\Civil3D-mcp\refs-2026 -p:NuGetAudit=false --source C:\Users\jorge\.nuget\packages` | Exit 0. 0 warnings, 0 errors. |
| `dotnet build Civil3D-MCP-Plugin\Civil3DMcpPlugin.csproj -c Release --restore -p:Civil3DReferencesPath=C:\Users\jorge\PycharmProjects\Civil3D-mcp\C_References -p:TargetFramework=net10.0-windows -p:NuGetAudit=false --source C:\Users\jorge\.nuget\packages` | Exit 0. 0 warnings, 0 errors. |
| Net8 build above, repeated after net10 with `--restore` | Exit 0. 0 warnings, 0 errors. |
| `dotnet run --project tests\RunCodeHarness\RunCodeHarness.csproj -f net8.0 -p:NuGetAudit=false -p:RestoreSources=C:\Users\jorge\.nuget\packages` | Exit 0. PASS (230 references). |
| `dotnet run --project tests\RunCodeHarness\RunCodeHarness.csproj -f net10.0 -p:NuGetAudit=false -p:RestoreSources=C:\Users\jorge\.nuget\packages` | Exit 0. PASS (238 references). |
| `powershell -NoProfile -ExecutionPolicy Bypass -File tests\powershell\install-trusted.check.ps1` | Exit 0. 0 failures. Fake roots only. |
| `node --check scripts/live-multi-instance-smoke.mjs` | Exit 0. |
| `npx vitest run tests/instance_routing.test.ts --configLoader runner --cache=false` | Exit 0. 21 tests passed. |
| `npx vitest run tests/instance_manager.test.ts --configLoader runner --cache=false` | Exit 0. 58 tests passed, including the final orphan cap assertion. |
| `powershell -NoProfile -ExecutionPolicy Bypass -File C:\CAD-Tools\core\tools\Find-CaseCollisions.ps1 scripts\claude-bootstrap-and-run.ps1` | Exit 0. no case collisions. |
| `git -c safe.directory=C:/Users/jorge/PycharmProjects/c3d-multi-instance diff --check` | Exit 0. Line-ending notices only. |

NuGet restore used the local cache with auditing disabled because network access
is restricted. The initial net8 build exited 0 with two NU1900 audit warnings;
the cache-only rebuilds had zero warnings. The new routing fixture initially
failed response-schema validation, then passed after its mock reply was corrected.
The full suite needed no PowerShell environment retry.

Production-handler TCP tests switch attachments during initial and execution
progress for client-approved write-mode run-code. Only the original instance
receives the write and token. Fixed routing without discovery sends the legacy-only
marker unless explicitly authorized. The harness executes the production plugin
guard and verifies that a discovered user plugin rejects that marker before dispatch.

Orphan checks cover dead and reused owner pids, live or unknown owners, authenticated
same-label re-adoption, explicit cross-label adoption, owner metadata in claims and
discovery, listener restart and the agent cap. The plugin rechecks both process
identities before adoption. Pending and taken claim reservations remain covered.
Live Autodesk and Civil 3D 2024/2025 host checks were not run.

### Current live smoke invocation (not run in this review)

Leave `CIVIL3D_PORT` unset in the MCP registration. The existing user-instance
smokes require exactly one user instance with the named disposable drawing and
explicitly attach to its pid:

```powershell
npm run test:live-plugin -- --user-instance --expect-drawing RUNCODE-SMOKE.dwg
node scripts/live-run-code-smoke.mjs --user-instance --expect-drawing RUNCODE-SMOKE.dwg
node scripts/live-label-smoke.mjs --user-instance --expect-drawing RUNCODE-SMOKE.dwg
node scripts/live-plot-smoke.mjs --user-instance --expect-drawing RUNCODE-SMOKE.dwg --out <existing-export-folder>
npm run test:live-mcp-client -- --user-instance --expect-drawing RUNCODE-SMOKE.dwg
npm run test:live-host -- --user-instance --expect-drawing RUNCODE-SMOKE.dwg
node scripts/live-multi-instance-smoke.mjs C:\scratch\A.dwg C:\scratch\B.dwg
```

`--expect-drawing` matches the complete filename including `.dwg`, ignoring
case, or an exact absolute path. For the host smoke, configure `MCP_HTTP_TOKEN`
before starting the HTTP server and give the smoke process the same token.
Keep its `CIVIL3D_LIVE_SMOKE_CONFIRM=DISPOSABLE_DRAWING` opt-in as well.
The plugin verifier also supports `--user-instance --no-drawing` with exactly
one empty scratch user instance. Run the commands separately; `test:live-all`
does not forward selection flags to every script.

The multi-instance smoke reports `SKIPPED` for its user-attachment guard when
no user instance exists. `civil3d_instance start` can wait 300 seconds; the
MCP registration needs `tool_timeout_sec` at least 330. A cancelled start
retains its ownership claim through expiry. External registration edits remain
pending: remove `CIVIL3D_PORT` and set that timeout before a live run.

### Launch-claim review follow-up (2026-10-15)

Checked in `c3d-multi-instance`. Nothing installed. Civil 3D was not started.

| Command | Result |
|---|---|
| `npm run build` | Exit 0. |
| `npx vitest run --configLoader runner --cache=false` | Exit 0. 815 tests passed in 59 files (91.76 seconds). |
| `npm run docs:check` | Exit 0. Generated reference current, 212 entries. No regeneration needed. |
| `dotnet build Civil3D-MCP-Plugin\Civil3DMcpPlugin.csproj -c Release --restore -p:Civil3DReferencesPath=C:\Users\jorge\AppData\Local\Civil3D-mcp\refs-2026` plus offline flags below | Exit 0. 0 warnings, 0 errors. |
| `dotnet build Civil3D-MCP-Plugin\Civil3DMcpPlugin.csproj -c Release --restore -p:Civil3DReferencesPath=C:\Users\jorge\PycharmProjects\Civil3D-mcp\C_References -p:TargetFramework=net10.0-windows` plus offline flags | Exit 0. 0 warnings, 0 errors. |
| Net8 build above, repeated after net10 with `--restore` | Exit 0. 0 warnings, 0 errors. |
| `dotnet run --project tests\RunCodeHarness\RunCodeHarness.csproj -f net8.0` plus offline flags and explicit paths below | Exit 0. PASS (230 references). |
| Same harness command with `-f net10.0` and its explicit paths | Exit 0. PASS (238 references). |
| `powershell -NoProfile -ExecutionPolicy Bypass -File tests\powershell\install-trusted.check.ps1` | Exit 0. 0 failures. Fake roots only. |
| `powershell -NoProfile -ExecutionPolicy Bypass -File C:\CAD-Tools\core\tools\Find-CaseCollisions.ps1 scripts\claude-bootstrap-and-run.ps1` | Exit 0. No case collisions. That existing script was not edited in this follow-up. |
| `node --check scripts/live-multi-instance-smoke.mjs` | Exit 0. Live script not run. |
| `git -c safe.directory=C:/Users/jorge/PycharmProjects/c3d-multi-instance diff --check` | Exit 0. Line-ending notices only. |

Offline flags: `-p:RestoreSources=C:\Users\CodexSandboxOffline\.nuget\packages -p:NuGetAudit=false`.
Net8 harness arguments: `-- C:\Users\jorge\AppData\Local\Civil3D-mcp\refs-2026 Civil3D-MCP-Plugin\bin\Release\net8.0-windows\Civil3DMcpPlugin.dll`.
Net10 harness arguments: `-- C:\Users\jorge\PycharmProjects\Civil3D-mcp\C_References Civil3D-MCP-Plugin\bin\Release\net10.0-windows\Civil3DMcpPlugin.dll`.
The harness skipped file-symlink checks because this account lacks the privilege.

The harness proves same-instance recovery after a claim-reader lock, pending-proof
and drawing-load retries, exact launch-token/pid/start matching, and one atomic
winner across processes. Node regressions cover rooted drive-relative paths,
normalization through launch, missing proof and concurrent unrelated discoveries.
Focused runs passed those manager assertions and all six lifecycle source checks.
The harmless PowerShell child check passed and its temporary script passed the
case-collision scan. Grouped runs exceeded the five-second default on two Windows
process checks; they now allow 20 seconds and passed in the final suite. Earlier
attempts hit `spawn EPERM` when embedding child-launch code in an encoded command;
the check now uses a temporary script, like the production helper. Later attempts
hit `Access is denied` starting Node or lost the runner pipe (exit -1), including
a failed docs check during prebuild. A build also passed with bundled Node 24.21.0
and temporary npm wrappers. The final sequential verification above passed with
the ordinary commands and no environment changes. Actual Explorer and Civil 3D
launch remain live-only. Civil 3D 2024/2025 were not run.

### Multi-instance review fixes (2026-10-15)

Checked in `c3d-multi-instance`. No installation or live Autodesk execution.

| Command | Result |
|---|---|
| `npm run build` | Exit 0. |
| `npx vitest run --configLoader runner --cache=false` | Exit 0. 806 tests passed in 59 files. |
| `npm run docs:check` | Exit 0. Generated reference current, 212 entries. No regeneration needed. |
| `dotnet build Civil3D-MCP-Plugin\Civil3DMcpPlugin.csproj -c Release --restore -p:Civil3DReferencesPath=C:\Users\jorge\AppData\Local\Civil3D-mcp\refs-2026` | Exit 0. Initial build: 2 NU1900 warnings, 0 errors. NuGet audit could not reach nuget.org. |
| Same net8 command with the offline flags below | Exit 0. 0 warnings, 0 errors. |
| `dotnet build Civil3D-MCP-Plugin\Civil3DMcpPlugin.csproj -c Release --restore -p:Civil3DReferencesPath=C:\Users\jorge\PycharmProjects\Civil3D-mcp\C_References -p:TargetFramework=net10.0-windows` plus offline flags | Exit 0. 0 warnings, 0 errors. |
| Net8 command with offline flags, repeated after net10 | Exit 0. 0 warnings, 0 errors. |
| `dotnet run --project tests\RunCodeHarness\RunCodeHarness.csproj -f net8.0` plus offline flags and `-- C:\Users\jorge\AppData\Local\Civil3D-mcp\refs-2026 Civil3D-MCP-Plugin\bin\Release\net8.0-windows\Civil3DMcpPlugin.dll` | Exit 0. PASS (230 references). |
| `dotnet run --project tests\RunCodeHarness\RunCodeHarness.csproj -f net10.0` plus offline flags and `-- C:\Users\jorge\PycharmProjects\Civil3D-mcp\C_References Civil3D-MCP-Plugin\bin\Release\net10.0-windows\Civil3DMcpPlugin.dll` | Exit 0. PASS (238 references). |
| `powershell -NoProfile -ExecutionPolicy Bypass -File tests\powershell\install-trusted.check.ps1` | Exit 0. 0 failures. Fake roots only. |
| `powershell -NoProfile -ExecutionPolicy Bypass -File C:\CAD-Tools\core\tools\Find-CaseCollisions.ps1 scripts\claude-bootstrap-and-run.ps1` | Exit 0. No case collisions. The existing PowerShell change was not edited during this review. |
| `node --check scripts/live-multi-instance-smoke.mjs` | Exit 0. Live script not run. |
| `git -c safe.directory=C:/Users/jorge/PycharmProjects/c3d-multi-instance diff --check` | Exit 0. Only line-ending notices. |

Offline flags: `-p:RestoreSources=C:\Users\CodexSandboxOffline\.nuget\packages -p:NuGetAudit=false`.
Harness paths were supplied explicitly to avoid the sandbox account's different
LocalAppData. File-symlink checks skipped for missing privilege.

New tests cover legacy fallback markers over TCP, approval refusal after switching
empty instances or reusing a pid, HTTP instance authentication, real process creation
times, stale-pid cap and drawing checks, quit deadlines, token-revocation order,
user snapshots and cleanup after failed starts. Both harnesses compile the smoke
script's actual pid and drawing guard. Plugin dispatch and wake wiring use
source checks; the harness tests monotonic expiry. Host wake and actual quit remain
live-only. Civil 3D 2024/2025 were not run.

Focused attempts before the final checks:

- `npx vitest run tests/instance_plugin_lifecycle.test.ts` exited 1 before tests.
  The default loader tried to write to the read-only `node_modules/.vite-temp`.
  The runner loader passed all 6 lifecycle tests without that write.
- `dotnet build Civil3D-MCP-Plugin/Civil3DMcpPlugin.csproj --no-restore -v:q`
  exited 1 because the default `../C_References` directory is absent in this
  worktree. The explicit reference paths above passed.
- `dotnet run --project tests/RunCodeHarness -f net10.0 --no-restore -- --instance-only`
  first exited 1 on the existing cross-process claim check with `(agent,agent)`.
  An immediate identical rerun passed with `(agent,user)`. A bounded 20-run loop
  reproduced 3 double claims. The fix holds an exclusive claim read handle through
  validation and rename. The same command passed all 27 instance checks afterward;
  20 sequential runs with `--no-build` added passed with no double claims. A new
  check proves reader exclusion and rename with the source handle still open.

### Several Civil 3D instances (2026-10-15)

Checked in `c3d-multi-instance`. Nothing was installed and Civil 3D was not started.

| Check | Result |
|---|---|
| `npm run build` | Pass. |
| `npx vitest run --configLoader runner --cache=false` | 783 tests passed in 59 files after review fixes. |
| `npm run docs:generate`, then `npm run docs:check` | Pass. 212 entries. |
| Plugin Release net8, net10, then net8 rebuild with `--restore` | Final builds: 0 warnings, 0 errors. First net8 build had two NU1900 audit warnings. Offline reruns used `-p:RestoreSources=C:\Users\CodexSandboxOffline\.nuget\packages -p:NuGetAudit=false`. net8 used `-p:Civil3DReferencesPath=C:\Users\jorge\AppData\Local\Civil3D-mcp\refs-2026`; net10 used `-p:Civil3DReferencesPath=C:\Users\jorge\PycharmProjects\Civil3D-mcp\C_References -p:TargetFramework=net10.0-windows`. |
| RunCodeHarness, net8 and net10 | PASS (230 and 238 references). File-symlink checks skipped because this account lacks the required privilege. |
| `powershell -NoProfile -ExecutionPolicy Bypass -File tests\powershell\install-trusted.check.ps1` | 0 failures, fake roots only. |
| Case-collision check on `scripts\claude-bootstrap-and-run.ps1` | No case collisions. |
| `node --check scripts/live-multi-instance-smoke.mjs` | Pass. Live script not run. |

New checks cover stale discovery, attachment and token routing, ownership rules,
claims, launch cleanup, the cap, stop guards, Unicode helper input and atomic claim
contention. Host launch, document events and shutdown still need the live smoke test.
Civil 3D 2024/2025 shortcut selection is covered; those hosts were not run.

Review checks also cover fixed user ownership (including unsaved startup drawings),
process-age claim rejection, listener restart ownership, separate synchronized claim
processes, the production port binder, Save As snapshots and save-event source guards.
Real TCP checks prove request routing stays pinned across an attachment change during
approval progress and that each RPC carries its process identity. Stop checks reject
changed approval targets and replacement processes. The smoke script is run in memory
with mocked clients to prove cleanup includes an unexpected third successful start.
Both full harness commands used the same offline restore flags above:
`dotnet run --project tests\RunCodeHarness\RunCodeHarness.csproj -f net8.0` and `-f net10.0`.
Live save events and port replacement in Civil 3D remain untested.

### Run-code preview and drawing helpers: issues #20 and #22 (2026-10-12)

Offline results for `jorgegarcias60/run-code-helpers-preview`, measured outside any
sandbox on Jorge's machine on 2026-10-12. The integration build included
`civil3d-2027`, this branch and `jorgegarcias60/plot-hardening`.

| Check | Result |
|---|---|
| `npm run build` | ok |
| vitest | All passed: 739 tests (55 files) |
| `docs:check` | ok |
| Plugin Release net8, refs-2026 | 0 warnings |
| Plugin Release net10, C_References | 0 warnings |
| RunCodeHarness, net8 and net10 | pass |
| `tests/powershell/install-trusted.check.ps1` | 0 failures |

New tests and checks cover:

- #20: preview validation, API skew, separate approvals, args hashes, final counts and handle limits, drawing rollback, document targeting, and transaction, Session and filesystem guards.
- #22: helper signatures and read-mode guards, text bounds, XClip geometry at large coordinates, xref units and paths, helper rollback and undo, viewport identity, activation and layout restoration.

#### Live results

Civil 3D 2026, 2026-10-12, run by Claude Code with the integration build installed
and scratch drawings A (active) and B. The command passed 67, failed 0 and skipped 3
(the long read, plus a dormant-layout check that found no candidate):

`node scripts/live-run-code-smoke.mjs --approval client --expect-drawing RUNCODE-SMOKE --side <10 MB background> --damaged <fixtures> --other <B> --helpers --preview`

- `--helpers`: TextBox corners for rotated DBText, MText and attributes; XClip kept only the inside line; AttachXref required an explicit scale for a unitless source, converted inches into feet as 1/12, and one UNDO removed it; SetViewport edits persisted after layout activation and the overall viewport was refused.
- `--preview`: exact added, erased and modified counts and handles, unchanged drawings on A and with `document` = B, and refusal of system-variable and file writes.

The same round passed 47 hardening checks with 0 failures and live-label-smoke 9/9.
AutoCAD reads XClip's BackClip back as `1e300`, meaning no back plane, although the helper passes zero.

Hand checks on a reopened drawing, with layouts not yet activated in the session,
refused SetViewport on a dormant layout's overall viewport and restored the previous
layout. An edit to a normal viewport on another dormant layout persisted after
activation over COM and regeneration. The automated dormant-layout check has not
been run live yet; it found no candidate in the smoke run.

The live findings were fixed in this branch: preview identifies new objects by the
starting Handseed because `IsNewObject` is false inside the open run transaction;
SetViewport activates dormant layouts before identifying the overall viewport and
restores the previous layout.
### Plot publishing hardening: issue #21 (2026-10-12)

Offline results for `jorgegarcias60/plot-hardening`, measured outside any
sandbox on Jorge's machine on 2026-10-12. The integration build combined
`civil3d-2027`, this branch and `jorgegarcias60/run-code-helpers-preview`:

| Check | Result |
|---|---|
| `npm run build` | Ok. |
| `npx vitest run --configLoader runner --cache=false` | All passed: 739 tests in 55 files. |
| `npm run docs:check` | Ok. |
| Plugin Release net8.0-windows, refs-2026 | 0 warnings. |
| Plugin Release net10.0-windows, C_References | 0 warnings. |
| RunCodeHarness `-f net8.0` | Pass. |
| RunCodeHarness `-f net10.0` | Pass. |
| `tests/powershell/install-trusted.check.ps1` | 0 failures. |

New tests and checks cover:

- #21: document selection and API skew, reopened layout initialization, saved-revision selection and settings, CTB/STB and media preflight before writes, PDF page count and size verification, object-stream detection, cancellation drain, job snapshots and smoke-fixture cleanup after failures.

#### Live results

Live, Civil 3D 2026, 2026-10-12, run by Claude Code on a scratch drawing with
that integration build installed through `install-trusted.ps1`: **20/20 passed**.
The run followed a Civil 3D restart; layout tabs had not been activated in that
session. To repeat it on a saved scratch drawing with two configured layouts:

`node scripts/live-plot-smoke.mjs --expect-drawing RUNCODE-SMOKE.dwg --out <temporary folder in the export root> --ctb-fixture`

The output folder must exist inside both import and export roots. Disable the
PC3's viewer option first. The live check groups passed:

- Discovery: plot API 2 and targeted `list_layouts` flags.
- Publishing: a two-sheet job completed and verified two pages, both page sizes within 1 pt, with resolved settings echoed.
- Missing table: a saved DWG was refused with the table and layout names before any output was written.
- Saved revision: unsaved changes were refused; `requireSaved: false` used the saved plot style.
- Cancellation and cleanup: native publishing drained before the job reported, health answered afterwards, and the temporary plot style copy and all outputs were removed.

Found live and fixed in this branch: `Layout.GetViewports()` is empty after
reopening a drawing when layout tabs have not been activated. Initialization
now also checks the layout block for a viewport. `SetCurrentStyleSheet` refuses
a missing table name, so `--ctb-fixture` copies an installed CTB/STB, saves the
side DWG with it, then removes only the copy before testing refusal.
No live checks were rerun during this documentation edit.

### Approval logging: issue #29 (2026-10-12)

`tests/approval_logging.test.ts` covers direct MCP, registry, nested `civil3d_orchestrate` / `civil3d_docs` and authenticated HTTP calls in token and client modes. Each expected refusal logs one WARN line with no stack frames; unexpected errors keep their stacks; client error text, `isError`, codes and request IDs are unchanged. The tests fail on the previous code.

| Check | Result |
|---|---|
| `npm run build` | ok |
| vitest | 696 passed (53 files) |
| `docs:check` | ok |
| Plugin builds, net8 and net10 | 0 warnings |
| RunCodeHarness, net8 and net10 | pass |
| `install-trusted` check | 0 failures |

Live, Civil 3D 2026, 2026-10-12: a token-mode server against the running plugin logged one `[WARN] Tool request refused by approval policy` line, with no `[ERROR]` line and no stack frames, for an approval-required call, an unknown token, and a call nested through `civil3d_orchestrate`.

### Run-code hardening: issues #7, #11, #12, #24 and #25 (2026-10-12)

Offline results for `jorgegarcias60/run-code-hardening`, measured outside any
sandbox on Jorge's machine on 2026-10-12:

| Check | Result |
|---|---|
| `npm run build` | Ok. |
| `npx vitest run --configLoader runner --cache=false` | All passed: 622 tests in 50 files. |
| `npm run docs:check` | Ok. |
| Plugin Release net8.0-windows, refs-2026 | 0 warnings. |
| Plugin Release net10.0-windows, C_References | 0 warnings. |
| RunCodeHarness `-f net8.0` | Pass. |
| RunCodeHarness `-f net10.0` | Pass. |
| `tests/powershell/install-trusted.check.ps1` | 0 failures. |

New tests and checks cover:

- #7: startup host mapping and refusal before token reads or plugin calls.
- #11: read-mode Database mutation guards, detached destinations, aliases, null/default definitions and ref/out exposure.
- #12: streamed and nested-tuple results, inline limits, JSON previews, copies, confinement and serialization/file failures.
- #24: fresh-database retries, disposal, drawing-content error filtering and preservation of the first error.
- #25: retention settings and age boundaries, current-run and audit-file protection, confinement, filesystem links and deletion failures.

#### Smoke-script stderr fix (2026-10-12)

Offline follow-up in the same worktree, inside the Codex sandbox. Changes remain uncommitted. No Civil 3D live checks were run.

| Check | Result |
|---|---|
| `node --check` on the six changed `.mjs` scripts | Exit 0 for each. |
| `npm run build` | Exit 0. |
| `npx vitest run --configLoader runner --cache=false` | 637 tests passed in 51 files. |
| `npm run docs:check` | Exit 0; 210 entries current. |
| Plugin Release net10, then net8 rebuilt with `--restore --ignore-failed-sources -p:NuGetAudit=false` | Both exit 0; 0 warnings and 0 errors. |
| `dotnet run --project tests\RunCodeHarness\RunCodeHarness.csproj -f net8.0` | Exit 0; PASS (230 references). |
| `dotnet run --project tests\RunCodeHarness\RunCodeHarness.csproj -f net10.0` | Exit 0; PASS (238 references). |
| `powershell -NoProfile -ExecutionPolicy Bypass -File tests\powershell\install-trusted.check.ps1` | Exit 0; 0 failures. |

The static check rejected all five undrained transports in the committed scripts. Buffer tests drain over 450 KB and check the 200-line limit and bounded unterminated lines. NuGet was unreachable: the first net8 plugin build and both harness runs reported NU1900 audit warnings; plugin rebuilds used cached packages with the network audit disabled. The net10 harness skipped file-symlink checks because the sandbox lacks that privilege.

#### Live-check setup fixes (2026-10-12)

Offline follow-up in this worktree. Existing stderr changes were preserved. No Civil 3D live checks were run.

| Check | Result |
|---|---|
| `node --check scripts/live-run-code-smoke.mjs` | Exit 0. The other five changed smoke/helper scripts also passed `node --check`. |
| `npm run build` | Exit 0. |
| `npx vitest run --configLoader runner --cache=false` | Exit 0; 637 tests passed in 51 files. |
| `npm run docs:check` | Exit 0; 210 entries current. |
| Plugin Release net8, then net10, then net8 rebuilt, each with `--restore` and the matching references | Each exit 0; 0 warnings and 0 errors. |
| `dotnet run --project tests\RunCodeHarness\RunCodeHarness.csproj -f net8.0` | Exit 0; PASS (230 references). |
| `dotnet run --project tests\RunCodeHarness\RunCodeHarness.csproj -f net10.0` | Exit 0; PASS (238 references). |
| `powershell -NoProfile -ExecutionPolicy Bypass -File tests\powershell\install-trusted.check.ps1` | Exit 0; 0 failures. |

Both harness runs reported NU1900 warnings because the NuGet audit service was unreachable. The net10 harness skipped two file-symlink checks because the sandbox lacks the required privilege. Plugin builds had no warnings.

#### Live results

Follow-up live result for PR #18, Civil 3D 2026, 2026-10-12 (run by Claude Code on scratch drawings):
with drawing A active and B open, a write with `document` = B added one tagged
DBText (`status: "ok"`, `committed: true`, `activated: true`, `switchedBack: true`).
A targeted read of B then counted 1, with A still active. After activating B,
one `UNDO 1` sent over COM removed it. A was activated again, and a targeted
read of B counted 0. This was run twice with the same result.

#### Live hardening checks

Live, Civil 3D 2026, 2026-10-12, run by Claude Code on scratch drawings. The server came from this branch; the plugin was built from `civil3d-2027` plus PRs #26-#28 and installed with the new installer:
- Full smoke, token mode: 51 passed, 1 skipped (the long read, not requested). Client mode: the same. `live-label-smoke`: 9/9.
- `--hardening`, token mode: 48 passed, 0 failed. Client mode: 47 passed, 0 failed. The checks covered:
  - #7: refusals with no new run folder;
  - #11: `SetDimstyleData` and `GetObjectId(true)` rejected, with DIMSTYLE and DBMOD unchanged; both detached `ReadDwgFile` forms counted 131,305 objects of a 10 MB background;
  - #12: 20,000 objects and a large tuple saved as complete JSON with matching byte counts; a small result stayed inline.
- #24 fixtures were outside the import roots, so they were skipped there and tested separately with synthetic files inside the import root:
  - a good copy compared in 2.7 s;
  - a truncated copy failed with `eEndOfFile` in 4.4 s;
  - zero-filled and text files failed with `eBadDwgHeader` in under 3 s;
  - `civil3d_health` answered after each.
- Before the stderr fix, the token-mode full smoke froze after about 45 runs; with it, the same run passes.

Smoke scripts drain server stderr so repeated approval logs cannot fill the pipe and freeze the server; failures print a bounded tail.

With a running plugin from this build and a scratch active drawing, run:

`node scripts/live-run-code-smoke.mjs --hardening --approval token --side "C:\Scratch\side.dwg" --damaged "C:\Scratch\damaged.dwg"`

Repeat with `--approval client` for client approval mode. Keep the DWGs inside
the plugin's allowed import roots; `--damaged` also accepts a directory.
Omitted paths skip their checks; `--no-drawing` skips active-drawing checks
and comparison. Import-root refusals report SKIP with the full policy message.
Place damaged files inside the roots listed there (`CIVIL3D_IMPORT_ROOTS` or
`CIVIL3D_FILE_ROOTS`) to exercise comparison recovery. The summary counts skips separately.

- #7: token mode calls the IPv6 child directly without a token, then with approval from the normal server for the same parameters. The first call must require approval or refuse the host; the second must name `CIVIL3D_HOST`. Neither creates a run folder. Client mode checks host refusal as before. The normal server still runs code afterwards.
- #11: mutation refusals leave DIMSTYLE, dimension scale and DBMOD unchanged; handle lookup and both detached `ReadDwgFile` forms work.
- #12: large saved JSON results have complete contents, matching byte counts and bounded previews; small results stay inline.
- #24: damaged-DWG comparison reports a file-read error within 60 seconds, or SKIP if import-root policy refuses the path; plugin health responds afterwards.

### PR #14 follow-up: file-root refusal names the variable in use (2026-10-12)

`CIVIL3D.PATH_NOT_ALLOWED` now names only the variable that `LoadRoots` reads (the import or export variable when it is set, otherwise `CIVIL3D_FILE_ROOTS`) and still lists the allowed roots.

### Trusted installer checks (#8, #9, #10, 2026-10-12)

Offline results from Jorge's machine on 2026-10-12, measured outside any
sandbox for `jorgegarcias60/installer-hardening`:

| Check | Result |
|---|---|
| `npm run build` | ok |
| vitest | 616 passed (50 files) |
| `docs:check` | ok |
| Plugin builds, net8 and net10 | 0 warnings |
| RunCodeHarness, net8 and net10 | pass |
| `tests/FileBoundaryHarness` (default and `--shared-roots`) | pass; both fail on the old message |
| `install-trusted` check | 0 failures |

Live, Civil 3D 2026, 2026-10-12, integration build of `civil3d-2027` plus PRs #26, #27 and #28, with no root variable set:
- an import outside the roots (`civil3d_compare` drawing) and an export outside the roots (`civil3d_compare` snapshot) were both refused;
- each message listed the allowed roots and named only `CIVIL3D_FILE_ROOTS`;
- nothing was written.

PR #14 itself, live on Civil 3D 2026 (its own live run was on 2027), on the scratch drawing:
- `civil3d_standards style_list` returned names for `profile_view`, `profile_view_band_set`, `section_view` and `section_view_band_set`.
- `createAlignment` from points with the default `addCurves` fitted curves: length 199,914.16 against 200,000 straight. With `addCurves: false` the length equalled the straight distance. Both test alignments were deleted.
- `civil3d_profile view_update` set a user elevation range on a profile view, then restored the original range.
| `npx vitest run --configLoader runner --cache=false` | 602 passed in 49 files |
| `npm run docs:check` | ok |
| Plugin Release net8, refs-2026 | 0 warnings |
| Plugin Release net10, C_References | 0 warnings |
| RunCodeHarness `-f net8.0` | pass |
| RunCodeHarness `-f net10.0` | pass |
| `tests/powershell/install-trusted.check.ps1` | 0 failures |

New installer checks use fake roots and cover:

- #8: all-release and explicit-release uninstall, orphan blocks, empty installs, locked DLLs, short aliases and bracketed paths; user code, backups and encodings across Civil 3D 2024-2027.
- #9: support-path order, reinstall and user edits, normalized and malformed paths, preserved saved loads, cycle and repeated-load guards, and downstream user code through an offline LISP evaluator.
- #10: load failure messages, `C3DMCPLOAD` retry, one automatic attempt per session, startup preservation and LISP balance.

Live, Civil 3D 2026, 2026-10-12, on Jorge's machine. The install held only `R25.1`, with one marked roaming support folder (`enu`). The test used an integration build of `civil3d-2027` plus PRs #26, #27 and #28:
- `-Uninstall` without `-SourceDir` removed the block and `R25.1` (exit 0). The `acaddoc.lsp` held only the block, so it was deleted.
- Install with `-SourceDir` (the net8 build) copied 6 files and wrote the block. A second install copied 0 files and left `acaddoc.lsp` byte-identical.
- Civil 3D started through `Start-Civil3D.ps1`. The block NETLOADed the plugin at drawing open with no security prompt; the plugin answered after 24 s.
- `C3DMCPLOAD` was defined in both open drawings. Running it with the plugin already loaded did not NETLOAD again.
- Not covered live: the failed-NETLOAD message, and chaining (this profile has no other `acaddoc.lsp` on its support path). The offline LISP evaluator covers both.

### PR 4 writes to another open drawing (2026-10-12)

Verified in `jorgegarcias60/run-code-activate-write`, in the
`c3d-pr4-activate` worktree. Changes remain uncommitted. No npm packages were
installed, no plugin was installed, and no Civil 3D live checks were run.

| Check | Result |
|---|---|
| `npm.cmd run build` | Pass. |
| `npx.cmd vitest run --configLoader runner --cache=false` | 602 tests passed in 49 files. |
| Focused tests against pre-PR server and plugin source | Expected failure: 9 failures, 38 passes in 2 files; implementation restored afterwards. |
| `npm.cmd run docs:check` | Pass; generated reference current (210 entries). |
| Plugin Release net8.0-windows, refs-2026 | 0 warnings, 0 errors. |
| Plugin Release net10.0-windows, C_References | 0 warnings, 0 errors. |
| Final net8 rebuild with `--restore` | 0 warnings, 0 errors. |
| RunCodeHarness `-f net8.0` | 181 checks passed; 230 references. |
| RunCodeHarness `-f net10.0` | 181 checks passed; 238 references. |
| `node --check scripts/live-run-code-smoke.mjs` | Pass. |
| `git diff --check` | Pass. |

The plugin builds used the requested commands, with default restore succeeding:

```powershell
dotnet build Civil3D-MCP-Plugin\Civil3DMcpPlugin.csproj -c Release --restore -p:Civil3DReferencesPath=C:\Users\jorge\AppData\Local\Civil3D-mcp\refs-2026
dotnet build Civil3D-MCP-Plugin\Civil3DMcpPlugin.csproj -c Release --restore -p:Civil3DReferencesPath=C:\Users\jorge\PycharmProjects\Civil3D-mcp\C_References -p:TargetFramework=net10.0-windows
# Rebuild net8 with the first command after net10.
```

Both harness runs used the existing local NuGet cache for offline restore, with
`-p:RestoreSources=C:\Users\jorge\.nuget\packages -p:NuGetAudit=false`.
Each received its matching references directory and Release plugin DLL as
positional arguments. The `.cmd` wrappers avoid PowerShell's `npm.ps1` policy
restriction and run the same npm/npx commands without installing dependencies.

Regression checks cover API 4 refusal and API 5 forwarding for document writes,
API 4 reads, the tool description, activation fields, explicit target command
dispatch, and restoration in `finally` inside the host gate. Source checks also
cover the unchanged untargeted `ExecuteAsync(write: false, inCommand: writeMode)`
route, checking the active target before locking or opening a transaction,
checking that the previous drawing is still open, and the separate 30-second
cleanup budget. Started targeted host callbacks are drained on cancellation
before restoration or gate release; this prevents switch-back racing a running
write or a restoration callback. Commit and rollback logic is unchanged.

Live checks are written but unrun. With run-code API 5 or newer and scratch A
active/B open, use `--other <B full path>` together with
`--expect-drawing <A scratch name>` or `--allow-writes`. The script checks a
tagged DBText committed in B (`committed: true`, `drawing: B`, `activated: true`,
`switchedBack: true`), a plain `Doc.Name` read confirming A is active again, a
targeted read finding exactly one tagged text, a failing targeted write rolling
back without changing the count and restoring A, and a targeted cleanup write
erasing both smoke tags. Undo in B is deliberately skipped because checking it
would require activating B. The existing active-drawing undo smoke check remains.
Live behavior, including Civil 3D's actual activation/undo behavior, still needs
verification in the host.


Live, Civil 3D 2026, 2026-10-12, trusted install of this build, with drawing A active and scratch drawing B open:
- `live-run-code-smoke --other <B> ...` passed **52/52**:
  - a write with `document` = B committed in B (activated, switchedBack) and A was active again;
  - a failing write to B rolled back and switched back;
  - a cleanup write left B empty.
- Undo in B was not checked from the script.
- Review (GPT 6.1 Sol, xhigh): one P1, fixed with harness tests (265 checks per framework). A cancel between the queued command and its start could switch back while the command was still pending. Start and cancel are now exclusive, and the switch-back waits for the dispatch.

### PR 3 document reads and no-drawing runs (2026-10-12)

Verified in `jorgegarcias60/run-code-document`; changes remain uncommitted. No plugin was installed and no Civil 3D live checks were run.

| Check | Result |
|---|---|
| `npm.cmd run build` | Pass. |
| `npx.cmd vitest run --configLoader runner --cache=false` | 592 tests passed in 49 files. |
| Focused domain and approval tests | 43 tests passed in 2 files. |
| Focused tests against pre-PR `codeDomain.ts` | Expected failure: 6 failures, 37 passes; implementation restored afterwards. |
| `npm.cmd run docs:check` | Pass; generated reference current (210 entries). |
| Plugin Release net8.0-windows, refs-2026 | 0 warnings, 0 errors. |
| Plugin Release net10.0-windows, C_References | 0 warnings, 0 errors. |
| Final net8 rebuild with `--restore` | 0 warnings, 0 errors. |
| RunCodeHarness `-f net8.0` | 180 checks passed; 230 references. |
| RunCodeHarness `-f net10.0` | 180 checks passed; 238 references. |
| `node --check scripts/live-run-code-smoke.mjs` | Pass. |
| `git diff --check` | Pass. |

The default NuGet restore failed with NU1301 (TLS/security-package failure). Builds and harness restores used the existing local cache with `-p:RestoreSources=C:\Users\jorge\.nuget\packages -p:NuGetAudit=false`. The reference paths and framework overrides match the PR 2 commands below. PowerShell blocks `npm.ps1`, so the equivalent `.cmd` wrappers were used. No Node packages were installed or changed.

The tests cover `document` bounds and forwarding, API 4 refusal before `runCode`, the catalog's `requiresActiveDrawing: false`, and token approval with no drawing. Harness checks cover path normalization, unique and ambiguous names, missing paths listing open drawings, unsaved `Drawing1.dwg`, omitted `.dwg`, the preserved untargeted `ExecuteAsync(write: false, inCommand: writeMode)` route, and `HasDrawing` compilation.

Live checks are written but unrun. With API 4 or later, `--other <second open drawing path>` checks selection by path/name, the response's `drawing`, unchanged active drawing and DBMOD, refusal of a write to the other drawing, and a not-open path listing both drawings. `--no-drawing --side <scratch DWG>` checks a side-only read without `drawing`, the exact `Db` runtime-error message, and `CIVIL3D.NO_DRAWING` for writes; it skips drawing-dependent checks. These require Civil 3D and scratch drawings.

Live, Civil 3D 2026, 2026-10-12, trusted install of this build:
- Drawing A was active and scratch drawing B open. `live-run-code-smoke --other <B> --side <background> --damaged <fixtures> --expect-drawing RUNCODE-SMOKE` passed **50/50**:
  - B was read by full path and by file name while A stayed active;
  - a write with `document` = B was refused ("write mode runs on the active drawing");
  - a not-open path listed the open drawings.
- Then every drawing was closed. `--no-drawing` passed 4/4: an OpenSide-only read was ok with no `drawing` field; touching `Db` gave the clear runtime_error; a write gave NO_DRAWING.
- After reopening A, a read was ok and the working database was A again.
- Review (GPT 6.1 Sol, xhigh): one P2, fixed. A full-path request could match an unsaved `Drawing1.dwg` through the process folder.

### PR 2 run plumbing and script API (2026-10-12)

Verified in the `jorgegarcias60/run-code-plumbing` worktree. Nothing was
installed into Civil 3D. Changes stay uncommitted for review.

| Check | Result |
|---|---|
| TypeScript `npm.cmd run build` | Pass. |
| `npx.cmd vitest run --configLoader runner --cache=false` | 49 files, 588 tests passed. |
| `npm.cmd run docs:check` | Pass. Generated reference current: 210 entries. |
| Plugin Release, net8.0-windows, refs-2026 | Pass. 0 warnings, 0 errors. |
| Plugin Release, net10.0-windows, C_References | Pass. 0 warnings, 0 errors. |
| Rebuild net8.0-windows with `--restore` after net10 | Pass. 0 warnings, 0 errors. |
| RunCodeHarness net8.0 | 164 checks passed. 230 managed references. |
| RunCodeHarness net10.0 | 164 checks passed. 238 managed references. |
| Harness with the pre-PR `ScriptCompiler.cs` and current API | Expected failure: 14 new guard checks fail. |

New checks cover file inputs, BOM removal, size limits, approval after file
edits, execution of the prepared content, read/write budgets, API skew,
hash fallback, and heartbeat timing and suppression. The harness checks
hashes, handles, drawing-content errors and atomic output copies, including
a locked destination. It compiles the new helpers and checks both modes'
plugin-internal guard. The source test checks fresh-database side retries.
The old-compiler check used an isolated project under the harness's ignored
`bin` folder. It proves the new read-helper, plugin-internal and side-database
ownership checks catch regressions in the old guard.

PowerShell blocked the `npm.ps1` wrapper. The `.cmd` wrappers ran the same
commands. No packages were installed and the shared `node_modules` junction
was not changed. Vitest used the requested runner loader and disabled cache.

The first default plugin restore failed with NU1301 (NuGet TLS) and one
NU1900 warning. Local restore succeeded with existing cached packages. For
the offline builds and harness runs, these extra MSBuild properties were used:

```powershell
-p:RestoreSources=C:\Users\jorge\.nuget\packages
-p:RestorePackagesPath=C:\Users\jorge\.nuget\packages
-p:NuGetAudit=false
```

The net8 build used
`-p:Civil3DReferencesPath=C:\Users\jorge\AppData\Local\Civil3D-mcp\refs-2026`.
The net10 build used
`-p:Civil3DReferencesPath=C:\Users\jorge\PycharmProjects\Civil3D-mcp\C_References`
and `-p:TargetFramework=net10.0-windows`. Both used `-c Release`.
Each harness run received its matching references folder and built plugin DLL
as positional arguments. This avoids the harness's net8 default on net10.

Live Civil 3D checks remain pending. This task did not authorize touching
Civil 3D or the installed plugin.

Live, Civil 3D 2026, 2026-10-12, scratch drawing, trusted install of this build:
- Before the install (old plugin, runCode API 1), `copyOutputsTo` was refused with "older than this server" and nothing ran.
- `node scripts/live-run-code-smoke.mjs --approval client --expect-drawing RUNCODE-SMOKE --side <background> --damaged <fixtures> --long 590` passed **46/46**:
  - the 590 s read finished (585 s, 41 progress heartbeats);
  - codePath/argsPath hash matched;
  - copyOutputsTo and keepOutputs:false left only script.cs and run.json;
  - Try, ById, PaperSpace and EnsureLayer twice behaved as described;
  - a Tag/Tagged rerun stayed at 3;
  - plugin-internal calls were refused.
- `OpenSideOrNull` over 11 paths: the real file that broke the original run reported `DwgNeedsRecovery`, a truncated copy `EndOfFile`, a text file `BadDwgHeader` and a missing path an error, while the other real files, a zero-filled copy and the open drawing were read. The run finished ok.
- `live-label-smoke` passed 9/9.

### run_code client approval mode (2026-10-12)

`tests/run_code_approval_mode.test.ts` covers direct client authorization,
token validation, registry and orchestrator isolation, and preview text.
`tests/http_bridge_nesting.test.ts` covers client mode with and without a bridge
token. `npm run build` passed. `npx vitest run --configLoader runner --cache=false`
passed 571 tests in 48 files. `npm run docs:check` passed with 210 entries.
The Vitest config loader needs `runner` here because the shared `node_modules`
junction prevents writes to `.vite-temp`.

| check | command | last result (2026-09-11, `civil3d-2027-support`) |
|---|---|---|
| Node unit tests | `npm test` | 425 passed / 33 files |
| Generated tool reference current | `npm run docs:check` | current (206 entries) |
| Version files in sync | `npm run version:check` | agree on 1.2.1 |
| Plugin compiles (2027 refs) | `.\scripts\build-2027.ps1` | 0 warnings, 0 errors |
| Deploy scripts parse | `[Parser]::ParseFile` on `scripts\*.ps1` | clean |

Baseline before the 2026-09-11 improvement pass: 424 tests; `docs:check` and
`version:check` **failed** on every Windows clone — a line-ending bug in the
checkers, not real drift (fixed in `fix-check-eol`, also in upstream PR #7).

## Live (Civil 3D 2027 open)

| scenario | how | last result |
|---|---|---|
| Startup + health | `npm run test:live-plugin` | connected, plugin 1.2.1.0 |
| Zero documents: fail fast | close all drawings, `npm run test:live-plugin` (has a `noDrawing` step) | `CIVIL3D.NO_DRAWING` in 5 ms |
| Zero documents: gate released on client disconnect | force a 120 s timeout, then `civil3d_health` | `operationInProgress:false`, queue 0 |
| Zero documents: open a drawing | `civil3d_request_approval` for `civil3d_drawing new` (fingerprint `no-active-drawing`), then `new` with the token | `Drawing1.dwg` opened immediately |
| Document open: open another | same, with a drawing active (`new` always uses the `Application.Idle` hop now) | `Drawing2.dwg` opened immediately |
| Live verifier watchdog | run `verify-live-plugin.mjs` against a fake plugin that never answers `getDrawingInfo` | fails in 5 s (aborts the request), not 120 s |
| Gated write round-trip | `civil3d_point create` → `list` → `delete`, each via `civil3d_request_approval` | created/listed/deleted; reused token rejected |

Host-hop recovery (`civil3d-2027` @ `87d6bf1`, installed 2026-09-25, Jorge's machine, Civil 3D 2027 on a blank drawing):

| scenario | how | result |
|---|---|---|
| Normal reads and gated write | `civil3d_drawing info`, `civil3d_surface list`, point create → list → delete | pass |
| LISP prompt at the Command line | COM `SendCommand` of `(getstring "...")`, then `civil3d_drawing info` | call answered after ~5 s through the Idle hop; the prompt was cancelled (`getstring` reports `CMDACTIVE` 0, so the watcher took the fallback; logged, kept until restart) |
| Stuck LISP reader (the original incident) | COM `SendCommand` of an unterminated string, command line at `("_>`, then `civil3d_drawing info` | `CIVIL3D.HOST_BUSY` after 15.0 s (was a 120 s timeout) |
| Recovery once the prompt clears | Civil 3D's automatic save cleared the `("_>` prompt; then drawing info, point list, point create | pass without a restart (previously every call failed until restart) |
| User in a command | 3DORBIT active, then a gated point delete | `CIVIL3D.HOST_BUSY`; the user's command was not interrupted |
| Modal dialog open | "Drawing Recovery" notice open at startup, then drawing info / surface list | **120 s timeouts**: no Idle ticks inside a modal loop. Fixed by the no-Idle watchdog, re-tested below |
| Modal dialog open (no-Idle watchdog, `fix/host-busy-no-idle`, 2026-09-26) | COM `SendCommand` of `_.UNITS` (Drawing Units dialog open), then `civil3d_drawing info` | `CIVIL3D.HOST_BUSY` "a dialog is open ..." after 15.0 s |
| After the dialog closes | Cancel on Drawing Units, then drawing info and surface list | pass at once, on the normal command-context hop (no fallback switch logged) |

The Drawing Units dialog ignored `WM_CLOSE` and a posted `IDCANCEL`; a `BM_CLICK` on its Cancel button took effect only after a delay. Close test dialogs by hand if a script cannot.

Notes: after a rebuild Civil 3D asks "Security - Unsigned Executable File" for the bundle DLL; check the path and answer **Load Once**. A plugin call made through the command-context hop cancels whatever command or prompt is active (AutoCAD's `ExecuteInCommandContextAsync` behaviour).

Before the deadlock fix the zero-document scenarios hung for 120 s per call and
wedged the plugin until Civil 3D restarted (`docs/FINDINGS.md` in
`Joshua8-AI/civil3d-automation` has the original field report). The fix is
`fix-no-document-deadlock` / upstream PR #8.

## Host routes without the command-context hop (`fix/plugin-command-route`, 2026-10-11, live)

Civil 3D 2027 on Joshua8-AI's machine, a copy of an HW drawing (TIN surfaces EG
and EG - No Breaklines). civil3d-driver, a COM automation add-in, was loaded and
sent commands throughout.

### Diagnosis (diagnostic build, not merged)

The build logged command, LISP, lock and quiescence events, and checked whether
a queued `ExecuteInCommandContextAsync` callback runs. Each trial started Civil
3D, checked that a callback ran, sent one trigger, then checked again (6 s).

| Windows session | loaded | trigger | callback after it |
|---|---|---|---|
| disconnected | all add-ins | PowerShell COM `SendCommand("(princ) ")` | never ran |
| disconnected | all add-ins | the plugin's own `C3DMCPHOP` (in-process COM) | never ran |
| disconnected | all add-ins | in-process COM `(princ)` | never ran |
| disconnected | all add-ins | `SendStringToExecute("(princ) ")` x3 | ran each time |
| disconnected | this plugin only, Civil 3D started directly | PowerShell COM `(princ)` | never ran |
| connected (RDP) | all add-ins | PowerShell COM `(princ)` x3 | ran (20-51 ms) |
| connected (RDP) | all add-ins | `C3DMCPHOP` x2, in-process COM, driver `REGEN` x2, PowerShell `REGEN` | ran (12-34 ms) |

- In the wedged state, queued callbacks never start their internal
  `EXECUTEFUNCTION` command.
- Meanwhile `Application.Idle` keeps firing, `CMDACTIVE` is 0, the editor is
  quiescent and the document is unlocked.
- Nothing revived it:
  - waiting;
  - `SendStringToExecute`, `PostCommandPrompt`;
  - more COM commands;
  - `WM_NULL`, focus, injected mouse and key input.

Also found:
- `Editor.SelectImplied` returns an error outside a `UsePickSet` command, even
  when LISP `(ssget "_I")` sees the selection.
- A plugin read under a document lock empties the redo stack.

### This branch, live (Windows session disconnected)

| check | result |
|---|---|
| read right after PowerShell COM and driver commands | 0.11 s |
| 30 rounds of read + write, COM or driver command every few rounds | all ok; median 0.07 / 0.10 s, max 0.41 s; 30 alignments |
| three reads, then command-line `UNDO 1` | removed the last write: reads add no undo step |
| write, `undoDrawing`, `redoDrawing`, `undoDrawing` | removed, restored, removed |
| `computeSurfaceVolume`, `calculateSurfaceVolume`, `qtySurfaceVolume` | ok; no scratch surface left in the drawing |
| LISP `sssetfirst` of all alignments, then `getSelectedCivilObjectsInfo`, `getProjectContext` | selection reported; still selected afterwards |
| LISP `getstring` prompt open: read, then write | `HOST_BUSY` after 15 s each; the prompt kept waiting |
| `LINE` waiting for a point: read | `HOST_BUSY` after 15 s; `LINE` still active |
| Esc, then read or write | 0.1 s |

The same design in the Joshua8-AI fork (`civil3d-2027-support` 00b4bbd) passed
further runs in both session states:
- 80 requests from 8 concurrent clients;
- a client disconnecting mid-request;
- a modal dialog;
- jobs, plot, publish and xref operations;
- two drawings and zero drawings;
- a 200-request soak (median 0.12 s).

### Live on Civil 3D 2027 — 2026-09-26 (plot, xref, compare, bridge reads)

Plugin from `civil3d-2027-support` at `35b5600`, installed with
`build-2027.ps1 -Install`. Calls went straight to the plugin on 8757 through
the repo's own `build/utils/ConnectionManager.js` (the approval gate lives in
the Node server and is covered offline). Drawings: copies of the 2027 Civil
Tutorials `Pipe Networks-1A.dwg` (meters, NH83; 2 TIN surfaces, 56 parcels),
`Pipe Networks-3.dwg` (feet; 1 gravity network, 11 pipes / 12 structures) and
`Plan Production-Plan Profile Sheets-Create.dwg` (2 layouts, 6 page setups),
placed under Documents (the default file root).

| scenario | result |
|---|---|
| `npm run test:live-plugin`, zero documents | health OK; `CIVIL3D.NO_DRAWING` in 6 ms |
| `getDrawingUnits` (meters drawing / feet drawing) | `Meters` / `Feet`, `unitsConsistent: true` |
| `getSurfaceTinVertices` EG (23,092 vertices) | full set in 163 ms (1.07 MB); `maxPoints: 5` → even stride, `truncated: true` |
| `getParcelGeometry` rectangular lot / lot with an arc | `computedArea` = `area` to 0.001 m²; `geometrySource: baseCurve:Polyline` |
| `getParcelGeometry` road ROW (parcel with holes) | outer loop only: 77,140 vs 20,913 m², flagged in `notes` — **known gap** |
| `getPipeNetwork` Pipe Networks-3 | **was failing** (`Retrieve attribute failed` on network style / parts list); fixed in `35b5600`: 11 pipes + 12 structures in 131 ms, `style`/`partsList` null, all new pipe fields filled, inverts match `civil3d_compare` |
| `plotListLayouts` / `plotListPageSetups` / `plotListPlotters` | 2 layouts / 6 setups / 19 devices (2.4 s for the device list) |
| `plotLayoutsToPdf` Letter layout on DWG To PDF.pc3 | refused up front with a `paperSize` hint (no stuck `-PLOT`) |
| `plotLayoutsToPdf` `allLayouts` + `paperSize: "ANSI A (11.00 x 8.50 Inches)"` | 2 PDFs in 1.6 s; Layout2 content checked visually |
| `plotPublishSheetSet` right after plotting | `CIVIL3D.CONFLICT` (plotting marks the drawing modified) |
| `plotPublishSheetSet` `requireSaved: false`, `keepDsd: true` | 2-sheet PDF in 3.1 s; generated DSD accepted by 2027 `-PUBLISH` |
| `listXrefs` / `overlayXref` (relative) / `unloadXrefs` / `detachXrefs` | correct statuses and stored `.\Parcel-1A.dwg` |
| `reloadXrefs`, `bindXrefs` | **were hanging to the 120 s timeout** (work done, control never returned); fixed in `7a7fac9` via `-XREF`: reload 80 ms, repath 95 ms, bind (insert) 209 ms |
| `writeDrawingSnapshot` / `compareDrawingSnapshot` | 28 KB fingerprint in 124 ms / diff in 336 ms |
| `compareDrawings` against a DWG open in another tab | side database read, 376 ms |
| `listDataShortcutReferences` | empty list, working folder reported |

Not exercised live: `data_shortcut_repair` / `promote` / `sync` (the tutorial
drawings have no data-shortcut references), `asJob` plotting, overwrite and
path-boundary refusals, zero-document plot calls, grid/volume surfaces for
`getSurfaceTinVertices`, US-survey-foot drawings for `getDrawingUnits`.

Autodesk's own AutoCAD MCP (`AutoCAD-MCP-Server-2027.bundle`, not ours):
its `MCPHTTPSTART` command exists only when `ACMCP_TOOLS_CONFIG_FILE` points at
a config with `EnableHttpServer: true`, and then fails with
`FileNotFoundException: ModelContextProtocol.AspNetCore 0.4.0.0` — the 2027
bundle does not ship its HTTP host, so it cannot be registered as an external
MCP server.

### Live checklist: `civil3d_plot` (feature/plot-publish)

Offline only so far: plugin compiles against 2027 refs, vitest covers schemas,
approval classification, routing and job registration, and the FileBoundary
harness covers the directory lock used while the plotter writes (2026-09-26 on
the branch: `npm test` 437 passed / 34 files, `docs:check` current at 207
entries, `build-2027.ps1` 0 warnings / 0 errors). Live results from
2026-09-26 are in the table above; rows still marked pending were not run:

| scenario | how | last result |
|---|---|---|
| Discovery | `civil3d_plot` `list_layouts`, `list_page_setups`, `list_plotters` (with and without `device: "DWG To PDF.pc3"`) | passed 2026-09-26 |
| Zero documents | close all drawings, call `list_layouts` and `plot_layouts_to_pdf` | pending (expect `CIVIL3D.NO_DRAWING` fast) |
| Plot one layout | approval, then `plot_layouts_to_pdf` with `layoutNames: [..]`, `outputPath` under Documents | pending |
| Plot all layouts | `allLayouts: true`, `outputDirectory`; check per-layout bytes/pageCount, CTAB and BACKGROUNDPLOT restored | passed 2026-09-26 (2 layouts, 1.6 s) |
| Page setup / paper override | `pageSetup`, then `paperSize` + `plotStyleTable: "monochrome.ctb"` | pending |
| Overwrite guard | repeat without `overwrite` (expect `CIVIL3D.CONFLICT`), then with `overwrite: true` | pending |
| Path boundary | `outputDirectory` outside export roots (expect `CIVIL3D.PATH_NOT_ALLOWED`, nothing plotted) | pending |
| Prompt-chain drift | a layout whose paper is not on DWG To PDF.pc3 (expect up-front `INVALID_INPUT`, no stuck `-PLOT`) | passed 2026-09-26 |
| Publish multi-sheet | save drawing, `publish_sheet_set` with 2+ layouts; confirm page count and order; `keepDsd: true` to inspect the DSD | passed 2026-09-26 (2 sheets, DSD accepted by 2027) |
| Publish unsaved guard | modify drawing, `publish_sheet_set` (expect `CIVIL3D.CONFLICT`) | passed 2026-09-26 (plotting itself marks the drawing modified) |
| As job | `asJob: true`, poll `civil3d_job status`, cancel mid-batch | pending |

## Xrefs, data-shortcut references, drawing comparison (2026-09-26, `feature/xref-datashortcut-compare`)

Offline, on the branch:

| check | command | result |
|---|---|---|
| Node unit tests (adds `xref_domain`, `data_shortcut_references`, `compare_domain`) | `npm test` | 440 passed / 36 files |
| Generated tool reference current | `npm run docs:check` | current (208 entries) |
| Version files in sync | `npm run version:check` | agree on 1.2.1 |
| Plugin compiles (2027 refs) | `.\scripts\build-2027.ps1` (no `-Install`) | 0 warnings, 0 errors |
| Fingerprint diff harness | `npm run test:compare-diff` | passed |
| P1/P2/P4 harness still green | `npm run test:p2-boundaries` | passed |
| Startup smoke | `npm run test:startup` | 210 registered tools |

**Partly verified live on 2026-09-26** (see the live table above: list,
overlay, unload, reload, repath, bind, detach, compare, snapshot). Still to
run with the plugin loaded and `CIVIL3D_IMPORT_ROOTS` /
`CIVIL3D_EXPORT_ROOTS` covering the test folders:

- `civil3d_xref list` on a drawing with one attached, one overlaid, one
  unloaded, one missing (renamed source), and one nested xref whose parent
  is unloaded. Expect `loaded`, `unloaded`, `not_found`, and `orphaned`,
  with correct `savedPath` and `foundPath`. Check the parent resolution by
  native pointer (`GraphNode.In()` returns an untyped wrapper).
- `attach` and `overlay` with `pathType: relative` on a saved host, then
  `unload`, `reload`, `repath` to a copied file, `bind` with `insert`, and
  `detach`, each through `civil3d_request_approval`. Confirm that the
  no-transaction path (`ExecuteLockedWithoutTransactionAsync`) does not raise
  `eInvalidOpenState` and does not wedge the host gate.
- `civil3d_project data_shortcut_references` on a drawing with current, stale
  (edit and save the source), and broken (rename the source) references.
  Then `data_shortcut_repair` to the renamed source. Check that
  `AeccDataShortcutMgd` resolves (it is loaded on the host thread), that
  `workingFolder` and `currentProjectFolder` are filled, and that `repaired`
  is true.
- `data_shortcut_promote`: check whether `_AeccPromoteReference` honours the
  pickfirst set or prompts for the object. The command name comes from the
  CUIx, but its prompt sequence has not been verified. Check
  `data_shortcut_sync` with `_AeccSynchronizeReferences`.
- `civil3d_compare drawing` against the drawing's own `.bak` or a previous
  submittal copy, including one that is currently open in another document
  (this uses the `OpenTryForReadShare` fallback). Then `snapshot` followed by
  `compare_snapshot` on an unchanged drawing: expect `identical: true`. Time a
  large drawing, because the whole comparison runs on the host thread under
  the gate.

## Bridge-support reads (2026-09-26, `feature/bridge-support-commands`)

`getSurfaceTinVertices`, `getParcelGeometry`, `getDrawingUnits`, and the new
pipe fields on `getPipeNetwork`. Offline, on the branch:

| check | command | result |
|---|---|---|
| Node unit tests (adds `bridge_support_commands`) | `npm test` | 467 passed / 38 files |
| Generated tool reference current | `npm run docs:check` | current (209 entries) |
| Version files in sync | `npm run version:check` | agree on 1.2.1 |
| Plugin compiles (2027 refs) | `.\scripts\build-2027.ps1` (no `-Install`) | 0 warnings, 0 errors |
| Arc, area, chaining, decimation and unit math | `npm run test:bridge-math` | passed |
| Other harnesses still green | `npm run test:compare-diff`, `npm run test:p2-boundaries` | passed |
| Startup smoke | `npm run test:startup` | 211 registered tools |

The Civil 3D API members used were checked against the 2027 reference
assemblies (metadata only): `TinSurface.GetTriangles(bool)`,
`TinSurfaceTriangle.Vertex1..3.Location`, `GridSurface.GetVertices(bool)`,
`Parcel.BaseCurve`/`GetGeCurve`/`Explode`, `Pipe.InnerHeight`/`OuterDiameterOrWidth`,
`SettingsUnitZone.DrawingUnits`/`ImperialToMetricConversion`.

**Mostly verified live on 2026-09-26** (see the live table above, and the
bridge end-to-end run in civil3d-automation `bridge/TESTING.md`). Still open:

- `getSurfaceTinVertices` on a TIN surface with an outer boundary: check that
  `totalVertexCount` excludes points outside the boundary, that a `boundary`
  polygon clips, and that `maxPoints: 100000` on a surface with more vertices
  returns `truncated: true`, the same subset on a second call, and a response
  under 8 MiB. Time it on a surface with over 1 million vertices (it walks
  every visible triangle on the host thread). Try a grid surface (DEM) and a
  TIN volume surface (expect `CIVIL3D.INVALID_INPUT`).
- `getParcelGeometry` on a rectangular lot, a lot with a curved frontage, and
  a parcel with a curve on its back line. Record `geometrySource`: whether
  `Parcel.BaseCurve` works or the `GetGeCurve`/`Explode` fallbacks are needed.
  `computedArea` should match `area` (a note appears when they differ by more
  than 0.1%). Check the sign of the bulges against the drawing, and check a
  parcel with an interior hole: only the outer loop is expected.
- `getDrawingUnits` on drawings with INSUNITS = Feet (2), USSurveyFeet (21),
  Meters (6) and Undefined (0), and on a Feet drawing whose Civil 3D
  Drawing Settings use the US survey foot (expect `unitsConsistent: false`
  and a warning).
- `getPipeNetwork`: compare `startPoint`/`endPoint` with the pipe's grips and
  `startInvert`/`endInvert` with the inverts shown in Pipe Properties,
  including an elliptical or box pipe (`innerHeight` differs from
  `innerDiameter`).
- Bridge end to end: `bridge_status` should report the three commands as
  `available`; `bridge_create_toposolid sampling: "tin"` and the pipe and
  parcel tools should use the new data.

## cubic review fixes ported from upstream PRs #17-#19 (2026-09-26, offline only)

Ported from the review-fix commits on `upstream/plot-publish`,
`upstream/xref-datashortcut-compare` and `upstream/bridge-support-reads`.
None of these have been re-run live.

- **Plot/publish (#17):** the plotter and `-PUBLISH` write to a hidden,
  unpredictable temp name in the locked output folder; it is checked to be a
  regular file and renamed over the final name (`FileBoundary.BeginExternalWrite`),
  so a link planted at the final name after validation cannot redirect output,
  and a failed plot no longer deletes an existing PDF. `RunCommandAsync` refuses
  to start a command while one it drove earlier is still at a prompt (the queued
  Ctrl-C only runs after the host work returns). `publish_sheet_set` skips (with
  a warning) or rejects never-initialized layouts. Whitespace-only `pageSetup`,
  `paperSize`, `plotStyleTable` and `device` are rejected by the schema.
- **Data shortcuts / compare (#18):** unreadable reference health is `unknown`,
  not `current`; repair/promote prefer a reference over a same-named local object;
  `view_frame_group` reaches `data_shortcut_repair`; `civil.truncated`; malformed
  snapshots are `INVALID_INPUT`; block attributes and TIN/grid triangle count and
  areas are in the fingerprint.
- **Bridge reads (#19):** inner height falls back to `InnerDiameterOrWidth` only
  for circular pipes (otherwise null heights/inverts); the units/info reads return
  the raw coordinate-system code without a library lookup. The same defensive
  inner-height read was applied to the `civil3d_compare` pipe-network fingerprint,
  where a throwing `InnerHeight` dropped the whole network.

| check | result |
|---|---|
| `npm run build` | ok |
| `npm test` | 472 passed / 38 files |
| `npm run docs:check` | current (209 entries) |
| `npm run version:check` | agree on 1.2.1 |
| `npm run test:startup` | 211 tools |
| `test:p2-boundaries`, `test:compare-diff`, `test:bridge-math` | passed |
| `.\scripts\build-2027.ps1` (no `-Install`) | 0 warnings, 0 errors |

Live checks still to do: plot and publish to a fresh and an existing PDF
(temp file renamed, no `.mcp-tmp.pdf` left behind); publish with a
never-opened layout; `civil3d_compare` on a network with a non-circular pipe.

## Profile view, layer and K-value fixes (2026-09-28, offline only)

Three bugs found live on Civil 3D 2027: `civil3d_profile view_create` always
failed (reflection probed `ProfileView.Create` overloads that do not exist; now
the typed 2027 overload, with first-style/band-set fallback and a new `layer`);
a missing requested layer silently became the current layer (now created, all
10 `GetLayerId` callers are inside `CivilExecution.WriteAsync`); and
`check_k_values` used A as a decimal and a metric-only table (now
K = L / (100·|g2−g1|) against AASHTO SSD tables in mph or km/h, `speedUnits`
defaulting from the drawing units). `get` also reported symmetric parabolas as
asymmetric; fixed. The band-set lookup, which always returned a null id, was
fixed along the way.

| check | result |
|---|---|
| `npm run build` | ok |
| `npm test` | 480 passed / 39 files (new `tests/profile_domain.test.ts`, 8 tests) |
| `npm run docs:check` | current (209 entries) |
| `npm run version:check` | agree on 1.2.1 |
| `npm run test:startup` | 211 tools |
| `npm run test:vertical-curve-math` (new harness) | passed |
| `test:p2-boundaries`, `test:compare-diff`, `test:bridge-math` | passed |
| `.\scripts\build-2027.ps1` (no `-Install`) | 0 warnings, 0 errors |

**Live verification pending** (needs the new DLL installed, so Civil 3D must be
closed first):
- `view_create` with no style/band set, with named ones, with an unknown style
  (now `CIVIL3D.INVALID_INPUT`, see below), and with `layer` set to a new
  layer; check name, handle, layer and style in the result and in the drawing.
- `create_layout layer:"C-ROAD-DES"` on a drawing without that layer: the layer
  is created and the profile is on it; an invalid name (e.g. `A<B`) is rejected.
- `check_k_values` on the 280 ft, −1.4 % → +1.4 % sag: K = 100, required 96 at
  50 mph (feet drawing, default mph); 52 mph uses the 55 mph row with a note;
  25 mph is rejected; `speedUnits:"km/h"` on the feet drawing converts lengths.
- `get` on a profile with an `add_curve` symmetric parabola reports
  `symmetric_parabola`.
- `view_band_set` now imports the named band set (it previously got a null id).

## Style lookups and plot-to-final-path (2026-09-28, offline only)

Two more bugs found live on Civil 3D 2027. Style names: `create_layout` style
"Design Profile" produced an "Existing Ground Profile" profile and
`view_create` style "Profile View" warned the style was missing. Every style
name read as null on 2027 (StyleBase's set-only `Name` hides the readable base
`Name`), so no named lookup matched and each fell back to the first style. Now
names are read through `DBObject.Name`, the reflection boundary resolves the
readable declaration, and an unknown name is `CIVIL3D.INVALID_INPUT` listing
the available names. Plot: the "open in viewer when done" option opened the
hidden temp name the plotter had been given; the plotter now writes the final
path (see `FileBoundary.BeginExternalWrite` for the link protections and the
backup/restore on failure).

| check | result |
|---|---|
| `npm run build` | ok |
| `npm test` | 480 passed / 39 files |
| `npm run docs:check` | current (209 entries) |
| `npm run version:check` | agree on 1.2.1 |
| `npm run test:startup` | 211 tools |
| `npm run test:style-lookups` (new harness) | 28 checks passed |
| `npm run test:p2-boundaries` (external-write checks rewritten: fresh, overwrite, failed/partial/rejected output restores the old file, planted hard link, junction, symbolic link) | passed |
| `test:compare-diff`, `test:bridge-math`, `test:vertical-curve-math` | passed |
| `.\scripts\build-2027.ps1` (no `-Install`) | 0 warnings, 0 errors |

**Live verification pending** (needs the new DLL installed):
- `create_layout` / `create_from_surface` with style "Design Profile": the
  result's `style` and the drawing both say "Design Profile"; a misspelled
  style is `CIVIL3D.INVALID_INPUT` listing the styles.
- `view_create` style "Profile View": no warning, the view uses that style.
- `plot_layouts_to_pdf` and `publish_sheet_set` with "open in viewer when done"
  on: the viewer opens the real PDF. Repeat with `overwrite: true` over an
  existing PDF (replaced, no `.*.mcp-bak` left behind) and with the existing
  PDF held open by a viewer that locks it (clear `FILE_IO_ERROR`, old PDF kept).

## Volume to datum, plot area / Model tab / orientation, alignment radii (2026-10-10, live)

Upstream: #22 (`volume_to_datum`), #17 (three commits added), and per-PI `radii`
(built on jorgegarcias60's #12; not submitted). Offline on this branch: `npm test`
492 passed / 41 files, `docs:check` current (209 entries), `version:check` 1.2.1,
`build-2027.ps1` 0 warnings / 0 errors.

Live on Civil 3D 2027 with this build, against a copy of the HW 03 drawing (feet;
TIN surface EG, 1,688 points, two hide boundaries; five ARCH D 36 x 24 layouts):

| scenario | result |
|---|---|
| `calculateSurfaceVolumeToDatum` EG, 200 x 200 ft square, datum 740 / 760 / 790 | fill 939,719 / 139,719 / 0, cut 0 / 0 / 1,060,281 ft^3; a 1 ft sampled integral of `FindElevationAtXY` agrees within 0.4 ft^3 |
| same, self-crossing boundary | `CIVIL3D.INVALID_INPUT` (Civil 3D throws `ArgumentException`) |
| `plotLayoutsToPdf` default area, ARCH D 36 x 24 at rotation 0 | 36 x 24 landscape page (was 24 x 36 portrait, cut off, before the orientation fix) |
| same, ARCH D 24 x 36 at rotation 90 | 36 x 24 landscape page; `list_layouts` reports both as landscape |
| `plotArea` `extents`, `window` [0,0,18,12] | plotted; the window page shows the lower-left quarter of the sheet |
| Model, ANSI B, `extents` / `window` | whole drawing / the window, fit and centred |
| layout + Model in one batch | 2 PDFs |
| Model with `plotArea: "layout"`; `window` without corners | `CIVIL3D.INVALID_INPUT`, nothing plotted |
| `createAlignment` radii [300, null] / [300, 200] / [300] / curveRadius 250 | curves R300 / R300 + R200 / R300 / R250 at both PIs, read back from the alignment entities |
| radii with curveRadius; radii [0]; radius too big for its tangents | `INVALID_INPUT`; `INVALID_INPUT`; reported in `curveFailures`, the PI stays sharp |

The hang seen during this run (every call stuck after the civil3d-driver used
the command line) is fixed below.

## Command-context wedge: cause and fallback route (2026-10-10, live)

Cause, reproduced on Civil 3D 2027 with the plugin alone: after Civil 3D
started and a drawing was open, one COM `SendCommand("(princ) ")` from another
process (PowerShell) was enough in 3 of 3 runs that day (but see 2026-10-11
below: it is not deterministic). From then on no `ExecuteInCommandContextAsync`
callback ran, not even one queued from the main thread, and every plugin call
hung holding the host gate until Civil 3D restarted. Meanwhile
`Application.Idle` kept firing (373 ticks in 3 s) and `CMDACTIVE` was 0. None of
these brought the route back: `WM_NULL` every 250 ms for 10 s, `WM_MOUSEMOVE`,
a Shift key down/up, another COM command, or switching the active document
away and back. jorgegarcias60/Civil3D-mcp#1 hit the same state after a LISP
prompt left open over COM.

Fix on `fix/command-context-wedge`: the Idle watcher from jorgegarcias60#1/#2,
reads from the Idle tick with an `OpenCloseTransaction`, and writes and command
sequences inside the plugin's `C3DMCPHOP` command, started over COM. Offline:
`npm test` 496 passed / 42 files (new `host_route_fallback.test.ts`),
`docs:check` current, `build-2027.ps1` 0 warnings / 0 errors.

Live, on a copy of the HW 03 drawing:

| scenario | result |
|---|---|
| before the wedge: read, `createAlignment` U-1, plots, `list_layouts` | normal route (`hostRoute: commandContext`), unchanged results |
| one COM `SendCommand("(princ) ")`, then `getDrawingInfo` | answered in 5.1 s (the grace period); `hostRoute: fallback` from then on |
| further reads (`getDrawingInfo`, `listSurfaces`, `calculateSurfaceVolumeToDatum`) | 0.1 s each, same values as before the wedge |
| `createAlignment` U-2 | created through `C3DMCPHOP` in 0.1 s |
| `plotLayoutsToPdf` (a `-PLOT` sequence) | plotted through `C3DMCPHOP` in 1.2 s, landscape |
| `UNDO 1`, repeated | undid the plot step, then U-2, then U-1 (made before the wedge): the undo history survived the Idle reads and the edits are undoable |
| a LISP `getstring` prompt left open over COM, then a write | `CIVIL3D.HOST_BUSY` after 15.0 s; the prompt kept waiting, nothing was typed into it |
| Esc, then the same write | created in 32 ms |

Not run live: xref changes (`ExecuteLockedWithoutTransactionAsync`) in the
fallback route, `asJob` operations in the fallback route, a modal dialog in the
fallback route (covered by jorgegarcias60#2 before the wedge).

## Command-context wedge: the trigger is not deterministic (2026-10-11, live)

Re-run while porting the fix to jorgegarcias60/Civil3D-mcp#4 and upstream #23.
The PowerShell `SendCommand("(princ) ")` did not wedge the hop in 3 tries, on
either build. Commands sent by the civil3d-driver (`REGEN`, `UNDO 1`) wedged it
within one or two commands in three Civil 3D sessions (this fork's build and
jorgegarcias60's), and not at all in two others. On jorgegarcias60's branch with
the fix, after the wedge: reads 0.1 s, two `createAlignment` writes through
`C3DMCPHOP`, both undone by `UNDO 1` twice, and an open LISP `getstring` prompt
got `HOST_BUSY` after 15.1 s with nothing typed into it.

## Host routes without the command-context hop (2026-10-11, live)

### Diagnosis

A diagnostic build (local branch `diag/wedge`, never merged) logged command,
LISP, lock-mode, quiescence and modal events, and probed whether a queued
`ExecuteInCommandContextAsync` callback runs. Each trial started Civil 3D, checked
that a probe runs, sent one trigger, and probed again (6 s).

| trial | Windows session | loaded | trigger | probe after it |
|---|---|---|---|---|
| A1 | disconnected | all four bundles | PowerShell COM `SendCommand("(princ) ")` | never ran |
| A2 | disconnected | all four bundles | the plugin's own `C3DMCPHOP` (in-process COM `SendCommand`) | never ran |
| A3 | disconnected | all four bundles | in-process COM `(princ)` | never ran |
| A4 | disconnected | all four bundles | `SendStringToExecute("(princ) ")` x3 | ran every time |
| A5b | disconnected | this plugin only, Civil 3D started directly | PowerShell COM `(princ)` | never ran |
| A6 | connected (RDP) | all four bundles | PowerShell COM `(princ)` x3 | ran (20-51 ms) |
| A7 | connected (RDP) | all four bundles | `C3DMCPHOP` x2, in-process COM, driver `REGEN` x2, PowerShell COM `REGEN` | ran (12-34 ms) |

- Each callback runs as an internal `EXECUTEFUNCTION` command. In the wedged
  state, queued callbacks never start it.
- Meanwhile Idle keeps firing, `CMDACTIVE` is 0, the editor is quiescent, the
  document is unlocked, and the context is application context. Nothing in the
  state shows the wedge.
- Nothing revived it after the first COM command:
  - waiting;
  - `SendStringToExecute`, `PostCommandPrompt`;
  - more in-process or external COM commands, `C3DMCPHOP`;
  - `WM_NULL`, focus, injected mouse and key input.
- So in a disconnected session, the first COM `SendCommand` from any client
  disables the route for the rest of the session. That is why it looked
  intermittent: it depends on whether anyone is connected to the session.
  - In the earlier "0 of 3" runs, the PowerShell helper often found no
    `ActiveDocument`, so nothing was sent.
  - Not tested: a session that disconnects while Civil 3D runs.

Also found while testing:
- `Editor.SelectImplied` returns an error outside a `UsePickSet` command, even
  when LISP `(ssget "_I")` sees 30 objects.
- `SendStringToExecute` input queued inside a COM-started command mixes with
  that command's input: `UNDO` received "C3DMCPHOP" as its count.
- A plugin read under a document lock empties the redo stack.

### The new routes, stress-tested

Build `fix/command-route-only`. The civil3d-driver was loaded and sent commands
throughout. Run in a disconnected session and again in a connected one: PASS
both times.

| check | result |
|---|---|
| read right after PowerShell COM and driver commands | 0.09-0.32 s |
| 30 rounds of read + write, COM or driver command every few rounds | all ok; median 0.07-0.10 s, max 0.60 s; 30 alignments |
| three reads, then command-line `UNDO 1` | removed the last write (reads add no undo step) |
| write, `undoDrawing`, `redoDrawing`, `undoDrawing` | removed, restored, removed |
| `computeSurfaceVolume`, `calculateSurfaceVolume`, `qtySurfaceVolume`, `calculateSurfaceVolumeToDatum` | ok; no scratch surface left in the drawing |
| `plotLayoutsToPdf` C-101, C-102 and Model in one call | 3 PDFs |
| xref attach, reload, unload, reload, bind | ok (0.1-0.5 s); xref gone after bind |
| LISP `sssetfirst` of all alignments, then `getSelectedCivilObjectsInfo`, `getProjectContext` | selection reported; still selected afterwards |
| LISP `getstring` prompt open: read, then write | `HOST_BUSY` after 15 s each; the prompt kept waiting |
| `LINE` waiting for a point: read | `HOST_BUSY` after 15 s; `LINE` still active, not cancelled |
| Esc, then read or write | 0.1 s |

Second set (disconnected session):

| check | result |
|---|---|
| Drawing Units dialog open, then a read | `HOST_BUSY` "a dialog is open" after 15 s; after closing it (and the lighting notice it raises), a read took 0.24 s |
| 8 concurrent clients x 5 read/write pairs | 80/80 ok, 40 alignments; gate idle afterwards |
| client killed while its write waited behind `LINE` | the write never ran; the next write took 0.1 s |
| `startJob` `plot_layouts_to_pdf` | job completed |
| `plotPublishSheetSet` (`requireSaved=false`) | PDF written, 3.4 s |
| second drawing opened | writes go to the active drawing; switching back, a COM command, then a write: ok |
| soak: 100 rounds of read + write, COM command every 10th | 200/200 ok; median 0.12 s, p95 0.30 s, max 0.40 s |
| all drawings closed, read; then `newDrawing` and a write | `NO_DRAWING` in 0.07 s; new drawing, write ok |

Plugin-only session, Civil 3D started directly, connected: 40 requests with COM
commands in between, all ok, median 0.10 s.

## In-process C#: `civil3d_run_code` (2026-10-11, `jorgegarcias60/run-code`)

Offline, 2026-10-11:

| check | command | result |
|---|---|---|
| Node unit tests | `npm test` | 506 passed / 43 files (new: `tests/run_code_domain.test.ts`; boundary tests updated for `RunCodeLog.cs` and `RunCodeAssemblies.cs`) |
| Generated tool reference | `npm run docs:check` | current (210 entries) |
| Plugin, 2026 refs (net8) | `dotnet build ... -p:Civil3DReferencesPath=<refs-2026>` | 0 errors; output has `roslyn\Microsoft.CodeAnalysis*.dll` and no Roslyn or System.Collections.Immutable next to the plugin DLL |
| Plugin, 2027 refs (net10) | `dotnet build ... -p:TargetFramework=net10.0-windows` | 0 errors, same layout |
| Compiler harness, 2026 | `npm run test:run-code-compiler` | 24/24 pass (230 references) |
| Compiler harness, 2027 | `dotnet run --project tests/RunCodeHarness -f net10.0 -- <2027 refs> <net10 plugin DLL>` | 24/24 pass (238 references) |

The harness compiles real snippets against the Civil 3D reference assemblies:
the 2026-10-11 obstacle-sampling script, the Entity/Surface aliases, compile
errors on the right snippet line (syntax, semantic, hoisted `using`), and the
guard: read mode refuses `OpenMode.ForWrite`, `UpgradeOpen`/`Erase`,
`AppendEntity`, regular transactions, `Database` setters and Civil 3D `Add`,
allows open-close transactions and entities built in memory; both modes refuse
Editor prompts, alert dialogs and event subscriptions. A net8 harness against
the 2027 (net10) references fails with CS1705 by design: run it on net10.

Live, Civil 3D 2026, 2026-10-11 (`node scripts/live-run-code-smoke.mjs --side <10 MB background>
--expect-drawing RUNCODE-SMOKE` on a scratch copy of the test job): **20/20 passed** on the build with
the review fixes (18/18 on the first build). Highlights:
- Civil 3D's own Roslyn 4.10 loaded from the AutoCAD folder (the default context resolves it; the
  bundle's `roslyn\` fallback and the resolver were not needed).
- First compile 1.4-1.7 s (cold Roslyn), later ones 30-300 ms.
- A read leaves DBMOD at 0.
- A 3 s budget ends `while (true) Check();` at 3.0 s, and Civil 3D answers the next call.
- The 96,913-object background read as a side database and sampled into 1,516,406 points in 1.08-1.15 s.
- A write plus one `civil3d_drawing undo` leaves nothing.
- A write that throws, or that catches its own timeout, is rolled back (with the warning that changes
  outside the transaction are not).
- A `Dictionary<ObjectId,...>` result has handle keys.
- Civil 3D asked "Security - Unsigned Executable File" for `Civil3DMcpPlugin.dll` at every start (the
  `%APPDATA%` bundle folder is not trusted); answered Load Once.

Trusted-folder install (`scripts/install-trusted.ps1`, Civil 3D 2026, 2026-10-11): the plugin copied to
`C:\CAD-Tools\plugins\Civil3DMcp\R25.1` (under TRUSTEDPATHS) and NETLOADed from the `acaddoc.lsp` block
loaded with no security prompt. A cold start through CAD-Tools `Start-Civil3D.ps1` (Explorer launch, the
script opens a drawing on the Start tab, retrying until COM answers) was plugin-ready in 22 s. The
smoke test passed 21/21, and the real 96,913-object job gave the identical 547,441 points in 1.38 s
(`node scripts/run-code-file.mjs`). Ruled out first:
- the bundle folder as a junction into the trusted folder: still prompted;
- an absolute `ModuleName` in `PackageContents.xml`: ignored;
- a registry demand-load entry (HKCU Applications, LOADCTRLS 2): never run;
- the registry Startup Suite (`Dialogs\Appload\Startup`): never run.

Review fixes (Codex review of PRs #5/#6, verified finding by finding; 2026-10-11), offline:

| check | command | result |
|---|---|---|
| Node unit tests | `npx vitest run` | 558 passed / 46 files (new: `socket_client_limits`, `plugin_auth`, `connection_manager`, `powershell_scripts`, `http_bridge_nesting`; extended `run_code_domain`, `http_bridge`, `socket_client`, `security_boundary`) |
| Generated tool reference | `npm run docs:check` | current (210 entries) |
| Plugin, 2026 refs (net8) and 2027 refs (net10) | `dotnet build` as above | 0 warnings, 0 errors |
| Compiler and runtime harness, net8 and net10 | `npm run test:run-code-compiler`, `-f net10.0` | 114/114 pass on both |
| Installer check, Windows PowerShell 5.1 | `powershell -File tests\powershell\install-trusted.check.ps1` | 55/55 (the new checks fail on the old installer) |
| Launcher (fake npm/node in a temp copy) | `npx vitest run tests/powershell_scripts.test.ts` | 8/8: install before build when the lock changes, `--no-save` keeps the lock, no npm call when fresh, two concurrent starts give one build and both start node, a held lock is waited for only when a build is needed |

New harness checks: read mode refuses `++`/`--`, compound, deconstruction and parenthesized
setters on host properties and numeric `OpenMode` constants, and still allows reads in tuples and
`++` on locals and in-memory entities; both modes refuse `ShowDialog` (WinForms, WPF, AutoCAD
dialogs), `TaskDialog`, WinForms and WPF `Application.Run`, `Dispatcher.PushFrame`/`Run`, VB
`InputBox`/`MsgBox`, `Editor.DoPrompt`, `ShowModal` (AutoCAD `OpenFileDialog`, `ColorDialog`),
`TaskDialog.Show` (a stand-in type: AdWindows is not in the references), `TopTransaction`, a bare
nested `StartTransaction`/`StartOpenCloseTransaction` (the `using` forms compile) and `using`/`Dispose`
on an `OpenSide` database; `RunCodeOutput` caps one oversized `Log`; `RpcAuth` issues, accepts, refuses
and revokes the token, and the token file has a protected access list with only the current user
(also when it replaces a file with inherited permissions). The Node client reaches a plugin on
127.0.0.1 and never a decoy on `[::1]` when configured with `localhost`; the bridge refuses nested
run_code requests without a token, and routes them with one. The new guard checks fail on the old
compiler (24 of them) and pass now. pwsh is not installed here, so the PowerShell 7 encoding path is
untested.

Live checklist (kept for 2027 and later changes):

- `Test-AcadIdle.ps1` first. Plugin log shows which Roslyn loaded (`Loading
  Microsoft.CodeAnalysis ... from ...`): on 2026 expect the AutoCAD folder (4.10),
  or nothing logged when Civil 3D already resolves it itself.
- Read: `return Db.Filename;` → `ok`, no undo step added (UNDO list unchanged).
- Read, bulk: the obstacle-sampling script on a background (`OpenSide`) → `ok`,
  timing about 1-2 s, `resultFile` for a large point list, `files` lists
  `WriteFile` outputs.
- Compile error and guard: a typo → `compile_error` with the line; `OpenMode.ForWrite`
  in read mode → `rejected`.
- Runtime error: `throw new InvalidOperationException("x");` on line 2 → `runtime_error`, `error.line` 2.
- Timeout: `while (true) Check();` with `timeoutSeconds: 3` → `timeout` after about 3 s, Civil 3D responsive.
- Write: add a line → `ok`, one UNDO removes it. Write that throws after adding a
  line → `runtime_error`, the line is not in the drawing.
- Approval: a call without `approvalToken` is refused; with a token for different
  code it is refused.
- Token: `rpc-token` (64 hex characters) exists while the plugin listens and is gone after
  `C3DMCPSTOP`; a raw TCP `runCode` without it, or with a wrong one, gets `CIVIL3D.AUTH_REQUIRED`.
- Start window: while a 12 s run holds Civil 3D, a raw `runCode` with `startWithinSeconds: 3`
  gets `CIVIL3D.HOST_BUSY` and its `run.json` says `busy`.
- `Tr` wrapper: `{ using var tx = Tr; }` then a read through `Tr` → `ok` (it crashed Civil 3D
  before the wrapper); write with `var tx = Tr; ... tx.Commit();` → `runtime_error` ("The run owns
  Tr"), `committed: false`, nothing added; write inside `using var tx = Tr;` → `ok`, one UNDO
  removes it.
- Read mode refuses `Db.Ltscale++` (`rejected`).
- `icacls %LOCALAPPDATA%\Civil3DMcpPlugin\rpc-token` shows only the user, `(F)`.
- A database from `OpenSide` kept in `Session`: the next run's `TransactionFor` on it gives
  `runtime_error`, and Civil 3D stays up; `Tr` kept in `Session` and used later throws, no crash.
- Write: `using var t = Db.TransactionManager.StartTransaction();` with `t.Commit()` gives `ok`; the
  bare form gives `rejected`.
- Repeat the read and write cases on Civil 3D 2027 (Luis) and note which Roslyn
  copy loaded there.
- `install-bundle.ps1` copies `Contents\roslyn\` (2 DLLs).

Live, Civil 3D 2026, 2026-10-11, after both review rounds (the trusted install of the merged
branches, cold start through `Start-Civil3D.ps1` in 23-27 s with no prompt):
`node scripts/live-run-code-smoke.mjs --side <10 MB background> --expect-drawing RUNCODE-SMOKE`
**32/32 passed**, exit 0 (the token file is `HOMEOFFICE\Jorge:(F)` only; `HOST_BUSY` after a
9-10 s wait; the background sampled into 1,516,406 points in 0.94-1.19 s). Opening every block table
record with `Open<T>`, 118 of 167 from an attached xref, works.

## Deploy-script regression checks

- `install-bundle.ps1` derives `SeriesMin/Max` from the build's target framework
  (`net10.0-windows` → R26.0, `net8.0-windows` → R25.1) and refuses anything else.
- Both install and `-Uninstall` refuse while a running Civil 3D holds the bundle DLL
  (probe: hold the DLL open exclusively, run the script, expect refusal, verify the
  bundle is untouched).
- `gather-refs-2027.ps1` skips only byte-identical staged references and refreshes
  mismatches (probe: plant a wrong DLL under a required name in a scratch `-Destination`).
- `build-2027.ps1` leaves `obj\project.assets.json` on `net8.0-windows` afterwards so a
  default 2026 build with `--no-restore` still works.

## label_add (2026-10-11, `jorgegarcias60/label-add-fixes`)

Offline: `tests/label_commands_source.test.ts` (4 source guards; they fail on the d33f8ba code),
plugin builds for 2026 (net8) and 2027 (net10) with 0 warnings.

Live, Civil 3D 2026, 2026-10-11, `node scripts/live-label-smoke.mjs --expect-drawing RUNCODE-SMOKE`
on a scratch copy of the test job: **9/9 passed**. The script builds its own alignment (line, arc,
line, spiral-curve-spiral, line), two profile views and a pipe network, then:
- `label_set` with three label sets gives `labelGroups` 0, 4 and 5 (each replaces the previous);
- `curves` gives 2 labels (the simple arc and the arc inside the spiral-curve-spiral);
- pipe and structure `plan` give 1 label each; `profile` gives 2 each, one in each view (checked by
  position), `skipped` 0;
- a pipe shown in no profile view gives "not drawn in any profile view".

The first live run caught a bug the offline checks could not: Civil 3D keeps one
`ProfileViewPart` per part, shared by every view that shows it, with placeholder extents
((0,0)-(1,0)), so matching views by extents labelled only the first view. Each view from
`GetProfileViewsDisplayingMe` is now labelled with `Part.ProfileViewPartId`.

Not yet run on Civil 3D 2027 (Luis): run the same two scripts there.

## Not automated

The plugin has no C# unit tests; `tests/FileBoundaryHarness` (dotnet) is upstream's
only .NET test. Everything Civil 3D-side is exercised through the live scenarios above.
