// Diagnostic for issue #57 (kept until it is settled). Experiment: why do structure profile labels
// land at the drawing origin? Runs in an AGENT instance that the caller started on
// a disposable drawing (civil3d_instance start), attached with force: true.
//
//   npm run build
//   node scripts/scratch-live-57.mjs --expect-drawing <absolute path of the scratch .dwg>
//
// It adds an alignment, two profile views and a pipe network with one structure of
// EVERY structure family in the chosen parts list (the smoke takes family [0]) plus a
// pipe, all shown in both views, named L57-<pid>-.... Then, per structure and view:
//   B  StructureProfileLabel.Create in a write run, read in the same transaction
//      (ViewId, FeatureId, AnchorInfo.Location, LabelLocation, Dragged, and the
//      plugin's new origin gate);
//   C  label_add through the installed plugin (what the smoke does);
//   D  a read run after commit: GeometricExtents, the view each label sits in, the
//      anchor and the gate again;
//   E  only if a label is at the origin: one copy gets LabelLocation set to its
//      structure's point in the view (does that help, or does it drag a leader?).
// Nothing is removed. Prints one JSON report on stdout.
import { parseArgs } from "node:util";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { drainServerStderr } from "./server-stderr.mjs";
import { attachScratchAgentInstance } from "./live-agent-instance.mjs";

const { values: options } = parseArgs({ options: { "expect-drawing": { type: "string" } }, strict: true });
const expectDrawing = options["expect-drawing"];
if (!expectDrawing) {
  console.error("usage: node scripts/scratch-live-57.mjs --expect-drawing <absolute path of the scratch drawing>");
  process.exit(2);
}

const prefix = `L57-${process.pid}`;
const transport = new StdioClientTransport({
  command: process.execPath,
  args: ["./build/index.js"],
  cwd: process.cwd(),
  env: { ...process.env, MCP_HTTP_HOST: "127.0.0.1", MCP_HTTP_PORT: "0" },
  stderr: "pipe",
});
const printServerStderr = drainServerStderr(transport.stderr);
const client = new Client({ name: "civil3d-issue57-scratch", version: "1.0.0" });
const text = (result) => (result.content ?? []).map((item) => item.text ?? "").join("\n");
const call = (name, args) => client.callTool({ name, arguments: args }, undefined, { timeout: 180_000, maxTotalTimeout: 180_000 });

async function gated(toolName, args) {
  let response = await call(toolName, args);
  if (response.isError && /approval required/i.test(text(response))) {
    const approval = await call("civil3d_request_approval", { toolName, action: args.action, parameters: args });
    if (approval.isError) throw new Error(`approval failed: ${text(approval)}`);
    response = await call(toolName, { ...args, approvalToken: approval.structuredContent.approvalToken });
  }
  return response;
}

const runNotes = [];
async function runCode(code, args = {}, mode = "read") {
  const parameters = { code, args, mode, timeoutSeconds: 60 };
  let response = await call("civil3d_run_code", parameters);
  if (response.isError && /approval required/i.test(text(response))) {
    const approval = await call("civil3d_request_approval", { toolName: "civil3d_run_code", action: "run", parameters });
    if (approval.isError) throw new Error(`approval failed: ${text(approval)}`);
    response = await call("civil3d_run_code", { ...parameters, approvalToken: approval.structuredContent.approvalToken });
  }
  if (response.isError) throw new Error(`civil3d_run_code failed: ${text(response)}`);
  const result = response.structuredContent.result;
  if ((result.tryErrors ?? []).length > 0 || (result.warnings ?? []).length > 0) runNotes.push({ tryErrors: result.tryErrors, warnings: result.warnings });
  if (result.status !== "ok") throw new Error(`run_code ${result.status}: ${JSON.stringify(result.errors ?? result.error).slice(0, 600)}`);
  return result.result;
}

