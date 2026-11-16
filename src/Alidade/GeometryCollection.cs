using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Torquetum.Alidade;

internal sealed record CollectionDiagnostic(string Code, string Message, int[]? Path = null,
    int? ItemIndex = null, string? Field = null, bool Warning = false)
{
    public override string ToString() =>
        (ItemIndex is { } index ? $"Item {index}: " : "") + $"{Code}: {Message}"
        + (Field is null ? "" : $" ({Field})");
}

internal sealed class CollectionFormatException : FormatException
{
    internal CollectionFormatException(CollectionDiagnostic diagnostic) : base(diagnostic.Path is { Length: > 0 } path
        ? $"Branch {{{string.Join(";", path)}}}: {diagnostic}" : diagnostic.ToString()) =>
        Diagnostic = diagnostic;

    internal CollectionDiagnostic Diagnostic { get; }

    internal static CollectionFormatException At(string code, string message, string? field = null) =>
        new(new CollectionDiagnostic(code, message, Field: field));
}

/// <summary>An immutable geometry tree in Project coordinates and metres.</summary>
public sealed class GeometryCollection
{
    internal const int MaxExchangeBytes = 32 * 1024 * 1024;
    private const double MinimumPlaneAxis = 1e-9;
    private static readonly string[] ItemFields = { "kind", "itemKey", "name" };
    private static readonly string[] CurveFields = { "kind", "itemKey", "name", "domain" };

    private GeometryCollection(JsonElement payload, int schemaVersion, int branchCount, int count, int geometryCount, bool hasNames)
    {
        Payload = payload;
        SchemaVersion = schemaVersion;
        BranchCount = branchCount;
        SlotCount = count;
        GeometryCount = geometryCount;
        HasNames = hasNames;
    }

    public JsonElement Payload { get; }
    public int SchemaVersion { get; }
    public int BranchCount { get; }
    public int SlotCount { get; }
    public int GeometryCount { get; }
    internal bool HasNames { get; }

    public override string ToString() => $"{BranchCount} branches, {SlotCount} slots, {GeometryCount} geometries";

