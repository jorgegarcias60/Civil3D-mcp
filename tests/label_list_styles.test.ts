import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { beforeEach, describe, expect, it, vi } from "vitest";

const sendCommand = vi.fn();

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async (fn: (client: { sendCommand: typeof sendCommand }) => unknown) => await fn({ sendCommand }),
}));

const { STANDARDS_DOMAIN_DEFINITION } = await import("../src/tools/domains/standardsDomain.js");

const listStyles = STANDARDS_DOMAIN_DEFINITION.actions.label_list_styles;

describe("label_list_styles labelType", () => {
  beforeEach(() => {
    sendCommand.mockReset().mockResolvedValue({ styles: [] });
  });

  it("keeps labelType through the schema and forwards it to listLabelStyles", async () => {
    for (const labelType of ["curves", "station", "label_set"]) {
      const args = listStyles.inputSchema.parse({ action: "label_list_styles", objectType: "alignment", labelType });
      expect(args).toMatchObject({ labelType });
      await listStyles.execute(args as never);
      expect(sendCommand).toHaveBeenLastCalledWith("listLabelStyles", { objectType: "alignment", labelType });
    }
  });

  it("still accepts a call without labelType", async () => {
    const args = listStyles.inputSchema.parse({ action: "label_list_styles", objectType: "surface" });
    await listStyles.execute(args as never);
    expect(sendCommand).toHaveBeenLastCalledWith("listLabelStyles", { objectType: "surface", labelType: undefined });
  });

  it("passes labelType through civil3d_label list_styles and the canonical tool", () => {
    const label = STANDARDS_DOMAIN_DEFINITION.exposures.find((item) => item.toolName === "civil3d_label");
    expect(label!.inputShape).toHaveProperty("labelType");
    const resolved = label!.resolveAction({ action: "list_styles", objectType: "alignment", labelType: "curves" });
    expect(resolved.action).toBe("label_list_styles");
    expect(resolved.args).toEqual({ action: "label_list_styles", objectType: "alignment", labelType: "curves" });

    const standards = STANDARDS_DOMAIN_DEFINITION.exposures.find((item) => item.toolName === "civil3d_standards");
    expect(standards!.inputShape).toHaveProperty("labelType");
  });
});

// No AutoCAD-free harness reaches LabelCommands, so this reads the source (see
// label_commands_source.test.ts). The point is that label_list_styles and
// label_add resolve alignment style names from one helper and cannot drift.
const source = readFileSync(
  fileURLToPath(new URL("../Civil3D-MCP-Plugin/LabelCommands.cs", import.meta.url)),
  "utf8",
).replaceAll("\r\n", "\n");

describe("label_list_styles and label_add share the alignment style collections (LabelCommands.cs)", () => {
  it("lists curves and station styles from GetAlignmentLabelStyleCollection", () => {
    const list = source.slice(source.indexOf("public static Task<object?> ListLabelStylesAsync"), source.indexOf("public static Task<object?> ListLabelsAsync"));
    expect(list).toContain('PluginRuntime.GetOptionalString(parameters, "labelType")');
    expect(list).toContain('"curves" or "station" => CivilObjectUtils.ToObjectIds(GetAlignmentLabelStyleCollection(civilDoc, alignmentLabelType))');
    expect(list).toContain('"label_set" => GetLabelStyleCollection(civilDoc, objectType)');
    expect(list).toContain('result["labelType"] = alignmentLabelType');
  });

  it("resolves label_add curves and station names from the same helper", () => {
    expect(source).toContain('FindStyleInCollection(transaction, GetAlignmentLabelStyleCollection(civilDoc, "curves"), labelStyle,');
    expect(source).toContain('FindStyleInCollection(transaction, GetAlignmentLabelStyleCollection(civilDoc, "station"), labelStyle,');
    // The only place the two collections are named.
    expect(source.match(/\.CurveLabelStyles/g)).toHaveLength(1);
    expect(source.match(/\.StationOffsetLabelStyles/g)).toHaveLength(1);
  });
});
