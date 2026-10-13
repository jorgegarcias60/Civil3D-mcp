using Civil3DMcpPlugin;

// Compiler regressions only. None of these snippets execute Autodesk APIs or IO.
internal static class PreviewGuardChecks
{
  internal static void Run(Func<string, bool, ScriptCompileResult> compile,
    Action<bool, string, ScriptCompileResult?> expect, Func<string, ScriptCompileResult> compileRead)
  {
    CheckFileSystem(compile, expect, compileRead);

    foreach (var (label, code, line) in new[] {
      ("another drawing's committed transaction", """
        var other = Application.DocumentManager.Cast<Document>().First(d => d != Doc);
        using (other.LockDocument())
        using (var tx = other.Database.TransactionManager.StartTransaction())
        {
          var table = (BlockTable)tx.GetObject(other.Database.BlockTableId, OpenMode.ForRead);
          var space = (BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
          var text = new DBText();
          space.AppendEntity(text);
          tx.AddNewlyCreatedDBObject(text, true);
          tx.Commit();
        }
        """, 3),
      ("another drawing without an explicit lock", "var other = Application.DocumentManager.MdiActiveDocument.Database;\nusing var tx = other.TransactionManager.StartTransaction(); tx.Commit();", 2),
      ("mixed database alias", "var db = Db;\nif (Args.Count > 0) db = Application.DocumentManager.MdiActiveDocument.Database;\nusing var tx = db.TransactionManager.StartTransaction();", 3),
      ("transaction manager alias from another drawing", "var manager = Application.DocumentManager.MdiActiveDocument.Database.TransactionManager;\nusing var tx = manager.StartTransaction();", 2),
      ("database alias exposed by ref", "void Swap(ref Database db) {}\nvar db = Db; Swap(ref db);\nusing var tx = db.TransactionManager.StartTransaction();", 3),
      ("database alias replaced by deconstruction", "var db = Db;\n(db, _) = (Application.DocumentManager.MdiActiveDocument.Database, 0);\nusing var tx = db.TransactionManager.StartTransaction();", 3),
      ("manager alias replaced by deconstruction", "var manager = Db.TransactionManager;\n(manager, _) = (Application.DocumentManager.MdiActiveDocument.Database.TransactionManager, 0);\nusing var tx = manager.StartTransaction();", 3),
      ("side database transaction", "var side = OpenSide(\"C:/source.dwg\");\nusing var tx = side.TransactionManager.StartTransaction();", 2),
      ("StreamWriter", "using var writer = new StreamWriter(Path.Combine(RunFolder, \"out.txt\")); writer.Write(\"x\");", 1),
      ("qualified StreamWriter", "using var writer = new System.IO.StreamWriter(\"out.txt\");", 1),
      ("target-typed StreamWriter", "using StreamWriter writer = new(\"out.txt\");", 1),
      ("FileStream", "using var stream = new FileStream(\"out.txt\", FileMode.Create); stream.WriteByte(1);", 1),
      ("target-typed FileStream", "using FileStream stream = new(\"out.txt\", FileMode.Create);", 1),
      ("FileStream creation with read access", "using var stream = new FileStream(\"out.txt\", FileMode.OpenOrCreate, FileAccess.Read);", 1),
      ("Session array assignment", "var a = (int[])Session[\"counters\"]!;\na[0] = 5;", 2),
      ("Session array compound assignment", "var a = (int[])Session[\"counters\"]!;\na[0] += 5;", 2),
      ("Session array post-increment", "var a = (int[])Session[\"counters\"]!;\na[0]++;", 2),
      ("Session array pre-increment", "var a = (int[])Session[\"counters\"]!;\n++a[0];", 2),
      ("Session array post-decrement", "var a = (int[])Session[\"counters\"]!;\na[0]--;", 2),
      ("Session array pre-decrement", "var a = (int[])Session[\"counters\"]!;\n--a[0];", 2),
      ("Session object field increment", "var box = (System.Runtime.CompilerServices.StrongBox<int>)Session[\"box\"]!;\nbox.Value++;", 2),
      ("Session array alias increment", "var a = (int[])Session[\"counters\"]!; var b = a;\nb[0]++;", 2),
      ("Session array element ref assignment", "var a = (int[])Session[\"counters\"]!;\nref var element = ref a[0];\nelement = 5;", 3),
      ("Session array element ref increment", "var a = (int[])Session[\"counters\"]!;\nref var element = ref a[0];\nelement++;", 3),
      ("Session array element ref alias", "var a = (int[])Session[\"counters\"]!;\nref var element = ref a[0]; ref var alias = ref element;\nalias++;", 3),
      ("Session array element ref reassignment", "var a = (int[])Session[\"counters\"]!;\nref var element = ref a[0]; element = ref a[1];\nelement++;", 3),
      ("Session list mutation", "var list = (List<int>)Session[\"values\"]!;\nlist.Add(1);", 2),
      ("cached Session list Clear", "var list = (List<int>)Session[\"values\"]!;\nlist.Clear();", 2),
      ("cached Session list Sort", "var list = (List<int>)Session[\"values\"]!;\nlist.Sort();", 2),
      ("cached Session list indexed write", "var list = (List<int>)Session[\"values\"]!;\nlist[0] = 5;", 2),
      ("cached Session dictionary mutation", "var lookup = (Dictionary<string, int>)Session[\"lookup\"]!;\nlookup.Remove(\"key\");", 2),
      ("cached Session dictionary indexed write", "var lookup = (Dictionary<string, int>)Session[\"lookup\"]!;\nlookup[\"key\"] = 5;", 2),
      ("shared list returned by a read-only finder", "var lists = (List<List<int>>)Session[\"lists\"]!;\nvar alias = lists.Find(list => list.Count > 0); alias!.Add(1);", 2),
      ("shared list element in ToArray copy", "var lists = (List<List<int>>)Session[\"lists\"]!; var copy = lists.ToArray();\ncopy[0].Add(1);", 2),
      ("shared list element in GetRange copy", "var lists = (List<List<int>>)Session[\"lists\"]!; var copy = lists.GetRange(0, 1);\ncopy[0].Add(1);", 2),
      ("conditional access shared list element", "var lists = (List<List<int>>)Session[\"lists\"]!;\nvar alias = lists?.FirstOrDefault();\nalias?.Add(1);", 3),
      ("deconstruction declaration of Session list", "var (list, n) = ((List<int>)Session[\"values\"]!, 1);\nlist.Add(n);", 2),
      ("deconstruction assignment of Session list", "List<int> list = new(); int n = 0;\n(list, n) = ((List<int>)Session[\"values\"]!, 1);\nlist.Add(n);", 3),
      ("nested deconstruction of Session list", "var ((n, list), other) = ((1, (List<int>)Session[\"values\"]!), 0);\nlist.Add(n);", 2),
      ("nested tuple containing Session list", "var pair = (nested: ((List<int>)Session[\"values\"]!, 1), n: 0);\npair.nested.Item1.Add(1);", 2),
      ("unnamed tuple Item1 Session alias", "var pair = ((List<int>)Session[\"values\"]!, 1);\npair.Item1.Add(1);", 2),
      ("unnamed tuple Item2 Session alias", "var pair = (1, (List<int>)Session[\"values\"]!);\npair.Item2.Add(1);", 2),
      ("named tuple Item1 Session alias", "var pair = (values: (List<int>)Session[\"values\"]!, n: 1);\npair.Item1.Add(1);", 2),
      ("named tuple Item2 Session alias", "var pair = (n: 1, values: (List<int>)Session[\"values\"]!);\npair.Item2.Add(1);", 2),
      ("direct Session value mutation", "((List<int>)Session[\"values\"]!).Add(1);", 1),
      ("parenthesized Session alias mutation", "var list = ((List<int>)Session[\"values\"]!);\n(list!).Add(1);", 2),
      ("Session TryGetValue out declaration", "Session.TryGetValue(\"values\", out var value);\n((List<int>)value!).Add(1); return 1;", 2),
      ("Session TryGetValue existing out local", "object? value; Session.TryGetValue(\"values\", out value);\n((List<int>)value!).Add(1);", 2),
      ("Session TryGetValue named out argument through an alias", "var cache = Session; cache.TryGetValue(value: out var value, key: \"values\");\nvar list = (List<int>)value!; list.Add(1);", 2),
      ("Session TryGetValue array assignment", "Session.TryGetValue(\"counters\", out var value);\n((int[])value!)[0] = 5;", 2),
      ("Session TryGetValue array increment", "Session.TryGetValue(\"counters\", out var value);\nvar a = (int[])value!; a[0]++;", 2),
      ("Session foreach entry mutation", "foreach (var kv in Session)\n  ((List<int>)kv.Value!).Add(1);", 2),
      ("Session foreach array assignment", "foreach (var kv in Session)\n  ((int[])kv.Value!)[0] = 5;", 2),
      ("Session foreach array increment", "foreach (var value in Session.Values)\n  ((int[])value!)[0]++;", 2),
      ("Session foreach through a shallow copy", "var entries = Session.ToArray(); foreach (var kv in entries)\n  ((List<int>)kv.Value!).Add(1);", 2),
      ("Session foreach deconstructed value", "foreach (var (key, value) in Session)\n  ((List<int>)value!).Add(1);", 2),
      ("shared value inside a detached shallow copy", "var entries = Session.ToArray();\n((List<int>)entries[0].Value!).Add(1);", 2) })
    {
      var preview = compile(code, true);
      expect(!preview.Succeeded && preview.Errors.All(error => error.Code == ScriptCompiler.GuardDiagnosticId)
        && preview.Errors.Any(error => error.Line == line), $"preview refuses {label} on snippet line {line}", preview);
      var write = compile(code, false);
      expect(write.Succeeded, $"write mode still allows {label}", write);
    }

    foreach (var code in new[] {
      "using var tx = Doc.Database.TransactionManager.StartTransaction(); tx.Commit();",
      "using var stream = new FileStream(\"input.txt\", FileMode.Open, FileAccess.Read); return stream.ReadByte();",
      "using var tx = ((Db!)).TransactionManager.StartTransaction(); tx.Commit();",
      "var doc = Doc; var db = doc.Database; var manager = db.TransactionManager;\nusing var tx = manager.StartTransaction(); tx.Commit();",
      "var db = Args.Count > 0 ? Db : Doc.Database;\nusing var tx = db.TransactionManager.StartTransaction(); tx.Commit();",
      "var n = Session.Count; return n.CompareTo(0);",
      "return Session.Count.CompareTo(0);",
      "return ((string)Session[\"name\"]!).StartsWith(\"A\");",
      "var name = (string)Session[\"name\"]!; return name.Split(',').First().Substring(1);",
      "var a = Session.ToArray(); a[0] = default; return a.Length;",
      "var a = Session.ToList(); a.Clear(); return a.Count;",
      "var a = (int[])Session[\"counters\"]!; var copy = a.ToArray(); copy[0]++; return copy;",
      "var a = (int[])Session[\"counters\"]!; var copy = (int[])a.Clone(); copy[0]++; return copy;",
      "var list = (List<int>)Session[\"values\"]!; var copy = list.ToArray(); copy[0]++; return copy;",
      "var list = (List<int>)Session[\"values\"]!; var copy = list.ToArray(); copy[0] = 5; return copy;",
      "var list = (List<int>)Session[\"values\"]!; var copy = list.GetRange(0, 1); copy.Add(1); return copy;",
      "var list = (List<int>)Session[\"values\"]!; return list.IndexOf(1) + list.BinarySearch(1) + list.Find(x => x > 0);",
      "return ((List<int>)Session[\"values\"]!).IndexOf(1);",
      "var lookup = (Dictionary<string, int>)Session[\"lookup\"]!; return lookup.ContainsValue(1);",
      "var values = (HashSet<int>)Session[\"values\"]!; return values.Contains(1);",
      "return ((StringBuilder)Session[\"text\"]!).ToString();",
      "var list = (List<int>)Session[\"values\"]!; var (copy, n) = (list.ToArray(), 1); copy[0]++; return n;",
      "var list = (List<int>)Session[\"values\"]!; int[] copy = Array.Empty<int>(); int n = 0; (copy, n) = (list.ToArray(), 1); copy[0]++; return n;",
      "var list = (List<int>)Session[\"values\"]!; var pair = (list.ToArray(), list.GetRange(0, 1)); pair.Item1[0]++; pair.Item2.Add(1); return pair;",
      "var list = (List<int>)Session[\"values\"]!; var pair = (shared: list, copy: list.ToArray()); pair.Item2[0]++; return pair;",
      "var list = (List<int>)Session[\"values\"]!; var pair = (copy: list.GetRange(0, 1), shared: list); pair.copy.Add(1); return pair;",
      "var list = (List<int>)Session[\"values\"]!; var (shared, copy) = (list, list.ToArray()); copy[0]++; return copy;",
      "var list = (List<int>)Session[\"values\"]!; var ((shared, n), copy) = ((list, 1), list.ToArray()); copy[0] = n; return copy;",
      "var a = (int[])Session[\"counters\"]!; var copy = a.ToArray(); ref var element = ref copy[0]; element = 5; element++; return copy;",
      "var a = (int[])Session[\"counters\"]!; var copy = a.ToArray(); ref var element = ref copy[0]; ref var alias = ref element; alias = ref copy[1]; alias++; return copy;",
      "var list = (List<int>)Session[\"values\"]!; var copy = list?.ToArray(); if (copy != null) copy[0]++; return copy;",
      "var a = (int[])Session[\"counters\"]!; var n = a[0]; n++; --n; return n.CompareTo(0);",
      "var n = (int)Session[\"counter\"]!; n++; return n;",
      "var box = (System.Runtime.CompilerServices.StrongBox<int>)Session[\"box\"]!; var n = box.Value; ++n; return n;",
      "Session.TryGetValue(\"counter\", out var value); var n = (int)value!; n++; return n.CompareTo(0);",
      "Session.TryGetValue(\"name\", out var value); return ((string)value!).StartsWith(\"A\");",
      "Session.TryGetValue(\"counters\", out var value); var copy = ((int[])value!).ToArray(); copy[0]++; return copy;",
      "foreach (var kv in Session) { var n = (int)kv.Value!; n++; var name = kv.Key; name.StartsWith(\"A\"); }",
      "foreach (var value in Session.Values) { var n = (int)value!; ++n; ((string)value!).StartsWith(\"A\"); }",
      "foreach (var (key, value) in Session) { var n = (int)value!; n++; key.StartsWith(\"A\"); }",
      "var cache = new Dictionary<string, object>(); cache.TryGetValue(\"values\", out var value); ((List<int>)value!).Add(1);",
      "var lists = new List<List<int>>(); foreach (var list in lists) list.Add(1);",
      "var list = (List<int>)Session[\"values\"]!; list = new List<int>(); return list.Count;" })
    {
      var result = compile(code, true);
      expect(result.Succeeded, "preview permits run-database nesting or independent Session reads/copies: " + code, result);
    }
  }

