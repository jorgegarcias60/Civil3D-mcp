using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Civil3DMcpPlugin;

internal static class ResultChecks
{
  internal static void Run(Action<bool, string> expect)
  {
    var root = Path.Combine(Path.GetTempPath(), "RunCodeResult-" + Guid.NewGuid().ToString("N"));
    var oldLogPath = PluginLog.LogFilePath;
    try
    {
      PluginLog.LogFilePath = Path.Combine(root, "plugin.log");
      Directory.CreateDirectory(root);
      var smallFolder = RunCodeLog.CreateRunFolder("small");
      var small = RunCodeResult.Save(smallFolder, stream => RunCodeJson.Serialize(stream, new Dictionary<string, string> { ["name"] = "small" }));
      var smallResponse = new JsonObject();
      small.AddTo(smallResponse, (int)small.Bytes, 20);
      expect(small.FilePath != null && small.Bytes == new FileInfo(small.FilePath).Length
        && smallResponse["result"]?["name"]?.GetValue<string>() == "small"
        && smallResponse["resultShape"]?.GetValue<string>() == "object with 1 keys"
        && smallResponse["resultBytes"]?.GetValue<long>() == small.Bytes
        && smallResponse["resultFile"] == null && File.Exists(small.FilePath),
        "a result at the exact inline threshold is parsed and its completed file is retained");
      var oneByteBelow = new JsonObject();
      small.AddTo(oneByteBelow, (int)small.Bytes - 1, 20);
      expect(oneByteBelow["result"] == null && oneByteBelow["resultFile"]?.GetValue<string>() == small.FilePath
        && oneByteBelow["resultBytes"]?.GetValue<long>() == small.Bytes,
        "a result one byte over the inline threshold is returned by file path");

      var nullFolder = RunCodeLog.CreateRunFolder("null");
      var nullResult = RunCodeResult.Save(nullFolder, stream => RunCodeJson.Serialize(stream, null));
      var nullResponse = new JsonObject();
      nullResult.AddTo(nullResponse, 1024, 20);
      expect(nullResult.Bytes == 4 && nullResponse.ContainsKey("result") && nullResponse["result"] == null
        && nullResponse["resultError"] == null && File.Exists(nullResult.FilePath),
        "JSON null remains a completed inline result");

      var largeFolder = RunCodeLog.CreateRunFolder("large");
      var moves = 0;
      IEnumerable<string> LazyValues()
      {
        for (var index = 0; index < 20000; index++)
        {
          moves++;
          yield return index == 0 ? "\U0001F600" : new string('x', 20);
        }
      }
      var large = RunCodeResult.Save(largeFolder, stream => RunCodeJson.Serialize(stream, LazyValues()));
      var largeResponse = new JsonObject();
      large.AddTo(largeResponse, 1024, 20);
      var preview = largeResponse["resultPreview"]?.GetValue<string>();
      expect(moves == 20000 && large.Bytes > 1024 && largeResponse["result"] == null
        && largeResponse["resultFile"]?.GetValue<string>() == large.FilePath
        && preview?.Length <= 23,
        "a large lazy result streams to disk and has a bounded preview");

      var tupleFolder = RunCodeLog.CreateRunFolder("small-tuple");
      var tuple = RunCodeResult.Save(tupleFolder, stream => RunCodeJson.Serialize(stream, (2, new[] { 1, 2 })));
      var tupleResponse = new JsonObject();
      tuple.AddTo(tupleResponse, 1024, 20);
      expect(tupleResponse["result"]?.ToJsonString() == "[2,[1,2]]"
        && tupleResponse["resultShape"]?.GetValue<string>() == "ValueTuple`2",
        "the production serializer keeps small tuple results inline as arrays");

      var tokenFolder = RunCodeLog.CreateRunFolder("large-token");
      var token = RunCodeResult.Save(tokenFolder, stream => RunCodeJson.Serialize(stream, (new string('x', 100000), 1)));
      using (var parsed = JsonDocument.Parse(File.ReadAllText(token.FilePath!)))
      {
        expect(parsed.RootElement[0].GetString()?.Length == 100000 && parsed.RootElement[1].GetInt32() == 1,
          "a single tuple string larger than a writer chunk is saved completely");
      }

      var optionsFolder = RunCodeLog.CreateRunFolder("serializer-options");
      var cyclic = new ResultValue();
      cyclic.Child = cyclic;
      var optionsResult = RunCodeResult.Save(optionsFolder, stream => RunCodeJson.Serialize(stream, Tuple.Create(cyclic)));
      var optionsResponse = new JsonObject();
      optionsResult.AddTo(optionsResponse, 1024, 20);
      expect(optionsResponse["result"]?[0]?["State"]?.GetValue<string>() == "Ready"
        && optionsResponse["result"]?[0]?["Number"]?.GetValue<string>() == "NaN"
        && optionsResponse["result"]?[0]?["Child"] == null && optionsResponse["resultError"] == null,
        "streamed tuple members keep enum strings, public fields, named floats and cycle handling");

      foreach (var (name, wrap) in new (string, Func<IEnumerable<int>, object>)[]
      {
        ("tuple", items => (90001, items)),
        ("reference-tuple", items => Tuple.Create(90001, items)),
        ("nested-tuple", items => (1, (90001, items))),
        ("object-tuple", items => new { items = (90001, items) }),
        ("array-tuple", items => new object[] { (90001, items) }),
      })
      {
        var folder = RunCodeLog.CreateRunFolder(name);
        long bytesDuringEnumeration = 0;
        IEnumerable<int> TupleValues(Stream stream)
        {
          for (var index = 0; index <= 90000; index++)
          {
            if (index == 90000) bytesDuringEnumeration = stream.Position;
            yield return index;
          }
        }
        var saved = RunCodeResult.Save(folder, stream => RunCodeJson.Serialize(stream, wrap(TupleValues(stream))));
        var response = new JsonObject();
        saved.AddTo(response, 1024, 20);
        expect(bytesDuringEnumeration > 0 && saved.Bytes > bytesDuringEnumeration
          && saved.Error == null && response["result"] == null
          && response["resultFile"]?.GetValue<string>() == saved.FilePath
          && response["resultPreview"]?.GetValue<string>().Length <= 23,
          $"the production serializer streams {name} members before enumeration completes and spills with a preview");
        using var parsed = JsonDocument.Parse(File.ReadAllText(saved.FilePath!));
        var array = name switch
        {
          "object-tuple" => parsed.RootElement.GetProperty("items")[1],
          "array-tuple" => parsed.RootElement[0][1],
          "nested-tuple" => parsed.RootElement[1][1],
          _ => parsed.RootElement[1],
        };
        expect(array.GetArrayLength() == 90001 && array[90000].GetInt32() == 90000,
          $"the streamed {name} JSON is complete and preserves its array shape");
      }
      var unicodeFolder = RunCodeLog.CreateRunFolder("unicode");
      var unicode = RunCodeResult.Save(unicodeFolder, stream =>
      {
        var json = "[\"\U0001F600" + new string('x', 2000) + "\"]";
        var bytes = Encoding.UTF8.GetBytes(json);
        stream.Write(bytes);
        return "array";
      });
      var unicodeResponse = new JsonObject();
      unicode.AddTo(unicodeResponse, 1024, 3);
      expect(unicodeResponse["resultPreview"]?.GetValue<string>() == "[\"..."
        && unicodeResponse["resultFile"]?.GetValue<string>() == unicode.FilePath,
        "a UTF-8 preview stops before a surrogate pair that cannot fit the character limit");
      var copiedFolder = Path.Combine(root, "copied");
      var copied = RunCodeLog.CopyOutputs(largeFolder, copiedFolder, removeCopied: false);
      expect(copied.Copied.Contains(Path.Combine(copiedFolder, RunCodeLog.ResultFile)) && File.Exists(large.FilePath),
        "a completed large result can be copied while its original remains");
      var copiedSmall = RunCodeLog.CopyOutputs(smallFolder, copiedFolder, removeCopied: false);
      expect(copiedSmall.Copied.Contains(Path.Combine(copiedFolder, RunCodeLog.ResultFile)) && File.Exists(small.FilePath),
        "a completed small inline result can also be copied");

      var failedFolder = RunCodeLog.CreateRunFolder("failed");
      long writtenBeforeFailure = 0;
      IEnumerable<int> FailsLate(Stream stream)
      {
        for (var index = 0; index < 100000; index++)
        {
          if (index == 90000)
          {
            writtenBeforeFailure = stream.Position;
            throw new InvalidOperationException("enumeration failed");
          }
          yield return index;
        }
      }
      var failed = RunCodeResult.Save(failedFolder, stream => RunCodeJson.Serialize(stream, FailsLate(stream)));
      var failedResponse = new JsonObject();
      failed.AddTo(failedResponse, 1024, 20);
      expect(writtenBeforeFailure > 0 && failed.FilePath == null && failed.Error?.Contains("enumeration failed") == true
        && !File.Exists(RunCodeLog.ResultPath(failedFolder)) && failedResponse["resultFile"] == null
        && failedResponse["resultError"] != null,
        "a mid-stream serializer exception removes the partial file and reports only resultError");

      var failedTupleFolder = RunCodeLog.CreateRunFolder("failed-tuple");
      writtenBeforeFailure = 0;
      var failedTuple = RunCodeResult.Save(failedTupleFolder, stream => RunCodeJson.Serialize(stream, (90000, FailsLate(stream))));
      var failedTupleResponse = new JsonObject();
      failedTuple.AddTo(failedTupleResponse, 1024, 20);
      expect(writtenBeforeFailure > 0 && failedTuple.FilePath == null
        && failedTuple.Error?.Contains("enumeration failed") == true
        && !File.Exists(RunCodeLog.ResultPath(failedTupleFolder))
        && failedTupleResponse["resultError"] != null && failedTupleResponse["resultFile"] == null
        && failedTupleResponse["resultPreview"] == null && failedTupleResponse["resultBytes"] == null,
        "a production tuple serialization error after streamed bytes reports only resultError and removes the partial file");
      var writeFolder = RunCodeLog.CreateRunFolder("write-failure");
      ThrowAfterStream? forwarding = null;
      var writeFailure = RunCodeResult.Save(writeFolder, stream =>
      {
        forwarding = new ThrowAfterStream(stream, 1024);
        return RunCodeJson.Serialize(forwarding, Enumerable.Range(0, 10000));
      });
      expect(forwarding?.Forwarded == 1024 && writeFailure.FilePath == null && writeFailure.Error != null
        && !File.Exists(RunCodeLog.ResultPath(writeFolder)),
        "an actual FileStream write error after partial output is returned as resultError and cleaned up");

      if (OperatingSystem.IsWindows())
      {
        var stickyFolder = RunCodeLog.CreateRunFolder("sticky");
        var stickyPath = RunCodeLog.ResultPath(stickyFolder);
        try
        {
          var sticky = RunCodeResult.Save(stickyFolder, stream =>
          {
            RunCodeJson.Serialize(stream, new string('x', 10000));
            File.SetAttributes(stickyPath, FileAttributes.ReadOnly);
            throw new InvalidOperationException("late failure");
          });
          var stickyResponse = new JsonObject();
          sticky.AddTo(stickyResponse, 1024, 20);
          var stickyCopy = RunCodeLog.CopyOutputs(stickyFolder, Path.Combine(root, "sticky-copy"), false, includeResult: sticky.FilePath != null);
          expect(sticky.FilePath == null && sticky.Error?.Contains("late failure") == true
            && File.Exists(stickyPath) && stickyResponse["resultFile"] == null
            && stickyCopy.Copied.Count == 0 && !Directory.Exists(Path.Combine(root, "sticky-copy")),
            "a partial file left by failed cleanup is never published or copied");
        }
        finally
        {
          if (File.Exists(stickyPath)) File.SetAttributes(stickyPath, FileAttributes.Normal);
        }
      }

      var rejectedFolder = RunCodeLog.CreateRunFolder("rejected");
      Directory.CreateDirectory(RunCodeLog.ResultPath(rejectedFolder));
      var rejected = RunCodeResult.Save(rejectedFolder, stream => RunCodeJson.Serialize(stream, 1));
      expect(rejected.FilePath == null && rejected.Error != null
        && Directory.Exists(RunCodeLog.ResultPath(rejectedFolder)),
        "a result file write failure is reported without deleting a pre-existing path");

      var flushFolder = RunCodeLog.CreateRunFolder("flush");
      var flushFailure = RunCodeResult.Save(flushFolder, stream =>
      {
        RunCodeJson.Serialize(stream, 42);
        stream.Dispose();
        return "number";
      });
      expect(flushFailure.FilePath == null && flushFailure.Error != null
        && !File.Exists(RunCodeLog.ResultPath(flushFolder)),
        "a flush failure never publishes the result and removes its partial file");

      var skippedCopy = RunCodeLog.CopyOutputs(failedFolder, Path.Combine(root, "failed-copy"), false, includeResult: false);
      expect(skippedCopy.Copied.Count == 0 && !Directory.Exists(Path.Combine(root, "failed-copy")),
        "a failed result has no completed file to copy");
      File.WriteAllText(RunCodeLog.ResultPath(failedFolder), "partial");
      skippedCopy = RunCodeLog.CopyOutputs(failedFolder, Path.Combine(root, "failed-copy"), false, includeResult: false);
      expect(skippedCopy.Copied.Count == 0 && !Directory.Exists(Path.Combine(root, "failed-copy")),
        "copy explicitly excludes a leftover partial result");

      using (var locked = new FileStream(small.FilePath!, FileMode.Open, FileAccess.Read, FileShare.None))
      {
        var response = new JsonObject();
        small.AddTo(response, 1024, 20);
        expect(response["resultError"] != null && response["result"] == null,
          "a result read failure is returned as resultError");
      }
      using (var locked = new FileStream(large.FilePath!, FileMode.Open, FileAccess.Read, FileShare.None))
      {
        var response = new JsonObject();
        large.AddTo(response, 1024, 20);
        expect(response["resultError"] != null && response["resultPreview"] == null
          && response["resultFile"] == null,
          "a large result preview read failure is returned as resultError");
      }
      File.WriteAllText(small.FilePath!, new string('x', (int)small.Bytes), new UTF8Encoding(false));
      var malformed = new JsonObject();
      small.AddTo(malformed, 1024, 20);
      expect(malformed["resultError"] != null && malformed["result"] == null,
        "malformed saved JSON is returned as resultError");

      var outside = RunCodeResult.Save(root, stream => RunCodeJson.Serialize(stream, 1));
      expect(outside.FilePath == null && outside.Error?.Contains("inside") == true
        && !File.Exists(Path.Combine(root, RunCodeLog.ResultFile)),
        "a result cannot be written outside the run-code root");
      var outsideFile = Path.Combine(root, "outside.json");
      File.WriteAllText(outsideFile, "42");
      var outsideResponse = new JsonObject();
      new RunCodeResult(outsideFile, 2, "number", null).AddTo(outsideResponse, 1024, 20);
      expect(outsideResponse["resultError"] != null && outsideResponse["result"] == null,
        "result presentation refuses a file outside the run-code root");
    }
    finally
    {
      PluginLog.LogFilePath = oldLogPath;
      if (Directory.Exists(root)) Directory.Delete(root, true);
    }
  }

  private enum ResultState { Ready }

  private sealed class ResultValue
  {
    public ResultState State = ResultState.Ready;
    public double Number = double.NaN;
    public ResultValue? Child { get; set; }
  }

  private sealed class ThrowAfterStream(Stream target, int limit) : Stream
  {
    internal int Forwarded { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => target.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
      var allowed = Math.Min(buffer.Length, limit - Forwarded);
      if (allowed > 0)
      {
        target.Write(buffer[..allowed]);
        Forwarded += allowed;
      }
      if (allowed < buffer.Length || Forwarded == limit) throw new IOException("simulated mid-stream write failure");
    }
  }
}
