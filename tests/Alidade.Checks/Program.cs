using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Torquetum.Alidade;

if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--exchange"))
    throw new ArgumentException("Usage: Alidade.Checks <Alidade.gha> <geometry-collection.json> [--exchange]");

AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
await AlidadeChecks.Run(args[1], args.Length == 3);

internal static class AlidadeChecks
{
    internal static async Task Run(string fixturePath, bool exchange)
    {
        var diagnostics = new List<CollectionDiagnostic>();
        var fixture = File.ReadAllText(fixturePath);
        var collection = Parse(fixture);
        var unordered = Parse("""{"schemaVersion":1,"units":"m","space":"project","branches":[{"path":[2,10],"items":[null]},{"path":[2,9],"items":[]},{"path":[2],"items":[]}]}""");
        var sortedPaths = unordered.Payload.GetProperty("branches").EnumerateArray().Select(branch => branch.GetProperty("path").GetRawText());
        Check(sortedPaths.SequenceEqual(new[] { "[2]", "[2,9]", "[2,10]" }), "numeric branch ordering");
        Reject(fixture.Replace("[0, 4]", "[0]"), "Duplicate branch path");
        Reject(fixture.Replace("[1, 2, 3]", "[1e400, 2, 3]"), "finite number");
        Reject(fixture.Replace("[0, 5]", "[5, 0]"), "strictly increasing");
        Reject(fixture.Replace("\"triangles\": [[0, 1, 2]]", "\"triangles\": [[0, 1, 3]]"), "MESH_INDEX_OUT_OF_RANGE");
        Reject(fixture.Replace("\"triangles\": [[0, 1, 2]]", "\"triangles\": [[0, 1.5, 2]]"), "(triangles[0])");
        Reject("""{"schemaVersion":1,"units":"m","space":"project","branches":[{"path":[0],"items":[{"kind":"polyline","points":[[0,0,0],[1,0,0],[0,0,0],[1,0,0]],"closed":true}]}]}""",
            "three distinct vertices");
        Reject(fixture.Replace("\"closed\": true", "\"closed\": 1"), "Boolean closure");

        var cadFixture = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fixturePath))!, "..", "..", "v2", "fixtures", "geometry-collection.json");
        var cadSource = File.ReadAllText(cadFixture);
        var cad = Parse(cadSource);
        var cadBranches = cad.Payload.GetProperty("branches");
        Reject(cadSource.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1"), "declared collection version");
        Reject(cadSource.Replace("\"shapeType\": \"solid\"", "\"shapeType\": \"mesh\""), "face, shell or solid");
        Reject(cadSource.Replace("END-ISO-10303-21;", "BROKEN-STEP;"), "INVALID_STEP");
        var initial = cadBranches[0].GetProperty("items")[0].GetProperty("step").GetString()!;
        var withMetadata = initial.Replace("Open CASCADE Shape Model", "/private/source.stp")
            .Replace("'Author'", "'Private; O''Brien'");
        var sanitized = StepText.SanitizeExport(withMetadata);
        Check(!sanitized.Contains("/private/source.stp", StringComparison.Ordinal)
            && !sanitized.Contains("O''Brien", StringComparison.Ordinal)
            && sanitized[sanitized.IndexOf("DATA;", StringComparison.Ordinal)..] ==
               initial[initial.IndexOf("DATA;", StringComparison.Ordinal)..], "sanitization preserves geometry body");
        var managed = JsonNode.Parse(fixture)!;
        managed["branches"]![2]!["items"] = new JsonArray();
        managed["branches"]![0]!["items"]![2]!.AsObject().Remove("domain");
        managed["branches"]![0]!["items"]![0]!["name"] = "Datum point";
        var managedAsset = Parse(managed.ToJsonString());
        var (geometry, keys, names) = GeometryCollectionConverter.Unpack(managedAsset, 100d, diagnostics, null)!.Value;
        Check(geometry.PathCount == 3 && geometry.Branches[0][1] == null && geometry.Branches[1].Count == 0, "native GH null and empty branches");
        Check(geometry.Paths[2].Indices.SequenceEqual(new[] { 3, 0, 7 }), "native GH deep path");
        Check(geometry.Branches[0][0] is GH_Point point && point.Value == new Point3d(100, 200, 300), "point in target centimetres");
        Check(geometry.Branches[0][2] is GH_Line line && line.Value.To == new Point3d(100, 0, 0), "line direction and target centimetres");
        Check(keys.Branches[0][0].Value == "sample-point" && keys.Branches[0][1] == null && keys.Branches[1].Count == 0, "native GH key alignment");
        Check(names.Branches[0][0].Value == "Datum point" && names.Branches[0][1] == null && names.Branches[1].Count == 0,
            "native GH name alignment keeps null slots and empty branches");

        var roundTrip = GeometryCollectionConverter.Build(geometry, keys, names, 0.01d, diagnostics, null);
        Check(roundTrip != null && diagnostics.Count == 0 && Equal(managedAsset.Payload, roundTrip.Payload),
            "native GH centimetre round trip");
        VerifyGeometryMetadata();

        keys.Append(new GH_String("unexpected"), new GH_Path(0, 4));
        Check(GeometryCollectionConverter.Build(geometry, keys, names, 0.01d, diagnostics, null) == null
            && diagnostics.Any(item => item.Code == "ITEM_KEYS_STRUCTURE"),
            "key structure mismatch rejects the whole collection");
        Console.WriteLine("PASS v1 and v2 formats, STEP metadata scrub, native GH trees, and stable Geometry IDs and names across branch swaps.");
        if (exchange)
            await GeometryExchangeCheck.Run(collection, cad, Console.ReadLine() ?? "");
    }

    internal static GeometryCollection Parse(string json) =>
        GeometryCollection.FromOwnedPayload(JsonSerializer.Deserialize<JsonElement>(json));

    private static void VerifyGeometryMetadata()
    {
        var geometry = new GH_Structure<IGH_Goo>();
        var ids = new GH_Structure<GH_String>();
        var names = new GH_Structure<GH_String>();
        var first = new GH_Path(2);
        var second = new GH_Path(7);
        geometry.Append(new GH_Point(new Point3d(1000, 0, 0)), first);
        geometry.Append(new GH_Point(new Point3d(1000, 0, 0)), second);
        ids.Append(new GH_String("point-A"), first);
        ids.Append(new GH_String("point-B"), second);
        names.Append(new GH_String("Fixture A"), first);
        names.Append(new GH_String("Fixture B"), second);
        VerifyRoundTrip((100, "point-A", "Fixture A"), (100, "point-B", "Fixture B"));

        geometry.Branches[1][0] = new GH_Point(new Point3d(1493, 0, 0));
        ids.Branches[0][0] = new GH_String("point-B");
        ids.Branches[1][0] = new GH_String("point-A");
        names.Branches[0][0] = new GH_String("Fixture B");
        names.Branches[1][0] = new GH_String("Fixture A");
        VerifyRoundTrip((100, "point-B", "Fixture B"), (149.3, "point-A", "Fixture A"));

        names.Branches[1][0] = new GH_String("Fixture B");
        var nameDiagnostics = new List<CollectionDiagnostic>();
        Check(GeometryCollectionConverter.Build(geometry, ids, names, 0.001, nameDiagnostics, null) != null,
            "display names may repeat");
        names.Branches[1][0] = new GH_String(" ");
        Check(GeometryCollectionConverter.Build(geometry, ids, names, 0.001, nameDiagnostics, null) == null
            && nameDiagnostics.Single() is { Code: "INVALID_ITEM_NAME", ItemIndex: 0, Path: [7] },
            "blank Geometry Name reports its branch and slot");
        nameDiagnostics.Clear();
        names.Append(new GH_String("unexpected"), second);
        Check(GeometryCollectionConverter.Build(geometry, ids, names, 0.001, nameDiagnostics, null) == null
            && nameDiagnostics.Single() is { Code: "ITEM_NAMES_STRUCTURE", Path: [7] },
            "names must match the geometry's slot count");

        foreach (var (invalidId, code) in new[] { ("point-B", "DUPLICATE_ITEM_KEY"), (" ", "INVALID_ITEM_KEY") })
        {
            ids.Branches[1][0] = new GH_String(invalidId);
            var diagnostics = new List<CollectionDiagnostic>();
            Check(GeometryCollectionConverter.Build(geometry, ids, null, 0.001, diagnostics, null) == null
                && diagnostics.Single() is { ItemIndex: 0, Path: [7] } failure && failure.Code == code,
                "invalid Geometry ID reports its branch and slot");
        }

        void VerifyRoundTrip(params (double X, string Id, string Name)[] expected)
        {
            var diagnostics = new List<CollectionDiagnostic>();
            var collection = GeometryCollectionConverter.Build(geometry, ids, names, 0.001, diagnostics, null);
            Check(collection != null, "build explicitly identified pieces");
            var (points, returnedIds, returnedNames) = GeometryCollectionConverter.Unpack(collection!, 100d, diagnostics, null)!.Value;
            var paths = geometry.Paths.Select(path => path.ToString());
            Check(diagnostics.Count == 0 && new[] { points.Paths, returnedIds.Paths, returnedNames.Paths }
                .All(result => result.Select(path => path.ToString()).SequenceEqual(paths)),
                "geometry, persistent IDs and names share the same returned branches");
            for (var branch = 0; branch < expected.Length; branch++)
            {
                var (x, id, name) = expected[branch];
                Check(returnedIds.Branches[branch][0].Value == id && returnedNames.Branches[branch][0].Value == name
                    && Math.Abs(((GH_Point)points.Branches[branch][0]).Value.X - x) < 1e-9, "round-trip geometry identity");
            }
        }
    }

    private static void Reject(string json, string expected)
    {
        try
        {
            Parse(json);
            throw new InvalidOperationException("Unexpectedly accepted: " + expected);
        }
        catch (CollectionFormatException error) when (error.Message.Contains(expected, StringComparison.Ordinal))
        {
        }
    }

    internal static void Check(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException("Failed: " + label);
    }

    private static bool Equal(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;
        return left.ValueKind switch
        {
            JsonValueKind.Object => left.EnumerateObject().Count() == right.EnumerateObject().Count()
                && left.EnumerateObject().All(property =>
                    right.TryGetProperty(property.Name, out var value) && Equal(property.Value, value)),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength()
                && left.EnumerateArray().Zip(right.EnumerateArray(), Equal).All(equal => equal),
            JsonValueKind.Number => left.GetDouble() == right.GetDouble(),
            _ => left.GetRawText() == right.GetRawText(),
        };
    }
}
