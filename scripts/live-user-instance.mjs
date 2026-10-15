import { basename, resolve } from "node:path";

const replyText = reply => (reply.content ?? []).map(item => item.text ?? "").join("\n");
const sameDrawing = (actual, expected) => expected.includes("\\") || expected.includes("/")
  ? resolve(actual).toLowerCase() === resolve(expected).toLowerCase()
  : basename(actual).toLowerCase() === expected.toLowerCase();

// A smoke test may use an existing user process only after an explicit opt-in
// and an unambiguous match to the disposable drawing named by the caller.
export async function attachScratchUserInstance(call, expectedDrawing, { noDrawing = false } = {}) {
  if (!noDrawing && !expectedDrawing?.trim()) throw new Error("--user-instance requires --expect-drawing <scratch drawing name or absolute path>.");
  const listed = await call("civil3d_instance", { action: "list" });
  if (listed.isError) throw new Error(replyText(listed));
  const instances = listed.structuredContent?.result?.instances;
  if (!Array.isArray(instances)) throw new Error("Instance list is missing.");
  const users = instances.filter(instance => instance.owner === "user");
  if (users.length !== 1) throw new Error(`Expected exactly one user instance; found ${users.length}.`);
  const [user] = users;
  if (noDrawing ? user.documents?.length !== 0 : !user.documents?.some(drawing => sameDrawing(drawing, expectedDrawing))) {
    throw new Error(`User instance ${user.pid} does not contain the expected scratch drawing.`);
  }
  const attached = await call("civil3d_instance", { action: "attach", pid: user.pid, userInstance: true });
  if (attached.isError) throw new Error(replyText(attached));
  return user;
}
