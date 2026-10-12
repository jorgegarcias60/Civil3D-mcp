# Maintaining this fork

`jorgegarcias60/Civil3D-mcp` sits on top of two other repositories:

```text
Sacred-G/Civil3D-mcp          the original project (Civil 3D 2026)            remote: upstream
  └─ Joshua8-AI/Civil3D-mcp   branch civil3d-2027-support: 2027 build support  remote: origin
       └─ jorgegarcias60      branch civil3d-2027 (default): 2027 + our fixes  remote: myfork
```

## Branches in this fork

| Branch | What it is |
|---|---|
| `civil3d-2027` (default) | What we run: Joshua8-AI `civil3d-2027-support` with each fix branch merged in. Anyone who forks or clones this repo gets it. |
| `fix/*` | One fix each, based on Sacred-G `main`: the upstream pull requests. |
| `fork/*` | The same fixes based on Joshua8-AI `civil3d-2027-support`: the pull requests to the 2027 fork. These are what `civil3d-2027` merges. |
| `main` | Kept equal to Sacred-G `main` (a plain mirror; no fork-only commits). |

Fixes so far: `qc-triangles` (#10 / #1), `civil3d-2027-api-paths` (#11 / #2),
`catalog-list-and-alignment-from-polyline` (#12 / #3), `coordinate-system-set` (#13 / #4). Joshua8-AI has all four (2026-09-29); only the Sacred-G pull requests are still open. Last sync with
`origin/civil3d-2027-support`: 2026-10-11 (46 commits, merge resolved toward Joshua8-AI's versions).
Fork only (not sent upstream yet): `fix/command-context-wedge` (Idle-hop fallback when
`ExecuteInCommandContextAsync` stops running callbacks, `CIVIL3D.HOST_BUSY` while a command is active).

## Working together

This fork is developed here directly; the upstreams are slow to take pull requests, so
`civil3d-2027` is our main line. Sending a fix upstream is optional and never blocks our work.

- **Production = `civil3d-2027`.** Build and install the plugin only from an up-to-date
  `civil3d-2027` (`git checkout civil3d-2027`, `git pull`, then the build below). Never install
  from a working branch.
- **Collaborators:** clone this repo, make a branch `<your-github-user>/<topic>` from
  `civil3d-2027`, push it, and open a pull request into `civil3d-2027`. Run the build and tests
  below first and say in the PR what you tested in Civil 3D.
- **Review:** the ruleset "Protect civil3d-2027 (production)" requires a pull request with one
  approval (the owner's) to merge; a new push after approval needs approval again. Force-push and
  deleting the branch are blocked. The owner is exempt and may push directly.
- New upstream work reaches `civil3d-2027` through the routine below, like any other change.

## Remotes (one-time, in the working clone)

```bash
git remote add upstream https://github.com/Sacred-G/Civil3D-mcp.git     # the original
git remote add origin   https://github.com/Joshua8-AI/Civil3D-mcp.git   # the 2027 fork
git remote add myfork   https://github.com/jorgegarcias60/Civil3D-mcp.git
```

## Routine

**Pick up new work from the 2027 fork** (or after one of our PRs is merged there):

```bash
git fetch origin
git checkout civil3d-2027
git merge origin/civil3d-2027-support
```

Then build, test and push (below). Git sees a merged fix as already present, so nothing is lost or
duplicated.

**Add a new fix:**

1. Branch `fix/<name>` from `upstream/main`, commit, open a PR to Sacred-G.
2. Cherry-pick the commit(s) onto `fork/<name>` from `origin/civil3d-2027-support`, open a PR to Joshua8-AI.
3. `git checkout civil3d-2027 && git merge --no-ff myfork/fork/<name>`, add it to the list in the README note.

**Answer review on a PR:** commit on `fix/<name>`, cherry-pick the same commit onto `fork/<name>`,
push both, then merge `fork/<name>` into `civil3d-2027` again.

**When a fix is merged in both places:** delete its `fix/` and `fork/` branches and drop it from the
README note. `civil3d-2027` already has it through the 2027 fork.

**Keep `main` a mirror:** `gh repo sync jorgegarcias60/Civil3D-mcp --branch main` (or GitHub's "Sync fork" button).

## Build and test before pushing `civil3d-2027`

```powershell
npm ci; npm test                            # TypeScript tests
.\scripts\build-2027.ps1                    # plugin, .NET 10, against ..\C_References
.\scripts\build-2027.ps1 -Install           # also deploy to the ApplicationPlugins bundle (close Civil 3D first)
git push myfork civil3d-2027
```

Jorge's machine installs from a trusted folder instead of the bundle (no security prompt at each
start): close Civil 3D, then
`.\scripts\install-trusted.ps1 -SourceDir Civil3D-MCP-Plugin\bin\Release\net8.0-windows` (2026; the
net10.0-windows output for 2027). It copies the build to `C:\CAD-Tools\plugins\Civil3DMcp\R<series>\`
(under `TRUSTEDPATHS` there), loads it from a marked block in `acaddoc.lsp` and removes the bundle; the
plugin then loads when a drawing opens. `C:\CAD-Tools\core\tools\Start-Civil3D.ps1` starts Civil 3D (also
for an agent) and opens a drawing so the plugin comes up. A machine without that trusted folder keeps
the bundle and answers Load Once at each start. The installer refuses before changing anything while
Civil 3D holds the bundle or a changed build: close Civil 3D completely and run it again.

**Update the plugin and the Node server together.** `civil3d_run_code` sends the plugin's
per-session token (`%LOCALAPPDATA%\Civil3DMcpPlugin\rpc-token`, private to the user, sent only to
127.0.0.1), so a server older than the plugin gets
`CIVIL3D.AUTH_REQUIRED` on that tool (other tools keep working). After a pull: install the plugin
(Civil 3D closed), and the Claude launcher (`scripts\claude-bootstrap-and-run.ps1`) rebuilds the server
at its next start (`npm install --no-save` when the package files changed, so the clone stays clean for
`git pull`); run `npm run build` yourself if you start the server another way.

Run-code API 3 adds output copying and read budgets up to 600 seconds. The
server checks `getCivil3DHealth.runCodeApi` before using `copyOutputsTo`, an
explicit `keepOutputs`, or a timeout above 90 seconds. A missing version
counts as API 1. An older plugin gets the "older than this server" refusal
with the parameter and loaded API version. Nothing ran. Install the plugin
from the same build as the server, with Civil 3D closed. The server fills in
the script hash for older plugins on calls that use supported parameters.

To use client approval for `civil3d_run_code`, set `CIVIL3D_RUN_CODE_APPROVAL=client`
in your own MCP registration, such as `~/.claude.json` under
`mcpServers.civil3d-mcp.env`. Do not set it in the shared launcher. Keep
`civil3d_request_approval` off the client's allow list so a nested run_code
call still prompts for permission to request its token.

The plugin project targets .NET 8 (Civil 3D 2026). For a quick compile check of a `fix/` branch
against the 2027 references:
`dotnet build Civil3D-MCP-Plugin\Civil3DMcpPlugin.csproj -c Release --restore /p:Civil3DReferencesPath=<C_References> /p:TargetFramework=net10.0-windows`.
Autodesk assemblies are never committed.
