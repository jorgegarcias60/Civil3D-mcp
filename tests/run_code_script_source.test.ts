import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const source = readFileSync(
  fileURLToPath(new URL("../Civil3D-MCP-Plugin/RunCodeScript.cs", import.meta.url)),
  "utf8",
).replaceAll("\r\n", "\n");

describe("OpenSide retry ownership", () => {
  it("opens each attempt in a new Database and disposes a failed attempt", () => {
    expect(source).toContain("ReadSideOnce(fullPath, FileOpenMode.OpenForReadAndAllShare)");
    expect(source).toContain("ReadSideOnce(fullPath, FileOpenMode.OpenTryForReadShare)");
    const helper = source.slice(source.indexOf("private static Database ReadSideOnce("), source.indexOf("/// <summary>The run's transaction"));
    expect(helper).toContain("var side = new Database(false, true);");
    expect(helper).toContain("side.ReadDwgFile(path, mode, true, string.Empty);");
    expect(helper).toContain("side.CloseInput(true);");
    expect(helper).toMatch(/catch\s*\{\s*side\.Dispose\(\);\s*throw;/);
  });

  it("does not retry drawing-content failures and preserves the first error when both attempts fail", () => {
    expect(source).toContain("catch (Exception first) when (first is not RunCodeTimeoutException and not OperationCanceledException\n      && !RunCodeHelpers.IsDrawingContentError((first as AcRuntimeException)?.ErrorStatus.ToString()))");
    expect(source).toContain("ExceptionDispatchInfo.Capture(first).Throw()");
  });
});
