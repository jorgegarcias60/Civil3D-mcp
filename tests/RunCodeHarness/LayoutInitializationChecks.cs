using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;

// Compile the production helper against managed stand-ins for AutoCAD objects.
internal static class LayoutInitializationChecks
{
  internal static void Run(string repoRoot, Action<bool, string> expect)
  {
    var source = File.ReadAllText(Path.Combine(repoRoot, "Civil3D-MCP-Plugin", "PlotCommands.cs"));
    var methods = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
      .OfType<MethodDeclarationSyntax>().ToList();
    var helper = methods.Single(method => method.Identifier.Text == "IsInitialized");
    var fixture = """
      using System;
      using System.Collections;
      using System.Collections.Generic;
      public static class LayoutInitializationFixture
      {
        private enum OpenMode { ForRead }
        private readonly record struct ObjectId(int Value);
        private class Entity { }
        private sealed class Viewport : Entity { }
        private sealed class Line : Entity { }
        private sealed class BlockTableRecord : IEnumerable<ObjectId>
        {
          public List<ObjectId> Entities = new();
          public IEnumerator<ObjectId> GetEnumerator() => Entities.GetEnumerator();
          IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
        private sealed class Layout
        {
          public bool ModelType;
          public bool CacheFails;
          public List<ObjectId> Cached = new();
          public ObjectId BlockTableRecordId = new(1);
          public List<ObjectId> GetViewports() => CacheFails ? throw new Exception("cache") : Cached;
        }
        private sealed class Transaction
        {
          public Dictionary<ObjectId, object> Objects = new();
          public int Reads;
          public object GetObject(ObjectId id, OpenMode mode)
          {
            Reads++;
            return Objects[id];
          }
        }
        __HELPER__
        private static bool Check(bool model, bool warm, bool savedViewport, bool otherEntity,
          bool cacheFails = false, bool recordFails = false)
        {
          var layout = new Layout { ModelType = model, CacheFails = cacheFails };
          var transaction = new Transaction();
          var record = new BlockTableRecord();
          if (warm) layout.Cached.Add(new ObjectId(2));
          if (otherEntity) { record.Entities.Add(new ObjectId(4)); transaction.Objects[new ObjectId(4)] = new Line(); }
          if (savedViewport) { record.Entities.Add(new ObjectId(3)); transaction.Objects[new ObjectId(3)] = new Viewport(); }
          if (!recordFails) transaction.Objects[layout.BlockTableRecordId] = record;
          var initialized = IsInitialized(layout, transaction);
          return initialized == (model || warm || savedViewport) && (!model || transaction.Reads == 0);
        }
        public static bool[] Run() =>
        [
          Check(false, false, true, false),
          Check(false, false, false, false),
          Check(false, true, false, false),
          Check(false, false, false, true),
          Check(false, false, true, true),
          Check(false, false, true, false, cacheFails: true),
          Check(false, false, false, false, recordFails: true),
          Check(true, false, false, false)
        ];
      }
      """.Replace("__HELPER__", helper.ToFullString());
    var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
      .Select(path => MetadataReference.CreateFromFile(path));
    var compilation = CSharpCompilation.Create("LayoutInitializationFixture",
      [CSharpSyntaxTree.ParseText(fixture)], references,
      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    using var output = new MemoryStream();
    var emit = compilation.Emit(output);
    if (!emit.Success)
      throw new InvalidOperationException(string.Join(Environment.NewLine, emit.Diagnostics));
    var assembly = Assembly.Load(output.ToArray());
    var checks = (bool[])assembly.GetType("LayoutInitializationFixture")!.GetMethod("Run")!.Invoke(null, null)!;
    var names = new[] {
      "saved viewport initializes a layout when the cache is empty",
      "empty cache and empty paper space remain uninitialized",
      "a warm viewport cache initializes the layout",
      "non-viewport paper-space entities do not initialize a layout",
      "a saved viewport is found after a non-viewport entity",
      "saved viewport works when the cache is unreadable",
      "an unreadable paper-space record fails closed",
      "model layout is initialized without reading paper space",
    };
    for (var index = 0; index < checks.Length; index++) expect(checks[index], names[index]);

    var calls = methods.SelectMany(method => method.DescendantNodes().OfType<InvocationExpressionSyntax>()
      .Where(call => call.Expression is IdentifierNameSyntax name && name.Identifier.Text == "IsInitialized")
      .Select(call => (Caller: method.Identifier.Text, Call: call))).ToList();
    expect(calls.Count == 4 && calls.All(item => item.Call.ArgumentList.Arguments.Count == 2
      && item.Call.ArgumentList.Arguments[1].Expression.ToString() == "transaction")
      && calls.Select(item => item.Caller).Order().SequenceEqual(new[] {
        "ReadLayoutSnapshots", "ReadLayoutSnapshots", "ToLayoutSummary", "ValidatePublishLayout" }.Order()),
      "publish validation, open and side database snapshots, and layout summaries pass their transaction");
  }
}
