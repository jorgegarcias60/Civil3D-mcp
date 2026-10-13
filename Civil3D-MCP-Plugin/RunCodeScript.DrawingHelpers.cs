using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.DatabaseServices.Filters;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

public abstract partial class RunCodeScript
{
  public sealed record XrefResult(ObjectId DefinitionId, ObjectId ReferenceId, double Scale,
    string SourceUnits, string HostUnits, string PathName);

  /// <summary>Four oriented text corners in reading order: lower left, lower right, upper right, upper left.</summary>
  public Point3d[] TextBox(Entity entity, Matrix3d? toWcs = null)
  {
    Check();
    if (entity == null) throw new ArgumentNullException(nameof(entity));
    Point3d[] corners;
    if (entity is AttributeReference { IsMTextAttribute: true } attribute)
    {
      using var mtextAttribute = attribute.MTextAttribute;
      corners = TextBox(mtextAttribute);
    }
    else if (entity is MText mtext)
    {
      var points = mtext.GetBoundingPoints();
      if (points.Count != 4) throw new InvalidOperationException("MText has no four-corner bounding box.");
      var right = mtext.Direction.GetNormal();
      var up = mtext.Normal.CrossProduct(right).GetNormal();
      corners = OrderCorners(points.Cast<Point3d>().ToArray(), right, up);
    }
    else if (entity is DBText text)
    {
      using var copy = (DBText)text.Clone();
      var oldWorking = HostApplicationServices.WorkingDatabase;
      try
      {
        var textDatabase = text.Database ?? oldWorking;
        if (textDatabase != null)
        {
          HostApplicationServices.WorkingDatabase = textDatabase;
          copy.AdjustAlignment(textDatabase);
        }
        var plane = Matrix3d.PlaneToWorld(copy.Normal);
        var flatten = plane.Inverse() * Matrix3d.Rotation(-copy.Rotation, copy.Normal, copy.Position);
        copy.TransformBy(flatten);
        var box = copy.GeometricExtents;
        var z = box.MinPoint.Z;
        var local = new[] {
          new Point3d(box.MinPoint.X, box.MinPoint.Y, z), new Point3d(box.MaxPoint.X, box.MinPoint.Y, z),
          new Point3d(box.MaxPoint.X, box.MaxPoint.Y, z), new Point3d(box.MinPoint.X, box.MaxPoint.Y, z) };
        corners = local.Select(point => point.TransformBy(flatten.Inverse())).ToArray();
        if (text.IsMirroredInX) corners = new[] { corners[1], corners[0], corners[3], corners[2] };
        if (text.IsMirroredInY) corners = new[] { corners[3], corners[2], corners[1], corners[0] };
      }
      finally { HostApplicationServices.WorkingDatabase = oldWorking; }
    }
    else throw new ArgumentException("TextBox supports DBText, AttributeReference, MText and MTextAttribute only.", nameof(entity));
    if (corners.Any(p => !Finite(p)) || corners[0].DistanceTo(corners[1]) <= 1e-10 || corners[1].DistanceTo(corners[2]) <= 1e-10)
      throw new InvalidOperationException("TextBox has invalid or degenerate extents.");
    if (toWcs.HasValue) corners = corners.Select(point => point.TransformBy(toWcs.Value)).ToArray();
    if (corners.Any(p => !Finite(p)) || corners[0].DistanceTo(corners[1]) <= 1e-10 || corners[1].DistanceTo(corners[2]) <= 1e-10)
      throw new InvalidOperationException("TextBox WCS transform produced invalid or degenerate extents.");
    Check();
    return corners;
  }

  private static Point3d[] OrderCorners(Point3d[] points, Vector3d right, Vector3d up)
  {
    var origin = points[0];
    return points.OrderBy(p => (p - origin).DotProduct(up)).Take(2)
      .OrderBy(p => (p - origin).DotProduct(right))
      .Concat(points.OrderByDescending(p => (p - origin).DotProduct(up)).Take(2)
        .OrderByDescending(p => (p - origin).DotProduct(right))).ToArray();
  }

