using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Colors;
using Autodesk.Civil.ApplicationServices;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace Civil3DMcpPlugin;

/// <summary>
/// Base class of every runCode script (civil3d_run_code). The snippet becomes
/// the body of <see cref="Run"/>, so these members are in scope by name:
/// Doc, Db, Ed, Civil, Tr, Args, Log(...), Check(), Open&lt;T&gt;(id),
/// ModelSpace&lt;T&gt;(), PaperSpace&lt;T&gt;(layout), OpenSide(path),
/// OpenSideOrNull(path), SideErrors, Try(label, action), TryErrors, ById(handle),
/// EnsureLayer(...), Tag(...), TagValue(...), Tagged&lt;T&gt;(...),
/// TextBox(...), XClip(...), AttachXref(...), SetViewport(...), ImportBlock(...),
/// TransactionFor(db), WriteFile(...), RunFolder, Session.
/// EnsureLayer, Tag, XClip, AttachXref, SetViewport and ImportBlock need write mode.
/// SideErrors and TryErrors keep at most
/// 200 entries each; timeout and cancellation are never swallowed.
///
/// The script runs on Civil 3D's main thread and cannot be stopped from
/// outside: call Check() in long loops (ModelSpace() does it for every entity)
/// so the time budget and a cancelled request end it.
/// </summary>
public abstract partial class RunCodeScript
{
  public sealed record RunCodeSideError(string Path, string? ErrorStatus, string Message);
  public sealed record RunCodeTryError(string Label, string Type, string Message, int? Line);

  private const int MaxOutputChars = 4 * 1024 * 1024;

  private readonly StringBuilder _output = new();
  // Log runs on the main thread; Output can be read from the request's thread
  // when a cancelled request stops waiting while the script still runs.
  private readonly object _outputLock = new();
  // Disposed after the run, newest first: for each side database the script's
  // wrapper (detached only), its transaction, then the database.
  private readonly List<IDisposable> _owned = new();
  private readonly Dictionary<Database, Transaction> _transactions = new();
  private Stopwatch _clock = new();
  private TimeSpan _budget;
  // A script may point HostApplicationServices.WorkingDatabase at a side
  // database; it is set back before side databases are disposed.
  private Database? _savedWorkingDatabase;
  // A failed WorkingDatabase restore must not let an owning wrapper's
  // finalizer free the database still used by AutoCAD.
  private static readonly List<Database> RetainedWorkingDatabases = new();
  private const string NoDrawingMessage = "No drawing is open in Civil 3D, so Db is not available: read files with OpenSide(path) and TransactionFor(side).";

  private readonly List<RunCodeSideError> _sideErrors = new();
  private readonly List<RunCodeTryError> _tryErrors = new();
  internal bool ActivatedLayout { get; private set; }
  // A cancelled request can collect reports while the main thread still runs.
  public IReadOnlyList<RunCodeSideError> SideErrors
  {
    get { lock (_outputLock) return _sideErrors.ToArray(); }
  }
  public IReadOnlyList<RunCodeTryError> TryErrors
  {
    get { lock (_outputLock) return _tryErrors.ToArray(); }
  }

  /// <summary>
  /// Kept across runs until Civil 3D closes: cache plain data between runs
  /// (arrays, lists, numbers, strings), for example geometry read from a
  /// background drawing. Never store DBObjects, ObjectIds of side databases or
  /// Databases: those are closed or disposed when the run ends.
  /// </summary>
  public static ConcurrentDictionary<string, object?> Session { get; } = new(StringComparer.OrdinalIgnoreCase);

  private Document? _doc;
  private Database? _db;
  private bool _previewMode;
  private CivilDocument? _civil;
  private Transaction? _transaction;
  public bool HasDrawing => _doc != null;
  public Document Doc => _doc ?? throw new InvalidOperationException(NoDrawingMessage);
  public Database Db => _db ?? throw new InvalidOperationException(NoDrawingMessage);
  public Editor Ed => Doc.Editor;
  public CivilDocument Civil => _civil ?? throw new InvalidOperationException(NoDrawingMessage);

  /// <summary>
  /// The run's transaction on <see cref="Db"/>. Read mode: an open-close
  /// transaction that is never committed. Write mode: a regular transaction,
  /// committed when Run returns (one Civil 3D undo step) and rolled back when
  /// it throws or runs out of time. Only changes made through Tr (and transactions
  /// nested in it) are rolled back: database and system variables, files and
  /// Session are not. Open-close transactions are refused in write mode, and
  /// a transaction the script starts must be the resource of a using.
  /// The run owns it: Commit() and Abort() throw, and Dispose() does nothing
  /// (RunTransaction).
  /// </summary>
  public Transaction Tr => _transaction ?? throw new InvalidOperationException(NoDrawingMessage);

