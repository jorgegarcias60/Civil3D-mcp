import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { sendCommandMock } = vi.hoisted(() => ({
  sendCommandMock: vi.fn(),
}));

vi.mock("../src/utils/ConnectionManager.js", () => ({
  withApplicationConnection: async <T>(
    operation: (client: { sendCommand: typeof sendCommandMock }) => Promise<T>,
  ) => await operation({
    sendCommand: sendCommandMock,
  }),
}));

import {
  PLOT_DOMAIN_DEFINITION,
  PlotLayoutsToPdfArgsSchema,
  PublishSheetSetArgsSchema,
} from "../src/tools/domains/plotDomain.js";
import { JOB_DOMAIN_DEFINITION } from "../src/tools/domains/jobDomain.js";
import { isApprovalRequired } from "../src/tools/approvalPolicy.js";
import { GENERATED_TOOL_CATALOG_ENTRIES, findManifestAction } from "../src/tools/toolManifest.js";

const pluginSource = (fileName: string) =>
  readFileSync(new URL(`../Civil3D-MCP-Plugin/${fileName}`, import.meta.url), "utf8");

// C# source with comments and string/char literals removed, so guards match
// code only (the file documents why it avoids PlotEngine in comments). A small
// lexer rather than regexes: it handles verbatim ("" escapes, newlines), raw
// and interpolated strings (hole expressions are kept as code), char literals
// such as '"', and comment markers inside strings.
function stripCSharpCommentsAndStrings(source: string): string {
  let i = 0;
  const lexCode = (untilCloseBrace: boolean): string => {
    let out = "";
    let depth = 0;
    while (i < source.length) {
      const c = source[i];
      const next = source[i + 1];
      if (c === "/" && next === "/") {
        while (i < source.length && source[i] !== "\n") i++;
        out += " ";
      } else if (c === "/" && next === "*") {
        const end = source.indexOf("*/", i + 2);
        i = end < 0 ? source.length : end + 2;
        out += " ";
      } else if (c === "'") {
        i++;
        while (i < source.length && source[i] !== "'") i += source[i] === "\\" ? 2 : 1;
        i++;
        out += "''";
      } else if (c === '"' || ((c === "@" || c === "$") && /^[@$]{0,2}"/.test(source.slice(i, i + 3)))) {
        out += '""' + lexString();
      } else if (untilCloseBrace && c === "{") {
        depth++;
        out += c;
        i++;
      } else if (untilCloseBrace && c === "}") {
        if (depth === 0) return out;
        depth--;
        out += c;
        i++;
      } else {
        out += c;
        i++;
      }
    }
    return out;
  };
  // Skips a string literal and returns the code of its interpolation holes.
  const lexString = (): string => {
    let holes = "";
    let verbatim = false;
    let interpolated = 0;
    while (source[i] === "@" || source[i] === "$") {
      if (source[i] === "@") verbatim = true;
      else interpolated++;
      i++;
    }
    let quotes = 0;
    while (source[i + quotes] === '"') quotes++;
    if (quotes >= 3) {
      // Raw string literal: ends at the same run of quotes.
      i += quotes;
      const fence = '"'.repeat(quotes);
      while (i < source.length && !source.startsWith(fence, i)) {
        if (interpolated > 0 && source.startsWith("{".repeat(interpolated), i)) {
          i += interpolated;
          holes += " " + lexCode(true);
          i++;
        } else {
          i++;
        }
      }
      i += quotes;
      return holes;
    }
    i++;
    while (i < source.length) {
      const c = source[i];
      if (c === '"') {
        if (verbatim && source[i + 1] === '"') {
          i += 2;
          continue;
        }
        i++;
        return holes;
      }
      if (!verbatim && c === "\\") {
        i += 2;
      } else if (interpolated > 0 && c === "{") {
        if (source[i + 1] === "{") {
          i += 2;
        } else {
          i++;
          holes += " " + lexCode(true);
          i++;
        }
      } else if (!verbatim && c === "\n") {
        return holes;
      } else {
        i++;
      }
    }
    return holes;
  };
  return lexCode(false);
}

const pluginCode = (fileName: string) => stripCSharpCommentsAndStrings(pluginSource(fileName));

function approvalTargetFor(action: string) {
  const match = findManifestAction("civil3d_plot", action);
  expect(match, `civil3d_plot action ${action} is registered`).toBeDefined();
  return {
    toolName: "civil3d_plot",
    action,
    capabilities: match!.actionDefinition.capabilities,
    safeForRetry: match!.actionDefinition.safeForRetry,
    requiresActiveDrawing: match!.actionDefinition.requiresActiveDrawing,
  };
}

describe("civil3d_plot schemas", () => {
  it("preserves explicit layout initialization flags and accepts legacy discovery", () => {
    const schema = PLOT_DOMAIN_DEFINITION.actions.list_layouts.responseSchema!;
    const layout = {
      name: "C-101", tabOrder: 1, isModel: false, isCurrent: false,
      pageSetup: null, handle: "10", device: "DWG To PDF.pc3", canonicalMediaName: "A4",
      paperUnits: "Millimeters", paperWidth: 210, paperHeight: 297,
      orientation: "portrait", rotation: "Degrees000", plotType: "Layout", plotStyleTable: "monochrome.ctb",
    };
    for (const initialized of [false, true]) {
      const parsed = schema.parse({ drawing: "scratch.dwg", currentLayout: null, count: 1,
        layouts: [{ ...layout, initialized }] });
      expect(parsed.layouts[0].initialized).toBe(initialized);
    }
    expect(schema.safeParse({ drawing: "scratch.dwg", currentLayout: null, count: 1,
      layouts: [layout] }).success).toBe(true);
    expect(schema.safeParse({ drawing: "scratch.dwg", currentLayout: null, count: 1,
      layouts: [{ ...layout, initialized: "true" }] }).success).toBe(false);
  });

  it("selects two initialized smoke layouts even after a configured uninitialized tab", () => {
    const source = readFileSync(new URL("../scripts/live-plot-smoke.mjs", import.meta.url), "utf8");
    const expression = source.match(/const selected = (discovery\.layouts\.filter\([\s\S]*?\.slice\(0, 2\));/);
    expect(expression).not.toBeNull();
    const configured = { isModel: false, ctbFound: true, mediaAvailable: true,
      plotStyleTable: "monochrome.ctb", paperWidth: 210, paperHeight: 297 };
    const selected = runInNewContext(expression![1], { discovery: { layouts: [
      { ...configured, name: "Never opened", initialized: false },
      { ...configured, name: "Legacy plugin" },
      { ...configured, name: "C-101", initialized: true },
      { ...configured, name: "C-102", initialized: true },
    ] } });
    expect(selected.map((layout: { name: string }) => layout.name)).toEqual(["C-101", "C-102"]);
    const summary = pluginSource("PlotCommands.cs").split("ToLayoutSummary(Layout layout")[1].split("return summary;")[0];
    expect(summary).toContain('summary["initialized"] = IsInitialized(layout, transaction)');
  });

  it("accepts a layout list or allLayouts with exactly one output target", () => {
    expect(PlotLayoutsToPdfArgsSchema.safeParse({
      action: "plot_layouts_to_pdf",
      layoutNames: ["C-101", "C-102"],
      outputDirectory: "C:/Users/me/Documents/plots",
    }).success).toBe(true);
    expect(PlotLayoutsToPdfArgsSchema.safeParse({
      action: "plot_layouts_to_pdf",
      allLayouts: true,
      outputDirectory: "C:/Users/me/Documents/plots",
      pageSetup: "PDF-ANSI-D",
      plotStyleTable: "monochrome.ctb",
      orientation: "landscape",
    }).success).toBe(true);
    expect(PlotLayoutsToPdfArgsSchema.safeParse({
      action: "plot_layouts_to_pdf",
      layoutNames: ["C-101"],
      outputPath: "C:/Users/me/Documents/C-101.pdf",
    }).success).toBe(true);
    expect(PlotLayoutsToPdfArgsSchema.safeParse({
      action: "plot_layouts_to_pdf",
      layoutNames: ["Model"],
      outputPath: "C:/Users/me/Documents/model.pdf",
      paperSize: "ANSI B (17.00 x 11.00 Inches)",
      plotArea: "window",
      windowCorners: [2000, 1300, 3000, 2000],
    }).success).toBe(true);
    expect(PlotLayoutsToPdfArgsSchema.safeParse({
      action: "plot_layouts_to_pdf",
      allLayouts: true,
      outputDirectory: "C:/Users/me/Documents/plots",
      plotArea: "extents",
    }).success).toBe(true);
  });

  it("rejects ambiguous layout selection and output targets", () => {
    const invalid = [
      { layoutNames: ["C-101"], allLayouts: true, outputDirectory: "C:/out" },
      { outputDirectory: "C:/out" },
      { layoutNames: [], outputDirectory: "C:/out" },
      { layoutNames: ["C-101"] },
      { layoutNames: ["C-101"], outputDirectory: "C:/out", outputPath: "C:/out/a.pdf" },
      { layoutNames: ["C-101", "C-102"], outputPath: "C:/out/a.pdf" },
      { allLayouts: true, outputPath: "C:/out/a.pdf" },
      { layoutNames: ["C-101"], outputPath: "C:/out/a.dwg" },
      { layoutNames: ["C-101"], outputDirectory: "C:/out", orientation: "sideways" },
      // Whitespace-only overrides would be ignored by the plugin.
      { layoutNames: ["C-101"], outputDirectory: "C:/out", pageSetup: "   " },
      { layoutNames: ["C-101"], outputDirectory: "C:/out", paperSize: " " },
      { layoutNames: ["C-101"], outputDirectory: "C:/out", plotStyleTable: "\t" },
      // A window needs its corners, corners need a window, and there are four numbers.
      { layoutNames: ["C-101"], outputDirectory: "C:/out", plotArea: "window" },
      { layoutNames: ["C-101"], outputDirectory: "C:/out", windowCorners: [0, 0, 36, 24] },
      { layoutNames: ["C-101"], outputDirectory: "C:/out", plotArea: "window", windowCorners: [0, 0, 36] },
      { layoutNames: ["C-101"], outputDirectory: "C:/out", plotArea: "display" },
    ];
    for (const args of invalid) {
      expect(
        PlotLayoutsToPdfArgsSchema.safeParse({ action: "plot_layouts_to_pdf", ...args }).success,
        JSON.stringify(args),
      ).toBe(false);
    }
    const listPlotters = PLOT_DOMAIN_DEFINITION.actions.list_plotters.inputSchema;
    expect(listPlotters.safeParse({ action: "list_plotters", device: "DWG To PDF.pc3" }).success).toBe(true);
    expect(listPlotters.safeParse({ action: "list_plotters", device: "  " }).success).toBe(false);
  });

  it("validates publish_sheet_set sheet lists and the PDF output path", () => {
    expect(PublishSheetSetArgsSchema.safeParse({
      action: "publish_sheet_set",
      outputPath: "C:/Users/me/Documents/set.pdf",
    }).success).toBe(true);
    expect(PublishSheetSetArgsSchema.safeParse({
      action: "publish_sheet_set",
      outputPath: "C:/Users/me/Documents/set.PDF",
      sheets: [{ layoutName: "C-101" }, { layoutName: "C-201", drawingPath: "C:/Users/me/Documents/other.dwg" }],
    }).success).toBe(true);
    expect(PublishSheetSetArgsSchema.safeParse({
      action: "publish_sheet_set",
      outputPath: "C:/Users/me/Documents/set.dwf",
    }).success).toBe(false);
    expect(PublishSheetSetArgsSchema.safeParse({
      action: "publish_sheet_set",
      outputPath: "C:/Users/me/Documents/set.pdf",
      layoutNames: ["C-101"],
      sheets: [{ layoutName: "C-102" }],
    }).success).toBe(false);
    expect(PublishSheetSetArgsSchema.safeParse({
      action: "publish_sheet_set",
      outputPath: "C:/Users/me/Documents/set.pdf",
      sheets: [{ drawingPath: "C:/x.dwg" }],
    }).success).toBe(false);
  });
});

describe("civil3d_plot policy classification", () => {
  it("never gates the read-only discovery actions", () => {
    for (const action of ["list_layouts", "list_page_setups", "list_plotters"]) {
      const target = approvalTargetFor(action);
      expect(target.capabilities).toEqual(["query", "inspect"]);
      expect(target.safeForRetry).toBe(true);
      expect(isApprovalRequired(target)).toBe(false);
    }
  });

  it("gates both file-producing actions behind approval and marks them non-retryable", () => {
    for (const action of ["plot_layouts_to_pdf", "publish_sheet_set"]) {
      const target = approvalTargetFor(action);
      expect(target.capabilities).toContain("export");
      expect(target.safeForRetry).toBe(false);
      expect(target.requiresActiveDrawing).toBe(true);
      expect(isApprovalRequired(target)).toBe(true);
    }
  });

  it("publishes a single canonical civil3d_plot catalog entry", () => {
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((candidate) => candidate.toolName === "civil3d_plot");
    expect(entry).toBeDefined();
    expect(entry!.domain).toBe("plot");
    expect(entry!.safeForRetry).toBe(false);
    expect(entry!.requiresActiveDrawing).toBe(true);
    expect(entry!.pluginMethods).toEqual(expect.arrayContaining([
      "plotListLayouts",
      "plotListPageSetups",
      "plotListPlotters",
      "plotLayoutsToPdf",
      "plotPublishSheetSet",
    ]));
  });

  it("exposes plot jobs through civil3d_job, which is itself approval-gated", () => {
    const start = JOB_DOMAIN_DEFINITION.actions.start;
    expect(start.inputSchema.safeParse({
      action: "start",
      operation: "plot_layouts_to_pdf",
      parameters: { allLayouts: true, outputDirectory: "C:/out" },
    }).success).toBe(true);
    expect(start.inputSchema.safeParse({
      action: "start",
      operation: "publish_sheet_set",
      parameters: { outputPath: "C:/out/set.pdf" },
    }).success).toBe(true);
    expect(isApprovalRequired({
      toolName: "civil3d_job",
      action: "start",
      capabilities: start.capabilities,
      safeForRetry: start.safeForRetry,
    })).toBe(true);

    const jobs = pluginSource("JobCommands.cs");
    expect(jobs).toContain('["plot_layouts_to_pdf"] = "plotLayoutsToPdf"');
    expect(jobs).toContain('["publish_sheet_set"] = "plotPublishSheetSet"');
  });
});

describe("civil3d_plot routing", () => {
  beforeEach(() => {
    sendCommandMock.mockReset();
  });

  it("keeps document selection in discovery and publish schemas", () => {
    const document = "C:/imports/scratch.dwg";
    expect(PLOT_DOMAIN_DEFINITION.actions.list_layouts.inputSchema.parse({
      action: "list_layouts", document,
    })).toMatchObject({ document });
    expect(PublishSheetSetArgsSchema.parse({
      action: "publish_sheet_set", document, outputPath: "C:/out/set.pdf",
    })).toMatchObject({ document });
    expect(PublishSheetSetArgsSchema.safeParse({
      action: "publish_sheet_set", document: "  ", outputPath: "C:/out/set.pdf",
    }).success).toBe(false);
  });

  it.each([{}, { plotApi: 1 }, { plotApi: "2" }])("refuses document against a legacy plugin on all routes (%j)", async (health) => {
    sendCommandMock.mockResolvedValue(health);
    const parameters = { document: "scratch.dwg", outputPath: "C:/out/set.pdf" };
    for (const asJob of [false, true]) {
      sendCommandMock.mockClear();
      await expect(PLOT_DOMAIN_DEFINITION.actions.publish_sheet_set.execute({
        action: "publish_sheet_set", ...parameters, asJob,
      })).rejects.toThrow(/install the plugin from this build.*nothing ran/i);
      expect(sendCommandMock.mock.calls).toEqual([["getCivil3DHealth", {}]]);
    }
    sendCommandMock.mockClear();
    await expect(JOB_DOMAIN_DEFINITION.actions.start.execute({
      action: "start", operation: "publish_sheet_set", parameters,
    })).rejects.toThrow(/install the plugin from this build.*nothing ran/i);
    expect(sendCommandMock.mock.calls).toEqual([["getCivil3DHealth", {}]]);
    sendCommandMock.mockClear();
    await expect(PLOT_DOMAIN_DEFINITION.actions.list_layouts.execute({
      action: "list_layouts", document: "scratch.dwg",
    })).rejects.toThrow(/install the plugin from this build.*nothing ran/i);
    expect(sendCommandMock.mock.calls).toEqual([["getCivil3DHealth", {}]]);
  });

  it("passes document only after plot API 2 is confirmed on both job routes", async () => {
    sendCommandMock.mockImplementation(async (method) => method === "getCivil3DHealth"
      ? { plotApi: 2 } : { jobId: "abc", state: "running", operation: "publish_sheet_set" });
    const parameters = { document: "C:/imports/scratch.dwg", outputPath: "C:/out/set.pdf" };
    await PLOT_DOMAIN_DEFINITION.actions.publish_sheet_set.execute(PublishSheetSetArgsSchema.parse({
      action: "publish_sheet_set", ...parameters, asJob: true,
    }));
    expect(sendCommandMock.mock.calls[0]).toEqual(["getCivil3DHealth", {}]);
    expect(sendCommandMock).toHaveBeenLastCalledWith("startJob", {
      operation: "publish_sheet_set", parameters: expect.objectContaining(parameters),
    });
    sendCommandMock.mockClear();
    await JOB_DOMAIN_DEFINITION.actions.start.execute({
      action: "start", operation: "publish_sheet_set", parameters,
    });
    expect(sendCommandMock.mock.calls[0]).toEqual(["getCivil3DHealth", {}]);
    expect(sendCommandMock).toHaveBeenLastCalledWith("startJob", { operation: "publish_sheet_set", parameters });
  });

  it("passes document for direct publishing and discovery after the version check", async () => {
    sendCommandMock.mockResolvedValue({ plotApi: 2 });
    await PLOT_DOMAIN_DEFINITION.actions.list_layouts.execute({ action: "list_layouts", document: "scratch.dwg" });
    expect(sendCommandMock).toHaveBeenLastCalledWith("plotListLayouts", { includeModel: false, document: "scratch.dwg" });
    await PLOT_DOMAIN_DEFINITION.actions.publish_sheet_set.execute({
      action: "publish_sheet_set", outputPath: "C:/out/set.pdf", document: "scratch.dwg",
    });
    expect(sendCommandMock).toHaveBeenLastCalledWith("plotPublishSheetSet", expect.objectContaining({ document: "scratch.dwg" }));
  });

  it("sends plot_layouts_to_pdf to the plugin with safe defaults", async () => {
    sendCommandMock.mockResolvedValue({ plotted: 1 });
    const args = PlotLayoutsToPdfArgsSchema.parse({
      action: "plot_layouts_to_pdf",
      layoutNames: ["C-101"],
      outputDirectory: "C:/out",
    });
    await PLOT_DOMAIN_DEFINITION.actions.plot_layouts_to_pdf.execute(args);

    expect(sendCommandMock).toHaveBeenCalledWith("plotLayoutsToPdf", expect.objectContaining({
      layoutNames: ["C-101"],
      allLayouts: false,
      outputDirectory: "C:/out",
      overwrite: false,
      continueOnError: true,
    }));
  });

  it("passes the plot area and window corners through", async () => {
    sendCommandMock.mockResolvedValue({ plotted: 1 });
    const args = PlotLayoutsToPdfArgsSchema.parse({
      action: "plot_layouts_to_pdf",
      layoutNames: ["Model"],
      outputPath: "C:/out/model.pdf",
      plotArea: "window",
      windowCorners: [2000, 1300, 3000, 2000],
    });
    await PLOT_DOMAIN_DEFINITION.actions.plot_layouts_to_pdf.execute(args);

    expect(sendCommandMock).toHaveBeenCalledWith("plotLayoutsToPdf", expect.objectContaining({
      layoutNames: ["Model"],
      plotArea: "window",
      windowCorners: [2000, 1300, 3000, 2000],
    }));
  });

  it("starts a job instead when asJob is set", async () => {
    sendCommandMock.mockResolvedValue({ jobId: "abc", state: "running", operation: "publish_sheet_set" });
    const args = PublishSheetSetArgsSchema.parse({
      action: "publish_sheet_set",
      outputPath: "C:/out/set.pdf",
      layoutNames: ["C-101", "C-102"],
      asJob: true,
    });
    const result = await PLOT_DOMAIN_DEFINITION.actions.publish_sheet_set.execute(args);

    expect(sendCommandMock).toHaveBeenCalledWith("startJob", {
      operation: "publish_sheet_set",
      parameters: {
        outputPath: "C:/out/set.pdf",
        layoutNames: ["C-101", "C-102"],
        sheets: undefined,
        overwrite: false,
        requireSaved: true,
        keepDsd: false,
      },
    });
    expect(PLOT_DOMAIN_DEFINITION.actions.publish_sheet_set.responseSchema!.safeParse(result).success).toBe(true);
  });

  it("validates a structured per-layout plot result", () => {
    const response = {
      device: "DWG To PDF.pc3",
      method: "-PLOT (BACKGROUNDPLOT=0)",
      requested: 2,
      plotted: 1,
      failed: 1,
      skipped: 0,
      totalBytes: 48213,
      durationMs: 5120,
      layouts: [
        {
          layoutName: "C-101", status: "plotted", outputPath: "C:/out/site-C-101.pdf", bytes: 48213, pageCount: 1,
          paperSize: "ANSI D (34.00 x 22.00 Inches)", orientation: "landscape", plotStyleTable: "monochrome.ctb",
          pageSetup: null, durationMs: 4100, error: null,
        },
        {
          layoutName: "C-102", status: "failed", outputPath: "C:/out/site-C-102.pdf", bytes: null, pageCount: null,
          paperSize: "ANSI D (34.00 x 22.00 Inches)", orientation: "landscape", plotStyleTable: null,
          pageSetup: null, durationMs: 1000, error: "The plot command finished but no PDF was written.",
        },
      ],
      warnings: ["1 of 2 layouts were not plotted; see per-layout status."],
    };
    expect(PLOT_DOMAIN_DEFINITION.actions.plot_layouts_to_pdf.responseSchema!.safeParse(response).success).toBe(true);
  });
});

describe("civil3d_plot native implementation guards", () => {
  it("takes the page orientation from the paper's own shape and the rotation", () => {
    // A medium such as ARCH D (36 x 24) is already landscape at rotation 0;
    // judging by the rotation alone plotted it on a portrait page, cut off.
    const code = pluginCode("PlotCommands.cs");
    expect(code).toContain("return (paper.X > paper.Y) != quarterTurn;");
    expect(code).toContain('IsLandscape(source)');
    expect(pluginSource("PlotCommands.cs")).toContain('IsLandscape(settings) ? "landscape" : "portrait"');
  });

  it("strips C# comments and every string form before the code guards run", () => {
    const sample = [
      'var a = $"{prefix ?? name + "-"}{Sanitize(x)}" + ".pdf"; // PlotEngine',
      'var b = @"C:\\PlotEngine\\""quoted"" ' + "\n" + 'PlotFactory";',
      "var c = '\"'; var d = PlotSettingsValidator.Current; /* PreviewEngine */",
      'var e = $@"{Path.Combine(dir, "PlotEngine")}"; var f = "\\"PlotEngine\\"";',
      'var g = """' + "\n" + 'BackgroundPlotEngine "quoted"' + "\n" + '""";',
    ].join("\n");
    const code = stripCSharpCommentsAndStrings(sample);
    expect(code).not.toMatch(/PlotEngine|PlotFactory|PreviewEngine|quoted/);
    expect(code).toContain("Sanitize(x)");
    expect(code).toContain("Path.Combine(dir, \"\")");
    expect(code).toContain("PlotSettingsValidator.Current");
    expect(code).toContain("var g = \"\";");
  });

  it("drives -PLOT/-PUBLISH in the foreground instead of the crash-prone PlotEngine", () => {
    const source = pluginSource("PlotCommands.cs");
    expect(source).toContain('"_.-PLOT"');
    expect(source).toContain('"_.-PUBLISH"');
    expect(source).toContain('sysvars.Set("BACKGROUNDPLOT", 0)');
    expect(source).toContain('sysvars.Set("FILEDIA", 0)');
    expect(source).toContain('await RunCommandAsync(doc, "PUBLISH", "_.-PUBLISH", dsdPath)');
    expect(source).toContain("CivilExecution.ExecuteCommandSequenceAsync<object?>(");
    // Any use of the plot API in code, however qualified or spaced, fails the
    // guard; the comments explaining the decision do not.
    const code = pluginCode("PlotCommands.cs");
    expect(code).toContain("PlotSettingsValidator.Current");
    expect(code).not.toMatch(/\b(?:PlotFactory|PlotEngine|BackgroundPlotEngine|PreviewEngine|SetPlotCentered)\b/);
  });

  it("lets the plotter write the final path under the boundary's link checks", () => {
    const source = pluginSource("PlotCommands.cs");
    const boundary = pluginSource("FileBoundary.cs");
    expect(source).toContain("FileBoundary.BeginExternalWrite(job.OutputPath!, overwrite)");
    expect(source).toContain("FileBoundary.BeginExternalWrite(outputPath, overwrite)");
    // The plotter gets the final name, so "open in viewer when done" opens the
    // real PDF instead of a temp name that was renamed away.
    expect(source).toMatch(/tokens\.Add\(output\.FinalPath\);\s*\/\/ File name/);
    expect(source).toContain("BuildDsd(sheets, currentPath, output.FinalPath)");
    expect(source).not.toContain("TempPath");
    // The content check reads the handle Commit checked, never the path again.
    expect(source).toContain("output.Commit((path, stream) => VerifyOutput(path, stream, job.StartedUtc))");
    expect(source).toContain("output.Commit((path, stream) => VerifyPublishedOutput(path, stream, startedUtc, settings))");
    // Both anchors must exist, or the guard would pass on an empty slice.
    const verifyStart = pluginCode("PlotCommands.cs").indexOf("OutputFile VerifyOutput(");
    expect(verifyStart).toBeGreaterThan(-1);
    const verify = pluginCode("PlotCommands.cs").slice(verifyStart);
    const buildDsdStart = verify.indexOf("private static string BuildDsd");
    expect(buildDsdStart).toBeGreaterThan(0);
    expect(verify.slice(0, buildDsdStart)).not.toMatch(/\b(?:FileInfo|File\.ReadAll\w*|File\.Open\w*)\(/);
    // The publish DSD is written under the external-write lock, then re-checked
    // and held open right before -PUBLISH reads it.
    const publishStart = source.indexOf("FileBoundary.BeginExternalWrite(outputPath, overwrite)");
    expect(publishStart).toBeGreaterThan(-1);
    const publish = source.slice(publishStart);
    const dsdWrite = publish.indexOf("FileBoundary.WriteAllTextAtomic(dsdPath, dsd, encoding, true, \".dsd\")");
    const dsdHold = publish.indexOf("FileBoundary.HoldVerifiedFile(dsdPath, dsd, encoding)");
    expect(dsdWrite).toBeGreaterThan(-1);
    expect(dsdHold).toBeGreaterThan(dsdWrite);
    expect(publish.indexOf('await RunCommandAsync(doc, "PUBLISH"')).toBeGreaterThan(dsdHold);
    expect(source.indexOf("FileBoundary.WriteAllTextAtomic(dsdPath")).toBeGreaterThan(source.indexOf("FileBoundary.BeginExternalWrite(outputPath, overwrite)"));
    expect(boundary).toMatch(/GenericRead \| FileReadAttributes,\s*FileShare\.Read,/);
    expect(boundary).toContain("FileFlagBackupSemantics | FileFlagOpenReparsePoint");
    expect(boundary).toContain("links != 1");
    expect(boundary).not.toContain("mcp-tmp");
  });

  it("refuses to feed a new command into a prompt an earlier request left open", () => {
    const runner = pluginSource("PlotCommands.cs");
    const run = runner.slice(runner.indexOf("internal static async Task RunCommandAsync"));
    const staleCheck = run.indexOf("FindActiveCommand(doc, PendingCommandsFor(doc))");
    const command = run.indexOf("await doc.Editor.CommandAsync(tokens)");
    expect(staleCheck).toBeGreaterThan(-1);
    expect(staleCheck).toBeLessThan(command);
    expect(run.indexOf("ActiveCommandNames(doc)")).toBeGreaterThan(command);
    // Only unfinished invocations are remembered: a command that completed is
    // forgotten at once, so a PLOT/PUBLISH/XREF the user starts later is not
    // cancelled as a stale request, and pending entries are per document.
    expect(runner).not.toContain("HashSet<string> DrivenCommands");
    expect(runner).toContain("List<(Document Doc, string Command)> PendingInvocations");
    expect(run.indexOf("PendingInvocations.Remove(invocation)")).toBeGreaterThan(command);
    expect(runner).toContain("ReferenceEquals(entry.Doc, doc)");
    // CMDNAMES describes only the active drawing, so pending state for
    // another drawing is never updated from it.
    expect(runner).toContain("ReferenceEquals(App.DocumentManager.MdiActiveDocument, doc)");
  });

  it("refuses an incomplete sheet set before moving a PDF or writing the DSD", () => {
    const source = pluginSource("PlotCommands.cs");
    expect(source).toContain("layout.GetViewports().Count > 0");
    expect(source).not.toContain("currentLayouts.Where(layout => layout.Initialized)");
    expect(source).not.toContain("was not pre-validated");
    expect(source.match(/RequireInitialized\(match\);/g)?.length).toBe(2);
    const publish = source.slice(source.indexOf("public static Task<object?> PublishSheetSetAsync"));
    const preflight = publish.indexOf("PreflightPublishSheets(");
    expect(preflight).toBeGreaterThan(0);
    expect(preflight).toBeLessThan(publish.indexOf("FileBoundary.BeginExternalWrite(outputPath, overwrite)"));
    expect(preflight).toBeLessThan(publish.indexOf("FileBoundary.WriteAllTextAtomic(dsdPath"));
    const read = source.slice(source.indexOf("private static List<LayoutSnapshot> ReadLayoutSnapshots(Document doc)"),
      source.indexOf("private static List<LayoutSnapshot> ReadLayoutSnapshots(string sourcePath)"));
    expect(read).toContain("doc.LockDocument()");
    expect(read).toContain("StartOpenCloseTransaction()");
    expect(read).not.toContain("transaction.Commit()");
  });

  it("drains native publishing on cancellation and preserves an accepted result", () => {
    const execution = pluginCode("CivilExecution.cs");
    const sequence = execution.slice(execution.indexOf("ExecuteCommandSequenceAsync"), execution.indexOf("public static Task<T> ReadAsync"));
    expect(sequence).toContain("bool drainOnCancellation = false");
    expect(sequence).toContain("drainOnCancellation: drainOnCancellation");
    expect(sequence).toContain("if (drainOnCancellation)");
    expect(sequence).toContain("await hostTask;");
    expect(sequence).toContain("catch (OperationCanceledException) when (actionCompleted)");
    expect(sequence).toContain("catch (OperationCanceledException) when (capturedException != null)");
    expect(sequence).toContain("else await AwaitHostContextAsync(hostTask, cancellationToken)");
    const plots = pluginCode("PlotCommands.cs");
    const publish = plots.slice(plots.indexOf("PublishSheetSetAsync"), plots.indexOf("private sealed class PlotJob"));
    expect(publish).toContain("drainOnCancellation: true");
    expect(plots.slice(plots.indexOf("PlotLayoutsToPdfAsync"), plots.indexOf("PublishSheetSetAsync"))).not.toContain("drainOnCancellation: true");
    expect(sequence).not.toMatch(/MdiActiveDocument\s*=(?!=)/);
    const jobs = pluginCode("JobCommands.cs");
    expect(jobs).toMatch(/lock\s*\(job.SyncRoot\)[\s\S]*?job.Warnings.ToArray\(\)/);
  });

  it("routes every output path through FileBoundary and fails fast without a document", () => {
    const source = pluginSource("PlotCommands.cs");
    expect(source).toContain('FileBoundary.ResolveExportPath(raw, overwrite, ".pdf")');
    expect(source).toContain('FileBoundary.ResolveExportPath(outputPathRaw, overwrite, ".pdf")');
    expect(source).toContain('FileBoundary.ResolveImportPath(drawingPath, ".dwg")');
    expect(source).toContain("FileBoundary.WriteAllTextAtomic(dsdPath");

    const execution = pluginSource("CivilExecution.cs");
    const sequence = execution.slice(execution.indexOf("ExecuteCommandSequenceAsync"));
    expect(sequence.indexOf("CIVIL3D.NO_DRAWING")).toBeGreaterThan(-1);
    expect(sequence.indexOf("CIVIL3D.NO_DRAWING")).toBeLessThan(sequence.indexOf("RunOnHostAsync"));
  });
});