  /// <summary>Replaces a block reference's spatial clip from a WCS polygon.</summary>
  public ObjectId XClip(BlockReference blockRef, IReadOnlyList<Point3d> wcsPolygon)
  {
    RequireDrawingWrite(nameof(XClip));
    if (blockRef == null || blockRef.ObjectId.IsNull || blockRef.IsErased || blockRef.Database != Db)
      throw new ArgumentException("XClip requires a live block reference in this drawing.", nameof(blockRef));
    if (wcsPolygon == null) throw new ArgumentNullException(nameof(wcsPolygon));
    var inverse = blockRef.BlockTransform.Inverse();
    var local = wcsPolygon.Select(p => { Check(); return p.TransformBy(inverse); }).ToArray();
    if (local.Length == 0 || local.Any(p => !Finite(p))) throw new ArgumentException("XClip points must be finite.", nameof(wcsPolygon));
    var elevation = local[0].Z;
    if (local.Any(p => Math.Abs(p.Z - elevation) > 1e-8))
      throw new ArgumentException("XClip points must lie in a plane parallel to the block's XY plane.", nameof(wcsPolygon));
    var vertices = RunCodeHelpers.ClipPolygon(local.Select(p => (p.X, p.Y)).ToArray(), Check);
    using var tx = TransactionFor(Db).TransactionManager.StartTransaction();
    var block = (BlockReference)tx.GetObject(blockRef.ObjectId, OpenMode.ForWrite);
    if (block.ExtensionDictionary.IsNull) block.CreateExtensionDictionary();
    var extension = (DBDictionary)tx.GetObject(block.ExtensionDictionary, OpenMode.ForWrite);
    DBDictionary filters;
    if (extension.Contains("ACAD_FILTER")) filters = (DBDictionary)tx.GetObject(extension.GetAt("ACAD_FILTER"), OpenMode.ForWrite);
    else
    {
      filters = new DBDictionary();
      extension.SetAt("ACAD_FILTER", filters); tx.AddNewlyCreatedDBObject(filters, true);
    }
    if (filters.Contains("SPATIAL"))
    {
      var oldId = filters.GetAt("SPATIAL");
      filters.Remove("SPATIAL");
      ((DBObject)tx.GetObject(oldId, OpenMode.ForWrite)).Erase();
    }
    var points = new Point2dCollection(vertices.Select(p => new Point2d(p.X, p.Y)).ToArray());
    var filter = new SpatialFilter { Definition = new SpatialFilterDefinition(points, Vector3d.ZAxis, 0, 0, 0, true) };
    var id = filters.SetAt("SPATIAL", filter);
    tx.AddNewlyCreatedDBObject(filter, true);
    Check(); tx.Commit(); return id;
  }

  /// <summary>Attaches a DWG xref and inserts one scaled model-space reference.</summary>
  public XrefResult AttachXref(string path, Point3d insertionPoint, double? scale = null,
    string? layer = null, bool relativePath = true, string? blockName = null)
  {
    RequireDrawingWrite(nameof(AttachXref));
    if (!Finite(insertionPoint)) throw new ArgumentException("Insertion point must be finite.", nameof(insertionPoint));
    var source = FileBoundary.ResolveImportPath(path, ".dwg");
    if (System.IO.Path.IsPathFullyQualified(Doc.Name) &&
        string.Equals(System.IO.Path.GetFullPath(Doc.Name), source, StringComparison.OrdinalIgnoreCase))
      throw new ArgumentException("A drawing cannot attach itself as an xref.", nameof(path));
    var name = blockName ?? System.IO.Path.GetFileNameWithoutExtension(source);
    if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Xref block name is required.", nameof(blockName));
    using var tx = TransactionFor(Db).TransactionManager.StartTransaction();
    Check();
    var blocks = (BlockTable)tx.GetObject(Db.BlockTableId, OpenMode.ForRead);
    if (blocks.Has(name)) throw new ArgumentException($"Block name '{name}' already exists; choose another xref blockName.", nameof(blockName));
    var definitionId = Db.AttachXref(source, name);
    var definition = (BlockTableRecord)tx.GetObject(definitionId, OpenMode.ForRead);
    var xrefDatabase = definition.GetXrefDatabase(false)
      ?? throw new InvalidOperationException($"Attached xref '{source}' has no available database for INSUNITS inspection.");
    var sourceUnits = xrefDatabase.Insunits;
    var hostUnits = Db.Insunits;
    var factor = RunCodeHelpers.XrefScale(scale, sourceUnits.ToString(), hostUnits.ToString(),
      () => UnitsConverter.GetConversionFactor(sourceUnits, hostUnits));
    var savedHost = System.IO.Path.IsPathFullyQualified(Doc.Name) &&
      string.Equals(System.IO.Path.GetFullPath(Doc.Name), System.IO.Path.GetFullPath(Db.Filename), StringComparison.OrdinalIgnoreCase);
    var pathName = RunCodeHelpers.XrefPathName(source, savedHost ? Db.Filename : string.Empty, relativePath);
    definition.UpgradeOpen(); definition.PathName = pathName;
    var layerId = ObjectId.Null;
    if (layer != null)
    {
      var layers = (LayerTable)tx.GetObject(Db.LayerTableId, OpenMode.ForRead);
      if (layers.Has(layer)) layerId = layers[layer];
      else
      {
        layers.UpgradeOpen();
        var record = new LayerTableRecord { Name = layer };
        layerId = layers.Add(record);
        tx.AddNewlyCreatedDBObject(record, true);
      }
    }
    var model = (BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(Db), OpenMode.ForWrite);
    using var reference = new BlockReference(insertionPoint, definitionId) { ScaleFactors = new Scale3d(factor) };
    if (!layerId.IsNull) reference.LayerId = layerId;
    var referenceId = model.AppendEntity(reference);
    tx.AddNewlyCreatedDBObject(reference, true);
    Check(); tx.Commit();
    return new XrefResult(definitionId, referenceId, factor, sourceUnits.ToString(), hostUnits.ToString(), pathName);
  }

