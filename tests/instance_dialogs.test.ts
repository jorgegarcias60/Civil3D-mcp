import { describe, expect, it, vi } from "vitest";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";
import { scanInstanceDialogs, titleIs, SHX_PROMPT, XREF_PROMPT } from "../src/utils/instanceDialogs.js";
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
      { pid: 42, startedAtUtc: "2026-10-15T12:00:00Z", reportOnly: false });
  });

  it("passes reportOnly to the scanner only when asked", async () => {
    vi.mocked(windowsScriptFile).mockClear();
    vi.mocked(windowsScriptFile).mockResolvedValue(JSON.stringify({ dialogs: [], missingXrefs: [] }));
    await scanInstanceDialogs(42, "2026-10-15T12:00:00Z", { reportOnly: true });
    await scanInstanceDialogs(42, "2026-10-15T12:00:00Z", {});
    expect(vi.mocked(windowsScriptFile).mock.calls.map(c => c[1])).toEqual([
      { pid: 42, startedAtUtc: "2026-10-15T12:00:00Z", reportOnly: true },
      { pid: 42, startedAtUtc: "2026-10-15T12:00:00Z", reportOnly: false },
    ]);
  });

  it("matches dialog titles trimmed and case-insensitively", () => {
    expect(titleIs({ title: "  missing shx files " }, SHX_PROMPT)).toBe(true);
    expect(titleIs({ title: "References - Not Found Files" }, XREF_PROMPT)).toBe(true);
    expect(titleIs({ title: "Missing SHX Files (2)" }, SHX_PROMPT)).toBe(false);
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

  it("answers only the two table prompts, only when not reportOnly, and verifies the matched title", async () => {
    const source = await readFile(fileURLToPath(new URL("../scripts/scan-instance-dialogs.ps1", import.meta.url)), "utf8");
    // One table: title -> the one Ignore button; missingXrefs belongs to the xref dialog only.
    const table = source.match(/\$knownPrompts = @\(([\s\S]*?)\r?\n\)/)?.[1] ?? "";
    expect(table.match(/title = '/g)).toHaveLength(2);
    expect(table).toMatch(/title = 'References - Not Found Files'; xrefs = \$true/);
    expect(table).toMatch(/title = 'Missing SHX Files'; xrefs = \$false/);
    expect(source).toMatch(/\$prompt\.xrefs/);
    expect(source).toMatch(/-ieq \$known\.title/);
    // Titles are not repeated outside the table (comments aside), so a third prompt cannot slip past the checks.
    const code = source.replace(table, "").split(/\r?\n/).filter(line => !line.trim().startsWith("#")).join("\n");
    expect(code).not.toMatch(/References - Not Found Files|Missing SHX Files/);
    // The C# click verifies against the title passed in, case-insensitively.
    expect(source).toMatch(/ClickButton\(long dialogHandle, long buttonHandle, int expectedPid, string expectedTitle\)/);
    expect(source).toMatch(/string\.Equals\(TextOf\(dialog\)\.Trim\(\), expectedTitle, StringComparison\.OrdinalIgnoreCase\)/);
    expect(source).toMatch(/\]::ClickButton\([^)]*\[string\]\$prompt\.title\)/);
    // reportOnly defaults to false and guards the only block that can invoke or click.
    expect(source).toMatch(/\$reportOnly = \$data\.reportOnly -eq \$true/);
    const guard = source.indexOf("if ($recognized -and -not $reportOnly) {");
    expect(guard).toBeGreaterThan(0);
    for (const action of [/\.Invoke\(\)/g, /\]::ClickButton\(/g, /Add-Type -TypeDefinition \$taskDialogSource/g]) {
      const hits = [...source.matchAll(action)].map(m => m.index!);
      expect(hits).toHaveLength(1);
      expect(hits[0]).toBeGreaterThan(guard);
    }
  });

  it.skipIf(process.platform !== "win32")("returns an empty scan for an exited process before loading UIA, with or without reportOnly", async () => {
    const path = fileURLToPath(new URL("../scripts/scan-instance-dialogs.ps1", import.meta.url));
    const scan = (reportOnly: boolean) => new Promise<string>((resolve, reject) => {
      const child = spawn("powershell.exe", ["-NoProfile", "-NonInteractive", "-File", path],
        { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
      let stdout = "";
      let stderr = "";
      child.stdout.on("data", chunk => { stdout += chunk; });
      child.stderr.on("data", chunk => { stderr += chunk; });
      child.on("error", reject);
      child.on("close", code => code === 0 ? resolve(stdout.trim()) : reject(new Error(stderr)));
      child.stdin.end(JSON.stringify({ pid: 2147483647, startedAtUtc: "2026-10-15T12:00:00Z", reportOnly }));
    });
    for (const reportOnly of [false, true]) {
      expect(JSON.parse(await scan(reportOnly))).toEqual({ dialogs: [], missingXrefs: [] });
    }
  });
});
