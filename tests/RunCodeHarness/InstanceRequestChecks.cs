using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Execute the production request-validation statements without loading AutoCAD.
internal static class InstanceRequestChecks
{
  internal static void Run(string repoRoot, Action<bool, string> expect)
  {
    var source = File.ReadAllText(Path.Combine(repoRoot, "Civil3D-MCP-Plugin", "PluginRuntime.cs"));
    var method = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
      .OfType<MethodDeclarationSyntax>().Single(node => node.Identifier.Text == "HandleRawRequestAsync");
    var statements = method.Body!.Statements.TakeWhile(statement => statement is not LocalDeclarationStatementSyntax local
      || !local.Declaration.Variables.Any(variable => variable.Identifier.Text == "previousOperation")).ToList();
    if (statements.Count == method.Body.Statements.Count) throw new InvalidOperationException("Request dispatch boundary not found.");
    var fixture = """
      using System;
      using System.Text.Json.Nodes;
      namespace Civil3DMcpPlugin;
      public static class RequestFixture
      {
        private static Discovery? _discovery;
        private sealed class Discovery
        {
          public bool AllowsRequestIdentity(JsonNode? identity) => identity == null || identity["pid"]?.GetValue<int>() == 202;
        }
        public static void Initialize() => _discovery = new Discovery();
        public static string Dispatch(string rawRequest)
        {
          __VALIDATION__
          return "DISPATCHED";
        }
      }
      """.Replace("__VALIDATION__", string.Join(Environment.NewLine, statements));
    var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
      .Select(path => MetadataReference.CreateFromFile(path));
    var protocol = File.ReadAllText(Path.Combine(repoRoot, "Civil3D-MCP-Plugin", "JsonRpcProtocol.cs"));
    var compilation = CSharpCompilation.Create("RequestInitializationFixture",
      [CSharpSyntaxTree.ParseText(fixture), CSharpSyntaxTree.ParseText(protocol)], references,
      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    using var output = new MemoryStream();
    var emit = compilation.Emit(output);
    if (!emit.Success) throw new InvalidOperationException(string.Join(Environment.NewLine, emit.Diagnostics));
    var type = Assembly.Load(output.ToArray()).GetType("Civil3DMcpPlugin.RequestFixture")!;
    var dispatch = type.GetMethod("Dispatch")!;
    string Send(int? pid, bool legacyFallback = false) => (string)dispatch.Invoke(null, [new JsonObject
    {
      ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "createPolyline", ["params"] = new JsonObject(),
      ["instanceIdentity"] = pid.HasValue ? new JsonObject { ["pid"] = pid.Value } : null,
      ["legacyFallback"] = legacyFallback,
    }.ToJsonString()])!;
    bool Stale(string reply) => JsonNode.Parse(reply)?["error"]?["data"]?["code"]?.GetValue<string>() == "CIVIL3D.STALE_INSTANCE";
    expect(Stale(Send(101)), "listener initialization rejects a mutation pinned to the replaced process before dispatch");
    expect(Stale(Send(null)), "listener initialization also rejects identity-free mutations before dispatch");
    type.GetMethod("Initialize")!.Invoke(null, null);
    expect(Stale(Send(101)), "initialized replacement listener still rejects the old process identity");
    expect(Send(202) == "DISPATCHED", "initialized listener dispatches a matching request");
    expect(Stale(Send(null, legacyFallback: true)), "discovery-capable user listener rejects undiscovered fixed routing before dispatch");
    expect(Send(null) == "DISPATCHED", "explicitly authorized identity-free fixed routing retains user listener compatibility");
  }
}
