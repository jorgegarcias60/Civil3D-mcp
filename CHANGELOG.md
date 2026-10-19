# Changelog

## Unreleased

- Issue #53 diagnostics. `civil3d_health` reports `hostRoute`: how the plugin starts its
  `C3DMCPHOP` command (`hopStart`, `com` by default) and what is known about typed input
  in this session (`typedInput`: `unknown`, `alive` or `dead`). `probeTypedInput: true`
  types the harmless `C3DMCPPROBE` command with `SendStringToExecute` and reports whether
  it ran, the latency, `acedIsInputPending` before and after, and the Windows session
  (remote, connect state, input desktop, foreground, minimized). `hopStart` sets the
  attached instance's start route until a restart: `typed` fails with HOST_BUSY when a
  typed start is lost (2 s of quiet idle time), `auto` then starts the same operation over
  COM and stays on COM until a probe runs. An operation's command-line input and ESC ESC
  cancel take the channel of its start. The default route is unchanged. The README
  troubleshooting entry from #46 now says what is known: not every time, recovery seen.
  Refs #53.

- When the plugin starts, it removes the discovery files that crashed or killed instances
  left in `%LOCALAPPDATA%\Civil3DMcpPlugin\instances`: the pid has exited, or it now
  belongs to a process started at another time. A file whose process cannot be inspected
  is kept. Fixes #39.

- A `-PLOT` left waiting at a prompt is cancelled with ESC ESC over COM `SendCommand`
  after the operation, also when it failed. It used `SendStringToExecute`, whose input
  AutoCAD silently drops once any COM client has used `SendCommand` in the session
  (C3DMCPHOP always does). The plugin no longer uses `SendStringToExecute`; README
  troubleshooting explains the effect on other plugins. Fixes #46.

- The build message and docs say which references each target needs: Civil 3D 2025/2026
  (AutoCAD 25.x) for net8.0-windows, Civil 3D 2027 (AutoCAD 26.x) for net10.0-windows.
  `C_References` from `gather-refs-2027.ps1` holds 2027 references. Fixes #47.

- Two tests failed when vitest ran tests in shuffled order: the dialog scanner mock kept
  calls from earlier tests, and the instance manager kept the job label an earlier test
  set. Each test now starts clean. The approval_logging failure #34 names did not
  reproduce in 15 solo and 8 shuffled full runs. Closes #34.

- `label_list_styles` takes an optional `labelType` for alignments (`label_set`,
  `curves`, `station`) and lists the names `label_add` accepts for it, from the
  same collections. Before, it always listed the label set styles, which
  `label_add` rejects for `curves` and `station`. Fixes #13.

- Run-code API 7 adds `ImportBlock(dwgPath, blockName, replace = false, db = null)`: clones one block definition, with its nested blocks, layers and linetypes, from another DWG into the drawing and returns its ObjectId without inserting a reference. An existing definition is kept unless `replace` is true; an unknown name lists up to 20 block names in the source. Write mode only; read mode and preview refuse it at compile time. The server refuses code that uses it on an older plugin. Closes #22.

- `civil3d_plot` `render_layouts_to_png` renders paper-space layouts to review PNGs
  (one per layout, always replaced) on `PublishToWeb PNG.pc3`, so a sheet can be
  looked at before the one PDF is plotted. It uses the same `-PLOT` driver as
  `plot_layouts_to_pdf` (REGENALL first, `regenerated`, approval, `asJob`, `continueOnError`),
  never changes a layout's page setup, picks the pixel medium nearest `pixelWidth`
  (default 3300; 4K UHD for an 11x17 sheet) and reports the medium and the PNG's pixel
  size. The sheet is a window over the layout's limits, fitted and centred. Plot API 3.
  The README has the review-before-plot recipe. Closes #45.

