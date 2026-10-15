import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import assert from "node:assert/strict";
import { win32 } from "node:path";
import { expect, it } from "vitest";

const A = "C:\\scratch\\A.dwg";
const B = "C:\\scratch\\B.dwg";
const source = readFileSync(new URL("../scripts/live-multi-instance-smoke.mjs", import.meta.url), "utf8")
  .replace(/^import .*;\r?$/gm, "");

async function smoke(options: { thirdSucceeds?: boolean; userChange?: "documents" | "startedAtUtc" | "added";
  failedStartClaim?: boolean; lateStartClaim?: boolean; capLateStartClaim?: boolean;
  lateDiscoveryAt?: number; failedReplyDelayMs?: number; refuseCleanup?: boolean; noUser?: boolean;
  fixedPort?: boolean; orphans?: boolean; orphan?: boolean } = {}) {
  const stopped: number[] = [];
  const closed: number[] = [];
  const errors: string[] = [];
  const writes: string[] = [];
  const removed: string[] = [];
  const events: string[] = [];
  const killed: number[] = [];
  const copies: string[] = [];
  const instances = new Map<number, any>();
  let nextSession = 0;
  let afterStops = false;
  let now = 0;
  let lateInstance: any;
  const user = { pid: 99, owner: "user", startedAtUtc: "2026-01-01T00:00:00Z",
    documents: ["C:\\user\\design.dwg"] };
  const ok = (result: any) => ({ structuredContent: { result } });
  const error = (message: string) => ({ isError: true, content: [{ text: message }] });
  class Client {
    id = ++nextSession;
    label: string;
    constructor({ name }: { name: string }) { this.label = name; }
    async connect() {}
    async close() { closed.push(this.id); events.push(`close:${this.id}`); }
    async callTool({ name, arguments: args }: any) {
      if (name === "civil3d_request_approval") return { structuredContent: { approvalToken: "test" } };
      if (name === "civil3d_run_code") {
        if (args.mode === "write") {
          writes.push(args.code);
          return ok({ status: "ok", result: "line" });
        }
        const drawing = this.id === 1 ? A : this.id === 2 ? B : win32.join("C:\\temp\\smoke", "orphan-check.dwg");
        return ok({ status: "ok", result: { drawing, documents: [drawing] } });
      }
      if (name === "civil3d_drawing" && args.action === "info") {
        return { isError: true, errorCode: "CIVIL3D.NOT_ATTACHED", content: [{ text: "Select an instance." }] };
      }
      if (name === "civil3d_health") return ok({ routing: "fixed", status: "not attached" });
      if (name !== "civil3d_instance") throw new Error(`Unexpected tool ${name}`);
      if (args.action === "list") {
        if (lateInstance && now >= (options.lateDiscoveryAt ?? 4000)) {
          instances.set(lateInstance.pid, lateInstance); lateInstance = undefined;
        }
        const currentUser = { ...user, documents: [...user.documents] };
        if (afterStops && options.userChange === "documents") currentUser.documents.push("C:\\user\\other.dwg");
        if (afterStops && options.userChange === "startedAtUtc") currentUser.startedAtUtc = "2026-01-02T00:00:00Z";
        const added = afterStops && options.userChange === "added"
          ? [{ ...currentUser, pid: 100 }] : [];
        return ok({ sessionId: `s${this.id}`, instances: [...(options.noUser ? [] : [currentUser]), ...added,
          ...[...instances.values()].map(i => ({ ...i, attachedToThisSession: i.ownerSession === `s${this.id}` }))] });
      }
      if (args.action === "start") {
        if (this.id === 6 && options.orphan) return error("Agent instance cap (1) reached");
        if (this.id === 3 && !options.thirdSucceeds && !options.capLateStartClaim) return error("Agent instance cap (2) reached");
        const instance = { pid: this.id, port: 8756 + this.id, owner: "agent", ownerSession: `s${this.id}`,
          claimId: `claim${this.id}`, startedAtUtc: `2026-01-0${this.id}T00:00:00Z`, documents: [args.drawing],
          ownerLabel: this.label, orphan: false, responsive: true };
        if ((this.id === 1 && options.lateStartClaim) || (this.id === 3 && options.capLateStartClaim)) lateInstance = instance;
        else instances.set(instance.pid, instance);
        if ((this.id === 1 && options.lateStartClaim) || (this.id === 3 && options.capLateStartClaim)) {
          now += options.failedReplyDelayMs ?? 0;
          return error("Civil 3D start timed out before discovery");
        }
        if (this.id === 1 && options.failedStartClaim) return error("Civil 3D start timed out after claim");
        return ok({ instance });
      }
      if (args.action === "attach") {
        if (args.pid === 99) return error("userInstance: true required");
        const found = instances.get(args.pid);
        if (found?.orphan) {
          if (this.label !== found.ownerLabel) return error("adoptOrphan: true required");
          found.ownerSession = `s${this.id}`;
          found.orphan = false;
          events.push(`adopt:${args.pid}:${this.id}`);
          return ok({ instance: found, adopted: true });
        }
        return error("force: true required");
      }
      if (args.action === "stop") {
        if (!args.discardChanges) return error("unsaved changes");
        if (options.refuseCleanup) return error("instance not responsive");
        const pid = args.pid ?? this.id;
        stopped.push(pid);
        events.push(`stop:${pid}`);
        instances.delete(pid);
        if (pid === 2) afterStops = true;
        return ok({ stopped: true });
      }
      throw new Error(`Unexpected action ${args.action}`);
    }
  }
  const processMock = { argv: ["node", "smoke", A, B, ...(options.orphans ? ["--orphans"] : []),
      ...(options.orphan ? ["--orphan"] : [])],
    env: options.fixedPort ? { CIVIL3D_PORT: "8757" } : { LOCALAPPDATA: "C:\\local" },
    execPath: "node", cwd: () => ".", exitCode: 0,
    kill: (pid: number) => {
      killed.push(pid);
      const found = instances.get(pid - 1000);
      if (found) found.orphan = true;
      events.push(`kill:${pid}`);
    } };
  await runInNewContext(`(async () => { ${source}\n })()`, {
    assert, Client, StdioClientTransport: class { stderr = null; pid = nextSession + 1001; }, drainServerStderr: () => () => {},
    copyFile: async (_from: string, to: string) => { copies.push(to); },
    mkdtemp: async () => "C:\\temp\\smoke", rm: async (path: string) => { removed.push(path); },
    stat: async (path: string) => {
      if (options.orphan && /\\instances\\5\.json$/i.test(path) && !instances.has(5)) {
        const error: any = new Error("not found"); error.code = "ENOENT"; throw error;
      }
      return { isFile: () => true };
    },
    homedir: () => "C:\\home", tmpdir: () => "C:\\temp",
    Date: { now: () => now }, delay: async (ms: number) => { now += ms; },
    isAbsolute: win32.isAbsolute, join: win32.join, resolve: win32.resolve,
    process: processMock, console: { error: (...args: any[]) => errors.push(args.map(String).join(" ")) },
  });
  return { stopped, closed, events, killed, copies, errors: errors.join("\n"), exitCode: processMock.exitCode, writes, removed, now };
}