  public bool WriteMode { get; private set; }

  /// <summary>The tool call's "args" object (empty when none was given).</summary>
  public JsonObject Args { get; private set; } = new();

  public CancellationToken Token { get; private set; }

  /// <summary>This run's folder (script, summary, results); WriteFile writes here.</summary>
  public string RunFolder { get; private set; } = string.Empty;

  public TimeSpan Elapsed => _clock.Elapsed;

  public TimeSpan Budget => _budget;

  internal bool OutputTruncated { get; private set; }

  internal string Output
  {
    get
    {
      lock (_outputLock)
      {
        return _output.ToString();
      }
    }
  }

  /// <summary>
  /// The timeout or cancellation Check() threw, kept even when the script
  /// caught it and went on: the run still ends as timed out or cancelled, and
  /// a write is rolled back.
  /// </summary>
  internal Exception? Tripped { get; private set; }

  public abstract object? Run();

  internal void Attach(Document? doc, Database? db, CivilDocument? civil, Transaction? transaction, bool writeMode,
    JsonObject args, CancellationToken token, TimeSpan budget, string runFolder, bool previewMode = false)
  {
    _doc = doc;
    _db = db;
    _civil = civil;
    _transaction = transaction == null ? null : new RunTransaction(transaction);
    WriteMode = writeMode;
    _previewMode = previewMode;
    Args = args;
    Token = token;
    RunFolder = runFolder;
    _budget = budget;
    if (db != null && _transaction != null) _transactions[db] = _transaction;
    _savedWorkingDatabase = HostApplicationServices.WorkingDatabase;
    _clock = Stopwatch.StartNew();
    // Only SetViewport reads it (issue #43); write runs are the only ones that commit.
    if (writeMode && !previewMode && db != null)
    {
      try { _viewportWatch = new RunCodeViewportWatch(db); }
      catch (Exception exception) { PluginLog.Swallow("RunCode", "watching viewport changes", exception); }
    }
  }

  private RunCodeViewportWatch? _viewportWatch;
  internal bool ViewportsChangedInRun => _viewportWatch?.Changed == true;

  /// <summary>Appends a line to the run's output (returned as "output").</summary>
  public void Log(object? value)
  {
    var text = value?.ToString();
    lock (_outputLock)
    {
      if (RunCodeOutput.AppendLine(_output, text, MaxOutputChars))
      {
        OutputTruncated = true;
      }
    }
  }

  /// <summary>
  /// Ends the script when its time budget is used up or the request was
  /// cancelled. Call it in every long loop: nothing else can stop a script.
  /// </summary>
  public void Check()
  {
    if (Tripped != null)
    {
      throw Tripped;
    }

    if (Token.IsCancellationRequested)
    {
      throw Tripped = new RunCodeCancelledException("The request was cancelled (the MCP client disconnected or gave up waiting).");
    }

    if (_clock.Elapsed > _budget)
    {
      throw Tripped = new RunCodeTimeoutException($"The script used up its time budget of {_budget.TotalSeconds:0} s.");
    }
  }

  /// <summary>
  /// Opens an object through the run's transaction for its database (the
  /// drawing's or a side database's; an attached xref's objects through the
  /// drawing's, read only). forWrite needs mode 'write'.
  /// </summary>
  public T Open<T>(ObjectId id, bool forWrite = false) where T : DBObject
  {
    if (forWrite && !WriteMode)
    {
      throw new InvalidOperationException("Open(id, forWrite: true) needs mode 'write'.");
    }

    // Objects of an attached xref (its dependent block definitions and what is
    // in them) report the xref's database, which has no transaction of its own
    // in the run; the drawing's transaction opens them, for read only.
    var db = id.Database;
    if (db == null && !HasDrawing) throw new InvalidOperationException(NoDrawingMessage);
    Transaction transaction;
    if (db is { IsDisposed: false } && !_transactions.ContainsKey(db))
    {
      if (forWrite)
      {
        throw new InvalidOperationException($"Object {id.Handle} belongs to an attached xref (or another database this run did not open): it can be read, not opened for write.");
      }

      transaction = Tr;
    }
    else
    {
      transaction = db == null ? Tr : TransactionFor(db);
    }

    var obj = transaction.GetObject(id, forWrite ? OpenMode.ForWrite : OpenMode.ForRead);
    return obj as T ?? throw new InvalidCastException($"Object {id.Handle} is a {obj.GetType().Name}, not a {typeof(T).Name}.");
  }

