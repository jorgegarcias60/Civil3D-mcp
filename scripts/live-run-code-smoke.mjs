// Live smoke test of civil3d_run_code through the real MCP server (build/index.js)
// and the plugin in a running Civil 3D. Run it against a SCRATCH drawing: the
// write cases add and remove text objects in scratch drawings. Active-drawing
// cases send one undo; a targeted write cleans up in its target drawing.
//
//   npm run build
//   node scripts/live-run-code-smoke.mjs --user-instance --expect-drawing <name> [--side <drawing.dwg>] [--approval token|client]
//     [--long <seconds>] [--damaged <directory>] [--other <open drawing path>] [--no-drawing] [--hardening] [--helpers]
//     [--preview] [--other <open scratch drawing path>]
//
// --expect-drawing: attach only to the unique user process containing this scratch
//   drawing name or full path. --no-drawing instead requires an empty user process.
//   --other must name a disposable open
//   drawing too when writes are enabled: the PR 4 case commits and erases text there.
// --side: a DWG read as a side database (never opened, never changed) for the
//   OpenSide and bulk-sampling cases; skipped when not given.
// --hardening: opt-in checks for #7, #11, #12 and #24. With this option,
//   --damaged also accepts one damaged .dwg file inside the plugin's import roots.
// --helpers: API 6 fixtures, only with --expect-drawing matching the scratch drawing.
// --preview: API 6 rollback and guard checks, only on a named scratch drawing.
//   Xref checks need the plugin's import roots to include its run-code folder.
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { createHash } from "node:crypto";
import net from "node:net";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { parseArgs } from "node:util";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { drainServerStderr } from "./server-stderr.mjs";
import { attachScratchUserInstance } from "./live-user-instance.mjs";

// Strict: an unknown option or a missing value is an error, never a silently disabled guard.
const { values: options } = parseArgs({
  options: {
    side: { type: "string" },
    "expect-drawing": { type: "string" },
    "allow-writes": { type: "boolean", default: false },
    approval: { type: "string", default: "token" },
    long: { type: "string" },
    damaged: { type: "string" },
    other: { type: "string" },
    "no-drawing": { type: "boolean", default: false },
    hardening: { type: "boolean", default: false },
    helpers: { type: "boolean", default: false },
    preview: { type: "boolean", default: false },
    "user-instance": { type: "boolean", default: false },
  },
  strict: true,
});
const sidePath = options.side;
const expectDrawing = options["expect-drawing"];
let selectedUserInstance;
if (!options["user-instance"]) throw new Error("Pass --user-instance to opt into the existing scratch user instance.");
if (!expectDrawing && !options["no-drawing"]) throw new Error("--user-instance requires --expect-drawing <scratch drawing name>.");
const approvalMode = options.approval;
const longSeconds = options.long === undefined ? undefined : Number(options.long);
if (longSeconds !== undefined && (!Number.isInteger(longSeconds) || longSeconds < 91 || longSeconds > 600)) {
  console.error("--long must be an integer from 91 to 600 seconds");
  process.exit(2);
}
const damagedDir = options.damaged;
if (options["no-drawing"] && (options.other || expectDrawing || options["allow-writes"])) {
  console.error("--no-drawing cannot be combined with --other, --expect-drawing, or --allow-writes");
  process.exit(2);
}
if (approvalMode !== "token" && approvalMode !== "client") {
  console.error("--approval must be token or client");
  process.exit(2);
}
if (options.helpers && (!expectDrawing || options["no-drawing"])) {
  console.error("--helpers requires --expect-drawing <scratch drawing name> and an open scratch drawing");
  process.exit(2);
}
if (options.preview && (!expectDrawing || options["no-drawing"] || options["allow-writes"])) {
  console.error("--preview requires --expect-drawing <scratch drawing name> and an open scratch drawing");
  process.exit(2);
}

const serverEnv = { ...process.env, MCP_HTTP_HOST: "127.0.0.1", MCP_HTTP_PORT: "0", CIVIL3D_RUN_CODE_APPROVAL: approvalMode };
const transport = new StdioClientTransport({
  command: process.execPath,
  args: ["./build/index.js"],
  cwd: process.cwd(),
  env: serverEnv,
  stderr: "pipe",
});
const printServerStderr = drainServerStderr(transport.stderr);
const client = new Client({ name: "civil3d-run-code-smoke", version: "1.0.0" });
const results = [];
let failures = 0;
let skips = 0;

function record(name, passed, detail) {
  results.push({ name, passed, ...(detail === undefined ? {} : { detail }) });
  if (!passed) {
    failures++;
    printServerStderr();
  }
  console.error(`${passed ? (options.hardening ? "PASS" : "ok  ") : "FAIL"} ${name}${detail === undefined ? "" : ` :: ${JSON.stringify(detail).slice(0, 400)}`}`);
}

function skip(name, detail) {
  skips++;
  results.push({ name, passed: null, skipped: true, ...(detail === undefined ? {} : { detail }) });
  console.error(`SKIP ${name}${detail === undefined ? "" : ` :: ${String(detail).replace(/[\r\n]+/g, " ")}`}`);
}

async function call(name, args, requestOptions = {}, targetClient = client) {
  return await targetClient.callTool({ name, arguments: args }, undefined,
    { timeout: 180_000, maxTotalTimeout: 180_000, ...requestOptions });
}

function text(result) {
  return (result.content ?? []).map((item) => item.text ?? "").join("\n");
}

async function approve(toolName, action, parameters, targetClient = client) {
  const approval = await call("civil3d_request_approval", { toolName, action, parameters }, {}, targetClient);
  if (approval.isError) throw new Error(`approval failed: ${text(approval)}`);
  return approval.structuredContent.approvalToken;
}

// One approved run; returns the plugin's response object (structuredContent.result).
async function run(parameters, requestOptions) {
  let response = await call("civil3d_run_code", parameters, requestOptions);
  if (response.isError && /approval required/i.test(text(response))) {
    const approvalToken = await approve("civil3d_run_code", "run", parameters);
    response = await call("civil3d_run_code", { ...parameters, approvalToken }, requestOptions);
  }
  if (response.isError) throw new Error(`civil3d_run_code failed: ${text(response)}`);
  return response.structuredContent.result;
}

async function callGatedRun(parameters, targetClient = client, requestOptions = {}) {
  let response = await call("civil3d_run_code", parameters, requestOptions, targetClient);
  if (response.isError && /approval required/i.test(text(response))) {
    const approvalToken = await approve("civil3d_run_code", "run", parameters, targetClient);
    response = await call("civil3d_run_code", { ...parameters, approvalToken }, requestOptions, targetClient);
  }
  return response;
}

function damagedPaths() {
  // Keep the original directory-only behavior unless hardening was requested.
  if (options.hardening && statSync(damagedDir).isFile()) {
    if (!/\.dwg$/i.test(damagedDir)) throw new Error("--damaged must name a .dwg file or directory");
    return [resolve(damagedDir)];
  }
  const paths = readdirSync(damagedDir, { withFileTypes: true })
    .filter((entry) => entry.isFile() && /\.dwg$/i.test(entry.name))
    .map((entry) => resolve(damagedDir, entry.name));
  if (paths.length === 0) throw new Error(`No .dwg files in ${damagedDir}`);
  return paths;
}

