using System;
using System.Net;
using System.Windows.Forms;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using GH_IO.Serialization;

namespace Torquetum.Alidade;

public sealed class SendGeometry : AlidadeAsyncComponent
{
    private sealed record Inputs(ProjectSession Project, GeometryCollection Collection, GeometryPortReference Target);

    private sealed class Attempt
    {
        internal required ProjectSession Project { get; init; }
        internal required GeometryPortReference Target { get; init; }
        internal required WeakReference<GeometryCollection> Collection { get; init; }
        internal required Guid Id { get; init; }
        internal GeometryWriteRequest? Request { get; set; }
        internal GeometryReceipt? Receipt { get; set; }
        internal DateTimeOffset? RetryAfter { get; set; }
        internal bool CanStartAnother { get; set; }
    }

    private ProjectSession? _project;
    private Inputs? _inputs;
    private Attempt? _attempt;
    private string? _prepareError;
    private bool _triggerHigh = true;

    public SendGeometry()
        : base("Send Geometry", "Send Geometry", "Send a Geometry Collection to an Asterism input on demand.", "Exchange") { }

    public override Guid ComponentGuid => new("9d700ca8-b14a-4b59-b587-4d86490a705f");
    protected override System.Drawing.Bitmap Icon => ComponentIcons.Load("SendGeometry");

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddParameter(new AlidadeProjectParameter());
        manager.AddParameter(new AlidadeCollectionParameter());
        manager.AddParameter(new AlidadePortReferenceParameter(), "Target", "T", "Asterism input to update.", GH_ParamAccess.item);
        manager.AddBooleanParameter("Send", "Send", "Connect a Grasshopper Button to send once per press.", GH_ParamAccess.item);
        for (var i = 0; i < 4; i++)
            manager[i].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddParameter(new AlidadeWriteReceiptParameter());
        manager.AddParameter(new AlidadePortReferenceParameter());
        manager.AddTextParameter("Status", "S", "Send status for the current geometry and target.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var document = OnPingDocument();
        _project = ConnectedProject(data, document);
        if (_attempt is { } old && _project != null && !ReferenceEquals(old.Project, _project))
        {
            CancelRequest();
            _attempt = null;
        }
        var (next, blocked) = Capture(data, _project);
        if (_inputs != next) _prepareError = null;
        _inputs = next;
        if (Params.Input[3].SourceCount > 0 && SingleInput<GH_Boolean>(data, 3) is { } trigger)
        {
            var pressed = trigger.Value && !_triggerHigh;
            _triggerHigh = trigger.Value;
            if (pressed) Begin();
        }
        else
            _triggerHigh = true;

        var error = _prepareError ?? (next != null ? blocked : null);
        var status = error ?? blocked ?? "Ready";
        if (_attempt is { } attempt && ReferenceEquals(_project, attempt.Project))
        {
            if (next is { } current && blocked == null)
            {
                if (MatchesCurrent(attempt, current))
                {
                    error ??= Failure?.Message ?? (attempt.Receipt is { State: "rejected" } rejected ? rejected.Message : null);
                    status = IsPending ? "Sending…" : error ?? (attempt.Receipt?.State == "committed" ? "Sent" : "Check result");
                }
                else if (IsPending || !attempt.CanStartAnother)
                    status = "Check previous send";
            }

            if (attempt.Receipt is { } receipt)
            {
                data.SetData(0, new AlidadeWriteReceipt(receipt));
                if (receipt is { State: "committed", ETag: { } tag })
                    data.SetData(1, new AlidadePortReference(attempt.Target with { ETag = tag }));
            }
        }
        ReportStatus(data, 2, status, error);
    }

    private static bool MatchesCurrent(Attempt attempt, Inputs current) => attempt.Collection.TryGetTarget(out var collection)
        && ReferenceEquals(collection, current.Collection) && attempt.Target.PortId == current.Target.PortId
        && (attempt.Target.ETag == current.Target.ETag || attempt.Receipt?.ETag == current.Target.ETag);

    private (Inputs? Inputs, string? Blocked) Capture(IGH_DataAccess data, ProjectSession? project)
    {
        if (project == null)
            return (null, "Connect Project");
        if (SingleInput<AlidadeCollection>(data, 1)?.Value is not { GeometryCount: > 0 } collection)
            return (null, "Connect Geometry Collection");
        if (ConnectedPort(data, 2, project, "input") is not { } reference)
            return (null, "Choose Input");
        var input = new Inputs(project, collection, reference);
        return (input, Unavailable(input));
    }