  /// <summary>
  /// Every entity of type T in model space (of the drawing, or of a side
  /// database), opened for read. Calls Check() for each entity.
  /// </summary>
  public IEnumerable<T> ModelSpace<T>(Database? db = null) where T : Autodesk.AutoCAD.DatabaseServices.Entity
  {
    db ??= Db;
    var transaction = TransactionFor(db);
    var table = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
    var modelSpace = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
    foreach (ObjectId id in modelSpace)
    {
      Check();
      if (transaction.GetObject(id, OpenMode.ForRead) is T entity)
      {
        yield return entity;
      }
    }
  }

  /// <summary>
  /// Reads another DWG as a side database (never opened in the editor; the
  /// file is not locked after this returns) with a transaction of its own
  /// (TransactionFor). Disposed when the run ends: the script gets a wrapper
  /// that does not own the database, so a using or Dispose() on it (refused
  /// at compile time) cannot free it while the run's transaction still holds
  /// its objects. Changes stay in memory unless the script saves the database
  /// somewhere, which needs mode 'write'.
  /// </summary>
  public Database OpenSide(string path)
  {
    var fullPath = Path.GetFullPath(path);
    if (!File.Exists(fullPath))
    {
      throw new FileNotFoundException($"Drawing not found: {fullPath}", fullPath);
    }

    var side = ReadSideWithRetry(fullPath);

    // The owning wrapper, freed by ReleaseOwned after the transaction that
    // holds its objects.
    _owned.Add(side);
    var transaction = side.TransactionManager.StartOpenCloseTransaction();
    // The real one is disposed by ReleaseOwned; the script only gets the wrapper.
    _owned.Add(transaction);
    // Keyed by the owning wrapper, which the script never sees, so the key
    // cannot be disposed; Equals/GetHashCode follow the native database, so
    // the script's wrapper and id.Database find the same entry.
    _transactions[side] = new RunTransaction(transaction);
    // The script's wrapper: autoDelete false, like the ones id.Database hands
    // out, so its Dispose() only detaches it and never deletes the database.
    // ReleaseOwned detaches it too, so one kept past the run holds no pointer.
    var alias = (Database)Autodesk.AutoCAD.Runtime.DisposableWrapper.Create(typeof(Database), side.UnmanagedObject, false);
    _owned.Add(alias);
    return alias;
  }

  // Shared by OpenSide and ImportBlock: sharing errors get one retry with a
  // fresh database, drawing-content errors none.
  private static Database ReadSideWithRetry(string fullPath)
  {
    try
    {
      return ReadSideOnce(fullPath, FileOpenMode.OpenForReadAndAllShare);
    }
    catch (Exception first) when (first is not RunCodeTimeoutException and not OperationCanceledException
      && !RunCodeHelpers.IsDrawingContentError((first as AcRuntimeException)?.ErrorStatus.ToString()))
    {
      try
      {
        return ReadSideOnce(fullPath, FileOpenMode.OpenTryForReadShare);
      }
      catch
      {
        ExceptionDispatchInfo.Capture(first).Throw();
        throw;
      }
    }
  }

  private static Database ReadSideOnce(string path, FileOpenMode mode)
  {
    var side = new Database(false, true);
    try
    {
      side.ReadDwgFile(path, mode, true, string.Empty);
      side.CloseInput(true);
      return side;
    }
    catch
    {
      side.Dispose();
      throw;
    }
  }

  /// <summary>The run's transaction for the drawing (Tr) or for a database from OpenSide.</summary>
  public Transaction TransactionFor(Database db)
  {
    // A disposed wrapper has no pointer, and 2027's Database.GetHashCode reads
    // the native database: never hash one (address 0).
    if (db is { IsDisposed: true })
    {
      throw new InvalidOperationException("That database was disposed, or comes from a run that has ended: use one from this run's OpenSide, and do not dispose it (the run disposes side databases when it ends).");
    }

    return _transactions.TryGetValue(db, out var transaction)
      ? transaction
      : throw new InvalidOperationException("That database has no transaction in this run: use Db or a database returned by OpenSide.");
  }

