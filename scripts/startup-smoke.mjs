import { spawn } from "node:child_process";
import { drainServerStderr } from "./server-stderr.mjs";

const port = process.env.MCP_HTTP_PORT ?? "31999";
const child = spawn(process.execPath, ["./build/index.js"], {
  env: { ...process.env, MCP_HTTP_PORT: port, MCP_HTTP_HOST: "127.0.0.1" },
  stdio: ["ignore", "ignore", "pipe"],
});
const printServerStderr = drainServerStderr(child.stderr);

try {
  let response;
  for (let attempt = 0; attempt < 30; attempt += 1) {
    await new Promise((resolve) => setTimeout(resolve, 100));
    try {
      response = await fetch(`http://127.0.0.1:${port}/health`, {
        headers: process.env.MCP_HTTP_TOKEN
          ? { Authorization: `Bearer ${process.env.MCP_HTTP_TOKEN}` }
          : undefined,
      });
      if (response.ok) break;
    } catch { /* server is still starting */ }
  }
  if (!response?.ok) throw new Error("Server did not become healthy.");
  const body = await response.json();
  if (body.bridge !== "ok" || body.registeredTools < 1) throw new Error(`Unexpected liveness response: ${JSON.stringify(body)}`);
  console.log(`Startup smoke passed with ${body.registeredTools} registered tools.`);
} catch (error) {
  printServerStderr();
  throw error;
} finally {
  child.kill();
}
