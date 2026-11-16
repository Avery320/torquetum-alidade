using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;

namespace Torquetum.Alidade;

internal static class GeometryCollectionConverter
{
    private const double ParameterTolerance = 1e-10;

    internal static double MetresPerUnit(string? explicitUnit, RhinoDoc? document)
    {
        var units = UnitSystem.Unset;
        if (explicitUnit is null)
            units = document?.ModelUnitSystem ?? UnitSystem.Unset;
        else
        {
            var name = explicitUnit.Trim();
            name = name.ToLowerInvariant() switch
            {
                "m" => nameof(UnitSystem.Meters),
                "cm" => nameof(UnitSystem.Centimeters),
                "mm" => nameof(UnitSystem.Millimeters),
                "in" => nameof(UnitSystem.Inches),
                "ft" => nameof(UnitSystem.Feet),
                _ => name,
            };
            if (!Enum.TryParse(name, true, out units)
                || !string.Equals(Enum.GetName(units), name, StringComparison.OrdinalIgnoreCase))
                units = UnitSystem.Unset;
        }
        if (units is UnitSystem.None or UnitSystem.Unset or UnitSystem.CustomUnits)
            throw CollectionFormatException.At("UNIT_UNAVAILABLE",
                "Set a supported unit such as Millimeters, Centimeters, Meters or Inches.");
        var scale = RhinoMath.UnitScale(units, UnitSystem.Meters);
        return scale > 0 && double.IsFinite(scale)
            ? scale
            : throw CollectionFormatException.At("UNIT_UNAVAILABLE", "Cannot convert this Rhino document unit.");
    }

    internal static GeometryCollection? Build(
        GH_Structure<IGH_Goo> geometry, GH_Structure<GH_String>? keys, GH_Structure<GH_String>? names, double scale,
        List<CollectionDiagnostic> diagnostics, RhinoDoc? document)
    {
        if (!CheckMetadataTrees(geometry, keys, names, diagnostics))
            return null;

        var branches = new List<object>(geometry.PathCount);
        var schemaVersion = 1;
        double? toleranceMetres = null;
        double CadTolerance() => toleranceMetres ??= RhinoStepGeometry.ToleranceMetres(document);
        for (var branchIndex = 0; branchIndex < geometry.PathCount; branchIndex++)
        {
            var path = geometry.Paths[branchIndex].Indices.ToArray();
            var values = geometry.Branches[branchIndex];
            var keyBranch = keys?.Branches[branchIndex];
            var nameBranch = names?.Branches[branchIndex];
            var items = new object?[values.Count];
            for (var index = 0; index < values.Count; index++)
            {
                var item = values[index];
                var key = keyBranch?[index]?.Value;
                var name = nameBranch?[index]?.Value;
                if (item == null)
                {
                    if (key != null)
                        diagnostics.Add(new("ITEM_KEYS_STRUCTURE", "A null geometry slot cannot have a Geometry ID.", path, index, "itemKey"));
                    if (name != null)
                        diagnostics.Add(new("ITEM_NAMES_STRUCTURE", "A null geometry slot cannot have a Geometry Name.", path, index, "name"));
                    continue;
                }
                try
                {
                    var shape = EncodeItem(item, scale, CadTolerance, (code, message) => diagnostics.Add(
                        new CollectionDiagnostic(code, message, path, index, Warning: true)));
                    if (key != null)
                        shape["itemKey"] = key;
                    if (name != null)
                        shape["name"] = name;
                    if (shape["kind"] is "brep-step")
                        schemaVersion = 2;
                    items[index] = shape;
                }
                catch (CollectionFormatException error)
                {
                    diagnostics.Add(error.Diagnostic with { Path = path, ItemIndex = index });
                }
            }
            branches.Add(new { path, items });
        }
        if (diagnostics.Any(item => !item.Warning))
            return null;
        try
        {
            return GeometryCollection.FromOwnedPayload(GeometryCollection.Snapshot(branches, schemaVersion));
        }
        catch (CollectionFormatException error)
        {
            diagnostics.Add(error.Diagnostic);
            return null;
        }
    }

