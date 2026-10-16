using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Civil3DMcpPlugin;

// Exercises the real Running Object Table with a stand-in object: register under a per-process name, find it by
// that display name (as CAD-Tools' ROT scan does), and revoke it. No AutoCAD involved.
internal static class ComRegistrationChecks
{
  [DllImport("ole32.dll")]
  private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);

  [DllImport("ole32.dll")]
  private static extern int CreateBindCtx(int reserved, out IBindCtx context);

  [ComVisible(true)]
  public sealed class StandIn { }

  internal static void Run(Action<bool, string> expect)
  {
    expect(ComRegistration.DisplayName(4242) == "!Civil3DMcp.AcadApplication:4242", "per-process COM display name");
    // Far above real process ids, so the check never collides with a running AutoCAD.
    var fakeProcessId = 2_000_000_000 + Environment.ProcessId % 100_000;
    var standIn = new StandIn();
    ComRegistration.Register(standIn, fakeProcessId);
    try
    {
      expect(ComRegistration.IsRegistered, "per-process COM registration is recorded");
      ComRegistration.Register(new StandIn(), fakeProcessId);
      var found = Find(ComRegistration.DisplayName(fakeProcessId));
      expect(found is StandIn && ReferenceEquals(found, standIn), "ROT returns the registered object by its per-process name; a second Register is a no-op");
    }
    finally { ComRegistration.Revoke(); }
    expect(!ComRegistration.IsRegistered && Find(ComRegistration.DisplayName(fakeProcessId)) == null,
      "revoking removes the per-process COM name");
    try { ComRegistration.Register(standIn, 0); expect(false, "per-process COM registration refuses a non-positive pid"); }
    catch (ArgumentOutOfRangeException) { expect(true, "per-process COM registration refuses a non-positive pid"); }
  }

  private static object? Find(string displayName)
  {
    Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out var rot));
    Marshal.ThrowExceptionForHR(CreateBindCtx(0, out var context));
    rot.EnumRunning(out var monikers);
    var batch = new IMoniker[1];
    try
    {
      while (monikers.Next(1, batch, IntPtr.Zero) == 0)
      {
        try
        {
          batch[0].GetDisplayName(context, null, out var name);
          if (name == displayName) { rot.GetObject(batch[0], out var registered); return registered; }
        }
        finally { Marshal.ReleaseComObject(batch[0]); }
      }
      return null;
    }
    finally
    {
      Marshal.ReleaseComObject(monikers);
      Marshal.ReleaseComObject(context);
      Marshal.ReleaseComObject(rot);
    }
  }
}
