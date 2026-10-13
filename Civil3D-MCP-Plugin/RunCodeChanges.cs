using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

// Final transaction state only. No Autodesk types, events or host calls.
internal sealed record RunCodeChange(string Type, string? Layer, bool IsNewObject,
  bool IsErased, bool IsModified, string? Handle);

internal static class RunCodeChanges
{
  internal const int HandleLimit = 200;

  internal static JsonObject Reduce(IEnumerable<RunCodeChange> objects)
  {
    var groups = new Dictionary<(string Type, string? Layer), long[]>();
    var totals = new long[3];
    var erased = new SortedSet<string>(StringComparer.Ordinal);
    var modified = new SortedSet<string>(StringComparer.Ordinal);
    var truncated = false;
    foreach (var obj in objects)
    {
      var kind = obj.IsNewObject ? (obj.IsErased ? -1 : 0)
        : obj.IsErased ? 1 : obj.IsModified ? 2 : -1;
      if (kind < 0) continue;
      if (!groups.TryGetValue((obj.Type, obj.Layer), out var counts))
        groups[(obj.Type, obj.Layer)] = counts = new long[3];
      counts[kind]++;
      totals[kind]++;
      if (kind == 0 || string.IsNullOrEmpty(obj.Handle)) continue;
      var handles = kind == 1 ? erased : modified;
      handles.Add(obj.Handle.ToUpperInvariant());
      if (handles.Count > HandleLimit)
      {
        handles.Remove(handles.Max!);
        truncated = true;
      }
    }
    JsonObject Counts(long[] counts) => new()
    {
      ["added"] = counts[0], ["erased"] = counts[1], ["modified"] = counts[2],
    };
    var byType = new JsonArray();
    foreach (var group in groups.OrderBy(pair => pair.Key.Type, StringComparer.Ordinal)
        .ThenBy(pair => pair.Key.Layer, StringComparer.Ordinal))
    {
      var row = Counts(group.Value);
      row["type"] = group.Key.Type;
      row["layer"] = group.Key.Layer;
      byType.Add(row);
    }
    return new JsonObject
    {
      ["byType"] = byType,
      ["totals"] = Counts(totals),
      ["erasedHandles"] = new JsonArray(erased.Select(handle => (JsonNode)JsonValue.Create(handle)!).ToArray()),
      ["modifiedHandles"] = new JsonArray(modified.Select(handle => (JsonNode)JsonValue.Create(handle)!).ToArray()),
      ["truncated"] = truncated,
    };
  }
}
