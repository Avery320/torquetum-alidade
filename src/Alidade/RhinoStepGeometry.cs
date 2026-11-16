using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;

namespace Torquetum.Alidade;

internal static class StepText
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    private const string FileName = "FILE_NAME('Alidade.stp','2000-01-01T00:00:00',(''),(''),'Alidade','Rhino','')";
    private static readonly Regex FileNameStatement = new(
        @"(?i)\bFILE_NAME\s*\((?:'(?:''|[^'])*'|[^;'])*\)\s*;",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static bool IsPart21(string value)
    {
        if (value.Length == 0 || value.Length > GeometryCollection.MaxExchangeBytes || value.Contains('\0'))
            return false;
        var text = value.AsSpan().Trim();
        try
        {
            return text.StartsWith("ISO-10303-21;", StringComparison.Ordinal)
                && text.EndsWith("END-ISO-10303-21;", StringComparison.Ordinal)
                && Utf8.GetByteCount(value) <= GeometryCollection.MaxExchangeBytes;
        }
        catch (EncoderFallbackException) { return false; }
    }

    internal static string SanitizeExport(string value)
    {
        var headerEnd = value.IndexOf("ENDSEC;", StringComparison.OrdinalIgnoreCase);
        if (!IsPart21(value) || headerEnd is < 0 or > 16384)
            throw CollectionFormatException.At("INVALID_STEP", "Rhino did not write a valid STEP Part 21 header.", "step");

        // Strip export location and user metadata without touching STEP geometry.
        var header = value[..headerEnd];
        try
        {
            var fileNames = FileNameStatement.Matches(header);
            if (fileNames.Count == 1)
            {
                var statement = fileNames[0];
                return string.Concat(value.AsSpan(0, statement.Index), FileName.AsSpan(), ";", value.AsSpan(statement.Index + statement.Length));
            }
        }
        catch (RegexMatchTimeoutException) { }
        throw CollectionFormatException.At("INVALID_STEP", "Rhino returned unexpected STEP file metadata.", "step");
    }
}

internal static class RhinoStepGeometry
{
    private const double MinimumToleranceMetres = 1e-6;

    internal static (string ShapeType, string Text) Encode(GeometryBase input, double metresPerUnit, double toleranceMetres)
    {
        using var copy = input is Brep && metresPerUnit == 1 ? null : DuplicateBrep(input);
        var shape = copy ?? (Brep)input;
        Scale(shape, metresPerUnit);
        var type = ShapeType(shape);
        return InTemporaryModel(toleranceMetres, (document, path) =>
        {
            if (document.Objects.AddBrep(shape) == Guid.Empty ||
                !FileStp.Write(path, document, new FileStpWriteOptions
                {
                    Schema = FileStpWriteOptions.StepSchema.SF_214,
                    SplitClosedSurfaces = false,
                }))
                throw CollectionFormatException.At("STEP_EXPORT_FAILED", "Rhino could not export this shape as STEP.", "step");
            var file = new FileInfo(path);
            if (file.Length > GeometryCollection.MaxExchangeBytes)
                throw CollectionFormatException.At("CONTENT_TOO_LARGE", "The native STEP document exceeds the transfer limit.", "step");
            var text = StepText.SanitizeExport(File.ReadAllText(path, StepText.Utf8));
            if (text.Contains(path, StringComparison.Ordinal))
                throw CollectionFormatException.At("STEP_EXPORT_FAILED", "Rhino included the temporary path in STEP geometry metadata.", "step");
            return (type, text);
        });
    }

    internal static Brep Decode(string expectedType, string text, double targetUnitsPerMetre, double toleranceMetres)
    {
        return InTemporaryModel(toleranceMetres, (document, path) =>
        {
            File.WriteAllText(path, text, StepText.Utf8);
            if (!FileStp.Read(path, document, new FileStpReadOptions { JoinSurfaces = true }))
                throw CollectionFormatException.At("STEP_IMPORT_FAILED", "Rhino could not open this STEP item.", "step");

            var objects = document.Objects.Take(2).ToArray();
            if (objects.Length != 1)
                throw CollectionFormatException.At("UNSUPPORTED_GEOMETRY", "A STEP item must contain one Rhino shape.", "step");

            var shape = DuplicateBrep(objects[0].Geometry);
            try
            {
                // Use the units Rhino assigned to the imported coordinates.
                var sourceMetres = GeometryCollectionConverter.MetresPerUnit(null, document);
                Scale(shape, sourceMetres * targetUnitsPerMetre);
                if (ShapeType(shape) != expectedType)
                    throw CollectionFormatException.At("INVALID_STEP", "The imported shape does not match its face, shell or solid declaration.", "shapeType");
                return shape;
            }
            catch
            {
                shape.Dispose();
                throw;
            }
        });
    }

    internal static double ToleranceMetres(RhinoDoc? document)
    {
        if (document == null)
            return MinimumToleranceMetres;
        try
        {
            var tolerance = document.ModelAbsoluteTolerance * GeometryCollectionConverter.MetresPerUnit(null, document);
            return double.IsFinite(tolerance) ? Math.Max(tolerance, MinimumToleranceMetres) : MinimumToleranceMetres;
        }
        catch (CollectionFormatException) { return MinimumToleranceMetres; }
    }

    private static Brep DuplicateBrep(GeometryBase? shape) => (shape switch
    {
        Brep brep => brep.DuplicateBrep(),
        BrepFace face => face.DuplicateFace(false),
        Extrusion extrusion => extrusion.ToBrep(false),
        Surface surface => surface.ToBrep(),
        _ => null,
    }) ?? throw CollectionFormatException.At("UNSUPPORTED_GEOMETRY", "This object cannot be exchanged as a STEP face, shell or solid.");

    private static string ShapeType(Brep geometry)
    {
        if (!geometry.IsValid || !geometry.IsManifold || geometry.Faces.Count == 0)
            throw CollectionFormatException.At("INVALID_GEOMETRY", "STEP exchange needs a valid manifold Brep.");
        var disjoint = geometry.GetConnectedComponents();
        foreach (var component in disjoint)
            component.Dispose();
        if (disjoint.Length != 0)
            throw CollectionFormatException.At("UNSUPPORTED_GEOMETRY", "Separate disconnected CAD shapes into different inputs.");

        return geometry.IsSolid ? "solid" : geometry.Faces.Count == 1 ? "face" : "shell";
    }

    private static void Scale(Brep shape, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0
            || (scale != 1 && !shape.Transform(Transform.Scale(Point3d.Origin, scale))))
            throw CollectionFormatException.At("PRECISION_LOSS", "Rhino could not retain a valid shape after converting units.");
    }

    private static T InTemporaryModel<T>(double toleranceMetres, Func<RhinoDoc, string, T> run)
    {
        try
        {
            var directory = Directory.CreateTempSubdirectory("alidade-step-");
            try
            {
                using var document = RhinoDoc.CreateHeadless(null)
                    ?? throw CollectionFormatException.At("STEP_UNAVAILABLE", "Rhino could not open an isolated document for STEP exchange.");
                document.ModelUnitSystem = UnitSystem.Meters;
                document.ModelAbsoluteTolerance = toleranceMetres;
                return run(document, Path.Combine(directory.FullName, "Alidade.stp"));
            }
            finally { directory.Delete(recursive: true); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or EncoderFallbackException or DecoderFallbackException)
        {
            throw CollectionFormatException.At("STEP_FILE_ERROR", "Rhino could not process a temporary STEP document.", "step");
        }
    }
}