    private static string? Unavailable(Inputs input)
    {
        var project = input.Project.Status;
        var code = project.CanWrite ? project.CollectionWriteError(input.Collection.SchemaVersion, input.Collection.HasNames)
            : project.AvailabilityReason ?? "READ_ONLY";
        return code == null ? null : AlidadeClient.ErrorMessage(code);
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        var document = OnPingDocument();
        var enabled = !IsPending && CanUseProject(document, _project);
        Menu_AppendItem(menu, "Send", (_, _) => Begin(), enabled && CanStart());
        Menu_AppendItem(menu, "Check result", (_, _) => Continue(check: true), enabled && CanContinue(check: true));
        Menu_AppendItem(menu, "Retry same request", (_, _) => Continue(check: false), enabled && CanContinue(check: false));
    }

    private bool CanStart() => _inputs is { } input && Unavailable(input) == null
        && (_attempt == null || _attempt.CanStartAnother);

    private bool CanContinue(bool check) =>
        _attempt is { } attempt && ReferenceEquals(_project, attempt.Project)
        && (attempt.RetryAfter == null || attempt.RetryAfter <= DateTimeOffset.UtcNow)
        && (check || attempt.Request != null);

    private void Begin()
    {
        var document = OnPingDocument();
        if (IsPending || !CanStart() || _inputs is not { } input || !CanUseProject(document, input.Project))
            return;

        try
        {
            var target = input.Target;
            if (_attempt is { Receipt: { State: "committed", ETag: { } tag } } previous && previous.Target == target)
                target = target with { ETag = tag };
            var request = AlidadeClient.PrepareWrite(input.Collection, target);
            _attempt = new Attempt
            {
                Project = input.Project, Target = input.Target, Collection = new(input.Collection),
                Id = request.Id, Request = request,
            };
            Execute(document, _attempt, check: false);
        }
        catch (AlidadeConnectionException error)
        {
            _prepareError = error.Message;
            RequestSolution(document);
        }
    }

    private void Continue(bool check)
    {
        var document = OnPingDocument();
        if (IsPending || !CanContinue(check) || _attempt is not { } attempt
            || !CanUseProject(document, attempt.Project))
            return;

        Execute(document, attempt, check);
    }

    private void Execute(GH_Document document, Attempt attempt, bool check)
    {
        var project = attempt.Project;
        var request = attempt.Request;
        if (!check) attempt.CanStartAnother = false; // Retried writes can be admitted even if the original was not.
        _prepareError = null;
        StartRequest(document, project, cancellation => check
                ? AlidadeClient.CheckReceiptAsync(project, document.RuntimeID, attempt.Id, attempt.Target.PortId, cancellation)
                : AlidadeClient.WriteGeometryAsync(project, document.RuntimeID, request!, cancellation),
            receipt =>
            {
                attempt.Receipt = receipt;
                attempt.RetryAfter = receipt.RetryAfter;
                attempt.CanStartAnother = receipt.State != "pending";
                if (attempt.CanStartAnother) attempt.Request = null;
            },
            error => RecordFailure(attempt, error, check));
    }

    private static void RecordFailure(Attempt attempt, AlidadeConnectionException error, bool checking)
    {
        attempt.RetryAfter = error.RetryAfter;
        if (checking)
        {
            if (error.Code == "REQUEST_UNKNOWN")
                attempt.CanStartAnother = true;
            return;
        }

        if (error.Code is "PROJECT_BUSY" or "CAPACITY_EXCEEDED" or "PROJECT_SYNC_PENDING")
        {
            // These replies are sent before a write is admitted. Its immutable envelope can be retried.
            attempt.CanStartAnother = true;
        }
        else if (error.Code != "REQUEST_ID_CONFLICT" && error.HttpStatus is { } status
            && status >= HttpStatusCode.BadRequest && (int)status < 500)
        {
            attempt.CanStartAnother = true;
            attempt.Request = null;
        }
    }

    protected override void ResetState()
    {
        _project = null;
        _inputs = null;
        _attempt = null;
        _prepareError = null;
        _triggerHigh = true;
    }

    public override bool Read(GH_IReader reader)
    {
        ComponentArchive.RestoreAppendedParameter(reader, Params.Input[3], 3, input: true);
        return base.Read(reader);
    }
}
