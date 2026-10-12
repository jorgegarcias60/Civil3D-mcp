using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

internal static partial class RunCodeJson
{
  static partial void ConfigureHostValues(JsonSerializerOptions options) =>
    options.Converters.Insert(1, new HostValueConverterFactory());

  private sealed class HostValueConverterFactory : JsonConverterFactory
  {
    private static readonly Dictionary<Type, JsonConverter> Fixed = new()
    {
      [typeof(Point3d)] = new DelegateConverter<Point3d>((writer, point) => WriteNumbers(writer, point.X, point.Y, point.Z)),
      [typeof(Point2d)] = new DelegateConverter<Point2d>((writer, point) => WriteNumbers(writer, point.X, point.Y)),
      [typeof(Vector3d)] = new DelegateConverter<Vector3d>((writer, vector) => WriteNumbers(writer, vector.X, vector.Y, vector.Z)),
      [typeof(Vector2d)] = new DelegateConverter<Vector2d>((writer, vector) => WriteNumbers(writer, vector.X, vector.Y)),
      // Also dictionary keys (Dictionary<ObjectId, ...>): the handle text.
      [typeof(ObjectId)] = new DelegateConverter<ObjectId>(
        (writer, id) =>
        {
          if (id.IsNull) writer.WriteNullValue(); else writer.WriteStringValue(id.Handle.ToString());
        },
        id => id.IsNull ? "null" : id.Handle.ToString()),
      [typeof(Handle)] = new DelegateConverter<Handle>((writer, handle) => writer.WriteStringValue(handle.ToString()), handle => handle.ToString()),
      [typeof(Extents3d)] = new DelegateConverter<Extents3d>((writer, extents) =>
      {
        writer.WriteStartObject();
        writer.WritePropertyName("min");
        WriteNumbers(writer, extents.MinPoint.X, extents.MinPoint.Y, extents.MinPoint.Z);
        writer.WritePropertyName("max");
        WriteNumbers(writer, extents.MaxPoint.X, extents.MaxPoint.Y, extents.MaxPoint.Z);
        writer.WriteEndObject();
      }),
    };

    public override bool CanConvert(Type type)
    {
      if (Fixed.ContainsKey(type) || typeof(DBObject).IsAssignableFrom(type))
      {
        return true;
      }

      // Other Autodesk values: as text, except collections (ObjectIdCollection,
      // Point3dCollection...), which are written element by element.
      return type.Namespace?.StartsWith("Autodesk", StringComparison.Ordinal) == true && !typeof(IEnumerable).IsAssignableFrom(type);
    }

    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
    {
      if (Fixed.TryGetValue(type, out var converter))
      {
        return converter;
      }

      var generic = typeof(DBObject).IsAssignableFrom(type) ? typeof(DbObjectConverter<>) : typeof(TextConverter<>);
      return (JsonConverter)Activator.CreateInstance(generic.MakeGenericType(type))!;
    }
  }

  private static void WriteNumbers(Utf8JsonWriter writer, params double[] numbers)
  {
    writer.WriteStartArray();
    foreach (var number in numbers)
    {
      if (double.IsFinite(number)) writer.WriteNumberValue(number); else writer.WriteNullValue();
    }

    writer.WriteEndArray();
  }

  private sealed class DelegateConverter<T> : WriteOnlyConverter<T>
  {
    private readonly Action<Utf8JsonWriter, T> _write;
    private readonly Func<T, string>? _key;

    public DelegateConverter(Action<Utf8JsonWriter, T> write, Func<T, string>? key = null)
    {
      _write = write;
      _key = key;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => _write(writer, value);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
      if (_key == null)
      {
        throw new NotSupportedException($"{typeof(T).Name} cannot be a dictionary key in a runCode result; use string keys.");
      }

      writer.WritePropertyName(_key(value));
    }
  }

  private sealed class DbObjectConverter<T> : WriteOnlyConverter<T> where T : DBObject
  {
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
      writer.WriteStartObject();
      writer.WriteString("type", value.GetType().Name);
      writer.WriteString("handle", value.Handle.ToString());
      writer.WriteEndObject();
    }
  }

  private sealed class TextConverter<T> : WriteOnlyConverter<T>
  {
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
      writer.WriteStringValue(value?.ToString());
  }
}
