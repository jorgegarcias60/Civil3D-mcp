import { z } from "zod";
import { withApplicationConnection, type ApplicationCommandClient } from "../../utils/ConnectionManager.js";
import { Civil3DRpcError } from "../../utils/SocketClient.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

/**
 * civil3d_plot — layout discovery, PDF plotting/publishing and PNG review renders.
 *
 * The plugin drives the -PLOT and -PUBLISH commands (BACKGROUNDPLOT=0) rather
 * than the AutoCAD PlotEngine API: live testing on Civil 3D 2027 found the
 * PlotEngine crash-prone from a command context, whereas a command that gets
 * an unexpected answer fails with a message. See PlotCommands.cs for details.
 *
 * Read-only discovery actions are safe to retry and are never approval-gated.
 * All three output actions declare the "export" capability, so the central
 * approval policy requires an approval token before they write a file, and
 * every output path is re-validated by the plugin's FileBoundary (export
 * roots, .pdf/.png extension, no junctions, overwrite=false by default; the
 * review PNGs are always replaced).
 */

export const PLOT_JOB_OPERATIONS = {
  plot_layouts_to_pdf: "plot_layouts_to_pdf",
  render_layouts_to_png: "render_layouts_to_png",
  publish_sheet_set: "publish_sheet_set",
} as const;

// Plot API levels (civil3d_health plotApi): 2 added document targeting, 3 the PNG review render.
const RENDER_PLOT_API = 3;

const OrientationSchema = z.enum(["portrait", "landscape"]);
const NonBlankStringSchema = z.string().refine((value) => value.trim().length > 0, "must not be blank");

const PlotSettingsSummaryShape = {
  device: z.string().nullable(),
  canonicalMediaName: z.string().nullable(),
  paperUnits: z.string(),
  paperWidth: z.number(),
  paperHeight: z.number(),
  orientation: OrientationSchema,
  rotation: z.string(),
  plotType: z.string(),
  plotStyleTable: z.string().nullable(),
};

const LayoutSummarySchema = z.object({
  name: z.string(),
  tabOrder: z.number().int(),
  isModel: z.boolean(),
  initialized: z.boolean().optional(),
  isCurrent: z.boolean(),
  pageSetup: z.string().nullable(),
  handle: z.string(),
  ctbFound: z.boolean().optional(),
  mediaAvailable: z.boolean().optional(),
  ...PlotSettingsSummaryShape,
}).passthrough();

const PageSetupSummarySchema = z.object({
  name: z.string(),
  modelType: z.boolean(),
  ...PlotSettingsSummaryShape,
}).passthrough();

const LayoutPlotResultSchema = z.object({
  layoutName: z.string(),
  status: z.enum(["plotted", "failed", "skipped"]),
  outputPath: z.string().nullable(),
  bytes: z.number().int().nonnegative().nullable(),
  pageCount: z.number().int().nonnegative().nullable(),
  paperSize: z.string(),
  orientation: OrientationSchema,
  plotStyleTable: z.string().nullable(),
  pageSetup: z.string().nullable(),
  durationMs: z.number().nonnegative(),
  error: z.string().nullable(),
}).passthrough();

const PlotLayoutsResultSchema = z.object({
  device: z.string(),
  method: z.string(),
  requested: z.number().int().nonnegative(),
  plotted: z.number().int().nonnegative(),
  failed: z.number().int().nonnegative(),
  skipped: z.number().int().nonnegative(),
  totalBytes: z.number().nonnegative(),
  durationMs: z.number().nonnegative(),
  layouts: z.array(LayoutPlotResultSchema),
  warnings: z.array(z.string()),
}).passthrough();

const LayoutRenderResultSchema = z.object({
  layoutName: z.string(),
  status: z.enum(["rendered", "failed", "skipped"]),
  ok: z.boolean(),
  outputPath: z.string().nullable(),
  bytes: z.number().int().nonnegative().nullable(),
  media: z.string().nullable(),
  mediaPixels: z.array(z.number()).length(2).nullable(),
  pixelWidth: z.number().int().positive().nullable(),
  pixelHeight: z.number().int().positive().nullable(),
  paperSize: z.string(),
  orientation: OrientationSchema,
  plotArea: z.enum(["layout", "window"]),
  plotStyleTable: z.string().nullable(),
  durationMs: z.number().nonnegative(),
  regenerated: z.boolean().optional(),
  error: z.string().nullable(),
}).passthrough();