  /// <summary>Edits an existing floating viewport in the named layout without changing its ObjectId.</summary>
  public ObjectId SetViewport(string layoutName, ObjectId viewportId, Point3d? paperCenter = null,
    double? paperWidth = null, double? paperHeight = null, Point3d? modelCenter = null,
    double? customScale = null, double? twistAngle = null, bool? locked = null)
  {
    RequireDrawingWrite(nameof(SetViewport));
    if (string.IsNullOrWhiteSpace(layoutName)) throw new ArgumentException("Layout name is required.", nameof(layoutName));
    if (viewportId.IsNull || viewportId.IsErased || viewportId.Database != Db)
      throw new ArgumentException("A live viewport in this drawing is required.", nameof(viewportId));
    if (paperCenter.HasValue && !Finite(paperCenter.Value)) throw new ArgumentException("Paper center must be finite.", nameof(paperCenter));
    if (modelCenter.HasValue && !Finite(modelCenter.Value)) throw new ArgumentException("Model center must be finite.", nameof(modelCenter));
    if (paperWidth.HasValue) Positive(paperWidth.Value, nameof(paperWidth));
    if (paperHeight.HasValue) Positive(paperHeight.Value, nameof(paperHeight));
    if (customScale.HasValue) Positive(customScale.Value, nameof(customScale));
    if (twistAngle.HasValue && !double.IsFinite(twistAngle.Value)) throw new ArgumentException("Twist angle must be finite.", nameof(twistAngle));
    ObjectId layoutId;
    bool viewportsEmpty;
    using (var inspect = TransactionFor(Db).TransactionManager.StartTransaction())
    {
      var layouts = (DBDictionary)inspect.GetObject(Db.LayoutDictionaryId, OpenMode.ForRead);
      if (!layouts.Contains(layoutName)) throw new KeyNotFoundException($"Layout '{layoutName}' was not found.");
      layoutId = layouts.GetAt(layoutName);
      var layout = (Layout)inspect.GetObject(layoutId, OpenMode.ForRead);
      if (layout.ModelType) throw new ArgumentException("SetViewport requires a paper layout.", nameof(layoutName));
      layoutName = layout.LayoutName;
      // Reopened drawings leave this empty until the layout is activated.
      viewportsEmpty = layout.GetViewports().Count == 0;
    }

    var manager = LayoutManager.Current;
    return RunCodeHelpers.WithCurrentLayout(layoutName, viewportsEmpty,
      () => manager.CurrentLayout, name => manager.CurrentLayout = name, () => ActivatedLayout = true, () =>
      {
        Check();
        using var tx = TransactionFor(Db).TransactionManager.StartTransaction();
        var layout = (Layout)tx.GetObject(layoutId, OpenMode.ForRead);
        var viewports = layout.GetViewports();
        if (viewports.Count == 0)
          throw new InvalidOperationException("Cannot identify the overall viewport after activating the layout; nothing was edited.");
        if (viewportId == viewports[0])
          throw new ArgumentException("The overall viewport shows paper space; edits to it revert.", nameof(viewportId));
        var viewport = (Viewport)tx.GetObject(viewportId, OpenMode.ForRead);
        if (viewport.OwnerId != layout.BlockTableRecordId || viewport.Number == 1)
          throw new ArgumentException("Viewport must be floating and owned by the requested layout; the overall viewport shows paper space and its edits revert.", nameof(viewportId));
        viewport.UpgradeOpen();
        var wasLocked = viewport.Locked;
        viewport.Locked = false;
        if (paperCenter.HasValue) viewport.CenterPoint = paperCenter.Value;
        if (paperWidth.HasValue) viewport.Width = paperWidth.Value;
        if (paperHeight.HasValue) viewport.Height = paperHeight.Value;
        if (modelCenter.HasValue) { viewport.ViewTarget = modelCenter.Value; viewport.ViewCenter = new Point2d(0, 0); }
        if (twistAngle.HasValue) viewport.TwistAngle = twistAngle.Value;
        if (customScale.HasValue)
        {
          viewport.CustomScale = customScale.Value;
          viewport.ViewHeight = (paperHeight ?? viewport.Height) / customScale.Value;
        }
        viewport.Locked = locked ?? wasLocked;
        viewport.UpdateDisplay();
        Check(); tx.Commit(); return viewportId;
      });
  }

  private void RequireDrawingWrite(string name)
  {
    Check();
    if (!WriteMode) throw new InvalidOperationException($"{name} needs mode 'write'.");
    _ = Db;
  }

  private static void Positive(double value, string name)
  {
    if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name, "Value must be finite and positive.");
  }

  private static bool Finite(Point3d point) =>
    double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);
}
