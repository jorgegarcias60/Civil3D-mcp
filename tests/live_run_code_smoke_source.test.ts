import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const source = readFileSync(
  fileURLToPath(new URL("../scripts/live-run-code-smoke.mjs", import.meta.url)),
  "utf8",
);

describe("preview smoke snapshots", () => {
  it("reads DBMOD from the target document while A remains active for a B read", () => {
    const snapshot = source.slice(
      source.indexOf("  const snapshotCode = ["),
      source.indexOf("  const previewCode = ["),
    );
    expect(snapshot).toContain("Doc.GetAcadDocument()).GetVariable(\\\"DBMOD\\\")");
    expect(snapshot).not.toContain("Application.GetSystemVariable");
    expect(source).toContain("const before = await runIn({ code: snapshotCode, args });");
    expect(source).toContain("const after = await runIn({ code: snapshotCode, args });");
    expect(source).toContain("const targets = [undefined, ...(options.other ? [resolve(options.other)] : [])]");
  });
});

describe("helper viewport smoke", () => {
  it("probes a dormant saved layout before helper writes or layout switches", () => {
    const probe = source.slice(
      source.indexOf("  dormantViewport: String.raw`"),
      source.indexOf("  textSetup: String.raw`"),
    );
    expect(probe).toContain("OpenSide(path)");
    expect(probe).toContain("openViewports.Count != 0");
    expect(probe).toContain("OfType<Viewport>()");
    expect(probe).toContain("saved.GetViewports()");
    expect(probe).toContain("matching.Handle.ToString()");
    expect(probe).toContain("overall.Handle.ToString()");
    expect(source.indexOf('execute("dormantViewport", {}, "read")')).toBeLessThan(
      source.indexOf('execute("textSetup")'),
    );
    expect(source.indexOf("if (options.helpers) await helperChecks(runCodeApi);")).toBeLessThan(
      source.indexOf("  if (options.other) {", source.indexOf("  drawingName = info.result")),
    );
  });

  it("checks the dormant refusal and non-current edit response", () => {
    expect(source).toContain('skip("helpers: dormant layout overall viewport", fixture.skip)');
    expect(source).toContain('execute("viewportOverall", args)');
    expect(source).toContain('refused.status === "runtime_error" && refused.committed === false && refused.activatedLayout === true');
    expect(source).toContain('before.current === fixture.original && after.current === fixture.original');
    expect(source).toContain('JSON.stringify(before) === JSON.stringify(after)');
    expect(source).toContain('changedResponse.activatedLayout === true');
  });
});