const RenderLayoutsResultSchema = z.object({
  device: z.string(),
  method: z.string(),
  regenerated: z.boolean(),
  requested: z.number().int().nonnegative(),
  rendered: z.number().int().nonnegative(),
  failed: z.number().int().nonnegative(),
  skipped: z.number().int().nonnegative(),
  totalBytes: z.number().nonnegative(),
  durationMs: z.number().nonnegative(),
  layouts: z.array(LayoutRenderResultSchema),
  warnings: z.array(z.string()),
}).passthrough();

const PublishResultSchema = z.object({
  outputPath: z.string(),
  bytes: z.number().int().positive(),
  pageCount: z.number().int().nonnegative().nullable(),
  sheetCount: z.number().int().positive(),
  sheets: z.array(z.object({
    index: z.number().int().positive(),
    layoutName: z.string(),
    drawingPath: z.string(),
    pageSetup: z.string().nullable().optional(),
    device: z.string().optional(),
    media: z.string().nullable().optional(),
    plotStyleTable: z.string().nullable().optional(),
    paperSizeMm: z.array(z.number()).length(2).optional(),
  }).passthrough()),
  verified: z.boolean().optional(),
  verification: z.enum(["verified", "unverified"]).optional(),
  method: z.string(),
  dsdPath: z.string().nullable(),
  durationMs: z.number().nonnegative(),
  published: z.literal(true),
  warnings: z.array(z.string()),
}).passthrough();

// Returned instead of the result when asJob=true (see civil3d_job status).
const JobHandleSchema = z.object({
  jobId: z.string(),
  state: z.enum(["running", "completed", "failed", "cancelled"]),
  operation: z.string(),
}).passthrough();

const ListLayoutsArgsSchema = z.object({
  action: z.literal("list_layouts"),
  includeModel: z.boolean().optional(),
  document: NonBlankStringSchema.optional(),
});

const ListPageSetupsArgsSchema = z.object({
  action: z.literal("list_page_setups"),
});

// Plot area: a paper-space layout defaults to "layout" (the sheet at 1:1); the
// Model tab, which has no layout area, defaults to "extents" scaled to fit.
// "window" corners are in the plotted space's own units: paper units on a
// layout, drawing units on the Model tab.
const PlotAreaSchema = z.enum(["layout", "extents", "window"]);

// The plugin treats a whitespace-only override as absent, so reject it here
// instead of silently plotting with the layout's own settings.
const ListPlottersArgsSchema = z.object({
  action: z.literal("list_plotters"),
  device: NonBlankStringSchema.optional(),
});

