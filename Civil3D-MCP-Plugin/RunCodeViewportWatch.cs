using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DMcpPlugin;

// Issue #43: SetViewport may switch layouts, which crashed AutoCAD at commit when
// the same run had created or erased viewports. This tells it whether the run did.
// Tripwire only: handlers never throw into AutoCAD, and a failure to observe leaves
// Changed false (the compile guard and the new-viewport check still apply).
internal sealed class RunCodeViewportWatch : IDisposable
{
  private readonly Database _database;
  internal bool Changed { get; private set; }

  internal RunCodeViewportWatch(Database database)
  {
    _database = database;
    _database.ObjectAppended += Appended;
    try { _database.ObjectErased += Erased; }
    catch { _database.ObjectAppended -= Appended; throw; }
  }

  private void Appended(object sender, ObjectEventArgs args)
  {
    try { if (args.DBObject is Viewport) Changed = true; }
    catch (Exception) { }
  }

  private void Erased(object sender, ObjectErasedEventArgs args)
  {
    try { if (args.Erased && args.DBObject is Viewport) Changed = true; }
    catch (Exception) { }
  }

  public void Dispose()
  {
    try { _database.ObjectAppended -= Appended; }
    catch (Exception) { }
    finally
    {
      try { _database.ObjectErased -= Erased; }
      catch (Exception) { }
    }
  }
}
