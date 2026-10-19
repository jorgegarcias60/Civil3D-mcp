using Civil3DMcpPlugin;

// AutoCAD-free checks of the DBMOD bit names and the stop refusal text
// (DbmodFlags.cs, issue #54). Nothing here talks to Civil 3D.
internal static class DbmodFlagsChecks
{
  internal static void Run(Action<bool, string> expect)
  {
    expect(DbmodFlags.Describe(0).Count == 0 && DbmodFlags.Format(0) == "0", "DBMOD 0 has no flags and formats as 0");
    expect(DbmodFlags.Describe(1).SequenceEqual(["objects"]) && DbmodFlags.Format(1) == "1 (objects)", "bit 1 is objects");
    expect(DbmodFlags.Describe(4).SequenceEqual(["variables"]), "bit 4 is variables");
    expect(DbmodFlags.Describe(48).SequenceEqual(["view", "fields"]) && DbmodFlags.Format(48) == "48 (view, fields)",
      "48 is view and fields, lowest bit first");
    expect(DbmodFlags.Describe(2).SequenceEqual(["bit 2"]), "undocumented bit 2 is reported by its value");
    expect(DbmodFlags.Describe(63).SequenceEqual(["objects", "bit 2", "variables", "window", "view", "fields"]),
      "63 lists every bit in order");
    expect(DbmodFlags.Describe(64 | 8).SequenceEqual(["window", "bit 64"]), "unknown higher bits keep bit order");
    // AutoCAD returns DBMOD as a 16-bit short: its top bit must not sign-extend.
    expect(DbmodFlags.FromSystemVariable((short)-32768) == 32768
      && DbmodFlags.Describe(DbmodFlags.FromSystemVariable((short)-32768)).SequenceEqual(["bit 32768"]),
      "DBMOD's top bit (a negative short) reads as 32768, one 'bit 32768'");
    expect(DbmodFlags.FromSystemVariable((short)48) == 48 && DbmodFlags.FromSystemVariable((short)(-32768 | 1)) == 32769
      && DbmodFlags.FromSystemVariable(null) == 0, "DBMOD shorts read unsigned, null reads 0");

    expect(!DbmodFlags.HasContentChanges(0) && !DbmodFlags.HasContentChanges(8 | 16 | 32) && !DbmodFlags.HasContentChanges(2),
      "view, window, fields and bit 2 are not content changes");
    expect(DbmodFlags.HasContentChanges(1) && DbmodFlags.HasContentChanges(4) && DbmodFlags.HasContentChanges(4 | 16),
      "objects or variables are content changes");

    var json = DbmodFlags.ToJson(17);
    expect(json.Count == 2 && json["value"] is 17 && json["flags"] is string[] flags && flags.SequenceEqual(["objects", "view"]),
      "ToJson returns value and flags");
    expect(DbmodFlags.ToJson(0)["flags"] is string[] { Length: 0 }, "ToJson of 0 has an empty flag list");

    expect(DbmodFlags.FormatDrawings([("A.dwg", 1), ("B.dwg", 16)]) == "A.dwg (DBMOD 1 (objects)), B.dwg (DBMOD 16 (view))",
      "drawings are listed with their DBMOD value and flags");

    var content = DbmodFlags.UnsavedDrawingsMessage([("A.dwg", 1), ("B.dwg", 48)]);
    expect(content == "Instance has unsaved drawings: A.dwg (DBMOD 1 (objects)), B.dwg (DBMOD 48 (view, fields)). Pass discardChanges=true to close them without saving.",
      "a refusal with content changes keeps the old words around the list and adds no note");
    var display = DbmodFlags.UnsavedDrawingsMessage([("C:\\jobs\\A.dwg", 48), ("B.dwg", 8)]);
    expect(display.StartsWith("Instance has unsaved drawings: C:\\jobs\\A.dwg (DBMOD 48 (view, fields)), B.dwg (DBMOD 8 (window)).")
      && display.Contains(" Only view, window or field flags are set: no object or drawing-variable changes.")
      && display.EndsWith(" Pass discardChanges=true to close them without saving."),
      "a refusal for display-only flags says so between the list and the discard hint");
    var unknown = DbmodFlags.UnsavedDrawingsMessage([("A.dwg", 2 | 16)]);
    expect(unknown.Contains(" No object or drawing-variable changes are flagged.") && !unknown.Contains("Only view"),
      "an undocumented bit without content bits is not called a view, window or field flag");
    expect(System.Text.RegularExpressions.Regex.IsMatch(display, "unsaved", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
      "the refusal still matches /unsaved/i");

    // civil3d_drawing save: read right after SaveAs, and once the save command has ended.
    const string stillModified = "The file was written, but Civil 3D still counts the drawing as modified (DBMOD {0}): civil3d_instance stop refuses it without discardChanges.";
    expect(DbmodFlags.SaveWarnings(0, null, 0, null).Count == 0, "a save that leaves DBMOD 0 at both reads has no warning");
    expect(DbmodFlags.SaveWarnings(1, null, 1, null).SequenceEqual([string.Format(stillModified, "1 (objects)")]),
      "DBMOD not 0 at both reads: one warning that stop refuses");
    expect(DbmodFlags.SaveWarnings(0, null, 16, null).SequenceEqual([
        string.Format(stillModified, "16 (view)"),
        "DBMOD changed after the save: 0 right after Database.SaveAs, 16 (view) once the save command had ended."]),
      "DBMOD 0 after SaveAs but set once the command ended: the later value counts, and the change is reported");
    expect(DbmodFlags.SaveWarnings(1, null, 0, null).SequenceEqual([
        "DBMOD changed after the save: 1 (objects) right after Database.SaveAs, 0 once the save command had ended."]),
      "DBMOD cleared by the end of the command: no stop warning, only the change");
    expect(DbmodFlags.SaveWarnings(4, null, null, "host busy").SequenceEqual([
        "The file was written, but its DBMOD could not be read after the save command ended: host busy",
        string.Format(stillModified, "4 (variables)")]),
      "the later read failed: its error, and the value right after SaveAs decides the stop warning");
    expect(DbmodFlags.SaveWarnings(null, "no document", 0, null).SequenceEqual([
        "The file was written, but its DBMOD could not be read right after the save: no document"]),
      "the first read failed: its error only, when the later read is 0");
  }
}