    private static bool CheckMetadataTrees(GH_Structure<IGH_Goo> geometry, GH_Structure<GH_String>? keys,
        GH_Structure<GH_String>? names, List<CollectionDiagnostic> diagnostics)
    {
        if (keys == null && names == null) return true;
        var fields = new[] { (keys, "ITEM_KEYS_STRUCTURE", "Geometry IDs"), (names, "ITEM_NAMES_STRUCTURE", "Geometry Names") };
        foreach (var (tree, code, label) in fields)
        {
            if (tree != null && tree.PathCount != geometry.PathCount)
            {
                diagnostics.Add(new(code, $"{label} must have exactly the Geometry tree's branches."));
                return false;
            }
        }
        for (var index = 0; index < geometry.PathCount; index++)
        {
            var path = geometry.Paths[index].Indices;
            var count = geometry.Branches[index].Count;
            foreach (var (tree, code, label) in fields)
                if (tree != null && (!path.AsSpan().SequenceEqual(tree.Paths[index].Indices) || tree.Branches[index].Count != count))
                    diagnostics.Add(new(code, $"{label} must match the Geometry tree's paths and slots.", path.ToArray()));
        }
        return diagnostics.All(item => item.Warning);
    }

    internal static (GH_Structure<IGH_Goo> Geometry, GH_Structure<GH_String> Keys, GH_Structure<GH_String> Names)? Unpack(
        GeometryCollection collection, double scale, List<CollectionDiagnostic> diagnostics, RhinoDoc? document)
    {
        var geometry = new GH_Structure<IGH_Goo>();
        var keys = new GH_Structure<GH_String>();
        var names = new GH_Structure<GH_String>();
        double? toleranceMetres = null;
        double CadTolerance() => toleranceMetres ??= RhinoStepGeometry.ToleranceMetres(document);
        foreach (var branch in collection.Payload.GetProperty("branches").EnumerateArray())
        {
            var indices = branch.GetProperty("path").EnumerateArray().Select(value => (int)value.GetDouble()).ToArray();
            var path = new GH_Path(indices);
            geometry.EnsurePath(path);
            keys.EnsurePath(path);
            names.EnsurePath(path);
            var index = 0;
            foreach (var item in branch.GetProperty("items").EnumerateArray())
            {
                try
                {
                    geometry.Append(item.ValueKind == JsonValueKind.Null ? null! : DecodeItem(item, scale, CadTolerance), path);
                    keys.Append(ItemText(item, "itemKey")!, path);
                    names.Append(ItemText(item, "name")!, path);
                }
                catch (CollectionFormatException error)
                {
                    diagnostics.Add(error.Diagnostic with { Path = indices, ItemIndex = index });
                }
                index++;
            }
        }
        if (diagnostics.Count == 0)
            return (geometry, keys, names);

        foreach (var branch in geometry.Branches)
        foreach (var item in branch)
        {
            if (item is GH_Curve curve) curve.Value.Dispose();
            if (item is GH_Mesh mesh) mesh.Value.Dispose();
            if (item is GH_Brep brep) brep.Value.Dispose();
        }
        return null;
    }

    private static GH_String? ItemText(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) ? new GH_String(value.GetString()) : null;

    private static Dictionary<string, object?> EncodeItem(IGH_Goo item, double scale, Func<double> cadTolerance,
        Action<string, string> warn)
    {
        var native = item is GH_ObjectWrapper wrapper ? wrapper.Value : item.ScriptVariable();
        if (native is IGH_Goo wrapped)
            native = wrapped.ScriptVariable();
        Dictionary<string, object?> EncodePoint(Point3d point) => Fields("point", ("position", Point(point, scale)));
        Dictionary<string, object?> EncodeLine(Point3d from, Point3d to) =>
            Fields("line", ("start", Point(from, scale)), ("end", Point(to, scale)));
        var shape = native switch
        {
            Point3d point => EncodePoint(point),
            Rhino.Geometry.Point point => EncodePoint(point.Location),
            Line line => EncodeLine(line.From, line.To),
            LineCurve line => EncodeLine(line.PointAtStart, line.PointAtEnd),
            Polyline polyline => EncodePolyline(polyline, scale),
            Curve curve => EncodeCurve(curve, scale),
            Plane plane => Fields("plane", ("origin", Point(plane.Origin, scale)),
                ("normal", Vector(plane.ZAxis)), ("direction", Vector(plane.XAxis))),
            Box box => Fields("box",
                ("plane", new { origin = Point(box.Plane.Origin, scale), normal = Vector(box.Plane.ZAxis), direction = Vector(box.Plane.XAxis) }),
                ("x", Interval(box.X, scale)), ("y", Interval(box.Y, scale)), ("z", Interval(box.Z, scale))),
            Mesh mesh => EncodeMesh(mesh, scale, warn),
            Brep or Surface => EncodeStep((GeometryBase)native, scale, cadTolerance()),
            _ => throw CollectionFormatException.At("UNSUPPORTED_GEOMETRY",
                $"This collection version cannot represent {native?.GetType().Name ?? item.TypeName}."),
        };
        if (native is Curve source)
            shape["domain"] = Interval(source.Domain, 1);
        return shape;
    }