    internal static GeometryCollection FromOwnedPayload(JsonElement payload)
    {
        Fields(payload, new[] { "schemaVersion", "units", "space", "branches" });
        var schemaVersion = Integer(payload.GetProperty("schemaVersion"), "schemaVersion");
        if (schemaVersion is not (1 or 2) || Text(payload.GetProperty("units"), "units") != "m"
            || Text(payload.GetProperty("space"), "space") != "project")
            throw Invalid("Expected a supported collection in Project metres.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var comparePaths = Comparer<int[]>.Create((left, right) => left.AsSpan().SequenceCompareTo(right));
        var branches = new SortedDictionary<int[], JsonElement>(comparePaths);
        var previousPath = Array.Empty<int>();
        var outOfOrder = false;
        var slots = 0;
        var geometryCount = 0;
        var hasNames = false;
        foreach (var branch in Elements(payload.GetProperty("branches"), "branches"))
        {
            Fields(branch, new[] { "path", "items" });
            var path = Elements(branch.GetProperty("path"), "path", 1).Select(value => Integer(value, "path")).ToArray();
            if (!branches.TryAdd(path, branch))
                throw new CollectionFormatException(new("INVALID_COLLECTION", "Duplicate branch path.", path, Field: "path"));

            outOfOrder |= comparePaths.Compare(previousPath, path) > 0;
            previousPath = path;
            var index = 0;
            foreach (var item in Elements(branch.GetProperty("items"), "items"))
            {
                if (item.ValueKind != JsonValueKind.Null)
                {
                    try
                    {
                        CheckItem(item, schemaVersion);
                        if (item.TryGetProperty("itemKey", out var keyValue))
                        {
                            var key = Text(keyValue, "itemKey");
                            if (string.IsNullOrWhiteSpace(key))
                                throw CollectionFormatException.At("INVALID_ITEM_KEY", "A Geometry ID cannot be blank.", "itemKey");
                            if (!keys.Add(key))
                                throw CollectionFormatException.At("DUPLICATE_ITEM_KEY", "Geometry IDs must be unique within a collection.", "itemKey");
                        }
                        if (item.TryGetProperty("name", out var name))
                        {
                            if (name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
                                throw CollectionFormatException.At("INVALID_ITEM_NAME", "A Geometry Name must be a nonblank string.", "name");
                            hasNames = true;
                        }
                        geometryCount++;
                    }
                    catch (CollectionFormatException error)
                    {
                        throw new CollectionFormatException(error.Diagnostic with { Path = path, ItemIndex = index });
                    }
                }
                index++;
            }
            slots += index;
        }

        if (outOfOrder)
            payload = Snapshot(branches.Values, schemaVersion);
        return new GeometryCollection(payload, schemaVersion, branches.Count, slots, geometryCount, hasNames);
    }

    internal static JsonElement Snapshot<T>(IEnumerable<T> branches, int schemaVersion) => JsonSerializer.SerializeToElement(
        new { schemaVersion, units = "m", space = "project", branches });

    private static void CheckItem(JsonElement item, int schemaVersion)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("kind", out var value))
            throw Invalid("Expected a geometry object with a kind.", "kind");
        var kind = Text(value, "kind");
        void ShapeFields(params string[] fields) => Fields(item, fields, kind is "line" or "polyline" ? CurveFields : ItemFields);
        switch (kind)
        {
            case "point":
                ShapeFields("position");
                Coordinates(item.GetProperty("position"), "position");
                break;
            case "line":
                ShapeFields("start", "end");
                if (Coordinates(item.GetProperty("start"), "start") == Coordinates(item.GetProperty("end"), "end"))
                    throw Invalid("A line's endpoints must differ.", "end");
                CheckDomain(item);
                break;
            case "polyline":
                ShapeFields("points", "closed");
                CheckPolyline(item);
                CheckDomain(item);
                break;
            case "plane":
                ShapeFields("origin", "normal", "direction");
                CheckPlane(item);
                break;
            case "box":
                ShapeFields("plane", "x", "y", "z");
                var plane = item.GetProperty("plane");
                Fields(plane, new[] { "origin", "normal", "direction" });
                CheckPlane(plane);
                foreach (var axis in new[] { "x", "y", "z" })
                    Interval(item.GetProperty(axis), axis);
                break;
            case "triangle-mesh":
                ShapeFields("positions", "triangles");
                CheckMesh(item);
                break;
            case "brep-step" when schemaVersion == 2:
                ShapeFields("shapeType", "step");
                if (Text(item.GetProperty("shapeType"), "shapeType") is not ("face" or "shell" or "solid"))
                    throw Invalid("Declare a STEP face, shell or solid.", "shapeType");
                if (!StepText.IsPart21(Text(item.GetProperty("step"), "step")))
                    throw CollectionFormatException.At("INVALID_STEP", "Expected a bounded STEP Part 21 document.", "step");
                break;
            default:
                throw CollectionFormatException.At("UNSUPPORTED_GEOMETRY", "This geometry kind is unavailable in the declared collection version.", "kind");
        }
    }

    private static void CheckDomain(JsonElement item)
    {
        if (item.TryGetProperty("domain", out var domain))
            Interval(domain, "domain");
    }