export const PlotLayoutsToPdfArgsSchema = z.object({
  action: z.literal("plot_layouts_to_pdf"),
  layoutNames: z.array(z.string().min(1)).min(1).optional(),
  allLayouts: z.boolean().optional(),
  outputDirectory: z.string().min(1).optional(),
  outputPath: z.string().min(1).optional(),
  fileNamePrefix: z.string().optional(),
  pageSetup: NonBlankStringSchema.optional(),
  paperSize: NonBlankStringSchema.optional(),
  plotStyleTable: NonBlankStringSchema.optional(),
  orientation: OrientationSchema.optional(),
  plotArea: PlotAreaSchema.optional(),
  windowCorners: z.array(z.number()).length(4).optional(),
  overwrite: z.boolean().optional(),
  continueOnError: z.boolean().optional(),
  asJob: z.boolean().optional(),
}).superRefine((args, context) => {
  const hasLayouts = (args.layoutNames?.length ?? 0) > 0;
  if (hasLayouts === Boolean(args.allLayouts)) {
    context.addIssue({
      code: z.ZodIssueCode.custom,
      message: "Provide either a non-empty layoutNames array or allLayouts=true (not both).",
      path: ["layoutNames"],
    });
  }
  if (Boolean(args.outputDirectory) === Boolean(args.outputPath)) {
    context.addIssue({
      code: z.ZodIssueCode.custom,
      message: "Provide exactly one of outputDirectory or outputPath.",
      path: ["outputDirectory"],
    });
  }
  if (args.outputPath && !/\.pdf$/i.test(args.outputPath)) {
    context.addIssue({ code: z.ZodIssueCode.custom, message: "outputPath must end in .pdf.", path: ["outputPath"] });
  }
  if ((args.plotArea === "window") !== Boolean(args.windowCorners)) {
    context.addIssue({
      code: z.ZodIssueCode.custom,
      message: "plotArea 'window' needs windowCorners [x1, y1, x2, y2], and windowCorners needs plotArea 'window'.",
      path: ["windowCorners"],
    });
  }
  if (args.outputPath && (args.allLayouts || (args.layoutNames?.length ?? 0) > 1)) {
    context.addIssue({
      code: z.ZodIssueCode.custom,
      message: "outputPath plots exactly one layout; use outputDirectory for several.",
      path: ["outputPath"],
    });
  }
});

// A drive-letter or UNC path. The plugin re-checks the folder against the export roots.
const AbsolutePathSchema = z.string().min(1).refine(
  (value) => /^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+)/.test(value),
  "must be an absolute path (for example C:/Users/me/Documents/review).",
);

// Layout-to-PNG review render: a paper-space layout at its own aspect on the
// raster PC3's pixel media, one file per layout, always overwritten. Plot area
// is the sheet ("layout", default: a window over the layout's limits, fitted and
// centred) or a paper-space "window".
export const RenderLayoutsToPngArgsSchema = z.object({
  action: z.literal("render_layouts_to_png"),
  layoutNames: z.array(z.string().min(1)).min(1).optional(),
  allLayouts: z.boolean().optional(),
  outputDirectory: AbsolutePathSchema,
  fileNamePrefix: z.string().optional(),
  pixelWidth: z.number().int().min(320).max(16000).optional(),
  paperSize: NonBlankStringSchema.optional(),
  plotStyleTable: NonBlankStringSchema.optional(),
  plotArea: z.enum(["layout", "window"]).optional(),
  windowCorners: z.array(z.number()).length(4).optional(),
  continueOnError: z.boolean().optional(),
  asJob: z.boolean().optional(),
}).superRefine((args, context) => {
  const hasLayouts = (args.layoutNames?.length ?? 0) > 0;
  if (hasLayouts === Boolean(args.allLayouts)) {
    context.addIssue({
      code: z.ZodIssueCode.custom,
      message: "Provide either a non-empty layoutNames array or allLayouts=true (not both).",
      path: ["layoutNames"],
    });
  }
  if ((args.plotArea === "window") !== Boolean(args.windowCorners)) {
    context.addIssue({
      code: z.ZodIssueCode.custom,
      message: "plotArea 'window' needs windowCorners [x1, y1, x2, y2], and windowCorners needs plotArea 'window'.",
      path: ["windowCorners"],
    });
  }
});

const PublishSheetSchema = z.object({
  layoutName: z.string().min(1),
  drawingPath: z.string().min(1).optional(),
});

export const PublishSheetSetArgsSchema = z.object({
  action: z.literal("publish_sheet_set"),
  document: NonBlankStringSchema.optional(),
  outputPath: z.string().min(1).regex(/\.pdf$/i, "outputPath must end in .pdf."),
  layoutNames: z.array(z.string().min(1)).min(1).optional(),
  sheets: z.array(PublishSheetSchema).min(1).optional(),
  overwrite: z.boolean().optional(),
  requireSaved: z.boolean().optional(),
  keepDsd: z.boolean().optional(),
  asJob: z.boolean().optional(),
}).refine((args) => !(args.layoutNames && args.sheets), {
  message: "Provide layoutNames or sheets, not both. Omit both to publish every paper-space layout.",
  path: ["sheets"],
});