  /// <summary>Opens a side drawing, recording an error and returning null if it cannot be read.</summary>
  public Database? OpenSideOrNull(string path)
  {
    try { return OpenSide(path); }
    catch (Exception error) when (error is not RunCodeTimeoutException and not OperationCanceledException)
    {
      lock (_outputLock)
      {
        if (_sideErrors.Count < 200)
          _sideErrors.Add(new RunCodeSideError(path, (error as AcRuntimeException)?.ErrorStatus.ToString(), error.Message));
      }
      Log($"Side drawing {path}: {error.Message}");
      return null;
    }
  }

  /// <summary>Runs a recoverable step and records its failure. Timeouts and cancellation always escape.</summary>
  public bool Try(string label, Action action)
  {
    try { Check(); action(); Check(); return true; }
    catch (Exception error) when (error is not RunCodeTimeoutException and not OperationCanceledException)
    {
      RecordTryError(label, error);
      return false;
    }
  }

  public T? Try<T>(string label, Func<T> action)
  {
    try { Check(); var result = action(); Check(); return result; }
    catch (Exception error) when (error is not RunCodeTimeoutException and not OperationCanceledException)
    {
      RecordTryError(label, error);
      return default;
    }
  }

  private void RecordTryError(string label, Exception error)
  {
    var line = ScriptCompiler.FindScriptLine(error.ToString());
    lock (_outputLock)
    {
      if (_tryErrors.Count < 200)
        _tryErrors.Add(new RunCodeTryError(label, error.GetType().Name, error.Message, line));
    }
    Log($"{label}: {error.GetType().Name}: {error.Message}{(line == null ? string.Empty : $" (line {line})")}");
  }

  /// <summary>Finds an existing object by hexadecimal handle, without creating one.</summary>
  public ObjectId ById(string hex, Database? db = null)
  {
    db ??= Db;
    TransactionFor(db);
    var handle = new Handle(RunCodeHelpers.ParseHandle(hex));
    if (!db.TryGetObjectId(handle, out var id) || id.IsNull || id.IsErased)
      throw new KeyNotFoundException($"Object handle {hex} was not found or is erased.");
    return id;
  }

