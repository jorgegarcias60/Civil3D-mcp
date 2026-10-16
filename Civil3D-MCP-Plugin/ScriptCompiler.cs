using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Operations;

namespace Civil3DMcpPlugin;

/// <summary>
/// Compiles a runCode snippet (C# statements) into an in-memory assembly whose
/// class derives from <c>RunCodeScript</c>; the snippet becomes the body of
/// <c>Run()</c>. Only Roslyn and the BCL are used here, no AutoCAD types, so
/// tests/RunCodeHarness can exercise it outside Civil 3D against the reference
/// assemblies. Nothing here touches the host: callers compile on a worker
/// thread and only run the result on the main thread.
///
/// Methods of this class reference Roslyn types, so the first call loads
/// Roslyn: RoslynResolver must be registered before that (RunCodeCommands).
/// </summary>
internal static class ScriptCompiler
{
  // File name the snippet's lines map to (#line), in diagnostics and stack traces.
  internal const string ScriptPath = "script";
  internal const string BaseClassName = "Civil3DMcpPlugin.RunCodeScript";
  internal const string GuardDiagnosticId = "C3DMCP001";

  // Namespaces every snippet can use without a using directive.
  // Autodesk.AutoCAD.Runtime is left out on purpose: its Exception hides
  // System.Exception in every catch. Type names that two of these namespaces
  // both declare would not compile (CS0104), so DefaultAliases names every one
  // of them (tests/RunCodeHarness checks that none is missing).
  internal static readonly string[] DefaultUsings =
  {
    "System",
    "System.Collections.Generic",
    "System.Diagnostics",
    "System.Globalization",
    "System.IO",
    "System.Linq",
    "System.Text",
    "System.Text.Json",
    "System.Text.Json.Nodes",
    "System.Text.RegularExpressions",
    "System.Threading",
    "System.Threading.Tasks",
    "Autodesk.AutoCAD.ApplicationServices",
    "Autodesk.AutoCAD.Colors",
    "Autodesk.AutoCAD.DatabaseServices",
    "Autodesk.AutoCAD.EditorInput",
    "Autodesk.AutoCAD.Geometry",
    "Autodesk.Civil",
    "Autodesk.Civil.ApplicationServices",
    "Autodesk.Civil.DatabaseServices",
    "Autodesk.Civil.DatabaseServices.Styles",
  };

  // A name both AutoCAD and Civil 3D declare means AutoCAD's (the Civil 3D one
  // gets a Civil prefix), except Surface and Section, which mean Civil 3D's
  // (AutoCAD's get an Ac prefix).
  internal static readonly string[] DefaultAliases =
  {
    "AcException = Autodesk.AutoCAD.Runtime.Exception",
    "ErrorStatus = Autodesk.AutoCAD.Runtime.ErrorStatus",
    "Application = Autodesk.AutoCAD.ApplicationServices.Application",
    "DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject",
    "CivilDBObject = Autodesk.Civil.DatabaseServices.DBObject",
    "Entity = Autodesk.AutoCAD.DatabaseServices.Entity",
    "CivilEntity = Autodesk.Civil.DatabaseServices.Entity",
    "Surface = Autodesk.Civil.DatabaseServices.Surface",
    "AcSurface = Autodesk.AutoCAD.DatabaseServices.Surface",
    "Section = Autodesk.Civil.DatabaseServices.Section",
    "AcSection = Autodesk.AutoCAD.DatabaseServices.Section",
    "SectionViewStyle = Autodesk.Civil.DatabaseServices.Styles.SectionViewStyle",
    "AcSectionViewStyle = Autodesk.AutoCAD.DatabaseServices.SectionViewStyle",
    "Table = Autodesk.AutoCAD.DatabaseServices.Table",
    "CivilTable = Autodesk.Civil.DatabaseServices.Table",
    "TableStyle = Autodesk.AutoCAD.DatabaseServices.TableStyle",
    "CivilTableStyle = Autodesk.Civil.DatabaseServices.Styles.TableStyle",
    "Shape = Autodesk.AutoCAD.DatabaseServices.Shape",
    "CivilShape = Autodesk.Civil.DatabaseServices.Shape",
    "PointCloud = Autodesk.AutoCAD.DatabaseServices.PointCloud",
    "CivilPointCloud = Autodesk.Civil.DatabaseServices.PointCloud",
    "Graph = Autodesk.AutoCAD.DatabaseServices.Graph",
    "CivilGraph = Autodesk.Civil.DatabaseServices.Graph",
    "Group = Autodesk.AutoCAD.DatabaseServices.Group",
    "RegexGroup = System.Text.RegularExpressions.Group",
    "NurbSurface = Autodesk.AutoCAD.DatabaseServices.NurbSurface",
    "GeNurbSurface = Autodesk.AutoCAD.Geometry.NurbSurface",
    "TransactionManager = Autodesk.AutoCAD.DatabaseServices.TransactionManager",
    "SystemVariableChangedEventArgs = Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs",
    "SystemVariableChangedEventHandler = Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventHandler",
    "SystemVariableChangingEventArgs = Autodesk.AutoCAD.ApplicationServices.SystemVariableChangingEventArgs",
    "SystemVariableChangingEventHandler = Autodesk.AutoCAD.ApplicationServices.SystemVariableChangingEventHandler",
    "ThreadState = System.Threading.ThreadState",
    "TimeZone = System.TimeZone",
    "Trace = System.Diagnostics.Trace",
  };

  // A leading "using X;" / "using static X;" / "using A = X;" line of the
  // snippet. "using var x = ...;" and "using (...)" statements do not match.
  private static readonly Regex UsingDirective = new(
    @"^using\s+(?:static\s+)?(?:[A-Za-z_]\w*\s*=\s*)?[A-Za-z_][\w.]*(?:<[^;]*>)?\s*;$",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);

  private static readonly Regex StackLine = new(@"\bin " + Regex.Escape(ScriptPath) + @":line (\d+)", RegexOptions.Compiled);

  private static readonly ConcurrentDictionary<string, MetadataReference?> ReferenceCache = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// The full source: default usings, the snippet's own leading using
  /// directives (moved up, each mapped back to its line), then the class with
  /// the snippet as Run()'s body, mapped to "script" from line 1.
  /// </summary>
  internal static string BuildSource(string code, string className)
  {
    var lines = code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    var hoisted = new StringBuilder();
    for (var index = 0; index < lines.Length; index++)
    {
      var trimmed = lines[index].Trim();
      if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
      {
        continue;
      }

      // A using directive holds no string literal, so a "//" in it starts a comment.
      var comment = trimmed.IndexOf("//", StringComparison.Ordinal);
      if (comment > 0)
      {
        trimmed = trimmed[..comment].TrimEnd();
      }

      if (!UsingDirective.IsMatch(trimmed))
      {
        break;
      }

      hoisted.Append("#line ").Append(index + 1).Append(" \"").Append(ScriptPath).Append("\"\n");
      hoisted.Append(trimmed).Append('\n');
      hoisted.Append("#line default\n");
      lines[index] = string.Empty;
    }

    var source = new StringBuilder();
    foreach (var name in DefaultUsings)
    {
      source.Append("using ").Append(name).Append(";\n");
    }

    foreach (var alias in DefaultAliases)
    {
      source.Append("using ").Append(alias).Append(";\n");
    }

    source.Append(hoisted);
    source.Append("public sealed class ").Append(className).Append(" : global::").Append(BaseClassName).Append('\n');
    source.Append("{\n");
    source.Append("  public override object Run()\n");
    source.Append("  {\n");
    source.Append("#line 1 \"").Append(ScriptPath).Append("\"\n");
    foreach (var line in lines)
    {
      source.Append(line).Append('\n');
    }

    source.Append("#line default\n");
    source.Append("#pragma warning disable CS0162\n");
    source.Append("    return null;\n");
    source.Append("#pragma warning restore CS0162\n");
    source.Append("  }\n");
    source.Append("}\n");
    return source.ToString();
  }

  internal static ScriptCompileResult Compile(string code, string assemblyName, IEnumerable<string> referencePaths, bool writeMode, bool previewMode = false)
  {
    var className = "Script_" + Regex.Replace(assemblyName, @"[^A-Za-z0-9_]", "_");
    var source = BuildSource(code, className);
    var tree = CSharpSyntaxTree.ParseText(
      source,
      new CSharpParseOptions(LanguageVersion.Latest),
      path: assemblyName + ".cs",
      encoding: Encoding.UTF8);

    var references = referencePaths
      .Select(GetReference)
      .Where(reference => reference != null)
      .Cast<MetadataReference>()
      .ToList();

    var options = new CSharpCompilationOptions(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release,
        allowUnsafe: false,
        nullableContextOptions: NullableContextOptions.Disable,
        platform: Platform.X64)
      // Version unification between the host's assemblies is expected.
      .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>
      {
        ["CS1701"] = ReportDiagnostic.Suppress,
        ["CS1702"] = ReportDiagnostic.Suppress,
      });