    private static void CheckPolyline(JsonElement item)
    {
        var closedValue = item.GetProperty("closed");
        if (closedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid("Expected a Boolean closure flag.", "closed");
        var closed = closedValue.GetBoolean();
        var points = Elements(item.GetProperty("points"), "points", closed ? 3 : 2);
        points.MoveNext();
        var first = Coordinates(points.Current, "points", 0);
        var second = first;
        var previous = first;
        var hasThird = false;
        var index = 1;
        while (points.MoveNext())
        {
            var current = Coordinates(points.Current, "points", index);
            if (previous == current)
                throw Invalid("A polyline segment has zero length.", IndexedField("points", index));
            if (index == 1)
                second = current;
            hasThird |= current != first && current != second;
            previous = current;
            index++;
        }
        if (closed && (previous == first || !hasThird))
            throw Invalid("A closed polyline needs three distinct vertices and no repeated closing vertex.", "points");
    }

    private static void CheckPlane(JsonElement item)
    {
        Coordinates(item.GetProperty("origin"), "origin");
        var normal = Coordinates(item.GetProperty("normal"), "normal");
        var direction = Coordinates(item.GetProperty("direction"), "direction");
        var length = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
        if (!double.IsFinite(length) || length <= MinimumPlaneAxis)
            throw Invalid("A plane needs a nonzero normal.", "normal");
        var side = Cross((normal.X / length, normal.Y / length, normal.Z / length), direction);
        var width = Math.Sqrt(side.X * side.X + side.Y * side.Y + side.Z * side.Z);
        if (!double.IsFinite(width) || width <= MinimumPlaneAxis)
            throw Invalid("A plane's direction and normal must not be parallel.", "direction");
    }

    private static void CheckMesh(JsonElement item)
    {
        var positions = item.GetProperty("positions");
        var vertices = Elements(positions, "positions", 3);
        var points = new (double X, double Y, double Z)[positions.GetArrayLength()];
        var index = 0;
        foreach (var vertex in vertices)
        {
            points[index] = Coordinates(vertex, "positions", index);
            index++;
        }

        index = 0;
        foreach (var face in Elements(item.GetProperty("triangles"), "triangles", 1))
        {
            RequireLength(face, 3, "triangles", index);
            var a = Integer(face[0], "triangles", index);
            var b = Integer(face[1], "triangles", index);
            var c = Integer(face[2], "triangles", index);
            if (a >= points.Length || b >= points.Length || c >= points.Length)
                throw CollectionFormatException.At("MESH_INDEX_OUT_OF_RANGE", "A triangle refers to a missing vertex.", IndexedField("triangles", index));
            var normal = Cross(Subtract(points[b], points[a]), Subtract(points[c], points[a]));
            if (normal == (0, 0, 0) || !double.IsFinite(normal.X) || !double.IsFinite(normal.Y) || !double.IsFinite(normal.Z))
                throw Invalid("A triangle must have a finite, nonzero area.", IndexedField("triangles", index));
            index++;
        }
    }

    private static (double X, double Y, double Z) Subtract((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static (double X, double Y, double Z) Coordinates(JsonElement value, string field, int index = -1)
    {
        RequireLength(value, 3, field, index);
        return (Number(value[0], field, index), Number(value[1], field, index), Number(value[2], field, index));
    }

    private static void Interval(JsonElement value, string field)
    {
        RequireLength(value, 2, field);
        if (Number(value[0], field) >= Number(value[1], field))
            throw Invalid("Expected a strictly increasing interval.", field);
    }

    private static void RequireLength(JsonElement value, int count, string field, int index = -1)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != count)
            throw Invalid($"Expected exactly {count} values.", IndexedField(field, index));
    }

    private static JsonElement.ArrayEnumerator Elements(JsonElement value, string field, int minimum = 0)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < minimum)
            throw Invalid($"Expected an array with at least {minimum} elements.", field);
        return value.EnumerateArray();
    }

    private static int Integer(JsonElement value, string field, int index = -1)
    {
        var number = Number(value, field, index);
        if (number < 0 || number > int.MaxValue || number != Math.Truncate(number))
            throw Invalid("Expected a nonnegative 32-bit integer.", IndexedField(field, index));
        return (int)number;
    }

    private static double Number(JsonElement value, string field, int index = -1) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : throw Invalid("Expected a finite number.", IndexedField(field, index));

    private static string Text(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw Invalid("Expected a string.", field);

    private static void Fields(JsonElement value, string[] required, params string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid("Expected an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if ((!required.Contains(property.Name) && !optional.Contains(property.Name)) || !seen.Add(property.Name))
                throw Invalid("Unexpected or repeated property.", property.Name);
        }
        foreach (var name in required)
        {
            if (!seen.Contains(name))
                throw Invalid("Missing required property.", name);
        }
    }

    private static string IndexedField(string field, int index) => index < 0 ? field : $"{field}[{index}]";

    private static CollectionFormatException Invalid(string message, string? field = null) =>
        CollectionFormatException.At("INVALID_GEOMETRY", message, field);
}
