using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Civil3DMcpPlugin;

// Registers this process's AutoCAD Application in the Running Object Table under a per-process name.
// AutoCAD's own registration hands every COM client the earliest-started instance still running (verified
// live 2026-10-15), so scripts could not reach a later instance. With this name a client picks an instance by
// process id. The object is the Application AutoCAD already exposes; clients still check its process through
// Application.HWND. Same-user only (no ROTFLAGS_ALLOWANYCLIENT), like AutoCAD's own entry.
internal static class ComRegistration
{
  internal const string ItemPrefix = "Civil3DMcp.AcadApplication:";
  private const int RotFlagsRegistrationKeepsAlive = 1;
  private static readonly object Sync = new();
  private static int _cookie;

  [DllImport("ole32.dll")]
  private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);

  [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
  private static extern int CreateItemMoniker(string delimiter, string item, out IMoniker moniker);

  internal static string ItemName(int processId) => ItemPrefix + processId.ToString(CultureInfo.InvariantCulture);

  // The display name a client sees when it enumerates the ROT.
  internal static string DisplayName(int processId) => "!" + ItemName(processId);

  internal static bool IsRegistered
  {
    get { lock (Sync) return _cookie != 0; }
  }

  internal static void Register(object application, int processId)
  {
    ArgumentNullException.ThrowIfNull(application);
    if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
    lock (Sync)
    {
      if (_cookie != 0) return;
      Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out var rot));
      Marshal.ThrowExceptionForHR(CreateItemMoniker("!", ItemName(processId), out var moniker));
      try { _cookie = rot.Register(RotFlagsRegistrationKeepsAlive, application, moniker); }
      finally
      {
        Marshal.ReleaseComObject(moniker);
        Marshal.ReleaseComObject(rot);
      }
    }
  }

  internal static void Revoke()
  {
    lock (Sync)
    {
      if (_cookie == 0) return;
      var cookie = _cookie;
      _cookie = 0;
      Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out var rot));
      try { rot.Revoke(cookie); }
      finally { Marshal.ReleaseComObject(rot); }
    }
  }
}
