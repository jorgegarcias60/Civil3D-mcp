using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

internal static class RunCodeArguments
{
  // API 6's server supplies canonical JSON to preserve its number/Unicode
  // formatting across runtimes. Raw RPC callers use the sorted-key fallback.
  internal static string CanonicalJson(JsonObject args, string? supplied)
  {
    if (supplied != null)
    {
      if (JsonNode.Parse(supplied) is not JsonObject parsed || !JsonNode.DeepEquals(args, parsed))
        throw new ArgumentException("'argsJson' must encode the same object as 'args'.");
      return supplied;
    }
    JsonNode? Sort(JsonNode? node) => node switch
    {
      JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Sort(pair.Value))).ToArray()),
      JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
      _ => node?.DeepClone(),
    };
    return Sort(args)!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
  }
}