it("passes when user processes and drawings are unchanged", async () => {
  const run = await smoke();
  expect(run.exitCode).toBe(0);
  expect(run.closed).toEqual([1, 2, 3, 4]);
  expect(run.errors).toContain("PASS explicit CIVIL3D_PORT");
  expect(run.stopped).toEqual([1, 2]);
  expect(run.writes).toHaveLength(1);
  const code = run.writes[0];
  expect(code.indexOf("GetCurrentProcess().Id != 1")).toBeGreaterThanOrEqual(0);
  expect(code.indexOf("Doc.Name")).toBeGreaterThanOrEqual(0);
  expect(code.indexOf("GetCurrentProcess().Id != 1")).toBeLessThan(code.indexOf("var ms"));
  expect(code.indexOf("Doc.Name")).toBeLessThan(code.indexOf("var ms"));
});

it("reports the user guard as skipped when no user instance exists", async () => {
  const run = await smoke({ noUser: true });
  expect(run.exitCode).toBe(0);
  expect(run.errors).toMatch(/SKIPPED user attachment guard/);
});

it("refuses fixed-port mode before creating sessions or scratch files", async () => {
  const run = await smoke({ fixedPort: true });
  expect(run.exitCode).toBe(1);
  expect(run.errors).toMatch(/Unset CIVIL3D_PORT/);
  expect(run.closed).toEqual([]);
  expect(run.removed).toEqual([]);
});