type PlotLayoutsToPdfArgs = z.infer<typeof PlotLayoutsToPdfArgsSchema>;
type RenderLayoutsToPngArgs = z.infer<typeof RenderLayoutsToPngArgsSchema>;
type PublishSheetSetArgs = z.infer<typeof PublishSheetSetArgsSchema>;

export function toPlotLayoutsParameters(args: PlotLayoutsToPdfArgs) {
  return {
    layoutNames: args.layoutNames,
    allLayouts: args.allLayouts ?? false,
    outputDirectory: args.outputDirectory,
    outputPath: args.outputPath,
    fileNamePrefix: args.fileNamePrefix,
    pageSetup: args.pageSetup,
    paperSize: args.paperSize,
    plotStyleTable: args.plotStyleTable,
    orientation: args.orientation,
    plotArea: args.plotArea,
    windowCorners: args.windowCorners,
    overwrite: args.overwrite ?? false,
    continueOnError: args.continueOnError ?? true,
  };
}

export function toRenderLayoutsParameters(args: RenderLayoutsToPngArgs) {
  return {
    layoutNames: args.layoutNames,
    allLayouts: args.allLayouts ?? false,
    outputDirectory: args.outputDirectory,
    fileNamePrefix: args.fileNamePrefix,
    pixelWidth: args.pixelWidth,
    paperSize: args.paperSize,
    plotStyleTable: args.plotStyleTable,
    plotArea: args.plotArea,
    windowCorners: args.windowCorners,
    continueOnError: args.continueOnError ?? true,
  };
}

export function toPublishSheetSetParameters(args: PublishSheetSetArgs) {
  return {
    ...(args.document !== undefined ? { document: args.document } : {}),
    outputPath: args.outputPath,
    layoutNames: args.layoutNames,
    sheets: args.sheets,
    overwrite: args.overwrite ?? false,
    requireSaved: args.requireSaved ?? true,
    keepDsd: args.keepDsd ?? false,
  };
}

// Keep this list in sync with parameters and operations added after the
// original plot API. Both direct plotting and generic job starts must refuse
// ignored targeting and operations an older plugin does not have.
export async function requirePlotApi(
  client: ApplicationCommandClient,
  parameters: Record<string, unknown>,
  operation?: string,
) {
  const needs: Array<{ name: string; api: number }> = [];
  if (parameters.document !== undefined) needs.push({ name: "document", api: 2 });
  if (operation === PLOT_JOB_OPERATIONS.render_layouts_to_png) {
    needs.push({ name: PLOT_JOB_OPERATIONS.render_layouts_to_png, api: RENDER_PLOT_API });
  }
  if (needs.length === 0) return;
  const health = await client.sendCommand("getCivil3DHealth", {}) as Record<string, unknown>;
  const api = typeof health?.plotApi === "number" && Number.isFinite(health.plotApi) ? health.plotApi : 1;
  const unmet = needs.find((need) => api < need.api);
  if (unmet) {
    throw new Civil3DRpcError(
      `The Civil 3D plugin is older than this server: ${unmet.name} needs plot API ${unmet.api}, the loaded plugin has ${api}. Install the plugin from this build (Civil 3D closed); nothing ran.`,
      "CIVIL3D.INVALID_INPUT", -32602,
    );
  }
}

async function runPlotOperation(
  pluginMethod: string,
  jobOperation: string,
  parameters: Record<string, unknown>,
  asJob: boolean | undefined,
) {
  return await withApplicationConnection(async (appClient) => {
    await requirePlotApi(appClient, parameters, jobOperation);
    return asJob
      ? await appClient.sendCommand("startJob", { operation: jobOperation, parameters })
      : await appClient.sendCommand(pluginMethod, parameters);
  });
}

