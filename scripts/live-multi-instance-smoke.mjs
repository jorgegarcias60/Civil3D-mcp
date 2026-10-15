// Opt-in LIVE check. Both arguments must be disposable, saved scratch DWGs.
// Starts two Civil 3D processes, edits A in memory and discards their changes.
//   node scripts/live-multi-instance-smoke.mjs C:\scratch\A.dwg C:\scratch\B.dwg
import assert from "node:assert/strict";
import { copyFile, mkdtemp, rm, stat } from "node:fs/promises";
import { tmpdir } from "node:os";
import { isAbsolute, join, resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { drainServerStderr } from "./server-stderr.mjs";

const sessions = [];
const started = new Map();
const uncertainStarts = new Map();
// InstanceManager keeps a launch claim for up to five minutes. Start the
// cleanup window when the failed response arrives, then allow a brief interval
// for a claim taken just before expiry to finish publishing discovery.
const CLAIM_WINDOW_MS = 330_000;
const DISCOVERY_GRACE_MS = 2_000;
let temp;
let capDrawing;
const text = reply => (reply.content ?? []).map(c => c.text ?? "").join("\n");
const same = (a, b) => resolve(a).toLowerCase() === resolve(b).toLowerCase();

async function session(label, cap = "3", fixedPort) {
  const env = { ...process.env, CIVIL3D_SESSION_LABEL: label, CIVIL3D_MAX_AGENT_INSTANCES: cap,
    MCP_HTTP_HOST: "127.0.0.1", MCP_HTTP_PORT: "0" };
  delete env.CIVIL3D_PORT;
  delete env.CIVIL3D_ALLOW_FIXED_INSTANCE;
  if (fixedPort !== undefined) env.CIVIL3D_PORT = String(fixedPort);
  const transport = new StdioClientTransport({ command: process.execPath, args: ["./build/index.js"],
    cwd: process.cwd(), env, stderr: "pipe" });
  const printStderr = drainServerStderr(transport.stderr);
  const client = new Client({ name: label, version: "1.0.0" });
  const s = { client, transport, printStderr, sessionId: undefined };
  sessions.push(s);
  await client.connect(transport);
  s.sessionId = result(await call(s, "civil3d_instance", { action: "list" })).sessionId;
  assert(s.sessionId, `Missing session ID for ${label}.`);
  return s;
}

async function call(s, name, args) {
  return await s.client.callTool({ name, arguments: args }, undefined,
    { timeout: 360_000, maxTotalTimeout: 420_000, resetTimeoutOnProgress: true });
}

async function approved(s, name, args) {
  const action = args.action ?? "run";
  const grant = await call(s, "civil3d_request_approval", { toolName: name, action, parameters: args });
  assert(!grant.isError, text(grant));
  return await call(s, name, { ...args, approvalToken: grant.structuredContent.approvalToken });
}

function result(reply) { assert(!reply.isError, text(reply)); return reply.structuredContent.result; }
function refused(reply, pattern) { assert(reply.isError, `Expected refusal: ${text(reply)}`); assert.match(text(reply), pattern); }

async function start(s, drawing) {
  let r;
  try { r = result(await approved(s, "civil3d_instance", { action: "start", drawing })); }
  catch (error) { uncertainStarts.set(s, Date.now() + CLAIM_WINDOW_MS + DISCOVERY_GRACE_MS); throw error; }
  started.set(s, r.instance);
  assert.equal(r.instance.owner, "agent");
  return r.instance;
}

async function run(s, code, mode = "read") {
  const r = result(await approved(s, "civil3d_run_code", { code, mode }));
  assert.equal(r.status, "ok", JSON.stringify(r));
  return r.result;
}

async function main() {
  assert(!process.env.CIVIL3D_PORT?.trim(), "Unset CIVIL3D_PORT before the multi-instance smoke; fixed routing disables discovery.");
  assert(!process.argv.includes("--orphans"), "--orphans is unsupported; no Civil 3D processes were launched.");
  const args = process.argv.slice(2);
  assert.equal(args.length, 2, "Supply exactly two disposable scratch DWG paths.");
  for (const path of args) {
    assert(isAbsolute(path) && /\.dwg$/i.test(path) && (await stat(path)).isFile(), "Supply absolute existing .dwg paths.");
  }
  const [a, b] = args.map(path => resolve(path));
  assert(!same(a, b), "Scratch DWGs must be different files.");
  temp = await mkdtemp(join(tmpdir(), "civil3d-multi-smoke-"));
  const c = capDrawing = join(temp, "cap-check.dwg");
  await copyFile(a, c);
  const s1 = await session("multi-smoke-A");
  const s2 = await session("multi-smoke-B");
  const before = result(await call(s1, "civil3d_instance", { action: "list" }));
  assert(!before.instances.some(i => i.documents.some(d => same(d, a) || same(d, b))), "Scratch DWGs are already open. Close them before this test.");
  assert.equal(before.instances.filter(i => i.owner === "agent").length, 0, "Stop existing agent instances before this test; user instances may stay open.");
  const users = before.instances.filter(i => i.owner === "user");
  const i1 = await start(s1, a);
  const i2 = await start(s2, b);
  assert.notEqual(i1.pid, i2.pid);
  assert.notEqual(i1.port, i2.port);
  assert.notEqual(i1.ownerSession, i2.ownerSession);
  const code = "return new { drawing = Doc.Name, documents = Application.DocumentManager.Cast<Autodesk.AutoCAD.ApplicationServices.Document>().Select(d => d.Name).ToArray() };";
  const r1 = await run(s1, code);
  const r2 = await run(s2, code);
  assert(same(r1.drawing, a) && r1.documents.some(d => same(d, a)) && !r1.documents.some(d => same(d, b)), "Session A reached the wrong drawing.");
  assert(same(r2.drawing, b) && r2.documents.some(d => same(d, b)) && !r2.documents.some(d => same(d, a)), "Session B reached the wrong drawing.");
  console.error("PASS independent session ports, tokens and run_code drawings");
  const s3 = await session("multi-smoke-cap", "2");
  const unattachedReply = await call(s3, "civil3d_drawing", { action: "info" });
  assert.equal(unattachedReply.errorCode, "CIVIL3D.NOT_ATTACHED", text(unattachedReply));
  let capReply;
  try { capReply = await approved(s3, "civil3d_instance", { action: "start", drawing: c }); }
  catch (error) { uncertainStarts.set(s3, Date.now() + CLAIM_WINDOW_MS + DISCOVERY_GRACE_MS); throw error; }
  if (!capReply.isError) started.set(s3, result(capReply).instance);
  else if (!/cap \(2\) reached/i.test(text(capReply))) {
    uncertainStarts.set(s3, Date.now() + CLAIM_WINDOW_MS + DISCOVERY_GRACE_MS);
  }
  refused(capReply, /cap \(2\) reached/i);
  console.error("PASS third start refused at explicit cap 2 (normal sessions use default cap 3)");
  const fixed = await session("multi-smoke-fixed", "3", i1.port);
  const fixedReply = await call(fixed, "civil3d_drawing", { action: "info" });
  assert.equal(fixedReply.errorCode, "CIVIL3D.NOT_ATTACHED", text(fixedReply));
  const fixedHealth = result(await call(fixed, "civil3d_health", {}));
  assert.equal(fixedHealth.routing, "fixed");
  assert.equal(fixedHealth.status, "not attached");
  console.error("PASS explicit CIVIL3D_PORT refuses another session's agent instance and health reports fixed routing");
  refused(await call(s1, "civil3d_instance", { action: "attach", pid: i2.pid }), /force: true/i);
  for (const user of users) {
    refused(await call(s3, "civil3d_instance", { action: "attach", pid: user.pid }), /userInstance: true/i);
    // No successful user attachment and no plugin operations against user pids.
  }
  if (users.length === 0) console.error("SKIPPED user attachment guard: no user instance is present");
  else console.error(`PASS attachment guards (${users.length} existing user instances checked)`);
  const writeCode = `if (System.Diagnostics.Process.GetCurrentProcess().Id != ${i1.pid}
    || !string.Equals(System.IO.Path.GetFullPath(Doc.Name), System.IO.Path.GetFullPath(${JSON.stringify(a)}), System.StringComparison.OrdinalIgnoreCase))
    throw new System.InvalidOperationException("Smoke target changed; no edit made.");
    var ms = Open<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), true);
    var line = new Line(Point3d.Origin, new Point3d(1, 1, 0));
    ms.AppendEntity(line); Tr.AddNewlyCreatedDBObject(line, true); return line.ObjectId;`;
  await run(s1, writeCode, "write");
  refused(await approved(s1, "civil3d_instance", { action: "stop" }), /unsaved/i);
  console.error("PASS stop refuses unsaved changes");
  for (const s of [s1, s2]) {
    result(await approved(s, "civil3d_instance", { action: "stop", discardChanges: true }));
    started.delete(s);
  }
  const after = result(await call(s3, "civil3d_instance", { action: "list" }));
  assert.equal(after.instances.filter(i => i.owner === "user").length, users.length,
    "User instance set changed.");
  for (const user of users) {
    const current = after.instances.find(i => i.pid === user.pid && i.owner === "user");
    assert(current, `User instance ${user.pid} disappeared.`);
    assert.equal(current.startedAtUtc, user.startedAtUtc, `User instance ${user.pid} was replaced.`);
    assert.deepEqual(current.documents.map(d => resolve(d).toLowerCase()).sort(),
      user.documents.map(d => resolve(d).toLowerCase()).sort(), `User instance ${user.pid} documents changed.`);
  }
  console.error("PASS both owned instances stopped; user instances untouched");
}

try { await main(); }
catch (error) {
  console.error(error);
  for (const s of sessions) s.printStderr();
  process.exitCode = 1;
} finally {
  // A start may claim a process and fail before discovery. Keep its owning
  // server alive until that claim expires, stopping any late discoveries.
  let preserveTemp = false;
  for (const s of sessions) {
    if (!s.sessionId) continue;
    const deadline = uncertainStarts.get(s) ?? 0;
    const failedStops = new Set();
    do {
      let instances;
      try { instances = result(await call(s, "civil3d_instance", { action: "list" })).instances; }
      catch (error) {
        console.error(`Cleanup could not list session ${s.sessionId}:`, error);
        if (capDrawing) preserveTemp = true;
        process.exitCode = 1;
        instances = started.has(s) ? [started.get(s)] : [];
      }
      for (const instance of instances.filter(i => i.owner === "agent" && i.ownerSession === s.sessionId && i.claimId
        && !failedStops.has(i.pid))) {
        try { result(await approved(s, "civil3d_instance", { action: "stop", pid: instance.pid, discardChanges: true })); }
        catch (error) {
          failedStops.add(instance.pid);
          console.error(`Cleanup failed for session-owned pid ${instance.pid} (${instance.startedAtUtc}):`, error);
          console.error(`UNRESOLVED session ${s.sessionId} pid ${instance.pid}, drawing(s) ${JSON.stringify(instance.documents)}`);
          if (instance.documents?.some(d => capDrawing && same(d, capDrawing))) preserveTemp = true;
          process.exitCode = 1;
        }
      }
      if (Date.now() >= deadline) break;
      await delay(Math.min(1000, deadline - Date.now()));
    } while (true);
    try {
      const remaining = result(await call(s, "civil3d_instance", { action: "list" })).instances
        .filter(i => i.owner === "agent" && i.ownerSession === s.sessionId && i.claimId);
      for (const i of remaining) {
        console.error(`UNRESOLVED session ${s.sessionId} pid ${i.pid}, drawing(s) ${JSON.stringify(i.documents)}`);
        if (i.documents?.some(d => capDrawing && same(d, capDrawing))) preserveTemp = true;
        process.exitCode = 1;
      }
    } catch (error) {
      console.error(`Cleanup could not verify session ${s.sessionId}:`, error);
      if (capDrawing) preserveTemp = true;
      process.exitCode = 1;
    }
  }
  for (const s of sessions) {
    try { await s.client.close(); } catch (error) { console.error(error); process.exitCode = 1; }
  }
  if (temp) {
    if (preserveTemp) console.error(`Preserved temporary drawing for unresolved process: ${capDrawing}`);
    else {
      try { await rm(temp, { recursive: true, force: true }); }
      catch (error) { console.error(error); process.exitCode = 1; }
    }
  }
}
