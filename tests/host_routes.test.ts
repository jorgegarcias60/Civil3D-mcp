import { readdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const pluginDirectory = fileURLToPath(new URL("../Civil3D-MCP-Plugin/", import.meta.url));
const source = (name: string) => readFileSync(`${pluginDirectory}/${name}`, "utf8");
const withoutComments = (code: string) => code.replace(/\/\/[^\n]*/g, "");
const pluginFiles = readdirSync(pluginDirectory).filter((name) => name.endsWith(".cs"));

// Body of the lambda passed to the call that starts at `start`.
function lambdaBody(code: string, start: number): string {
  const arrow = code.indexOf("=>", start);
  let k = arrow + 2;
  while (/\s/.test(code[k])) k++;
  if (code[k] !== "{") return code.slice(k, code.indexOf(";", k));
  let depth = 0;
  for (let j = k; j < code.length; j++) {
    if (code[j] === "{") depth++;
    if (code[j] === "}" && --depth === 0) return code.slice(k, j);
  }
  return code.slice(k);
}

// Civil 3D 2027 stops running ExecuteInCommandContextAsync callbacks for the
// rest of the session once any COM client has run SendCommand (verified live;
// see CivilExecution.RunOnHostAsync). Host work therefore runs from an
// Application.Idle tick (reads) or inside the plugin's C3DMCPHOP command.
describe("host routes", () => {
  const execution = withoutComments(source("CivilExecution.cs"));

  it("never uses the command-context hop", () => {
    for (const name of pluginFiles) {
      expect(withoutComments(source(name)), name).not.toMatch(/DocumentManager\s*\.\s*ExecuteInCommandContextAsync/);
    }
  });

  it("reads from Application.Idle with an OpenCloseTransaction, which keeps the undo history", () => {
    expect(execution).toMatch(/route == HostRoute\.Idle\s*\?\s*database\.TransactionManager\.StartOpenCloseTransaction\(\)/);
    expect(execution).toContain("needsCommand: write || inCommand");
    expect(execution).toMatch(/if \(needsCommand\)\s*\{\s*var commandDocument = targetDocument \?\? activeDocument[\s\S]*?dispatch\.TryDispatch\(\(\) => StartPluginCommandAsync\(commandDocument,/);
    expect(execution).toContain("await dispatch.DispatchDone;");
  });

  it("runs writes, command sequences and lock-only work inside the plugin's own command", () => {
    expect(execution.match(/needsCommand: true, needsQuietEditor: false/g)?.length).toBe(2);
    expect(execution).toContain('Civil3DCompatibility.InvokeComMethod(acadDocument, "SendCommand"');
    const entry = source("PluginEntry.cs");
    expect(entry).toContain("[CommandMethod(CivilExecution.PluginCommandName");
    // async void and awaited, so Editor.CommandAsync can run inside the command.
    expect(entry).toMatch(/public static async void HostHopCommand\(\)[\s\S]*await CivilExecution\.RunPluginCommandWorkAsync\(\)/);
  });

  it("fails fast while Civil 3D is busy instead of waiting for the request timeout", () => {
    expect(execution).toContain("HostBusyTimeout = TimeSpan.FromSeconds(15)");
    expect(execution).toContain('"CIVIL3D.HOST_BUSY"');
    expect(execution).toContain("Editor.IsQuiescent == false");
    const dispatch = withoutComments(source("HostDispatchState.cs"));
    expect(execution).toContain("dispatch.TryStart()");
    expect(execution).toContain("dispatch.TryAbandon()");
    expect(dispatch).toContain("Interlocked.CompareExchange(ref state, Started, NotStarted)");
    expect(dispatch).toContain("Interlocked.CompareExchange(ref state, Abandoned, NotStarted)");
  });

  // An OpenCloseTransaction is not rolled back on abort, and Idle ticks run
  // outside any command, so a plain read must not create scratch objects, use
  // the pickfirst selection or run commands. Those use ReadInCommandAsync or
  // ReadThenCommandLineAsync, and command-line input goes through
  // QueueCommandLine (sent over COM after the operation), never
  // SendStringToExecute, whose input mixes with C3DMCPHOP's.
  it("keeps scratch objects, pickfirst and command-line input out of plain reads", () => {
    const scratch = ["GetVolumeProperties(", "ComputeVolumeBetweenSurfaces(", "GetEarthworkVolumes(", "TinVolumeSurface.Create("];
    const pickfirst = ["SetImpliedSelection(", "SelectImplied(", "BuildSelectedCivilObjects("];
    const commandLine = ["QueueCommandLine(", "SendCommand(doc"];
    const writes = ["UpgradeOpen(", "OpenMode.ForWrite", ".Commit("];
    const offenders: string[] = [];
    const counts: Record<string, number> = {};
    for (const name of pluginFiles.filter((file) => file !== "CivilExecution.cs")) {
      const code = withoutComments(source(name));
      for (const match of code.matchAll(/CivilExecution\.(ReadAsync|ReadInCommandAsync|ReadThenCommandLineAsync)</g)) {
        const kind = match[1];
        counts[kind] = (counts[kind] ?? 0) + 1;
        const body = lambdaBody(code, match.index!);
        const has = (markers: string[]) => markers.filter((marker) => body.includes(marker));
        const bad =
          kind === "ReadAsync" ? [...has(scratch), ...has(pickfirst), ...has(commandLine), ...has(writes)]
          : kind === "ReadThenCommandLineAsync" ? [...has(scratch), ...has(pickfirst), ...has(writes)]
          : has(writes);
        if (bad.length > 0) offenders.push(`${name} ${kind}: ${bad.join(", ")}`);
      }
      const sendString = code.match(/SendStringToExecute\(/g)?.length ?? 0;
      // PlotCommands cancels a stuck -PLOT from inside its own command with ESC ESC.
      if (sendString > (name === "PlotCommands.cs" ? 1 : 0)) offenders.push(`${name}: SendStringToExecute`);
    }
    expect(offenders).toEqual([]);
    expect(counts.ReadInCommandAsync).toBe(11);
    expect(counts.ReadThenCommandLineAsync).toBe(6);
  });
});