  /// <summary>Creates a layer when missing; existing layers keep their properties.</summary>
  public ObjectId EnsureLayer(string name, short? colorIndex = null, string? linetype = null, Database? db = null)
  {
    if (!WriteMode) throw new InvalidOperationException("EnsureLayer needs mode 'write'.");
    db ??= Db;
    var tx = TransactionFor(db);
    var layers = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);
    if (layers.Has(name)) return layers[name];
    var layer = new LayerTableRecord { Name = name };
    if (colorIndex.HasValue)
    {
      if (colorIndex.Value is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(colorIndex), "ACI color must be between 1 and 255.");
      layer.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex.Value);
    }
    if (linetype != null)
    {
      var types = (LinetypeTable)tx.GetObject(db.LinetypeTableId, OpenMode.ForRead);
      if (!types.Has(linetype)) throw new KeyNotFoundException($"Linetype '{linetype}' was not found.");
      layer.LinetypeObjectId = types[linetype];
    }
    layers.UpgradeOpen();
    var id = layers.Add(layer);
    tx.AddNewlyCreatedDBObject(layer, true);
    return id;
  }

  /// <summary>Sets an application's XData string on an object opened for write; null stores an empty string.</summary>
  public void Tag(DBObject obj, string app, string? value = null)
  {
    if (!WriteMode) throw new InvalidOperationException("Tag needs mode 'write'.");
    if (obj == null) throw new ArgumentNullException(nameof(obj));
    var tx = TransactionFor(obj.Database);
    var apps = (RegAppTable)tx.GetObject(obj.Database.RegAppTableId, OpenMode.ForRead);
    if (!apps.Has(app))
    {
      apps.UpgradeOpen();
      var record = new RegAppTableRecord { Name = app };
      apps.Add(record);
      tx.AddNewlyCreatedDBObject(record, true);
    }
    if (!obj.IsWriteEnabled) obj.UpgradeOpen();
    var data = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, app),
      new TypedValue((int)DxfCode.ExtendedDataAsciiString, value ?? ""));
    using (data) obj.XData = data;
  }

  /// <summary>Returns the tag's string (including an empty string), or null when no string tag exists.</summary>
  public string? TagValue(DBObject obj, string app)
  {
    TransactionFor(obj.Database);
    using var data = obj.GetXDataForApplication(app);
    if (data == null) return null;
    foreach (var item in data.AsArray())
      if (item.TypeCode == (int)DxfCode.ExtendedDataAsciiString) return item.Value as string;
    return null;
  }

  public IEnumerable<Autodesk.AutoCAD.DatabaseServices.Entity> ModelSpace(Database? db = null) =>
    ModelSpace<Autodesk.AutoCAD.DatabaseServices.Entity>(db);

  /// <summary>Finds tagged entities in model and paper space; null matches any value.</summary>
  public IEnumerable<T> Tagged<T>(string app, string? value = null, Database? db = null)
    where T : Autodesk.AutoCAD.DatabaseServices.Entity
  {
    db ??= Db;
    foreach (var entity in ModelSpace<T>(db))
      if (HasTag(entity, app, value)) yield return entity;
    foreach (var name in LayoutNames(db))
      foreach (var entity in PaperSpace<T>(name, db))
        if (HasTag(entity, app, value)) yield return entity;
  }

  private bool HasTag(DBObject obj, string app, string? value)
  {
    using var data = obj.GetXDataForApplication(app);
    return data != null && (value == null || TagValue(obj, app) == value);
  }

  public IEnumerable<Autodesk.AutoCAD.DatabaseServices.Entity> Tagged(string app, string? value = null, Database? db = null) =>
    Tagged<Autodesk.AutoCAD.DatabaseServices.Entity>(app, value, db);

  private IEnumerable<string> LayoutNames(Database db)
  {
    var tx = TransactionFor(db);
    var layouts = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
    foreach (DBDictionaryEntry entry in layouts)
    {
      Check();
      var layout = (Layout)tx.GetObject(entry.Value, OpenMode.ForRead);
      if (!layout.ModelType) yield return layout.LayoutName;
    }
  }

  public IEnumerable<T> PaperSpace<T>(string layoutName, Database? db = null)
    where T : Autodesk.AutoCAD.DatabaseServices.Entity
  {
    db ??= Db;
    var tx = TransactionFor(db);
    var layouts = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
    var available = new List<string>();
    ObjectId blockId = ObjectId.Null;
    foreach (DBDictionaryEntry entry in layouts)
    {
      Check();
      var layout = (Layout)tx.GetObject(entry.Value, OpenMode.ForRead);
      if (layout.ModelType) continue;
      available.Add(layout.LayoutName);
      if (string.Equals(layout.LayoutName, layoutName, StringComparison.OrdinalIgnoreCase)) blockId = layout.BlockTableRecordId;
    }
    if (blockId.IsNull) throw new KeyNotFoundException($"Layout '{layoutName}' was not found. Available layouts: {string.Join(", ", available)}.");
    var block = (BlockTableRecord)tx.GetObject(blockId, OpenMode.ForRead);
    foreach (ObjectId id in block)
    {
      Check();
      if (tx.GetObject(id, OpenMode.ForRead) is T entity) yield return entity;
    }
  }

  public IEnumerable<Autodesk.AutoCAD.DatabaseServices.Entity> PaperSpace(string layoutName, Database? db = null) =>
    PaperSpace<Autodesk.AutoCAD.DatabaseServices.Entity>(layoutName, db);

  /// <summary>Writes a text file into RunFolder (a file name, no folders) and returns its full path.</summary>
  public string WriteFile(string fileName, string contents)
  {
    if (_previewMode) throw new InvalidOperationException("Preview cannot roll back WriteFile; use mode 'write'.");
    return RunCodeLog.WriteScriptFile(RunFolder, fileName, contents, null);
  }

  /// <summary>Writes lines into a file in RunFolder and returns its full path.</summary>
  public string WriteFile(string fileName, IEnumerable<string> lines)
  {
    if (_previewMode) throw new InvalidOperationException("Preview cannot roll back WriteFile; use mode 'write'.");
    return RunCodeLog.WriteScriptFile(RunFolder, fileName, null, lines);
  }

  internal void ReleaseOwned()
  {
    try { _viewportWatch?.Dispose(); }
    catch (Exception exception) { PluginLog.Swallow("RunCode", "ending the viewport watch", exception); }
    _viewportWatch = null;

    // Never leave a disposed side database as the working database.
    var restoreFailed = false;
    Database? working = null;
    var workingUnknown = false;
    try
    {
      if (HostApplicationServices.WorkingDatabase != _savedWorkingDatabase)
      {
        HostApplicationServices.WorkingDatabase = _savedWorkingDatabase;
      }
    }
    catch (Exception exception)
    {
      restoreFailed = true;
      try { working = HostApplicationServices.WorkingDatabase; }
      catch { workingUnknown = true; }
      PluginLog.Swallow("RunCode", "restoring the working database", exception);
    }

    // Tr and the side transactions as the script sees them: detached before
    // the real ones end, so a wrapper kept past the run never reaches freed
    // memory. The run itself only uses the real transactions from here on.
    foreach (var wrapper in _transactions.Values.OfType<RunTransaction>())
    {
      try
      {
        wrapper.Release();
      }
      catch (Exception exception)
      {
        PluginLog.Swallow("RunCode", "releasing a transaction wrapper", exception);
      }
    }

    for (var index = _owned.Count - 1; index >= 0; index--)
    {
      try
      {
        if (restoreFailed && _owned[index] is Database side
            && (workingUnknown || (working != null && side.UnmanagedObject == working.UnmanagedObject)))
        {
          RetainedWorkingDatabases.Add(side);
          PluginLog.Warn("RunCode", "Kept a side database alive because the working database could not be restored; it must not be freed while AutoCAD uses it.");
          continue;
        }
        _owned[index].Dispose();
      }
      catch (Exception exception)
      {
        PluginLog.Swallow("RunCode", "disposing a side database", exception);
      }
    }

    _owned.Clear();
    _transactions.Clear();
  }
}