async function hardeningChecks(hasDrawing) {
  if (!hasDrawing) {
    skip("hardening #11: active-drawing guards (--no-drawing)");
  } else {
    await check("hardening #11: SetDimstyleData rejected; DIMSTYLE and DBMOD unchanged", async () => {
      const snapshot = 'return new { dimstyle = Convert.ToString(Application.GetSystemVariable("DIMSTYLE")), id = Db.Dimstyle.Handle.ToString(), dimscale = Db.Dimscale, dbmod = Convert.ToInt32(Application.GetSystemVariable("DBMOD")) };';
      const before = await run({ mode: "read", code: snapshot });
      if (before.status !== "ok" || typeof before.result?.dimstyle !== "string") {
        throw new Error(`Cannot read DIMSTYLE before the guard check: ${JSON.stringify(before)}`);
      }
      const refused = await run({ mode: "read", code: "using var style = new DimStyleTableRecord();\nDb.SetDimstyleData(style);\nreturn 0;" });
      const after = await run({ mode: "read", code: snapshot });
      record("hardening #11: SetDimstyleData rejected; DIMSTYLE and DBMOD unchanged",
        refused.status === "rejected" && (refused.errors ?? []).some((error) => /Database\.SetDimstyleData/.test(error.message ?? ""))
          && after.status === "ok" && JSON.stringify(before.result) === JSON.stringify(after.result),
        { status: refused.status, errors: refused.errors, before: before.result, after: after.result });
    });

    await check("hardening #11: GetObjectId(true) rejected", async () => {
      const response = await run({ mode: "read", code: "return Db.GetObjectId(true, Db.BlockTableId.Handle, 0);" });
      record("hardening #11: GetObjectId(true) rejected", response.status === "rejected"
        && (response.errors ?? []).some((error) => /Database\.GetObjectId/.test(error.message ?? "")),
        { status: response.status, errors: response.errors });
    });
    await check("hardening #11: GetObjectId(false) resolves a real handle", async () => {
      const response = await run({ mode: "read", code: "return Db.GetObjectId(false, Db.BlockTableId.Handle, 0) == Db.BlockTableId;" });
      record("hardening #11: GetObjectId(false) resolves a real handle", response.status === "ok" && response.result === true,
        { status: response.status, result: response.result, errors: response.errors, error: response.error });
    });
  }

  if (!sidePath) {
    skip("hardening #11: detached ReadDwgFile checks (no --side)");
  } else {
    const countSide = [
      'side.ReadDwgFile((string)Args["path"]!, FileOpenMode.OpenForReadAndAllShare, true, "");',
      "side.CloseInput(true);",
      "using var tx = side.TransactionManager.StartOpenCloseTransaction();",
      "var blocks = (BlockTable)tx.GetObject(side.BlockTableId, OpenMode.ForRead);",
      "long count = 0;",
      "foreach (ObjectId blockId in blocks)",
      "{",
      "  var block = (BlockTableRecord)tx.GetObject(blockId, OpenMode.ForRead);",
      "  foreach (ObjectId id in block) { Check(); count++; }",
      "}",
      "return count;",
    ];
    for (const [name, code] of [
      ["hardening #11: detached ReadDwgFile with using counts objects", ["using var side = new Database(false, true);", ...countSide].join("\n")],
      ["hardening #11: null-init detached ReadDwgFile with finally counts objects", [
        "Database side = null;", "try", "{", "side = new Database(false, true);", ...countSide,
        "}", "finally { side?.Dispose(); }",
      ].join("\n")],
    ]) {
      await check(name, async () => {
        const response = await run({ mode: "read", code, args: { path: resolve(sidePath) }, timeoutSeconds: 90 });
        record(name, response.status === "ok" && Number.isSafeInteger(response.result) && response.result >= 0,
          { status: response.status, count: response.result, errors: response.errors, error: response.error });
      });
    }
  }

  const maxInlineBytes = 4096;
  for (const [name, code, valid] of [
    ["hardening #12: 20000 anonymous objects saved as complete JSON",
      'return Enumerable.Range(0, 20000).Select(i => new { index = i, label = "item" }).ToList();',
      (value) => Array.isArray(value) && value.length === 20000 && value.every((item, i) => item.index === i && item.label === "item")],
    ["hardening #12: tuple with a large string saved as complete JSON",
      "return (7, new string('x', 200000));",
      (value) => Array.isArray(value) && value.length === 2 && value[0] === 7 && value[1] === "x".repeat(200000)],
  ]) {
    await check(name, async () => {
      const response = await run({ mode: "read", code, maxInlineBytes });
      if (response.status !== "ok" || typeof response.resultFile !== "string") {
        throw new Error(`Expected a saved result: ${JSON.stringify(response).slice(0, 400)}`);
      }
      const file = statSync(response.resultFile);
      const bytes = readFileSync(response.resultFile);
      const json = bytes.toString("utf8");
      const value = JSON.parse(json);
      const preview = response.resultPreview;
      record(name, !response.resultError && response.result == null && file.isFile()
        && response.resultBytes > maxInlineBytes && response.resultBytes === file.size && bytes.length === file.size
        && typeof preview === "string" && preview.length > 3 && preview.length <= 2003
        && preview.endsWith("...") && json.startsWith(preview.slice(0, -3)) && valid(value),
        { status: response.status, resultFile: response.resultFile, resultBytes: response.resultBytes,
          fileBytes: file.size, previewChars: preview?.length, inlineAbsent: response.result == null, resultError: response.resultError });
    });
  }
  await check("hardening #12: small result stays inline", async () => {
    const response = await run({ mode: "read", code: 'return new { value = 7, label = "small" };', maxInlineBytes });
    record("hardening #12: small result stays inline", response.status === "ok" && !response.resultError
      && response.result?.value === 7 && response.result?.label === "small" && !response.resultFile
      && response.resultBytes > 0 && response.resultBytes <= maxInlineBytes, response);
  });

  await check("hardening #7: ::1 host refused with nothing run and no new run folder", async () => {
    if (!process.env.LOCALAPPDATA) throw new Error("LOCALAPPDATA is required to inspect plugin run folders");
    const root = join(process.env.LOCALAPPDATA, "Civil3DMcpPlugin", "run-code");
    const folders = () => existsSync(root)
      ? readdirSync(root, { withFileTypes: true }).filter((entry) => entry.isDirectory()).map((entry) => entry.name)
      : [];
    const before = new Set(folders());
    const badClient = new Client({ name: "civil3d-run-code-hardening-host", version: "1.0.0" });
    const badTransport = new StdioClientTransport({
      command: process.execPath, args: ["./build/index.js"], cwd: process.cwd(),
      env: { ...serverEnv, CIVIL3D_HOST: "::1" }, stderr: "pipe",
    });
    const printBadServerStderr = drainServerStderr(badTransport.stderr, "CIVIL3D_HOST=::1 MCP server");
    const failuresBefore = failures;
    let completed = false;
    try {
      await badClient.connect(badTransport);
      const parameters = { mode: "read", code: "return 7;" };
      const requestOptions = { timeout: 30_000, maxTotalTimeout: 30_000 };
      const hostRefused = (response) => response.isError === true && response.errorCode === "CIVIL3D.INVALID_INPUT"
        && /CIVIL3D_HOST/.test(text(response)) && /nothing ran/i.test(text(response));
      if (approvalMode === "token") {
        // Do not request approval from the unreachable ::1 child.
        const direct = await call("civil3d_run_code", parameters, requestOptions, badClient);
        const directAdded = folders().filter((folder) => !before.has(folder));
        record("hardening #7: ::1 direct call without token refused with no new run folder",
          direct.isError === true && (/approval required/i.test(text(direct)) || hostRefused(direct)) && directAdded.length === 0,
          { error: text(direct), added: directAdded });
        // The normal server's token is bound to these parameters but stored only in that process.
        const approvalToken = await approve("civil3d_run_code", "run", parameters);
        const response = await call("civil3d_run_code", { ...parameters, approvalToken }, requestOptions, badClient);
        const added = folders().filter((folder) => !before.has(folder));
        record("hardening #7: foreign token cannot make the ::1 server run anything; no new run folder",
          response.isError === true && (hostRefused(response)
            || /Approval token is missing, expired, or has already been used\./i.test(text(response)))
            && added.length === 0, { error: text(response), added });
      } else {
        const response = await callGatedRun(parameters, badClient, requestOptions);
        const added = folders().filter((folder) => !before.has(folder));
        record("hardening #7: ::1 host refused with nothing run and no new run folder",
          hostRefused(response) && added.length === 0, { error: text(response), added });
      }
      completed = true;
    } finally {
      if (!completed || failures > failuresBefore) printBadServerStderr();
      // Use the same bounded close as the normal server, including failed startup.
      await Promise.race([badClient.close().catch(() => undefined), new Promise((resolve) => setTimeout(resolve, 5000))]);
    }
  });
  await check("hardening #7: normal server still runs code after host refusal", async () => {
    const response = await run({ mode: "read", code: "return 7;" });
    record("hardening #7: normal server still runs code after host refusal", response.status === "ok" && response.result === 7,
      { status: response.status, result: response.result, error: response.error });
  });

  if (damagedDir === undefined || !hasDrawing) {
    skip(`hardening #24: damaged comparison (${!hasDrawing ? "--no-drawing" : "no --damaged"})`);
  } else {
    await check("hardening #24: damaged DWG comparison errors within 60 seconds", async () => {
      for (const otherPath of damagedPaths()) {
        const started = performance.now();
        const response = await call("civil3d_compare", { action: "drawing", otherPath, includeCivilSummaries: false, maxDetails: 0 },
          { timeout: 60_000, maxTotalTimeout: 60_000 });
        const elapsedMs = Math.round(performance.now() - started);
        const message = text(response);
        if (response.isError === true && response.errorCode === "CIVIL3D.PATH_NOT_ALLOWED") {
          skip(`hardening #24: damaged DWG comparison (${otherPath})`,
            `Files must be inside the import roots listed in this message (CIVIL3D_IMPORT_ROOTS or CIVIL3D_FILE_ROOTS): ${message}`);
          continue;
        }
        record(`hardening #24: damaged DWG comparison errors within 60 seconds (${otherPath})`,
          response.isError === true && response.errorCode === "CIVIL3D.FILE_IO_ERROR" && elapsedMs < 60_000
            && /Unable to read.*as a side database/i.test(message) && message.toLowerCase().includes(otherPath.toLowerCase()),
          { elapsedMs, error: message });
      }
    });
    await check("hardening #24: plugin health responds after damaged comparison", async () => {
      const response = await call("civil3d_health", {}, { timeout: 30_000, maxTotalTimeout: 30_000 });
      record("hardening #24: plugin health responds after damaged comparison",
        response.isError !== true && response.structuredContent?.result?.connected === true, text(response).slice(0, 300));
    });
  }
}

// One JSON-RPC request straight to the plugin's port, bypassing the MCP server
// (and its approval): for the token and start-window cases. The socket is not
// half-closed before the answer, because the plugin cancels a request whose
// client closes.
function rawRpc(method, params, timeoutMs = 120_000) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: "127.0.0.1", port: selectedUserInstance?.port ?? 8757 });
    let data = "";
    let settled = false;
    const finish = (fn, value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.destroy();
      fn(value);
    };
    const timer = setTimeout(() => finish(reject, new Error(`raw ${method} timed out`)), timeoutMs);
    socket.setEncoding("utf8");
    socket.on("connect", () => socket.write(JSON.stringify({ jsonrpc: "2.0", id: `smoke-${process.pid}-${method}`, method, params })));
    socket.on("data", (chunk) => {
      data += chunk;
      try { finish(resolve, JSON.parse(data)); } catch { /* not complete yet */ }
    });
    socket.on("error", (error) => finish(reject, error));
    socket.on("close", () => finish(reject, new Error(`raw ${method}: connection closed without a complete answer (${data.length} chars)`)));
  });
}

