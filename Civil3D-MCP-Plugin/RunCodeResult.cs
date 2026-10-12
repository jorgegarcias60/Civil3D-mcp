using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

// A path is published only for a result whose serialization, flush, and
// disposal all succeeded. A failed partial file is never a completed result.
internal sealed record RunCodeResult(string? FilePath, long Bytes, string? Shape, string? Error)
{
  internal static RunCodeResult Save(string runFolder, Func<Stream, string> serialize)
  {
    string? path = null;
    var created = false;
    try
    {
      path = RunCodeLog.ResultPath(runFolder);
      string shape;
      long bytes;
      using (var stream = RunCodeLog.OpenResultStream(runFolder))
      {
        created = true;
        shape = serialize(stream);
        stream.Flush(flushToDisk: true);
        bytes = stream.Length;
      }
      return new RunCodeResult(path, bytes, shape, null);
    }
    catch (Exception exception)
    {
      if (created && path != null)
      {
        try { RunCodeLog.DeletePartialResult(path); }
        catch (Exception cleanupException)
        {
          PluginLog.Swallow("RunCode", "removing a partial result", cleanupException);
        }
      }
      return new RunCodeResult(null, 0, null, Describe(exception));
    }
  }

  internal void AddTo(JsonObject response, int maxInlineBytes, int previewChars)
  {
    if (Error != null)
    {
      response["resultError"] = $"The return value could not be written as JSON: {Error}";
      return;
    }
    if (FilePath == null) return;

    response["result"] = null;
    response["resultShape"] = Shape;
    response["resultBytes"] = Bytes;
    try
    {
      using var stream = RunCodeLog.OpenResultReadStream(FilePath);
      if (stream.Length != Bytes) throw new IOException("The saved result size changed.");
      if (Bytes <= maxInlineBytes)
      {
        response["result"] = JsonNode.Parse(stream);
        return;
      }

      response["resultFile"] = FilePath;
      response["resultPreview"] = ReadPreview(stream, previewChars) + "...";
    }
    catch (Exception exception)
    {
      response.Remove("resultPreview");
      response.Remove("resultFile");
      response["resultError"] = $"The saved return value could not be read: {Describe(exception)}";
    }
  }

  private static string ReadPreview(Stream stream, int previewChars)
  {
    var charLimit = Math.Clamp(previewChars, 0, 10000);
    if (charLimit == 0) return string.Empty;
    var bytes = new byte[(int)Math.Min(stream.Length, (long)charLimit * 4 + 4)];
    var count = 0;
    while (count < bytes.Length)
    {
      var read = stream.Read(bytes, count, bytes.Length - count);
      if (read == 0) throw new EndOfStreamException("The saved result ended while reading its preview.");
      count += read;
    }

    var text = new StringBuilder(charLimit);
    var position = 0;
    while (position < bytes.Length && text.Length < charLimit)
    {
      var status = Rune.DecodeFromUtf8(bytes.AsSpan(position), out var rune, out var consumed);
      if (status == OperationStatus.NeedMoreData && count < stream.Length) break;
      if (status != OperationStatus.Done)
        throw new DecoderFallbackException("The saved result is not valid UTF-8.");
      if (text.Length + rune.Utf16SequenceLength > charLimit) break;
      text.Append(rune.ToString());
      position += consumed;
    }
    return text.ToString();
  }

  private static string Describe(Exception exception)
  {
    var message = exception.Message;
    return message.Length <= 512 ? message : message[..512];
  }
}
