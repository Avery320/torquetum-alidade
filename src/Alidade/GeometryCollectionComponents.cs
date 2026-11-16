using System;
using System.Collections.Generic;
using System.Linq;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace Torquetum.Alidade;

internal static class CollectionOutputs
{
    internal const string GeometryIdsName = "Geometry IDs";
    internal const string GeometryNamesName = "Geometry Names";

    internal static bool ReadGeometryIds(bool loaded, IGH_Param parameter, string description)
    {
        if (loaded && parameter.Name == "Item Keys")
        {
            parameter.Name = GeometryIdsName;
            parameter.Description = description;
        }
        return loaded;
    }

    internal static void WriteDiagnostics(GH_Component component, IGH_DataAccess data, int index,
        List<CollectionDiagnostic> diagnostics)
    {
        var result = new GH_Structure<GH_String>();
        foreach (var diagnostic in diagnostics)
            result.Append(new GH_String(diagnostic.ToString()), new GH_Path(diagnostic.Path ?? new[] { 0 }));
        data.BlitData(index, result, overwrite: true);

        if (diagnostics.Count != 0)
        {
            var first = diagnostics.FirstOrDefault(item => !item.Warning) ?? diagnostics[0];
            component.AddRuntimeMessage(first.Warning ? GH_RuntimeMessageLevel.Warning : GH_RuntimeMessageLevel.Error,
                first.ToString());
        }
    }
}

public sealed class BuildGeometryCollection : GH_Component
{
    private const string GeometryIdsDescription =
        "Stable, unique IDs at the same paths and slots as Geometry. Reuse an ID when updating the same item.";

    public BuildGeometryCollection()
        : base("Build Geometry Collection", "Build Collection", "Convert Rhino geometry and its tree to an Alidade collection.",
            "Alidade", "Convert") { }

    public override Guid ComponentGuid => new("61799db7-0931-4460-b71c-a22097849a68");
    protected override System.Drawing.Bitmap Icon => ComponentIcons.Load("BuildCollection");

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddGenericParameter("Geometry", "G", "Rhino geometry data tree.", GH_ParamAccess.tree);
        manager.AddTextParameter(CollectionOutputs.GeometryIdsName, "K", GeometryIdsDescription, GH_ParamAccess.tree);
        manager.AddTextParameter("Source units", "U", "Rhino unit name or mm, cm, m, in or ft. Uses the Rhino document when omitted.",
            GH_ParamAccess.item);
        manager.AddTextParameter(CollectionOutputs.GeometryNamesName, "N",
            "Display names at the same paths and slots as Geometry. Omitted names default to the Geometry ID in Torquetum.", GH_ParamAccess.tree);
        for (var i = 0; i < 4; i++)
            manager[i].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddParameter(new AlidadeCollectionParameter());
        manager.AddTextParameter("Diagnostics", "D", "Conversion messages at the original paths and item indices.", GH_ParamAccess.tree);
    }

    public override bool Read(GH_IReader reader)
    {
        ComponentArchive.RestoreAppendedParameter(reader, Params.Input[3], 3, input: true);
        return CollectionOutputs.ReadGeometryIds(base.Read(reader), Params.Input[1], GeometryIdsDescription);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var diagnostics = new List<CollectionDiagnostic>();
        try
        {
            data.GetDataTree<IGH_Goo>(0, out var geometry);
            if (Params.Input[0].SourceCount == 0 && (geometry == null || geometry.PathCount == 0))
                throw CollectionFormatException.At("GEOMETRY_MISSING", "Connect a Rhino geometry tree.");
            string? units = null;
            data.GetData(2, ref units);
            var document = OnPingDocument()?.RhinoDocument;
            var scale = GeometryCollectionConverter.MetresPerUnit(units, document);
            var collection = GeometryCollectionConverter.Build(geometry ?? new GH_Structure<IGH_Goo>(),
                OptionalTextTree(data, 1), OptionalTextTree(data, 3), scale, diagnostics, document);
            if (collection != null)
                data.SetData(0, new AlidadeCollection(collection));
        }
        catch (CollectionFormatException error)
        {
            diagnostics.Add(error.Diagnostic);
        }
        data.DisableGapLogic();
        CollectionOutputs.WriteDiagnostics(this, data, 1, diagnostics);
    }

    private GH_Structure<GH_String>? OptionalTextTree(IGH_DataAccess data, int index)
    {
        data.GetDataTree<GH_String>(index, out var tree);
        if (tree is { PathCount: > 0 }) return tree;
        return Params.Input[index].SourceCount == 0 ? null : new GH_Structure<GH_String>();
    }
}

public sealed class UnpackGeometryCollection : GH_Component
{
    private const string GeometryIdsDescription = "Persistent IDs aligned with the returned Geometry tree.";

    public UnpackGeometryCollection()
        : base("Unpack Geometry Collection", "Unpack Collection", "Convert an Alidade collection back to Rhino geometry and its tree.",
            "Alidade", "Convert") { }

    public override Guid ComponentGuid => new("41b84810-df87-4080-b29b-b731a9d53196");
    protected override System.Drawing.Bitmap Icon => ComponentIcons.Load("UnpackCollection");

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddParameter(new AlidadeCollectionParameter());
        manager.AddTextParameter("Target units", "U", "Rhino unit name or mm, cm, m, in or ft. Uses the Rhino document when omitted.",
            GH_ParamAccess.item);
        manager[1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddGenericParameter("Geometry", "G", "Rhino geometry with the original branch paths and slots.", GH_ParamAccess.tree);
        manager.AddTextParameter(CollectionOutputs.GeometryIdsName, "K", GeometryIdsDescription, GH_ParamAccess.tree);
        manager.AddTextParameter("Diagnostics", "D", "Conversion messages at the original paths and item indices.", GH_ParamAccess.tree);
        manager.AddTextParameter(CollectionOutputs.GeometryNamesName, "N", "Display names aligned with the returned Geometry tree.", GH_ParamAccess.tree);
    }

    public override bool Read(GH_IReader reader)
    {
        ComponentArchive.RestoreAppendedParameter(reader, Params.Output[3], 3, input: false);
        return CollectionOutputs.ReadGeometryIds(base.Read(reader), Params.Output[1], GeometryIdsDescription);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        AlidadeCollection? input = null;
        if (!data.GetData(0, ref input) || input?.Value is not { } collection)
            return;

        var diagnostics = new List<CollectionDiagnostic>();
        data.DisableGapLogic();
        try
        {
            string? units = null;
            data.GetData(1, ref units);
            var document = OnPingDocument()?.RhinoDocument;
            var scale = GeometryCollectionConverter.MetresPerUnit(units, document);
            var unpacked = GeometryCollectionConverter.Unpack(collection, 1 / scale, diagnostics, document);
            if (unpacked is { } trees)
            {
                // The parameter types match these structures. Copy them without Grasshopper re-matching empty slots.
                data.BlitData(0, trees.Geometry, overwrite: true);
                data.BlitData(1, trees.Keys, overwrite: true);
                data.BlitData(3, trees.Names, overwrite: true);
            }
        }
        catch (CollectionFormatException error)
        {
            diagnostics.Add(error.Diagnostic);
        }
        CollectionOutputs.WriteDiagnostics(this, data, 2, diagnostics);
    }
}
