using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using GH_IO.Serialization;
using EtoApplication = Eto.Forms.Application;

namespace Torquetum.Alidade;

public abstract class AlidadeAsyncComponent : GH_Component
{
    private CancellationTokenSource? _operation;

    protected AlidadeAsyncComponent(string name, string nickname, string description, string category)
        : base(name, nickname, description, "Alidade", category) { }

    protected bool IsPending => _operation != null;
    private protected AlidadeConnectionException? Failure { get; private set; }
    protected bool CanUse([NotNullWhen(true)] GH_Document? document) => document?.Enabled == true && !Locked;

    protected bool CanUseProject([NotNullWhen(true)] GH_Document? document,
        [NotNullWhen(true)] ProjectSession? project) => CanUse(document) && project?.IsValidFor(document.RuntimeID) == true;

    protected T? SingleInput<T>(IGH_DataAccess data, int index) where T : class
    {
        T? value = null;
        return Params.Input[index].VolatileDataCount == 1 && data.GetData(index, ref value) ? value : null;
    }

    protected ProjectSession? ConnectedProject(IGH_DataAccess data, GH_Document? document) =>
        SingleInput<AlidadeProject>(data, 0)?.Value is { } session && CanUseProject(document, session) ? session : null;

    protected GeometryPortReference? ConnectedPort(IGH_DataAccess data, int index, ProjectSession project, string direction) =>
        SingleInput<AlidadePortReference>(data, index)?.Value is { } port && port.Matches(project, direction) ? port : null;

    protected void ReportStatus(IGH_DataAccess data, int index, string status, string? warning)
    {
        Message = warning == status ? "" : status;
        data.SetData(index, status);
        if (warning != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);
    }

    internal void StartRequest<T>(GH_Document document, ProjectSession? project, Func<CancellationToken, Task<T>> request,
        Action<T> accept, Action<AlidadeConnectionException>? fail = null, Action<T>? abandon = null)
    {
        CancelRequest();
        var operation = new CancellationTokenSource();
        _operation = operation;
        if (document.SolutionDepth == 0) RequestSolution(document);
        _ = CompleteAsync(document, project, operation, request, accept, fail, abandon);
    }

    private async Task CompleteAsync<T>(GH_Document document, ProjectSession? project, CancellationTokenSource operation,
        Func<CancellationToken, Task<T>> request, Action<T> accept, Action<AlidadeConnectionException>? fail, Action<T>? abandon)
    {
        T result = default!;
        AlidadeConnectionException? error = null;
        try { result = await request(operation.Token).ConfigureAwait(false); }
        catch (Exception failure) when (failure is AlidadeConnectionException or HttpRequestException
            or OperationCanceledException or CollectionFormatException or JsonException)
        {
            error = failure as AlidadeConnectionException ?? new AlidadeConnectionException(failure switch
            {
                HttpRequestException => "Torquetum is unavailable. Check the running project and try again.",
                OperationCanceledException => "Torquetum did not answer in time. Check the request before trying again.",
                CollectionFormatException invalid => invalid.Message,
                _ => "Torquetum returned invalid exchange data.",
            });
        }

        EtoApplication.Instance.AsyncInvoke(() =>
        {
            var current = ReferenceEquals(_operation, operation);
            if (current) _operation = null;
            operation.Dispose();
            if (!current || !ReferenceEquals(OnPingDocument(), document))
            {
                if (error == null) abandon?.Invoke(result);
                return;
            }

            if (error == null && project != null && !project.IsValidFor(document.RuntimeID))
                error = new AlidadeConnectionException("Pair with the project again.", pairingRequired: true);
            if (error == null)
                accept(result);
            else
            {
                if (error.PairingRequired)
                    project?.Disconnect();
                Failure = error;
                fail?.Invoke(error);
            }
            RequestSolution(document);
        });
    }

    protected void CancelRequest()
    {
        var operation = _operation;
        _operation = null;
        Failure = null;
        operation?.Cancel();
    }

    protected void RequestSolution(GH_Document document)
    {
        if (document.SolutionDepth > 0)
        {
            document.SolutionEnd -= OnSolutionEnd;
            document.SolutionEnd += OnSolutionEnd;
            return;
        }
        ExpireSolution(false);
        document.ScheduleSolution(1);
    }

    private void OnSolutionEnd(object sender, GH_SolutionEventArgs args)
    {
        var document = args.Document;
        document.SolutionEnd -= OnSolutionEnd;
        EtoApplication.Instance.AsyncInvoke(() =>
        {
            if (ReferenceEquals(OnPingDocument(), document))
                RequestSolution(document);
        });
    }

    protected abstract void ResetState();

    private void InvalidateDocument(GH_Document? document)
    {
        if (document != null)
            document.SolutionEnd -= OnSolutionEnd;
        CancelRequest();
        ResetState();
    }

    public override bool Read(GH_IReader reader)
    {
        InvalidateDocument(OnPingDocument());
        return base.Read(reader);
    }

    public override void RemovedFromDocument(GH_Document document)
    {
        InvalidateDocument(document);
        base.RemovedFromDocument(document);
    }

    public override void MovedBetweenDocuments(GH_Document oldDocument, GH_Document newDocument)
    {
        InvalidateDocument(oldDocument);
        base.MovedBetweenDocuments(oldDocument, newDocument);
    }

    public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
    {
        if (context == GH_DocumentContext.Close)
            InvalidateDocument(document);
        base.DocumentContextChanged(document, context);
    }
}

internal static class ComponentArchive
{
    internal static void RestoreAppendedParameter(GH_IReader reader, IGH_Param parameter, int index, bool input)
    {
        // Grasshopper treats missing parameter chunks as corruption, including ports added after this archive.
        var name = input ? "param_input" : "param_output";
        if (reader.FindChunk(name, index) == null && reader is GH_IWriter archive)
            parameter.Write(archive.CreateChunk(name, index));
    }
}