it("refuses unsupported orphan mode before creating sessions or scratch files", async () => {
  const run = await smoke({ orphans: true });
  expect(run.exitCode).toBe(1);
  expect(run.errors).toMatch(/--orphans is unsupported/);
  expect(run.closed).toEqual([]);
  expect(run.removed).toEqual([]);
});

it("runs opt-in orphan recovery through a live same-label server", async () => {
  const run = await smoke({ orphan: true });
  expect(run.exitCode).toBe(0);
  expect(run.copies).toEqual(["C:\\temp\\smoke\\cap-check.dwg", "C:\\temp\\smoke\\orphan-check.dwg"]);
  expect(run.killed).toEqual([1005]);
  expect(run.events).toContain("adopt:5:7");
  expect(run.events.indexOf("kill:1005")).toBeLessThan(run.events.indexOf("adopt:5:7"));
  expect(run.events.indexOf("adopt:5:7")).toBeLessThan(run.events.indexOf("stop:5"));
  expect(run.stopped).toEqual([1, 2, 5]);
  expect(run.closed).toEqual([1, 2, 3, 4, 6, 7]);
  expect(run.errors).toContain("PASS orphan discovery, cross-label refusal, cap 1, same-label readoption and discovery cleanup");
  expect(run.removed).toContain("C:\\temp\\smoke");
});

it("cleans up an unexpectedly successful third start before closing sessions", async () => {
  const run = await smoke({ thirdSucceeds: true });
  expect(run.exitCode).toBe(1);
  expect(run.stopped).toEqual([1, 2, 3]);
  expect(run.closed).toEqual([1, 2, 3]);
});

it.each(["documents", "startedAtUtc", "added"] as const)("fails when user %s changes", async userChange => {
  const run = await smoke({ userChange });
  expect(run.exitCode).toBe(1);
  expect(run.errors).toMatch(userChange === "documents" ? /documents changed/
    : userChange === "startedAtUtc" ? /was replaced/ : /User instance set changed/);
  expect(run.writes).toHaveLength(1);
  expect(run.writes[0]).toContain("GetCurrentProcess().Id != 1");
  expect(run.writes[0]).toContain("Doc.Name");
  expect(run.writes[0]).toContain("Smoke target changed; no edit made.");
});

it("discovers and stops a claimed process even when start returned an error", async () => {
  const run = await smoke({ failedStartClaim: true });
  expect(run.exitCode).toBe(1);
  expect(run.stopped).toEqual([1]);
  expect(run.closed).toEqual([1, 2]);
});

it("keeps the owner server alive until claim expiry and stops a late discovery", async () => {
  const run = await smoke({ lateStartClaim: true, failedReplyDelayMs: 10_000, lateDiscoveryAt: 341_000 });
  expect(run.exitCode).toBe(1);
  expect(run.stopped).toEqual([1]);
  expect(run.closed).toEqual([1, 2]);
  expect(run.now).toBeGreaterThanOrEqual(342_000);
  expect(run.events.indexOf("stop:1")).toBeLessThan(run.events.indexOf("close:1"));
});

