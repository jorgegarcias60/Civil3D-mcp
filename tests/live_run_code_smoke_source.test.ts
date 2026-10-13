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