    var compilation = CSharpCompilation.Create(assemblyName, new[] { tree }, references, options);
    var result = new ScriptCompileResult { TypeName = className, ReferenceCount = references.Count };

    result.Errors.AddRange(FindLineDirectives(tree, source));
    if (result.Errors.Count > 0)
    {
      return result;
    }

    foreach (var diagnostic in compilation.GetDiagnostics())
    {
      AddDiagnostic(result, diagnostic);
    }

    if (result.Errors.Count > 0)
    {
      return result;
    }

    result.Errors.AddRange(FindGuardViolations(compilation, tree, writeMode, previewMode));
    if (result.Errors.Count > 0)
    {
      return result;
    }

    using var peStream = new MemoryStream();
    using var pdbStream = new MemoryStream();
    var emit = compilation.Emit(peStream, pdbStream, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
    if (!emit.Success)
    {
      foreach (var diagnostic in emit.Diagnostics)
      {
        AddDiagnostic(result, diagnostic);
      }

      return result;
    }

    var compiled = new ScriptCompileResult
    {
      Assembly = peStream.ToArray(),
      Pdb = pdbStream.ToArray(),
      TypeName = className,
      ReferenceCount = references.Count,
    };
    compiled.Warnings.AddRange(result.Warnings);
    return compiled;
  }

  /// <summary>Snippet line of the innermost stack frame in the script, or null.</summary>
  internal static int? FindScriptLine(string? stackTrace)
  {
    if (string.IsNullOrEmpty(stackTrace))
    {
      return null;
    }

    var match = StackLine.Match(stackTrace);
    return match.Success ? int.Parse(match.Groups[1].Value) : null;
  }

