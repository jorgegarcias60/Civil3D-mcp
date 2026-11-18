using Civil3DMcpPlugin;

// AutoCAD-free checks of the restore comparison used after plotting (finding cad-tools#18).
internal static class SystemVariableValuesChecks
{
  internal static void Run(Action<bool, string> expect)
  {
    expect(SystemVariableValues.Same("Model", "MODEL"), "a layout name equals the same name in another case");
    expect(!SystemVariableValues.Same("GP1", "Model"), "different layout names differ");
    expect(SystemVariableValues.Same((short)0, 0) && SystemVariableValues.Same(1, (short)1),
      "numbers compare by value whatever their boxed type");
    expect(!SystemVariableValues.Same((short)0, 1), "different numbers differ");
    expect(!SystemVariableValues.Same(null, 0) && !SystemVariableValues.Same(0, null),
      "a null on either side is never the same, so it is not skipped");
    expect(!SystemVariableValues.Same("0", 0), "a string and a number are not the same");
    expect(SystemVariableValues.Describe("GP1") == "'GP1'" && SystemVariableValues.Describe((short)0) == "0"
      && SystemVariableValues.Describe(null) == "null", "values are described for the log line");
  }
}
