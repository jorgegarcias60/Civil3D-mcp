import { readFileSync } from "node:fs";
import { join } from "node:path";
import ts from "typescript";
import { describe, expect, it, vi } from "vitest";

const source = readFileSync(new URL("../scripts/live-plot-smoke.mjs", import.meta.url), "utf8");
const script = ts.createSourceFile("live-plot-smoke.mjs", source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
const fixtureCheck = script.statements.find((node) => ts.isFunctionDeclaration(node)
  && node.name?.text === "checkMissingTableFixture");
if (!fixtureCheck) throw new Error("Live smoke lacks the saved missing-table check");
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
const execute = new AsyncFunction("run", "saveMissingTableFixture", "missingDrawing", "check", "existsSync",
  "result", "call", "terminal", "startPublish", "paths", "readdirSync", "outputFolder", "prefix",
  `${fixtureCheck.getText(script)}; await checkMissingTableFixture("scratch.dwg", "Sheet", "missing.ctb");`);

async function smokeFixture(error = "Plot style table 'missing.ctb' for layout 'Sheet' was not found.", files: string[] = [], state = "failed") {
  const prefix = "smoke";
  const outputFolder = "outputs";
  const missingDrawing = join(outputFolder, `${prefix}-missing.dwg`);
  const outputPath = join(outputFolder, `${prefix}-missing-table.pdf`);
  const run = vi.fn(async (_code, args, mode) => {
    expect(args).toEqual({ source: "scratch.dwg", layout: "Sheet", style: "missing.ctb", output: missingDrawing });
    expect(mode).toBe("write");
    return missingDrawing;
  });
  const startPublish = vi.fn(async (_path, _layouts, options) => {
    expect(run).toHaveBeenCalledOnce();
    expect(_path).toBe(outputPath);
    expect(_layouts).toEqual(["Sheet"]);
    expect(options).toEqual({ document: missingDrawing, keepDsd: true });
    expect(options).not.toHaveProperty("requireSaved");
    return "job";
  });
  const call = vi.fn(async (tool, options) => {
    expect(tool).toBe("civil3d_plot");
    expect(options).toEqual({ action: "list_layouts", document: missingDrawing });
    return { layouts: [{ name: "Sheet", plotStyleTable: "missing.ctb", ctbFound: false, initialized: true }] };
  });
  const check = vi.fn((name, condition) => { if (!condition) throw new Error(name); });
  await execute(run, "fixture code", missingDrawing, check, (path: string) => path === missingDrawing,
    (response: unknown) => response, call, async () => ({ state, result: { error } }), startPublish,
    { "missing-table": outputPath }, () => files, outputFolder, prefix);
  expect(startPublish).toHaveBeenCalledOnce();
  expect(check).toHaveBeenCalledTimes(3);
}

describe("live plot saved missing-table check", () => {
  it("publishes the saved fixture with default requireSaved and retains evidence of any DSD write", async () => {
    await smokeFixture();
  });

  it.each([
    "Drawing 'scratch.dwg' has unsaved changes.",
    "Plot style table 'other.ctb' for layout 'Sheet' was not found.",
    "Plot style table 'missing.ctb' for layout 'Other' was not found.",
  ])("rejects an unrelated refusal: %s", async (error) => {
    await expect(smokeFixture(error)).rejects.toThrow("saved missing-table publish");
  });

  it.each(["smoke-missing-table.pdf", "smoke-missing-table.mcp-publish.dsd"])("fails if publish wrote %s", async (file) => {
    await expect(smokeFixture(undefined, [file])).rejects.toThrow("saved missing-table publish");
  });

  it("fails if missing-table validation was removed and publishing completes", async () => {
    await expect(smokeFixture(undefined, [], "completed")).rejects.toThrow("saved missing-table publish");
  });

  it("registers the disposable DWG for job-drained cleanup and runs the saved check before unsaved edits", () => {
    expect(source).toContain("createdPaths.push(missingDrawing)");
    const invocation = source.indexOf("await checkMissingTableFixture(info.filename");
    expect(invocation).toBeGreaterThan(-1);
    expect(invocation).toBeLessThan(source.indexOf("styleRestore = { layout:"));
    expect(source).toContain("for (const path of createdPaths)");
  });
});