/// <summary>
/// Tr and TransactionFor(...) as the script sees them. Every call goes to the
/// run's transaction, but Commit() and Abort() throw and Dispose() does
/// nothing, so an alias (var tx = Tr; tx.Commit(); or using var tx = Tr;) can
/// neither end it early nor leave a freed transaction that the next call would
/// dereference (a Civil 3D crash). The run alone commits, aborts and disposes
/// the real one. Members not overridden here (GetAllObjects, the RXObject
/// members) work on the same native transaction, exactly as on the real object;
/// OpenCloseTransaction does not override them either. When the run ends,
/// Release() zeroes this wrapper's pointer, so those members then fail with a
/// NullReferenceException instead of reading the freed transaction.
/// </summary>
internal sealed class RunTransaction : Transaction
{
  private readonly Transaction _run;
  private volatile bool _released;

  // autoDelete false: this wrapper never ends or frees the native transaction.
  internal RunTransaction(Transaction run) : base(run.UnmanagedObject, false)
  {
    _run = run;
  }

  // A wrapper kept past its run (in Session, or by a task still running)
  // must not reach the disposed transaction: that is a native crash.
  private Transaction Run => _released || _run.IsDisposed
    ? throw new InvalidOperationException("This transaction belongs to a run that has ended; use the current run's Tr.")
    : _run;

  // Main thread, when the run ends (ReleaseOwned). The base Dispose with
  // AutoDelete false only detaches (pointer zeroed, nothing freed): the
  // override below stays a no-op for the script.
  internal void Release()
  {
    _released = true;
    base.Dispose(true);
  }

  public override DBObject GetObject(ObjectId id, OpenMode mode) => Run.GetObject(id, mode);

  public override DBObject GetObject(ObjectId id, OpenMode mode, bool openErased) => Run.GetObject(id, mode, openErased);

  public override DBObject GetObject(ObjectId id, OpenMode mode, bool openErased, bool forceOpenOnLockedLayer) =>
    Run.GetObject(id, mode, openErased, forceOpenOnLockedLayer);

  public override void AddNewlyCreatedDBObject(DBObject obj, bool add) => Run.AddNewlyCreatedDBObject(obj, add);

  public override int NumberOfOpenedObjects => Run.NumberOfOpenedObjects;

  public override Autodesk.AutoCAD.DatabaseServices.TransactionManager TransactionManager => Run.TransactionManager;

  public override void Commit() =>
    throw new InvalidOperationException("The run owns Tr (and TransactionFor's transactions): write mode commits Tr when the script returns; remove Commit().");

  public override void Abort() =>
    throw new InvalidOperationException("The run owns Tr (and TransactionFor's transactions): throw an exception to roll back; remove Abort().");

  protected override void Dispose(bool disposing)
  {
    // Nothing to do: the run disposes the real transaction.
  }
}

/// <summary>Thrown by Check() when the script's time budget is used up.</summary>
public sealed class RunCodeTimeoutException : Exception
{
  public RunCodeTimeoutException(string message) : base(message)
  {
  }
}

/// <summary>Thrown by Check() when the request was cancelled.</summary>
public sealed class RunCodeCancelledException : OperationCanceledException
{
  public RunCodeCancelledException(string message) : base(message)
  {
  }
}
