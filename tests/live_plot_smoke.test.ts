import { closeSync, existsSync, mkdtempSync, openSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { isAbsolute, join, resolve } from "node:path";
import ts from "typescript";
import { afterEach, describe, expect, it, vi } from "vitest";

const source = readFileSync(new URL("../scripts/live-plot-smoke.mjs", import.meta.url), "utf8");
const script = ts.createSourceFile("live-plot-smoke.mjs", source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
function declaration(name: string) {
  const node = script.statements.find((item) => ts.isFunctionDeclaration(item) && item.name?.text === name);
  if (!node) throw new Error(`Live smoke lacks ${name}`);
  return node.getText(script);
}
function variable(name: string) {
  const node = script.statements.find((item) => ts.isVariableStatement(item)
    && item.declarationList.declarations.some((decl) => ts.isIdentifier(decl.name) && decl.name.text === name));
  if (!node) throw new Error(`Live smoke lacks ${name}`);
  return node.getText(script);
}

const fixtureRunner = new AsyncFunction("deps", `
  const { run, check, missingDrawing, existsSync, statSync, readdirSync, isAbsolute, join, resolve,
    randomUUID, openSync, writeFileSync, readFileSync, closeSync, rmSync, console } = deps;
  ${variable("readPlotStyleProfile")}
  ${variable("saveMissingTableFixture")}
  ${declaration("createMissingTableFixture")}
  return await createMissingTableFixture("scratch.dwg", "Sheet");
`);

const tempFolders: string[] = [];
afterEach(() => {
  for (const folder of tempFolders.splice(0)) rmSync(folder, { recursive: true, force: true });
});
function fixtureEnvironment(colorDependent: boolean, runFixture?: (args: Record<string, string>) => unknown) {
  const folder = mkdtempSync(join(tmpdir(), "c3dmcp-plot-test-"));
  tempFolders.push(folder);
  const extension = colorDependent ? ".ctb" : ".stb";
  const original = join(folder, `installed${extension}`);
  writeFileSync(original, "installed style bytes");
  const copy = join(folder, `_c3dmcp-smoke-fixed-id${extension}`);
  const missingDrawing = join(folder, "missing.dwg");
  const run = vi.fn(async (code: string, args?: Record<string, string>, mode?: string) => {
    if (code.includes("PrinterStyleSheetPath")) return { path: `${join(folder, "absent")};${folder}`, colorDependent };
    expect(mode).toBe("write");
    expect(args).toEqual({ source: "scratch.dwg", layout: "Sheet", style: `_c3dmcp-smoke-fixed-id${extension}`, output: missingDrawing });
    expect(readFileSync(copy, "utf8")).toBe("installed style bytes");
    return runFixture ? runFixture(args!) : missingDrawing;
  });
  const check = vi.fn((name: string, condition: boolean) => { if (!condition) throw new Error(name); });
  const log = vi.fn();
  const deps = { run, check, missingDrawing, existsSync, statSync, readdirSync, isAbsolute, join, resolve,
    randomUUID: () => "fixed-id", openSync, writeFileSync, readFileSync, closeSync, rmSync,
    console: { error: log } };
  return { folder, original, copy, missingDrawing, run, check, log, deps };
}

describe("temporary plot style fixture", () => {
  it.each([true, false])("copies the active profile's %s mode table, saves a DWG, and removes only its copy", async (colorDependent) => {
    const env = fixtureEnvironment(colorDependent);
    await expect(fixtureRunner(env.deps)).resolves.toEqual({ savedPath: env.missingDrawing,
      missingTable: `_c3dmcp-smoke-fixed-id.${colorDependent ? "ctb" : "stb"}` });
    expect(existsSync(env.copy)).toBe(false);
    expect(readFileSync(env.original, "utf8")).toBe("installed style bytes");
    expect(env.check).toHaveBeenCalledWith("temporary plot style copy is gone", true, env.copy);
    expect(env.log).toHaveBeenCalledWith(`Temporary plot style table: ${env.copy}`);
  });

  it("removes its copy when the save fails", async () => {
    const env = fixtureEnvironment(true, () => { throw new Error("save failed"); });
    await expect(fixtureRunner(env.deps)).rejects.toThrow("save failed");
    expect(existsSync(env.copy)).toBe(false);
    expect(existsSync(env.original)).toBe(true);
  });

  it("never overwrites or removes a preexisting colliding file", async () => {
    const env = fixtureEnvironment(true);
    writeFileSync(env.copy, "preexisting");
    await expect(fixtureRunner(env.deps)).rejects.toThrow();
    expect(readFileSync(env.copy, "utf8")).toBe("preexisting");
    expect(env.run).toHaveBeenCalledTimes(1);
    expect(env.check).not.toHaveBeenCalled();
  });

  it("reports deletion failure instead of returning a saved fixture", async () => {
    const env = fixtureEnvironment(true);
    const rmSyncBlocked = vi.fn(() => { throw new Error("copy deletion denied"); });
    await expect(fixtureRunner({ ...env.deps, rmSync: rmSyncBlocked })).rejects.toThrow("temporary plot style copy is gone");
    expect(existsSync(env.copy)).toBe(true);
    expect(rmSyncBlocked).toHaveBeenCalledWith(env.copy);
    expect(env.check).toHaveBeenCalledWith("temporary plot style copy is gone", false, env.copy);
  });

  it("does not write if no compatible table is found", async () => {
    const env = fixtureEnvironment(true);
    rmSync(env.original);
    await expect(fixtureRunner(env.deps)).rejects.toThrow("No existing .ctb table");
    expect(readdirSync(env.folder)).toEqual([]);
    expect(env.run).toHaveBeenCalledTimes(1);
  });
});

const checkRunner = new AsyncFunction("deps", `
  const { createMissingTableFixture, missingDrawing, check, existsSync, result, call, terminal,
    startPublish, paths, readdirSync, outputFolder, prefix } = deps;
  ${declaration("checkMissingTableFixture")}
  await checkMissingTableFixture("scratch.dwg", "Sheet");
`);

async function smokeFixture(error = "Plot style table '_c3dmcp-smoke-test.ctb' for layout 'Sheet' was not found.", files: string[] = [], state = "failed") {
  const prefix = "smoke";
  const outputFolder = "outputs";
  const missingDrawing = join(outputFolder, `${prefix}-missing.dwg`);
  const outputPath = join(outputFolder, `${prefix}-missing-table.pdf`);
  const missingTable = "_c3dmcp-smoke-test.ctb";
  const createMissingTableFixture = vi.fn(async () => ({ savedPath: missingDrawing, missingTable }));
  const startPublish = vi.fn(async (_path, _layouts, options) => {
    expect(createMissingTableFixture).toHaveBeenCalledWith("scratch.dwg", "Sheet");
    expect(_path).toBe(outputPath);
    expect(_layouts).toEqual(["Sheet"]);
    expect(options).toEqual({ document: missingDrawing, keepDsd: true });
    expect(options).not.toHaveProperty("requireSaved");
    return "job";
  });
  const call = vi.fn(async (tool, options) => {
    expect(tool).toBe("civil3d_plot");
    expect(options).toEqual({ action: "list_layouts", document: missingDrawing });
    return { layouts: [{ name: "Sheet", plotStyleTable: missingTable, ctbFound: false, initialized: true }] };
  });
  const check = vi.fn((name, condition) => { if (!condition) throw new Error(name); });
  await checkRunner({ createMissingTableFixture, missingDrawing, check, existsSync: (path: string) => path === missingDrawing,
    result: (response: unknown) => response, call, terminal: async () => ({ state, result: { error } }), startPublish,
    paths: { "missing-table": outputPath }, readdirSync: () => files, outputFolder, prefix });
  expect(startPublish).toHaveBeenCalledOnce();
  expect(check).toHaveBeenCalledTimes(3);
}

describe("saved missing-table refusal", () => {
  it("publishes with default requireSaved and retains DSD evidence", async () => { await smokeFixture(); });
  it.each(["Drawing has unsaved changes.", "Plot style table 'other.ctb' for layout 'Sheet' was not found.",
    "Plot style table '_c3dmcp-smoke-test.ctb' for layout 'Other' was not found."])
  ("rejects an unrelated refusal: %s", async (error) => {
    await expect(smokeFixture(error)).rejects.toThrow("saved missing-table publish");
  });
  it.each(["smoke-missing-table.pdf", "smoke-missing-table.mcp-publish.dsd"])
  ("fails if publish wrote %s", async (file) => {
    await expect(smokeFixture(undefined, [file])).rejects.toThrow("saved missing-table publish");
  });
  it("fails if validation was removed and publishing completes", async () => {
    await expect(smokeFixture(undefined, [], "completed")).rejects.toThrow("saved missing-table publish");
  });
  it("does not publish when fixture copy cleanup fails", async () => {
    const startPublish = vi.fn();
    await expect(checkRunner({ createMissingTableFixture: async () => { throw new Error("copy deletion denied"); },
      startPublish })).rejects.toThrow("copy deletion denied");
    expect(startPublish).not.toHaveBeenCalled();
  });
});

describe("runner control flow", () => {
  it("defaults the profile fixture off and records exactly one skip", () => {
    expect(source).toContain('"ctb-fixture": { type: "boolean", default: false }');
    expect(source).toMatch(/if \(!options\["ctb-fixture"\]\)\s*\{\s*skip\("missing CTB\/STB fixture"/);
    expect(source.match(/skip\("missing CTB\/STB fixture"/g)).toHaveLength(1);
    expect(source).toMatch(/if \(options\["ctb-fixture"\]\)\s*\{\s*await attempt\("saved missing-table fixture", \(\) => checkMissingTableFixture/);
    expect(source).toContain("createdPaths.push(missingDrawing)");
  });

  it("records a failed attempt and continues to cancellation and health", async () => {
    const results: unknown[] = [];
    const errors: string[] = [];
    const process = { exitCode: 0 };
    const run = new AsyncFunction("deps", `
      const { results, process, errors } = deps;
      const console = { error: (line) => errors.push(line) };
      const printServerStderr = () => {};
      ${declaration("fail")}
      ${declaration("attempt")}
      await attempt("fixture", async () => { throw new Error("fixture failed"); });
      await attempt("cancel", async () => { results.push({ name: "cancel reached", passed: true }); });
      await attempt("health", async () => { results.push({ name: "health reached", passed: true }); });
    `);
    await run({ results, process, errors });
    expect(results).toEqual([{ name: "fixture", passed: false, error: "fixture failed" },
      { name: "cancel reached", passed: true }, { name: "health reached", passed: true }]);
    expect(process.exitCode).toBe(1);
    expect(errors).toContain("FAIL fixture: fixture failed");
    const fixture = source.indexOf('await attempt("saved missing-table fixture"');
    expect(source.indexOf('await attempt("in-flight cancellation"', fixture)).toBeGreaterThan(fixture);
    expect(source.indexOf('await attempt("health after cancellation"', fixture)).toBeGreaterThan(fixture);
    expect(source.indexOf("// Never delete outputs", fixture)).toBeGreaterThan(fixture);
  });

  it("records a failed assertion once when an attempt catches it", async () => {
    const results: unknown[] = [];
    const process = { exitCode: 0 };
    const run = new AsyncFunction("deps", `
      const { results, process } = deps;
      const console = { error: () => {} };
      const printServerStderr = () => {};
      ${declaration("fail")}
      ${declaration("check")}
      ${declaration("attempt")}
      await attempt("fixture", async () => check("fixture assertion", false, { cause: "bad refusal" }));
    `);
    await run({ results, process });
    expect(results).toEqual([{ name: "fixture assertion", passed: false,
      error: 'fixture assertion: {"cause":"bad refusal"}' }]);
    expect(process.exitCode).toBe(1);
  });
});

const mainTry = script.statements.find((item) => ts.isTryStatement(item)
  && item.tryBlock.getText(script).includes("await client.connect(transport)"));
if (!mainTry) throw new Error("Live smoke lacks the main runner");
const defaultSkip = script.statements.find((item) => ts.isIfStatement(item)
  && item.expression.getText(script) === '!options["ctb-fixture"]');
if (!defaultSkip) throw new Error("Live smoke lacks the default fixture skip");
const mainRunner = new AsyncFunction("deps", `
  const { options, client, transport, results, process, console, call, result, run,
    existsSync, isAbsolute, startPublish, terminal, paths, verifyPdf,
    checkMissingTableFixture, checkUnsavedRevision, checkCancellation,
    jobs, createdPaths, rmSync, delay, printServerStderr, setStyle, prefix, attachScratchUserInstance } = deps;
  let document, styleRestore, cleanupFailed = false;
  ${declaration("fail")}
  ${declaration("check")}
  ${declaration("attempt")}
  ${declaration("skip")}
  ${defaultSkip.getText(script)}
  ${mainTry.getText(script)}
  return { results, cleanupFailed };
`);

async function runMain(fixtureEnabled: boolean, fixtureFails: boolean) {
  const results: Array<Record<string, unknown>> = [];
  const process = { exitCode: 0 };
  const events: string[] = [];
  const selected = ["Sheet 1", "Sheet 2"].map((name) => ({ name, initialized: true,
    ctbFound: true, mediaAvailable: true, plotStyleTable: "installed.ctb",
    paperWidth: 210, paperHeight: 297, isModel: false }));
  const call = vi.fn(async (tool: string, args: Record<string, unknown>) => {
    if (tool === "civil3d_health") { events.push("health"); return { plotApi: 2, connected: true }; }
    if (tool === "civil3d_plot" && args.action === "list_layouts") return { layouts: selected };
    throw new Error(`Unexpected ${tool}`);
  });
  const checkMissingTableFixture = vi.fn(async () => {
    events.push("fixture");
    if (fixtureFails) throw new Error("fixture save failed");
  });
  const checkCancellation = vi.fn(async () => { events.push("cancellation"); });
  const rmSync = vi.fn();
  const deps = {
    options: { "expect-drawing": "scratch", "ctb-fixture": fixtureEnabled },
    attachScratchUserInstance: vi.fn(async () => { events.push("attach"); }),
    client: { connect: vi.fn(async () => {}), close: vi.fn(async () => { events.push("close"); }) },
    transport: {}, results, process, console: { error: vi.fn() }, call,
    result: (value: unknown) => value,
    run: vi.fn(async () => ({ name: "scratch.dwg", filename: "C:/scratch.dwg", dbmod: 0 })),
    existsSync: (path: string) => path === "C:/scratch.dwg", isAbsolute: () => true,
    startPublish: vi.fn(async () => "job"),
    terminal: vi.fn(async () => ({ state: "completed", result: { verified: true,
      sheetCount: 2, pageCount: 2, sheets: selected.map((layout) => ({
        layoutName: layout.name, drawingPath: "C:/scratch.dwg", device: "PDF",
        media: "A4", plotStyleTable: "installed.ctb", pageSetup: null,
        paperSizeMm: [210, 297],
      })) } })),
    paths: { set: "set.pdf" }, verifyPdf: vi.fn(), checkMissingTableFixture,
    checkUnsavedRevision: vi.fn(async () => { events.push("unsaved"); }),
    checkCancellation, jobs: new Map(), createdPaths: ["set.pdf"], rmSync,
    delay: async () => {}, printServerStderr: vi.fn(), setStyle: "", prefix: "smoke",
  };
  const state = await mainRunner(deps);
  return { state, events, checkMissingTableFixture, checkCancellation, call, rmSync, process };
}

describe("whole live runner with mocked tools", () => {
  it("defaults to one skip without touching the profile and still runs cancellation, health, cleanup", async () => {
    const run = await runMain(false, false);
    expect(run.checkMissingTableFixture).not.toHaveBeenCalled();
    expect(run.state.results.filter((entry: Record<string, unknown>) => entry.skipped)).toHaveLength(1);
    expect(run.events).toEqual(["attach", "health", "unsaved", "cancellation", "health", "close"]);
    expect(run.state.results).toContainEqual({ name: "smoke output files are gone", passed: true });
    expect(run.process.exitCode).toBe(0);
  });

  it("records fixture failure and still runs unsaved, cancellation, health, cleanup", async () => {
    const run = await runMain(true, true);
    expect(run.events).toEqual(["attach", "health", "fixture", "unsaved", "cancellation", "health", "close"]);
    expect(run.state.results).toContainEqual({ name: "saved missing-table fixture", passed: false,
      error: "fixture save failed" });
    expect(run.state.results).toContainEqual({ name: "smoke output files are gone", passed: true });
    expect(run.process.exitCode).toBe(1);
  });
});