// Static snippets also compile in the offline RunCodeHarness. No host code executes there.
const helperScripts = {
  dormantViewport: String.raw`
var path = Doc.Name;
if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !Path.IsPathFullyQualified(Db.Filename)
    || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(Db.Filename), StringComparison.OrdinalIgnoreCase))
  return new { skip = "The open drawing has no saved DWG to read with OpenSide." };
var original = LayoutManager.Current.CurrentLayout;
var layouts = Open<DBDictionary>(Db.LayoutDictionaryId);
var side = OpenSide(path);
var savedLayouts = Open<DBDictionary>(side.LayoutDictionaryId);
foreach (DBDictionaryEntry entry in layouts) {
  var layout = Open<Layout>(entry.Value);
  if (layout.ModelType || layout.LayoutName == original || !savedLayouts.Contains(layout.LayoutName)) continue;
  var openViewports = layout.GetViewports();
  if (openViewports.Count != 0) continue;
  var paper = Open<BlockTableRecord>(layout.BlockTableRecordId);
  var entities = paper.Cast<ObjectId>().Select(id => Open<DBObject>(id)).OfType<Viewport>().ToList();
  if (entities.Count == 0) continue;
  var saved = Open<Layout>(savedLayouts.GetAt(layout.LayoutName));
  var savedViewports = saved.GetViewports();
  if (savedViewports.Count == 0) continue;
  var overall = Open<Viewport>(savedViewports[0]);
  var matching = entities.FirstOrDefault(v => string.Equals(v.Handle.ToString(), overall.Handle.ToString(), StringComparison.OrdinalIgnoreCase));
  if (matching == null) continue;
  return new { layout = layout.LayoutName, handle = matching.Handle.ToString(),
    savedHandle = overall.Handle.ToString(), openCount = openViewports.Count,
    savedCount = savedViewports.Count, entityCount = entities.Count, original };
}
return new { skip = "No dormant saved paper layout has zero open GetViewports(), VIEWPORT entities, and a matching saved overall viewport." };
`,
  textSetup: String.raw`
var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), true);
var text = new DBText { TextString = "Rotated helper", Position = new Point3d(-1e6, -1e6, 0), Height = 2, Rotation = Math.PI / 6 };
text.SetDatabaseDefaults(Db);
ms.AppendEntity(text); Tr.AddNewlyCreatedDBObject(text, true); text.AdjustAlignment(Db);
var mtext = new MText { Contents = "MText helper", Location = new Point3d(-1e6, -1e6 - 20, 0), TextHeight = 2, Width = 40, Rotation = Math.PI / 6 };
mtext.SetDatabaseDefaults(Db);
ms.AppendEntity(mtext); Tr.AddNewlyCreatedDBObject(mtext, true);
return new[] { text.ObjectId, mtext.ObjectId };
`,
  textRead: String.raw`
return ((JsonArray)Args["handles"]!).Select(node => {
  var entity = Open<Entity>(ById((string)node!));
  var extents = entity.GeometricExtents;
  var corners = TextBox(entity);
  var translated = TextBox(entity, Matrix3d.Displacement(new Vector3d(10, 20, 0)));
  return new { corners, translated, min = extents.MinPoint, max = extents.MaxPoint };
}).ToArray();
`,
  eraseEntities: String.raw`
foreach (var node in (JsonArray)Args["handles"]!) {
  var handle = new Handle(Convert.ToInt64((string)node!, 16));
  if (Db.TryGetObjectId(handle, out var id) && !id.IsNull && !id.IsErased) Open<Entity>(id, true).Erase();
}
return 1;
`,
  clipSetup: String.raw`
var blocks = Open<BlockTable>(Db.BlockTableId, true);
var definition = new BlockTableRecord { Name = (string)Args["name"]! };
var definitionId = blocks.Add(definition); Tr.AddNewlyCreatedDBObject(definition, true);
foreach (var x in new[] { 2.0, 20.0 }) {
  var line = new Line(new Point3d(x, 2, 0), new Point3d(x + 1, 3, 0));
  definition.AppendEntity(line); Tr.AddNewlyCreatedDBObject(line, true);
}
var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), true);
var reference = new BlockReference(new Point3d(-1e6, -1e6, 0), definitionId) { Rotation = Math.PI / 6, ScaleFactors = new Scale3d(2.5) };
ms.AppendEntity(reference); Tr.AddNewlyCreatedDBObject(reference, true);
return reference.ObjectId;
`,
  clipWrite: String.raw`
var reference = Open<BlockReference>(ById((string)Args["handle"]!));
var local = new[] { new Point3d(0,0,0), new Point3d(10,0,0), new Point3d(10,10,0), new Point3d(0,10,0) };
return XClip(reference, local.Select(p => p.TransformBy(reference.BlockTransform)).ToArray());
`,
  clipRead: String.raw`
using Autodesk.AutoCAD.DatabaseServices.Filters;
var reference = Open<BlockReference>(ById((string)Args["handle"]!));
var extension = Open<DBDictionary>(reference.ExtensionDictionary);
var filters = Open<DBDictionary>(extension.GetAt("ACAD_FILTER"));
var filter = Open<SpatialFilter>(filters.GetAt("SPATIAL"));
var definition = filter.Definition;
var points = definition.GetPoints().Cast<Point2d>().ToArray();
bool Inside(Point3d p) {
  var inside = false;
  for (int i = 0, j = points.Length - 1; i < points.Length; j = i++) {
    var a = points[i]; var b = points[j];
    if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
  }
  return inside;
}
var block = Open<BlockTableRecord>(reference.BlockTableRecord);
var lines = block.Cast<ObjectId>().Select(id => Open<Line>(id)).ToArray();
return new { id = filter.ObjectId, points, definition.Enabled, definition.Elevation, definition.FrontClip, definition.BackClip,
  total = lines.Length, visible = lines.Count(line => Inside(line.StartPoint) && Inside(line.EndPoint)) };
`,
  clipCleanup: String.raw`
var blocks = Open<BlockTable>(Db.BlockTableId);
if (blocks.Has((string)Args["name"]!)) {
  var definition = Open<BlockTableRecord>(blocks[(string)Args["name"]!], true);
  foreach (ObjectId id in definition.GetBlockReferenceIds(true, false)) Open<BlockReference>(id, true).Erase();
  definition.Erase();
}
return 1;
`,
  xrefSetup: String.raw`
var folder = (string)Args["folder"]!;
var originalUnits = Db.Insunits.ToString();
foreach (var units in new[] { UnitsValue.Inches, UnitsValue.Undefined }) {
  // A new fixture needs default symbol tables; false is for reading an existing DWG.
  using var source = new Database(true, true);
  source.Insunits = units;
  using (var t = source.TransactionManager.StartTransaction()) {
    var ms = (BlockTableRecord)t.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(source), OpenMode.ForWrite);
    var line = new Line(Point3d.Origin, new Point3d(12,0,0));
    ms.AppendEntity(line); t.AddNewlyCreatedDBObject(line, true); t.Commit();
  }
  source.SaveAs(Path.Combine(folder, units == UnitsValue.Inches ? "helpers-inches.dwg" : "helpers-unitless.dwg"), DwgVersion.Current);
}
Db.Insunits = UnitsValue.Feet;
return originalUnits;
`,
  xrefAuto: String.raw`
var result = AttachXref((string)Args["path"]!, new Point3d(-1e6, -1e6, 0), layer: (string)Args["layer"]!, blockName: (string)Args["name"]!);
var reference = Open<BlockReference>(result.ReferenceId);
return new { value = result, position = reference.Position, scale = reference.ScaleFactors.X, layer = reference.Layer,
  length = reference.GeometricExtents.MaxPoint.X - reference.GeometricExtents.MinPoint.X };
`,
  xrefCount: String.raw`
var blocks = Open<BlockTable>(Db.BlockTableId);
var layers = Open<LayerTable>(Db.LayerTableId);
return new { definition = blocks.Has((string)Args["name"]!), layer = layers.Has((string)Args["layer"]!),
  inserts = ModelSpace<BlockReference>().Count(r => r.Name == (string)Args["name"]!) };
`,
  xrefUnitless: String.raw`
var name = (string)Args["name"]!;
var layer = (string)Args["layer"]!;
var recovered = Try("unitless", () => AttachXref((string)Args["path"]!, Point3d.Origin, layer: layer, blockName: name));
var blocks = Open<BlockTable>(Db.BlockTableId);
var layers = Open<LayerTable>(Db.LayerTableId);
return new { failed = recovered == null, errors = TryErrors, partialDefinition = blocks.Has(name), partialLayer = layers.Has(layer) };
`,
  xrefExplicit: String.raw`
return AttachXref((string)Args["path"]!, new Point3d(-1e6, -1e6, 0), scale: 1.0 / 12, layer: (string)Args["layer"]!, blockName: (string)Args["name"]!);
`,
  xrefCleanup: String.raw`
foreach (var node in (JsonArray)Args["names"]!) {
  var name = (string)node!;
  var blocks = Open<BlockTable>(Db.BlockTableId);
  if (blocks.Has(name)) {
    var definition = Open<BlockTableRecord>(blocks[name]);
    foreach (ObjectId id in definition.GetBlockReferenceIds(true, false)) Open<BlockReference>(id, true).Erase();
    Db.DetachXref(definition.ObjectId);
  }
}
foreach (var node in (JsonArray)Args["layers"]!) {
  var layers = Open<LayerTable>(Db.LayerTableId);
  if (layers.Has((string)node!)) Open<LayerTableRecord>(layers[(string)node!], true).Erase();
}
Db.Insunits = Enum.Parse<UnitsValue>((string)Args["units"]!);
return 1;
`,
  // Issue #43: creating a layout, switching to it and creating viewports in one write run crashed AutoCAD, so the
  // compile guard refuses it and the fixture is built in three runs: viewportCreate, viewportActivate, viewportSetup.
  viewportCreate: String.raw`
LayoutManager.Current.CreateLayout((string)Args["layout"]!);
return LayoutManager.Current.CurrentLayout;
`,
  viewportSetup: String.raw`
var layouts = Open<DBDictionary>(Db.LayoutDictionaryId);
var layout = Open<Layout>(layouts.GetAt((string)Args["layout"]!));
var overall = layout.GetViewports()[0];
var ps = Open<BlockTableRecord>(layout.BlockTableRecordId, true);
var viewport = new Viewport { CenterPoint = new Point3d(10,10,0), Width = 8, Height = 5, ViewDirection = Vector3d.ZAxis };
ps.AppendEntity(viewport); Tr.AddNewlyCreatedDBObject(viewport, true); viewport.On = true; viewport.Locked = true;
return new { handle = viewport.ObjectId, overall };
`,
  viewportWrite: String.raw`
return SetViewport((string)Args["layout"]!, ById((string)Args["handle"]!), paperCenter: new Point3d(18,12,0),
  paperWidth: 26, paperHeight: 10, modelCenter: new Point3d(1000,2000,0), customScale: 0.025, twistAngle: 0.15, locked: true);
`,
  viewportRead: String.raw`
var viewport = Open<Viewport>(ById((string)Args["handle"]!));
return new { id = viewport.ObjectId, center = viewport.CenterPoint, width = viewport.Width, height = viewport.Height,
  target = viewport.ViewTarget, viewCenter = viewport.ViewCenter, viewHeight = viewport.ViewHeight,
  scale = viewport.CustomScale, twist = viewport.TwistAngle, locked = viewport.Locked, current = LayoutManager.Current.CurrentLayout };
`,
  viewportActivate: String.raw`
LayoutManager.Current.CurrentLayout = (string)Args["layout"]!;
return LayoutManager.Current.CurrentLayout;
`,
  viewportOverall: String.raw`
return SetViewport((string)Args["layout"]!, ById((string)Args["handle"]!), paperWidth: 7);
`,
  viewportCleanup: String.raw`
var manager = LayoutManager.Current;
manager.CurrentLayout = (string)Args["original"]!;
var layouts = Open<DBDictionary>(Db.LayoutDictionaryId);
if (layouts.Contains((string)Args["layout"]!)) manager.DeleteLayout((string)Args["layout"]!);
return 1;
`,
};