it("keeps the cap-check owner alive after an unexpected failed start and stops late discovery", async () => {
  const run = await smoke({ capLateStartClaim: true, lateDiscoveryAt: 331_000 });
  expect(run.exitCode).toBe(1);
  expect(run.stopped).toEqual([1, 2, 3]);
  expect(run.now).toBeGreaterThanOrEqual(332_000);
  expect(run.events.indexOf("stop:3")).toBeLessThan(run.events.indexOf("close:3"));
});

it("reports a claimed process that cleanup cannot stop", async () => {
  const run = await smoke({ failedStartClaim: true, refuseCleanup: true });
  expect(run.exitCode).toBe(1);
  expect(run.errors).toMatch(/UNRESOLVED session s1 pid 1/);
});

it("retains the cap-check drawing when its process cannot be stopped", async () => {
  const run = await smoke({ thirdSucceeds: true, refuseCleanup: true });
  expect(run.exitCode).toBe(1);
  expect(run.errors).toMatch(/Preserved temporary drawings for unresolved process: C:\\temp\\smoke/);
  expect(run.removed).not.toContain("C:\\temp\\smoke");
});

const userHelperSource = readFileSync(new URL("../scripts/live-user-instance.mjs", import.meta.url), "utf8")
  .replace(/^import .*;\r?$/gm, "").replace("export async function", "async function");
const attachUser = runInNewContext(`(() => { ${userHelperSource}; return attachScratchUserInstance; })()`, {
  basename: win32.basename, resolve: win32.resolve,
});

it("attaches a unique user instance with the exact scratch path", async () => {
  const calls: any[] = [];
  const user = { pid: 99, owner: "user", documents: [A] };
  const call = async (name: string, args: any) => {
    calls.push({ name, ...args });
    return { structuredContent: { result: { instances: [user] } } };
  };
  await attachUser(call, A.toLowerCase());
  expect(calls).toEqual([{ name: "civil3d_instance", action: "list" },
    { name: "civil3d_instance", action: "attach", pid: 99, userInstance: true }]);
});

it("matches complete drawing basenames case-insensitively without prefix matches", async () => {
  const calls: string[] = [];
  const call = async (_name: string, args: any) => {
    calls.push(args.action);
    return { structuredContent: { result: { instances: [{ pid: 99, owner: "user",
      documents: ["C:\\scratch\\RUNCODE-SMOKE.dwg"] }] } } };
  };
  await expect(attachUser(call, "RUNCODE-SMOKE")).rejects.toThrow(/expected scratch drawing/i);
  expect(calls).toEqual(["list"]);
  await expect(attachUser(call, "runcode-smoke.DWG")).resolves.toMatchObject({ pid: 99 });
  expect(calls).toEqual(["list", "list", "attach"]);
});

it.each([{ instances: [] }, { instances: [{ pid: 99, owner: "user", documents: [B] }] },
  { instances: [{ pid: 99, owner: "user", documents: [A] }, { pid: 100, owner: "user", documents: [A] }] }])(
  "refuses ambiguous or mismatching scratch user selection %#", async ({ instances }) => {
    const calls: string[] = [];
    const call = async (_name: string, args: any) => {
      calls.push(args.action);
      return { structuredContent: { result: { instances } } };
    };
    await expect(attachUser(call, A)).rejects.toThrow();
    expect(calls).toEqual(["list"]);
  });

it("requires an empty user process for explicit no-drawing checks", async () => {
  let documents: string[] = [A];
  const call = async () => ({ structuredContent: { result: { instances: [{ pid: 99, owner: "user", documents }] } } });
  await expect(attachUser(call, undefined, { noDrawing: true })).rejects.toThrow();
  documents = [];
  await expect(attachUser(call, undefined, { noDrawing: true })).resolves.toMatchObject({ pid: 99 });
});
