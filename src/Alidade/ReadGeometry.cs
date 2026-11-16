using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Grasshopper.Kernel;
using GH_IO.Serialization;

namespace Torquetum.Alidade;

public sealed class ReadGeometry : AlidadeAsyncComponent
{
    private sealed record Input(ProjectSession Project, GeometryPortReference Target, GeometryReceipt? Receipt);

    private Input? _input;
    private GeometryCollection? _collection;

    public ReadGeometry()
        : base("Read Geometry", "Read Geometry", "Read a Geometry Collection from an Asterism output.", "Exchange") { }

    public override Guid ComponentGuid => new("aecf9292-0da8-4783-9ca6-2aeedf326acc");
    protected override System.Drawing.Bitmap Icon => ComponentIcons.Load("ReadGeometry");

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddParameter(new AlidadeProjectParameter());
        manager.AddParameter(new AlidadePortReferenceParameter());
        manager.AddParameter(new AlidadeWriteReceiptParameter());
        manager[0].Optional = manager[1].Optional = manager[2].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddParameter(new AlidadeCollectionParameter());
        manager.AddParameter(new AlidadePortReferenceParameter());
        manager.AddTextParameter("Status", "S", "Latest read status.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var document = OnPingDocument();
        var project = ConnectedProject(data, document);
        var (next, unavailable) = Capture(data, project);
        if (_input != next)
        {
            _input = next;
            Refresh();
        }

        if (_collection is { } current && _input is { } input)
        {
            data.SetData(0, new AlidadeCollection(current));
            data.SetData(1, new AlidadePortReference(input.Target));
        }
        var error = Failure?.Message;
        var status = error ?? (IsPending ? "Reading…" : unavailable);
        if (_collection != null && _input is { } completed)
            status = completed.Receipt?.RequestId is { } id
                ? $"Read {id.ToString("D")[^8..]}"
                : completed.Target.DisplayName;
        ReportStatus(data, 2, status, error);
    }

    private (Input?, string) Capture(IGH_DataAccess data, ProjectSession? project)
    {
        if (project == null)
            return (null, "Connect Project");
        if (ConnectedPort(data, 1, project, "output") is not { } target)
            return (null, "Choose Output");
        if (Params.Input[2].SourceCount == 0)
            return (new Input(project, target, null), target.DisplayName);
        if (SingleInput<AlidadeWriteReceipt>(data, 2)?.Value is not { } receipt)
            return (null, "Waiting for receipt");
        if (!receipt.Matches(project))
            return (null, "This receipt belongs to another project connection.");
        return receipt.State switch
        {
            "committed" => (new Input(project, target, receipt), target.DisplayName),
            "pending" => (null, "Waiting for Input"),
            _ => (null, "Input write rejected"),
        };
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        var document = OnPingDocument();
        Menu_AppendItem(menu, "Refresh read", (_, _) => Refresh(), !IsPending && CanUseProject(document, _input?.Project));
    }

    private void Refresh()
    {
        var document = OnPingDocument();
        _collection = null;
        if (_input is { } input && CanUseProject(document, input.Project))
            StartRequest(document, input.Project, cancellation => ReadOutputAsync(input, document.RuntimeID, cancellation),
                result => _collection = result);
        else CancelRequest();
    }

    private static async Task<GeometryCollection> ReadOutputAsync(Input input, ulong documentId, CancellationToken cancellation)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return await AlidadeClient.ReadGeometryAsync(input.Project, documentId, input.Target, cancellation, input.Receipt)
                    .ConfigureAwait(false);
            }
            catch (AlidadeConnectionException error) when (input.Receipt != null && error.Code == "OUTPUT_NOT_READY"
                && Stopwatch.GetElapsedTime(started).TotalSeconds < 15)
            {
                await Task.Delay(500, cancellation).ConfigureAwait(false);
            }
        }
    }

    protected override void ResetState()
    {
        _input = null;
        _collection = null;
    }

    public override bool Read(GH_IReader reader)
    {
        ComponentArchive.RestoreAppendedParameter(reader, Params.Input[2], 2, input: true);
        return base.Read(reader);
    }
}
