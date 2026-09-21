using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

public static class AlignmentCommands
{
  public static Task<object?> ListAlignmentsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignments = new List<Dictionary<string, object?>>();
      foreach (ObjectId objectId in civilDoc.GetAlignmentIds())
      {
        var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, objectId, OpenMode.ForRead);
        alignments.Add(ToAlignmentSummary(alignment));
      }

      return new Dictionary<string, object?>
      {
        ["alignments"] = alignments,
      };
    });
  }

  public static Task<object?> GetAlignmentAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      var entities = new List<Dictionary<string, object?>>();
      var entityCollection = CivilObjectUtils.GetPropertyValue<object>(alignment, "Entities");
      if (entityCollection is System.Collections.IEnumerable enumerable)
      {
        var index = 0;
        foreach (var entity in enumerable)
        {
          entities.Add(new Dictionary<string, object?>
          {
            ["index"] = index++,
            ["type"] = MapAlignmentEntityType(CivilObjectUtils.GetStringProperty(entity, "EntityType") ?? entity?.GetType().Name),
            ["startStation"] = CivilObjectUtils.GetPropertyValue<double?>(entity, "StartStation") ?? 0,
            ["endStation"] = CivilObjectUtils.GetPropertyValue<double?>(entity, "EndStation") ?? 0,
            ["length"] = CivilObjectUtils.GetPropertyValue<double?>(entity, "Length") ?? 0,
          });
        }
      }

      var dependentProfiles = alignment.GetProfileIds()
        .Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetRequiredObject<Profile>(transaction, id, OpenMode.ForRead).Name)
        .ToList();

      return new Dictionary<string, object?>
      {
        ["name"] = alignment.Name,
        ["handle"] = CivilObjectUtils.GetHandle(alignment),
        ["type"] = MapAlignmentType(CivilObjectUtils.GetStringProperty(alignment, "AlignmentType") ?? alignment.GetType().Name),
        ["style"] = CivilObjectUtils.GetName(transaction.GetObject(alignment.StyleId, OpenMode.ForRead)) ?? string.Empty,
        ["layer"] = alignment.Layer,
        ["length"] = alignment.Length,
        ["startStation"] = alignment.StartingStation,
        ["endStation"] = alignment.EndingStation,
        ["entityCount"] = entities.Count,
        ["entities"] = entities,
        ["dependentProfiles"] = dependentProfiles,
        ["dependentCorridors"] = new List<string>(),
        ["isReference"] = CivilObjectUtils.GetPropertyValue<bool?>(alignment, "IsReferenceObject") ?? false,
      };
    });
  }

  public static Task<object?> StationToPointAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");
    var offset = PluginRuntime.GetOptionalDouble(parameters, "offset") ?? 0;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      double x = 0;
      double y = 0;
      alignment.PointLocation(station, offset, ref x, ref y);

      return new Dictionary<string, object?>
      {
        ["x"] = x,
        ["y"] = y,
        ["station"] = station,
        ["offset"] = offset,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  public static Task<object?> SampleStationsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var offset = PluginRuntime.GetOptionalDouble(parameters, "offset") ?? 0;
    var stationsNode = PluginRuntime.GetParameter(parameters, "stations") as JsonArray;
    if (stationsNode == null || stationsNode.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "alignmentSampleStations requires at least one station.");
    }
    if (stationsNode.Count > 200)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "alignmentSampleStations accepts at most 200 stations.");
    }

    var stations = stationsNode.Select(node =>
      node?.GetValue<double>()
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Every station must be numeric."))
      .ToArray();

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      var units = CivilObjectUtils.LinearUnits(database);
      var samples = new List<Dictionary<string, object?>>(stations.Length);

      foreach (var station in stations)
      {
        double x = 0;
        double y = 0;
        alignment.PointLocation(station, offset, ref x, ref y);
        samples.Add(new Dictionary<string, object?>
        {
          ["x"] = x,
          ["y"] = y,
          ["station"] = station,
          ["offset"] = offset,
          ["units"] = units,
        });
      }

      return new Dictionary<string, object?> { ["samples"] = samples };
    });
  }

  public static Task<object?> PointToStationAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      double station = 0;
      double offset = 0;
      alignment.StationOffset(x, y, ref station, ref offset);

      return new Dictionary<string, object?>
      {
        ["station"] = station,
        ["offset"] = offset,
        ["distanceFromAlignment"] = Math.Abs(offset),
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  public static Task<object?> CreateAlignmentAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");

    // Either build from a points array, or wrap an existing polyline the user
    // already drew. When a polyline is named, it is NEVER erased -- that is
    // their drawing, not scratch geometry.
    var polylineHandle = PluginRuntime.GetOptionalString(parameters, "polylineHandle");
    var pointsNode = PluginRuntime.GetParameter(parameters, "points") as JsonArray;

    if (string.IsNullOrWhiteSpace(polylineHandle) && (pointsNode == null || pointsNode.Count < 2))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "createAlignment requires either 'polylineHandle' or at least two 'points'.");
    }

    // curveRadius: insert a curve of exactly this radius at every interior PI.
    // addCurves: let Civil 3D fit curves using the drawing's default radius.
    //   Defaults to true for 'points' (the original behavior) and false for
    //   'polylineHandle', where the alignment should trace the polyline exactly.
    // Neither curves option: straight tangents that follow the source geometry.
    var curveRadius = PluginRuntime.GetOptionalDouble(parameters, "curveRadius");
    var addCurves = PluginRuntime.GetOptionalBool(parameters, "addCurves") ?? string.IsNullOrWhiteSpace(polylineHandle);

    if (curveRadius is <= 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "'curveRadius' must be greater than zero.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      ObjectId polylineId;
      bool eraseSource;

      if (!string.IsNullOrWhiteSpace(polylineHandle))
      {
        // Use the polyline the user drew. Keep it.
        ObjectId existingId;
        try
        {
          var handle = new Handle(Convert.ToInt64(polylineHandle, 16));
          existingId = database.GetObjectId(false, handle, 0);
        }
        catch (Exception ex)
        {
          throw new JsonRpcDispatchException(
            "CIVIL3D.OBJECT_NOT_FOUND", $"Could not resolve polyline handle '{polylineHandle}': {ex.Message}");
        }

        if (transaction.GetObject(existingId, OpenMode.ForRead) is not Polyline)
        {
          throw new JsonRpcDispatchException(
            "CIVIL3D.INVALID_INPUT", $"Handle '{polylineHandle}' is not a polyline.");
        }

        polylineId = existingId;
        eraseSource = false;
      }
      else
      {
        using var polyline = new Polyline();
        for (var index = 0; index < pointsNode!.Count; index++)
        {
          if (pointsNode[index] is not JsonObject point)
          {
            continue;
          }

          polyline.AddVertexAt(index, new Point2d(point["x"]!.GetValue<double>(), point["y"]!.GetValue<double>()), 0, 0, 0);
        }

        polylineId = modelSpace.AppendEntity(polyline);
        transaction.AddNewlyCreatedDBObject(polyline, true);
        eraseSource = true;   // scratch geometry we made; clean it up
      }

      // When an explicit radius is requested, create straight tangents first and
      // insert the curves afterwards -- PolylineOptions has no radius field, so
      // AddCurvesBetweenTangents would silently use the drawing default instead.
      var polylineOptions = new PolylineOptions
      {
        AddCurvesBetweenTangents = curveRadius == null && addCurves,
        EraseExistingEntities = eraseSource,
        PlineId = polylineId,
      };

      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer"));
      var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var labelSetId = LookupUtils.GetAlignmentLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));
      var siteId = LookupUtils.GetSiteId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "site"));

      var alignmentId = Alignment.Create(civilDoc, polylineOptions, name, siteId, layerId, styleId, labelSetId);

      // Insert a curve of the requested radius at every interior PI. The
      // alignment was created as pure tangents above, so consecutive entity
      // pairs are the tangents meeting at each PI.
      var curvesAdded = 0;
      var curveFailures = new List<string>();
      if (curveRadius != null)
      {
        var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignmentId, OpenMode.ForWrite);
        var tangentIds = new List<int>();
        foreach (AlignmentEntity entity in writeAlignment.Entities)
        {
          tangentIds.Add(entity.EntityId);
        }

        for (var index = 0; index < tangentIds.Count - 1; index++)
        {
          try
          {
            writeAlignment.Entities.AddFreeCurve(
              tangentIds[index],
              tangentIds[index + 1],
              curveRadius.Value,
              CurveParamType.Radius,
              false,
              // CurveType only distinguishes Compound vs Reverse, which matters
              // when the neighbours are curves. Between two tangents it is inert.
              CurveType.Compound);
            curvesAdded++;
          }
          catch (Exception ex)
          {
            // A radius that will not fit between two short tangents is a real
            // design constraint, not a crash. Report it and keep the corner sharp.
            curveFailures.Add($"PI {index + 1}: {ex.Message}");
          }
        }
      }

      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignmentId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["name"] = alignment.Name,
        ["handle"] = CivilObjectUtils.GetHandle(alignment),
        ["length"] = alignment.Length,
        ["sourcePolyline"] = string.IsNullOrWhiteSpace(polylineHandle) ? null : polylineHandle,
        ["sourcePolylineErased"] = eraseSource,
        ["curveRadius"] = curveRadius,
        ["curvesAdded"] = curvesAdded,
        ["curveFailures"] = curveFailures,
        ["created"] = true,
      };
    });
  }

  public static Task<object?> DeleteAlignmentAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignment.ObjectId, OpenMode.ForWrite);
      writeAlignment.Erase();

      return new Dictionary<string, object?>
      {
        ["name"] = name,
        ["deleted"] = true,
      };
    });
  }

  private static Dictionary<string, object?> ToAlignmentSummary(Alignment alignment)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = alignment.Name,
      ["handle"] = CivilObjectUtils.GetHandle(alignment),
      ["type"] = MapAlignmentType(CivilObjectUtils.GetStringProperty(alignment, "AlignmentType") ?? alignment.GetType().Name),
      ["length"] = alignment.Length,
      ["startStation"] = alignment.StartingStation,
      ["endStation"] = alignment.EndingStation,
      ["site"] = CivilObjectUtils.GetStringProperty(alignment, "SiteName"),
      ["profileCount"] = alignment.GetProfileIds().Count,
      ["isReference"] = CivilObjectUtils.GetPropertyValue<bool?>(alignment, "IsReferenceObject") ?? false,
    };
  }

  private static string MapAlignmentType(string? value)
  {
    var text = value?.ToLowerInvariant() ?? string.Empty;
    if (text.Contains("offset"))
    {
      return "offset";
    }

    if (text.Contains("rail"))
    {
      return "rail";
    }

    if (text.Contains("curb"))
    {
      return "curb_return";
    }

    return "centerline";
  }

  private static string MapAlignmentEntityType(string? value)
  {
    var text = value?.ToLowerInvariant() ?? string.Empty;
    if (text.Contains("spiral"))
    {
      return "spiral";
    }

    if (text.Contains("curve") || text.Contains("arc"))
    {
      return "arc";
    }

    return "line";
  }
}
