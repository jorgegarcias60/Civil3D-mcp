using System.Collections.Specialized;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// Handlers for the civil3d_plot domain: layout/page-setup/plotter discovery
/// and PDF output of paper-space layouts.
///
/// Implementation choice (why commands, not PlotEngine):
///   Live testing against Civil 3D 2027 (civil3d-automation docs/FINDINGS.md)
///   found that PlotFactory/PlotEngine is crash-prone when driven from a
///   command/SendCommand context -- which is exactly the context this plugin
///   runs host work in -- and a crash takes the whole application (and any
///   unsaved work) down. The -PLOT command driven with BACKGROUNDPLOT=0 is
///   slower but fails with a prompt or a message instead of crashing. The
///   same findings record that PlotSettingsValidator.SetPlotCentered throws for
///   Layout plots and that changing layouts inside an open transaction
///   crashes, so this module:
///     * only READS plot settings through .NET (short, disposed transactions,
///       plus throwaway non-database-resident PlotSettings for media lookups);
///     * never modifies a layout or page setup ("Save changes to page setup?
///       No");
///     * issues -PLOT / -PUBLISH through Editor.CommandAsync inside
///       ExecuteInCommandContextAsync with FILEDIA=0, CMDECHO=0 and
///       BACKGROUNDPLOT=0, restoring every system variable afterwards;
///     * validates every answer (layout, device, paper, plot style table,
///       output path) BEFORE starting the command, so a bad value cannot shift
///       the prompt chain;
///     * verifies the output file exists, is non-empty and was written by this
///       run before reporting success.
///
/// The -PLOT prompt chain is the one established empirically with LASTPROMPT
/// logging in civil3d-automation lisp/plot.lsp (detailed configuration = Yes,
/// "Plot paper space first?" before "Hide paperspace objects?", and no
/// "write to file?" prompt for a PDF device).
/// </summary>
public static class PlotCommands
{
  public const string DefaultPdfDevice = "DWG To PDF.pc3";
  private const long PageCountReadLimitBytes = 256L * 1024 * 1024;
  private static readonly Regex PdfPageObject = new(@"/Type\s*/Page(?![A-Za-z])", RegexOptions.Compiled);

  // -------------------------------------------------------------------------
  // plotListLayouts
  // -------------------------------------------------------------------------

  public static Task<object?> ListLayoutsAsync(JsonObject? parameters)
  {
    var includeModel = PluginRuntime.GetOptionalBool(parameters, "includeModel") ?? false;
    var document = PluginRuntime.GetOptionalString(parameters, "document");
    return CivilExecution.ReadAsync<object?>((active, _, database, transaction) =>
    {
      if (document == null)
        return ListLayouts(database, transaction, active.Name,
          Convert.ToString(App.GetSystemVariable("CTAB")), includeModel);

      var open = App.DocumentManager.Cast<Document>().ToArray();
      var match = RunCodeHelpers.MatchDocument(document, open.Select(item => item.Name).ToArray());
      if (match.Error == null)
      {
        var target = open[match.Index];
        if (ReferenceEquals(target, active))
          return ListLayouts(database, transaction, target.Name,
            Convert.ToString(App.GetSystemVariable("CTAB")), includeModel);
        using var targetLock = target.LockDocument();
        using var targetTransaction = target.Database.TransactionManager.StartOpenCloseTransaction();
        return ListLayouts(target.Database, targetTransaction, target.Name, null, includeModel);
      }

      // A full path under an import root can also name a saved closed DWG.
      // A filename alone must uniquely identify an open drawing.
      if (!Path.IsPathFullyQualified(document))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", match.Error);
      var path = FileBoundary.ResolveImportPath(document, ".dwg");
      using var side = new Database(false, true);
      side.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
      side.CloseInput(true);
      using var sideTransaction = side.TransactionManager.StartOpenCloseTransaction();
      return ListLayouts(side, sideTransaction, path, null, includeModel);
    });
  }

  private static object ListLayouts(Database database, Transaction transaction, string drawing,
    string? currentLayout, bool includeModel)
  {
      var validator = PlotSettingsValidator.Current;
      using var probe = new PlotSettings(false);
      validator.RefreshLists(probe);
      var devices = ToList(validator.GetPlotDeviceList());
      var styles = ToList(validator.GetPlotStyleSheetList());
      var pdfMedia = devices.Contains(DefaultPdfDevice, StringComparer.OrdinalIgnoreCase)
        ? ReadMedia(probe, DefaultPdfDevice) : [];
      var layouts = ReadLayouts(database, transaction)
        .Where(layout => includeModel || !layout.ModelType)
        .Select(layout =>
        {
          var summary = ToLayoutSummary(layout, transaction, currentLayout);
          summary["ctbFound"] = !string.IsNullOrWhiteSpace(layout.CurrentStyleSheet)
            && styles.Contains(layout.CurrentStyleSheet, StringComparer.OrdinalIgnoreCase);
          summary["mediaAvailable"] = pdfMedia.Any(media =>
            string.Equals(media.Canonical, layout.CanonicalMediaName, StringComparison.OrdinalIgnoreCase));
          return summary;
        })
        .ToList();

      return new Dictionary<string, object?>
      {
        ["drawing"] = drawing,
        ["currentLayout"] = currentLayout,
        ["count"] = layouts.Count,
        ["layouts"] = layouts,
      };
  }

  // -------------------------------------------------------------------------
  // plotListPageSetups
  // -------------------------------------------------------------------------

  public static Task<object?> ListPageSetupsAsync()
  {
    return CivilExecution.ReadAsync<object?>((_, _, database, transaction) =>
    {
      var pageSetups = new List<Dictionary<string, object?>>();
      var dictionary = (DBDictionary)transaction.GetObject(database.PlotSettingsDictionaryId, OpenMode.ForRead);
      foreach (DBDictionaryEntry entry in dictionary)
      {
        if (transaction.GetObject(entry.Value, OpenMode.ForRead) is not PlotSettings settings) continue;
        var summary = ToPlotSettingsSummary(settings);
        summary["name"] = entry.Key;
        summary["modelType"] = settings.ModelType;
        pageSetups.Add(summary);
      }

      return new Dictionary<string, object?>
      {
        ["count"] = pageSetups.Count,
        ["pageSetups"] = pageSetups.OrderBy(item => Convert.ToString(item["name"]), StringComparer.OrdinalIgnoreCase).ToList(),
      };
    });
  }

  // -------------------------------------------------------------------------
  // plotListPlotters
  // -------------------------------------------------------------------------

  public static Task<object?> ListPlottersAsync(JsonObject? parameters)
  {
    var device = PluginRuntime.GetOptionalString(parameters, "device");
    return CivilExecution.ReadAsync<object?>((_, _, database, transaction) =>
    {
      var validator = PlotSettingsValidator.Current;
      using var probe = new PlotSettings(false);
      validator.RefreshLists(probe);

      var devices = ToList(validator.GetPlotDeviceList());
      var styleSheets = ToList(validator.GetPlotStyleSheetList());
      List<Dictionary<string, object?>>? media = null;
      string? resolvedDevice = null;

      if (!string.IsNullOrWhiteSpace(device))
      {
        resolvedDevice = RequireDevice(devices, device);
        media = ReadMedia(probe, resolvedDevice)
          .Select(item => new Dictionary<string, object?>
          {
            ["canonicalName"] = item.Canonical,
            ["localeName"] = item.Locale,
          })
          .ToList();
      }

      return new Dictionary<string, object?>
      {
        ["devices"] = devices,
        ["plotStyleTables"] = styleSheets,
        ["defaultPdfDevice"] = DefaultPdfDevice,
        ["pdfDeviceAvailable"] = devices.Any(name => string.Equals(name, DefaultPdfDevice, StringComparison.OrdinalIgnoreCase)),
        ["device"] = resolvedDevice,
        ["media"] = media,
      };
    });
  }