  private static void AddDiagnostic(ScriptCompileResult result, Diagnostic diagnostic)
  {
    if (diagnostic.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
    {
      return;
    }

    var (line, column) = MapToScript(diagnostic.Location);
    var entry = new ScriptDiagnostic(line, column, diagnostic.Id, diagnostic.GetMessage());
    if (diagnostic.Severity == DiagnosticSeverity.Error)
    {
      result.Errors.Add(entry);
    }
    else if (result.Warnings.Count < 20)
    {
      result.Warnings.Add(entry);
    }
  }

  // 1-based line and column in the snippet; 0 when the location is outside it
  // (a missing reference, or the generated wrapper).
  private static (int Line, int Column) MapToScript(Location location)
  {
    if (location == Location.None || !location.IsInSource)
    {
      return (0, 0);
    }

    var span = location.GetMappedLineSpan();
    if (!span.HasMappedPath || span.Path != ScriptPath)
    {
      return (0, 0);
    }

    return (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
  }

  // Callers pass managed assemblies only (RunCodeAssemblies.ReferencePaths
  // filters out native DLLs): one unreadable reference fails the compilation.
  private static MetadataReference? GetReference(string path)
  {
    return ReferenceCache.GetOrAdd(path, static candidate =>
    {
      try
      {
        return MetadataReference.CreateFromFile(candidate);
      }
      catch (Exception)
      {
        return null;
      }
    });
  }

  // ---------------------------------------------------------------------------
  // Guard: refuse API calls that would block Civil 3D, or change the drawing in
  // read mode. It reads the compiled symbols, so aliases and using static are
  // caught, but it is a guard against mistakes, not a sandbox: reflection,
  // dynamic or COM get past it, and the code runs with the user's full rights.
  // ---------------------------------------------------------------------------

  private static readonly HashSet<string> InteractiveEditorMethods = new(StringComparer.Ordinal)
  {
    "GetPoint", "GetCorner", "GetDistance", "GetAngle", "GetString", "GetKeywords", "GetInteger",
    "GetDouble", "GetEntity", "GetNestedEntity", "GetSelection", "GetFileNameForOpen",
    "GetFileNameForSave", "Drag", "Command", "CommandAsync", "StartUserInteraction", "DoPrompt",
  };

  private static readonly string[] CivilWritePrefixes =
  {
    "Create", "Add", "Remove", "Delete", "Rebuild", "Import", "Insert", "Erase", "Rename", "Update",
    "Set", "Reverse", "Join", "Split", "Extend", "Trim", "Promote", "Synchronize", "Lock", "Unlock",
    "Move", "Copy", "Clear", "Reset", "Extract",
  };

  private const string DatabaseTypeName = "Autodesk.AutoCAD.DatabaseServices.Database";
  private const string ObjectIdTypeName = "Autodesk.AutoCAD.DatabaseServices.ObjectId";

  // Reviewed against the 2026 (25.1) and 2027 (26.0) Database metadata.
  // These change database contents/settings, undo state or saved output without
  // needing an object opened ForWrite. Keep queries and Purge (an ID filter) out.
  private static readonly HashSet<string> DatabaseWriteMethods = new(StringComparer.Ordinal)
  {
    "SaveAs", "Save", "Insert", "DeepCloneObjects", "Audit", "LoadLineTypeFile", "UpdateExt",
    "AttachXref", "OverlayXref", "DetachXref", "BindXrefs", "ReloadXrefs", "UnloadXrefs",
    "ResolveXrefs", "AddDBObject", "EvaluateFields", "SetDimstyleData", "SetTimeZoneAsUtcOffset",
    "SetWorldUcsBaseOrigin", "SetWorldPaperspaceUcsBaseOrigin", "DisableUndoRecording",
    "EraseEmptyObjects", "LoadMlineStyleFile", "ReclaimMemoryFromErasedObjects", "ResetTimes",
    "RestoreForwardingXrefSymbols", "RestoreOriginalXrefSymbols", "XBindXrefs", "ForceWblockDatabaseCopy",
  };

  // Preview cannot undo filesystem changes. New members fail closed instead
  // of relying on a list of write-like names. Metadata setters are not reads.
  private static readonly HashSet<string> PreviewReadOnlyFileMembers = new(StringComparer.Ordinal)
  {
    "Exists", "ReadAllText", "ReadAllTextAsync", "ReadAllLines", "ReadAllLinesAsync",
    "ReadAllBytes", "ReadAllBytesAsync", "ReadLines", "ReadLinesAsync", "OpenRead", "OpenText",
    "GetAttributes", "GetCreationTime", "GetCreationTimeUtc", "GetLastAccessTime", "GetLastAccessTimeUtc",
    "GetLastWriteTime", "GetLastWriteTimeUtc", "GetUnixFileMode", "GetCurrentDirectory", "GetDirectoryRoot",
    "GetLogicalDrives", "EnumerateFiles", "EnumerateDirectories", "EnumerateFileSystemEntries",
    "GetFiles", "GetDirectories", "GetFileSystemEntries", "EnumerateFileSystemInfos", "GetFileSystemInfos",
    "Length", "Name", "FullName", "Extension", "DirectoryName", "Directory", "Parent", "Root",
    "Attributes", "CreationTime", "CreationTimeUtc", "LastAccessTime", "LastAccessTimeUtc",
    "LastWriteTime", "LastWriteTimeUtc", "IsReadOnly", "UnixFileMode", "LinkTarget", "ResolveLinkTarget", "Refresh",
    "ToString", "Equals", "GetHashCode", "GetType",
  };

  private static readonly HashSet<string> PreviewReadOnlyFileStreamMembers = new(StringComparer.Ordinal)
  {
    "CanRead", "CanSeek", "CanWrite", "CanTimeout", "IsAsync", "Length", "Name", "Position",
    "Read", "ReadAsync", "ReadByte", "ReadAtLeast", "ReadAtLeastAsync", "ReadExactly", "ReadExactlyAsync",
    "BeginRead", "EndRead", "Seek", "Close", "Dispose", "DisposeAsync",
    "ToString", "Equals", "GetHashCode", "GetType",
  };

  // A snippet's own #line directive would map the code after it away from
  // "script", where the guard does not look (that is how it skips the
  // wrapper), so refuse any inside the snippet's body. Ours sit outside it,
  // and hoisted using lines cannot hold one.
  private static IEnumerable<ScriptDiagnostic> FindLineDirectives(SyntaxTree tree, string source)
  {
    var bodyMarker = "#line 1 \"" + ScriptPath + "\"\n";
    var bodyStart = source.IndexOf(bodyMarker, source.IndexOf("public override object Run()", StringComparison.Ordinal), StringComparison.Ordinal) + bodyMarker.Length;
    var bodyEnd = source.LastIndexOf("#line default\n#pragma warning disable CS0162", StringComparison.Ordinal);
    var firstBodyLine = tree.GetText().Lines.GetLinePosition(bodyStart).Line;
    foreach (var trivia in tree.GetRoot().DescendantTrivia())
    {
      if ((trivia.IsKind(SyntaxKind.LineDirectiveTrivia) || trivia.IsKind(SyntaxKind.LineSpanDirectiveTrivia))
          && trivia.SpanStart >= bodyStart && trivia.SpanStart < bodyEnd)
      {
        var position = tree.GetLineSpan(trivia.Span).StartLinePosition;
        yield return new ScriptDiagnostic(position.Line - firstBodyLine + 1, position.Character + 1, GuardDiagnosticId,
          "#line directives are not allowed in a snippet: they move code away from its script lines, past the guard.");
      }
    }
  }

  private static IEnumerable<ScriptDiagnostic> FindGuardViolations(CSharpCompilation compilation, SyntaxTree tree, bool writeMode, bool previewMode)
  {
    var model = compilation.GetSemanticModel(tree);
    var reported = new HashSet<(int, string)>();
    foreach (var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
    {
      var (line, column) = MapToScript(node.GetLocation());
      if (line == 0)
      {
        continue;
      }

      var info = model.GetSymbolInfo(node);
      var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
      if (symbol == null)
      {
        continue;
      }

      var reason = DenyReason(symbol, node, model, writeMode, previewMode);
      if (reason != null && reported.Add((line, symbol.Name)))
      {
        yield return new ScriptDiagnostic(line, column, GuardDiagnosticId, reason);
      }
    }

    if (previewMode)
    {
      foreach (var creation in tree.GetRoot().DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
      {
        if (model.GetOperation(creation) is not IObjectCreationOperation operation
            || operation.Constructor?.ContainingType is not { } writerType
            || !IsPreviewWriterType(writerType)) continue;
        if (InheritsFrom(writerType, "System.IO.FileStream") && IsReadOnlyFileOpen(operation.Arguments)) continue;
        var (line, column) = MapToScript(creation.GetLocation());
        if (line != 0 && reported.Add((line, writerType.Name)))
          yield return new(line, column, GuardDiagnosticId,
            writerType.Name + ": Preview cannot roll back file writes; use mode 'write'.");
      }

      foreach (var expression in tree.GetRoot().DescendantNodes().OfType<ExpressionSyntax>())
      {
        var target = expression switch
        {
          AssignmentExpressionSyntax assignment => assignment.Left,
          PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression)
            || prefix.IsKind(SyntaxKind.PreDecrementExpression) => prefix.Operand,
          PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression)
            || postfix.IsKind(SyntaxKind.PostDecrementExpression) => postfix.Operand,
          _ => null,
        };
        if (target == null || !IsSessionMutationTarget(model, ExpressionOperation(model, target))) continue;
        var (line, column) = MapToScript(expression.GetLocation());
        if (line != 0 && reported.Add((line, "Session")))
          yield return new(line, column, GuardDiagnosticId, "Preview cannot roll back Session writes; Session reads are allowed.");
      }
    }

    // Preview already refuses every layout change above and rolls back anyway.
    if (writeMode && !previewMode
        && FindLayoutViewportViolation(tree, model) is { } layoutViewport)
    {
      yield return layoutViewport;
    }

    if (writeMode)
    {
      yield break;
    }

    // An OpenMode written as a number, such as (OpenMode)1, names no ForWrite
    // symbol for DenyReason to see. Keyed by member name like the loop above,
    // so OpenMode.ForWrite is reported once.
    foreach (var expression in tree.GetRoot().DescendantNodes().OfType<ExpressionSyntax>())
    {
      var mode = OpenModeConstant(model, expression);
      if (mode == null || mode == "ForRead")
      {
        continue;
      }

      var (line, column) = MapToScript(expression.GetLocation());
      if (line != 0 && reported.Add((line, mode)))
      {
        var display = mode.StartsWith("(", StringComparison.Ordinal) ? mode : "OpenMode." + mode;
        yield return new ScriptDiagnostic(line, column, GuardDiagnosticId,
          $"Read mode opens objects for read only: {display} is not allowed. Use mode 'write' to change the drawing.");
      }
    }
  }

  // Issue #43: one write run that created a layout, switched to it and created
  // and erased viewports crashed AutoCAD at commit (AccessViolation in
  // FlushGraphics, live, 2026-10-15). Each part alone ran fine in its own run,
  // so only the combination is refused. This reads the whole source: a branch
  // chosen by Args at run time still counts, so stages must be separate scripts.
  // SetViewport edits an existing viewport and neither creates nor erases one,
  // and it may activate the layout itself, so it is not counted here: it
  // refuses at run time instead (RunCodeHelpers.WithCurrentLayout).
  private const string LayoutManagerTypeName = "Autodesk.AutoCAD.DatabaseServices.LayoutManager";
  private const string ViewportTypeName = "Autodesk.AutoCAD.DatabaseServices.Viewport";

  // Layout switches that bypass LayoutManager: the CTAB and TILEMODE system
  // variables and Database.TileMode. A non-constant variable name is not counted.
  private static bool SwitchesLayoutWithoutManager(ISymbol symbol, SimpleNameSyntax node, SemanticModel model)
  {
    if (symbol is IPropertySymbol && symbol.Name == "TileMode"
        && symbol.ContainingType?.ToDisplayString() == DatabaseTypeName && IsAssignmentTarget(node))
      return true;
    return symbol is IMethodSymbol { Name: "SetSystemVariable" }
      && symbol.ContainingType?.ToDisplayString() is "Autodesk.AutoCAD.ApplicationServices.Application" or "Autodesk.AutoCAD.ApplicationServices.Core.Application"
      && InvocationOf(node) is { } call && model.GetOperation(call) is IInvocationOperation operation
      && operation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == 0)?.Value.ConstantValue
        is { HasValue: true, Value: string variable }
      && (variable.Equals("CTAB", StringComparison.OrdinalIgnoreCase) || variable.Equals("TILEMODE", StringComparison.OrdinalIgnoreCase));
  }

  private static ScriptDiagnostic? FindLayoutViewportViolation(SyntaxTree tree, SemanticModel model)
  {
    var layoutChange = false;
    SyntaxNode? firstViewport = null;
    SyntaxNode? firstErase = null;
    void NoteViewport(SyntaxNode node)
    {
      if (MapToScript(node.GetLocation()).Line != 0 && (firstViewport == null || node.SpanStart < firstViewport.SpanStart))
        firstViewport = node;
    }

    foreach (var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
    {
      if (MapToScript(node.GetLocation()).Line == 0) continue;
      var info = model.GetSymbolInfo(node);
      var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
      if (symbol == null) continue;
      if (InheritsFrom(symbol.ContainingType, LayoutManagerTypeName)
          && (symbol is IMethodSymbol && symbol.Name is "CreateLayout" or "SetCurrentLayoutId"
              || symbol is IPropertySymbol && symbol.Name == "CurrentLayout" && IsAssignmentTarget(node)))
        layoutChange = true;
      else if (SwitchesLayoutWithoutManager(symbol, node, model))
        layoutChange = true;
      // Erase is declared on DBObject, so the receiver's static type decides.
      else if (symbol is IMethodSymbol && symbol.Name == "Erase" && MethodReceiver(node) is { } receiver
          && InheritsFrom(model.GetTypeInfo(receiver).Type as INamedTypeSymbol, ViewportTypeName))
      {
        NoteViewport(node);
        if (firstErase == null || node.SpanStart < firstErase.SpanStart) firstErase = node;
      }
    }

    // Issue #44, live 2026-10-16: the second write run that erased and re-created the viewports on the current
    // layout killed acad.exe (the first such run passed); a plot after the same pattern crashed in the field.
    // Erasing a layout's own overall viewport wedged AutoCAD in a command. So viewports are never erased here.
    if (firstErase != null)
    {
      var (eraseLine, eraseColumn) = MapToScript(firstErase.GetLocation());
      return new ScriptDiagnostic(eraseLine, eraseColumn, GuardDiagnosticId,
        "Erasing viewports crashed AutoCAD (2026-10-16: the second erase-and-re-create of viewports on the current layout killed acad.exe; "
        + "a plot after the same pattern crashed too). Create viewports once and update them in place (SetViewport, or Width, Height, "
        + "CenterPoint, ViewCenter, CustomScale) instead of erasing and re-creating them. See issue #44.");
    }

    foreach (var creation in tree.GetRoot().DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
    {
      if (model.GetOperation(creation) is IObjectCreationOperation { Constructor.ContainingType: { } created }
          && InheritsFrom(created, ViewportTypeName))
        NoteViewport(creation);
    }

    if (!layoutChange || firstViewport == null) return null;
    var (line, column) = MapToScript(firstViewport.GetLocation());
    return new ScriptDiagnostic(line, column, GuardDiagnosticId,
      "Changing or creating a layout and creating/erasing viewports in one write run crashed AutoCAD at commit (AccessViolation in FlushGraphics, 2026-10-15). "
      + "Split it into separate write runs: (1) layout, title block and table without switching layouts, (2) only the CurrentLayout switch, (3) the viewports. "
      + "This check reads the whole script, so stages chosen by an Args value inside one script or codePath file are refused too: keep each stage in its own script or file. See issue #43.");
  }

  // Require explicit constant mode/access and constant sharing/options for
  // FileStream, File.Open and FileInfo.Open. Options must not change the file
  // outside the transaction, even when opening with read-only access.
  private static bool IsReadOnlyFileOpen(IEnumerable<IArgumentOperation> arguments)
  {
    const FileOptions readOnlyOptions = FileOptions.SequentialScan | FileOptions.RandomAccess | FileOptions.Asynchronous;
    var hasMode = false;
    var hasAccess = false;
    foreach (var argument in arguments)
    {
      switch (argument.Parameter?.Type.ToDisplayString())
      {
        case "System.IO.FileMode":
          if (argument.Value.ConstantValue is not { HasValue: true, Value: int mode } || mode != (int)FileMode.Open)
            return false;
          hasMode = true;
          break;
        case "System.IO.FileAccess":
          if (argument.Value.ConstantValue is not { HasValue: true, Value: int access } || access != (int)FileAccess.Read)
            return false;
          hasAccess = true;
          break;
        case "System.IO.FileShare":
          if (argument.Value.ConstantValue is not { HasValue: true, Value: int }) return false;
          break;
        case "System.IO.FileOptions":
          if (argument.Value.ConstantValue is not { HasValue: true, Value: int options }
              || (options & ~(int)readOnlyOptions) != 0) return false;
          break;
        case "System.IO.FileStreamOptions":
          return false;
      }
    }
    return hasMode && hasAccess;
  }

  private static bool IsPreviewWriterType(INamedTypeSymbol type)
  {
    return InheritsFrom(type, "System.IO.FileStream")
      || InheritsFrom(type, "System.IO.TextWriter")
      || InheritsFrom(type, "System.IO.BinaryWriter")
      || InheritsFrom(type, "System.IO.BufferedStream")
      || InheritsFrom(type, "System.IO.Compression.DeflateStream")
      || InheritsFrom(type, "System.IO.Compression.GZipStream")
      || InheritsFrom(type, "System.IO.Compression.BrotliStream")
      || InheritsFrom(type, "System.IO.Compression.ZLibStream")
      || InheritsFrom(type, "System.IO.Compression.ZipArchive");
  }

  private static bool IsPreviewFileMemberAllowed(ISymbol symbol, SimpleNameSyntax node, SemanticModel model)
  {
    if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor })
      return symbol.ContainingType.ToDisplayString() is "System.IO.FileInfo" or "System.IO.DirectoryInfo";
    if (symbol is IPropertySymbol && IsAssignmentTarget(node)) return false;
    if (symbol.ContainingType.ToDisplayString() is "System.IO.File" or "System.IO.FileInfo" && symbol.Name == "Open")
      return InvocationOf(node) is { } call && model.GetOperation(call) is IInvocationOperation operation
        && IsReadOnlyFileOpen(operation.Arguments);
    return PreviewReadOnlyFileMembers.Contains(symbol.Name);
  }

  // The OpenMode member a constant expression stands for ("ForRead",
  // "ForWrite"...), "(OpenMode)n" for a value without a member, or null when
  // the expression is not a constant OpenMode.
  private static string? OpenModeConstant(SemanticModel model, ExpressionSyntax expression)
  {
    var typeInfo = model.GetTypeInfo(expression);
    var type = new[] { typeInfo.Type, typeInfo.ConvertedType }
      .OfType<INamedTypeSymbol>()
      .FirstOrDefault(candidate => candidate.ToDisplayString() == "Autodesk.AutoCAD.DatabaseServices.OpenMode");
    var constant = type == null ? default : model.GetConstantValue(expression);
    if (type == null || !constant.HasValue)
    {
      return null;
    }

    var value = Convert.ToInt64(constant.Value);
    var member = type.GetMembers().OfType<IFieldSymbol>()
      .FirstOrDefault(field => field.HasConstantValue && Convert.ToInt64(field.ConstantValue) == value);
    return member?.Name ?? $"(OpenMode){value}";
  }

  private static string? DenyReason(ISymbol symbol, SimpleNameSyntax node, SemanticModel model, bool writeMode, bool previewMode)
  {
    var type = symbol.ContainingType;
    var name = symbol.Name;
    var typeName = type?.ToDisplayString() ?? string.Empty;
    var display = string.IsNullOrEmpty(typeName) ? name : $"{type!.Name}.{name}";

    // The plugin implementation is loaded in the same assembly as the script
    // base class. Calling its command/queue internals from a snippet can
    // deadlock Civil 3D or bypass the run transaction.
    var outermost = symbol as INamedTypeSymbol ?? type;
    while (outermost?.ContainingType != null) outermost = outermost.ContainingType;
    if (outermost?.ContainingNamespace?.ToDisplayString() == "Civil3DMcpPlugin"
        && outermost.Name is not "RunCodeScript" and not "RunCodeTimeoutException" and not "RunCodeCancelledException")
    {
        return $"{display} is plugin code, not the script API (it can deadlock Civil 3D or queue commands).";
    }

    // Always refused: these wait for a person, open a modal window, or end the process.
    if (symbol is IMethodSymbol)
    {
      if (InheritsFrom(type, "Autodesk.AutoCAD.EditorInput.Editor") && InteractiveEditorMethods.Contains(name))
      {
        return $"{display} waits for user input or needs a running command; runCode runs without user interaction.";
      }

      if (InheritsFrom(type, "Autodesk.AutoCAD.ApplicationServices.Document") && name == "SendStringToExecute")
      {
        return $"{display} queues command-line input that would run after the script, outside its transaction.";
      }

      // ShowDialog and ShowModal of any toolkit (WinForms, WPF, and the
      // Autodesk.AutoCAD.Windows dialogs, which have both), so no list of
      // dialog classes is needed; AdWindows' TaskDialog opens with Show().
      if (name is "ShowDialog" or "ShowModal"
          || (typeName == "Autodesk.Windows.TaskDialog" && name == "Show")
          || (typeName is "Autodesk.AutoCAD.ApplicationServices.Application" or "Autodesk.AutoCAD.ApplicationServices.Core.Application"
              && name is "ShowModalDialog" or "ShowModalWindow" or "ShowAlertDialog" or "Quit")
          || (typeName is "System.Windows.Forms.MessageBox" or "System.Windows.MessageBox" && name == "Show")
          || (typeName == "Microsoft.VisualBasic.Interaction" && name is "InputBox" or "MsgBox"))
      {
        return $"{display} opens a modal window or closes Civil 3D, which would block or end the host.";
      }

      // A nested message loop on the main thread: the run's time budget is
      // never checked until it ends.
      if ((typeName is "System.Windows.Forms.Application" or "System.Windows.Application" && name == "Run")
          || (typeName == "System.Windows.Threading.Dispatcher" && name is "PushFrame" or "Run"))
      {
        return $"{display} runs a message loop that blocks Civil 3D until it ends.";
      }

      if (typeName == "System.Environment" && name is "Exit" or "FailFast")
      {
        return $"{display} ends the Civil 3D process.";
      }
    }

    if (symbol is IEventSymbol && IsHostApi(type))
    {
      return $"Subscribing to {display}: an event handler outlives the run and would fire later, outside any time budget.";
    }

    // TopTransaction hands out the run's transaction without the RunTransaction
    // wrapper, so a Commit or Dispose through it would end the run's transaction.
    if (symbol is IPropertySymbol && name == "TopTransaction"
        && InheritsFrom(type, "Autodesk.AutoCAD.DatabaseServices.TransactionManager"))
    {
      return $"{display} is the run's own transaction: use Tr (or TransactionFor(db) for another drawing).";
    }

    // The run owns Tr and the side-database transactions. A snippet's own
    // Tr.Commit() would commit the change and then make the run's commit fail,
    // so the run would be reported as rolled back (at run time the wrapper
    // throws too; this says so at compile time, with the line).
    if (symbol is IMethodSymbol && name is "Commit" or "Abort" or "Dispose"
        && InheritsFrom(type, "Autodesk.AutoCAD.DatabaseServices.Transaction") && IsRunTransaction(model, node))
    {
      return $"{display}: the run owns this transaction (write mode commits it when the snippet returns, read mode never does). Just return.";
    }

    // Write mode rolls back what goes through Tr (and transactions nested in
    // it). An open-close transaction or ObjectId.Open commits on its own, so
    // a failure later in the script would leave those changes behind.
    if (writeMode && symbol is IMethodSymbol
        && ((name == "StartOpenCloseTransaction" && type?.Name == "TransactionManager")
            || (typeName == "Autodesk.AutoCAD.DatabaseServices.ObjectId" && name == "Open")))
    {
      return $"{display}: write mode rolls back only changes made through Tr, and these commit on their own. Use Tr or Open<T>(id, forWrite: true).";
    }

    // A transaction the snippet starts (write: StartTransaction, read:
    // StartOpenCloseTransaction; each mode refuses the other kind) must end on
    // every path. One left open is aborted by the run through a wrapper of its
    // own, and the snippet's wrapper then ends it again from the finalizer
    // thread, a later Civil 3D crash.
    if (symbol is IMethodSymbol && type?.Name == "TransactionManager"
        && name == (writeMode ? "StartTransaction" : "StartOpenCloseTransaction")
        && !(InvocationOf(node) is { } call && IsUsingResource(call)))
    {
      return $"{display}: start it only as 'using var t = db.TransactionManager.{name}();' or 'using (var t = db.TransactionManager.{name}()) {{ ... }}', so it ends on every path; a transaction left open crashes Civil 3D later.";
    }

    // OpenSide hands out a database the run disposes when it ends (after the
    // transaction that holds its objects); the snippet's wrapper does not own
    // it, so a using or Dispose() on it would only detach that wrapper.
    if (DisposesSideDatabase(symbol, node, model))
    {
      return "OpenSide(...): the run disposes side databases when it ends; remove the using or the Dispose() call.";
    }

    if (previewMode)
    {
      const string outside = " Preview cannot roll back changes outside Tr; use mode 'write'.";
      if (symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol
          && (typeName is "System.IO.File" or "System.IO.Directory"
            || InheritsFrom(type, "System.IO.FileSystemInfo"))
          && !IsPreviewFileMemberAllowed(symbol, node, model))
        return display + ": Preview allows only read-only filesystem members." + outside;
      // Path manipulation is safe, but GetTempFileName also creates a file.
      if (typeName == "System.IO.Path" && name == "GetTempFileName") return display + outside;
      if (symbol is IMethodSymbol or IPropertySymbol
          && (InheritsFrom(type, "System.IO.FileStream")
            || MethodReceiver(node) is { } streamReceiver
              && InheritsFrom(model.GetTypeInfo(streamReceiver).Type as INamedTypeSymbol, "System.IO.FileStream"))
          && !(symbol is IMethodSymbol { MethodKind: MethodKind.Constructor })
          && (!PreviewReadOnlyFileStreamMembers.Contains(name)
            || symbol is IPropertySymbol && IsAssignmentTarget(node)))
        return display + outside;
      if (symbol is IMethodSymbol previewMethod)
      {
        if ((InheritsFrom(type, "Autodesk.AutoCAD.DatabaseServices.TransactionManager")
              && name == "StartTransaction"
            || InheritsFrom(type, "Autodesk.AutoCAD.ApplicationServices.Document") && name == "LockDocument")
            && !(MethodReceiver(node) is { } drawingReceiver
              && IsRunDrawingValue(model, drawingReceiver, new HashSet<ISymbol>(SymbolEqualityComparer.Default))))
          return display + ": Preview allows locks and nested transactions only on the run's drawing (Db or Doc.Database).";
        if (typeName == DatabaseTypeName && DatabaseDenyReason(previewMethod, node, model) is { } reason)
          return reason + outside;
        if (typeName == BaseClassName && name is "WriteFile" or "AttachXref" or "ImportBlock"
            || typeName is "Autodesk.AutoCAD.ApplicationServices.Application" or "Autodesk.AutoCAD.ApplicationServices.Core.Application"
              && name == "SetSystemVariable"
            || InheritsFrom(type, "Autodesk.AutoCAD.DatabaseServices.LayoutManager")
              && name is "CreateLayout" or "DeleteLayout" or "RenameLayout" or "CopyLayout" or "CloneLayout" or "SetCurrentLayoutId")
          return display + outside;
        if (MethodReceiver(node) is { } sessionReceiver
            && IsSessionValue(model, sessionReceiver, new HashSet<ISymbol>(SymbolEqualityComparer.Default))
            && !IsSessionReadOnlyMethod(previewMethod))
          return "Preview cannot roll back Session writes; Session reads are allowed.";
      }
      if (symbol is IPropertySymbol && IsAssignmentTarget(node)
          && (IsHostApi(type) && !InheritsFrom(type, "Autodesk.AutoCAD.DatabaseServices.DBObject")
              && !(typeName == "Autodesk.AutoCAD.DatabaseServices.HostApplicationServices" && name == "WorkingDatabase")
            || MethodReceiver(node) is { } receiver
              && IsSessionValue(model, receiver, new HashSet<ISymbol>(SymbolEqualityComparer.Default))))
        return "Setting " + display + outside;
    }

    if (writeMode)
    {
      return null;
    }

    const string useWrite = " Use mode 'write' to change the drawing.";

    if (symbol is IMethodSymbol && typeName == BaseClassName && name is ("EnsureLayer" or "Tag" or "XClip" or "AttachXref" or "SetViewport" or "ImportBlock"))
    {
      return $"{display} changes the drawing. Use mode 'write'.";
    }

    if (symbol is IFieldSymbol && typeName == "Autodesk.AutoCAD.DatabaseServices.OpenMode" && name is "ForWrite" or "ForNotify")
    {
      return $"Read mode opens objects for read only: {display} is not allowed.{useWrite}";
    }

    if (symbol is IMethodSymbol databaseMethod)
    {
      if (typeName == DatabaseTypeName
          && DatabaseDenyReason(databaseMethod, node, model) is { } databaseReason)
      {
        return databaseReason;
      }

      if (name == "StartTransaction" && type != null && type.Name == "TransactionManager")
      {
        return $"{display}: a regular transaction outside a command clears the drawing's undo history. Use Tr, OpenSide/TransactionFor, or 'using var t = db.TransactionManager.StartOpenCloseTransaction();' in read mode.";
      }

      if ((InheritsFrom(type, "Autodesk.AutoCAD.DatabaseServices.DBObject") && name is "UpgradeOpen" or "UpgradeFromNotify" or "Erase" or "SwapIdWith" or "HandOverTo")
          || (InheritsFrom(type, "Autodesk.AutoCAD.DatabaseServices.BlockTableRecord") && name == "AppendEntity")
          || (InheritsFrom(type, "Autodesk.AutoCAD.DatabaseServices.SymbolTable") && name == "Add")
          || (InheritsFrom(type, "Autodesk.AutoCAD.DatabaseServices.DBDictionary") && name is "SetAt" or "Remove")
          || (InheritsFrom(type, "Autodesk.AutoCAD.DatabaseServices.Transaction") && name == "AddNewlyCreatedDBObject")
          || (InheritsFrom(type, "Autodesk.AutoCAD.DatabaseServices.BlockReference") && name == "ExplodeToOwnerSpace")
          || (InheritsFrom(type, "Autodesk.AutoCAD.DatabaseServices.LayoutManager")
              && name is "CreateLayout" or "DeleteLayout" or "RenameLayout" or "CopyLayout" or "CloneLayout" or "SetCurrentLayoutId")
          || (typeName is "Autodesk.AutoCAD.ApplicationServices.Application" or "Autodesk.AutoCAD.ApplicationServices.Core.Application" && name == "SetSystemVariable"))
      {
        return $"{display} changes a drawing.{useWrite}";
      }

      var ns = type?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
      if (ns.StartsWith("Autodesk.Civil", StringComparison.Ordinal)
          && CivilWritePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
      {
        return $"{display} looks like a Civil 3D change (create/add/update...).{useWrite}";
      }
    }

    // Setters on database-resident objects need ForWrite, which is refused
    // above (and AutoCAD throws eNotOpenForWrite), and setters on objects made
    // in memory are harmless. Database, document, editor and Civil 3D settings
    // properties change the drawing without opening anything.
    if (symbol is IPropertySymbol && IsHostApi(type) && IsAssignmentTarget(node)
        && !InheritsFrom(type, "Autodesk.AutoCAD.DatabaseServices.DBObject")
        && !(typeName == "Autodesk.AutoCAD.DatabaseServices.HostApplicationServices" && name == "WorkingDatabase"))
    {
      return $"Setting {display} changes a drawing or a setting.{useWrite}";
    }

    return null;
  }

  // Follow mutable Session aliases, separately from copied scalars, strings and
  // detached containers. Shallow copies still share their reference-type elements.
  // Reflection/dynamic and mutable aliases passed to other code remain outside
  // this mistake-prevention guard, as they do in read/write modes.
  private static bool IsSessionValue(SemanticModel model, ExpressionSyntax expression, HashSet<ISymbol> visiting) =>
    IsSessionAlias(model, ExpressionOperation(model, expression), visiting);

  private static bool IsSessionReadOnlyMethod(IMethodSymbol method)
  {
    var type = method.ContainingType;
    var name = method.Name;
    if (type.ContainingNamespace.ToDisplayString() == "System.Linq"
        || type.ToDisplayString() == "System.Array" && name == "Clone"
        || name is "TryGetValue" or "ContainsKey" or "Contains" or "GetEnumerator" or "ToArray" or "ToString" or "GetType") return true;
    var collection = type.ContainingNamespace.ToDisplayString() + "." + type.OriginalDefinition.MetadataName;
    return collection switch
    {
      "System.Collections.Generic.List`1" => name is "AsReadOnly" or "BinarySearch"
        or "Exists" or "Find" or "FindAll" or "FindIndex" or "FindLast" or "FindLastIndex"
        or "GetRange" or "IndexOf" or "LastIndexOf" or "TrueForAll",
      "System.Collections.Generic.Dictionary`2" or "System.Collections.Generic.SortedDictionary`2"
        or "System.Collections.Generic.SortedList`2" => name is "ContainsValue" or "IndexOfKey" or "IndexOfValue",
      "System.Collections.Generic.IList`1" => name == "IndexOf",
      _ => false,
    };
  }

  private static bool IsDetachedSessionCopy(IMethodSymbol method)
  {
    var type = method.ContainingType;
    return method.Name is "ToArray" or "ToList" && (type.ContainingNamespace.ToDisplayString() == "System.Linq"
        || type.OriginalDefinition.MetadataName == "ConcurrentDictionary`2"
          && type.ContainingNamespace.ToDisplayString() == "System.Collections.Concurrent")
      || method.Name is "ToArray" or "GetRange" or "FindAll"
        && type.OriginalDefinition.MetadataName == "List`1"
        && type.ContainingNamespace.ToDisplayString() == "System.Collections.Generic"
      || method.Name == "Clone" && type.ToDisplayString() == "System.Array";
  }

  // Roslyn does not give parentheses or nullable suppression their own operation.
  private static IOperation? ExpressionOperation(SemanticModel model, ExpressionSyntax expression)
  {
    while (true)
    {
      if (expression is ParenthesizedExpressionSyntax parentheses) expression = parentheses.Expression;
      else if (expression is PostfixUnaryExpressionSyntax suppressed
          && suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression)) expression = suppressed.Operand;
      else if (expression is RefExpressionSyntax reference) expression = reference.Expression;
      else return model.GetOperation(expression);
    }
  }

  private static bool IsSessionAlias(SemanticModel model, IOperation? operation, HashSet<ISymbol> visiting,
    bool contents = false, int[]? tuplePath = null)
  {
    if (operation == null || operation.Type?.SpecialType == SpecialType.System_String
        || !contents && (tuplePath == null || tuplePath.Length == 0) && operation.Type?.IsValueType == true) return false;
    switch (operation)
    {
      case IConversionOperation conversion:
        return IsSessionAlias(model, conversion.Operand, visiting, contents, tuplePath);
      case IParenthesizedOperation parentheses:
        return IsSessionAlias(model, parentheses.Operand, visiting, contents, tuplePath);
      case IPropertyReferenceOperation property when property.Property.Name == "Session"
          && InheritsFrom(property.Property.ContainingType, BaseClassName):
        return true;
      case ILocalReferenceOperation reference:
        var local = reference.Local;
        if (!visiting.Add(local)) return false;
        try
        {
          return SessionLocalValues(model, local, operation.Syntax.SyntaxTree)
            .Any(value => IsSessionAlias(model, value.Value, visiting, contents,
              value.TuplePath.Concat(tuplePath ?? Array.Empty<int>()).ToArray()))
            || IsSessionOutOrIterationValue(model, local, operation.Syntax.SyntaxTree, visiting);
        }
        finally { visiting.Remove(local); }
      case IArrayElementReferenceOperation element:
        return IsSessionAlias(model, element.ArrayReference, visiting, contents: true);
      case IPropertyReferenceOperation property:
        return IsSessionAlias(model, property.Instance, visiting, contents: true);
      case IFieldReferenceOperation field:
        if (field.Field.ContainingType.IsTupleType)
        {
          var elements = field.Field.ContainingType.TupleElements;
          var index = Enumerable.Range(0, elements.Length).FirstOrDefault(i =>
            SymbolEqualityComparer.Default.Equals(elements[i].CorrespondingTupleField, field.Field.CorrespondingTupleField), -1);
          if (index >= 0) return IsSessionAlias(model, field.Instance, visiting, contents,
            new[] { index }.Concat(tuplePath ?? Array.Empty<int>()).ToArray());
        }
        return IsSessionAlias(model, field.Instance, visiting, contents: true);
      case ITupleOperation tuple:
        return tuplePath is { Length: > 0 }
          ? tuplePath[0] < tuple.Elements.Length && IsSessionAlias(model, tuple.Elements[tuplePath[0]], visiting,
              contents, tuplePath.Skip(1).ToArray())
          : contents && tuple.Elements.Any(element => IsSessionAlias(model, element, visiting, contents: true));
      case IInvocationOperation invocation:
        var method = invocation.TargetMethod;
        if (!contents && IsDetachedSessionCopy(method)) return false;
        return IsSessionAlias(model, invocation.Instance, visiting, contents)
          || method.IsExtensionMethod && invocation.Arguments.Any(argument => argument.Parameter?.Ordinal == 0
            && IsSessionAlias(model, argument.Value, visiting, contents));
      case IConditionalOperation conditional:
        return IsSessionAlias(model, conditional.WhenTrue, visiting, contents, tuplePath)
          || IsSessionAlias(model, conditional.WhenFalse, visiting, contents, tuplePath);
      case ICoalesceOperation coalesce:
        return IsSessionAlias(model, coalesce.Value, visiting, contents, tuplePath)
          || IsSessionAlias(model, coalesce.WhenNull, visiting, contents, tuplePath);
      case IConditionalAccessOperation conditionalAccess:
        return IsSessionAlias(model, conditionalAccess.WhenNotNull, visiting, contents, tuplePath);
      case IConditionalAccessInstanceOperation instance:
        var access = instance.Syntax.AncestorsAndSelf().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault();
        return access != null && IsSessionAlias(model, ExpressionOperation(model, access.Expression), visiting, contents, tuplePath);
    }
    return false;
  }

  // Select the matching tuple slot, so an independent sibling is not tainted.
  private static IEnumerable<(IOperation? Value, int[] TuplePath)> SessionLocalValues(
    SemanticModel model, ILocalSymbol local, SyntaxTree tree)
  {
    foreach (var value in LocalValues(model, local, tree))
      yield return (ExpressionOperation(model, value), Array.Empty<int>());
    foreach (var assignment in tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>())
      if (model.GetOperation(assignment) is IDeconstructionAssignmentOperation deconstruction
          && DeconstructionPath(deconstruction.Target, local) is { } path)
        yield return (deconstruction.Value, path);
  }

  private static int[]? DeconstructionPath(IOperation target, ILocalSymbol local)
  {
    if (target is IDeclarationExpressionOperation declaration) return DeconstructionPath(declaration.Expression, local);
    if (target is ILocalReferenceOperation reference)
      return SymbolEqualityComparer.Default.Equals(reference.Local, local) ? Array.Empty<int>() : null;
    if (target is ITupleOperation tuple)
      for (var i = 0; i < tuple.Elements.Length; i++)
        if (DeconstructionPath(tuple.Elements[i], local) is { } path) return new[] { i }.Concat(path).ToArray();
    return null;
  }

  private static bool IsSessionOutOrIterationValue(SemanticModel model, ILocalSymbol local, SyntaxTree tree,
    HashSet<ISymbol> visiting)
  {
    foreach (var argument in tree.GetRoot().DescendantNodes().OfType<ArgumentSyntax>()
        .Where(argument => argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)))
    {
      if (model.GetOperation(argument) is not IArgumentOperation output
          || argument.Parent?.Parent is not InvocationExpressionSyntax call
          || model.GetOperation(call) is not IInvocationOperation invocation
          || invocation.TargetMethod.Name != "TryGetValue") continue;
      var value = output.Value is IDeclarationExpressionOperation declaration ? declaration.Expression : output.Value;
      if (value is ILocalReferenceOperation reference && SymbolEqualityComparer.Default.Equals(reference.Local, local)
          && IsSessionAlias(model, invocation.Instance, visiting, contents: true)) return true;
    }

    foreach (var declaration in local.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax()))
    {
      var source = (declaration as ForEachStatementSyntax)?.Expression;
      if (declaration is SingleVariableDesignationSyntax designation)
        source = designation.Ancestors().OfType<ForEachVariableStatementSyntax>()
          .FirstOrDefault(candidate => candidate.Variable.Span.Contains(designation.Span))?.Expression;
      // Iteration copies the container's elements, not the container itself.
      // A shallow copy therefore still exposes Session's mutable values.
      if (source != null && IsSessionAlias(model, ExpressionOperation(model, source), visiting, contents: true))
        return true;
    }
    return false;
  }

  // An element or member write mutates its container even when the target is a
  // scalar. Reassignment/increment of an independent local does not.
  private static bool IsSessionMutationTarget(SemanticModel model, IOperation? operation, HashSet<ISymbol>? visiting = null)
  {
    visiting ??= new(SymbolEqualityComparer.Default);
    if (operation is ILocalReferenceOperation reference && reference.Local.RefKind != RefKind.None)
    {
      if (!visiting.Add(reference.Local)) return false;
      try
      {
        return SessionLocalValues(model, reference.Local, operation.Syntax.SyntaxTree)
          .Any(value => IsSessionMutationTarget(model, value.Value, visiting));
      }
      finally { visiting.Remove(reference.Local); }
    }
    return operation switch
    {
      IConversionOperation conversion => IsSessionMutationTarget(model, conversion.Operand, visiting),
      IParenthesizedOperation parentheses => IsSessionMutationTarget(model, parentheses.Operand, visiting),
      IArrayElementReferenceOperation element => IsSessionAlias(model, element.ArrayReference, new(SymbolEqualityComparer.Default)),
      IPropertyReferenceOperation property => IsSessionAlias(model, property.Instance, new(SymbolEqualityComparer.Default))
        || IsSessionMutationTarget(model, property.Instance, visiting),
      IFieldReferenceOperation field => IsSessionAlias(model, field.Instance, new(SymbolEqualityComparer.Default))
        || IsSessionMutationTarget(model, field.Instance, visiting),
      ITupleOperation tuple => tuple.Elements.Any(element => IsSessionMutationTarget(model, element, visiting)),
      _ => false,
    };
  }

  private static IEnumerable<ExpressionSyntax> LocalValues(SemanticModel model, ILocalSymbol local, SyntaxTree tree) =>
    local.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax())
      .OfType<VariableDeclaratorSyntax>().Where(variable => variable.Initializer != null)
      .Select(variable => variable.Initializer!.Value)
      .Concat(tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
        .Where(assignment => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, local))
        .Select(assignment => assignment.Right));

  // Prove every definition of a document/database/transaction-manager alias
  // belongs to this run. A transaction on another database is not nested in Tr.
  private static bool IsRunDrawingValue(SemanticModel model, ExpressionSyntax expression, HashSet<ISymbol> visiting)
  {
    switch (ExpressionOperation(model, expression))
    {
      case IConversionOperation conversion when conversion.Operand.Syntax is ExpressionSyntax operand:
        return IsRunDrawingValue(model, operand, visiting);
      case IParenthesizedOperation parentheses when parentheses.Operand.Syntax is ExpressionSyntax operand:
        return IsRunDrawingValue(model, operand, visiting);
      case IConditionalOperation conditional when conditional.WhenTrue?.Syntax is ExpressionSyntax whenTrue
          && conditional.WhenFalse?.Syntax is ExpressionSyntax whenFalse:
        return IsRunDrawingValue(model, whenTrue, visiting) && IsRunDrawingValue(model, whenFalse, visiting);
      case IPropertyReferenceOperation property:
        if (InheritsFrom(property.Property.ContainingType, BaseClassName)
            && property.Property.Name is "Db" or "Doc") return true;
        return (property.Property.Name == "Database"
              && InheritsFrom(property.Property.ContainingType, "Autodesk.AutoCAD.ApplicationServices.Document")
            || property.Property.Name == "TransactionManager" && property.Property.ContainingType.ToDisplayString() == DatabaseTypeName)
          && property.Instance?.Syntax is ExpressionSyntax instance && IsRunDrawingValue(model, instance, visiting);
      case ILocalReferenceOperation reference:
        var local = reference.Local;
        if (local.RefKind != RefKind.None || !visiting.Add(local)) return false;
        try
        {
          // Ref/out exposure and deconstruction make provenance ambiguous;
          // LocalValues tracks only direct assignments to this local.
          foreach (var name in expression.SyntaxTree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
          {
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name).Symbol, local)) continue;
            if (name.Ancestors().Any(parent => parent is RefExpressionSyntax
                || parent is ArgumentSyntax argument && argument.RefOrOutKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword))
              return false;
            var assignment = name.Ancestors().OfType<AssignmentExpressionSyntax>()
              .FirstOrDefault(candidate => candidate.Left.Span.Contains(name.Span));
            if (assignment != null && (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                || !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, local))) return false;
          }
          var values = LocalValues(model, local, expression.SyntaxTree).ToList();
          return values.Count > 0 && values.All(value => IsRunDrawingValue(model, value, visiting));
        }
        finally { visiting.Remove(local); }
      default:
        return false;
    }
  }

  private static string? DatabaseDenyReason(IMethodSymbol method, SimpleNameSyntax node, SemanticModel model)
  {
    const string useWrite = " Use mode 'write' to change the drawing.";
    var display = "Database." + method.Name;
    if (DatabaseWriteMethods.Contains(method.Name))
    {
      return $"{display} changes a drawing or database state.{useWrite}";
    }

    var call = InvocationOf(node);
    if (method.Name == "GetObjectId")
    {
      // true creates a stub; a method group or a nonconstant flag cannot prove
      // this is just a lookup. Named/reordered arguments use Roslyn's parameters.
      return call != null && IsConstant(model, CallArgument(model, call, 0), false)
        ? null
        : $"{display} can create an object ID stub. Read mode requires createIfNotFound: false.{useWrite}";
    }

    ExpressionSyntax? destination;
    var ownerId = false;
    switch (method.Name)
    {
      case "ReadDwgFile":
      case "DxfIn":
      case "ApplyPartialOpenFilters":
      case "DisablePartialOpen":
        destination = call == null ? null : MethodReceiver(node);
        break;
      case "Wblock" when method.ReturnType.ToDisplayString() == DatabaseTypeName:
        // These overloads create a new database; they do not change the source.
        return null;
      case "Wblock":
        destination = call == null ? null : CallArgument(model, call, 0);
        break;
      case "WblockCloneObjects":
        destination = call == null ? null : CallArgument(model, call, 1);
        ownerId = true;
        break;
      default:
        return null;
    }

    var hasDetachedValue = false;
    var detached = destination != null && IsDetachedTarget(model, destination, ownerId,
      new HashSet<ISymbol>(SymbolEqualityComparer.Default), ref hasDetachedValue) && hasDetachedValue;
    return detached ? null
      : $"{display}: read mode requires a proven detached {(ownerId ? "destination owner's database" : "destination database")}; Db, open drawings and ambiguous targets are refused.{useWrite}";
  }

  private static ExpressionSyntax? MethodReceiver(SimpleNameSyntax node) => node.Parent switch
  {
    MemberAccessExpressionSyntax access when access.Name == node => access.Expression,
    MemberBindingExpressionSyntax binding when binding.Parent is InvocationExpressionSyntax invocation
      && invocation.Parent is ConditionalAccessExpressionSyntax conditional => conditional.Expression,
    _ => null,
  };

  private static ExpressionSyntax? CallArgument(SemanticModel model, InvocationExpressionSyntax call, int ordinal) =>
    (model.GetOperation(call) as IInvocationOperation)?.Arguments
      .FirstOrDefault(argument => argument.Parameter?.Ordinal == ordinal)?.Value.Syntax as ExpressionSyntax;

  private static bool IsConstant(SemanticModel model, ExpressionSyntax? expression, bool value) =>
    expression != null && model.GetConstantValue(expression) is { HasValue: true, Value: bool actual } && actual == value;

  // A conservative provenance check, not execution or runtime reflection. Every
  // nonnull definition of a local must be detached, even in branches/closures or after
  // the call: aliases, reassignments and ref/out uses must not hide an open Db.
  // Null/default database values are harmless, but cannot alone prove a target.
  // hasDetachedValue records whether any definition proves a detached database.
  // Unknown factories, parameters, Session values and cycles remain ambiguous.
  private static bool IsDetachedTarget(SemanticModel model, ExpressionSyntax expression, bool ownerId,
    HashSet<ISymbol> visiting, ref bool hasDetachedValue)
  {
    switch (expression)
    {
      case ParenthesizedExpressionSyntax parentheses:
        return IsDetachedTarget(model, parentheses.Expression, ownerId, visiting, ref hasDetachedValue);
      case CastExpressionSyntax cast:
        return IsDetachedTarget(model, cast.Expression, ownerId, visiting, ref hasDetachedValue);
      case PostfixUnaryExpressionSyntax suppressed when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression):
        return IsDetachedTarget(model, suppressed.Operand, ownerId, visiting, ref hasDetachedValue);
      case ConditionalExpressionSyntax conditional:
        return IsDetachedTarget(model, conditional.WhenTrue, ownerId, visiting, ref hasDetachedValue)
          && IsDetachedTarget(model, conditional.WhenFalse, ownerId, visiting, ref hasDetachedValue);
      case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
        return IsDetachedTarget(model, coalesce.Left, ownerId, visiting, ref hasDetachedValue)
          && IsDetachedTarget(model, coalesce.Right, ownerId, visiting, ref hasDetachedValue);
    }

    if (!ownerId && model.GetConstantValue(expression) is { HasValue: true, Value: null }) return true;

    var symbol = model.GetSymbolInfo(expression).Symbol;
    if (symbol is ILocalSymbol local)
    {
      if (!visiting.Add(local)) return false;
      try
      {
        var values = local.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax())
          .OfType<VariableDeclaratorSyntax>().Where(variable => variable.Initializer != null)
          .Select(variable => variable.Initializer!.Value).ToList();
        foreach (var name in expression.SyntaxTree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
        {
          if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name).Symbol, local)) continue;
          SyntaxNode use = name;
          while (use.Parent is ParenthesizedExpressionSyntax
              || use.Parent is PostfixUnaryExpressionSyntax suppressed
                && suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression))
          {
            use = use.Parent;
          }
          if (use.Parent is ArgumentSyntax argument && argument.RefOrOutKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword
              || use.Parent is RefExpressionSyntax) return false;
          var assignment = name.Ancestors().OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault(candidate => candidate.Left.Span.Contains(name.Span));
          if (assignment == null) continue;
          if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
              || !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, local)) return false;
          values.Add(assignment.Right);
        }
        if (values.Count == 0) return false;
        foreach (var value in values)
        {
          if (!IsDetachedTarget(model, value, ownerId, visiting, ref hasDetachedValue)) return false;
        }
        return true;
      }
      finally { visiting.Remove(local); }
    }

    if (!ownerId && model.GetOperation(expression) is IObjectCreationOperation creation
        && creation.Type?.ToDisplayString() == DatabaseTypeName)
    {
      var noDocument = creation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "noDocument");
      if (noDocument?.Value.ConstantValue is not { HasValue: true, Value: true }) return false;
      hasDetachedValue = true;
      return true;
    }

    if (expression is InvocationExpressionSyntax call && symbol is IMethodSymbol method)
    {
      if (!ownerId)
      {
        var detached = IsOpenSide(method) || (method.ContainingType.ToDisplayString() == DatabaseTypeName
          && method.Name == "Wblock" && method.ReturnType.ToDisplayString() == DatabaseTypeName);
        hasDetachedValue |= detached;
        return detached;
      }
      if ((method.ContainingType.ToDisplayString() == DatabaseTypeName && method.Name == "GetObjectId")
          && call.Expression is MemberAccessExpressionSyntax lookup)
      {
        return IsDetachedTarget(model, lookup.Expression, false, visiting, ref hasDetachedValue);
      }
      if (method.ContainingType.ToDisplayString() == "Autodesk.AutoCAD.DatabaseServices.SymbolUtilityServices"
          && method.Name is "GetBlockModelSpaceId" or "GetBlockPaperSpaceId")
      {
        return CallArgument(model, call, 0) is { } database
          && IsDetachedTarget(model, database, false, visiting, ref hasDetachedValue);
      }
    }

    if (expression is MemberAccessExpressionSyntax access && symbol is IPropertySymbol property)
    {
      if (ownerId && property.ContainingType.ToDisplayString() == DatabaseTypeName
          && property.Type.ToDisplayString() == ObjectIdTypeName)
      {
        return IsDetachedTarget(model, access.Expression, false, visiting, ref hasDetachedValue);
      }
      if (!ownerId && property.ContainingType.ToDisplayString() == ObjectIdTypeName && property.Name == "Database")
      {
        return IsDetachedTarget(model, access.Expression, true, visiting, ref hasDetachedValue);
      }
    }
    return false;
  }

  private static bool IsHostApi(INamedTypeSymbol? type)
  {
    var ns = type?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
    return ns.StartsWith("Autodesk.AutoCAD", StringComparison.Ordinal)
      || ns.StartsWith("Autodesk.Civil", StringComparison.Ordinal)
      || ns.StartsWith("Autodesk.Aec", StringComparison.Ordinal);
  }

  // "Tr.Commit()" or "TransactionFor(db).Commit()": the receiver is the
  // script's Tr property or the result of its TransactionFor method.
  private static bool IsRunTransaction(SemanticModel model, SimpleNameSyntax node)
  {
    if (node.Parent is not MemberAccessExpressionSyntax access || access.Name != node)
    {
      return false;
    }

    var receiver = model.GetSymbolInfo(access.Expression).Symbol;
    if (receiver is IPropertySymbol { Name: "Tr" } property && InheritsFrom(property.ContainingType, BaseClassName))
    {
      return true;
    }

    return access.Expression is InvocationExpressionSyntax invocation
      && model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "TransactionFor" } method
      && InheritsFrom(method.ContainingType, BaseClassName);
  }

  // The call "M(...)" or "x.M(...)" whose method name is node, or null (a
  // method group, for example).
  private static InvocationExpressionSyntax? InvocationOf(SimpleNameSyntax node)
  {
    SyntaxNode callee = node.Parent switch
    {
      MemberAccessExpressionSyntax access when access.Name == node => access,
      MemberBindingExpressionSyntax binding when binding.Name == node => binding,
      _ => node,
    };
    return callee.Parent is InvocationExpressionSyntax invocation && invocation.Expression == callee ? invocation : null;
  }

  // The resource of "using (e)", "using (var v = e)" or "using var v = e;".
  private static bool IsUsingResource(ExpressionSyntax expression)
  {
    if (expression.Parent is UsingStatementSyntax statement && statement.Expression == expression)
    {
      return true;
    }

    return expression.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } }
      && (declaration.Parent is UsingStatementSyntax
          || (declaration.Parent is LocalDeclarationStatementSyntax local && local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)));
  }

  // A using over OpenSide(...), or over a local initialised directly from it,
  // or Dispose() called on either.
  private static bool DisposesSideDatabase(ISymbol symbol, SimpleNameSyntax node, SemanticModel model)
  {
    if (IsOpenSide(symbol))
    {
      return InvocationOf(node) is { } call && IsUsingResource(call);
    }

    if (symbol is ILocalSymbol && IsUsingResource(node))
    {
      return IsSideDatabase(model, node);
    }

    return symbol is IMethodSymbol { Name: "Dispose" }
      && node.Parent is MemberAccessExpressionSyntax access && access.Name == node
      && IsSideDatabase(model, access.Expression);
  }

  // OpenSide(...) itself, or a local whose declaration initialises it from OpenSide(...).
  private static bool IsSideDatabase(SemanticModel model, ExpressionSyntax expression)
  {
    if (expression is InvocationExpressionSyntax invocation)
    {
      return IsOpenSide(model.GetSymbolInfo(invocation).Symbol);
    }

    return model.GetSymbolInfo(expression).Symbol is ILocalSymbol local
      && local.DeclaringSyntaxReferences.Any(reference =>
        reference.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: InvocationExpressionSyntax value }
        && IsOpenSide(model.GetSymbolInfo(value).Symbol));
  }

  // RunCodeScript.OpenSide/OpenSideOrNull (not local functions of the snippet).
  private static bool IsOpenSide(ISymbol? symbol) =>
    symbol is IMethodSymbol { Name: "OpenSide" or "OpenSideOrNull" } method && method.ContainingType?.ToDisplayString() == BaseClassName;

  // Every way of writing to it: X = v, X += v, X++, --X, (X) = v, X!++,
  // (X, y) = (...) at any tuple depth, and C# 14's Db?.X = v.
  private static bool IsAssignmentTarget(SimpleNameSyntax node)
  {
    SyntaxNode target = node.Parent switch
    {
      MemberAccessExpressionSyntax access when access.Name == node => access,
      MemberBindingExpressionSyntax binding => binding,
      _ => node,
    };

    while (true)
    {
      switch (target.Parent)
      {
        case ParenthesizedExpressionSyntax parenthesized:
          target = parenthesized;
          continue;
        // Db.X!++ compiles and writes Db.X.
        case PostfixUnaryExpressionSyntax suppressed when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression):
          target = suppressed;
          continue;
        // A tuple element is written only when the tuple is a deconstruction
        // target; on the right side or as an argument it is a read.
        case ArgumentSyntax { Parent: TupleExpressionSyntax tuple }:
          target = tuple;
          continue;
        case AssignmentExpressionSyntax assignment:
          return assignment.Left == target;
        // C# itself refuses a property here today (CS8186); kept for completeness.
        case ForEachVariableStatementSyntax loop:
          return loop.Variable == target;
        case PrefixUnaryExpressionSyntax prefix:
          return prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression);
        case PostfixUnaryExpressionSyntax postfix:
          return postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression);
        default:
          return false;
      }
    }
  }

  private static bool InheritsFrom(INamedTypeSymbol? type, string fullName)
  {
    for (var current = type; current != null; current = current.BaseType)
    {
      if (current.ToDisplayString() == fullName)
      {
        return true;
      }
    }

    return false;
  }
}

// Kept outside ScriptCompiler so callers can hold results without loading Roslyn.
internal sealed record ScriptDiagnostic(int Line, int Column, string Code, string Message);

internal sealed class ScriptCompileResult
{
  public byte[]? Assembly { get; init; }
  public byte[]? Pdb { get; init; }
  public string TypeName { get; init; } = "";
  public List<ScriptDiagnostic> Errors { get; } = new();
  public List<ScriptDiagnostic> Warnings { get; } = new();
  public int ReferenceCount { get; init; }
  public bool Succeeded => Assembly != null && Errors.Count == 0;
}
