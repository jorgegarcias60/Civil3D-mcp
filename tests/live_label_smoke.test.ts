import { readFileSync } from "node:fs";
import { win32 } from "node:path";
import { runInNewContext } from "node:vm";
import { describe, expect, it } from "vitest";

// scripts/live-label-smoke.mjs runs only against Civil 3D, so its source is
// checked here; scripts/live-agent-instance.mjs runs with a fake tool call.
const smoke = readFileSync(new URL("../scripts/live-label-smoke.mjs", import.meta.url), "utf8").replaceAll("\r\n", "\n");
const helperSource = readFileSync(new URL("../scripts/live-agent-instance.mjs", import.meta.url), "utf8")
  .replace(/^import .*;\r?$/gm, "").replace("export async function", "async function");
const attachAgent = runInNewContext(`(() => { ${helperSource}; return attachScratchAgentInstance; })()`, {
  basename: win32.basename, resolve: win32.resolve,
});

const A = "C:\\scratch\\A.dwg";
const B = "C:\\scratch\\B.dwg";

function fakeCall(instances: unknown[], calls: unknown[] = []) {
  return async (name: string, args: any) => {
    calls.push({ name, ...args });
    return { structuredContent: { result: { instances } } };
  };
}

describe("live label smoke (issue #57)", () => {
  it("labels a structure family that is not a null structure, and says which", () => {
    expect(smoke).toContain("if (Open<PartFamily>(id).PartType != PartType.StructNull) { structFamily = id; break; }");
    expect(smoke).toContain("structureFamily = structureFamily.Name");
  });

  it("fails a part profile label that comes back with warnings", () => {
    expect(smoke).toMatch(/r\.skipped === 0 && r\.warnings === undefined/);
  });

  it("never accepts a null structure's profile label silently away from its views", () => {
    expect(smoke).toContain('parts.Add((nul, "-NULL", true));');
    expect(smoke).toContain("null structure: each profile label sits in a view or is named in warnings");
    expect(smoke).toMatch(/l\.view === null && !\(nullStructure\.warnings \?\? \[\]\)\.some\(\(w\) => w\.includes\(l\.handle\)\)/);
    // The null structure's labels are kept out of the per-view structure list.
    expect(smoke).toContain("Where(l => !apart.Contains(l.Handle.ToString()))");
  });

  it("checks the one import in a drawing with a single alignment label set instead of failing", () => {
    expect(smoke).toContain("if (sets.length < 2) {");
    expect(smoke).toContain("groups.length === 1 && groups[0].labelGroups === groups[0].items");
  });

  it("runs against exactly one of the user instance or an agent instance", () => {
    expect(smoke).toContain('if (options["user-instance"] === options["agent-instance"])');
    expect(smoke).toContain('if (options["agent-instance"]) await attachScratchAgentInstance(call, expectDrawing);');
    expect(smoke).toContain("else await attachScratchUserInstance(call, expectDrawing);");
  });
});

describe("attachScratchAgentInstance", () => {
  it("attaches the one agent instance with the scratch drawing, with force and never as a user instance", async () => {
    const calls: unknown[] = [];
    const agent = { pid: 41, owner: "agent", ownerSession: "other", documents: [A] };
    const user = { pid: 7, owner: "user", documents: [A] };
    await expect(attachAgent(fakeCall([user, agent], calls), A.toLowerCase())).resolves.toMatchObject({ pid: 41 });
    expect(calls).toEqual([{ name: "civil3d_instance", action: "list" },
      { name: "civil3d_instance", action: "attach", pid: 41, force: true }]);
  });

  it("matches a complete drawing name case-insensitively, without prefix matches", async () => {
    const calls: unknown[] = [];
    const call = fakeCall([{ pid: 41, owner: "agent", documents: ["C:\\scratch\\LABEL-57.dwg"] }], calls);
    await expect(attachAgent(call, "LABEL-57")).rejects.toThrow(/exactly one agent instance/i);
    await expect(attachAgent(call, "label-57.DWG")).resolves.toMatchObject({ pid: 41 });
  });

  it.each([
    { instances: [] },
    { instances: [{ pid: 7, owner: "user", documents: [A] }] },
    { instances: [{ pid: 41, owner: "agent", documents: [B] }] },
    { instances: [{ pid: 41, owner: "agent", documents: [A] }, { pid: 42, owner: "agent", documents: [A] }] },
  ])("refuses a missing, user-owned, mismatching or ambiguous instance %#", async ({ instances }) => {
    const calls: any[] = [];
    await expect(attachAgent(fakeCall(instances, calls), A)).rejects.toThrow();
    expect(calls.map((c) => c.action)).toEqual(["list"]);
  });

  it("requires the scratch drawing", async () => {
    const calls: unknown[] = [];
    await expect(attachAgent(fakeCall([], calls), undefined)).rejects.toThrow(/--expect-drawing/);
    expect(calls).toEqual([]);
  });

  it("passes on a refused attach", async () => {
    const call = async (_name: string, args: any) => args.action === "list"
      ? { structuredContent: { result: { instances: [{ pid: 41, owner: "agent", documents: [A] }] } } }
      : { isError: true, content: [{ text: "Instance belongs to another agent session." }] };
    await expect(attachAgent(call, A)).rejects.toThrow(/another agent session/);
  });
});