  // -------------------------------------------------------------------------
  // plotLayoutsToPdf
  // -------------------------------------------------------------------------

  public static Task<object?> PlotLayoutsToPdfAsync(JsonObject? parameters)
  {
    var requestedLayouts = GetOptionalStringArray(parameters, "layoutNames");
    var allLayouts = PluginRuntime.GetOptionalBool(parameters, "allLayouts") ?? false;
    var outputDirectory = PluginRuntime.GetOptionalString(parameters, "outputDirectory");
    var outputPath = PluginRuntime.GetOptionalString(parameters, "outputPath");
    var fileNamePrefix = PluginRuntime.GetOptionalString(parameters, "fileNamePrefix");
    var pageSetup = PluginRuntime.GetOptionalString(parameters, "pageSetup");
    var paperSize = PluginRuntime.GetOptionalString(parameters, "paperSize");
    var plotStyleTable = PluginRuntime.GetOptionalString(parameters, "plotStyleTable");
    var orientation = PluginRuntime.GetOptionalString(parameters, "orientation");
    var plotArea = PluginRuntime.GetOptionalString(parameters, "plotArea");
    var windowCorners = GetOptionalDoubleArray(parameters, "windowCorners");
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    var continueOnError = PluginRuntime.GetOptionalBool(parameters, "continueOnError") ?? true;

    if (allLayouts == (requestedLayouts.Count > 0))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Provide either a non-empty 'layoutNames' array or allLayouts=true (not both).");
    }
    if (string.IsNullOrWhiteSpace(outputDirectory) == string.IsNullOrWhiteSpace(outputPath))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Provide exactly one of 'outputDirectory' or 'outputPath'.");
    }
    if (orientation != null && orientation is not ("portrait" or "landscape"))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Parameter 'orientation' must be 'portrait' or 'landscape'.");
    }
    if (plotArea != null && plotArea is not ("layout" or "extents" or "window"))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Parameter 'plotArea' must be 'layout', 'extents' or 'window'.");
    }
    if ((plotArea == "window") != (windowCorners != null))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "plotArea 'window' needs 'windowCorners' [x1, y1, x2, y2], and 'windowCorners' needs plotArea 'window'.");
    }
    if (windowCorners != null && (windowCorners.Length != 4 || windowCorners[0] == windowCorners[2] || windowCorners[1] == windowCorners[3]))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "'windowCorners' must be [x1, y1, x2, y2] with two distinct x and two distinct y values.");
    }

    return CivilExecution.ExecuteCommandSequenceAsync<object?>(async (doc, cancellationToken) =>
    {
      // 1. Resolve and validate every job up front (read-only).
      var jobs = PreparePlotJobs(doc, requestedLayouts, allLayouts, pageSetup, paperSize, plotStyleTable, orientation, plotArea, windowCorners);
      if (outputPath != null && jobs.Count != 1)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'outputPath' plots exactly one layout, but {jobs.Count} layouts were selected. Use 'outputDirectory' instead.");
      }

      // 2. Resolve every output path through the filesystem boundary before
      //    anything is plotted, so a rejected path cannot leave partial output.
      var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var drawingBase = SanitizeFileName(Path.GetFileNameWithoutExtension(doc.Name));
      foreach (var job in jobs)
      {
        var raw = outputPath ?? Path.Combine(
          outputDirectory!,
          UniqueFileName(usedNames, $"{fileNamePrefix ?? drawingBase + "-"}{SanitizeFileName(job.LayoutName)}") + ".pdf");
        job.OutputPath = FileBoundary.ResolveExportPath(raw, overwrite, ".pdf");
      }
      var duplicate = jobs.GroupBy(job => job.OutputPath, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
      if (duplicate != null)
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Two layouts resolve to the same output file: {duplicate.Key}");
      }

      // 3. Plot, one -PLOT per layout, with sysvars saved and restored.
      var results = new List<Dictionary<string, object?>>();
      var warnings = new List<string>();
      var stopReason = (string?)null;
      var overall = Stopwatch.StartNew();

      using (var sysvars = new SystemVariableScope())
      {
        sysvars.Set("FILEDIA", 0);
        sysvars.Set("CMDECHO", 0);
        sysvars.Set("BACKGROUNDPLOT", 0);
        sysvars.Remember("CTAB");

        // Before the first CTAB change: the layout current now is the one whose
        // viewports may have been erased and re-created without a regen, and a
        // layout switch would be the first thing to touch those stale graphics.
        // The per-layout regen in PlotOneAsync still covers the layouts switched to.
        try
        {
          await RegenerateBeforePlotAsync(doc);
        }
        catch (JsonRpcDispatchException ex) when (ex.Data.Contains(RegenFailedKey))
        {
          stopReason = ex.Message;
        }

        foreach (var job in jobs)
        {
          if (stopReason != null)
          {
            results.Add(LayoutResult(job, "skipped", null, stopReason, 0));
            continue;
          }
          if (cancellationToken.IsCancellationRequested)
          {
            stopReason = "Cancellation was requested; remaining layouts were not plotted.";
            results.Add(LayoutResult(job, "skipped", null, stopReason, 0));
            continue;
          }

          var watch = Stopwatch.StartNew();
          try
          {
            var file = await PlotOneAsync(doc, job, overwrite);
            results.Add(LayoutResult(job, "plotted", file, null, watch.ElapsedMilliseconds));
          }
          catch (Exception ex)
          {
            var message = ex is JsonRpcDispatchException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            results.Add(LayoutResult(job, "failed", null, message, watch.ElapsedMilliseconds));
            if (ex is JsonRpcDispatchException { Code: CommandIncompleteCode })
            {
              stopReason = "An earlier -PLOT did not complete; remaining layouts were not plotted.";
            }
            else if (ex.Data.Contains(RegenFailedKey))
            {
              stopReason = "REGENALL failed before an earlier layout; remaining layouts were not plotted.";
            }
            else if (!continueOnError)
            {
              stopReason = "Stopped after the first failure (continueOnError=false).";
            }
          }
        }

        warnings.AddRange(sysvars.RestoreWarnings());
      }

      var plotted = results.Count(item => Equals(item["status"], "plotted"));
      if (plotted == 0)
      {
        var firstError = results.Select(item => item["error"] as string).FirstOrDefault(error => error != null);
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"No layouts were plotted. {firstError}".Trim());
      }
      if (plotted < results.Count)
      {
        warnings.Add($"{results.Count - plotted} of {results.Count} layouts were not plotted; see per-layout status.");
      }

      return new Dictionary<string, object?>
      {
        ["device"] = DefaultPdfDevice,
        ["method"] = "-PLOT (BACKGROUNDPLOT=0)",
        ["regenerated"] = jobs.Any(job => job.Regenerated),
        ["requested"] = results.Count,
        ["plotted"] = plotted,
        ["failed"] = results.Count(item => Equals(item["status"], "failed")),
        ["skipped"] = results.Count(item => Equals(item["status"], "skipped")),
        ["totalBytes"] = results.Sum(item => item["bytes"] is long bytes ? bytes : 0L),
        ["durationMs"] = overall.ElapsedMilliseconds,
        ["layouts"] = results,
        ["warnings"] = warnings,
      };
    });
  }

  // -------------------------------------------------------------------------
  // plotPublishSheetSet
  // -------------------------------------------------------------------------

  public static Task<object?> PublishSheetSetAsync(JsonObject? parameters)
  {
    var outputPathRaw = PluginRuntime.GetRequiredString(parameters, "outputPath");
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    var requireSaved = PluginRuntime.GetOptionalBool(parameters, "requireSaved") ?? true;
    var keepDsd = PluginRuntime.GetOptionalBool(parameters, "keepDsd") ?? false;
    var document = PluginRuntime.GetOptionalString(parameters, "document");
    var layoutNames = GetOptionalStringArray(parameters, "layoutNames");
    var sheetsNode = parameters?["sheets"] as JsonArray;

    if (layoutNames.Count > 0 && sheetsNode is { Count: > 0 })
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Provide 'layoutNames' or 'sheets', not both.");
    }

    var requestedSheets = new List<(string Layout, string? DrawingPath)>();
    foreach (var name in layoutNames) requestedSheets.Add((name, null));
    if (sheetsNode != null)
    {
      foreach (var node in sheetsNode)
      {
        if (node is not JsonObject sheet)
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each entry in 'sheets' must be an object with 'layoutName' and optional 'drawingPath'.");
        }
        requestedSheets.Add((
          PluginRuntime.GetRequiredString(sheet, "layoutName"),
          PluginRuntime.GetOptionalString(sheet, "drawingPath")));
      }
    }

    return CivilExecution.ExecuteCommandSequenceAsync<object?>(async (doc, cancellationToken) =>
    {
      var warnings = new List<string>();
      Document? source = doc;
      string currentPath;
      if (document != null)
      {
        var open = App.DocumentManager.Cast<Document>().ToArray();
        var match = RunCodeHelpers.MatchDocument(document, open.Select(item => item.Name).ToArray());
        if (match.Error == null)
          source = open[match.Index];
        else
        {
          if (!Path.IsPathFullyQualified(document))
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", match.Error);
          source = null;
        }
      }
      currentPath = source?.Database.Filename ?? FileBoundary.ResolveImportPath(document!, ".dwg");

      // PUBLISH reads the saved DWG. With requireSaved=false, selection and
      // initialization must use that same revision, including renamed tabs.
      var usesCurrentSource = requestedSheets.Count == 0 || requestedSheets.Any(sheet =>
        string.IsNullOrWhiteSpace(sheet.DrawingPath)
        || string.Equals(FileBoundary.ResolveImportPath(sheet.DrawingPath, ".dwg"), currentPath, StringComparison.OrdinalIgnoreCase));
      var currentLayouts = new List<LayoutSnapshot>();
      if (usesCurrentSource)
      {
        if (source != null)
        {
          using var documentLock = source.LockDocument();
          CheckSavedSource(source, requireSaved, warnings);
        }
        currentLayouts = (source != null && requireSaved ? ReadLayoutSnapshots(source) : ReadLayoutSnapshots(currentPath))
          .Where(layout => !layout.ModelType).ToList();
      }
      var sheets = new List<PublishSheet>();
      if (requestedSheets.Count == 0)
      {
        // Every paper-space tab is part of the requested set. Refuse an
        // uninitialized tab before making or replacing any output file.
        sheets.AddRange(currentLayouts.OrderBy(layout => layout.TabOrder).Select(layout => new PublishSheet(layout.Name, null)));
        var uninitialized = currentLayouts.Where(layout => !layout.Initialized).OrderBy(layout => layout.TabOrder).Select(layout => layout.Name).ToList();
        if (uninitialized.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Uninitialized layouts: {Preview(uninitialized)}. Open each layout tab once and save before publishing.");
        if (sheets.Count == 0)
        {
          throw uninitialized.Count > 0
            ? new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"None of the drawing's paper-space layouts have been initialized ({Preview(uninitialized)}). Open each layout tab once and save the drawing before publishing.")
            : new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "The source drawing has no paper-space layouts to publish.");
        }
      }
      else
      {
        foreach (var (layout, drawingPath) in requestedSheets)
        {
          if (string.IsNullOrWhiteSpace(drawingPath))
          {
            var match = currentLayouts.FirstOrDefault(item => string.Equals(item.Name, layout, StringComparison.OrdinalIgnoreCase))
              ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", LayoutNotFoundMessage(layout, currentLayouts));
            RequireInitialized(match);
            sheets.Add(new PublishSheet(match.Name, null));
          }
          else
          {
            var resolved = FileBoundary.ResolveImportPath(drawingPath, ".dwg");
            if (string.Equals(resolved, currentPath, StringComparison.OrdinalIgnoreCase))
            {
              var match = currentLayouts.FirstOrDefault(item => string.Equals(item.Name, layout, StringComparison.OrdinalIgnoreCase))
                ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", LayoutNotFoundMessage(layout, currentLayouts));
              RequireInitialized(match);
              sheets.Add(new PublishSheet(match.Name, null));
            }
            else
            {
              sheets.Add(new PublishSheet(layout, resolved));
            }
          }
        }
      }

      // Validate the complete sheet set, including saved closed sources, before
      // BeginExternalWrite moves an existing PDF to its protected backup.
      var settings = PreflightPublishSheets(source, sheets, currentPath, requireSaved, warnings);

      if (sheets.Any(sheet => sheet.DrawingPath == null))
      {
        // PUBLISH reads the drawing from disk, so an untitled drawing cannot be
        // published and unsaved edits may be missing from the PDF.
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
        {
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", "The source drawing has never been saved. Save it before publishing its layouts.");
        }
      }

      var outputPath = FileBoundary.ResolveExportPath(outputPathRaw, overwrite, ".pdf");
      var dsdPath = FileBoundary.ResolveExportPath(Path.ChangeExtension(outputPath, ".mcp-publish.dsd"), true, ".dsd");

      // Same stale-viewport crash guard as -PLOT (issue #44), for sheets taken
      // from the open drawing. Before BeginExternalWrite, so a failed regen never
      // moves an existing PDF. Sheets from closed drawings are read from disk
      // and have no in-session graphics. Commands only run in the active
      // drawing, so an open source that is not the active one cannot be
      // regenerated here; say so instead of claiming it.
      var regenerated = false;
      if (source != null && sheets.Any(sheet => sheet.DrawingPath == null))
      {
        if (ReferenceEquals(source, doc))
        {
          await RegenerateBeforePlotAsync(doc);
          regenerated = true;
        }
        else
        {
          warnings.Add("The source drawing is open but not the active drawing, so its viewports were not regenerated before publishing.");
        }
      }

      var watch = Stopwatch.StartNew();
      var startedUtc = DateTime.UtcNow;
      OutputFile file;
      try
      {
        // PUBLISH writes the final path (the DSD's DWF= target) itself, so
        // "open in viewer when done" opens the real PDF. BeginExternalWrite
        // locks the directory chain, refuses a link at the final name and
        // moves an existing PDF (overwrite only) to a backup; Commit checks
        // the result is a regular file before it is read, and a failed
        // publish restores the previous PDF. See FileBoundary.BeginExternalWrite.
        using var output = FileBoundary.BeginExternalWrite(outputPath, overwrite);

        // The DSD sits beside the PDF, so it is written under the same lock.
        // Its DWF= line decides where -PUBLISH writes, so right before the
        // command it is re-read from a handle that does not follow links and
        // must still be exactly what was written (DWF= is the final path);
        // that handle stays open, denying writes, deletes and renames, until
        // -PUBLISH has read it.
        var encoding = new UTF8Encoding(false);
        var dsd = BuildDsd(sheets, currentPath, output.FinalPath);
        FileBoundary.WriteAllTextAtomic(dsdPath, dsd, encoding, true, ".dsd");
        using (var sysvars = new SystemVariableScope())
        {
          sysvars.Set("FILEDIA", 0);
          sysvars.Set("CMDECHO", 0);
          sysvars.Set("BACKGROUNDPLOT", 0);
          sysvars.Set("PUBLISHCOLLATE", 1);

          cancellationToken.ThrowIfCancellationRequested();
          using (FileBoundary.HoldVerifiedFile(dsdPath, dsd, encoding))
          {
            await RunCommandAsync(doc, "PUBLISH", "_.-PUBLISH", dsdPath);
          }
          warnings.AddRange(sysvars.RestoreWarnings());
        }

        file = output.Commit((path, stream) => VerifyPublishedOutput(path, stream, startedUtc, settings));
      }
      finally
      {
        if (!keepDsd)
        {
          TryDelete(dsdPath);
        }
      }
      if (file.PageCount == null)
        warnings.Add("The PDF uses compressed object streams; page count could not be verified.");

      return new Dictionary<string, object?>
      {
        ["outputPath"] = file.Path,
        ["bytes"] = file.Bytes,
        ["pageCount"] = file.PageCount,
        ["pages"] = file.Pages?.Select((page, index) => new Dictionary<string, object?>
        {
          ["index"] = index + 1,
          ["mediaBoxPt"] = page.MediaBoxPt,
          ["rotation"] = page.Rotation,
          ["userUnit"] = page.UserUnit,
          ["widthMm"] = page.WidthMm,
          ["heightMm"] = page.HeightMm,
        }).ToList(),
        ["lastWriteUtc"] = file.LastWriteUtc,
        ["sheetCount"] = sheets.Count,
        ["sheets"] = sheets.Select((sheet, index) => new Dictionary<string, object?>
        {
          ["index"] = index + 1,
          ["layoutName"] = sheet.LayoutName,
          ["drawingPath"] = sheet.DrawingPath ?? currentPath,
          ["pageSetup"] = settings[index].PageSetup,
          ["device"] = settings[index].Device,
          ["media"] = settings[index].Media,
          ["plotStyleTable"] = settings[index].PlotStyleTable,
          ["paperSizeMm"] = new[] { settings[index].WidthMm, settings[index].HeightMm },
        }).ToList(),
        ["method"] = "-PUBLISH with generated DSD (BACKGROUNDPLOT=0, multi-sheet PDF)",
        ["dsdPath"] = keepDsd ? dsdPath : null,
        ["durationMs"] = watch.ElapsedMilliseconds,
        ["published"] = true,
        ["regenerated"] = regenerated,
        ["verified"] = file.PageCount != null,
        ["verification"] = file.PageCount != null ? "verified" : "unverified",
        ["warnings"] = warnings,
      };
    }, drainOnCancellation: true);
  }

  // =========================================================================
  // Plot preparation (read-only)
  // =========================================================================

  private sealed class PlotJob
  {
    public required string LayoutName { get; init; }
    public required string PaperSize { get; init; }
    public required string PaperUnitsToken { get; init; }
    public required string OrientationToken { get; init; }
    public required bool UpsideDown { get; init; }
    public required string StyleSheetToken { get; init; }
    public required bool IsModel { get; init; }
    public required string PlotAreaToken { get; init; }
    public string[]? WindowCorners { get; init; }
    public string? PageSetup { get; init; }
    public string? OutputPath { get; set; }
    public DateTime StartedUtc { get; set; }
    public bool Regenerated { get; set; }
  }

  private sealed record PublishSheet(string LayoutName, string? DrawingPath);

  private sealed record PublishSettings(string? PageSetup, string Device, string Media,
    string? PlotStyleTable, double WidthMm, double HeightMm);

  private static List<PublishSettings> PreflightPublishSheets(Document? defaultSource, IReadOnlyList<PublishSheet> sheets,
    string currentPath, bool requireSaved, List<string> warnings)
  {
    var validator = PlotSettingsValidator.Current;
    using var probe = new PlotSettings(false);
    validator.RefreshLists(probe);
    var devices = ToList(validator.GetPlotDeviceList());
    var styles = ToList(validator.GetPlotStyleSheetList());
    RequireDevice(devices, DefaultPdfDevice);
    var media = ReadMedia(probe, DefaultPdfDevice);
    var results = new List<PublishSettings>(sheets.Count);
    foreach (var sheet in sheets)
    {
      var sourcePath = sheet.DrawingPath ?? currentPath;
      var open = sheet.DrawingPath == null ? defaultSource : App.DocumentManager.Cast<Document>().FirstOrDefault(item =>
        Path.IsPathFullyQualified(item.Name)
        && string.Equals(item.Name, sourcePath, StringComparison.OrdinalIgnoreCase));
      if (open != null)
      {
        using var documentLock = open.LockDocument();
        CheckSavedSource(open, requireSaved, warnings);
        if (requireSaved)
        {
          using var transaction = open.Database.TransactionManager.StartOpenCloseTransaction();
          results.Add(ValidatePublishLayout(open.Database, transaction, sheet, sourcePath, devices, styles, media));
          continue;
        }
      }
      {
        // Read the original saved drawing in its own database so relative
        // references retain their source context. Do not activate or save it.
        // Unsaved page setups must not affect saved-revision preflight.
        using var database = new Database(false, true);
        database.ReadDwgFile(sourcePath, FileOpenMode.OpenForReadAndAllShare, true, "");
        database.CloseInput(true);
        using var transaction = database.TransactionManager.StartOpenCloseTransaction();
        results.Add(ValidatePublishLayout(database, transaction, sheet, sourcePath, devices, styles, media));
      }
    }
    return results;
  }

  private static void CheckSavedSource(Document source, bool requireSaved, List<string> warnings)
  {
    if (!Path.IsPathFullyQualified(source.Name) || !File.Exists(source.Name))
      throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Drawing '{source.Name}' has never been saved. Save it before publishing.");
    bool saved;
    try
    {
      var acadDocument = source.GetAcadDocument();
      var value = Civil3DCompatibility.GetComProperty(acadDocument, "Saved");
      saved = Convert.ToBoolean(value);
    }
    catch (Exception error)
    {
      throw new JsonRpcDispatchException("CIVIL3D.HOST_BUSY", $"Could not determine whether '{source.Name}' has unsaved changes: {error.Message}");
    }
    if (saved) return;
    if (requireSaved)
      throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Drawing '{source.Name}' has unsaved changes. Save it first, or pass requireSaved=false to publish its saved revision.");
    var warning = $"Drawing '{source.Name}' has unsaved changes; PUBLISH uses its saved revision. Unsaved changes were not included.";
    if (!warnings.Contains(warning, StringComparer.OrdinalIgnoreCase)) warnings.Add(warning);
  }

  private static PublishSettings ValidatePublishLayout(Database database, Transaction transaction,
    PublishSheet sheet, string sourcePath, IReadOnlyList<string> devices, IReadOnlyList<string> styles,
    IReadOnlyList<MediaName> media)
  {
    var layout = ReadLayouts(database, transaction).FirstOrDefault(item =>
      string.Equals(item.LayoutName, sheet.LayoutName, StringComparison.OrdinalIgnoreCase));
    if (layout == null || layout.ModelType)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Paper-space layout '{sheet.LayoutName}' was not found in '{sourcePath}'.");
    RequireInitialized(new LayoutSnapshot(layout.LayoutName, layout.TabOrder, false, IsInitialized(layout, transaction)));

    var device = layout.PlotConfigurationName;
    if (string.IsNullOrWhiteSpace(device))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Layout '{sheet.LayoutName}' in '{sourcePath}' has no plot device.");
    RequireDevice(devices, device);

    var style = NullIfEmpty(layout.CurrentStyleSheet);
    if (style == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Layout '{sheet.LayoutName}' in '{sourcePath}' has no CTB/STB selected.");
    if (style != null && !styles.Contains(style, StringComparer.OrdinalIgnoreCase))
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Plot style table '{style}' for layout '{sheet.LayoutName}' in '{sourcePath}' was not found.");
    var canonical = layout.CanonicalMediaName;
    if (string.IsNullOrWhiteSpace(canonical) || !media.Any(item => string.Equals(item.Canonical, canonical, StringComparison.OrdinalIgnoreCase)))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Media '{canonical}' for layout '{sheet.LayoutName}' in '{sourcePath}' is not available on {DefaultPdfDevice}.");
    var size = layout.PlotPaperSize;
    if (size.X <= 0 || size.Y <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Layout '{sheet.LayoutName}' in '{sourcePath}' has no valid paper size.");
    return new PublishSettings(NullIfEmpty(layout.PlotSettingsName), device, canonical, style,
      size.X, size.Y);
  }

  private sealed record LayoutSnapshot(string Name, int TabOrder, bool ModelType, bool Initialized = true);

  private static void RequireInitialized(LayoutSnapshot layout)
  {
    if (!layout.Initialized)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Layout '{layout.Name}' has never been initialized, and PUBLISH refuses uninitialized layouts. Open the layout tab once and save the drawing, then publish again.");
    }
  }

  // The cached viewport IDs can be empty after reopening a saved drawing.
  // The paper-space record still contains its saved viewport entities.
  private static bool IsInitialized(Layout layout, Transaction transaction)
  {
    if (layout.ModelType)
    {
      return true;
    }

    try
    {
      if (layout.GetViewports().Count > 0)
        return true;
    }
    catch
    {
      // Continue with the saved paper-space record when the cache is unreadable.
    }

    try
    {
      var paperSpace = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
      foreach (ObjectId entityId in paperSpace)
      {
        if (transaction.GetObject(entityId, OpenMode.ForRead) is Viewport)
          return true;
      }
    }
    catch
    {
      // Neither source established a viewport; keep the preflight fail-closed.
    }
    return false;
  }

  private sealed record MediaName(string Canonical, string Locale);

  private sealed record OutputFile(string Path, long Bytes, int? PageCount, DateTime LastWriteUtc,
    IReadOnlyList<PdfOutputInspector.Page>? Pages = null);

  private static List<PlotJob> PreparePlotJobs(
    Document doc,
    IReadOnlyList<string> requestedLayouts,
    bool allLayouts,
    string? pageSetup,
    string? paperSize,
    string? plotStyleTable,
    string? orientation,
    string? plotArea,
    double[]? windowCorners)
  {
    var database = doc.Database;
    using var documentLock = doc.LockDocument();
    using var transaction = database.TransactionManager.StartTransaction();

    var layouts = ReadLayouts(database, transaction).ToList();
    var paperLayouts = layouts.Where(layout => !layout.ModelType).OrderBy(layout => layout.TabOrder).ToList();

    List<Layout> selected;
    if (allLayouts)
    {
      selected = paperLayouts;
      if (selected.Count == 0)
      {
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "The active drawing has no paper-space layouts to plot.");
      }
    }
    else
    {
      selected = [];
      foreach (var name in requestedLayouts)
      {
        var layout = layouts.FirstOrDefault(item => string.Equals(item.LayoutName, name, StringComparison.OrdinalIgnoreCase))
          ?? throw new JsonRpcDispatchException(
            "CIVIL3D.OBJECT_NOT_FOUND",
            LayoutNotFoundMessage(name, paperLayouts.Select(item => new LayoutSnapshot(item.LayoutName, item.TabOrder, false))));
        if (layout.ModelType && plotArea == "layout")
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The Model tab has no layout plot area; use plotArea 'extents' (the default for Model) or 'window'.");
        }
        if (!selected.Contains(layout)) selected.Add(layout);
      }
    }

    PlotSettings? namedSetup = null;
    if (!string.IsNullOrWhiteSpace(pageSetup))
    {
      var dictionary = (DBDictionary)transaction.GetObject(database.PlotSettingsDictionaryId, OpenMode.ForRead);
      foreach (DBDictionaryEntry entry in dictionary)
      {
        if (!string.Equals(entry.Key, pageSetup, StringComparison.OrdinalIgnoreCase)) continue;
        namedSetup = transaction.GetObject(entry.Value, OpenMode.ForRead) as PlotSettings;
        break;
      }
      if (namedSetup == null)
      {
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Page setup '{pageSetup}' was not found. Use civil3d_plot action=list_page_setups.");
      }
      if (selected.Any(layout => layout.ModelType != namedSetup.ModelType))
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          namedSetup.ModelType
            ? $"Page setup '{pageSetup}' is a model-space page setup and cannot be applied to a paper-space layout."
            : $"Page setup '{pageSetup}' is a paper-space page setup and cannot be applied to the Model tab.");
      }
    }

    var validator = PlotSettingsValidator.Current;
    using var probe = new PlotSettings(false);
    validator.RefreshLists(probe);
    var devices = ToList(validator.GetPlotDeviceList());
    RequireDevice(devices, DefaultPdfDevice);
    var styleSheets = ToList(validator.GetPlotStyleSheetList());
    if (!string.IsNullOrWhiteSpace(plotStyleTable) &&
        !styleSheets.Any(name => string.Equals(name, plotStyleTable, StringComparison.OrdinalIgnoreCase)))
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Plot style table '{plotStyleTable}' was not found. Available: {Preview(styleSheets)}.");
    }

    var pdfMedia = ReadMedia(probe, DefaultPdfDevice);
    var jobs = new List<PlotJob>();
    foreach (var layout in selected)
    {
      PlotSettings source = namedSetup ?? layout;
      var paper = ResolvePaper(pdfMedia, paperSize, source.CanonicalMediaName, layout.LayoutName);
      var rotation = source.PlotRotation;
      var landscape = orientation != null ? orientation == "landscape" : IsLandscape(source);
      var styleSheet = plotStyleTable ?? source.CurrentStyleSheet;

      jobs.Add(new PlotJob
      {
        LayoutName = layout.LayoutName,
        PageSetup = namedSetup != null ? pageSetup : NullIfEmpty(layout.PlotSettingsName),
        PaperSize = paper,
        PaperUnitsToken = source.PlotPaperUnits == PlotPaperUnit.Millimeters ? "_M" : "_I",
        OrientationToken = landscape ? "_L" : "_P",
        UpsideDown = rotation is PlotRotation.Degrees180 or PlotRotation.Degrees270,
        // "." answers "Enter plot style table name ... (enter . for none)".
        StyleSheetToken = string.IsNullOrWhiteSpace(styleSheet) ? "." : styleSheet,
        IsModel = layout.ModelType,
        PlotAreaToken = (plotArea ?? (layout.ModelType ? "extents" : "layout")) switch
        {
          "extents" => "_E",
          "window" => "_W",
          _ => "_L",
        },
        // Plain invariant decimals: the corner prompts do not accept exponents.
        WindowCorners = windowCorners == null
          ? null
          : [FormatPoint(windowCorners[0], windowCorners[1]), FormatPoint(windowCorners[2], windowCorners[3])],
      });
    }

    transaction.Commit();
    return jobs;
  }

  private static string ResolvePaper(IReadOnlyList<MediaName> pdfMedia, string? requested, string? sourceCanonical, string layoutName)
  {
    if (!string.IsNullOrWhiteSpace(requested))
    {
      var match = pdfMedia.FirstOrDefault(item =>
        string.Equals(item.Locale, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(item.Canonical, requested, StringComparison.OrdinalIgnoreCase));
      return match?.Locale ?? throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Paper size '{requested}' is not available on {DefaultPdfDevice}. Use civil3d_plot action=list_plotters with device='{DefaultPdfDevice}' to list media. Examples: {Preview(pdfMedia.Select(item => item.Locale))}.");
    }

    var fromLayout = pdfMedia.FirstOrDefault(item => string.Equals(item.Canonical, sourceCanonical, StringComparison.OrdinalIgnoreCase));
    return fromLayout?.Locale ?? throw new JsonRpcDispatchException(
      "CIVIL3D.INVALID_INPUT",
      $"Layout '{layoutName}' uses paper '{sourceCanonical}', which {DefaultPdfDevice} does not provide. Pass 'paperSize' (for example '{pdfMedia.FirstOrDefault()?.Locale}') or a PDF page setup.");
  }

  // =========================================================================
  // Command execution
  // =========================================================================

  // A command left waiting for input is reported with this code so a batch
  // stops instead of feeding the next layout's answers into a stale prompt.
  private const string CommandIncompleteCode = "CIVIL3D.COMMAND_FAILED";

  // Regenerates every viewport of the drawing before it is plotted. Viewports
  // erased and re-created without a regen left stale graphics behind, and the
  // next DWG To PDF plot killed acad.exe (c0000005 in ntdll, issue #44);
  // REGENALL before rendering avoided it. REGENALL takes no prompts, so it runs
  // through the same command runner as -PLOT, inside the host command context.
  // A failed regen stops the plot: plotting on stale graphics is the crash.
  private static async Task RegenerateBeforePlotAsync(Document doc)
  {
    try
    {
      await RunCommandAsync(doc, "REGENALL", "_.REGENALL");
    }
    catch (Exception ex)
    {
      var failure = new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"REGENALL failed before plotting ({ex.Message}). Nothing was plotted: plotting on stale viewport graphics can crash Civil 3D. Run REGENALL in the drawing and retry.");
      failure.Data[RegenFailedKey] = true;
      throw failure;
    }
  }

  // Marks the exception RegenerateBeforePlotAsync throws, so a batch stops
  // instead of plotting the next layouts on the same stale graphics.
  private const string RegenFailedKey = "RegenFailed";

  private static async Task<OutputFile> PlotOneAsync(Document doc, PlotJob job, bool overwrite)
  {
    // Matches lisp/plot.lsp: make the layout current first so the layout-name
    // prompt default is the target, then answer the verified chain.
    App.SetSystemVariable("CTAB", job.LayoutName);

    // After CTAB, so the layout about to be plotted is the one regenerated, and
    // before BeginExternalWrite, so a failed regen never moves an existing PDF.
    await RegenerateBeforePlotAsync(doc);
    job.Regenerated = true;

    // The plotter writes the final path itself, so the device's "open in
    // viewer when done" option opens the real PDF. BeginExternalWrite locks
    // the output directory chain, refuses a link at the final name and moves
    // an existing PDF (overwrite only) to a backup; Commit checks the result
    // is a regular file before it is read, and a failed plot restores the
    // previous PDF. See FileBoundary.BeginExternalWrite.
    using var output = FileBoundary.BeginExternalWrite(job.OutputPath!, overwrite);
    job.StartedUtc = DateTime.UtcNow;

    // The prompt chains, recorded on Civil 3D 2027. A paper-space layout, any
    // plot area: area [+ two window corners], scale, offset, plot styles?,
    // style table, lineweights?, scale lineweights?, paper space first?, hide
    // paperspace objects?, file, save page setup?, proceed? The Model tab asks
    // the same up to lineweights?, then a shade plot setting instead of the
    // three paper-space questions.
    var tokens = new List<object>
    {
      "_.-PLOT",
      "_Y",                         // Detailed plot configuration?
      job.LayoutName,               // Layout name ("Model" for the Model tab)
      DefaultPdfDevice,             // Output device
      job.PaperSize,                // Paper size (locale name for the device)
      job.PaperUnitsToken,          // Paper units
      job.OrientationToken,         // Drawing orientation
      job.UpsideDown ? "_Y" : "_N", // Plot upside down?
      job.PlotAreaToken,            // Plot area: Layout, Extents or Window
    };
    if (job.WindowCorners != null)
    {
      tokens.Add(job.WindowCorners[0]); // Lower left corner of window
      tokens.Add(job.WindowCorners[1]); // Upper right corner of window
    }
    if (job.IsModel)
    {
      tokens.Add("_F");             // Plot scale: fit the area to the paper
      tokens.Add("_C");             // Plot offset: centred
    }
    else
    {
      tokens.Add("1:1");            // Plot scale (viewports carry the drawing scale)
      tokens.Add("0.00,0.00");      // Plot offset
    }
    tokens.Add("_Y");                // Plot with plot styles?
    tokens.Add(job.StyleSheetToken); // Plot style table name ("." = none)
    tokens.Add("_Y");                // Plot with lineweights?
    if (job.IsModel)
    {
      tokens.Add("_A");             // Shade plot setting: As displayed
    }
    else
    {
      tokens.Add("_N");             // Scale lineweights with plot scale?
      tokens.Add("_N");             // Plot paper space first?
      tokens.Add("_N");             // Hide paperspace objects?
    }
    tokens.Add(output.FinalPath);    // File name (PDF devices ask directly)
    tokens.Add("_N");                // Save changes to page setup?
    tokens.Add("_Y");                // Proceed with plot?

    await RunCommandAsync(doc, "PLOT", tokens.ToArray());

    return output.Commit((path, stream) => VerifyOutput(path, stream, job.StartedUtc));
  }

  // Invocations RunCommandAsync started that have not been seen to finish: a
  // command still at a prompt when its tokens ran out (a cancel was queued) or
  // one whose CommandAsync failed while the command was still active. A normal
  // completion is removed at once, so a PLOT/PUBLISH/XREF the user starts later
  // is never mistaken for a stale request. Only the UI thread runs commands,
  // but the list is locked so a stray caller cannot corrupt it.
  private static readonly List<(Document Doc, string Command)> PendingInvocations = new();

  internal static async Task RunCommandAsync(Document doc, string commandName, params object[] tokens)
  {
    // Editor.CommandAsync completes once its tokens are consumed, even when the
    // command is still waiting for more input (the acedCmdC coroutine model),
    // so a stuck command is detected below and a cancel is queued. That cancel
    // only runs after the host work returns, so check here that no command an
    // earlier request left at a prompt in this document is still there before
    // feeding it this request's answers.
    var stale = FindActiveCommand(doc, PendingCommandsFor(doc));
    if (stale != null)
    {
      var stalePrompt = Convert.ToString(App.GetSystemVariable("LASTPROMPT"));
      CivilExecution.QueueCancelAfterOperation();
      throw new JsonRpcDispatchException(
        CommandIncompleteCode,
        $"-{stale} from an earlier request is still waiting at prompt '{stalePrompt}', so -{commandName} was not started. A cancel was queued; retry the request.");
    }

    var invocation = (doc, commandName);
    lock (PendingInvocations)
    {
      PendingInvocations.Add(invocation);
    }

    var stillActive = true;
    try
    {
      await doc.Editor.CommandAsync(tokens);
    }
    finally
    {
      // Forget the invocation once the command is no longer active (normal
      // completion, or a failure that ended it); keep it only while it may
      // still be at a prompt. If another drawing became active, CMDNAMES says
      // nothing about this one: keep the entry, and the next request in this
      // drawing checks it again.
      var active = ActiveCommandNames(doc);
      stillActive = active != null && active.Contains(commandName, StringComparer.OrdinalIgnoreCase);
      if (active != null && !stillActive)
      {
        lock (PendingInvocations)
        {
          PendingInvocations.Remove(invocation);
        }
      }
    }

    // A token the command did not expect (renamed prompt in a future release,
    // unexpected paper-size dialog, ...) leaves the command waiting for input.
    // Detect that instead of reporting success, record the pending prompt for
    // diagnosis, and queue a cancel so the editor is usable again.
    if (stillActive)
    {
      var lastPrompt = Convert.ToString(App.GetSystemVariable("LASTPROMPT"));
      CivilExecution.QueueCancelAfterOperation();
      throw new JsonRpcDispatchException(
        CommandIncompleteCode,
        $"-{commandName} did not complete; it was waiting at prompt '{lastPrompt}'. A cancel was queued. The prompt chain may differ in this Civil 3D release.");
    }
  }

  // Commands this runner left unfinished in <paramref name="doc"/>. Entries
  // whose command is no longer active (the queued cancel ran) are dropped, and
  // so are entries for documents that have been closed. Entries are only
  // dropped as finished while <paramref name="doc"/> is the active drawing,
  // because CMDNAMES describes the active drawing only.
  private static string[] PendingCommandsFor(Document doc)
  {
    var active = ActiveCommandNames(doc);
    var open = App.DocumentManager.Cast<Document>().ToList();
    lock (PendingInvocations)
    {
      PendingInvocations.RemoveAll(entry =>
        !open.Contains(entry.Doc)
        || (active != null && ReferenceEquals(entry.Doc, doc) && !active.Contains(entry.Command, StringComparer.OrdinalIgnoreCase)));
      return PendingInvocations
        .Where(entry => ReferenceEquals(entry.Doc, doc))
        .Select(entry => entry.Command)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }
  }

  // The commands active in <paramref name="doc"/>, or null when it is not the
  // active drawing (CMDNAMES is only known for the active drawing).
  private static string[]? ActiveCommandNames(Document doc)
  {
    if (!ReferenceEquals(App.DocumentManager.MdiActiveDocument, doc))
    {
      return null;
    }

    var activeCommands = Convert.ToString(App.GetSystemVariable("CMDNAMES")) ?? string.Empty;
    return activeCommands
      .Split('\'')
      .Select(name => name.TrimStart('-', '_', '.'))
      .Where(name => name.Length > 0)
      .ToArray();
  }

  private static string? FindActiveCommand(Document doc, IReadOnlyCollection<string> commandNames)
  {
    if (commandNames.Count == 0)
    {
      return null;
    }

    return ActiveCommandNames(doc)
      ?.FirstOrDefault(name => commandNames.Contains(name, StringComparer.OrdinalIgnoreCase));
  }

  // Reads the output only through the stream Commit opened on the checked
  // handle; the path is used for messages and the result only.
  private static OutputFile VerifyOutput(string path, FileStream? stream, DateTime startedUtc)
  {
    if (stream == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"The plot command finished but no PDF was written to '{path}'.");
    }
    var lastWriteUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
    // Allow for coarse filesystem timestamps.
    if (lastWriteUtc < startedUtc.AddSeconds(-2))
    {
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"'{path}' exists but was not written by this plot run.");
    }
    var length = stream.Length;
    if (length == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"'{path}' was written but is empty.");
    }

    return new OutputFile(path, length, CountPdfPages(stream), lastWriteUtc);
  }

  private static OutputFile VerifyPublishedOutput(string path, FileStream? stream, DateTime startedUtc,
    IReadOnlyList<PublishSettings> settings)
  {
    var file = VerifyOutput(path, stream, startedUtc);
    if (stream == null || stream.Length > PageCountReadLimitBytes)
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"PDF '{path}' is too large to verify safely.");
    PdfOutputInspector.Inspection inspection;
    try { inspection = PdfOutputInspector.Inspect(stream); }
    catch (Exception error) when (error is InvalidDataException or FormatException or OverflowException)
    {
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"PDF '{path}' failed structure verification: {error.Message}");
    }
    try
    {
      if (!PdfOutputInspector.Validate(inspection, settings.Select(item => (item.WidthMm, item.HeightMm)).ToArray()))
        return file with { PageCount = null };
    }
    catch (InvalidDataException error)
    {
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"PDF '{path}' failed page verification: {error.Message}");
    }
    return file with { PageCount = inspection.Pages.Count, Pages = inspection.Pages };
  }

  // Heuristic: counts uncompressed /Type /Page objects. Returns null when the
  // file is too large to scan or uses compressed object streams (count 0).
  private static int? CountPdfPages(FileStream stream)
  {
    try
    {
      if (stream.Length > PageCountReadLimitBytes) return null;
      var bytes = new byte[stream.Length];
      stream.Position = 0;
      stream.ReadExactly(bytes);
      var text = Encoding.Latin1.GetString(bytes);
      var count = PdfPageObject.Matches(text).Count;
      return count > 0 ? count : null;
    }
    catch
    {
      return null;
    }
  }

  private static string BuildDsd(IReadOnlyList<PublishSheet> sheets, string currentPath, string outputPath)
  {
    // DSD (Drawing Set Description) is the INI-style sheet list PUBLISH reads.
    // Target Type=6 selects a multi-sheet PDF written to DWF= (the key name is
    // historical); PromptForDwfName=FALSE keeps -PUBLISH non-interactive.
    var builder = new StringBuilder();
    builder.AppendLine("[DWF6Version]");
    builder.AppendLine("Ver=1");
    builder.AppendLine("[DWF6MinorVersion]");
    builder.AppendLine("MinorVer=1");
    var sectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var sheet in sheets)
    {
      var dwg = sheet.DrawingPath ?? currentPath;
      var section = UniqueFileName(sectionNames, $"{Path.GetFileNameWithoutExtension(dwg)}-{sheet.LayoutName}");
      builder.AppendLine($"[DWF6Sheet:{section}]");
      builder.AppendLine($"DWG={dwg}");
      builder.AppendLine($"Layout={sheet.LayoutName}");
      builder.AppendLine("Setup=");
      builder.AppendLine($"OriginalSheetPath={dwg}");
      builder.AppendLine("Has Plot Port=0");
      builder.AppendLine("Has3DDWF=0");
    }
    builder.AppendLine("[Target]");
    builder.AppendLine("Type=6");
    builder.AppendLine($"DWF={outputPath}");
    builder.AppendLine($"OUT={Path.GetDirectoryName(outputPath)}{Path.DirectorySeparatorChar}");
    builder.AppendLine("PWD=");
    builder.AppendLine("[PdfOptions]");
    builder.AppendLine("IncludeHyperlinks=TRUE");
    builder.AppendLine("CreateBookmarks=TRUE");
    builder.AppendLine("CaptureFontsInDrawing=TRUE");
    builder.AppendLine("ConvertTextToGeometry=FALSE");
    builder.AppendLine("VectorResolution=1200");
    builder.AppendLine("RasterResolution=400");
    builder.AppendLine("[AutoCAD Block Data]");
    builder.AppendLine("IncludeBlockInfo=0");
    builder.AppendLine("BlockTmplFilePath=");
    builder.AppendLine("[SheetSet Properties]");
    builder.AppendLine("IsSheetSet=FALSE");
    builder.AppendLine("IsHomogeneous=FALSE");
    builder.AppendLine("SheetSet Name=");
    builder.AppendLine("NoOfCopies=1");
    builder.AppendLine("PlotStampOn=FALSE");
    builder.AppendLine("ViewFile=FALSE");
    builder.AppendLine("JobID=0");
    builder.AppendLine("SelectionSetName=");
    builder.AppendLine("AcadProfile=");
    builder.AppendLine("CategoryName=");
    builder.AppendLine("LogFilePath=");
    builder.AppendLine("IncludeLayer=FALSE");
    builder.AppendLine("LineMerge=FALSE");
    builder.AppendLine("CurrentPrecision=");
    builder.AppendLine("PromptForDwfName=FALSE");
    builder.AppendLine("PwdProtectPublishedDWF=FALSE");
    builder.AppendLine("PromptForPwd=FALSE");
    builder.AppendLine("RepublishingMarkups=FALSE");
    builder.AppendLine("DSDType=1");
    builder.AppendLine("PublishSheetSetMetadata=FALSE");
    builder.AppendLine("PublishSubsets=FALSE");
    return builder.ToString();
  }

  // =========================================================================
  // Helpers
  // =========================================================================

  /// <summary>Sets system variables and restores the originals on dispose.</summary>
  private sealed class SystemVariableScope : IDisposable
  {
    private readonly List<(string Name, object? Value)> originals = [];
    private readonly List<string> warnings = [];
    private bool restored;

    public void Remember(string name)
    {
      if (originals.Any(item => item.Name == name)) return;
      originals.Add((name, App.GetSystemVariable(name)));
    }

    public void Set(string name, object value)
    {
      Remember(name);
      App.SetSystemVariable(name, value);
    }

    public IEnumerable<string> RestoreWarnings()
    {
      Restore();
      return warnings;
    }

    public void Dispose() => Restore();

    private void Restore()
    {
      if (restored) return;
      restored = true;
      for (var index = originals.Count - 1; index >= 0; index--)
      {
        var (name, value) = originals[index];
        try
        {
          if (value != null) App.SetSystemVariable(name, value);
        }
        catch (Exception ex)
        {
          warnings.Add($"Could not restore system variable {name}: {ex.Message}");
        }
      }
    }
  }

  private static IEnumerable<Layout> ReadLayouts(Database database, Transaction transaction)
  {
    var dictionary = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
    foreach (DBDictionaryEntry entry in dictionary)
    {
      if (transaction.GetObject(entry.Value, OpenMode.ForRead) is Layout layout)
      {
        yield return layout;
      }
    }
  }

  private static List<LayoutSnapshot> ReadLayoutSnapshots(Document doc)
  {
    using var documentLock = doc.LockDocument();
    using var transaction = doc.Database.TransactionManager.StartOpenCloseTransaction();
    var snapshots = ReadLayouts(doc.Database, transaction)
      .Select(layout => new LayoutSnapshot(layout.LayoutName, layout.TabOrder, layout.ModelType, IsInitialized(layout, transaction)))
      .ToList();
    return snapshots;
  }

  private static List<LayoutSnapshot> ReadLayoutSnapshots(string sourcePath)
  {
    using var database = new Database(false, true);
    database.ReadDwgFile(sourcePath, FileOpenMode.OpenForReadAndAllShare, true, "");
    database.CloseInput(true);
    using var transaction = database.TransactionManager.StartOpenCloseTransaction();
    return ReadLayouts(database, transaction)
      .Select(layout => new LayoutSnapshot(layout.LayoutName, layout.TabOrder, layout.ModelType, IsInitialized(layout, transaction)))
      .ToList();
  }

  private static Dictionary<string, object?> ToLayoutSummary(Layout layout, Transaction transaction, string? currentLayout)
  {
    var summary = ToPlotSettingsSummary(layout);
    summary["name"] = layout.LayoutName;
    summary["tabOrder"] = layout.TabOrder;
    summary["isModel"] = layout.ModelType;
    summary["initialized"] = IsInitialized(layout, transaction);
    summary["isCurrent"] = string.Equals(layout.LayoutName, currentLayout, StringComparison.OrdinalIgnoreCase);
    summary["pageSetup"] = NullIfEmpty(layout.PlotSettingsName);
    summary["handle"] = layout.Handle.ToString();
    return summary;
  }

  // -PLOT's Portrait/Landscape answer is the orientation of the plotted page,
  // while PlotRotation turns the drawing relative to the paper as the device
  // defines it, and a medium such as "ARCH D (36.00 x 24.00 Inches)" is already
  // wider than tall. So the page is landscape when exactly one of the two holds:
  // the paper is wider than tall, or the rotation is a quarter turn.
  private static bool IsLandscape(PlotSettings settings)
  {
    var paper = settings.PlotPaperSize;
    var quarterTurn = settings.PlotRotation is PlotRotation.Degrees090 or PlotRotation.Degrees270;
    return (paper.X > paper.Y) != quarterTurn;
  }

  private static Dictionary<string, object?> ToPlotSettingsSummary(PlotSettings settings)
  {
    var rotation = settings.PlotRotation;
    return new Dictionary<string, object?>
    {
      ["device"] = NullIfEmpty(settings.PlotConfigurationName),
      ["canonicalMediaName"] = NullIfEmpty(settings.CanonicalMediaName),
      ["paperUnits"] = settings.PlotPaperUnits.ToString(),
      ["paperWidth"] = settings.PlotPaperSize.X,
      ["paperHeight"] = settings.PlotPaperSize.Y,
      ["orientation"] = IsLandscape(settings) ? "landscape" : "portrait",
      ["rotation"] = rotation.ToString(),
      ["plotType"] = settings.PlotType.ToString(),
      ["plotStyleTable"] = NullIfEmpty(settings.CurrentStyleSheet),
    };
  }

  private static List<MediaName> ReadMedia(PlotSettings probe, string device)
  {
    var validator = PlotSettingsValidator.Current;
    validator.SetPlotConfigurationName(probe, device, null);
    validator.RefreshLists(probe);
    var media = new List<MediaName>();
    foreach (var canonical in ToList(validator.GetCanonicalMediaNameList(probe)))
    {
      string locale;
      try
      {
        locale = validator.GetLocaleMediaName(probe, canonical);
      }
      catch
      {
        locale = canonical;
      }
      media.Add(new MediaName(canonical, string.IsNullOrWhiteSpace(locale) ? canonical : locale));
    }
    return media;
  }

  private static string RequireDevice(IReadOnlyList<string> devices, string device)
  {
    return devices.FirstOrDefault(name => string.Equals(name, device, StringComparison.OrdinalIgnoreCase))
      ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Plot device '{device}' is not configured. Available: {Preview(devices)}.");
  }

  private static List<string> ToList(StringCollection? collection)
  {
    var list = new List<string>();
    if (collection == null) return list;
    foreach (var item in collection)
    {
      if (!string.IsNullOrWhiteSpace(item)) list.Add(item);
    }
    return list;
  }

  private static double[]? GetOptionalDoubleArray(JsonObject? parameters, string name)
  {
    if (PluginRuntime.GetParameter(parameters, name) is not JsonArray array) return null;
    try
    {
      return array.Select(node => node!.GetValue<double>()).ToArray();
    }
    catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Parameter '{name}' must contain only numbers.");
    }
  }

  private static string FormatPoint(double x, double y) =>
    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{x:0.0#########},{y:0.0#########}");

  private static List<string> GetOptionalStringArray(JsonObject? parameters, string name)
  {
    if (PluginRuntime.GetParameter(parameters, name) is not JsonArray array) return [];
    var values = new List<string>();
    foreach (var node in array)
    {
      var value = node?.GetValue<string>();
      if (string.IsNullOrWhiteSpace(value))
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Parameter '{name}' must contain only non-empty strings.");
      }
      values.Add(value);
    }
    return values;
  }

  private static Dictionary<string, object?> LayoutResult(PlotJob job, string status, OutputFile? file, string? error, long durationMs)
  {
    return new Dictionary<string, object?>
    {
      ["layoutName"] = job.LayoutName,
      ["status"] = status,
      ["outputPath"] = file?.Path ?? job.OutputPath,
      ["bytes"] = file?.Bytes,
      ["pageCount"] = file?.PageCount,
      ["paperSize"] = job.PaperSize,
      ["orientation"] = job.OrientationToken == "_L" ? "landscape" : "portrait",
      ["plotStyleTable"] = job.StyleSheetToken == "." ? null : job.StyleSheetToken,
      ["pageSetup"] = job.PageSetup,
      ["durationMs"] = durationMs,
      ["regenerated"] = job.Regenerated,
      ["error"] = error,
    };
  }

  private static string LayoutNotFoundMessage(string name, IEnumerable<LayoutSnapshot> available) =>
    $"Layout '{name}' was not found. Available paper-space layouts: {Preview(available.OrderBy(item => item.TabOrder).Select(item => item.Name))}.";

  private static void TryDelete(string path)
  {
    try
    {
      if (File.Exists(path)) File.Delete(path);
    }
    catch
    {
      // Leftover DSD files are harmless and uniquely named per output.
    }
  }

  private static string SanitizeFileName(string value)
  {
    var invalid = Path.GetInvalidFileNameChars();
    var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim().TrimEnd('.');
    return string.IsNullOrWhiteSpace(cleaned) ? "layout" : cleaned;
  }

  private static string UniqueFileName(HashSet<string> used, string baseName)
  {
    var candidate = baseName;
    for (var suffix = 2; !used.Add(candidate); suffix++)
    {
      candidate = $"{baseName}_{suffix}";
    }
    return candidate;
  }

  private static string Preview(IEnumerable<string> values)
  {
    var list = values.ToList();
    if (list.Count == 0) return "(none)";
    var head = string.Join(", ", list.Take(12));
    return list.Count > 12 ? $"{head}, ... ({list.Count} total)" : head;
  }

  private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
