using Civil3DMcpPlugin;

// Compiler-only checks: no Autodesk methods in these snippets are executed.
internal static class DatabaseGuardChecks
{
  internal static void Run(Func<string, bool, ScriptCompileResult> compile,
    Action<bool, string, ScriptCompileResult?> expect)
  {
    void DenyReadAllowWrite(string name, string code, int line = 1)
    {
      var read = compile(code, false);
      expect(!read.Succeeded && read.Errors.Count > 0
        && read.Errors.All(error => error.Code == ScriptCompiler.GuardDiagnosticId)
        && read.Errors.Any(error => error.Line == line),
        $"read mode refuses Database.{name} with only C3DMCP001 on snippet line {line}", read);
      var write = compile(code, true);
      expect(write.Succeeded, $"write mode allows Database.{name}", write);
    }

    DenyReadAllowWrite("SetDimstyleData through a typed alias", "using DrawingDb = Autodesk.AutoCAD.DatabaseServices.Database;\nDrawingDb target = Db;\ntarget.SetDimstyleData(new DimStyleTableRecord());", 3);
    DenyReadAllowWrite("SetTimeZoneAsUtcOffset through conditional access", "Db?.SetTimeZoneAsUtcOffset(0);");

    void AllowRead(string name, string code)
    {
      var read = compile(code, false);
      expect(read.Succeeded, $"read mode allows {name}", read);
    }

    // Methods that write directly to Database without opening a DBObject ForWrite.
    foreach (var (name, code) in new[] {
      ("SetDimstyleData", "Db.SetDimstyleData(new DimStyleTableRecord());"),
      ("SetWorldPaperspaceUcsBaseOrigin", "Db.SetWorldPaperspaceUcsBaseOrigin(new Point3d(0, 0, 0), (OrthographicView)0);"),
      ("SetWorldUcsBaseOrigin", "Db.SetWorldUcsBaseOrigin(new Point3d(0, 0, 0), (OrthographicView)0);"),
      ("SetTimeZoneAsUtcOffset", "Db.SetTimeZoneAsUtcOffset(0);") })
    {
      DenyReadAllowWrite(name, code);
    }

    foreach (var (name, code) in new[] {
      ("DisableUndoRecording", "Db.DisableUndoRecording(true);"),
      ("EraseEmptyObjects", "Db.EraseEmptyObjects(0);"),
      ("LoadMlineStyleFile", "Db.LoadMlineStyleFile(\"M\", \"x.mln\");"),
      ("ReclaimMemoryFromErasedObjects", "Db.ReclaimMemoryFromErasedObjects(new ObjectIdCollection());"),
      ("ResetTimes", "Db.ResetTimes();"),
      ("RestoreForwardingXrefSymbols", "Db.RestoreForwardingXrefSymbols();"),
      ("RestoreOriginalXrefSymbols", "Db.RestoreOriginalXrefSymbols();"),
      ("XBindXrefs", "Db.XBindXrefs(new ObjectIdCollection(), false);"),
      ("ForceWblockDatabaseCopy", "Db.ForceWblockDatabaseCopy();") })
    {
      DenyReadAllowWrite(name, code);
    }

    // The pre-existing direct mutation rules must continue to hold.
    foreach (var (name, code) in new[] {
      ("AddDBObject", "Db.AddDBObject(new Xrecord());"),
      ("AttachXref", "Db.AttachXref(\"x.dwg\", \"X\");"),
      ("Insert", "Db.Insert(\"X\", new Database(false, true), false);"),
      ("DeepCloneObjects", "Db.DeepCloneObjects(new ObjectIdCollection(), Db.BlockTableId, new IdMapping(), false);") })
    {
      DenyReadAllowWrite(name, code);
    }

    // File loading changes the receiver. A detached database may be loaded in a read script.
    const string load = "ReadDwgFile(\"x.dwg\", System.IO.FileShare.Read, false, \"\")";
    foreach (var (name, code) in new[] {
      ("ReadDwgFile on Db", "Db." + load + ";"),
      ("ReadDwgFile on Doc.Database", "Doc.Database." + load + ";"),
      ("ReadDwgFile on another open document", "Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument.Database." + load + ";"),
      ("ReadDwgFile through a Db alias", "var alias = Db;\nalias." + load + ";"),
      ("ReadDwgFile after a mixed assignment", "Database side = new Database(false, true);\nif (Args.Count > 0) side = Db;\nside." + load + ";"),
      ("ReadDwgFile after reassignment from Db", "Database side = Db;\nside = new Database(false, true);\nside." + load + ";"),
      ("ReadDwgFile from an opaque function", "Database GetSide() => new Database(false, true);\nGetSide()." + load + ";"),
      ("ReadDwgFile on a function parameter", "void Load(Database side) { side." + load + "; }"),
      ("ReadDwgFile after ref exposure", "void Swap(ref Database side) { }\nvar side = new Database(false, true);\nSwap(ref side);\nside." + load + ";"),
      ("ReadDwgFile after deconstruction", "(Database side, int n) = (new Database(false, true), 1);\nside." + load + ";") })
    {
      DenyReadAllowWrite(name, code, code.Split('\n').Length);
    }
    DenyReadAllowWrite("ReadDwgFile file-mode overload", "Db.ReadDwgFile(\"x.dwg\", FileOpenMode.OpenForReadAndAllShare, false, \"\");");
    DenyReadAllowWrite("ReadDwgFile pointer overload", "Db.ReadDwgFile((nint)0, false, \"\");");
    DenyReadAllowWrite("DxfIn", "Db.DxfIn(\"x.dxf\", \"x.log\");");
    DenyReadAllowWrite("ApplyPartialOpenFilters", "Db.ApplyPartialOpenFilters(null, null);");
    DenyReadAllowWrite("DisablePartialOpen", "Db.DisablePartialOpen();");
    DenyReadAllowWrite("ReadDwgFile on a document-associated constructor", "var side = new Database(false, false);\nside." + load + ";", 2);
    DenyReadAllowWrite("ReadDwgFile with cyclic aliases", "var side = new Database(false, true);\nvar alias = side;\nside = alias;\nside." + load + ";", 4);
    DenyReadAllowWrite("ReadDwgFile after out exposure", "void Swap(out Database side) { side = Db; }\nvar side = new Database(false, true);\nSwap(out side);\nside." + load + ";", 4);
    DenyReadAllowWrite("ReadDwgFile after parenthesized ref exposure", "void Swap(ref Database side) { side = Db; }\nvar side = new Database(false, true);\nSwap(ref (side));\nside." + load + ";", 4);
    DenyReadAllowWrite("ReadDwgFile after parenthesized null-suppressed ref exposure", "void Swap(ref Database side) { side = Db; }\nvar side = new Database(false, true);\nSwap(ref (side!));\nside." + load + ";", 4);
    DenyReadAllowWrite("ReadDwgFile after nested parenthesized out exposure", "void Swap(out Database side) { side = Db; }\nvar side = new Database(false, true);\nSwap(out ((side)));\nside." + load + ";", 4);
    DenyReadAllowWrite("ReadDwgFile after parenthesized ref-local exposure", "var side = new Database(false, true);\nref Database alias = ref ((side));\nalias = Db;\nside." + load + ";", 4);

    AllowRead("ReadDwgFile on new Database(false, true)", "var side = new Database(false, true);\nside." + load + ";");
    AllowRead("ReadDwgFile on a named detached constructor", "var side = new Database(noDocument: true, buildDefaultDrawing: false);\nside." + load + ";");
    AllowRead("ReadDwgFile through conditional access on a detached database", "var side = new Database(false, true);\nside?." + load + ";");
    AllowRead("ReadDwgFile after assignment to an uninitialized local", "Database side;\nside = new Database(false, true);\nside." + load + ";");
    AllowRead("ReadDwgFile after null initialization with try/finally disposal", """
      Database? side = null;
      try
      {
        side = new Database(false, true);
        side.ReadDwgFile("x.dwg", FileOpenMode.OpenForReadAndAllShare, true, "");
      }
      finally { side?.Dispose(); }
      """);
    foreach (var initializer in new[] { "null", "default", "default(Database)", "(Database?)null" })
    {
      AllowRead($"ReadDwgFile after {initializer} initialization and detached assignment",
        $"Database? side = {initializer};\nside = new Database(false, true);\nside." + load + ";");
      DenyReadAllowWrite($"ReadDwgFile with only {initializer} definitions",
        $"Database? side = {initializer};\nside!." + load + ";", 2);
      DenyReadAllowWrite($"ReadDwgFile after {initializer}, detached and open-drawing assignments",
        $"Database? side = {initializer};\nside = new Database(false, true);\nif (Args.Count > 0) side = Db;\nside!." + load + ";", 4);
    }
    foreach (var value in new[] {
      "Args.Count > 0 ? new Database(false, true) : null",
      "Args.Count > 0 ? default : new Database(false, true)",
      "(Database?)null ?? new Database(false, true)",
      "new Database(false, true) ?? default(Database)",
      "Args.Count > 0 ? null : default(Database)" })
    {
      var code = $"Database? side = {value};\nside!." + load + ";";
      if (value == "Args.Count > 0 ? null : default(Database)")
        DenyReadAllowWrite("ReadDwgFile with only null/default conditional definitions", code, 2);
      else
        AllowRead($"ReadDwgFile with nullable detached target ({value})", code);
    }
    AllowRead("ReadDwgFile through a null-only alias coalesced with a detached database",
      "Database? empty = null;\nvar side = empty ?? new Database(false, true);\nside." + load + ";");
    AllowRead("ReadDwgFile after resetting a detached local to null",
      "Database? side = new Database(false, true);\nif (Args.Count > 0) side = null;\nside?." + load + ";");
    DenyReadAllowWrite("ReadDwgFile with null and open-drawing conditional targets",
      "Database? side = Args.Count > 0 ? null : Db;\nside!." + load + ";", 2);
    DenyReadAllowWrite("ReadDwgFile with null coalesced into an open drawing",
      "Database? side = default(Database) ?? Db;\nside." + load + ";", 2);
    DenyReadAllowWrite("ReadDwgFile after null initialization and parenthesized ref exposure",
      "void Swap(ref Database? target) { target = Db; }\nDatabase? side = null;\nside = new Database(false, true);\nSwap(ref (side));\nside!." + load + ";", 5);
    AllowRead("ReadDwgFile with detached-only conditional targets", "var side = Args.Count > 0 ? new Database(false, true) : OpenSide(\"x.dwg\");\nside." + load + ";");
    AllowRead("ReadDwgFile on OpenSide", "var side = OpenSide(\"x.dwg\");\nside." + load + ";");
    AllowRead("ReadDwgFile on OpenSideOrNull", "var side = OpenSideOrNull(\"x.dwg\");\nside!." + load + ";");
    AllowRead("ReadDwgFile after detached-only reassignments", "Database side = new Database(false, true);\nside = OpenSide(\"x.dwg\");\nside." + load + ";");
    AllowRead("ReadDwgFile through a detached alias", "var side = new Database(false, true);\nvar alias = side;\nalias." + load + ";");
    AllowRead("DxfIn on a detached database", "var side = new Database(false, true);\nside.DxfIn(\"x.dxf\", \"x.log\");");
    AllowRead("partial-open filters on a detached database", "var side = new Database(false, true);\nside.ApplyPartialOpenFilters(null, null);\nside.DisablePartialOpen();");

    // Wblock returns a separate Database. The explicit-output overload may touch its output.
    AllowRead("copy-returning Wblock overloads", "var whole = Db.Wblock();\nvar block = Db.Wblock(Db.BlockTableId);\nvar selected = Db.Wblock(new ObjectIdCollection(), new Point3d(0, 0, 0));");
    AllowRead("ReadDwgFile on a Wblock result", "var side = Db.Wblock();\nside." + load + ";");
    AllowRead("Wblock with detached explicit output", "var output = new Database(false, true);\nDb.Wblock(output, new ObjectIdCollection(), new Point3d(0, 0, 0), DuplicateRecordCloning.Ignore);");
    AllowRead("Wblock with aliased detached output", "var output = new Database(false, true);\nvar alias = output;\nDb.Wblock(alias, new ObjectIdCollection(), new Point3d(0, 0, 0), DuplicateRecordCloning.Ignore);");
    AllowRead("Wblock with reordered named detached output", "var output = new Database(false, true);\nDb.Wblock(cloning: DuplicateRecordCloning.Ignore, basePoint: new Point3d(0, 0, 0), outObjIds: new ObjectIdCollection(), outputDataBase: output);");
    DenyReadAllowWrite("Wblock with Db as output", "Db.Wblock(Db, new ObjectIdCollection(), new Point3d(0, 0, 0), DuplicateRecordCloning.Ignore);");
    DenyReadAllowWrite("Wblock with aliased Db output", "var alias = Db;\nDb.Wblock(alias, new ObjectIdCollection(), new Point3d(0, 0, 0), DuplicateRecordCloning.Ignore);", 2);
    DenyReadAllowWrite("Wblock with mixed output", "Database output = new Database(false, true);\nif (Args.Count > 0) output = Db;\nDb.Wblock(output, new ObjectIdCollection(), new Point3d(0, 0, 0), DuplicateRecordCloning.Ignore);", 3);

    const string cloneArgs = "new ObjectIdCollection(), OWNER, new IdMapping(), DuplicateRecordCloning.Ignore, false";
    AllowRead("WblockCloneObjects to a detached database property", "var side = new Database(false, true);\nDb.WblockCloneObjects(" + cloneArgs.Replace("OWNER", "side.BlockTableId") + ");");
    AllowRead("WblockCloneObjects to an aliased detached owner", "var side = new Database(false, true);\nvar owner = side.BlockTableId;\nDb.WblockCloneObjects(" + cloneArgs.Replace("OWNER", "owner") + ");");
    AllowRead("WblockCloneObjects to detached GetObjectId(false)", "var side = new Database(false, true);\nDb.WblockCloneObjects(" + cloneArgs.Replace("OWNER", "side.GetObjectId(false, new Handle(1), 0)") + ");");
    AllowRead("WblockCloneObjects to detached model space", "var side = new Database(false, true);\nDb.WblockCloneObjects(" + cloneArgs.Replace("OWNER", "SymbolUtilityServices.GetBlockModelSpaceId(side)") + ");");
    AllowRead("WblockCloneObjects with reordered named arguments", "var side = new Database(false, true);\nDb.WblockCloneObjects(deferTranslation: false, cloning: DuplicateRecordCloning.Ignore, mapping: new IdMapping(), id: side.BlockTableId, identifiers: new ObjectIdCollection());");
    DenyReadAllowWrite("WblockCloneObjects to Db owner", "var side = new Database(false, true);\nside.WblockCloneObjects(" + cloneArgs.Replace("OWNER", "Db.BlockTableId") + ");", 2);
    DenyReadAllowWrite("WblockCloneObjects to aliased Db owner", "var owner = Db.BlockTableId;\nvar alias = owner;\nDb.WblockCloneObjects(" + cloneArgs.Replace("OWNER", "alias") + ");", 3);
    DenyReadAllowWrite("WblockCloneObjects to unknown owner", "var owner = Doc.Database.BlockTableId;\nDb.WblockCloneObjects(" + cloneArgs.Replace("OWNER", "owner") + ");", 2);
    DenyReadAllowWrite("WblockCloneObjects after parenthesized ref replacement", "void Swap(ref Database target) { target = Db; }\nvar source = new Database(false, true);\nvar target = new Database(true, true);\nvar ids = new ObjectIdCollection(); ids.Add(source.CurrentSpaceId);\nSwap(ref (target));\nsource.WblockCloneObjects(ids, target.CurrentSpaceId, new IdMapping(), DuplicateRecordCloning.Ignore, false);", 6);

    AllowRead("GetObjectId(false)", "return Db.GetObjectId(false, new Handle(1), 0);");
    AllowRead("GetObjectId with const false", "const bool create = false;\nreturn Db.GetObjectId(create, new Handle(1), 0);");
    AllowRead("GetObjectId with reordered named false", "return Db.GetObjectId(identifier: 0, objHandle: new Handle(1), createIfNotFound: false);");
    DenyReadAllowWrite("GetObjectId(true)", "Db.GetObjectId(true, new Handle(1), 0);");
    DenyReadAllowWrite("GetObjectId with unknown flag", "bool create = Args.Count > 0;\nDb.GetObjectId(create, new Handle(1), 0);", 2);
    DenyReadAllowWrite("GetObjectId with reordered named true", "Db.GetObjectId(identifier: 0, objHandle: new Handle(1), createIfNotFound: true);");

    // Method-group conversion must not let a later invocation bypass the guard.
    DenyReadAllowWrite("ReadDwgFile method group", "Action<string, System.IO.FileShare, bool, string> load = Db.ReadDwgFile;");
    DenyReadAllowWrite("GetObjectId method group", "Func<bool, Handle, int, ObjectId> lookup = Db.GetObjectId;");
    DenyReadAllowWrite("WblockCloneObjects method group", "Action<ObjectIdCollection, ObjectId, IdMapping, DuplicateRecordCloning, bool> clone = Db.WblockCloneObjects;");
    DenyReadAllowWrite("Wblock explicit-output method group", "Action<Database, ObjectIdCollection, Point3d, DuplicateRecordCloning> copy = Db.Wblock;");
    DenyReadAllowWrite("SetDimstyleData method group", "Action<DimStyleTableRecord> set = Db.SetDimstyleData;");
    DenyReadAllowWrite("SetWorldPaperspaceUcsBaseOrigin method group", "Action<Point3d, OrthographicView> set = Db.SetWorldPaperspaceUcsBaseOrigin;");
    DenyReadAllowWrite("SetWorldUcsBaseOrigin method group", "Action<Point3d, OrthographicView> set = Db.SetWorldUcsBaseOrigin;");
    DenyReadAllowWrite("SetTimeZoneAsUtcOffset method group", "Func<double, Autodesk.AutoCAD.DatabaseServices.TimeZone> set = Db.SetTimeZoneAsUtcOffset;");

    // These do not write the Database. Purge filters its supplied collection/graph.
    AllowRead("Purge(ObjectIdCollection)", "Db.Purge(new ObjectIdCollection());");
    AllowRead("Purge(ObjectIdGraph)", "Db.Purge(new ObjectIdGraph());");
    AllowRead("CloseInput", "Db.CloseInput(true);");
    AllowRead("DxfOut", "Db.DxfOut(\"x.dxf\", 16, DwgVersion.Current);");
    AllowRead("CountEmptyObjects", "return Db.CountEmptyObjects(0);");
    AllowRead("CountHardReferences", "var ids = new ObjectIdCollection();\nvar counts = new int[0];\nDb.CountHardReferences(ids, counts);");
    AllowRead("GetDimstyleData", "return Db.GetDimstyleData();");
    AllowRead("WorldUcsBaseOrigin", "return Db.WorldUcsBaseOrigin((OrthographicView)0);");
    AllowRead("WorldPaperspaceUcsBaseOrigin", "return Db.WorldPaperspaceUcsBaseOrigin((OrthographicView)0);");
    AllowRead("TryGetObjectId", "return Db.TryGetObjectId(new Handle(1), out ObjectId id);");

    // Matching names on user-defined functions must not be mistaken for Database calls.
    AllowRead("similarly named local functions", """
      void SetDimstyleData() { }
      void ReadDwgFile() { }
      void WblockCloneObjects() { }
      void DisableUndoRecording() { }
      SetDimstyleData(); ReadDwgFile(); WblockCloneObjects(); DisableUndoRecording();
      """);
  }
}