export const PLOT_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "plot",
  actions: {
    list_layouts: {
      action: "list_layouts",
      inputSchema: ListLayoutsArgsSchema,
      responseSchema: z.object({
        drawing: z.string(),
        currentLayout: z.string().nullable(),
        count: z.number().int().nonnegative(),
        layouts: z.array(LayoutSummarySchema),
      }).passthrough(),
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["plotListLayouts"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => {
          await requirePlotApi(appClient, args);
          return await appClient.sendCommand("plotListLayouts", {
            includeModel: args.includeModel ?? false,
            ...(args.document !== undefined ? { document: args.document } : {}),
          });
        },
      ),
    },
    list_page_setups: {
      action: "list_page_setups",
      inputSchema: ListPageSetupsArgsSchema,
      responseSchema: z.object({
        count: z.number().int().nonnegative(),
        pageSetups: z.array(PageSetupSummarySchema),
      }).passthrough(),
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["plotListPageSetups"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("plotListPageSetups", {}),
      ),
    },
    list_plotters: {
      action: "list_plotters",
      inputSchema: ListPlottersArgsSchema,
      responseSchema: z.object({
        devices: z.array(z.string()),
        plotStyleTables: z.array(z.string()),
        defaultPdfDevice: z.string(),
        pdfDeviceAvailable: z.boolean(),
        device: z.string().nullable(),
        media: z.array(z.object({ canonicalName: z.string(), localeName: z.string() })).nullable(),
      }).passthrough(),
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["plotListPlotters"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("plotListPlotters", { device: args.device }),
      ),
    },
    plot_layouts_to_pdf: {
      action: "plot_layouts_to_pdf",
      inputSchema: PlotLayoutsToPdfArgsSchema as unknown as z.ZodType<Record<string, unknown>>,
      responseSchema: z.union([PlotLayoutsResultSchema, JobHandleSchema]),
      capabilities: ["export", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["plotLayoutsToPdf", "startJob"],
      execute: async (rawArgs) => {
        const args = rawArgs as PlotLayoutsToPdfArgs;
        return await runPlotOperation(
          "plotLayoutsToPdf",
          PLOT_JOB_OPERATIONS.plot_layouts_to_pdf,
          toPlotLayoutsParameters(args),
          args.asJob,
        );
      },
    },
    render_layouts_to_png: {
      action: "render_layouts_to_png",
      inputSchema: RenderLayoutsToPngArgsSchema as unknown as z.ZodType<Record<string, unknown>>,
      responseSchema: z.union([RenderLayoutsResultSchema, JobHandleSchema]),
      capabilities: ["export", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["plotLayoutsToPng", "startJob"],
      execute: async (rawArgs) => {
        const args = rawArgs as RenderLayoutsToPngArgs;
        return await runPlotOperation(
          "plotLayoutsToPng",
          PLOT_JOB_OPERATIONS.render_layouts_to_png,
          toRenderLayoutsParameters(args),
          args.asJob,
        );
      },
    },
    publish_sheet_set: {
      action: "publish_sheet_set",
      inputSchema: PublishSheetSetArgsSchema as unknown as z.ZodType<Record<string, unknown>>,
      responseSchema: z.union([PublishResultSchema, JobHandleSchema]),
      capabilities: ["export", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["plotPublishSheetSet", "startJob"],
      execute: async (rawArgs) => {
        const args = rawArgs as PublishSheetSetArgs;
        return await runPlotOperation(
          "plotPublishSheetSet",
          PLOT_JOB_OPERATIONS.publish_sheet_set,
          toPublishSheetSetParameters(args),
          args.asJob,
        );
      },
    },
  },
  exposures: [
    {
      toolName: "civil3d_plot",
      displayName: "Civil 3D Plot",
      description:
        "Lists layouts, page setups, and plot devices, and plots layouts (paper-space, or the Model tab by name) to PDF (one file per layout) or publishes paper-space layouts as one multi-sheet PDF. " +
        "render_layouts_to_png renders paper-space layouts to review PNGs (one per layout, always overwritten, on 'PublishToWeb PNG.pc3', fit to the pixel medium nearest pixelWidth); use it to look at the sheet before the one PDF plot. " +
        "Uses the -PLOT/-PUBLISH commands with BACKGROUNDPLOT=0 and the 'DWG To PDF.pc3' device; output paths must be absolute .pdf paths (.png in an outputDirectory for the render) inside the configured export roots. " +
        "Publish preflight checks every sheet's plot style table and PDF media. Set asJob=true for long runs and poll civil3d_job action=status.",
      inputShape: {
        action: z.enum([
          "list_layouts",
          "list_page_setups",
          "list_plotters",
          "plot_layouts_to_pdf",
          "render_layouts_to_png",
          "publish_sheet_set",
        ]),
        includeModel: z.boolean().optional().describe("list_layouts: include the Model layout."),
        document: NonBlankStringSchema.optional().describe("list_layouts/publish_sheet_set: open drawing full path or unique file name, or saved .dwg inside import roots. Reads settings without activating it; publish uses its saved path."),
        device: z.string().optional().describe("list_plotters: also list paper sizes for this device (for example 'DWG To PDF.pc3')."),
        layoutNames: z.array(z.string()).optional().describe("Layouts to plot/publish, in output order. plot_layouts_to_pdf also accepts 'Model'."),
        allLayouts: z.boolean().optional().describe("plot_layouts_to_pdf/render_layouts_to_png: every paper-space layout in tab order."),
        sheets: z.array(PublishSheetSchema).optional().describe("publish_sheet_set: explicit sheet list; drawingPath (absolute .dwg inside the import roots) defaults to document, or the active drawing when omitted."),
        outputDirectory: z.string().optional().describe("plot_layouts_to_pdf: absolute folder for one PDF per layout. render_layouts_to_png: absolute folder for one PNG per layout (required)."),
        outputPath: z.string().optional().describe("Absolute .pdf path (single layout for plot_layouts_to_pdf; the combined PDF for publish_sheet_set)."),
        fileNamePrefix: z.string().optional().describe("plot_layouts_to_pdf/render_layouts_to_png: file name prefix (default '<drawing>-')."),
        pageSetup: z.string().optional().describe("plot_layouts_to_pdf: named paper-space page setup to take paper size, orientation, and plot style from."),
        paperSize: z.string().optional().describe("plot_layouts_to_pdf: paper size name on DWG To PDF.pc3 (overrides the layout/page setup). render_layouts_to_png: pixel medium name on PublishToWeb PNG.pc3 (overrides the automatic choice; list_plotters with that device lists them)."),
        plotStyleTable: z.string().optional().describe("plot_layouts_to_pdf/render_layouts_to_png: plot style table, for example 'monochrome.ctb' (render default: the layout's own)."),
        pixelWidth: z.number().int().optional().describe("render_layouts_to_png: wanted width in pixels of the sheet's long side (default 3300, 320-16000). The pixel medium closest to it is used and reported."),
        orientation: OrientationSchema.optional(),
        plotArea: PlotAreaSchema.optional().describe("plot_layouts_to_pdf: 'layout' (paper-space default, 1:1), 'extents', or 'window'. The Model tab plots 'extents' (default) or 'window', scaled to fit and centred. render_layouts_to_png: 'layout' (default) or 'window'."),
        windowCorners: z.array(z.number()).optional().describe("plot_layouts_to_pdf/render_layouts_to_png with plotArea='window': [x1, y1, x2, y2] in paper units on a layout, drawing units on the Model tab."),
        overwrite: z.boolean().optional().describe("Replace existing PDFs (default false). render_layouts_to_png always replaces its PNGs."),
        continueOnError: z.boolean().optional().describe("plot_layouts_to_pdf/render_layouts_to_png: keep going after a layout fails (default true)."),
        requireSaved: z.boolean().optional().describe("publish_sheet_set: refuse when the drawing has unsaved changes (default true)."),
        keepDsd: z.boolean().optional().describe("publish_sheet_set: keep the generated .dsd sheet list next to the PDF."),
        asJob: z.boolean().optional().describe("Run the plot/publish as a background job and return a jobId."),
      },
      supportedActions: [
        "list_layouts",
        "list_page_setups",
        "list_plotters",
        "plot_layouts_to_pdf",
        "render_layouts_to_png",
        "publish_sheet_set",
      ],
      resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }),
    },
  ],
};
