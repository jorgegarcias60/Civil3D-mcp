import { describe, expect, it } from "vitest";
import { DRAWING_RUNTIME_DOMAIN_DEFINITION } from "../src/tools/domains/drawingRuntimeDomain.js";

describe("drawing runtime response contracts", () => {
  it("accepts a standalone drawing with no Civil 3D project", () => {
    const responseSchema = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions.info.responseSchema;

    expect(responseSchema.safeParse({
      drawingName: "Drawing1.dwg",
      projectName: null,
      units: "feet",
    }).success).toBe(true);
  });

  it("accepts settings for a plain AutoCAD drawing where every default style is null", () => {
    // A drawing with no Civil 3D content has no styles to report, so
    // LookupUtils.GetFirstStyleName returns null for each entry.
    const responseSchema = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions.settings.responseSchema;

    expect(responseSchema.safeParse({
      coordinateSystem: null,
      coordinateZone: null,
      datum: null,
      scaleFactor: 1,
      elevationReference: null,
      defaultLayer: "0",
      defaultStyles: { surface: null, alignment: null, profile: null, corridor: null, pipeNetwork: null },
    }).success).toBe(true);
  });

  it("accepts settings for a styled Civil 3D drawing where corridor is null", () => {
    // DrawingCommands.cs reports defaultStyles.corridor as a hardcoded null,
    // so this holds even when every other style resolves to a name.
    const responseSchema = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions.settings.responseSchema;

    expect(responseSchema.safeParse({
      coordinateSystem: "USVA-NF",
      scaleFactor: 1,
      defaultLayer: "C-TOPO",
      defaultStyles: { surface: "Contours 2' and 10'", alignment: "Proposed", profile: "Design", corridor: null, pipeNetwork: "Storm" },
    }).success).toBe(true);
  });
});

describe("drawing save response (issue #54)", () => {
  const saveSchema = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions.save.responseSchema!;

  it("accepts the result of plugins older than the DBMOD report", () => {
    expect(saveSchema.parse({ saved: true, filePath: "C:\\jobs\\A.dwg" })).toEqual({ saved: true, filePath: "C:\\jobs\\A.dwg" });
  });

  it("keeps dbmodAfterSave, dbmodAfterCommand, warnings and extra fields", () => {
    const result = {
      saved: true,
      filePath: "C:\\jobs\\A.dwg",
      dbmodAfterSave: { value: 0, flags: [] },
      dbmodAfterCommand: { value: 48, flags: ["view", "fields"] },
      warnings: [
        "The file was written, but Civil 3D still counts the drawing as modified (DBMOD 48 (view, fields)): civil3d_instance stop refuses it without discardChanges.",
        "DBMOD changed after the save: 0 right after Database.SaveAs, 48 (view, fields) once the save command had ended.",
      ],
      extra: 1,
    };
    expect(saveSchema.parse(result)).toEqual(result);
    expect(saveSchema.parse({ saved: true, filePath: "A.dwg", dbmodAfterSave: { value: 0, flags: [] } }).dbmodAfterSave)
      .toEqual({ value: 0, flags: [] });
    expect(saveSchema.parse({ saved: true, filePath: "A.dwg", dbmodAfterCommand: { value: 32768, flags: ["bit 32768"] } }).dbmodAfterCommand)
      .toEqual({ value: 32768, flags: ["bit 32768"] });
  });

  it("accepts a DBMOD that could not be read", () => {
    const result = { saved: true, filePath: "A.dwg", dbmodAfterSave: null, dbmodAfterCommand: null, warnings: [
      "The file was written, but its DBMOD could not be read right after the save: x",
      "The file was written, but its DBMOD could not be read after the save command ended: y",
    ] };
    expect(saveSchema.parse(result)).toEqual(result);
  });

  it("rejects a malformed save result", () => {
    expect(saveSchema.safeParse({ filePath: "A.dwg" }).success).toBe(false);
    expect(saveSchema.safeParse({ saved: true }).success).toBe(false);
    expect(saveSchema.safeParse({ saved: true, filePath: "A.dwg", dbmodAfterSave: { value: -1, flags: [] } }).success).toBe(false);
    expect(saveSchema.safeParse({ saved: true, filePath: "A.dwg", dbmodAfterSave: { value: 1 } }).success).toBe(false);
    expect(saveSchema.safeParse({ saved: true, filePath: "A.dwg", dbmodAfterCommand: { value: -32768, flags: [] } }).success).toBe(false);
    expect(saveSchema.safeParse({ saved: true, filePath: "A.dwg", dbmodAfterCommand: { flags: [] } }).success).toBe(false);
    expect(saveSchema.safeParse({ saved: true, filePath: "A.dwg", warnings: "one" }).success).toBe(false);
  });
});