async function helperChecks(runCodeApi) {
  if (runCodeApi < 6) {
    skip("helpers #22 (plugin API below 6)", `runCodeApi=${runCodeApi}`);
    return;
  }
  const drawing = await run({ code: "return Doc.Name;" });
  if (drawing.status !== "ok" || typeof drawing.result !== "string"
      || !drawing.result.toLowerCase().includes(expectDrawing.toLowerCase())) {
    throw new Error("Active drawing changed before helper checks; stopping before any helper write.");
  }
  const unique = `RUNCODE-HELPERS-${process.pid}-${Date.now()}`;
  const execute = async (name, args = {}, mode = "write") => await run({ code: helperScripts[name], args, mode });
  const requireOk = (response) => {
    if (response.status !== "ok") throw new Error(JSON.stringify({ status: response.status, errors: response.errors, error: response.error }));
    return response.result;
  };
  const close = (a, b) => typeof a === "number" && Math.abs(a - b) < 1e-6;
  const vector = (a, b) => Array.isArray(a) && a.length === b.length && a.every((v, i) => close(v, b[i]));

  await check("helpers: dormant layout overall viewport refuses without mutation", async () => {
    const probe = await execute("dormantViewport", {}, "read");
    if (probe.status === "runtime_error" && /CIVIL3D_(?:IMPORT|FILE)_ROOTS|outside.*roots|PATH_NOT_ALLOWED/i.test(probe.error?.message ?? "")) {
      skip("helpers: dormant layout overall viewport (saved DWG outside import roots)", probe.error.message);
      return;
    }
    const fixture = requireOk(probe);
    if (fixture.skip) { skip("helpers: dormant layout overall viewport", fixture.skip); return; }
    const args = { layout: fixture.layout, handle: fixture.handle };
    const before = requireOk(await execute("viewportRead", args, "read"));
    const refused = await execute("viewportOverall", args);
    const after = requireOk(await execute("viewportRead", args, "read"));
    record("helpers: dormant layout overall viewport refuses without mutation",
      fixture.openCount === 0 && fixture.savedCount > 0 && fixture.entityCount > 0
        && fixture.handle === fixture.savedHandle && before.id === fixture.handle
        && refused.status === "runtime_error" && refused.committed === false && refused.activatedLayout === true
        && /overall.*paper space.*revert/i.test(refused.error?.message ?? "")
        && before.current === fixture.original && after.current === fixture.original
        && JSON.stringify(before) === JSON.stringify(after),
      { fixture, status: refused.status, committed: refused.committed, activatedLayout: refused.activatedLayout,
        error: refused.error?.message, before, after });
  });

  let textHandles = [];
  try {
    await check("helpers: TextBox rotated DBText and MText match extents", async () => {
      textHandles = requireOk(await execute("textSetup"));
      const response = await execute("textRead", { handles: textHandles }, "read");
      const values = requireOk(response);
      const inReadingFrame = corners => {
        const right = [corners[1][0] - corners[0][0], corners[1][1] - corners[0][1]];
        const up = [corners[3][0] - corners[0][0], corners[3][1] - corners[0][1]];
        const normalize = v => v.map(x => x / Math.hypot(...v));
        return vector(normalize(right), [Math.cos(Math.PI / 6), Math.sin(Math.PI / 6)])
          && vector(normalize(up), [-Math.sin(Math.PI / 6), Math.cos(Math.PI / 6)]);
      };
      const valid = values.length === 2 && values.every(({ corners, translated, min, max }) =>
        corners.length === 4 && inReadingFrame(corners) && [0, 1, 2].every(axis => close(Math.min(...corners.map(p => p[axis])), min[axis])
          && close(Math.max(...corners.map(p => p[axis])), max[axis]))
        && corners.every((p, i) => vector(translated[i], [p[0] + 10, p[1] + 20, p[2]])));
      record("helpers: TextBox rotated DBText and MText match extents", valid, values);
    });
  } finally {
    if (textHandles.length) await check("helpers: text cleanup", async () => {
      requireOk(await execute("eraseEntities", { handles: textHandles }));
      record("helpers: text cleanup", true);
    });
  }

  const clipName = `${unique}-CLIP`;
  try {
    await check("helpers: XClip transformed insert keeps only the inside line", async () => {
      const handle = requireOk(await execute("clipSetup", { name: clipName }));
      const first = requireOk(await execute("clipWrite", { handle }));
      const second = requireOk(await execute("clipWrite", { handle }));
      const value = requireOk(await execute("clipRead", { handle }, "read"));
      record("helpers: XClip transformed insert keeps only the inside line", first !== second && value.id === second
        && value.Enabled && value.Elevation === 0 && value.FrontClip === 0
        && (value.BackClip === 0 || Math.abs(value.BackClip) >= 1e299)
        && value.total === 2 && value.visible === 1 && value.points.length === 4
        && [[0,0], [10,0], [10,10], [0,10]].every(p => value.points.some(q => vector(q, p))), value);
    });
  } finally {
    await check("helpers: clip cleanup", async () => {
      requireOk(await execute("clipCleanup", { name: clipName }));
      record("helpers: clip cleanup", true);
    });
  }

  const names = [`${unique}-INCHES`, `${unique}-UNITLESS`];
  const layers = [`${unique}-INCHES-LAYER`, `${unique}-UNITLESS-LAYER`];
  const initial = requireOk(await run({ code: "return new { folder = RunFolder, units = Db.Insunits.ToString() };" }));
  const paths = [join(initial.folder, "helpers-inches.dwg"), join(initial.folder, "helpers-unitless.dwg")];
  try {
    await check("helpers: AttachXref scale 1/12 and one UNDO", async () => {
      requireOk(await execute("xrefSetup", { folder: initial.folder }));
      const attached = await execute("xrefAuto", { path: paths[0], name: names[0], layer: layers[0] });
      if (attached.status === "runtime_error" && /CIVIL3D_(?:IMPORT|FILE)_ROOTS|outside.*roots|PATH_NOT_ALLOWED/i.test(attached.error?.message ?? "")) {
        skip("helpers: AttachXref fixtures refused by import-root policy", attached.error.message);
        return;
      }
      const value = requireOk(attached);
      record("helpers: AttachXref inches into feet uses 1/12", attached.committed === true && close(value.value.Scale, 1 / 12)
        && close(value.scale, 1 / 12) && close(value.length, 1) && vector(value.position, [-1e6,-1e6,0]) && value.layer === layers[0], value);
      // No intervening writes: undo exactly the committed attach, not fixture setup.
      const before = requireOk(await execute("xrefCount", { name: names[0], layer: layers[0] }, "read"));
      if (!attached.committed || before.inserts !== 1) throw new Error("Attach did not commit exactly one reference; UNDO was not sent.");
      const approvalToken = await approve("civil3d_drawing", "undo", { action: "undo", steps: 1 });
      const undo = await call("civil3d_drawing", { action: "undo", steps: 1, approvalToken });
      if (undo.isError) throw new Error(`UNDO failed: ${text(undo)}`);
      await new Promise(resolve => setTimeout(resolve, 1500));
      const after = requireOk(await execute("xrefCount", { name: names[0], layer: layers[0] }, "read"));
      record("helpers: one UNDO removes attach and its layer", !after.definition && !after.layer && after.inserts === 0, after);
      const failed = requireOk(await execute("xrefUnitless", { path: paths[1], name: names[1], layer: layers[1] }));
      record("helpers: unitless xref fails without partial changes", failed.failed && !failed.partialDefinition && !failed.partialLayer
        && failed.errors.length === 1 && /explicit scale/i.test(failed.errors[0].Message), failed);
      const explicit = requireOk(await execute("xrefExplicit", { path: paths[1], name: names[1], layer: layers[1] }));
      const explicitCount = requireOk(await execute("xrefCount", { name: names[1], layer: layers[1] }, "read"));
      record("helpers: unitless xref works with explicit scale", close(explicit.Scale, 1 / 12) && explicitCount.inserts === 1, explicit);
    });
  } finally {
    await check("helpers: xref cleanup and unit restoration", async () => {
      requireOk(await execute("xrefCleanup", { names, layers, units: initial.units }));
      for (const path of paths) rmSync(path, { force: true });
      record("helpers: xref cleanup and unit restoration", true);
    });
  }

  const layoutName = `${unique}-LAYOUT`;
  let original = requireOk(await run({ code: "return LayoutManager.Current.CurrentLayout;" }));
  try {
    await check("helpers: SetViewport persists after non-current layout activation", async () => {
      requireOk(await execute("viewportCreate", { layout: layoutName }));
      requireOk(await execute("viewportActivate", { layout: layoutName }));
      const fixture = requireOk(await execute("viewportSetup", { layout: layoutName }));
      requireOk(await execute("viewportActivate", { layout: original }));
      const args = { layout: layoutName, handle: fixture.handle };
      const changedResponse = await execute("viewportWrite", args);
      const changed = requireOk(changedResponse);
      const before = requireOk(await execute("viewportRead", args, "read"));
      requireOk(await execute("viewportActivate", { layout: layoutName }));
      const after = requireOk(await execute("viewportRead", args, "read"));
      const valid = value => value.id === fixture.handle && vector(value.center, [18,12,0]) && close(value.width, 26)
        && close(value.height, 10) && vector(value.target, [1000,2000,0]) && vector(value.viewCenter, [0,0])
        && close(value.viewHeight, 400) && close(value.scale, 0.025) && close(value.twist, 0.15) && value.locked;
      record("helpers: SetViewport persists after non-current layout activation", changed === fixture.handle
        && changedResponse.activatedLayout === true && valid(before) && valid(after)
        && before.current === original && after.current === layoutName, { before, after });
      const overallArgs = { layout: layoutName, handle: fixture.overall };
      const overallBefore = requireOk(await execute("viewportRead", overallArgs, "read"));
      const refused = await execute("viewportOverall", overallArgs);
      const overallAfter = requireOk(await execute("viewportRead", overallArgs, "read"));
      record("helpers: SetViewport refuses overall viewport without changing it", refused.status === "runtime_error" && refused.committed === false
        && /overall.*paper space.*revert/i.test(refused.error?.message ?? "")
        && JSON.stringify(overallBefore) === JSON.stringify(overallAfter), { status: refused.status, error: refused.error });
    });
  } finally {
    await check("helpers: viewport cleanup and layout restoration", async () => {
      requireOk(await execute("viewportCleanup", { layout: layoutName, original }));
      record("helpers: viewport cleanup and layout restoration", true);
    });
  }
}

