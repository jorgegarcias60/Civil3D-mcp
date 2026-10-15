import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { win32 } from "node:path";
import { expect, it } from "vitest";

const A = "C:\\scratch\\A.dwg";
const verifier = readFileSync(new URL("../scripts/verify-live-plugin.mjs", import.meta.url), "utf8")
  .replace(/^import .*;\r?$/gm, "").split("main().catch(")[0];
const helper = readFileSync(new URL("../scripts/live-user-instance.mjs", import.meta.url), "utf8")
  .replace(/^import .*;\r?$/gm, "").replace("export async function", "async function");

async function verify(options: Record<string, unknown>, users: any[], calls: string[] = []) {
  const output: string[] = [];
  const manager = {
    async list() { calls.push("list"); return { instances: users }; },
    async attach(args: any) { calls.push(`attach:${args.pid}:${args.userInstance}`); return { instance: users[0] }; },
  };
  const context = {
    parseArgs: () => ({ values: options }), instanceManager: manager,
    basename: win32.basename, resolve: win32.resolve,
    withApplicationConnection: async (fn: any) => {
      calls.push("plugin");
      return fn({ sendCommand: async () => ({ drawingLoaded: true }) });
    },
    getProjectContext: async () => { calls.push("context"); return {}; },
    createRequestId: () => "request", runWithRequestId: (_id: string, fn: any) => fn(),
    process: { stdout: { write: (value: string) => output.push(value) } },
  };
  const run = runInNewContext(`(async () => { ${helper}\n${verifier}\nawait main(); })()`, context);
  await run;
  return { calls, output };
}

it("attaches the selected scratch user before the first plugin call", async () => {
  const result = await verify({ "user-instance": true, "expect-drawing": A },
    [{ pid: 99, owner: "user", documents: [A] }]);
  expect(result.calls).toEqual(["list", "attach:99:true", "plugin", "context"]);
  expect(result.output).toHaveLength(1);
});

it.each([
  [{ "user-instance": false, "expect-drawing": A }, [{ pid: 99, owner: "user", documents: [A] }]],
  [{ "user-instance": true }, [{ pid: 99, owner: "user", documents: [A] }]],
  [{ "user-instance": true, "expect-drawing": A }, [{ pid: 99, owner: "user", documents: ["C:\\other.dwg"] }]],
  [{ "user-instance": true, "expect-drawing": A }, [{ pid: 99, owner: "user", documents: [A] },
    { pid: 100, owner: "user", documents: [A] }]],
] as const)("refuses unsafe verifier selection before plugin calls %#", async (options, users) => {
  const calls: string[] = [];
  await expect(verify(options, users, calls)).rejects.toThrow();
  expect(calls).not.toContain("plugin");
});

it("attaches an empty scratch user for a no-drawing check", async () => {
  const calls: string[] = [];
  await verify({ "user-instance": true, "no-drawing": true },
    [{ pid: 99, owner: "user", documents: [] }], calls);
  expect(calls.slice(0, 3)).toEqual(["list", "attach:99:true", "plugin"]);
});