// The plugin's gate (LabelCommands.DescribeLabelAtOrigin), copied: null = accepted.
const gateCode = String.raw`
string Gate(Autodesk.Civil.DatabaseServices.Label label, ProfileView view)
{
  const double tolerance = 1.0;
  try
  {
    var anchor = label.AnchorInfo.Location;
    if (Math.Abs(anchor.X) > tolerance || Math.Abs(anchor.Y) > tolerance) return null;
    double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
    if (!view.FindXYAtStationAndElevation(view.StationStart, view.ElevationMin, ref x1, ref y1)
      || !view.FindXYAtStationAndElevation(view.StationEnd, view.ElevationMax, ref x2, ref y2)) return null;
    var inView = Math.Min(x1, x2) - tolerance <= anchor.X && anchor.X <= Math.Max(x1, x2) + tolerance
      && Math.Min(y1, y2) - tolerance <= anchor.Y && anchor.Y <= Math.Max(y1, y2) + tolerance;
    return inView ? null : view.Name;
  }
  catch (Exception) { return null; }
}
double[] P(Point3d p) => new[] { Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4) };
// Every read guarded: a broken label is what this looks at.
object Safe(Func<object> read) { try { return read(); } catch (Exception ex) { return "error: " + ex.GetType().Name + ": " + ex.Message; } }
object Probe(Autodesk.Civil.DatabaseServices.Label label)
{
  ProfileView pv = null;
  try { pv = label.ViewId.IsNull ? null : Open<ProfileView>(label.ViewId); } catch (Exception) { }
  return new
  {
    handle = label.Handle.ToString(),
    type = label.GetType().Name,
    viewId = Safe(() => label.ViewId.IsNull ? "null" : label.ViewId.ObjectClass.Name),
    view = pv?.Name,
    featureClass = Safe(() => label.FeatureId.IsNull ? "null" : label.FeatureId.ObjectClass.Name),
    anchor = Safe(() => P(label.AnchorInfo.Location)),
    labelLocation = Safe(() => P(label.LabelLocation)),
    dragged = Safe(() => label.Dragged),
    style = Safe(() => label.StyleName),
    gate = pv == null ? "no view" : Gate(label, pv),
  };
}
`;

const setupCode = String.raw`
using Autodesk.Civil.DatabaseServices.Styles;
var p = (string)Args["prefix"];
var x0 = (double)Args["x0"];
ObjectId partsList = ObjectId.Null;
foreach (ObjectId id in Civil.Styles.PartsListSet)
{
  var candidate = Open<PartsList>(id);
  if (candidate.GetPartFamilyIdsByDomain(DomainType.Pipe).Count > 0 && candidate.GetPartFamilyIdsByDomain(DomainType.Structure).Count > 0) { partsList = id; break; }
}
if (partsList.IsNull) throw new InvalidOperationException("No parts list with both pipe and structure families.");
var alignmentId = Autodesk.Civil.DatabaseServices.Alignment.Create(Civil, p + "-AL", ObjectId.Null, Db.Clayer,
  Civil.Styles.AlignmentStyles[0], Civil.Styles.LabelSetStyles.AlignmentLabelSetStyles[0]);
var alignment = Open<Autodesk.Civil.DatabaseServices.Alignment>(alignmentId, forWrite: true);
alignment.Entities.AddFixedLine(new Point3d(x0, 0, 0), new Point3d(x0 + 300, 0, 0));
var views = new List<ObjectId>();
foreach (var (name, y) in new[] { (p + "-PV1", -200.0), (p + "-PV2", -400.0) })
{
  var pvId = ProfileView.Create(alignmentId, new Point3d(x0, y, 0), name, Civil.Styles.ProfileViewBandSetStyles[0], Civil.Styles.ProfileViewStyles[0]);
  var pv = Open<ProfileView>(pvId, forWrite: true);
  pv.ElevationRangeMode = ElevationRangeType.UserSpecified;
  pv.ElevationMin = -20;
  pv.ElevationMax = 20;
  views.Add(pvId);
}
var netName = p + "-NET";
var net = Open<Network>(Network.Create(Civil, ref netName), forWrite: true);
net.PartsListId = partsList;
var list = Open<PartsList>(partsList);
var pipeFamily = list.GetPartFamilyIdsByDomain(DomainType.Pipe)[0];
ObjectId pipe = ObjectId.Null;
net.AddLinePipe(pipeFamily, Open<PartFamily>(pipeFamily)[0], new LineSegment3d(new Point3d(x0 + 10, 0, -5), new Point3d(x0 + 90, 0, -6)), ref pipe, false);
var pipePart = Open<Part>(pipe, forWrite: true);
pipePart.Name = p + "-PIPE";
foreach (var pvId in views) pipePart.AddToProfileView(pvId);
var structures = new List<object>();
var families = list.GetPartFamilyIdsByDomain(DomainType.Structure);
for (var i = 0; i < families.Count && i < 6; i++)
{
  var family = Open<PartFamily>(families[i]);
  var info = new Dictionary<string, object> { ["index"] = i, ["family"] = family.Name, ["familyPartType"] = family.PartType.ToString(), ["familyShape"] = family.BoundingShape.ToString(), ["sizes"] = family.PartSizeCount };
  if (family.PartSizeCount == 0) { info["error"] = "no sizes"; structures.Add(info); continue; }
  var sid = ObjectId.Null;
  try
  {
    net.AddStructure(families[i], family[0], new Point3d(x0 + 120 + 30 * i, 0, 0), 0, ref sid, false);
    var s = Open<Structure>(sid, forWrite: true);
    s.Name = p + "-S" + i;
    info["name"] = s.Name;
    foreach (var pvId in views) s.AddToProfileView(pvId);
    info["partType"] = s.PartType.ToString();
    info["shape"] = s.BoundingShape.ToString();
    info["rim"] = s.RimElevation;
    info["sump"] = s.SumpElevation;
    info["style"] = s.StyleName;
  }
  catch (Exception ex) { info["error"] = ex.GetType().Name + ": " + ex.Message; }
  structures.Add(info);
}
return new { partsList = list.Name, structureFamilies = families.Count, structures };
`;