async function previewChecks(runCodeApi, activeDrawing) {
  if (runCodeApi < 6) { skip("preview (plugin API below 6)", `runCodeApi=${runCodeApi}`); return; }
  const tag = `RUNCODE-PREVIEW-${process.pid}-${Date.now()}`;
  const setupCode = [
    "var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), true);",
    "var handles = new List<string>();",
    "foreach (var suffix in new[] { \"-ERASE\", \"-MODIFY\" }) {",
    "  var text = new DBText { TextString = (string)Args[\"tag\"] + suffix, Layer = \"0\", Position = new Point3d(-1e6, -1e6, 0), Height = 1 };",
    "  ms.AppendEntity(text); Tr.AddNewlyCreatedDBObject(text, true); handles.Add(text.Handle.ToString());",
    "}",
    "return handles;",
  ].join("\n");
  const snapshotCode = [
    "var tag = (string)Args[\"tag\"];",
    "return new { name = Doc.Name, objectCount = ModelSpace().Count(), count = ModelSpace<DBText>().Count(t => t.TextString.StartsWith(tag)),",
    "  erase = Open<DBText>(ById((string)Args[\"erase\"])).TextString,",
    "  modify = Open<DBText>(ById((string)Args[\"modify\"])).TextString,",
    // GetVariable belongs to this document even when a read targets B while A is active.
    "  dbmod = Convert.ToInt32(((dynamic)Doc.GetAcadDocument()).GetVariable(\"DBMOD\")) };",
  ].join("\n");
  const previewCode = [
    "Open<DBText>(ById((string)Args[\"erase\"]), true).Erase();",
    "Open<DBText>(ById((string)Args[\"modify\"]), true).TextString = (string)Args[\"tag\"] + \"-CHANGED\";",
    "var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), true);",
    "for (int i = 0; i < 3; i++) {",
    "  var text = new DBText { TextString = (string)Args[\"tag\"] + \"-ADDED-\" + i, Layer = \"0\", Position = new Point3d(-1e6 + i, -1e6, 0), Height = 1 };",
    "  ms.AppendEntity(text); Tr.AddNewlyCreatedDBObject(text, true);",
    "}",
    "return ModelSpace<DBText>().Count(t => t.TextString.StartsWith((string)Args[\"tag\"]));",
  ].join("\n");
  const targets = [undefined, ...(options.other ? [resolve(options.other)] : [])];
  for (const document of targets) {
    const label = document ? "target B" : "active A";
    const runIn = async (parameters) => await run({ ...(document ? { document } : {}), ...parameters });
    const initialActive = (await run({ code: "return Doc.Name;" })).result;
    if (initialActive?.toLowerCase() !== activeDrawing.toLowerCase()) throw new Error("Active scratch drawing changed before preview checks");
    let handles;
    try {
      const setup = await runIn({ mode: "write", code: setupCode, args: { tag } });
      if (setup.status !== "ok" || setup.committed !== true || setup.result?.length !== 2) throw new Error(`preview fixture setup failed: ${JSON.stringify(setup)}`);
      handles = setup.result;
      const args = { tag, erase: handles[0], modify: handles[1] };
      const before = await runIn({ code: snapshotCode, args });
      const preview = await runIn({ mode: "preview", code: previewCode, args });
      const after = await runIn({ code: snapshotCode, args });
      const activeAfter = (await run({ code: "return Doc.Name;" })).result;
      const textGroup = preview.changes?.byType?.find(row => row.type.toUpperCase() === "TEXT" && row.layer === "0");
      record(`preview ${label}: counts and rollback`, preview.status === "ok" && preview.committed === false && preview.rolledBack === true
        && textGroup?.added === 3 && textGroup?.erased === 1 && textGroup?.modified === 1
        && JSON.stringify(preview.changes?.erasedHandles) === JSON.stringify([handles[0]])
        && preview.changes?.modifiedHandles?.filter(handle => handle === handles[1]).length === 1
        && preview.changes?.truncated === false && before.result?.count === 2
        && JSON.stringify(after.result) === JSON.stringify(before.result)
        && activeAfter?.toLowerCase() === activeDrawing.toLowerCase()
        && (document ? preview.activated === true && preview.switchedBack === true : true),
      { textGroup, changes: preview.changes, before: before.result, after: after.result, activeAfter, activated: preview.activated, switchedBack: preview.switchedBack });
      for (const code of ["Application.SetSystemVariable(\"CMDECHO\", 0); return 1;", "WriteFile(\"preview.txt\", \"unsafe\"); return 1;"]) {
        const cmdechoBefore = (await runIn({ code: "return Convert.ToInt32(Application.GetSystemVariable(\"CMDECHO\"));" })).result;
        const refused = await runIn({ mode: "preview", code });
        const cmdechoAfter = (await runIn({ code: "return Convert.ToInt32(Application.GetSystemVariable(\"CMDECHO\"));" })).result;
        record(`preview ${label}: refuses ${code.startsWith("WriteFile") ? "WriteFile" : "SetSystemVariable"}`,
          refused.status === "rejected" && refused.committed === false && cmdechoBefore === cmdechoAfter
          && (!refused.logFolder || !existsSync(join(refused.logFolder, "preview.txt"))),
          { status: refused.status, errors: refused.errors, cmdechoBefore, cmdechoAfter });
      }
    } finally {
      if (handles) {
        const cleanup = await runIn({ mode: "write", code: [
          "foreach (var id in ((JsonArray)Args[\"handles\"]!).Select(h => ById((string)h!))) Open<DBText>(id, true).Erase();",
          "return 1;",
        ].join("\n"), args: { handles } });
        record(`preview ${label}: fixture cleanup`, cleanup.status === "ok" && cleanup.committed === true, { status: cleanup.status, committed: cleanup.committed });
      }
    }
  }
}

async function check(name, fn) {
  try {
    await fn();
  } catch (error) {
    record(name, false, error instanceof Error ? error.message : String(error));
  }
}

