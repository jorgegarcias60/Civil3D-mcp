import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { readTokenFileMock, sendCommandMock, withConnectionMock } = vi.hoisted(() => {
  const sendCommandMock = vi.fn();
  return {
    readTokenFileMock: vi.fn(),
    sendCommandMock,
    withConnectionMock: vi.fn(async (operation) => await operation({ sendCommand: sendCommandMock })),
  };
});

vi.mock("node:fs", async (importOriginal) => ({
  ...await importOriginal<typeof import("node:fs")>(),
  readFileSync: readTokenFileMock,
}));

async function loadRunCode(host: string | undefined, laterHost = host) {
  vi.stubEnv("CIVIL3D_HOST", host);
  // Keep the real startup host capture and normalization; only RPC is faked.
  const connection = await import("../src/utils/ConnectionManager.js");
  vi.spyOn(connection, "withApplicationConnection").mockImplementation(withConnectionMock);
  vi.stubEnv("CIVIL3D_HOST", laterHost);
  const auth = await import("../src/utils/pluginAuth.js");
  const readPluginTokenSpy = vi.spyOn(auth, "readPluginToken");
  const { CODE_DOMAIN_DEFINITION, RunCodeArgsSchema } = await import("../src/tools/domains/codeDomain.js");
  // document would trigger a health RPC if execution reached the connection.
  const args = RunCodeArgsSchema.parse({ action: "run", code: "return 1;", document: "Job.dwg" });
  return { execute: () => CODE_DOMAIN_DEFINITION.actions.run.execute(args), readPluginTokenSpy };
}

beforeEach(() => {
  vi.resetModules();
  vi.clearAllMocks();
  vi.stubEnv("CIVIL3D_RPC_TOKEN_FILE", "test-rpc-token");
  readTokenFileMock.mockReturnValue("test-session-token\r\n");
  sendCommandMock.mockImplementation(async (method: string) =>
    method === "getCivil3DHealth" ? { runCodeApi: 5 } : { ok: true });
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
  vi.resetModules();
});

describe("civil3d_run_code host restriction", () => {
  it.each([undefined, "127.0.0.1", "localhost", "LocalHost", " LOCALHOST ", "", "   "])(
    "allows CIVIL3D_HOST=%j after the existing mapping",
    async (host) => {
      const { execute, readPluginTokenSpy } = await loadRunCode(host);
      await expect(execute()).resolves.toMatchObject({ ok: true });
      expect(readPluginTokenSpy).toHaveBeenCalledOnce();
      expect(readTokenFileMock).toHaveBeenCalledExactlyOnceWith("test-rpc-token", "utf8");
      expect(sendCommandMock).toHaveBeenCalledWith("runCode", expect.objectContaining({ authToken: "test-session-token" }), expect.anything());
    },
  );

  it.each(["::1", "localhost.", "remote.example", "127.0.0.2", " 127.0.0.1 "])(
    "refuses CIVIL3D_HOST=%j before reading the token or calling the plugin",
    async (host) => {
      const { execute, readPluginTokenSpy } = await loadRunCode(host);
      const result = await execute().catch((error) => error);
      expect.soft(readPluginTokenSpy).not.toHaveBeenCalled();
      expect.soft(readTokenFileMock).not.toHaveBeenCalled();
      expect.soft(withConnectionMock).not.toHaveBeenCalled();
      expect.soft(sendCommandMock).not.toHaveBeenCalled();
      expect(result).toMatchObject({
        name: "Civil3DRpcError", code: "CIVIL3D.INVALID_INPUT", rpcCode: -32602,
        message: expect.stringMatching(/CIVIL3D_HOST.*127\.0\.0\.1.*Nothing ran\./),
      });
    },
  );

  it("keeps refusing the captured remote host if the environment later becomes local", async () => {
    const { execute, readPluginTokenSpy } = await loadRunCode("remote.example", "127.0.0.1");
    await expect(execute()).rejects.toMatchObject({ code: "CIVIL3D.INVALID_INPUT" });
    expect(readPluginTokenSpy).not.toHaveBeenCalled();
    expect(readTokenFileMock).not.toHaveBeenCalled();
    expect(withConnectionMock).not.toHaveBeenCalled();
    expect(sendCommandMock).not.toHaveBeenCalled();
  });

  it("keeps allowing the captured local host if the environment later becomes remote", async () => {
    const { execute } = await loadRunCode("localhost", "remote.example");
    await expect(execute()).resolves.toMatchObject({ ok: true });
    expect(sendCommandMock).toHaveBeenCalledWith("runCode", expect.objectContaining({ authToken: "test-session-token" }), expect.anything());
  });
});
