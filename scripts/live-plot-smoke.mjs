// Run only against a saved SCRATCH drawing with two initialized PDF layouts.
// Disable the PC3's open-in-viewer option before running this live check.
//   npm run build
//   node scripts/live-plot-smoke.mjs --expect-drawing <scratch-name> --out <export-folder>
// --out must be inside both import and export roots for the disposable DWG.
// No open drawing is saved; only the disposable side database is saved.
// --ctb-fixture temporarily copies a table into the profile's plot style folder.
import { closeSync, existsSync, openSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { randomUUID } from "node:crypto";
import { isAbsolute, join, resolve } from "node:path";
import { parseArgs } from "node:util";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { drainServerStderr } from "./server-stderr.mjs";

const { values: options } = parseArgs({
  options: { "expect-drawing": { type: "string" }, out: { type: "string" }, "ctb-fixture": { type: "boolean", default: false } },
  strict: true,
});
if (!options["expect-drawing"]?.trim() || !options.out || !isAbsolute(options.out)
    || !existsSync(options.out) || !statSync(options.out).isDirectory()) {
  console.error("Requires --expect-drawing <scratch-name> and --out <existing absolute folder inside export roots>.");
  process.exit(2);
}

const outputFolder = resolve(options.out);
const prefix = `mcp-plot-smoke-${randomUUID()}`;
const paths = Object.fromEntries(["set", "missing-table", "unsaved", "saved-revision", "cancel"].map((name) => [name, join(outputFolder, `${prefix}-${name}.pdf`)]));
const missingDrawing = join(outputFolder, `${prefix}-missing.dwg`);
const createdPaths = Object.values(paths).flatMap((path) => [path, path.replace(/\.pdf$/i, ".mcp-publish.dsd")]);
createdPaths.push(missingDrawing);
if (createdPaths.some(existsSync)) throw new Error("Unique smoke output already exists; stopping before connecting.");
const transport = new StdioClientTransport({
  command: process.execPath,
  args: ["./build/index.js"],
  cwd: process.cwd(),
  env: { ...process.env, MCP_HTTP_HOST: "127.0.0.1", MCP_HTTP_PORT: "0", CIVIL3D_RUN_CODE_APPROVAL: "token" },
  stderr: "pipe",
});
const printServerStderr = drainServerStderr(transport.stderr);
const client = new Client({ name: "civil3d-plot-smoke", version: "1.0.0" });
const jobs = new Map();
const results = [];
let document;
let styleRestore;
let cleanupFailed = false;
const delay = (ms) => new Promise((done) => setTimeout(done, ms));
const responseText = (response) => (response.content ?? []).map((item) => item.text ?? "").join("\n");

async function call(name, parameters, timeout = 180_000) {
  return await client.callTool({ name, arguments: parameters }, undefined, { timeout, maxTotalTimeout: timeout });
}
function result(response) {
  if (response.isError) throw new Error(responseText(response));
  if (response.structuredContent?.result === undefined) throw new Error(`Missing structured result: ${responseText(response)}`);
  return response.structuredContent.result;
}
async function gated(name, action, parameters) {
  const approval = await call("civil3d_request_approval", { toolName: name, action, parameters });
  if (approval.isError || !approval.structuredContent?.approvalToken) throw new Error(`Approval failed: ${responseText(approval)}`);
  return await call(name, { ...parameters, approvalToken: approval.structuredContent.approvalToken });
}
async function run(code, args = {}, mode = "read") {
  const parameters = { mode, code, args, ...(document ? { document } : {}) };
  const response = result(await gated("civil3d_run_code", "run", parameters));
  if (response.status !== "ok" || (mode === "write" && response.committed !== true)) {
    throw new Error(`run_code ${response.status}: ${JSON.stringify(response.errors ?? response.error)}`);
  }
  return response.result;
}
function check(name, condition, detail) {
  if (!condition) {
    const error = new Error(`${name}: ${JSON.stringify(detail)}`);
    fail(name, error);
    error.recorded = true;
    throw error;
  }
  results.push({ name, passed: true });
  console.error(`PASS ${name}`);
}
function fail(name, error) {
  const message = error instanceof Error ? error.message : String(error);
  results.push({ name, passed: false, error: message });
  console.error(`FAIL ${name}: ${message}`);
  process.exitCode = 1;
}
async function attempt(name, action) {
  try { return await action(); }
  catch (error) {
    printServerStderr();
    if (!error?.recorded) fail(name, error);
    return undefined;
  }
}
function skip(name, reason) {
  results.push({ name, skipped: true, reason });
  console.error(`SKIP ${name}: ${reason}`);
}
async function startPublish(outputPath, layoutNames, extra = {}) {
  if ([...jobs.values()].some((job) => job.state === "running")) {
    throw new Error("A previous publish is still running; wait for cleanup before another native publish.");
  }
  const parameters = { action: "publish_sheet_set", document, layoutNames, outputPath, asJob: true, ...extra };
  const handle = result(await gated("civil3d_plot", "publish_sheet_set", parameters));
  if (!handle.jobId) throw new Error(`Missing publish job: ${JSON.stringify(handle)}`);
  jobs.set(handle.jobId, handle);
  return handle.jobId;
}
async function status(jobId) {
  const snapshot = result(await call("civil3d_job", { action: "status", jobId }, 30_000));
  jobs.set(jobId, snapshot);
  return snapshot;
}
async function terminal(jobId) {
  const deadline = Date.now() + 15 * 60_000;
  while (Date.now() < deadline) {
    const snapshot = await status(jobId);
    if (snapshot.state !== "running") return snapshot;
    await delay(250);
  }
  throw new Error(`Job ${jobId} still running after 15 minutes; do not remove its files or retry publishing.`);
}

// Independent raw-PDF check: the live set must be inspectable, not just nonempty.
// Regex is intentionally limited to ordinary PDF objects, matching the plugin.
function pdfPages(path) {
  const bytes = readFileSync(path);
  const text = bytes.toString("latin1");
  if (!text.startsWith("%PDF-") || /\/Type\s*\/ObjStm\b/.test(text)) throw new Error("Live PDF cannot be verified by the regex check (object streams or missing PDF header).");
  const objects = new Map([...text.matchAll(/(\d+)\s+\d+\s+obj\b([\s\S]*?)endobj/g)].map((match) => [match[1], match[2]]));
  function box(id, seen = new Set()) {
    if (seen.has(id)) throw new Error("Cyclic PDF page parent");
    seen.add(id);
    const body = objects.get(id);
    const match = body?.match(/\/MediaBox\s*\[\s*([-+\d.]+)\s+([-+\d.]+)\s+([-+\d.]+)\s+([-+\d.]+)\s*\]/);
    if (match) return match.slice(1).map(Number);
    const parent = body?.match(/\/Parent\s+(\d+)\s+\d+\s+R\b/);
    if (parent) return box(parent[1], seen);
    throw new Error(`PDF page ${id} lacks a readable MediaBox`);
  }
  const root = [...objects].find(([, body]) => /\/Type\s*\/Catalog\b/.test(body))?.[1].match(/\/Pages\s+(\d+)\s+\d+\s+R\b/)?.[1];
  const ordered = [];
  function walk(id, seen = new Set()) {
    if (seen.has(id)) throw new Error("Cyclic or duplicate PDF page tree");
    seen.add(id);
    const body = objects.get(id);
    if (/\/Type\s*\/Page\b/.test(body ?? "")) { ordered.push(box(id)); return; }
    const kids = body?.match(/\/Kids\s*\[([\s\S]*?)\]/)?.[1];
    if (!kids) throw new Error(`PDF pages object ${id} has no readable Kids`);
    for (const child of kids.matchAll(/(\d+)\s+\d+\s+R\b/g)) walk(child[1], seen);
  }
  if (!root) throw new Error("PDF has no readable page tree");
  walk(root);
  return ordered;
}
async function verifyPdf(path, expectedSheets) {
  const pages = pdfPages(path);
  await attempt("published PDF page count", () =>
    check("published PDF has exactly two pages", pages.length === 2 && expectedSheets.length === 2, { pages: pages.length }));
  for (const [index, sheet] of expectedSheets.entries()) {
    await attempt(`page ${index + 1} paper size`, () => {
      const box = pages[index] ?? [];
      const actual = [Math.abs(box[2] - box[0]), Math.abs(box[3] - box[1])].sort((a, b) => a - b);
      const expected = sheet.paperSizeMm.map((mm) => mm * 72 / 25.4).sort((a, b) => a - b);
      check(`page ${index + 1} matches the sheet paper size within 1 pt`, actual.every((pt, axis) => Number.isFinite(pt) && Math.abs(pt - expected[axis]) <= 1), { actual, expected });
    });
  }
}

// CurrentStyleSheet is get-only; the validator is the documented managed setter.
const setStyle = [
  'var dictionary = Open<DBDictionary>(Db.LayoutDictionaryId);',
  'var layout = Open<Layout>(dictionary.GetAt((string)Args["layout"]!), forWrite: true);',
  'PlotSettingsValidator.Current.SetCurrentStyleSheet(layout, (string)Args["style"]!);',
  'layout.PlotPlotStyles = (bool)Args["plotStyles"]!;',
  'return layout.CurrentStyleSheet;',
].join("\n");

// Commit the side database's edit before saving; leave the open scratch alone.
const saveMissingTableFixture = `
using var database = new Database(false, true);
database.ReadDwgFile((string)Args["source"]!, FileOpenMode.OpenForReadAndAllShare, true, "");
database.CloseInput(true);
using (var transaction = database.TransactionManager.StartTransaction())
{
  var dictionary = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
  var layout = (Layout)transaction.GetObject(dictionary.GetAt((string)Args["layout"]!), OpenMode.ForWrite);
  PlotSettingsValidator.Current.RefreshLists(layout);
  PlotSettingsValidator.Current.SetCurrentStyleSheet(layout, (string)Args["style"]!);
  transaction.Commit();
}
database.SaveAs((string)Args["output"]!, DwgVersion.Current);
return (string)Args["output"]!;
`;

// Read the active profile, rather than guessing a version-specific folder.
const readPlotStyleProfile = `
dynamic preferences = Application.Preferences;
return new { path = (string)preferences.Files.PrinterStyleSheetPath, colorDependent = Db.PlotStyleMode };
`;

async function createMissingTableFixture(source, layout) {
  const profile = await run(readPlotStyleProfile);
  const extension = profile.colorDependent ? ".ctb" : ".stb";
  const folders = profile.path.split(";").map((folder) => folder.trim()).filter(Boolean);
  let existingTable;
  for (const folder of folders) {
    if (!isAbsolute(folder) || !existsSync(folder) || !statSync(folder).isDirectory()) continue;
    const table = readdirSync(folder).find((name) => name.toLowerCase().endsWith(extension)
      && !name.startsWith("_c3dmcp-smoke-") && statSync(join(folder, name)).isFile());
    if (table) { existingTable = join(folder, table); break; }
  }
  if (!existingTable) throw new Error(`No existing ${extension} table in profile PrinterStyleSheetPath: ${profile.path}`);
  const missingTable = `_c3dmcp-smoke-${randomUUID()}${extension}`;
  const copiedTable = join(resolve(existingTable, ".."), missingTable);
  console.error(`Temporary plot style table: ${copiedTable}`);
  let owned = false;
  let savedPath;
  try {
    // Exclusive creation means a collision can never overwrite or remove a table.
    const descriptor = openSync(copiedTable, "wx");
    owned = true;
    try { writeFileSync(descriptor, readFileSync(existingTable)); }
    finally { closeSync(descriptor); }
    savedPath = await run(saveMissingTableFixture,
      { source, layout, style: missingTable, output: missingDrawing }, "write");
  } finally {
    if (owned) {
      try { rmSync(copiedTable); }
      finally { check("temporary plot style copy is gone", !existsSync(copiedTable), copiedTable); }
    }
  }
  return { savedPath, missingTable };
}

async function checkMissingTableFixture(source, layout) {
  const { savedPath, missingTable } = await createMissingTableFixture(source, layout);
  check("disposable missing-table DWG is saved", savedPath === missingDrawing && existsSync(missingDrawing), savedPath);
  const discovery = result(await call("civil3d_plot", { action: "list_layouts", document: missingDrawing }));
  const savedLayout = discovery.layouts.find((item) => item.name === layout);
  check("saved fixture contains the missing plot style", savedLayout?.plotStyleTable === missingTable
    && savedLayout.ctbFound === false && savedLayout.initialized === true, savedLayout);
  // Retain any DSD so a late native failure cannot masquerade as preflight.
  const refused = await terminal(await startPublish(paths["missing-table"], [layout],
    { document: missingDrawing, keepDsd: true }));
  const refusal = JSON.stringify(refused.result);
  check("saved missing-table publish names the table and layout before writing anything", refused.state === "failed"
    && refusal.includes(`Plot style table '${missingTable}'`)
    && refusal.includes(`layout '${layout}'`)
    && /was not found/i.test(refusal)
    && !readdirSync(outputFolder).some((name) => name.includes(`${prefix}-missing-table`)), refused);
}

async function checkCancellation(layoutNames) {
  const cancelId = await startPublish(paths.cancel, layoutNames, { requireSaved: false });
  // Observe native setup before cancellation, instead of cancelling the queued delay.
  const startDeadline = Date.now() + 30_000;
  while (!existsSync(paths.cancel.replace(/\.pdf$/i, ".mcp-publish.dsd"))) {
    const snapshot = await status(cancelId);
    if (snapshot.state !== "running") throw new Error("Publish finished before an in-flight cancellation could be tested; use slower scratch sheets.");
    if (Date.now() >= startDeadline) throw new Error("Publish did not start within 30 seconds");
    await delay(50);
  }
  const cancelled = result(await call("civil3d_job", { action: "cancel", jobId: cancelId }, 30_000));
  jobs.set(cancelId, cancelled);
  const finished = await terminal(cancelId);
  check("cancelled native publish drains before reporting terminal status", finished.state === "cancelled"
    || (finished.state === "completed" && (finished.warnings ?? []).some((warning) => /cancellation.*completed/i.test(warning))), finished);
}

async function checkUnsavedRevision(info, selected) {
  const layoutNames = selected.map((layout) => layout.name);
  const plotStyles = await run([
    'var dictionary = Open<DBDictionary>(Db.LayoutDictionaryId);',
    'var layout = Open<Layout>(dictionary.GetAt((string)Args["layout"]!));',
    'return layout.PlotPlotStyles;',
  ].join("\n"), { layout: selected[0].name });
  styleRestore = { layout: selected[0].name, style: selected[0].plotStyleTable, plotStyles };
  try {
    // Use an installed style and a reversible flag change; never assign a missing table.
    await run(setStyle, { ...styleRestore, plotStyles: !plotStyles }, "write");
    await attempt("unsaved changes refusal", async () => {
      const refused = await terminal(await startPublish(paths.unsaved, layoutNames));
      const refusal = JSON.stringify(refused.result);
      check("unsaved changes refuse the job before writing anything", refused.state === "failed"
        && /unsaved changes/i.test(refusal)
        && !readdirSync(outputFolder).some((name) => name.includes(`${prefix}-unsaved`)), refused);
    });
    await attempt("saved-revision publish", async () => {
      const saved = await terminal(await startPublish(paths["saved-revision"], layoutNames, { requireSaved: false }));
      check("saved-revision publish uses the saved plot style", saved.state === "completed"
        && saved.result?.verified === true
        && saved.result.sheets?.[0]?.plotStyleTable === styleRestore.style
        && saved.result.warnings?.some((warning) => /unsaved changes.*saved revision/i.test(warning)), saved);
    });
    await attempt("saved-revision PDF verification", () => verifyPdf(paths["saved-revision"],
      selected.map((layout) => ({ paperSizeMm: [layout.paperWidth, layout.paperHeight] }))));
  } finally {
    await attempt("restore scratch layout", async () => {
      const restored = await run(setStyle, styleRestore, "write");
      check("scratch layout settings are restored", restored === styleRestore.style, restored);
      styleRestore = undefined;
    });
  }
}

if (!options["ctb-fixture"]) {
  skip("missing CTB/STB fixture", "requires --ctb-fixture to temporarily copy a table into the profile plot style folder; no files outside --out are touched");
}
try {
  await client.connect(transport);
  await attempt("plot API health", async () => {
    const health = result(await call("civil3d_health", {}, 30_000));
    check("loaded plugin supports plot API 2", health.plotApi >= 2, { plotApi: health.plotApi });
  });
  const info = await attempt("scratch drawing safety guard", async () => {
    const info = await run('return new { name = Doc.Name, filename = Db.Filename, dbmod = Convert.ToInt32(Application.GetSystemVariable("DBMOD")) };');
    if (typeof info.name !== "string" || !info.name.toLowerCase().includes(options["expect-drawing"].trim().toLowerCase())) {
      throw new Error(`Active drawing '${info.name}' does not match --expect-drawing; stopped before writes.`);
    }
    if (!isAbsolute(info.filename ?? "") || !existsSync(info.filename) || info.dbmod !== 0) throw new Error("Save the scratch drawing with its finished page setups before this check.");
    document = info.name;
    return info;
  });
  // A failed safety guard prevents drawing writes, but health and cleanup still run.
  if (info) {
    const selected = await attempt("select scratch layouts", async () => {
      const discovery = result(await call("civil3d_plot", { action: "list_layouts", document }));
      await attempt("layout preflight flags", () => check("targeted list_layouts returns per-layout preflight flags", discovery.layouts.length >= 2
        && discovery.layouts.every((layout) => typeof layout.initialized === "boolean"
          && typeof layout.ctbFound === "boolean" && typeof layout.mediaAvailable === "boolean"), discovery));
      const selected = discovery.layouts.filter((layout) => !layout.isModel && layout.initialized === true
        && layout.ctbFound && layout.mediaAvailable && layout.plotStyleTable && layout.paperWidth > 0 && layout.paperHeight > 0).slice(0, 2);
      if (selected.length !== 2) throw new Error("Scratch drawing needs two initialized paper-space layouts with installed CTB/STB and PDF media.");
      return selected;
    });
    if (selected) {
      const layoutNames = selected.map((layout) => layout.name);
      const published = await attempt("two-sheet publish", async () => {
        const published = await terminal(await startPublish(paths.set, layoutNames));
        // Return the snapshot even on assertion failure so independent checks can inspect it.
        await attempt("publish result", () => check("two-sheet publish job completes and verifies", published.state === "completed" && published.result?.verified === true
          && published.result?.sheetCount === 2 && published.result?.pageCount === 2, published));
        return published;
      });
      await attempt("resolved publish settings", () => check("publish echoes resolved settings for both sheets", published?.result?.sheets?.length === 2
        && published.result.sheets.every((sheet, index) => sheet.layoutName === layoutNames[index]
          && sheet.drawingPath.toLowerCase() === info.filename.toLowerCase() && typeof sheet.device === "string"
          && typeof sheet.media === "string" && typeof sheet.plotStyleTable === "string" && Object.hasOwn(sheet, "pageSetup")
          && sheet.paperSizeMm?.length === 2), published?.result));
      await attempt("published PDF verification", () => verifyPdf(paths.set,
        selected.map((layout) => ({ paperSizeMm: [layout.paperWidth, layout.paperHeight] }))));
      if (options["ctb-fixture"]) {
        await attempt("saved missing-table fixture", () => checkMissingTableFixture(info.filename, selected[0].name));
      }
      await attempt("unsaved and saved-revision checks", () => checkUnsavedRevision(info, selected));
      await attempt("in-flight cancellation", () => checkCancellation(layoutNames));
    }
  }
} catch (error) {
  printServerStderr();
  fail("live plot smoke setup", error);
} finally {
  await attempt("health after cancellation", async () => {
    const after = result(await call("civil3d_health", {}, 30_000));
    check("Civil 3D still answers health after cancellation", after.connected === true, after);
  });
  // Never delete outputs while a native publisher may still be writing them.
  for (const [jobId, snapshot] of jobs) {
    if (snapshot.state === "running") {
      try {
        result(await call("civil3d_job", { action: "cancel", jobId }, 30_000));
        await terminal(jobId);
      } catch (error) { cleanupFailed = true; fail(`job cleanup ${jobId}`, error); }
    }
  }
  if (styleRestore) {
    try {
      const restored = await run(setStyle, styleRestore, "write");
      check("scratch layout settings are restored during cleanup", restored === styleRestore.style, restored);
      styleRestore = undefined;
    }
    catch (error) { cleanupFailed = true; if (!error?.recorded) fail("restore scratch layout during cleanup", error); }
  }
  if (![...jobs.values()].some((job) => job.state === "running")) {
    for (const path of createdPaths) {
      try { if (existsSync(path)) rmSync(path); }
      catch (error) { cleanupFailed = true; fail(`remove '${path}'`, error); }
    }
    await attempt("output cleanup", () => check("smoke output files are gone", !createdPaths.some(existsSync), createdPaths.filter(existsSync)));
  } else { cleanupFailed = true; fail("output cleanup", new Error(`Native work remains active; preserved smoke files with prefix '${prefix}'.`)); }
  await Promise.race([client.close().catch(() => undefined), delay(5000)]);
}
if (cleanupFailed) process.exitCode = 1;
console.log(JSON.stringify({ status: process.exitCode ? "failed" : "passed",
  passed: results.filter((item) => item.passed === true).length,
  failed: results.filter((item) => item.passed === false).length,
  skipped: results.filter((item) => item.skipped).length, cleanupFailed, results }, null, 2));
process.exit(process.exitCode ?? 0);
