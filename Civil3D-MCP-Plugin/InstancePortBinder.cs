namespace Civil3DMcpPlugin;

// Kept free of Autodesk types so the same selection path can be exercised offline.
internal static class InstancePortBinder
{
  internal static int Bind(Func<int, bool> tryBind)
  {
    for (var port = InstanceDiscovery.FirstPort; port <= InstanceDiscovery.LastPort; port++)
      if (tryBind(port)) return port;
    throw new IOException("All Civil 3D MCP ports (8757-8766) are in use.");
  }
}