// Per structure (and the pipe) and view: create in this transaction and probe at once.
const createCode = String.raw`
var p = (string)Args["prefix"];
${gateCode}
var views = ModelSpace<ProfileView>().Where(v => v.Name.StartsWith(p)).OrderBy(v => v.Name).ToList();
var structureStyle = Civil.Styles.LabelStyles.StructureLabelStyles.LabelStyles[0];
var pipeStyle = Civil.Styles.LabelStyles.PipeLabelStyles.PlanProfileLabelStyles[0];
var rows = new List<object>();
foreach (var part in ModelSpace<Part>().Where(x => x.Name.StartsWith(p)).OrderBy(x => x.Name).ToList())
{
  var pvps = ModelSpace<ProfileViewPart>().Where(v => v.ModelPartId == part.ObjectId).Select(v => v.Handle.ToString()).ToArray();
  foreach (var view in views)
  {
    if (part.PartType == PartType.Pipe && part.Name.EndsWith("-PIPE") == false) continue;
    try
    {
      var labelId = part is Structure
        ? StructureProfileLabel.Create(view.ObjectId, part.ProfileViewPartId, structureStyle)
        : PipeProfileLabel.Create(part.ProfileViewPartId, view.ObjectId, 0.5, pipeStyle);
      var label = Open<Autodesk.Civil.DatabaseServices.Label>(labelId);
      rows.Add(new { part = part.Name, partType = part.PartType.ToString(), wantedView = view.Name, profileViewPart = part.ProfileViewPartId.Handle.ToString(), pvps, featureIsPvp = Safe(() => label.FeatureId == part.ProfileViewPartId), featureIsPart = Safe(() => label.FeatureId == part.ObjectId), inTransaction = Probe(label) });
    }
    catch (Exception ex) { rows.Add(new { part = part.Name, wantedView = view.Name, error = ex.GetType().Name + ": " + ex.Message }); }
  }
}
return rows;
`;

// After commit: where each label is drawn, and the same probes again.
const readCode = String.raw`
var p = (string)Args["prefix"];
${gateCode}
var views = ModelSpace<ProfileView>().Where(v => v.Name.StartsWith(p)).Select(v => (v.Name, v.GeometricExtents)).ToList();
string ViewOf(Point3d c)
{
  foreach (var (name, v) in views)
    if (c.X >= v.MinPoint.X && c.X <= v.MaxPoint.X && c.Y >= v.MinPoint.Y && c.Y <= v.MaxPoint.Y) return name.Substring(p.Length);
  return null;
}
var rows = new List<object>();
foreach (var h in ((JsonArray)Args["handles"]).Select(n => (string)n))
{
  Autodesk.Civil.DatabaseServices.Label label;
  try { label = Open<Autodesk.Civil.DatabaseServices.Label>(ById(h)); }
  catch (Exception ex) { rows.Add(new { handle = h, error = ex.GetType().Name + ": " + ex.Message }); continue; }
  var extents = Try("extents", () => { var x = label.GeometricExtents; return new[] { P(x.MinPoint), P(x.MaxPoint) }; });
  string sitsIn = null;
  try { var x = label.GeometricExtents; sitsIn = ViewOf(x.MinPoint + (x.MaxPoint - x.MinPoint) / 2.0); } catch (Exception) { }
  string anchorIn = null;
  try { anchorIn = ViewOf(label.AnchorInfo.Location); } catch (Exception) { }
  rows.Add(new { extents, sitsIn, anchorIn, probe = Probe(label) });
}
return rows;
`;

// One label at the origin: set LabelLocation to its structure's point in its view.
const moveCode = String.raw`
${gateCode}
var label = Open<Autodesk.Civil.DatabaseServices.Label>(ById((string)Args["handle"]), forWrite: true);
var view = Open<ProfileView>(label.ViewId);
var s = Open<Structure>(ById((string)Args["structure"]));
var alignment = Open<Autodesk.Civil.DatabaseServices.Alignment>(view.AlignmentId);
double station = 0, offset = 0, x = 0, y = 0;
alignment.StationOffset(s.Position.X, s.Position.Y, ref station, ref offset);
var found = view.FindXYAtStationAndElevation(station, s.RimElevation, ref x, ref y);
var before = Probe(label);
label.LabelLocation = new Point3d(x, y, 0);
return new { found, target = new[] { x, y }, before, after = Probe(label) };
`;

