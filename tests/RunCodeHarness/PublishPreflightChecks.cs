using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;

// Execute the actual preflight control flow with managed stand-ins for host
// databases. Autodesk code is never loaded or called by these checks.
internal static class PublishPreflightChecks
{
  internal static void Run(string repoRoot, Action<bool, string> expect)
  {
    var source = File.ReadAllText(Path.Combine(repoRoot, "Civil3D-MCP-Plugin", "PlotCommands.cs"));
    RunSelectionChecks(source, expect);
    var methods = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
    var method = methods.Single(node => node.Identifier.Text == "PreflightPublishSheets");
    var validate = methods.Single(node => node.Identifier.Text == "ValidatePublishLayout");
    var fixture = """
      using System;
      using System.Collections.Generic;
      using System.IO;
      using System.Linq;
      public static class PreflightFixture
      {
        private const string DefaultPdfDevice = "DWG To PDF.pc3";
        private sealed record PublishSheet(string LayoutName, string? DrawingPath);
        private sealed record PublishSettings(string? PageSetup, string Device, string Media, string? PlotStyleTable, double WidthMm, double HeightMm)
        {
          public PublishSettings(string style, double widthMm, double heightMm) : this(null, DefaultPdfDevice, "A4", style, widthMm, heightMm) { }
        }
        private sealed record MediaName(string Canonical);
        private sealed record LayoutSnapshot(string Name, int TabOrder, bool ModelType, bool Initialized);
        private sealed class Layout(PublishSettings settings)
        {
          public string LayoutName => "Sheet";
          public int TabOrder => 1;
          public bool ModelType => false;
          public string PlotConfigurationName => settings.Device;
          public string? CurrentStyleSheet => settings.PlotStyleTable;
          public string CanonicalMediaName => settings.Media;
          public string? PlotSettingsName => settings.PageSetup;
          public (double X, double Y) PlotPaperSize => (settings.WidthMm, settings.HeightMm);
        }
        private sealed class JsonRpcDispatchException(string code, string message) : Exception(message) { public string Code = code; }
        private enum FileOpenMode { OpenForReadAndAllShare }
        private sealed class Document
        {
          public string Name = Path.GetFullPath("scratch.dwg");
          public Database Database = new(false, true) { Settings = new("missing.ctb", 297, 420) };
          public bool Saved;
          public bool Locked;
          public IDisposable LockDocument() { Locked = true; return new Scope(() => Locked = false); }
        }
        private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
        private sealed class Transaction : IDisposable { public void Dispose() { } }
        private sealed class TransactionManager
        {
          public Transaction StartOpenCloseTransaction() => new();
        }
        private sealed class Database(bool buildDefault, bool noDocument) : IDisposable
        {
          public PublishSettings Settings = new("monochrome.ctb", 210, 297);
          public TransactionManager TransactionManager = new();
          public void ReadDwgFile(string path, FileOpenMode mode, bool allowConversion, string password)
          {
            DiskReads++;
            if (SavedSettings.TryGetValue(path, out var saved)) Settings = saved;
          }
          public void CloseInput(bool close) { }
          public void Dispose() { }
        }
        private static class App { public static List<Document> DocumentManager = new(); }
        private sealed class PlotSettings(bool model) : IDisposable { public void Dispose() { } }
        private sealed class PlotSettingsValidator
        {
          public static PlotSettingsValidator Current = new();
          public void RefreshLists(PlotSettings settings) { }
          public List<string> GetPlotDeviceList() => [DefaultPdfDevice];
          public List<string> GetPlotStyleSheetList() => ["monochrome.ctb"];
        }
        private static int DiskReads;
        private static int SavedChecks;
        private static Dictionary<string, PublishSettings> SavedSettings = new();
        private static List<string> ToList(List<string> values) => values;
        private static void RequireDevice(IReadOnlyList<string> devices, string device) { }
        private static List<MediaName> ReadMedia(PlotSettings probe, string device) => [new("A4")];
        private static void CheckSavedSource(Document source, bool requireSaved, List<string> warnings)
        {
          if (!source.Locked) throw new Exception("Saved state checked without the document lock");
          SavedChecks++;
          if (!source.Saved && requireSaved) throw new InvalidOperationException("unsaved");
          if (!source.Saved) warnings.Add("saved revision");
        }
        private static IEnumerable<Layout> ReadLayouts(Database database, Transaction transaction) => [new(database.Settings)];
        private static bool IsInitialized(Layout layout) => true;
        private static void RequireInitialized(LayoutSnapshot layout) { }
        private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
        __VALIDATE_METHOD__
        __PREFLIGHT_METHOD__
        private static bool RefusesMissing(string extension, bool closed)
        {
          var path = Path.GetFullPath("missing-" + extension + ".dwg");
          var settings = new PublishSettings("missing." + extension, 210, 297);
          SavedSettings[path] = settings;
          var source = new Document { Name = path, Saved = true };
          source.Database.Settings = settings;
          var reads = DiskReads;
          try
          {
            PreflightPublishSheets(closed ? null : source, [new("Sheet", closed ? path : null)], path, true, new());
            return false;
          }
          catch (JsonRpcDispatchException error)
          {
            return error.Code == "CIVIL3D.OBJECT_NOT_FOUND"
              && error.Message.Contains("Plot style table 'missing." + extension + "'")
              && error.Message.Contains("layout 'Sheet'") && error.Message.Contains(path)
              && DiskReads == reads + (closed ? 1 : 0) && !source.Locked;
          }
        }
        public static bool[] Run()
        {
          var open = new Document();
          App.DocumentManager.Add(open);
          var warnings = new List<string>();
          var saved = PreflightPublishSheets(open, [new("Sheet", null)], open.Name, false, warnings).Single();
          var usesSaved = saved == new PublishSettings("monochrome.ctb", 210, 297)
            && DiskReads == 1 && SavedChecks == 1 && warnings.Count == 1 && !open.Locked;
          var external = PreflightPublishSheets(null, [new("Sheet", open.Name)], open.Name, false, warnings).Single();
          var usesExternalSaved = external == saved && DiskReads == 2 && SavedChecks == 2;
          var refused = false;
          try { PreflightPublishSheets(open, [new("Sheet", null)], open.Name, true, warnings); }
          catch (InvalidOperationException) { refused = true; }
          var rejectsUnsaved = refused && DiskReads == 2 && !open.Locked;
          open.Saved = true;
          open.Database.Settings = saved;
          var clean = PreflightPublishSheets(open, [new("Sheet", null)], open.Name, true, warnings).Single();
          var usesCleanOpen = clean == saved && DiskReads == 2 && !open.Locked;
          var closed = PreflightPublishSheets(null, [new("Sheet", Path.GetFullPath("closed.dwg"))], open.Name, true, warnings).Single();
          var usesClosed = closed == saved && DiskReads == 3;
          return [usesSaved, usesExternalSaved, rejectsUnsaved, usesCleanOpen, usesClosed,
            RefusesMissing("ctb", true), RefusesMissing("stb", true), RefusesMissing("ctb", false), RefusesMissing("stb", false)];
        }
      }
      """.Replace("__PREFLIGHT_METHOD__", method.ToFullString()).Replace("__VALIDATE_METHOD__", validate.ToFullString());
    var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
      .Select(path => MetadataReference.CreateFromFile(path));
    var compilation = CSharpCompilation.Create("PublishPreflightFixture",
      [CSharpSyntaxTree.ParseText(fixture)], references,
      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    using var output = new MemoryStream();
    var emit = compilation.Emit(output);
    if (!emit.Success)
      throw new InvalidOperationException(string.Join(Environment.NewLine, emit.Diagnostics));
    var assembly = Assembly.Load(output.ToArray());
    var checks = (bool[])assembly.GetType("PreflightFixture")!.GetMethod("Run")!.Invoke(null, null)!;
    var names = new[] {
      "unsaved A3 and missing CTB do not override saved A4 preflight settings",
      "another open drawing uses its saved settings when requireSaved=false",
      "requireSaved=true refuses unsaved changes before reading the DWG",
      "a clean open drawing retains locked in-memory preflight with requireSaved=true",
      "a closed source reads the saved DWG for preflight",
      "a closed saved DWG with a missing CTB refuses with the table, layout and drawing names",
      "a closed saved DWG with a missing STB refuses with the table, layout and drawing names",
      "a clean open saved DWG with a missing CTB refuses and releases its lock",
      "a clean open saved DWG with a missing STB refuses and releases its lock",
    };
    for (var index = 0; index < checks.Length; index++) expect(checks[index], names[index]);
  }

  private static void RunSelectionChecks(string source, Action<bool, string> expect)
  {
    var publish = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
      .OfType<MethodDeclarationSyntax>().Single(node => node.Identifier.Text == "PublishSheetSetAsync");
    var outer = publish.Body!.Statements;
    var lambda = publish.DescendantNodes().OfType<ParenthesizedLambdaExpressionSyntax>()
      .Single(node => node.ParameterList.Parameters.Any(parameter => parameter.Identifier.Text == "cancellationToken"));
    var inner = ((BlockSyntax)lambda.Body).Statements;
    static string Pick(SyntaxList<StatementSyntax> statements, string start, string end) =>
      string.Join(Environment.NewLine, statements.SkipWhile(statement => !statement.ToString().Contains(start))
        .TakeWhile(statement => !statement.ToString().Contains(end)).Select(statement => statement.ToFullString()));
    // Keep the production request parsing and all selection statements through
    // preflight. Only the PDF side effects after preflight are replaced.
    var parsing = Pick(outer, "var requireSaved =", "return CivilExecution.ExecuteCommandSequenceAsync");
    var selection = Pick(inner, "var warnings =", "if (sheets.Any(sheet => sheet.DrawingPath == null))");
    var savedCheck = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
      .OfType<MethodDeclarationSyntax>().Single(node => node.Identifier.Text == "CheckSavedSource");
    var savedWarning = savedCheck.DescendantNodes().OfType<VariableDeclaratorSyntax>()
      .Single(node => node.Identifier.Text == "warning").Initializer!.Value.ToFullString();
    var fixture = """
      using System;
      using System.Collections.Generic;
      using System.IO;
      using System.Linq;
      using System.Text.Json.Nodes;
      public static class PublishSelectionFixture
      {
        private sealed record LayoutSnapshot(string Name, int TabOrder, bool ModelType, bool Initialized = true);
        private sealed record PublishSheet(string LayoutName, string? DrawingPath);
        private sealed class Database { public string Filename = Path.GetFullPath("source.dwg"); }
        private sealed class Document { public string Name = Path.GetFullPath("source.dwg"); public Database Database = new(); public bool Saved; public bool Locked; public IDisposable LockDocument() { Locked = true; return new Scope(() => Locked = false); } }
        private sealed class Scope(Action close) : IDisposable { public void Dispose() => close(); }
        private sealed record Match(int Index, string? Error);
        private static class App { public static List<Document> DocumentManager = new(); }
        private static class RunCodeHelpers { public static Match MatchDocument(string path, string[] names) { var index = Array.FindIndex(names, name => string.Equals(path, name, StringComparison.OrdinalIgnoreCase)); return new(index, index < 0 ? "not open" : null); } }
        private static class FileBoundary { public static string ResolveImportPath(string path, string extension) => Path.GetFullPath(path); }
        private static class PluginRuntime
        {
          public static string GetRequiredString(JsonObject? p, string key) => (string?)p?[key] ?? throw new Exception(key);
          public static string? GetOptionalString(JsonObject? p, string key) => (string?)p?[key];
          public static bool? GetOptionalBool(JsonObject? p, string key) => (bool?)p?[key];
        }
        private sealed class JsonRpcDispatchException(string code, string message) : Exception(message) { public string Code = code; }
        private static List<string> GetOptionalStringArray(JsonObject? p, string key) => (p?[key] as JsonArray)?.Select(item => (string)item!).ToList() ?? new();
        private static string Preview(IEnumerable<string> names) => string.Join(", ", names);
        private static string LayoutNotFoundMessage(string name, IEnumerable<LayoutSnapshot> layouts) => name;
        private static void RequireInitialized(LayoutSnapshot layout) { if (!layout.Initialized) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "uninitialized " + layout.Name); }
        private static int OpenReads, DiskReads, SavedChecks;
        private static bool DiskPresent = true;
        private static List<string> DiskReadPaths = new(), CheckedPaths = new();
        private static List<LayoutSnapshot> OpenLayouts = new(), DiskLayouts = new();
        private static List<LayoutSnapshot> ReadLayoutSnapshots(Document source) { OpenReads++; return OpenLayouts; }
        private static List<LayoutSnapshot> ReadLayoutSnapshots(string path) { DiskReads++; DiskReadPaths.Add(path); if (!DiskPresent) throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", "never saved"); return DiskLayouts; }
        private static void CheckSavedSource(Document source, bool requireSaved, List<string> warnings)
        {
          if (!source.Locked) throw new Exception("saved check requires lock");
          SavedChecks++;
          CheckedPaths.Add(source.Name);
          if (!DiskPresent) throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", "never saved");
          if (!source.Saved && requireSaved) throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", "unsaved");
          var warning = __SAVED_WARNING__;
          if (!source.Saved && !warnings.Contains(warning)) warnings.Add(warning);
        }
        private static object PreflightPublishSheets(Document? source, List<PublishSheet> sheets, string currentPath, bool requireSaved, List<string> warnings)
        {
          foreach (var sheet in sheets)
          {
            var open = sheet.DrawingPath == null ? source : null;
            if (open != null) { using var hold = open.LockDocument(); CheckSavedSource(open, requireSaved, warnings); }
            if (sheet.DrawingPath != null) continue;
            if (!DiskLayouts.Any(item => item.Name == sheet.LayoutName))
              throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "saved layout missing " + sheet.LayoutName);
          }
          return new object();
        }
        private sealed record Selection(List<PublishSheet> Sheets, List<string> Warnings, int OpenReads, int DiskReads, int SavedChecks);
        private static Selection Select(JsonObject? parameters, Document? doc)
        {
          __PARSING__
          __SELECTION__
          return new Selection(sheets, warnings, OpenReads, DiskReads, SavedChecks);
        }
        private static void Reset(bool saved = false, bool diskPresent = true)
        {
          OpenReads = DiskReads = SavedChecks = 0; DiskPresent = diskPresent;
          DiskReadPaths.Clear(); CheckedPaths.Clear();
          App.DocumentManager.Clear();
          OpenLayouts = [new("Model", 0, true), new("Renamed", 1, false), new("NewUnsaved", 2, false, false)];
          DiskLayouts = [new("Model", 0, true), new("Original", 1, false)];
        }
        private static Document Source(bool saved = false) { var source = new Document { Saved = saved }; App.DocumentManager.Add(source); return source; }
        private static JsonObject Request(bool? requireSaved = false, string? layout = null, bool sheet = false, bool explicitPath = false)
        {
          var p = new JsonObject { ["outputPath"] = "out.pdf" };
          if (requireSaved != null) p["requireSaved"] = requireSaved.Value;
          if (layout != null)
          {
            if (sheet) p["sheets"] = new JsonArray(new JsonObject { ["layoutName"] = layout, ["drawingPath"] = explicitPath ? Path.GetFullPath("source.dwg") : null });
            else p["layoutNames"] = new JsonArray(layout);
          }
          return p;
        }
        private static bool Succeeds(JsonObject request, Document source, Func<Selection, bool> check)
        { try { return check(Select(request, source)); } catch { return false; } }
        private static bool Refuses(JsonObject request, Document source, string code, int maxReads)
        { try { Select(request, source); return false; } catch (JsonRpcDispatchException error) { return error.Code == code && DiskReads + OpenReads <= maxReads && !source.Locked; } }
        public static bool[] Run()
        {
          Reset(); var source = Source();
          var originalByName = Succeeds(Request(layout: "Original"), source, result => result.Sheets.Single().LayoutName == "Original" && result.DiskReads > 0 && result.OpenReads == 0 && result.Warnings.Count == 1 && result.Warnings[0].Contains("unsaved changes") && result.Warnings[0].Contains("saved revision"));
          Reset(); source = Source();
          var renamedIsMissing = Refuses(Request(layout: "Renamed"), source, "CIVIL3D.OBJECT_NOT_FOUND", 1) && DiskReads == 1 && OpenReads == 0;
          Reset(); source = Source();
          var defaultSavedOnly = Succeeds(Request(), source, result => result.Sheets.Select(sheet => sheet.LayoutName).SequenceEqual(["Original"]) && result.DiskReads > 0 && result.OpenReads == 0);
          Reset(); source = Source();
          var sheetsSameSource = Succeeds(Request(layout: "Original", sheet: true, explicitPath: true), source, result => result.Sheets.Single().LayoutName == "Original" && result.Sheets.Single().DrawingPath == null && result.DiskReads > 0 && result.OpenReads == 0);
          Reset(); source = Source();
          OpenLayouts = [new("Model", 0, true), new("Original", 1, false, false)];
          var initializedFromDisk = Succeeds(Request(layout: "Original"), source, result => result.Sheets.Count == 1 && result.DiskReads > 0 && result.OpenReads == 0);
          Reset(); source = Source();
          OpenLayouts = [new("Model", 0, true), new("NewUnsaved", 1, false)];
          var deletedOpenTab = Succeeds(Request(layout: "Original"), source, result => result.Sheets.Single().LayoutName == "Original" && result.DiskReads == 1 && result.OpenReads == 0);
          Reset(); source = Source();
          OpenLayouts = [new("Model", 0, true), new("Original", 1, false)];
          DiskLayouts = [new("Model", 0, true), new("Original", 1, false, false)];
          var savedUninitialized = Refuses(Request(layout: "Original"), source, "CIVIL3D.INVALID_INPUT", 1) && DiskReads == 1 && OpenReads == 0;
          Reset(); source = Source();
          var defaultRequiresSaved = Refuses(Request(requireSaved: null), source, "CIVIL3D.CONFLICT", 0);
          Reset(diskPresent: false); source = Source();
          var neverSaved = Refuses(Request(), source, "CIVIL3D.CONFLICT", 1) && OpenReads == 0;
          Reset(diskPresent: false); source = Source();
          var externalOnly = Request(layout: "External", sheet: true);
          ((JsonObject)((JsonArray)externalOnly["sheets"]!)[0]!)["drawingPath"] = Path.GetFullPath("other.dwg");
          var ignoresUnusedSource = Succeeds(externalOnly, source, result => result.Sheets.Single().DrawingPath == Path.GetFullPath("other.dwg") && result.OpenReads == 0 && result.DiskReads == 0 && result.SavedChecks == 0);
          Reset(); source = Source();
          var target = new Document { Name = Path.GetFullPath("target.dwg"), Database = new Database { Filename = Path.GetFullPath("target.dwg") } };
          App.DocumentManager.Add(target);
          var targetRequest = Request(layout: "Original");
          targetRequest["document"] = target.Name;
          var targetsNamedOpenDocument = Succeeds(targetRequest, source, result => result.Sheets.Single().LayoutName == "Original" && result.DiskReads == 1 && result.OpenReads == 0 && result.Warnings.Count == 1)
            && DiskReadPaths.SequenceEqual([target.Name]) && CheckedPaths.All(path => path == target.Name);
          return [originalByName, renamedIsMissing, defaultSavedOnly, sheetsSameSource, initializedFromDisk, deletedOpenTab, savedUninitialized, defaultRequiresSaved, neverSaved, ignoresUnusedSource, targetsNamedOpenDocument];
        }
      }
      """.Replace("__PARSING__", parsing).Replace("__SELECTION__", selection).Replace("__SAVED_WARNING__", savedWarning);
    var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
      .Select(path => MetadataReference.CreateFromFile(path));
    var compilation = CSharpCompilation.Create("PublishSelectionFixture", [CSharpSyntaxTree.ParseText(fixture)], references,
      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    using var output = new MemoryStream();
    var emit = compilation.Emit(output);
    if (!emit.Success) throw new InvalidOperationException(string.Join(Environment.NewLine, emit.Diagnostics));
    var assembly = Assembly.Load(output.ToArray());
    var checks = (bool[])assembly.GetType("PublishSelectionFixture")!.GetMethod("Run")!.Invoke(null, null)!;
    var names = new[] {
      "named layout resolves the saved original despite an unsaved rename",
      "unsaved renamed layout is absent from the saved source",
      "default publish excludes a new unsaved layout tab",
      "sheets with the current drawing path use its saved layouts",
      "layout initialization is read from the saved revision",
      "a layout deleted only in the open drawing remains in the saved source",
      "saved uninitialized layout is refused despite an initialized open tab",
      "default requireSaved=true refuses unsaved source before layout selection",
      "never-saved source remains a conflict",
      "external-only sheets do not inspect an unused unsaved active drawing",
      "named open document selects and checks that document's saved DWG",
    };
    for (var index = 0; index < checks.Length; index++) expect(checks[index], names[index]);
  }
}
