import { basename, resolve } from "node:path";

const replyText = reply => (reply.content ?? []).map(item => item.text ?? "").join("\n");
const sameDrawing = (actual, expected) => expected.includes("\\") || expected.includes("/")
  ? resolve(actual).toLowerCase() === resolve(expected).toLowerCase()
  : basename(actual).toLowerCase() === expected.toLowerCase();

// A smoke test may use an agent instance that another MCP session started (for
// example with civil3d_instance start on a disposable drawing). This script's
// server is a different session, so it attaches with force: true: ownership is
// unchanged and this session cannot stop the instance. A user instance is never
// selected, and the disposable drawing must be open in exactly one agent instance.
export async function attachScratchAgentInstance(call, expectedDrawing) {
  if (!expectedDrawing?.trim()) throw new Error("--agent-instance requires --expect-drawing <scratch drawing name or absolute path>.");
  const listed = await call("civil3d_instance", { action: "list" });
  if (listed.isError) throw new Error(replyText(listed));
  const instances = listed.structuredContent?.result?.instances;
  if (!Array.isArray(instances)) throw new Error("Instance list is missing.");
  const agents = instances.filter(instance => instance.owner === "agent"
    && instance.documents?.some(drawing => sameDrawing(drawing, expectedDrawing)));
  if (agents.length !== 1) throw new Error(`Expected exactly one agent instance with the scratch drawing open; found ${agents.length}.`);
  const [agent] = agents;
  const attached = await call("civil3d_instance", { action: "attach", pid: agent.pid, force: true });
  if (attached.isError) throw new Error(replyText(attached));
  return agent;
}