  private static void CheckFileSystem(Func<string, bool, ScriptCompileResult> compile,
    Action<bool, string, ScriptCompileResult?> expect, Func<string, ScriptCompileResult> compileRead)
  {
    var refused = new List<(string Label, string Code)>
    {
      ("File.Open without explicit read access", "using var stream = File.Open(\"out.txt\", FileMode.Open);"),
      ("File.Open with write access", "using var stream = File.Open(\"out.txt\", FileMode.Open, FileAccess.Write);"),
      ("File.Open with read/write access", "using var stream = File.Open(\"out.txt\", FileMode.Open, FileAccess.ReadWrite);"),
      ("File.Open with variable mode", "var mode = FileMode.Open; using var stream = File.Open(\"input.txt\", mode, FileAccess.Read);"),
      ("File.Open with variable access", "var access = FileAccess.Read; using var stream = File.Open(\"input.txt\", FileMode.Open, access);"),
      ("File.Open with variable share", "var share = FileShare.Read; using var stream = File.Open(\"input.txt\", FileMode.Open, FileAccess.Read, share);"),
      ("File.Open method group", "Func<string, FileMode, FileAccess, FileStream> open = File.Open;"),
      ("File.Open options", "using var stream = File.Open(\"input.txt\", new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read });"),
      ("aliased File.Open create", "using F = System.IO.File;\nusing var stream = F.Open(\"out.txt\", FileMode.Create);"),
      ("using static File.Create", "using static System.IO.File;\nusing var stream = Create(\"out.txt\");"),
      ("File.Create", "using var stream = File.Create(\"out.txt\");"),
      ("File.AppendAllText", "File.AppendAllText(\"out.txt\", \"x\");"),
      ("FileInfo.Delete", "new FileInfo(\"out.txt\").Delete();"),
      ("FileInfo.Delete method group", "Action delete = new FileInfo(\"out.txt\").Delete;"),
      ("FileInfo.Open", "using var stream = new FileInfo(\"out.txt\").Open(FileMode.Create);"),
      ("FileInfo.Open without explicit read access", "using var stream = new FileInfo(\"input.txt\").Open(FileMode.Open);"),
      ("FileInfo.Open with variable access", "var access = FileAccess.Read; using var stream = new FileInfo(\"input.txt\").Open(FileMode.Open, access);"),
      ("FileInfo.Open with variable share", "var share = FileShare.Read; using var stream = new FileInfo(\"input.txt\").Open(FileMode.Open, FileAccess.Read, share);"),
      ("FileInfo.Open options", "using var stream = new FileInfo(\"input.txt\").Open(new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read });"),
      ("FileInfo.CreateText", "using var writer = new FileInfo(\"out.txt\").CreateText();"),
      ("FileInfo.Attributes setter", "new FileInfo(\"out.txt\").Attributes = FileAttributes.Normal;"),
      ("FileSystemInfo timestamp setter", "FileSystemInfo info = new FileInfo(\"out.txt\"); info.LastWriteTimeUtc = DateTime.UtcNow;"),
      ("FileSystemInfo attributes compound assignment", "FileSystemInfo info = new FileInfo(\"out.txt\"); info.Attributes |= FileAttributes.Hidden;"),
      ("filesystem property deconstruction", "var info = new FileInfo(\"out.txt\"); (info.Attributes, info.LastWriteTimeUtc) = (FileAttributes.Normal, DateTime.UtcNow);"),
      ("Directory.CreateDirectory", "Directory.CreateDirectory(\"output\");"),
      ("DirectoryInfo.Create", "new DirectoryInfo(\"output\").Create();"),
      ("DirectoryInfo.Delete", "new DirectoryInfo(\"output\").Delete();"),
      ("DirectoryInfo timestamp setter", "new DirectoryInfo(\"output\").CreationTimeUtc = DateTime.UtcNow;"),
      ("Directory.SetCurrentDirectory", "Directory.SetCurrentDirectory(\"output\");"),
      ("Path.GetTempFileName", "return Path.GetTempFileName();"),
      ("FileStream default read/write access", "using var stream = new FileStream(\"input.txt\", FileMode.Open);"),
      ("FileStream variable mode", "var mode = FileMode.Open; using var stream = new FileStream(\"input.txt\", mode, FileAccess.Read);"),
      ("FileStream variable access", "var access = FileAccess.Read; using var stream = new FileStream(\"input.txt\", FileMode.Open, access);"),
      ("FileStream variable share", "var share = FileShare.Read; using var stream = new FileStream(\"input.txt\", FileMode.Open, FileAccess.Read, share);"),
      ("FileStream variable FileOptions", "var options = FileOptions.SequentialScan; using var stream = new FileStream(\"input.txt\", FileMode.Open, FileAccess.Read, FileShare.Read, 4096, options);"),
      ("target-typed FileStream DeleteOnClose with named arguments", "using FileStream stream = new(\"input.txt\", options: FileOptions.DeleteOnClose, bufferSize: 4096, share: FileShare.Read, access: FileAccess.Read, mode: FileMode.Open);"),
      ("FileStream write access", "using var stream = new FileStream(\"input.txt\", FileMode.Open, FileAccess.Write);"),
      ("FileStream options", "using FileStream stream = new(\"input.txt\", new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read });"),
      ("explicit FileStream options", "using var stream = new FileStream(\"input.txt\", new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Options = FileOptions.SequentialScan });"),
      ("FileStream handle constructor", "using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(IntPtr.Zero, false); using var stream = new FileStream(handle, FileAccess.Read);"),
      ("FileStream.SetLength", "using var stream = File.OpenRead(\"input.txt\"); stream.SetLength(0);"),
      ("FileStream.WriteByte", "using var stream = File.OpenRead(\"input.txt\"); stream.WriteByte(1);"),
      ("BinaryWriter", "using var writer = new BinaryWriter(new MemoryStream());"),
      ("target-typed BinaryWriter", "using BinaryWriter writer = new(new MemoryStream());"),
      ("BufferedStream", "using var stream = new BufferedStream(File.OpenRead(\"input.txt\"));"),
      ("GZipStream writer", "using var stream = new System.IO.Compression.GZipStream(new MemoryStream(), System.IO.Compression.CompressionMode.Compress);"),
    };
    foreach (var mode in new[] { "Create", "CreateNew", "Append", "Truncate", "OpenOrCreate" })
    {
      refused.Add(($"File.Open {mode}", $"using var stream = File.Open(\"out.txt\", FileMode.{mode});"));
      refused.Add(($"File.Open {mode} with read access", $"using var stream = File.Open(\"out.txt\", FileMode.{mode}, FileAccess.Read);"));
      refused.Add(($"FileStream {mode} with read access", $"using var stream = new FileStream(\"out.txt\", FileMode.{mode}, FileAccess.Read);"));
      refused.Add(($"FileInfo.Open {mode} with read access", $"using var stream = new FileInfo(\"out.txt\").Open(FileMode.{mode}, FileAccess.Read);"));
    }
    foreach (var options in new[] { "FileOptions.DeleteOnClose", "FileOptions.Encrypted", "FileOptions.WriteThrough",
      "FileOptions.SequentialScan | FileOptions.DeleteOnClose", "(FileOptions)1" })
    {
      refused.Add(($"FileStream {options}", $"using var stream = new FileStream(\"input.txt\", FileMode.Open, FileAccess.Read, FileShare.Read, 4096, {options});"));
    }
    foreach (var (label, code) in refused)
    {
      var preview = compile(code, true);
      expect(!preview.Succeeded && preview.Errors.All(error => error.Code == ScriptCompiler.GuardDiagnosticId)
        && preview.Errors.Any(error => error.Line == (code.StartsWith("using F =") || code.StartsWith("using static") ? 2 : 1)),
        $"preview refuses filesystem operation: {label}", preview);
      var write = compile(code, false);
      expect(write.Succeeded, $"write mode still allows filesystem operation: {label}", write);
      var read = compileRead(code);
      expect(read.Succeeded, $"read mode keeps its filesystem rules: {label}", read);
    }

    var allowed = new List<string>
    {
      "using var stream = File.Open(\"input.txt\", FileMode.Open, FileAccess.Read); return stream.ReadByte();",
      "using var stream = File.Open(path: \"input.txt\", access: FileAccess.Read, mode: FileMode.Open); return stream.Length;",
      "const FileMode mode = FileMode.Open; const FileAccess access = FileAccess.Read; using var stream = File.Open(\"input.txt\", mode, access, FileShare.Read);",
      "using var stream = File.Open(\"input.txt\", (FileMode)3, (FileAccess)1); return stream.ReadByte();",
      "using F = System.IO.File;\nusing var stream = F.Open(\"input.txt\", FileMode.Open, FileAccess.Read);",
      "using static System.IO.File;\nusing var stream = OpenRead(\"input.txt\");",
      "using var stream = new FileStream(\"input.txt\", FileMode.Open, FileAccess.Read); return stream.ReadByte();",
      "using FileStream stream = new(\"input.txt\", mode: FileMode.Open, access: FileAccess.Read); return stream.ReadByte();",
      "using FileStream stream = new(\"input.txt\", options: FileOptions.SequentialScan, bufferSize: 4096, share: FileShare.Read, access: FileAccess.Read, mode: FileMode.Open); return stream.ReadByte();",
      "const FileOptions options = FileOptions.SequentialScan | FileOptions.Asynchronous; const FileShare share = FileShare.Read; using var stream = new FileStream(\"input.txt\", FileMode.Open, FileAccess.Read, share, 4096, options);",
      "using var stream = new FileStream(\"input.txt\", FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);",
      "using var stream = new FileInfo(\"input.txt\").Open(FileMode.Open, FileAccess.Read); return stream.ReadByte();",
      "using var stream = new FileInfo(\"input.txt\").Open(share: FileShare.Read, access: FileAccess.Read, mode: FileMode.Open); return stream.Length;",
      "using var stream = File.OpenRead(\"input.txt\"); return stream.ReadByte();",
      "using var reader = File.OpenText(\"input.txt\"); return reader.ReadLine();",
      "using var stream = new FileInfo(\"input.txt\").OpenRead(); return stream.ReadByte();",
      "using var reader = new FileInfo(\"input.txt\").OpenText(); return reader.ReadLine();",
      "FileSystemInfo info = new FileInfo(\"input.txt\"); info.Refresh(); return info.Exists;",
      "return Path.GetFullPath(Path.Combine(\"input\", Path.ChangeExtension(\"file.txt\", \".csv\")));",
      "return Path.GetRandomFileName();",
    };
    foreach (var options in new[] { "None", "SequentialScan", "RandomAccess", "Asynchronous" })
      allowed.Add($"using var stream = new FileStream(\"input.txt\", FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.{options}); return stream.ReadByte();");
    foreach (var share in new[] { "None", "Read", "Write", "ReadWrite", "Delete", "Inheritable" })
    {
      allowed.Add($"using var stream = new FileStream(\"input.txt\", FileMode.Open, FileAccess.Read, FileShare.{share});");
      allowed.Add($"using var stream = File.Open(\"input.txt\", FileMode.Open, FileAccess.Read, FileShare.{share});");
      allowed.Add($"using var stream = new FileInfo(\"input.txt\").Open(FileMode.Open, FileAccess.Read, FileShare.{share});");
    }
    foreach (var member in new[] { "Exists", "ReadAllText", "ReadAllLines", "ReadAllBytes", "ReadLines",
      "GetAttributes", "GetCreationTime", "GetCreationTimeUtc", "GetLastAccessTime", "GetLastAccessTimeUtc",
      "GetLastWriteTime", "GetLastWriteTimeUtc" })
      allowed.Add($"return File.{member}(\"input.txt\");");
    foreach (var member in new[] { "Exists", "GetCreationTime", "GetCreationTimeUtc", "GetLastAccessTime", "GetLastAccessTimeUtc",
      "GetLastWriteTime", "GetLastWriteTimeUtc", "EnumerateFiles", "EnumerateDirectories", "EnumerateFileSystemEntries",
      "GetFiles", "GetDirectories", "GetFileSystemEntries", "GetDirectoryRoot" })
      allowed.Add($"return Directory.{member}(\"input\");");
    foreach (var member in new[] { "Length", "Name", "FullName", "Extension", "DirectoryName", "Directory", "Exists",
      "Attributes", "CreationTime", "CreationTimeUtc", "LastAccessTime", "LastAccessTimeUtc", "LastWriteTime", "LastWriteTimeUtc", "IsReadOnly" })
      allowed.Add($"return new FileInfo(\"input.txt\").{member};");
    foreach (var member in new[] { "Name", "FullName", "Extension", "Parent", "Root", "Exists", "Attributes",
      "CreationTime", "CreationTimeUtc", "LastAccessTime", "LastAccessTimeUtc", "LastWriteTime", "LastWriteTimeUtc" })
      allowed.Add($"return new DirectoryInfo(\"input\").{member};");
    foreach (var member in new[] { "EnumerateFiles", "EnumerateDirectories", "EnumerateFileSystemInfos", "GetFiles", "GetDirectories", "GetFileSystemInfos" })
      allowed.Add($"return new DirectoryInfo(\"input\").{member}();");
    foreach (var code in allowed)
    {
      var result = compile(code, true);
      expect(result.Succeeded, "preview permits read-only filesystem operation: " + code, result);
    }
  }

}