- run_code write runs refuse a layout change together with creating or erasing
  viewports (Fixes #43), and any viewport `Erase`: the second erase-and-re-create on the
  current layout killed acad.exe live. `civil3d_plot` runs REGENALL before every
  in-session plot and reports `regenerated` (Fixes #44).

- The plugin registers its instance's AutoCAD `Application` in the Running
  Object Table as `!Civil3DMcp.AcadApplication:<pid>`, so COM clients (CAD-Tools
  PowerShell) can reach any instance by process id, not only the earliest-started
  one. Revoked when the plugin stops; a registration failure only logs a warning.

- Agent starts also answer AutoCAD's "Missing SHX Files" prompt with Ignore and close the
  "Drawing Recovery" notice, including prompts that appear after the plugin registers
  (with a warning each).
  `attach` on an unresponsive instance names the open dialog; it answers known
  prompts only in this session's own agent instance and only reports elsewhere
  (`reportOnly`). Fixes #48.

- Scan dialogs only in the verified agent launch, found by Win32 enumeration of
  its pid. Press Ignore on "References - Not Found Files" (UIA Invoke, else the
  button's BN_CLICKED notification, then BM_CLICK) and report every dialog seen
  during the start. Only standard and modal dialogs are reported, not the main
  window or palettes. Verified live on Civil 3D 2026. Fixes #36.
- Keep timed-out launches agent-owned for a fifteen-minute grace period. List
  pending launches, attach after registration and require discard to stop them.
  Stopping a pending launch holds the process handle from the identity check to
  the kill, so a reused pid is never killed. Fixes #37.
- Add the opt-in `--orphan` live smoke phase for re-adoption after server exit.
  Fixes #38.
- Show the Design Master pipe and plugin version for the same live pid. A Design
  Master file older than the process (a reused pid) is ignored. Fixes #40.
- Tests use an empty temporary LOCALAPPDATA, so a running Civil 3D no longer
  fails 24 approval tests.

- Capture drawing attachments before preparation and progress notifications.
  Fixes client-approved run-code writes switching targets during concurrent attachment.
- Mark undiscovered fixed-port requests as legacy-only unless explicitly authorized.
  Fixes missing discovery bypassing fixed-instance authorization across accounts.
- Complete verified orphan adoption with owner labels and server process identities.
  Re-adopt matching labels and require `adoptOrphan` for others. Count orphans and
  pending launches toward the cap. Fixes incomplete orphan recovery after server exit.

- Refuse requests until listener identity is initialized. Fixes stale pinned
  mutations reaching a replacement instance during startup.
- Reserve capacity and drawing paths for unexpired pending launch claims.
  Fixes duplicate launches and cap overflow after cancelled starts.
- Require exactly one live instance for automatic attachment. Fixes selecting
  an owned agent while another user or agent instance is present.
- Remove unsupported orphan smoke mode and wait through uncertain launch expiry
  during cleanup. Fixes unusable recovery checks and missed late processes.
- Attach explicitly before live plugin verification. Fixes discovery-mode checks
  refusing before their first plugin call.
- Correct scratch smoke examples and document host HTTP authentication.
  Fixes missing opt-in flags, incomplete filenames and unauthenticated instance calls.

- Scope retry results to the selected instance and refuse retry keys for attachment
  changes. Fixes cached results after switching instances.
- Retain cancelled launch claims through expiry and publish process proof from the
  plain-JSON helper. Fixes late starts losing agent ownership.
- Reap start locks only after verifying their owning process exited. Clean taken
  claim files after attachment or shutdown. Fixes blocked starts and claim buildup.
- Probe only the attached instance during drawing calls and permit legacy fallback
  when no live records remain. Fixes unrelated probes and stale discovery refusals.
- Refuse implicit fixed-port access to user or other-session instances; expose
  fixed routing in health. Fixes drawing requests bypassing discovery ownership.
- Require explicit scratch user-instance attachment in live smoke scripts and
  verify unattached drawing refusal. Fixes smoke runs depending on ambiguous
  automatic routing.
- Leave the Claude Desktop extension port unset for discovery and document the
  330-second client timeout and start-claim expiry. Fixes short client waits and
  fixed-port configuration overriding instance selection.

- Retry locked launch claims on Idle before fixing user ownership.
  Fixes transient claim-reader contention causing agent start timeouts.
- Require and normalize fully qualified drive or UNC drawing paths before start.
  Fixes current-drive-rooted paths launching instances that cannot claim ownership.
- Bind ownership to a launch-only token, child pid and process creation time.
  Refuse attachment without matching launch proof. Fixes concurrent user launches
  being adopted by an agent session.

- Mark automatic legacy fallback and reject it in discovery-capable plugins.
  Fixes unattached requests reaching a new listener during startup or across accounts.
- Wake both quit callbacks and expire pending shutdown work.
  Fixes idle stop timeouts and delayed quits after failure.
- Revoke per-port and legacy tokens before releasing the listener port.
  Fixes shutdown deleting a replacement instance's token.
- Bind drawing approvals to the selected pid and process creation time.
  Fixes approval reuse across instances with no drawing open.
- Require HTTP authentication for instance calls and nested requests naming them.
  Fixes unauthenticated shutdown through the owning server.
- Check process creation times before retaining discovery records.
  Fixes reused pids blocking drawing starts or consuming the agent cap.
- Compare user process and document snapshots and guard the smoke script's write target.
  Fixes false reports that user instances were untouched.
- Reconcile session-owned discovery during smoke cleanup after failed starts.
  Fixes leaked claimed processes and reports unresolved pids.
- Hold the launch claim's read handle through its rename.
  Fixes a double-claim race reproduced by the offline contention check.

- Freeze process ownership, reject launch claims in pre-existing processes, and
  preserve ownership across listener restarts. Fixes multi-instance ownership races.
- Refresh discovery after first save and Save As. Fixes stale drawing paths.
- Bind stop approval to its resolved instance. Pin drawing requests through approval
  and execution, and check process identity on every discovery RPC. Fixes wrong-instance writes and shutdowns.
- Test claims in separate synchronized processes and test the production port binder.
  Track unexpected smoke-test launches for cleanup. Fixes multi-instance coverage and cleanup gaps.

- Add per-session Civil 3D instances with automatic ports, protected per-port tokens,
  drawing claims and discovery. Add `civil3d_instance` to list, launch, attach, detach
  and stop owned instances. Require explicit user-instance attachment, enforce the
  agent cap and refuse unsaved shutdowns. Keep legacy single-instance routing.
  Add offline checks and an opt-in two-session live smoke script. Fixes multi-instance port conflicts.

- Harden sheet-set publishing with document selection, plot API checks, reopened layout initialization and saved-revision settings. Check CTB/STB and media before writing; verify PDF page count, size and object streams. Drain cancellations and lock job snapshots. Add a guarded live smoke check with an opt-in missing-table fixture and cleanup after failures. Fixes #21.

### Added

- Run-code API 6 adds `mode: "preview"`: aborts the transaction, reports final counts and bounded existing handles using the starting Handseed, and returns script/args hashes. Guards refuse changes outside the run's drawing or transaction, Session mutations and file writes while allowing read-only operations and detached copies. Rollback uncertainty produces a warning. Preview needs separate approval from a later write, leaves an empty undo step and clears redo. Opt-in smoke checks cover rollback, counts and document targeting. Fixes #20.

- Run-code API 6 adds `TextBox`, `XClip`, scaled `AttachXref` and in-place `SetViewport`. Text bounds support attributes and nested transforms. XClip validates small polygons at large coordinates; its smoke check accepts AutoCAD's no-back-plane sentinel. Unitless xrefs require explicit scale; feet and US survey feet use scale 1. Write helpers use nested transactions. SetViewport activates dormant layouts, refuses the overall viewport, restores the previous layout and reports `activatedLayout`. Opt-in smoke checks cover geometry, scaling, undo and viewport persistence. `ImportBlock` remains deferred. Fixes #22.

- Run-code API 5: `mode: "write"` with `document` can target another open drawing. The plugin briefly activates that drawing for the command, then attempts to switch back even when the script fails. Responses report `activated` and `switchedBack`; the server refuses these writes against older plugins before running them.

- Run-code API 4: optional `document` selects another open drawing for read scripts by full path or unique file name, without changing the active drawing. With no drawing open, read scripts can use `OpenSide(path)`; `HasDrawing` reports whether drawing-backed accessors are available. API 4 plugins limit writes to the active drawing. The server checks API 4 before sending `document` on reads.

- Run-code API 3: `codePath` and `argsPath` with content-bound approvals,
  `copyOutputsTo`, and `keepOutputs`. Reads allow 600 seconds; writes stay at 90.
  Long runs send progress heartbeats when the client supplies a progress token.
- Script helpers: `OpenSideOrNull`, `Try`, `ById`, `EnsureLayer`, `Tag`,
  `TagValue`, `Tagged`, and `PaperSpace`. Responses and audit logs include
  script hashes, copied files and bounded error reports.
- The server refuses API 3 options when an older plugin is loaded. Both
  script modes refuse plugin internals outside the script API. Side drawing
  retries use a fresh database and skip drawing-content errors.

- Optional `CIVIL3D_RUN_CODE_APPROVAL=client` for direct MCP client calls to
  `civil3d_run_code`. Nested and HTTP calls still require a token.

- `civil3d_run_code` (plugin method `runCode`): runs a C# snippet inside Civil
  3D, compiled with Roslyn into a uniquely named in-memory assembly (its own
  collectible load context, unloaded after the run) and run on the main thread
  with the drawing locked. For bulk work COM and LISP make too slow: walking
  96,913 objects of a 10 MB background into 547,441 points took 1.2 s
  in-process, about 20 minutes in LISP. Scripts are compiled inside the
  already-trusted plugin, so no DLL is written and no AutoCAD security prompt
  appears per script.
  - Token mode stays approval-gated, even with `CIVIL3D_APPROVAL_MODE=disabled`;
    the token is bound to the exact code. Every script is saved with a summary in
    `run-code\<run id>\` next to `plugin.log`.
  - `mode: "read"` (default) runs from an idle tick in an open-close
    transaction that is never committed, and the compiler refuses write APIs
    (a guard against mistakes, not a sandbox). `mode: "write"` runs in
    `C3DMCPHOP`: committed as one undo step, rolled back on an exception or
    timeout (changes outside the transaction, such as database or system
    variables, files and `Session`, are not). Both refuse Editor prompts,
    modal windows and message loops (any `ShowDialog` or `ShowModal`,
    `TaskDialog.Show`, `MessageBox`, `InputBox`, `Application.Run`,
    `Dispatcher.PushFrame`), event subscriptions, `TopTransaction` and a
    `using`/`Dispose()` on an `OpenSide` database (the run disposes it, and
    the script gets a non-owning wrapper either way); a nested transaction is
    allowed only as the resource of a `using`, so it ends on every path; read mode counts `++`/`--`, compound
    assignment and deconstruction as setting a drawing property, and refuses a
    numeric `OpenMode` other than `ForRead`.
  - The script gets `Tr` and `TransactionFor(...)` as wrappers the run owns:
    `Commit()`/`Abort()` throw with a message, `Dispose()`/`using` do nothing,
    so `var tx = Tr; tx.Commit();` can neither commit behind the run's back
    nor leave a disposed transaction that crashes the next call; wrappers are
    detached when the run ends, so one kept in `Session` throws instead of
    reaching freed memory. Write responses report `committed` as `true`,
    `false` or `"unknown"`.
  - The plugin refuses `runCode` without this session's token
    (`%LOCALAPPDATA%\Civil3DMcpPlugin\rpc-token`, written when the plugin
    starts listening, deleted when it stops, private to the user; other
    methods are not gated): the MCP server reads it and sends it only to
    `127.0.0.1` (the default `CIVIL3D_HOST` is now `127.0.0.1`, and `localhost`
    is dialled as `127.0.0.1`, so a listener on `[::1]:8757` cannot receive
    it). Server and plugin must come from the same build. Without
    `MCP_HTTP_TOKEN`, the HTTP bridge refuses any request that names the tool
    anywhere in its parameters.
  - A run either starts with its full budget or not at all: one that cannot
    start within 30 s because Civil 3D is busy fails with `CIVIL3D.HOST_BUSY`
    and changes nothing. The MCP call waits for the budget plus 60 s, and a
    timeout message says where the run's `run.json` is.
  - Requests over the plugin's 1 MiB limit are refused before sending
    (`CIVIL3D.REQUEST_TOO_LARGE`, for every tool); `civil3d_run_code` refuses
    `code` plus `args` over about 1 MB of JSON before approval.
  - Cooperative time budget (`timeoutSeconds`, default 30, max 90): `Check()`
    ends the script when it is used up or the request is cancelled, and stays
    tripped if the script catches it; a script that never calls it cannot be
    stopped.
  - Globals: `Doc`, `Db`, `Ed`, `Civil`, `Tr`, `Args`, `Log`, `Check`,
    `Open<T>`, `ModelSpace<T>`, `OpenSide` (read-only side databases),
    `TransactionFor`, `WriteFile`, `Session` (kept across runs).
  - Compile and runtime errors come back with snippet line numbers; output or
    results over `maxInlineBytes` (default 64 KB) go to a file with a preview.
  - Compiles against `Microsoft.CodeAnalysis` 4.8 and uses Civil 3D's own copy
    when it has one at least that new (2026 ships 4.10); the bundle carries a
    fallback in `Contents\roslyn\`, which `install-bundle.ps1` now copies.
  - `tests/RunCodeHarness` (`npm run test:run-code-compiler`) checks the
    compiler offline against the Civil 3D reference assemblies;
    `scripts/live-run-code-smoke.mjs` (`npm run test:live-run-code`) checks a
    running Civil 3D end to end; `scripts/run-code-file.mjs` runs a `.cs` file
    through the MCP server from a terminal.
- `scripts/install-trusted.ps1`: installs the plugin into a trusted folder
  (default `C:\CAD-Tools\plugins\Civil3DMcp\R<series>`) and NETLOADs it from a
  marked `acaddoc.lsp` block, so Civil 3D no longer asks "Security - Unsigned
  Executable File" at every start as it does for the `%APPDATA%` bundle; removes
  the bundle; `-Uninstall` reverses. The plugin then loads when a drawing opens.
  An `acaddoc.lsp` further down the support path, which the new file would
  shadow, is loaded first (so its `S::STARTUP` is chained, not lost) and stays
  chained when the installer runs again, also after user code was added to the
  file or while that file is unreachable (only `-Uninstall` drops it, with a
  warning when user code keeps the file). Every lock (a changed build, the old
  bundle) is checked before anything is changed; `-Uninstall` leaves
  `acaddoc.lsp` files without our block untouched; ANSI files keep their
  encoding under PowerShell 7 too.
- `install-bundle.ps1` no longer claims the bundle folder is trusted: Civil 3D
  2026 prompts for it at every start (answer Load Once).
- `claude-bootstrap-and-run.ps1` runs `npm install --no-save` (the committed
  `package-lock.json` stays as it is) when `package.json` or
  `package-lock.json` changed since the last install, and rebuilds when any
  build input changed (source files, including deletions and renames, the
  package files, `tsconfig.json`, the copied assets), not only when there is no
  build: after a `git pull`, new tools appear without a manual build. A failed
  or interrupted build is rebuilt at the next start instead of running half a
  build. Concurrent starts of one clone take turns through a named mutex and
  reuse the first one's install and build; a start with nothing to do never
  waits. npm output goes to `%TEMP%\civil3d-mcp-bootstrap-<clone key>.log`
  (its tail to stderr on failure), never to stdout, and an npm notice on
  stderr no longer stops the start under Windows PowerShell 5.1.

### Fixed

- Expected approval refusals (approval required; unknown, expired, used or mismatched token) log one WARN line with tool, action and request ID and no stack trace, also when nested through `civil3d_orchestrate`, `civil3d_docs` or the HTTP bridge. Unexpected errors keep their stacks; client responses are unchanged. Fixes #29.

- The live IPv6 host check uses a direct call and normal-server approval in token mode, avoiding approval through the unreachable child. Fixes #7.

- Live damaged-DWG comparisons report SKIP for import-root refusals and preserve the policy message. The summary counts skips separately. Fixes #24.

- Smoke scripts drain server stderr and print a bounded tail on failure, preventing approval logs from filling the pipe and freezing the server. Fixes #27.

- `civil3d_run_code` requires the effective startup host to be `127.0.0.1` before token reads or plugin calls. `localhost` and empty hosts map to it; refusals say "Nothing ran." Opt-in live checks cover refusal and recovery. Fixes #7.

- Read mode refuses Database mutations and requires proven detached destinations for loading and copying. Alias checks allow null/default definitions alongside detached ones and refuse open-drawing targets or ref/out exposure. Queries and write-mode calls remain allowed. Opt-in live checks cover guards and detached reads. Fixes #11.

- Run-code results stream to `result.json`, including nested tuples. Small values also return inline; large values return a file and bounded preview. `resultError` preserves the script outcome on serialization or file failures; partial files are never returned or copied. Opt-in live checks validate saved results. Fixes #12.

- Run folders expire after 180 days by default; `CIVIL3D_RUN_CODE_RETENTION_DAYS` overrides this, with 0 or less keeping folders. Seven-day output pruning remains. Cleanup protects the current run, stays inside the log root, skips filesystem links and continues after deletion failures. Linked parents do not disable it. Fixes #25.

- Drawing comparison retries opening once on a fresh database after disposing the failed attempt. Drawing-content errors are not retried; a second failure reports the first error. Opt-in live checks cover damaged-DWG errors and plugin recovery. Fixes #24.
- Path refusals (`CIVIL3D.PATH_NOT_ALLOWED`) name only the root variable in use and keep the allowed-root list (follow-up to PR #14).
- `install-trusted.ps1 -Uninstall` without `-SourceDir` removes all installed
  releases and marked roaming support blocks, including orphans, after checking
  every selected DLL for locks. Explicit `-SourceDir` selects one release;
  short aliases cannot select unrelated folders. Fixes #8.

- `install-trusted.ps1` chains new or block-only startup files to the next
  `acaddoc.lsp` on the support path. Marked targets retain user edits;
  reinstall drops upward links and preserves other loads. Normalized paths,
  malformed-path handling and document guards prevent lost code, cycles and
  repeated loads. Fixes #9.

- Failed or missing trusted plugin loads report the DLL path and recovery
  instructions. `C3DMCPLOAD` retries without restarting; automatic attempts
  stay limited to once per session. LISP balance checks allow preserved
  startup loads. Fixes #10.

- `label_add` (`civil3d_standards` / `civil3d_label`):
  - Alignment `label_set` imports the named label set (it replaces the
    alignment's label groups) and returns `labelGroups`; `curves` labels every
    arc, including the arcs inside compound curves such as spiral-curve-spiral.
  - Pipe and structure `plan` and `profile` labels are real labels with styles
    from the pipe/structure label styles; `profile` puts one label in every
    profile view that shows the part (Civil 3D keeps one profile view part per
    part, shared by those views). Responses list every label: `handle`,
    `handles`, `created` (a count; it was `true` for pipes), `skipped`.
  - An unknown style name lists the available ones; profile `label_set` says
    the .NET API cannot set it instead of failing obscurely.
  - `scripts/live-label-smoke.mjs` checks all of it in a running Civil 3D.
- `createAlignment` from `points` reported `curvesAdded: 0` while Civil 3D
  fitted curves at the drawing's default radius (`addCurves` defaults to true
  for points), so an alignment meant to follow the points came out shorter with
  nothing in the result to say why. `curvesAdded` now counts those curves too,
  with `curvesAutoFitted` and `curveCount` (arcs on the finished alignment;
  arcs already in a traced polyline are not counted as fitted). A warning
  names the default when it fitted curves the caller did not ask for, and the
  `addCurves` field describes its default.
- A path outside the plugin's file roots now names the allowed roots and the
  variables that add one, instead of only "outside the configured roots".

- Every plugin call hung until Civil 3D restarted once any COM client had run
  a command with `SendCommand`. Verified on 2027 with only this plugin loaded:
  in a disconnected Windows session the first such command (from PowerShell,
  another add-in, or the plugin itself) stops
  `DocumentManager.ExecuteInCommandContextAsync` from running callbacks for
  the rest of the session, and nothing revives it. The plugin no longer uses
  that route:
  - plain reads run from an `Application.Idle` tick with an
    `OpenCloseTransaction`, so they add no undo steps and leave the undo
    history alone (a regular transaction under a document lock outside a
    command wipes it);
  - writes, xref changes, command sequences (plot, publish) and reads that
    create scratch objects (volume surfaces) or use the pickfirst selection run
    inside the plugin's own `C3DMCPHOP` command, started through COM
    `SendCommand` only while the editor is quiescent; each write is one
    undoable step, and the command keeps the user's pickfirst selection;
  - command-line input (undo, redo, Storm and Sanitary, data-shortcut
    commands) is sent through COM `SendCommand` after the operation instead of
    `SendStringToExecute`, whose queued input got mixed into the plugin
    command's input (`UNDO` received "C3DMCPHOP" as its count).
  A command, prompt or modal dialog that holds Civil 3D for 15 s now fails the
  request with `CIVIL3D.HOST_BUSY` (watcher from jorgegarcias60/Civil3D-mcp#1
  and #2) instead of a 120 s timeout, and plugin calls no longer cancel a
  command or prompt the user has open.
- `redoDrawing` sent `REDO n`; `REDO` takes no count, so the count reached the
  command line as an unknown command. It now sends `MREDO n`, and undo and
  redo run without a document lock (a plugin read under a lock empties the
  redo stack).
- `getSelectedCivilObjectsInfo` and `getProjectContext` read the pickfirst
  selection inside `C3DMCPHOP`, which is flagged `UsePickSet` and `Redraw`:
  outside such a command `Editor.SelectImplied` returns an error, and the
  selection and its grips stay in place afterwards.
- Style lookups by name never matched on Civil 3D 2027, so a requested style
  was silently replaced by the drawing's first one: `create_layout` with style
  "Design Profile" got "Existing Ground Profile", and `view_create` with style
  "Profile View" warned the style was missing. Civil 3D 2027's `StyleBase`
  declares a set-only `Name` that hides the readable base-class `Name`, so the
  reflective name read returned null for every style, label set and band set.
  Names are now read through the documented `DBObject.Name`, and the
  reflection boundary resolves a readable (or writable) declaration when a
  derived class hides one. A style, label set or band set name that does not
  exist is now `CIVIL3D.INVALID_INPUT` listing the available names instead of
  a silent substitution. No name still means the drawing's first one, except
  the section view band set and group plot style lookups, which pass Civil
  3D's default (a null id), and the profile view band set lookup, which uses
  the first band set only where the caller asks for it (`view_create` does).
  `create_layout` / `create_from_surface` now report the style and layer
  applied. Affects every `LookupUtils` style lookup (alignment, profile,
  surface, alignment/profile label set, profile view style and band set,
  parcel style, parcel area label style, section view style and band set,
  group plot style) and the name reads in the label and pipe-network style
  lookups.
- `civil3d_plot` plot/publish: with a PDF device set to "open in viewer when
  done", the viewer opened the hidden `.<name>.<guid>.mcp-tmp.pdf` the plotter
  had been pointed at, which had already been renamed to the final name ("file
  not found"). The plotter now writes the requested path itself. The output
  directory chain stays locked during the plot; a link or junction at the
  final name is refused; with `overwrite` an existing PDF is moved to a backup
  and restored if the plot fails; after the plot the output must be a regular
  file with one hard link (a link planted during the plot is removed and the
  call fails) before it is verified.
- `civil3d_profile view_create` always failed on Civil 3D 2027 ("ProfileView.Create
  returned null"): the plugin probed `ProfileView.Create` by reflection with
  argument orders that do not exist. It now calls the typed
  `Create(alignmentId, insertPosition, name, bandSetId, styleId)` overload,
  falling back to the drawing's first profile view style and band set when none
  is named. When the drawing has only one of the two, the view is created with
  Civil 3D's defaults, the one that exists is applied to it, and a warning
  names the one that was missing; a named style or band set that cannot be
  applied, or a view that cannot be given the requested name, fails with
  `CIVIL3D.INVALID_INPUT` and nothing is created. New optional `layer`; the
  result reports name, handle, layer, style and band set.
- The profile view band-set lookup read `ProfileViewBandSetStyles` from the
  label-set styles root, where it does not exist, so it always returned a null
  id (`view_band_set` imported nothing). It now reads `Styles.ProfileViewBandSetStyles`.
- A requested layer that did not exist was silently replaced by the current
  layer (e.g. `create_layout layer:"C-ROAD-DES"` landed on layer 0). Missing
  layers are now created; an invalid layer name is `CIVIL3D.INVALID_INPUT`.
  Applies to every create path that takes `layer` (profiles, alignments,
  offset alignments, polylines, text, lines, profile views).
- `civil3d_profile check_k_values` computed K with A as a decimal grade
  difference (0.028) instead of percent (2.8) — a 280 ft curve reported
  K ≈ 9,980 — and always used a metric table, so 50 (mph) required sag 9 /
  crest 4. K is now L / (100·|g2−g1|), checked against the AASHTO
  stopping-sight-distance design K tables in mph (ft/%) or km/h (m/%). New
  optional `speedUnits` (`mph` | `km/h`) defaults from the drawing's length
  unit; speeds between rows use the next higher row (and say so); speeds
  outside the table are `CIVIL3D.INVALID_INPUT`. Per curve it now returns
  grades in percent, A in percent, K, required K, start/end and PVI station.
  A profile with no vertical curves reports `allPass: false` with a warning,
  since nothing was checked.
- `civil3d_profile get` reported symmetric parabolas as `asymmetric_parabola`
  ("parabolasymmetric" contains "asymmetric"); they are now
  `symmetric_parabola`. The response schema still accepts the old `parabola`.
- With no document open, drawing-dependent requests hung and wedged the
  plugin's execution gate; they now fail fast with `CIVIL3D.NO_DRAWING`, and
  `civil3d_drawing new` works from zero documents.
- `civil3d_drawing` `settings` failed response validation on every drawing:
  the plugin reports `defaultStyles.corridor` as `null` unconditionally (Civil
  3D exposes no corridor style collection) and the schema rejected `null`. The
  five `defaultStyles` fields are now `string | null`, with regression tests.
- `docs:check` and `version:check` reported generated files as stale on any
  Windows clone with `core.autocrlf=true`; comparisons now ignore CRLF/LF.

### Added

- `civil3d_style list|get` takes `profile_view`, `profile_view_band_set`,
  `section_view` and `section_view_band_set`, so the names that
  `civil3d_profile view_create`/`view_band_set` and the section view tools
  accept can be listed.
- `civil3d_profile view_update` (also `civil3d_profile_view_update`, plugin
  `profileViewUpdate`): set an existing profile view's style, layer, station
  range (`stationStart`/`stationEnd`, or `stationRange: "auto"`), elevation
  range (`elevationMin`/`elevationMax`, or `elevationRange: "auto"`) and
  vertical exaggeration. `view_create` takes the same range and exaggeration
  fields. Vertical exaggeration is part of the profile view style, so it
  changes every view using the style; the result lists them in
  `styleSharedWith` and warns when there are others. Approval-gated like
  `view_band_set`.

- Read-only geometry for the Civil 3D-Revit bridge. None of these is
  approval-gated (`query`/`inspect`, `safeForRetry`):
  - `getSurfaceTinVertices` (`civil3d_surface get_tin_vertices`): the vertices
    of a TIN surface's visible triangles (or a grid surface's visible points),
    optionally clipped to a plan polygon. Over `maxPoints` (default 50,000,
    hard cap 100,000) they are decimated deterministically: sorted by X then Y,
    with an even stride. Coordinates are rounded to 6 decimals to keep the
    largest response near 6 MB. TIN volume surfaces are rejected clearly.
  - `getParcelGeometry` (`civil3d_parcel get_geometry`): real parcel boundary
    from the typed curve API (base curve, then `GetGeCurve`, then `Explode`)
    with line/arc segments, bulges, a densified polygon, perimeter and area
    computed including arcs. `reportParcels` still uses its reflection path.
  - `getDrawingUnits` (`civil3d_drawing units`): raw INSUNITS and its name, a
    `lengthUnit` that keeps `USSurveyFeet` distinct from `Feet`, metres per
    unit, the Civil 3D drawing unit settings (Feet/Meters and the
    imperial-to-metric foot), angular units, and a consistency warning when
    INSUNITS and Civil 3D disagree.
  - `getPipeNetwork`/`getPipe` pipes gain `startPoint`, `endPoint`,
    `startInvert`, `endInvert`, `startCrown`, `endCrown`, `innerDiameter`,
    `outerDiameter`, `innerHeight`, `outerHeight`, `wallThickness`,
    `crossSectionalShape` and `length2d` (additive; inverts are centreline
    minus inner height / 2, as in `civil3d_compare`).
  - `getDrawingInfo` and `getCoordinateSystemInfo` gain an additive
    `lengthUnit`; `linearUnits` keeps its old values.
  - Host-independent `BridgeMath.cs` (arcs, bulge areas, chaining,
    point-in-polygon, stride decimation, unit resolution) with an offline
    harness, `npm run test:bridge-math`. Live verification is pending (see
    TESTING.md).
- `civil3d_plot` domain tool (plugin `PlotCommands.cs`): `list_layouts`,
  `list_page_setups`, `list_plotters` (read-only, never approval-gated) and
  `plot_layouts_to_pdf` / `publish_sheet_set` (approval-gated `export`). Plots
  drive `-PLOT` / `-PUBLISH` with `BACKGROUNDPLOT=0` instead of the PlotEngine,
  which live testing found crash-prone from a command context; every answer is
  validated up front, output goes through `FileBoundary` (export roots, `.pdf`,
  directory chain locked while the plotter writes, `overwrite` defaults to
  false), and success is only reported for a non-empty PDF written by the run.
  Both output actions can run as `civil3d_job` operations
  (`plot_layouts_to_pdf`, `publish_sheet_set`) via `asJob: true`. Live
  verification against Civil 3D 2027 is pending (see TESTING.md).
  `plot_layouts_to_pdf` takes `plotArea` (`layout`, the default for
  paper-space layouts; `extents`; or `window` with `windowCorners`) and plots
  the Model tab when `layoutNames` names it (extents or a window, fit to the
  paper and centred). Page orientation comes from the paper's own shape as
  well as the layout rotation, so a layout on a landscape medium such as
  ARCH D (36 x 24) at rotation 0 plots landscape.
- `CivilExecution.ExecuteCommandSequenceAsync` for host work that issues
  AutoCAD commands; it keeps the zero-document `CIVIL3D.NO_DRAWING` fast fail
  and the drawing-identity check but opens no transaction around the command.
- `civil3d_xref` domain: `list` reports each xref's saved and found paths,
  status (`loaded`, `unloaded`, `unreferenced`, `not_found`, `unresolved`,
  `orphaned`), attach or overlay, nesting and parents, and instance count.
  `attach`, `overlay`, `detach`, `reload`, `unload`, `bind` (`bind`/`insert`)
  and `repath` (`absolute`/`relative`) change xrefs and need approval. Every
  caller path goes through `FileBoundary` (import roots, `.dwg` only, must
  exist). Reload, unload, bind, and detach run under the document lock with no
  enclosing transaction, through the new
  `CivilExecution.ExecuteLockedWithoutTransactionAsync`.
- `civil3d_project data_shortcut_references` (read-only) lists the
  data-shortcut references in the current drawing with source drawing, source
  object name/type/handle, where the source is relative to the working folder
  and current project, and `current`/`out_of_date`/`broken`/`source_missing`/`unknown`
  status, all from `Entity.GetReferenceInfo()` and the `IsReference*` flags.
  `data_shortcut_repair` (needs approval) repoints a reference to a new source
  drawing through `DataShortcuts.RepairBrokenDRef`, resolved at runtime from
  `AeccDataShortcutMgd.dll` through `Civil3DCompatibility`, so the build needs
  no seventh reference.
- `civil3d_compare` domain, which never modifies a drawing. `drawing` compares
  the active drawing with another DWG that it reads as a side database with
  `ReadDwgFile`, never opening it as a document. `snapshot` writes a JSON
  fingerprint to the export roots; it is gated as an export and does not
  overwrite by default. `compare_snapshot` diffs against a snapshot. Results
  group added/removed/modified entities by type and layer and add Civil 3D
  object diffs: alignment length and geometry hash, profile PVIs, surface
  statistics, pipe counts and inverts. The host-independent diff
  (`DrawingFingerprint.cs`) has an offline harness,
  `npm run test:compare-diff`.

### Changed

- `data_shortcut_sync` sends `_AeccSynchronizeReferences`, the command name
  found in the Civil 3D 2027 CUIx and `AeccUiBase.arx`, where it used to send
  `SynchronizeReferences`. `data_shortcut_list` fills `sourceFilePath` from
  `GetReferenceInfo()`.
- `data_shortcut_promote` now finds the reference, preselects it, and queues
  `_AeccPromoteReference`, where it used to only return manual steps.

- Civil 3D 2027 build path: `scripts/gather-refs-2027.ps1` stages the six
  managed references (spread across three folders in a 2027 install) and
  `scripts/build-2027.ps1` builds with a `net10.0-windows` override — Civil 3D
  2027's assemblies target .NET 10 — without changing the 2026 default.
- `scripts/install-bundle.ps1` deploys the plugin as an ApplicationPlugins
  bundle, which auto-loads under the default `SECURELOAD=1` policy that rejects
  Startup Suite entries outside `TRUSTEDPATHS`.

## v1.2.1 — 2026-07-14

### Production readiness

- Added a self-contained Claude Desktop MCPB package, a byte-identical legacy
  DXT compatibility artifact, installer configuration, checksum generation, and
  official manifest validation through `npm run package:claude`.
- Added a compact 33-tool default MCP surface while keeping 216 manifest-backed
  canonical and alias routes available internally; set
  `CIVIL3D_ENABLE_TOOL_ALIASES=true` to expose the full 218-tool MCP surface.
- Added first-class MCP output schemas, structured content, annotations,
  resources, progress notifications, and report-resource retrieval.
- Added parameter-bound, drawing-bound approval receipts, bounded serialized
  host execution, disconnect cancellation, idempotency, and stable errors.
- Added bounded background jobs for publishing, imports, corridor rebuilds, and
  bulk QC, with terminal retention and cancellation telemetry.
- Added liveness, readiness, plugin, queue, and version endpoints on the local
  HTTP bridge at port 3000.
- Added rotating native plugin logs with file-health and last-error telemetry.
- Added CI, generated tool documentation, manifest/dispatcher parity, package
  inspection, startup smoke, and opt-in live Civil 3D host validation.
- Removed tracked Autodesk assemblies; Release builds resolve licensed Civil 3D
  2026 references through `Civil3DReferencesPath`.

### Live validation

- Validated plugin 1.2.1.0 against Civil 3D 2026 on port 8080.
- Validated the spawned stdio MCP client, HTTP registration and routing,
  approvals, concurrency, document switching, create/undo/delete, temporary
  TIN-volume rollback, job cancellation/completion, and response limits.

## v1.1.0 — 2026-03-17

### Summary

Adds 12 new tools across 3 categories (parcel editing, survey processing, data shortcut management) to bring the total MCP tool count to 162.

---

### MCP Server (TypeScript)

**New tools (+12):**

| Category | New Tools | Actions |
|---|---|---|
| Parcel Editing | 4 | create, edit, lot-line-adjust, report |
| Survey Processing | 4 | observation-list, network-adjust, figure-create, landxml-import |
| Data Shortcut Management | 4 | create, promote, reference, sync |

**New files:**
- `src/tools/civil3d_parcel_editing.ts` — parcel CRUD and lot-line adjustment
- `src/tools/civil3d_survey_processing.ts` — survey observation, network adjustment, LandXML import
- `src/tools/civil3d_data_shortcut_mgmt.ts` — data shortcut lifecycle management
- `tests/parcel_survey_shortcuts.test.ts` — 34 schema-level tests

**Deferred:** gravity pipe HGL solver and APS 3D viewer (high complexity, no operational demand).

---

## v1.0.0 — 2026-03-17

### Summary

First stable release of the Civil3D MCP Ecosystem: a 150-tool MCP server for Autodesk Civil 3D paired with an AI CoPilot WPF plugin and a full hydrology analysis engine.

---

### MCP Server (TypeScript)

**150 tools across 16 categories:**

| Category | Tools | Highlights |
|---|---|---|
| Alignment | 22 | Create, edit, stationing, offsets, superelevation, widening |
| Surface | 18 | TIN create/edit, breaklines, contours, volume analysis, sampling |
| Corridor | 12 | Create, rebuild, extract solids, edit frequency, targets |
| Profile | 14 | Surface profiles, layout profiles, PVI edit, design speeds |
| Pipe Networks | 10 | Pipes, structures, network analysis, interference |
| Grading | 8 | Feature lines, grading objects, volume balance |
| COGO / Points | 10 | Import/export, groups, inverse, traverse, intersection |
| Plan Production | 12 | Sheet sets, view frames, match lines, north arrows |
| QC | 18 | Design standard checks, clearance, slope, cross-fall |
| Quantity Takeoff | 10 | Cut/fill volumes, earthwork reports, material summary |
| Hydrology | 5 | Flow paths, low point, watershed delineation, catchment, rational method |
| Sample Lines | 4 | Group creation, section views, material table |
| Data Shortcuts | 4 | Create, reference, synchronize, export |
| Labels / Styles | 4 | Apply label sets, import/export styles |
| Utilities | 9 | Drawing context, layer management, export formats |

**Architecture:**
- MCP protocol over HTTP POST `/execute`
- Tool registry with per-tool Zod validation
- `civil3d_hydrology` tool handles 5 hydrological analysis actions

---

### AI CoPilot Plugin (C#)

**Command routing (all 150 categories):**
- `IsMcpDirectCommand()` — routes `civil3d_*` and `create_cogo_point` to MCP async handler
- `IsHydrologyCommand()` — routes hydrology commands to async MCP → CAD draw pipeline
- `CommandRouter.Execute()` — synchronous CAD transaction handler for 14 draw command types
- `HandleMcpDirectCommandAsync()` — async MCP caller with formatted JObject result display

**Hydrology integration:**
- `HydrologyDrawer.cs` — draws flow paths (cyan), watershed boundaries (yellow), outlet markers (red)
- `McpClient.cs` — typed async methods for all 5 hydrology tools with result model classes
- Sequential workflow support: `lastHydroOutlet` chains `FindLowPoint` → `DelineateWatershed`

**AI providers supported:** Gemini, OpenAI, Anthropic

---

### Integration Tests

- 13 test files, **372 tests**, all passing
- End-to-end workflow tests covering alignment → surface → corridor → profile pipeline
- Hydrology workflow tests covering flow path → low point → watershed → catchment → runoff

---

### Bug Fixes

- **JFS-15**: Fixed structural C# compile error — hydrology methods were accidentally placed outside the `McpClient` class body
- **JFS-15**: Made `ExecuteMcpToolAsync` public (was private) to enable direct MCP command routing

---

### Known Limitations

- Civil 3D API calls require an active drawing session — no headless mode
- Hydrology delineation uses grid sampling (not DEM-optimized); large surfaces may be slow
- C# plugin targets .NET Framework 4.8 (AutoCAD/Civil 3D requirement)