const report = { prefix, drawing: null, setup: null, direct: null, tool: [], after: null, move: null, runNotes };
try {
  await client.connect(transport);
  const agent = await attachScratchAgentInstance(call, expectDrawing);
  report.instance = { pid: agent.pid, acadVersion: agent.acadVersion, documents: agent.documents };
  report.drawing = await runCode("return Doc.Name;");
  if (!String(report.drawing).toLowerCase().includes(expectDrawing.toLowerCase())) {
    throw new Error(`Active drawing '${report.drawing}' does not contain '${expectDrawing}'; stopping before any write.`);
  }
  report.version = await runCode("return new { acad = Autodesk.AutoCAD.ApplicationServices.Core.Application.Version.ToString(), civil = typeof(Autodesk.Civil.DatabaseServices.Structure).Assembly.GetName().Version.ToString() };");

  report.setup = await runCode(setupCode, { prefix, x0: (process.pid % 50) * 2000 + 10_000 }, "write");
  console.error("setup", JSON.stringify(report.setup));
  report.direct = await runCode(createCode, { prefix }, "write");
  console.error("direct", JSON.stringify(report.direct).slice(0, 2000));

  for (const s of report.setup.structures.filter((s) => s.name)) {
    const r = await gated("civil3d_standards", { action: "label_add", objectType: "structure", objectName: s.name, labelType: "profile" });
    report.tool.push({ structure: s.name, family: s.family, isError: r.isError === true, result: r.isError ? text(r) : (r.structuredContent?.result ?? r.structuredContent) });
  }
  const pipe = await gated("civil3d_standards", { action: "label_add", objectType: "pipe", objectName: `${prefix}-PIPE`, labelType: "profile" });
  report.tool.push({ pipe: `${prefix}-PIPE`, isError: pipe.isError === true, result: pipe.isError ? text(pipe) : (pipe.structuredContent?.result ?? pipe.structuredContent) });

  const handles = [
    ...report.direct.filter((r) => r.inTransaction).map((r) => r.inTransaction.handle),
    ...report.tool.flatMap((t) => (t.isError ? [] : t.result.handles ?? [])),
  ];
  report.after = await runCode(readCode, { prefix, handles });

  const atOrigin = report.direct.find((r) => r.partType !== "Pipe" && r.inTransaction
    && report.after.find((a) => (a.probe?.handle ?? a.handle) === r.inTransaction.handle && a.sitsIn === null));
  if (atOrigin) {
    const structure = await runCode(String.raw`return ModelSpace<Structure>().First(s => s.Name == (string)Args["name"]).Handle.ToString();`, { name: atOrigin.part });
    report.move = await runCode(moveCode, { handle: atOrigin.inTransaction.handle, structure }, "write");
    report.moveAfter = await runCode(readCode, { prefix, handles: [atOrigin.inTransaction.handle] });
  }

  // Summary: per part, did the direct and the tool labels land in their views, and
  // did the gate flag them in the transaction and after commit?
  const after = new Map(report.after.map((a) => [a.probe?.handle ?? a.handle, a]));
  report.summary = report.direct.map((r) => ({
    part: r.part,
    family: report.setup.structures.find((s) => s.name === r.part)?.family ?? "pipe",
    partType: r.partType,
    view: r.wantedView,
    error: r.error,
    sitsIn: r.inTransaction ? after.get(r.inTransaction.handle)?.sitsIn ?? null : null,
    gateInTransaction: r.inTransaction?.gate ?? null,
    gateAfterCommit: r.inTransaction ? after.get(r.inTransaction.handle)?.probe?.gate ?? null : null,
    anchorInTransaction: r.inTransaction?.anchor ?? null,
    anchorAfterCommit: r.inTransaction ? after.get(r.inTransaction.handle)?.probe?.anchor ?? null : null,
  }));
  report.toolSummary = report.tool.map((t) => ({
    part: t.structure ?? t.pipe,
    family: t.family ?? "pipe",
    sitsIn: t.isError ? t.result : (t.result.handles ?? []).map((h) => after.get(h)?.sitsIn ?? null),
    warnings: t.isError ? undefined : t.result.warnings,
  }));
  console.log(JSON.stringify(report, null, 2));
} catch (error) {
  printServerStderr();
  console.error(`Issue 57 scratch stopped: ${error instanceof Error ? error.message : String(error)}`);
  console.log(JSON.stringify(report, null, 2));
  process.exitCode = 1;
} finally {
  await Promise.race([client.close().catch(() => undefined), new Promise((resolve) => setTimeout(resolve, 5000))]);
}
process.exit(process.exitCode ?? 0);