    private static Dictionary<string, object?> Fields(string kind, params (string Field, object? Value)[] fields)
    {
        var shape = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = kind };
        foreach (var (field, value) in fields)
            shape.Add(field, value);
        return shape;
    }

    private static Dictionary<string, object?> EncodeCurve(Curve curve, double scale)
    {
        if (curve is not PolylineCurve && curve is not NurbsCurve { Degree: 1, IsRational: false })
            throw CollectionFormatException.At("EXACT_CURVE_UNAVAILABLE",
                $"This version cannot preserve the parameterization of a {curve.GetType().Name} curve.");
        if (!curve.TryGetPolyline(out var points, out var parameters)
            || points.Count < 2 || parameters.Length != points.Count
            || curve.IsClosed != IsClosed(points) || !IsUniform(parameters, curve.Domain))
            throw CollectionFormatException.At("PARAMETERIZATION_UNSUPPORTED",
                "The curve's vertex positions and parameterization are not reproducible as a v1 polyline.");
        return EncodePolyline(points, scale);
    }

    private static Dictionary<string, object?> EncodePolyline(Polyline line, double scale)
    {
        var closed = IsClosed(line);
        var points = line.Take(line.Count - (closed ? 1 : 0)).Select(point => Point(point, scale)).ToArray();
        return Fields("polyline", ("points", points), ("closed", closed));
    }

    private static bool IsClosed(Polyline points) => points.Count > 2 && points.First == points.Last;

    private static bool IsUniform(double[] parameters, Interval domain)
    {
        if (parameters.Length < 2 || !double.IsFinite(domain.Length) || domain.Length <= 0)
            return false;
        for (var i = 0; i < parameters.Length; i++)
        {
            var offset = (parameters[i] - domain.T0) / domain.Length;
            if (!double.IsFinite(offset) || Math.Abs(offset - (double)i / (parameters.Length - 1)) > ParameterTolerance)
                return false;
        }
        return true;
    }

    private static Dictionary<string, object?> EncodeMesh(Mesh original, double scale, Action<string, string> warn)
    {
        using var copy = original.Faces.QuadCount == 0 ? null : original.DuplicateMesh();
        var mesh = copy ?? original;
        if (copy != null)
        {
            if (!mesh.Faces.ConvertQuadsToTriangles())
                throw CollectionFormatException.At("INVALID_GEOMETRY", "Rhino could not triangulate the mesh.");
            warn("MESH_TRIANGULATED", "Rhino split quad faces into triangles.");
        }
        if (original.Ngons.Count != 0 || original.VertexColors.Count != 0 || original.Normals.Count != 0
            || original.TextureCoordinates.Count != 0)
            warn("MESH_ATTRIBUTES_OMITTED", "The collection keeps triangle geometry only. Ngon groups and vertex attributes are not sent.");
        return Fields("triangle-mesh",
            ("positions", Enumerable.Range(0, mesh.Vertices.Count).Select(index => Point(mesh.Vertices.Point3dAt(index), scale)).ToArray()),
            ("triangles", mesh.Faces.Select(face => new[] { face.A, face.B, face.C }).ToArray()));
    }

    private static IGH_Goo DecodeItem(JsonElement item, double scale, Func<double> cadTolerance)
    {
        Point3d Position(string field) => Point(item.GetProperty(field), scale);
        Interval Section(string field) => Interval(item.GetProperty(field), scale);
        return item.GetProperty("kind").GetString() switch
        {
            "point" => new GH_Point(Position("position")),
            "line" => DecodeLine(new Line(Position("start"), Position("end")), item),
            "polyline" => DecodePolyline(item, scale),
            "plane" => new GH_Plane(ReadPlane(item, scale)),
            "box" => new GH_Box(new Box(ReadPlane(item.GetProperty("plane"), scale), Section("x"), Section("y"), Section("z"))),
            "triangle-mesh" => DecodeMesh(item, scale),
            "brep-step" => new GH_Brep(RhinoStepGeometry.Decode(item.GetProperty("shapeType").GetString()!,
                item.GetProperty("step").GetString()!, scale, cadTolerance())),
            _ => throw CollectionFormatException.At("UNSUPPORTED_GEOMETRY", "This collection version cannot be unpacked."),
        };
    }

    private static IGH_Goo DecodeLine(Line line, JsonElement item)
    {
        if (line.From == line.To)
            throw CollectionFormatException.At("PRECISION_LOSS", "Unit conversion made the line's endpoints coincide.");
        var interval = ReadDomain(item, new Interval(0, 1));
        if (interval.T0 == 0 && interval.T1 == 1)
            return new GH_Line(line);
        var curve = new LineCurve(line, interval.T0, interval.T1);
        if (curve.IsValid && curve.Domain == interval)
            return new GH_Curve(curve);
        curve.Dispose();
        throw CollectionFormatException.At("PARAMETERIZATION_UNSUPPORTED",
            "Rhino could not reconstruct the line's original domain.", "domain");
    }

    private static GH_Curve DecodePolyline(JsonElement item, double scale)
    {
        var points = item.GetProperty("points").EnumerateArray().Select(point => Point(point, scale)).ToList();
        var closed = item.GetProperty("closed").GetBoolean();
        if (closed)
            points.Add(points[0]);
        var curve = new PolylineCurve(points);
        try
        {
            var interval = ReadDomain(item, new Interval(0, points.Count - 1));
            curve.Domain = interval;
            if (curve.Domain != interval || curve.IsClosed != closed
                || !curve.TryGetPolyline(out var reconstructed, out var parameters)
                || !points.SequenceEqual(reconstructed) || !IsUniform(parameters, interval))
                throw CollectionFormatException.At("PARAMETERIZATION_UNSUPPORTED",
                    "Rhino could not reconstruct this polyline with the same vertices and domain.");
            return new GH_Curve(curve);
        }
        catch
        {
            curve.Dispose();
            throw;
        }
    }

    private static Dictionary<string, object?> EncodeStep(GeometryBase geometry, double scale, double tolerance)
    {
        var (shapeType, text) = RhinoStepGeometry.Encode(geometry, scale, tolerance);
        return Fields("brep-step", ("shapeType", shapeType), ("step", text));
    }

    private static Plane ReadPlane(JsonElement item, double scale)
    {
        var normal = Vector(item.GetProperty("normal"));
        normal.Unitize();
        var y = Vector3d.CrossProduct(normal, Vector(item.GetProperty("direction")));
        var x = Vector3d.CrossProduct(y, normal);
        var plane = new Plane(Point(item.GetProperty("origin"), scale), x, y);
        return plane.IsValid ? plane : throw CollectionFormatException.At("INVALID_GEOMETRY", "Rhino rejected the plane.");
    }

    private static GH_Mesh DecodeMesh(JsonElement item, double scale)
    {
        var result = new Mesh();
        try
        {
            result.Vertices.UseDoublePrecisionVertices = true;
            foreach (var value in item.GetProperty("positions").EnumerateArray())
            {
                var point = Point(value, scale);
                var index = result.Vertices.Add(point);
                if (index < 0 || result.Vertices.Point3dAt(index) != point)
                    throw CollectionFormatException.At("PRECISION_LOSS", "Rhino could not retain the mesh vertex in double precision.");
            }
            foreach (var face in item.GetProperty("triangles").EnumerateArray())
                result.Faces.AddFace((int)face[0].GetDouble(), (int)face[1].GetDouble(), (int)face[2].GetDouble());
            if (!result.IsValid)
                throw CollectionFormatException.At("INVALID_GEOMETRY", "Rhino could not reconstruct a valid triangle mesh.");
            return new GH_Mesh(result);
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static double[] Point(Point3d value, double scale) =>
        value.IsValid ? new[] { Scale(value.X, scale), Scale(value.Y, scale), Scale(value.Z, scale) }
            : throw CollectionFormatException.At("INVALID_GEOMETRY", "The Rhino point is unset or nonfinite.");

    private static double[] Vector(Vector3d value) => new[] { Scale(value.X, 1), Scale(value.Y, 1), Scale(value.Z, 1) };
    private static double[] Interval(Interval value, double scale) => new[] { Scale(value.T0, scale), Scale(value.T1, scale) };
    private static Point3d Point(JsonElement value, double scale)
    {
        var point = new Point3d(Scale(value[0].GetDouble(), scale), Scale(value[1].GetDouble(), scale), Scale(value[2].GetDouble(), scale));
        return point.IsValid ? point : throw CollectionFormatException.At("PRECISION_LOSS", "The target Rhino coordinates are invalid.");
    }
    private static Vector3d Vector(JsonElement value) => new(value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble());
    private static Interval ReadDomain(JsonElement value, Interval fallback) =>
        value.TryGetProperty("domain", out var domain) ? Interval(domain, 1) : fallback;

    private static Interval Interval(JsonElement value, double scale)
    {
        var a = Scale(value[0].GetDouble(), scale);
        var b = Scale(value[1].GetDouble(), scale);
        return a < b ? new Interval(a, b) : throw CollectionFormatException.At("PRECISION_LOSS", "The target interval collapsed.");
    }
    private static double Scale(double value, double scale)
    {
        var result = value * scale;
        return double.IsFinite(result) && (value == 0 || result != 0)
            ? result : throw CollectionFormatException.At("PRECISION_LOSS", "The value is outside the supported numeric range.");
    }
}
