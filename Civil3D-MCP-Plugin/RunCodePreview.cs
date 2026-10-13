using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DMcpPlugin;

// Events are a tripwire only: ObjectModified does not report normal edits until
// commit. The summary always comes from GetAllObjects at the end of the run.
internal sealed class RunCodePreview : IDisposable
{
  private readonly Database _database;
  private readonly Transaction _transaction;
  private readonly int _runDepth;
  internal bool OutsideTransaction { get; private set; }
  internal bool ObservationFailed { get; private set; }

  internal RunCodePreview(Database database, Transaction transaction)
  {
    _database = database;
    _transaction = transaction;
    _runDepth = database.TransactionManager.NumberOfActiveTransactions;
    _database.ObjectModified += Modified;
    try { _database.ObjectErased += Erased; }
    catch { _database.ObjectModified -= Modified; throw; }
  }

  private void Modified(object sender, ObjectEventArgs args)
  {
    try { Observe(args.DBObject); }
    catch (Exception) { ObservationFailed = true; }
  }
  private void Erased(object sender, ObjectErasedEventArgs args)
  {
    try { Observe(args.DBObject); }
    catch (Exception) { ObservationFailed = true; }
  }

  private void Observe(DBObject obj)
  {
    // Never propagate an exception into an AutoCAD event callback.
    try
    {
      bool Resident(DBObjectCollection objects)
      {
        foreach (DBObject opened in objects)
          if (opened.ObjectId == obj.ObjectId) return true;
        return false;
      }
      var resident = Resident(_transaction.GetAllObjects());
      // A regular nested transaction belongs to the run and rolls back with
      // it. Erase events can arrive before the child commits its objects into
      // the outer transaction. An open-close transaction commits separately.
      var manager = _database.TransactionManager;
      var top = manager.TopTransaction;
      if (!resident && _transaction.AutoDelete && _transaction.UnmanagedObject != IntPtr.Zero
          && manager.NumberOfActiveTransactions > _runDepth && top != null && top is not OpenCloseTransaction)
        resident = Resident(top.GetAllObjects())
          || _runDepth == 1 && Resident(manager.GetAllObjects());
      if (!resident) OutsideTransaction = true;
    }
    catch (Exception) { ObservationFailed = true; }
  }

  internal JsonObject Capture(Action check)
  {
    IEnumerable<RunCodeChange> Objects()
    {
      foreach (DBObject obj in _transaction.GetAllObjects())
      {
        check();
        var isNew = obj.IsNewObject;
        var isErased = obj.IsErased;
        var isModified = obj.IsModified;
        if (isNew && isErased || !isNew && !isErased && !isModified) continue;
        // Non-entity objects (tables, dictionaries, Civil objects) count too.
        // New handles are deliberately omitted: abort can reuse them later.
        yield return new(obj.GetRXClass().DxfName, obj is Entity entity ? entity.Layer : null,
          isNew, isErased, isModified, isNew ? null : obj.Handle.ToString());
      }
    }
    return RunCodeChanges.Reduce(Objects());
  }

  public void Dispose()
  {
    try { _database.ObjectModified -= Modified; }
    catch (Exception) { ObservationFailed = true; }
    finally
    {
      try { _database.ObjectErased -= Erased; }
      catch (Exception) { ObservationFailed = true; }
    }
  }
}
