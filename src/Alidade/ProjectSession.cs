using System;
using System.Net.Http;
using System.Net.Http.Headers;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace Torquetum.Alidade;

internal sealed record ProjectStatus(
    string ProjectId, Guid InstanceId, string ProjectName, bool CanRead, bool CanWrite,
    string? AvailabilityReason, bool SupportsStep, bool GeometryNames, int MaxBodyBytes)
{
    internal bool SupportsCollection(int version) => version == 1 || (version == 2 && SupportsStep);
    internal string? CollectionWriteError(int version, bool hasNames) => !SupportsCollection(version) ? "STEP_UNSUPPORTED"
        : hasNames && !GeometryNames ? "GEOMETRY_NAMES_UNSUPPORTED" : null;
    internal bool Matches(string projectId, Guid instanceId) => ProjectId == projectId && InstanceId == instanceId;
}

public sealed class ProjectSession
{
    private readonly Uri _endpoint;
    private readonly ulong _documentId;
    private readonly DateTimeOffset _expiresAt;
    private volatile string? _accessToken;

    internal ProjectSession(Uri endpoint, ulong documentId, ProjectStatus status, string accessToken, DateTimeOffset expiresAt)
    {
        _endpoint = endpoint;
        _documentId = documentId;
        _expiresAt = expiresAt;
        _accessToken = accessToken;
        Status = status;
    }

    internal ProjectStatus Status { get; set; }

    internal bool IsValid => _accessToken != null && DateTimeOffset.UtcNow < _expiresAt;

    internal bool IsValidFor(ulong documentId) => IsValid && documentId == _documentId;

    internal void Disconnect() => _accessToken = null;

    internal HttpRequestMessage Request(ulong documentId, HttpMethod method, string path)
    {
        if (!IsValidFor(documentId) || _accessToken is not { } token)
            throw new AlidadeConnectionException("Pair with the project again.", pairingRequired: true);
        var request = new HttpRequestMessage(method, new Uri(_endpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}

public sealed class AlidadeProject : AlidadeLiveValue<ProjectSession>
{
    public AlidadeProject() { }
    internal AlidadeProject(ProjectSession? session) : base(session) { }

    public override bool IsValid => Value?.IsValid == true;
    public override string TypeName => "Alidade Project";
    public override string TypeDescription => "A live connection to a Torquetum project.";
    public override IGH_Goo Duplicate() => new AlidadeProject(Value);
    public override object ScriptVariable() => ToString();
    public override string ToString() => IsValid && Value is { } session ? session.Status.ProjectName : "Disconnected Alidade project";
}

public sealed class AlidadeProjectParameter : GH_Param<AlidadeProject>
{
    public AlidadeProjectParameter()
        : base("Project", "P", "A live Torquetum project connection.", "Alidade", "Session", GH_ParamAccess.item) { }

    public override Guid ComponentGuid => new("6300cb1e-3e9b-427e-b3d1-c39fed8ee509");
    public override GH_Exposure Exposure => GH_Exposure.hidden;
}
