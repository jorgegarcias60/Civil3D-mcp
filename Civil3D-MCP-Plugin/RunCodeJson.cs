using System.Buffers;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Civil3DMcpPlugin;

/// <summary>
/// Writes a runCode script's return value as JSON (System.Text.Json, written
/// straight to a stream). AutoCAD values get compact forms: points and vectors as
/// [x, y(, z)], ObjectIds and Handles as handle strings, Extents3d as
/// {min, max}, DBObjects as {type, handle}; tuples become arrays; the script's
/// own objects (anonymous types, records, classes) their public properties
/// and fields. Other Autodesk objects are written as ToString(), never walked,
/// because their object graphs reach the whole database. Runs on the main
/// thread, while the run's transactions are still open.
/// </summary>
internal static partial class RunCodeJson
{
  private static readonly JsonSerializerOptions Options = CreateOptions();

  private static JsonSerializerOptions CreateOptions()
  {
    var options = new JsonSerializerOptions
    {
      IncludeFields = true,
      ReferenceHandler = ReferenceHandler.IgnoreCycles,
      // JSON has no NaN or infinity; write them as strings instead of failing.
      NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
      MaxDepth = 32,
      Converters = { new TupleConverterFactory(), new JsonStringEnumConverter() },
    };
    ConfigureHostValues(options);
    return options;
  }

  // Kept separate so the offline harness exercises this serializer and its
  // tuple converter without loading AutoCAD's native dependencies.
  static partial void ConfigureHostValues(JsonSerializerOptions options);

  internal static string Serialize(Stream stream, object? value)
  {
    var shape = Describe(value);
    // A copy per run: the options cache metadata for every type they write,
    // which for the script's own (anonymous) types would keep each run's
    // collectible assembly loaded.
    // Stream serialization buffers a custom converter's entire Write call,
    // including large tuple members. Forward writer chunks ourselves instead.
    var options = new JsonSerializerOptions(Options);
    using var buffer = new StreamBufferWriter(stream);
    using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
    {
      MaxDepth = options.MaxDepth,
      Encoder = options.Encoder,
      Indented = options.WriteIndented,
    });
    JsonSerializer.Serialize(writer, value, value?.GetType() ?? typeof(object), options);
    writer.Flush();
    return shape;
  }

  // A one-line description of the result for previews: its type and size.
  private static string Describe(object? value)
  {
    return value switch
    {
      null => "null",
      string text => $"string ({text.Length} chars)",
      IDictionary dictionary => $"object with {dictionary.Count} keys",
      ICollection collection => $"array of {collection.Count}",
      _ => value.GetType().Name,
    };
  }

  // Utf8JsonWriter asks for another buffer when the current chunk fills. Only
  // a single token can require a larger buffer; tuple/enumerable size cannot.
  private sealed class StreamBufferWriter(Stream stream) : IBufferWriter<byte>, IDisposable
  {
    private const int ChunkSize = 16 * 1024;
    private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);

    public void Advance(int count)
    {
      var buffer = GetBuffer(0);
      if ((uint)count > (uint)buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
      stream.Write(buffer.AsSpan(0, count));
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => GetBuffer(sizeHint);
    public Span<byte> GetSpan(int sizeHint = 0) => GetBuffer(sizeHint);

    private byte[] GetBuffer(int sizeHint)
    {
      ObjectDisposedException.ThrowIf(_buffer == null, this);
      ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
      if (sizeHint > _buffer.Length)
      {
        var larger = ArrayPool<byte>.Shared.Rent(sizeHint);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = larger;
      }
      return _buffer;
    }

    public void Dispose()
    {
      if (_buffer == null) return;
      ArrayPool<byte>.Shared.Return(_buffer);
      _buffer = null;
    }
  }

  private sealed class TupleConverterFactory : JsonConverterFactory
  {
    public override bool CanConvert(Type type) => typeof(ITuple).IsAssignableFrom(type);
    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
      (JsonConverter)Activator.CreateInstance(typeof(TupleConverter<>).MakeGenericType(type))!;
  }

  private abstract class WriteOnlyConverter<T> : JsonConverter<T>
  {
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      throw new NotSupportedException("runCode results are only written.");
  }

  private sealed class TupleConverter<T> : WriteOnlyConverter<T> where T : ITuple
  {
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
      writer.WriteStartArray();
      for (var index = 0; index < value.Length; index++)
      {
        var item = value[index];
        JsonSerializer.Serialize(writer, item, item?.GetType() ?? typeof(object), options);
      }

      writer.WriteEndArray();
    }
  }
}
