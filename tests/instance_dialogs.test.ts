import { describe, expect, it, vi } from "vitest";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";
import { scanInstanceDialogs } from "../src/utils/instanceDialogs.js";
import { windowsScriptFile } from "../src/utils/instanceLauncher.js";

vi.mock("../src/utils/instanceLauncher.js", () => ({ windowsScriptFile: vi.fn() }));

describe("instance dialog scanner", () => {
  it("rejects invalid process identity before opening the helper", async () => {
    await expect(scanInstanceDialogs(0, "2026-10-15T12:00:00Z")).rejects.toThrow(/process ID/);
    await expect(scanInstanceDialogs(42, "invalid")).rejects.toThrow(/start time/);
    expect(windowsScriptFile).not.toHaveBeenCalled();
  });

  it("passes the exact process proof and preserves dialog diagnostics", async () => {
    const response = { dialogs: [{ title: "References - Not Found Files", className: "#32770",
      buttons: ["Ignore"], action: "ignore_unavailable", issues: ["Ignore button has no InvokePattern."] }],
      missingXrefs: ["C:\\missing.dwg"] };
    vi.mocked(windowsScriptFile).mockResolvedValueOnce(JSON.stringify(response));
    expect(await scanInstanceDialogs(42, "2026-10-15T12:00:00Z")).toEqual(response);
    expect(windowsScriptFile).toHaveBeenCalledWith(expect.any(String),
      { pid: 42, startedAtUtc: "2026-10-15T12:00:00Z" });
  });

  it("keeps automation limited to the documented UIA action", async () => {
    const source = await readFile(fileURLToPath(new URL("../scripts/scan-instance-dialogs.ps1", import.meta.url)), "utf8");
    expect(source.match(/Test-ProcessIdentity/g)?.length).toBeGreaterThanOrEqual(3);
    expect(source).toMatch(/References - Not Found Files/);
    expect(source).toMatch(/\^Ignore/);
    expect(source).toMatch(/InvokePattern/);
    // Fallback for task dialog command links without a UIA pattern: BN_CLICKED to the button's own host, then BM_CLICK
    // to that button, both after process, title, class and button-text checks.
    expect(source).toMatch(/CtrlNotifySink/);
    expect(source).toMatch(/BN_CLICKED/);
    expect(source).toMatch(/GetWindowThreadProcessId/);
    // Windows are found with Win32 enumeration of the launched pid only; UIA never walks other processes.
    expect(source).toMatch(/EnumWindows/);
    expect(source).not.toMatch(/RootElement/);
    expect(source).not.toMatch(/SendKeys|SetFocus|Click\(|mouse_event|keybd_event|SendInput|SetForegroundWindow|SetActiveWindow|PostMessage|SetCursorPos/i);
  });

  it.skipIf(process.platform !== "win32")("returns an empty scan for an exited process before loading UIA", async () => {
    const path = fileURLToPath(new URL("../scripts/scan-instance-dialogs.ps1", import.meta.url));
    const output = await new Promise<string>((resolve, reject) => {
      const child = spawn("powershell.exe", ["-NoProfile", "-NonInteractive", "-File", path],
        { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
      let stdout = "";
      let stderr = "";
      child.stdout.on("data", chunk => { stdout += chunk; });
      child.stderr.on("data", chunk => { stderr += chunk; });
      child.on("error", reject);
      child.on("close", code => code === 0 ? resolve(stdout.trim()) : reject(new Error(stderr)));
      child.stdin.end(JSON.stringify({ pid: 2147483647, startedAtUtc: "2026-10-15T12:00:00Z" }));
    });
    expect(JSON.parse(output)).toEqual({ dialogs: [], missingXrefs: [] });
  });
});