try {
  await client.connect(transport);
  selectedUserInstance = await attachScratchUserInstance(call, expectDrawing, { noDrawing: options["no-drawing"] });
  const health = await call("civil3d_health", {});
  if (health.isError) throw new Error(`civil3d_health failed: ${text(health)}`);
  const runCodeApi = health.structuredContent?.result?.runCodeApi ?? 1;
  const tools = await client.listTools();
  const tool = tools.tools.find((item) => item.name === "civil3d_run_code");
  record("the server lists civil3d_run_code as destructive", Boolean(tool && tool.annotations?.destructiveHint === true));

  let drawingName = "";
  if (options["no-drawing"]) {
    if (health.structuredContent?.result?.drawingLoaded !== false) {
      throw new Error("--no-drawing requires every drawing to be closed before testing");
    }
    if (runCodeApi < 4) skip("PR 3 no-drawing checks (plugin runCodeApi below 4)", `runCodeApi=${runCodeApi}`);
    else {
    await check("PR 3: OpenSide read with no drawing", async () => {
      if (!sidePath) throw new Error("--no-drawing requires --side <scratch drawing path>");
      const response = await run({ code: "return new { hasDrawing = HasDrawing, side = OpenSide((string)Args[\"path\"]!).Filename };", args: { path: sidePath } });
      record("PR 3: OpenSide read with no drawing", response.status === "ok" && response.result?.hasDrawing === false
        && typeof response.result?.side === "string" && !Object.hasOwn(response, "drawing"), response);
    });
    await check("PR 3: Db reports a clear runtime error with no drawing", async () => {
      const response = await run({ code: "return Db.Filename;" });
      record("PR 3: Db reports a clear runtime error with no drawing", response.status === "runtime_error"
        && response.error?.message === "No drawing is open in Civil 3D, so Db is not available: read files with OpenSide(path) and TransactionFor(side).", response);
    });
    await check("PR 3: write with no drawing is refused", async () => {
      const response = await callGatedRun({ code: "return 1;", mode: "write" });
      record("PR 3: write with no drawing is refused", response.isError === true && response.errorCode === "CIVIL3D.NO_DRAWING", response);
    });
    }
  } else {
  const info = await run({ code: "return new { name = Doc.Name, dbmod = Convert.ToInt32(Application.GetSystemVariable(\"DBMOD\")) };" });
  drawingName = info.result?.name ?? "";
  record("read: basic run returns the active drawing", info.status === "ok" && drawingName.length > 0, { drawing: drawingName, timing: info.timing });
  if (expectDrawing && !drawingName.toLowerCase().includes(expectDrawing.toLowerCase())) {
    throw new Error(`Active drawing '${drawingName}' does not contain '${expectDrawing}'; stopping before any write.`);
  }
  if (options.helpers) await helperChecks(runCodeApi);

  if (options.other) {
    if (runCodeApi < 4) {
      skip("PR 3 other-drawing checks (plugin runCodeApi below 4)", `runCodeApi=${runCodeApi}`);
    }
    else {
    const otherPath = resolve(options.other);
    const otherName = otherPath.split(/[\\/]/).at(-1);
    if (drawingName.toLowerCase() === otherPath.toLowerCase()) throw new Error("--other must name a different, already open drawing");
    const probe = "return new { name = Doc.Name, hasDrawing = HasDrawing };";
    const before = await run({ code: "return new { name = Doc.Name, dbmod = Convert.ToInt32(Application.GetSystemVariable(\"DBMOD\")) };" });
    for (const key of [otherPath, otherName]) {
      await check(`PR 3: read other drawing by ${key === otherPath ? "path" : "name"}`, async () => {
        const selected = await run({ code: probe, document: key });
        const after = await run({ code: "return new { name = Doc.Name, dbmod = Convert.ToInt32(Application.GetSystemVariable(\"DBMOD\")) };" });
        record(`PR 3: read other drawing by ${key === otherPath ? "path" : "name"}`,
          selected.status === "ok" && selected.result?.name?.toLowerCase() === otherPath.toLowerCase()
          && selected.result?.hasDrawing === true && selected.drawing?.toLowerCase() === otherPath.toLowerCase()
          && JSON.stringify(after.result) === JSON.stringify(before.result),
          { selected: selected.result, responseDrawing: selected.drawing, activeBefore: before.result, activeAfter: after.result });
      });
    }
    if (runCodeApi < 5) {
      await check("PR 3: write to other open drawing is refused", async () => {
        const response = await callGatedRun({ code: "return 1;", mode: "write", document: otherPath });
        record("PR 3: write to other open drawing is refused", response.isError === true && response.errorCode === "CIVIL3D.INVALID_INPUT"
          && /active drawing|active document/i.test(text(response)), text(response));
      });
    }
    await check("PR 3: missing drawing lists open drawings", async () => {
      const response = await callGatedRun({ code: "return 1;", document: join(resolve(options.other, ".."), `missing-pr3-${process.pid}.dwg`) });
      record("PR 3: missing drawing lists open drawings", response.isError === true
        && /not open/i.test(text(response)) && /Open drawings:/i.test(text(response))
        && text(response).toLowerCase().includes(otherName.toLowerCase())
        && text(response).toLowerCase().includes(drawingName.toLowerCase()), text(response));
    });
    }
  }

  if (runCodeApi < 3) {
    await check("skew guard: copyOutputsTo refused by an older plugin", async () => {
      const parameters = { code: "return 1;", copyOutputsTo: join(tmpdir(), `run-code-skew-${process.pid}`) };
      const approvalToken = approvalMode === "token" ? await approve("civil3d_run_code", "run", parameters) : undefined;
      const response = await call("civil3d_run_code", { ...parameters, ...(approvalToken ? { approvalToken } : {}) });
      record("skew guard: copyOutputsTo refused by an older plugin",
        response.isError === true && /older than this server/i.test(text(response)) && /nothing ran/i.test(text(response)),
        { runCodeApi, response: text(response).slice(0, 240) });
    });
    skip("PR 2 checks (plugin runCodeApi below 3)", `runCodeApi=${runCodeApi}`);
  } else {
    await check("PR 2: codePath/argsPath strips BOM and hashes script", async () => {
      const folder = mkdtempSync(join(tmpdir(), "run-code-files-"));
      try {
        const code = "return (string)Args[\"echo\"]!;";
        const codePath = join(folder, "script.cs");
        const argsPath = join(folder, "args.json");
        writeFileSync(codePath, `\uFEFF${code}`, "utf8");
        writeFileSync(argsPath, JSON.stringify({ echo: "pr2-file-arg" }), "utf8");
        const response = await run({ codePath, argsPath });
        const hash = createHash("sha256").update(code, "utf8").digest("hex");
        record("PR 2: codePath/argsPath strips BOM and hashes script",
          response.status === "ok" && response.result === "pr2-file-arg" && response.scriptSha256 === hash,
          { status: response.status, result: response.result, scriptSha256: response.scriptSha256, expected: hash });
      } finally {
        rmSync(folder, { recursive: true, force: true });
      }
    });

    await check("PR 2: copyOutputsTo copies WriteFile output", async () => {
      const folder = mkdtempSync(join(tmpdir(), "run-code-copy-"));
      try {
        const target = join(folder, "outputs");
        const output = join(target, "pr2-out.txt");
        const response = await run({ code: "return WriteFile(\"pr2-out.txt\", \"x\");", copyOutputsTo: target });
        record("PR 2: copyOutputsTo copies WriteFile output",
          response.status === "ok" && existsSync(output) && readFileSync(output, "utf8") === "x"
            && (response.copiedFiles ?? []).some((file) => resolve(file) === resolve(output)),
          { status: response.status, copiedFiles: response.copiedFiles, logFolder: response.logFolder });
      } finally {
        rmSync(folder, { recursive: true, force: true });
      }
    });

    await check("PR 2: keepOutputs:false leaves only script.cs and run.json", async () => {
      const folder = mkdtempSync(join(tmpdir(), "run-code-move-"));
      try {
        const target = join(folder, "outputs");
        const output = join(target, "pr2-out.txt");
        const response = await run({ code: "return WriteFile(\"pr2-out.txt\", \"x\");", copyOutputsTo: target, keepOutputs: false });
        const remaining = response.logFolder && existsSync(response.logFolder) ? readdirSync(response.logFolder).sort() : [];
        record("PR 2: keepOutputs:false leaves only script.cs and run.json",
          response.status === "ok" && existsSync(output) && readFileSync(output, "utf8") === "x"
            && (response.copiedFiles ?? []).some((file) => resolve(file) === resolve(output))
            && JSON.stringify(remaining) === JSON.stringify(["run.json", "script.cs"]),
          { status: response.status, copiedFiles: response.copiedFiles, remaining });
      } finally {
        rmSync(folder, { recursive: true, force: true });
      }
    });

    await check("PR 2: write timeout of 91 seconds is refused before running", async () => {
      const response = await call("civil3d_run_code", { code: "return 1;", mode: "write", timeoutSeconds: 91 });
      record("PR 2: write timeout of 91 seconds is refused before running",
        response.isError === true && /Write runs are limited to 90 seconds/i.test(text(response)), text(response).slice(0, 240));
    });

    if (longSeconds === undefined) {
      skip("PR 2: long read (no --long)");
    } else {
      await check("PR 2: long read and MCP progress", async () => {
        const progress = [];
        const response = await run({
          mode: "read", timeoutSeconds: longSeconds,
          code: `while (Elapsed.TotalSeconds < ${longSeconds} - 5) { Check(); System.Threading.Thread.Sleep(250); } return Elapsed.TotalSeconds;`,
        }, {
          onprogress: (notification) => progress.push(notification),
          resetTimeoutOnProgress: true,
          timeout: 60_000,
          maxTotalTimeout: (longSeconds + 120) * 1000,
        });
        record("PR 2: long read and MCP progress",
          response.status === "ok" && typeof response.result === "number"
            && response.result >= longSeconds - 6 && progress.length >= Math.floor(longSeconds / 15) - 2,
          { status: response.status, elapsed: response.result, notifications: progress.length, timeoutSeconds: longSeconds });
      });
    }

    if (damagedDir === undefined) {
      skip("PR 2: damaged drawings (no --damaged)");
    } else {
      await check("PR 2: OpenSideOrNull reports every failed drawing", async () => {
        const paths = damagedPaths();
        const parent = options.hardening && statSync(damagedDir).isFile() ? resolve(damagedDir, "..") : resolve(damagedDir);
        paths.push(join(parent, `missing-pr2-${process.pid}.dwg`), drawingName);
        const response = await run({
          args: { paths },
          code: [
            "var opened = new List<object>();",
            "foreach (var node in Args[\"paths\"]!.AsArray())",
            "{",
            "  var path = (string)node!;",
            "  var side = OpenSideOrNull(path);",
            "  opened.Add(new { path, opened = side != null });",
            "}",
            "return opened;",
          ].join("\n"),
        });
        const outcomes = response.result ?? [];
        const failed = Array.isArray(outcomes) ? outcomes.filter((item) => !item.opened) : [];
        record("PR 2: OpenSideOrNull reports every failed drawing",
          response.status === "ok" && Array.isArray(outcomes) && outcomes.length === paths.length
            && outcomes.some((item) => item.opened) && failed.length > 0
            && failed.every((item) => (response.sideErrors ?? []).some((error) => error.path === item.path && "errorStatus" in error)),
          { status: response.status, outcomes, sideErrors: response.sideErrors });
      });
    }

    await check("PR 2: Try records one error and continues", async () => {
      const response = await run({ code: [
        "var done = new List<int>();",
        "for (var i = 0; i < 3; i++)",
        "  Try($\"item-{i}\", () => { if (i == 1) throw new InvalidOperationException(\"middle\"); done.Add(i); });",
        "return done;",
      ].join("\n") });
      record("PR 2: Try records one error and continues",
        response.status === "ok" && JSON.stringify(response.result) === "[0,2]"
          && response.tryErrors?.length === 1 && response.tryErrors[0].label === "item-1",
        { status: response.status, result: response.result, tryErrors: response.tryErrors });
    });

    await check("PR 2: ById resolves a handle and rejects an unknown one", async () => {
      const known = await run({ code: "return ById(Db.BlockTableId.Handle.ToString()) == Db.BlockTableId;" });
      const missing = await run({ code: "return ById(\"FFFFFFFF\") == ObjectId.Null;" });
      record("PR 2: ById resolves a handle and rejects an unknown one",
        known.status === "ok" && known.result === true && missing.status === "runtime_error"
          && /Object handle FFFFFFFF was not found or is erased/.test(missing.error?.message ?? ""),
        { known: known.result, missing: missing.status, error: missing.error?.message });
    });

    await check("PR 2: PaperSpace lists layouts for an unknown name", async () => {
      // The unknown name lists the drawing's layouts; the first listed one must then work.
      const missing = await run({ code: "return PaperSpace(\"RUNCODE-SMOKE-NO-LAYOUT\").Count();" });
      const first = (/Available layouts:s*([^,.]+)/i.exec(missing.error?.message ?? "") ?? [])[1]?.trim();
      const known = first ? await run({ code: "return PaperSpace((string)Args[\"layout\"]).Count();", args: { layout: first } }) : { status: "skipped" };
      record("PR 2: PaperSpace lists layouts for an unknown name",
        known.status === "ok" && typeof known.result === "number" && missing.status === "runtime_error" && Boolean(first),
        { known: known.status, count: known.result, missing: missing.error?.message });
    });

    await check("PR 2: plugin-internal CivilExecution call is rejected", async () => {
      const response = await run({ code: "Civil3DMcpPlugin.CivilExecution.QueueCommandLine(\"x\"); return 1;" });
      record("PR 2: plugin-internal CivilExecution call is rejected", response.status === "rejected",
        { status: response.status, errors: response.errors });
    });

    await check("PR 2: EnsureLayer is rejected in read mode", async () => {
      const response = await run({ code: "return EnsureLayer(\"RUNCODE-SMOKE-LAYER\", 1).Handle.ToString();" });
      record("PR 2: EnsureLayer is rejected in read mode", response.status === "rejected",
        { status: response.status, errors: response.errors });
    });
  }

  const noTokenName = approvalMode === "client"
    ? "approval: direct run without a token succeeds with approvedBy client"
    : "approval: refused without a token";
  await check(noTokenName, async () => {
    const response = await call("civil3d_run_code", { code: "return 1;" });
    const passed = approvalMode === "client"
      ? response.isError !== true && response.structuredContent?.result?.approvedBy === "client"
        && response.structuredContent?.result?.status === "ok" && response.structuredContent?.result?.result === 1
      : response.isError === true && /approval required/i.test(text(response));
    record(noTokenName, passed, text(response).slice(0, 160));
  });

  const wrongTokenName = approvalMode === "client"
    ? "approval: a token for other code is still refused"
    : "approval: a token for other code is refused";
  await check(wrongTokenName, async () => {
    const token = await approve("civil3d_run_code", "run", { code: "return 1;" });
    const response = await call("civil3d_run_code", { code: "return 2;", approvalToken: token });
    record(wrongTokenName, response.isError === true && /does not match/i.test(text(response)), text(response).slice(0, 160));
  });

  await check("read: counts model space and logs", async () => {
    const response = await run({ code: "var n = ModelSpace<Entity>().Count();\nLog($\"{n} entities\");\nreturn n;" });
    record("read: counts model space and logs", response.status === "ok" && typeof response.result === "number" && /entities/.test(response.output ?? ""), { result: response.result, timing: response.timing });
  });

  await check("read: adds no change to the drawing (DBMOD unchanged)", async () => {
    const before = await run({ code: "return Convert.ToInt32(Application.GetSystemVariable(\"DBMOD\"));" });
    await run({ code: "foreach (var e in ModelSpace<Entity>()) { var x = e.Layer; }\nreturn 0;" });
    const after = await run({ code: "return Convert.ToInt32(Application.GetSystemVariable(\"DBMOD\"));" });
    record("read: adds no change to the drawing (DBMOD unchanged)", before.result === after.result, { before: before.result, after: after.result });
  });

  await check("compile error with its line", async () => {
    const response = await run({ code: "var a = 1;\nvar b = a + ;\nreturn b;" });
    record("compile error with its line", response.status === "compile_error" && response.errors?.[0]?.line === 2, response.errors);
  });

  await check("read mode refuses OpenMode.ForWrite", async () => {
    const response = await run({ code: "foreach (var e in ModelSpace<Entity>()) Tr.GetObject(e.ObjectId, OpenMode.ForWrite);\nreturn 0;" });
    record("read mode refuses OpenMode.ForWrite", response.status === "rejected" && response.errors?.[0]?.code === "C3DMCP001", response.errors);
  });

  await check("read mode refuses Db.Ltscale++", async () => {
    const response = await run({ code: "Db.Ltscale++;\nreturn 0;" });
    record("read mode refuses Db.Ltscale++", response.status === "rejected" && response.errors?.[0]?.code === "C3DMCP001", response.errors);
  });

  await check("using on Tr does nothing: Tr still works after it", async () => {
    const response = await run({ code: "{ using var tx = Tr; }\nreturn Open<DBObject>(Db.BlockTableId).Handle.ToString();" });
    record("using on Tr does nothing: Tr still works after it", response.status === "ok" && typeof response.result === "string", { status: response.status, result: response.result, error: response.error });
  });

  await check("a Tr kept in Session throws in a later run (no crash)", async () => {
    await run({ code: "Session[\"smoke-tr\"] = Tr;\nreturn 1;" });
    const later = await run({ code: "return ((Transaction)Session[\"smoke-tr\"]).NumberOfOpenedObjects;" });
    const after = await run({ code: "Session.TryRemove(\"smoke-tr\", out _);\nreturn 7;" });
    record("a Tr kept in Session throws in a later run (no crash)", later.status === "runtime_error" && after.status === "ok" && after.result === 7,
      { later: later.status, error: later.error?.message, after: after.status });
  });

  await check("read: a nested open-close transaction in a using works", async () => {
    const response = await run({ code: "using var t = Db.TransactionManager.StartOpenCloseTransaction();\nvar bt = (BlockTable)t.GetObject(Db.BlockTableId, OpenMode.ForRead);\nreturn bt.Has(BlockTableRecord.ModelSpace);" });
    record("read: a nested open-close transaction in a using works", response.status === "ok" && response.result === true, { status: response.status, error: response.error });
  });

  await check("the plugin wrote this session's rpc-token", async () => {
    const path = selectedUserInstance?.tokenPath;
    const token = existsSync(path) ? readFileSync(path, "utf8").trim() : "";
    record("the plugin wrote this session's rpc-token", /^[0-9A-F]{64}$/i.test(token), { path, length: token.length });
  });

  await check("runCode without the token is refused (raw TCP)", async () => {
    const missing = await rawRpc("runCode", { code: "return 1;" });
    const wrong = await rawRpc("runCode", { code: "return 1;", authToken: "0".repeat(64) });
    record("runCode without the token is refused (raw TCP)",
      missing.error?.data?.code === "CIVIL3D.AUTH_REQUIRED" && wrong.error?.data?.code === "CIVIL3D.AUTH_REQUIRED",
      { missing: missing.error?.data?.code ?? missing.result, wrong: wrong.error?.data?.code ?? wrong.result });
  });

  await check("start window: a run that cannot start in time fails HOST_BUSY", async () => {
    const long = run({ code: "while (Elapsed.TotalSeconds < 12) Check();\nreturn 1;", timeoutSeconds: 30 });
    await new Promise((resolve) => setTimeout(resolve, 3000));
    const busy = await rawRpc("runCode", { code: "return 1;", mode: "read", timeoutSeconds: 10, startWithinSeconds: 3, authToken: selectedUserInstance?.tokenPath ? readFileSync(selectedUserInstance.tokenPath, "utf8").trim() : undefined });
    const first = await long;
    record("start window: a run that cannot start in time fails HOST_BUSY",
      busy.error?.data?.code === "CIVIL3D.HOST_BUSY" && first.status === "ok",
      { busy: busy.error ? `${busy.error.data?.code}: ${busy.error.message}` : busy.result, first: first.status });
  });

  await check("runtime error with its line", async () => {
    const response = await run({ code: "var a = 1;\nthrow new InvalidOperationException(\"boom\");" });
    record("runtime error with its line", response.status === "runtime_error" && response.error?.line === 2 && response.error?.message === "boom", response.error);
  });

  await check("timeout through Check()", async () => {
    const response = await run({ code: "while (true) Check();", timeoutSeconds: 3 });
    const runMs = response.timing?.runMs ?? 0;
    record("timeout through Check()", response.status === "timeout" && runMs >= 2900 && runMs < 10_000, { status: response.status, timing: response.timing });
  });

  await check("Civil 3D still answers after the timeout", async () => {
    const response = await run({ code: "return 7;" });
    record("Civil 3D still answers after the timeout", response.status === "ok" && response.result === 7);
  });

  await check("large result goes to a file", async () => {
    const response = await run({ code: "return Enumerable.Range(0, 200000).Select(i => (i * 0.5, i * 0.25)).ToList();" });
    record("large result goes to a file", response.status === "ok" && Boolean(response.resultFile) && response.resultBytes > 65_536 && response.result === null,
      { resultFile: response.resultFile, resultBytes: response.resultBytes, resultShape: response.resultShape });
  });

  await check("WriteFile output is listed", async () => {
    const response = await run({ code: "return WriteFile(\"points.txt\", new[] { \"1 2\", \"3 4\" });" });
    record("WriteFile output is listed", response.status === "ok" && (response.files ?? []).some((file) => file.endsWith("points.txt")), response.files);
  });

  await check("AutoCAD values become compact JSON", async () => {
    const response = await run({ code: "return new { p = new Point3d(1, 2, 3), id = Db.BlockTableId, ext = new Extents3d(new Point3d(0,0,0), new Point3d(1,1,0)), t = (1, \"a\") };" });
    const r = response.result ?? {};
    record("AutoCAD values become compact JSON", response.status === "ok" && Array.isArray(r.p) && r.p[2] === 3 && typeof r.id === "string" && Array.isArray(r.ext?.min) && Array.isArray(r.t), r);
  });

  await check("dictionaries keyed by ObjectId use handles", async () => {
    const response = await run({ code: "return ModelSpace<Entity>().Take(2).ToDictionary(e => e.ObjectId, e => e.Layer);" });
    const keys = Object.keys(response.result ?? {});
    record("dictionaries keyed by ObjectId use handles", response.status === "ok" && !response.resultError && keys.length > 0 && keys.every((key) => /^[0-9A-F]+$/i.test(key)), { result: response.result, resultError: response.resultError });
  });

  await check("Session keeps data across runs", async () => {
    await run({ code: "Session[\"smoke\"] = 42;\nreturn null;" });
    const response = await run({ code: "return Session[\"smoke\"];" });
    record("Session keeps data across runs", response.status === "ok" && response.result === 42, response.result);
  });

  if (sidePath) {
    await check("side database: bulk curve sampling", async () => {
      const code = [
        "var side = OpenSide((string)Args[\"dwg\"]);",
        "long objects = 0, curves = 0, points = 0;",
        "foreach (var e in ModelSpace<Entity>(side))",
        "{",
        "  objects++;",
        "  if (e is not Curve c) continue;",
        "  double length; try { length = c.GetDistanceAtParameter(c.EndParam); } catch (Exception) { continue; }",
        "  curves++;",
        "  for (double s = 0; s <= length; s += 0.8) { try { c.GetPointAtDist(s); points++; } catch (Exception) { } }",
        "}",
        "return new { objects, curves, points, seconds = Elapsed.TotalSeconds };",
      ].join("\n");
      const response = await run({ code, args: { dwg: sidePath }, timeoutSeconds: 90 });
      record("side database: bulk curve sampling", response.status === "ok" && response.result?.objects > 0, { result: response.result, timing: response.timing, error: response.error });
    });
    await check("the working database is restored after a failing run", async () => {
      const failing = await run({
        code: "var side = OpenSide((string)Args[\"dwg\"]);\nHostApplicationServices.WorkingDatabase = side;\nthrow new InvalidOperationException(\"fail with the side database as working database\");",
        args: { dwg: sidePath },
      });
      const after = await run({ code: "return HostApplicationServices.WorkingDatabase == Db;" });
      record("the working database is restored after a failing run", failing.status === "runtime_error" && after.status === "ok" && after.result === true, { failing: failing.status, restored: after.result });
    });
    await check("a side database kept in Session throws in a later run (no crash)", async () => {
      await run({ code: "var side = OpenSide((string)Args[\"dwg\"]);\nSession[\"smoke-side\"] = side;\nreturn ModelSpace<Entity>(side).Take(10).Count();", args: { dwg: sidePath } });
      const later = await run({ code: "return TransactionFor((Database)Session[\"smoke-side\"]).NumberOfOpenedObjects;" });
      const after = await run({ code: "Session.TryRemove(\"smoke-side\", out _);\nreturn 7;" });
      record("a side database kept in Session throws in a later run (no crash)", later.status === "runtime_error" && after.status === "ok" && after.result === 7,
        { later: later.status, error: later.error?.message, after: after.status });
    });
  } else {
    skip("side database (no --side)");
  }

  // Writes: add a tagged text, check it, undo once, check it is gone.
  const tag = `RUNCODE-SMOKE-${process.pid}`;
  const countTagged = `return ModelSpace<DBText>().Count(t => t.TextString == (string)Args["tag"]);`;
  const writesAllowed = Boolean(expectDrawing) || options["allow-writes"];
  if (!writesAllowed) {
    skip("write cases (pass --expect-drawing <scratch drawing name>, or --allow-writes)");
  }

  if (writesAllowed) {
  if (options.other && runCodeApi >= 5) {
    const otherPath = resolve(options.other);
    const otherTag = `${tag}-OTHER`;
    const countOther = async (value) => await run({ document: otherPath,
      code: "return Tagged<DBText>(\"RUNCODE-SMOKE-PR4\").Count(t => t.TextString == (string)Args[\"tag\"]);",
      args: { tag: value } });
    const activeName = async () => (await run({ code: "return Doc.Name;" })).result;
    const addOther = [
      "var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), forWrite: true);",
      "var text = new DBText { TextString = (string)Args[\"tag\"], Position = new Point3d(-1e6, -1e6, 0), Height = 1 };",
      "ms.AppendEntity(text);",
      "Tr.AddNewlyCreatedDBObject(text, true);",
      "Tag(text, \"RUNCODE-SMOKE-PR4\");",
      "return text.Handle.ToString();",
    ].join("\n");
    try {
      await check("PR 4: targeted write commits in B and switches back to A", async () => {
        const added = await run({ document: otherPath, mode: "write", code: addOther, args: { tag: otherTag } });
        const count = await countOther(otherTag);
        const active = await activeName();
        record("PR 4: targeted write commits in B and switches back to A",
          added.status === "ok" && added.committed === true && added.activated === true && added.switchedBack === true
            && added.drawing?.toLowerCase() === otherPath.toLowerCase()
            && count.status === "ok" && count.result === 1 && active?.toLowerCase() === drawingName.toLowerCase(),
          { status: added.status, committed: added.committed, activated: added.activated,
            switchedBack: added.switchedBack, drawing: added.drawing, count: count.result, active });
      });
      await check("PR 4: failing targeted write rolls back and switches back", async () => {
        const before = await countOther(`${otherTag}-ROLLBACK`);
        const failed = await run({ document: otherPath, mode: "write", args: { tag: `${otherTag}-ROLLBACK` },
          code: addOther.replace("return text.Handle.ToString();", "throw new InvalidOperationException(\"roll back\");") });
        const after = await countOther(`${otherTag}-ROLLBACK`);
        const committedCount = await countOther(otherTag);
        const active = await activeName();
        record("PR 4: failing targeted write rolls back and switches back",
          failed.status === "runtime_error" && failed.committed === false && failed.activated === true
            && failed.switchedBack === true && failed.drawing?.toLowerCase() === otherPath.toLowerCase()
            && before.status === "ok" && before.result === 0 && after.status === "ok" && after.result === before.result
            && committedCount.status === "ok" && committedCount.result === 1
            && active?.toLowerCase() === drawingName.toLowerCase(),
          { status: failed.status, committed: failed.committed, activated: failed.activated,
            switchedBack: failed.switchedBack, drawing: failed.drawing, before: before.result,
            after: after.result, committedCount: committedCount.result, active });
      });
    } finally {
      await check("PR 4: targeted cleanup erases smoke text in B", async () => {
        const cleanup = await run({ document: otherPath, mode: "write", args: { tag: otherTag }, code: [
          "foreach (var item in Tagged<DBText>(\"RUNCODE-SMOKE-PR4\").Where(t => t.TextString == (string)Args[\"tag\"] || t.TextString == (string)Args[\"tag\"] + \"-ROLLBACK\").ToList())",
          "  Open<DBText>(item.ObjectId, forWrite: true).Erase();",
          "return 1;",
        ].join("\n") });
        const count = await countOther(otherTag);
        const rollbackCount = await countOther(`${otherTag}-ROLLBACK`);
        const active = await activeName();
        record("PR 4: targeted cleanup erases smoke text in B",
          cleanup.status === "ok" && cleanup.switchedBack === true && count.status === "ok" && count.result === 0
            && rollbackCount.status === "ok" && rollbackCount.result === 0
            && active?.toLowerCase() === drawingName.toLowerCase(),
          { status: cleanup.status, count: count.result, rollbackCount: rollbackCount.result, active });
      });
    }
  } else if (options.other) {
    skip("PR 4 targeted write checks (plugin runCodeApi below 5)", `runCodeApi=${runCodeApi}`);
  }
  if (runCodeApi >= 3) {
    await check("PR 2 write: EnsureLayer twice returns the same handle", async () => {
      const response = await run({ mode: "write", code: [
        "var first = EnsureLayer(\"RUNCODE-SMOKE-LAYER\", 1);",
        "var second = EnsureLayer(\"RUNCODE-SMOKE-LAYER\", 1);",
        "return new { first = first.Handle.ToString(), second = second.Handle.ToString() };",
      ].join("\n") });
      record("PR 2 write: EnsureLayer twice returns the same handle",
        response.status === "ok" && response.result?.first === response.result?.second
          && /^[0-9A-F]+$/i.test(response.result?.first ?? ""),
        { status: response.status, result: response.result, error: response.error });
    });

    const cleanupTagged = [
      "foreach (var entity in Tagged<Entity>(\"RUNCODE-SMOKE-PR2\").ToList())",
      "  Open<Entity>(entity.ObjectId, forWrite: true).Erase();",
      "return 1;",
    ].join("\n");
    try {
      await check("PR 2 write: Tag/Tagged rerun leaves three texts", async () => {
        const script = [
          "foreach (var entity in Tagged<Entity>(\"RUNCODE-SMOKE-PR2\").ToList())",
          "  Open<Entity>(entity.ObjectId, forWrite: true).Erase();",
          "var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), forWrite: true);",
          "for (var i = 0; i < 3; i++)",
          "{",
          "  var item = new DBText { TextString = $\"RUNCODE-SMOKE-PR2-{i}\", Position = new Point3d(-1e6 - i * 10, -1e6, 0), Height = 1 };",
          "  ms.AppendEntity(item);",
          "  Tr.AddNewlyCreatedDBObject(item, true);",
          "  Tag(item, \"RUNCODE-SMOKE-PR2\");",
          "}",
          "return Tagged<Entity>(\"RUNCODE-SMOKE-PR2\").Count();",
        ].join("\n");
        const first = await run({ mode: "write", code: script });
        const second = await run({ mode: "write", code: script });
        const after = await run({ code: "return Tagged<Entity>(\"RUNCODE-SMOKE-PR2\").Count();" });
        record("PR 2 write: Tag/Tagged rerun leaves three texts",
          first.status === "ok" && first.result === 3 && second.status === "ok" && second.result === 3
            && after.status === "ok" && after.result === 3,
          { first: first.status, firstCount: first.result, second: second.status, secondCount: second.result, after: after.result });
      });
    } finally {
      await check("PR 2 write: final tagged-text cleanup", async () => {
        const cleanup = await run({ mode: "write", code: cleanupTagged });
        const after = await run({ code: "return Tagged<Entity>(\"RUNCODE-SMOKE-PR2\").Count();" });
        record("PR 2 write: final tagged-text cleanup",
          cleanup.status === "ok" && after.status === "ok" && after.result === 0,
          { cleanup: cleanup.status, after: after.result, error: cleanup.error });
      });
    }
  }
  await check("write: adds an object, one undo removes it", async () => {
    const add = await run({
      mode: "write",
      args: { tag },
      code: [
        "var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), forWrite: true);",
        "var text = new DBText { TextString = (string)Args[\"tag\"], Position = new Point3d(-1e6, -1e6, 0), Height = 1 };",
        "ms.AppendEntity(text);",
        "Tr.AddNewlyCreatedDBObject(text, true);",
        "return text.Handle.ToString();",
      ].join("\n"),
    });
    const afterAdd = await run({ code: countTagged, args: { tag } });
    // Undo only what this case committed: an undo after a rolled-back add would revert the
    // drawing's previous undo group instead.
    let undo = { isError: true, content: [{ text: "not sent: the add did not commit" }] };
    let afterUndo = { result: afterAdd.result };
    if (add.status === "ok" && add.committed === true && afterAdd.result === 1) {
      const undoToken = await approve("civil3d_drawing", "undo", { action: "undo", steps: 1 });
      undo = await call("civil3d_drawing", { action: "undo", steps: 1, approvalToken: undoToken });
      await new Promise((resolve) => setTimeout(resolve, 1500));
      afterUndo = await run({ code: countTagged, args: { tag } });
    }
    record("write: adds an object, one undo removes it",
      add.status === "ok" && add.committed === true && afterAdd.result === 1 && !undo.isError && afterUndo.result === 0,
      { add: add.status, committed: add.committed, afterAdd: afterAdd.result, undo: undo.isError ? text(undo) : "ok", afterUndo: afterUndo.result });
  });

  await check("write: an exception rolls the change back", async () => {
    const failed = await run({
      mode: "write",
      args: { tag: `${tag}-ROLLBACK` },
      code: [
        "var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), forWrite: true);",
        "var text = new DBText { TextString = (string)Args[\"tag\"], Position = new Point3d(-1e6, -1e6, 0), Height = 1 };",
        "ms.AppendEntity(text);",
        "Tr.AddNewlyCreatedDBObject(text, true);",
        "throw new InvalidOperationException(\"roll back\");",
      ].join("\n"),
    });
    const after = await run({ code: countTagged, args: { tag: `${tag}-ROLLBACK` } });
    record("write: an exception rolls the change back", failed.status === "runtime_error" && after.result === 0, { failed: failed.status, after: after.result });
  });

  await check("write: a timeout the script catches is still rolled back", async () => {
    const swallowed = await run({
      mode: "write",
      timeoutSeconds: 2,
      args: { tag: `${tag}-SWALLOWED` },
      code: [
        "var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), forWrite: true);",
        "var text = new DBText { TextString = (string)Args[\"tag\"], Position = new Point3d(-1e6, -1e6, 0), Height = 1 };",
        "ms.AppendEntity(text);",
        "Tr.AddNewlyCreatedDBObject(text, true);",
        "try { while (true) Check(); } catch (Exception) { }",
        "return \"caught it\";",
      ].join("\n"),
    });
    const after = await run({ code: countTagged, args: { tag: `${tag}-SWALLOWED` } });
    record("write: a timeout the script catches is still rolled back", swallowed.status === "timeout" && after.result === 0, { status: swallowed.status, after: after.result, warnings: swallowed.warnings });
  });

  await check("write: Commit through an alias of Tr is refused and rolled back", async () => {
    const aliased = await run({
      mode: "write",
      args: { tag: `${tag}-ALIAS` },
      code: [
        "var tx = Tr;",
        "var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), forWrite: true);",
        "var text = new DBText { TextString = (string)Args[\"tag\"], Position = new Point3d(-1e6, -1e6, 0), Height = 1 };",
        "ms.AppendEntity(text);",
        "tx.AddNewlyCreatedDBObject(text, true);",
        "tx.Commit();",
        "return 1;",
      ].join("\n"),
    });
    const after = await run({ code: countTagged, args: { tag: `${tag}-ALIAS` } });
    record("write: Commit through an alias of Tr is refused and rolled back",
      aliased.status === "runtime_error" && aliased.committed === false && /owns Tr/.test(aliased.error?.message ?? "") && after.result === 0,
      { status: aliased.status, committed: aliased.committed, error: aliased.error?.message, after: after.result });
  });

  await check("write: a nested transaction in a using commits with the run", async () => {
    const nested = await run({
      mode: "write",
      args: { tag: `${tag}-NESTED` },
      code: [
        "using (var t = Db.TransactionManager.StartTransaction())",
        "{",
        "  var ms = (BlockTableRecord)t.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(Db), OpenMode.ForWrite);",
        "  var text = new DBText { TextString = (string)Args[\"tag\"], Position = new Point3d(-1e6, -1e6, 0), Height = 1 };",
        "  ms.AppendEntity(text);",
        "  t.AddNewlyCreatedDBObject(text, true);",
        "  t.Commit();",
        "}",
        "throw new InvalidOperationException(\"roll back the outer run\");",
      ].join("\n"),
    });
    const after = await run({ code: countTagged, args: { tag: `${tag}-NESTED` } });
    // The nested commit lands in the run's transaction, so the run's rollback removes it too.
    // Only the script's own last line may fail: an earlier error would also leave nothing behind.
    record("write: a nested transaction in a using commits with the run",
      nested.status === "runtime_error" && nested.error?.message === "roll back the outer run" && nested.committed === false && after.result === 0,
      { status: nested.status, error: nested.error?.message, committed: nested.committed, after: after.result, warnings: nested.warnings });
  });

  await check("write: using var tx = Tr commits as usual, one undo removes it", async () => {
    const add = await run({
      mode: "write",
      args: { tag: `${tag}-USING` },
      code: [
        "using var tx = Tr;",
        "var ms = (BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(Db), OpenMode.ForWrite);",
        "var text = new DBText { TextString = (string)Args[\"tag\"], Position = new Point3d(-1e6, -1e6, 0), Height = 1 };",
        "ms.AppendEntity(text);",
        "tx.AddNewlyCreatedDBObject(text, true);",
        "return text.Handle.ToString();",
      ].join("\n"),
    });
    const afterAdd = await run({ code: countTagged, args: { tag: `${tag}-USING` } });
    let undo = { isError: true, content: [{ text: "not sent: the add did not commit" }] };
    let afterUndo = { result: afterAdd.result };
    if (add.status === "ok" && add.committed === true && afterAdd.result === 1) {
      const undoToken = await approve("civil3d_drawing", "undo", { action: "undo", steps: 1 });
      undo = await call("civil3d_drawing", { action: "undo", steps: 1, approvalToken: undoToken });
      await new Promise((resolve) => setTimeout(resolve, 1500));
      afterUndo = await run({ code: countTagged, args: { tag: `${tag}-USING` } });
    }
    record("write: using var tx = Tr commits as usual, one undo removes it",
      add.status === "ok" && add.committed === true && afterAdd.result === 1 && !undo.isError && afterUndo.result === 0,
      { add: add.status, committed: add.committed, error: add.error?.message, afterAdd: afterAdd.result, undo: undo.isError ? text(undo) : "ok", afterUndo: afterUndo.result });
  });

  }

  }
  if (options.hardening) await hardeningChecks(!options["no-drawing"]);
  if (options.preview) await previewChecks(runCodeApi, drawingName);
  console.log(JSON.stringify({ status: failures === 0 ? "passed" : "failed", passes: results.filter((result) => result.passed === true).length,
    failures, skips, drawing: drawingName, results }, null, 2));
  process.exitCode = failures === 0 ? 0 : 1;
} catch (error) {
  printServerStderr();
  console.error(`Live run-code smoke stopped: ${error instanceof Error ? error.message : String(error)}`);
  console.log(JSON.stringify({ status: "stopped", passes: results.filter((result) => result.passed === true).length, failures, skips, results }, null, 2));
  process.exitCode = 1;
} finally {
  // Closing the stdio transport can leave a promise that never settles once
  // the server exits; Node would then end with code 13 (unsettled top-level
  // await) over the real result, so bound it and exit explicitly.
  await Promise.race([
    client.close().catch(() => undefined),
    new Promise((resolve) => setTimeout(resolve, 5000)),
  ]);
}
process.exit(process.exitCode ?? 0);
