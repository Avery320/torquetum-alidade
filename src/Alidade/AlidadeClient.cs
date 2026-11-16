using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Torquetum.Alidade;

internal static partial class AlidadeClient
{
    private const string InvitationPrefix = "ALIDADE1.";
    private const int ApiVersion = 1;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = GeometryCollection.MaxExchangeBytes,
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static bool HasInvitationPrefix(string code) => code.StartsWith(InvitationPrefix, StringComparison.Ordinal);

    public static async Task<ProjectSession> PairAsync(string code, ulong documentId, CancellationToken cancellation)
    {
        try
        {
            var invitation = ReadInvitation(code);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(invitation.Endpoint, "/v1/pair"));
            request.Content = JsonContent.Create(new
            {
                invite = invitation.Invite,
                clientName = "Alidade for Grasshopper",
                apiVersion = ApiVersion,
                collectionVersions = new[] { 1, 2 },
                geometryNames = true,
            });
            var grant = await SendAsync<Grant>(request, cancellation).ConfigureAwait(false);
            if (grant.InstanceId != invitation.InstanceId ||
                ReadInstanceId(grant.AccessToken) != invitation.InstanceId ||
                grant.GrantId == Guid.Empty || grant.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new AlidadeConnectionException("Torquetum returned an invalid pairing response.");
            return new ProjectSession(invitation.Endpoint, documentId, grant.Status(), grant.AccessToken, grant.ExpiresAt);
        }
        catch (JsonException)
        {
            throw new AlidadeConnectionException("Torquetum returned an invalid pairing response.");
        }
    }

    internal static async Task<ProjectStatus> ReadProjectAsync(ProjectSession project, ulong documentId, CancellationToken cancellation)
    {
        try
        {
            using var request = project.Request(documentId, HttpMethod.Get, "/v1/project");
            var status = (await SendAsync<ProjectResponse>(request, cancellation).ConfigureAwait(false)).Status();
            if (!project.Status.Matches(status.ProjectId, status.InstanceId))
                throw new AlidadeConnectionException("The Torquetum project changed. Pair again.", pairingRequired: true);
            return status;
        }
        catch (JsonException)
        {
            throw new AlidadeConnectionException("Torquetum returned invalid project information.");
        }
    }

    private static async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellation)
    {
        using var response = await SendForStatusAsync(request, HttpStatusCode.OK, cancellation).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellation).ConfigureAwait(false);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellation)
    {
        using var payload = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(payload, JsonOptions, cancellation).ConfigureAwait(false)
            ?? throw new JsonException();
    }

    private static async Task<HttpResponseMessage> SendForStatusAsync(
        HttpRequestMessage request, HttpStatusCode expected, CancellationToken cancellation, int timeoutSeconds = 15)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var response = await Http.SendAsync(request, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode == expected)
            return response;

        using (response)
        {
            ApiError? error;
            try { error = await ReadJsonAsync<ApiError>(response, cancellation).ConfigureAwait(false); }
            catch (JsonException) { error = null; }
            throw ErrorException(error?.Error, response.StatusCode, RetryAfter(response));
        }
    }

    private static DateTimeOffset? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { } delta ? DateTimeOffset.UtcNow + delta : response.Headers.RetryAfter?.Date;

    private static AlidadeConnectionException ErrorException(ErrorDetails? error, HttpStatusCode status,
        DateTimeOffset? retryAfter = null)
    {
        var message = ErrorMessage(error?.Code, error?.Message);
        if (error?.Path is { Length: > 0 } path)
            message += $" Branch {{{string.Join(";", path)}}}" +
                (error.ItemIndex is { } index ? $", item {index}." : ".");
        return new AlidadeConnectionException(message, pairingRequired: status == HttpStatusCode.Unauthorized,
            code: error?.Code, httpStatus: status, retryAfter: retryAfter);
    }

    internal static string ErrorMessage(string? code, string? serverMessage = null) => code switch
    {
        "INVITE_INVALID" => "The invitation is unavailable. Copy a new invitation from Torquetum.",
        "PAIRING_REQUIRED" => "Pair with the project again.",
        "PORT_NOT_FOUND" => "The Asterism port is unavailable or has the wrong direction. Refresh the port list.",
        "STALE_REVISION" => "The Asterism input has changed. Refresh the port list, then choose Send again.",
        "REQUEST_UNKNOWN" => "Torquetum has no receipt for this request. You can retry the same request.",
        "REQUEST_ID_CONFLICT" => "There is already a different request with this ID. Check its result before sending again.",
        _ when serverMessage is { Length: > 0 and <= 1024 } && !string.IsNullOrWhiteSpace(serverMessage) => serverMessage,
        "PROJECT_BUSY" => "The Torquetum project is busy. Try again when it is ready.",
        "READ_ONLY" => "This project currently accepts only read access.",
        "PROJECT_SYNC_PENDING" => "Wait for the Torquetum project to finish synchronizing.",
        "STEP_UNSUPPORTED" => "The paired project does not offer precise STEP exchange.",
        "GEOMETRY_NAMES_UNSUPPORTED" => "Update Torquetum to send Geometry Names to this project.",
        "PRECONDITION_REQUIRED" => "Refresh the Asterism input reference before sending.",
        "CONTENT_TOO_LARGE" => "The collection exceeds the Torquetum transfer limit.",
        _ => "Torquetum rejected the request.",
    };

    private static (Uri Endpoint, string Invite, Guid InstanceId) ReadInvitation(string code)
    {
        code = code.Trim();
        if (code.Length <= 4096 && HasInvitationPrefix(code))
        {
            try
            {
                var input = JsonSerializer.Deserialize<Invitation>(DecodeBase64Url(code[InvitationPrefix.Length..]), JsonOptions);
                if (input != null && Uri.TryCreate(input.Endpoint, UriKind.Absolute, out var endpoint) &&
                    endpoint.Port > 0 && input.Endpoint == $"http://127.0.0.1:{endpoint.Port}" &&
                    ReadInstanceId(input.Invite) is { } instanceId)
                    return (endpoint, input.Invite, instanceId);
            }
            catch (FormatException) { }
            catch (JsonException) { }
        }
        throw new AlidadeConnectionException("Copy a complete Alidade pairing code from Torquetum Project Settings.", code: "INVITE_INVALID");
    }

    private static Guid? ReadInstanceId(string? credential)
    {
        if (credential is not { Length: 80 } || credential[36] != '.' ||
            !Guid.TryParseExact(credential.AsSpan(0, 36), "D", out var instance) || instance == Guid.Empty)
            return null;
        try
        {
            return DecodeBase64Url(credential[37..]).Length == 32 ? instance : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        foreach (var character in value)
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                throw new FormatException();
        return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/')
            .PadRight((value.Length + 3) & ~3, '='));
    }

    private sealed class Invitation
    {
        public required string Endpoint { get; init; }
        public required string Invite { get; init; }
    }

    private class ProjectResponse
    {
        public required string ProjectId { get; init; }
        public required Guid InstanceId { get; init; }
        public required string ProjectName { get; init; }
        public required int ApiVersion { get; init; }
        public required int[] CollectionVersions { get; init; }
        public required bool CanRead { get; init; }
        public required bool CanWrite { get; init; }
        public bool GeometryNames { get; init; }
        public string[]? Kinds { get; init; }
        public ProjectLimits? Limits { get; init; }
        public string? AvailabilityReason { get; init; }

        public ProjectStatus Status()
        {
            if (string.IsNullOrWhiteSpace(ProjectId) || InstanceId == Guid.Empty ||
                string.IsNullOrWhiteSpace(ProjectName) || ApiVersion != AlidadeClient.ApiVersion ||
                CollectionVersions == null || !Array.Exists(CollectionVersions, version => version == 1) ||
                (CanWrite && !CanRead))
                throw new JsonException();
            var step = Array.Exists(CollectionVersions, version => version == 2)
                && Kinds != null && Array.Exists(Kinds, kind => kind == "brep-step");
            var bytes = Limits?.MaxBodyBytes is > 0 and var advertised
                ? Math.Min(advertised, GeometryCollection.MaxExchangeBytes)
                : step ? GeometryCollection.MaxExchangeBytes : 8 * 1024 * 1024;
            return new ProjectStatus(ProjectId, InstanceId, ProjectName, CanRead, CanWrite, AvailabilityReason, step, GeometryNames, bytes);
        }
    }

    private sealed class ProjectLimits
    {
        public int MaxBodyBytes { get; init; }
    }

    private sealed class Grant : ProjectResponse
    {
        public required Guid GrantId { get; init; }
        public required string AccessToken { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
    }

    private sealed class ApiError
    {
        public ErrorDetails? Error { get; init; }
    }

    private sealed class ErrorDetails
    {
        public string? Code { get; init; }
        public string? Message { get; init; }
        public int[]? Path { get; init; }
        public int? ItemIndex { get; init; }
    }
}

internal sealed class AlidadeConnectionException : Exception
{
    public AlidadeConnectionException(string message, bool pairingRequired = false, string? code = null,
        HttpStatusCode? httpStatus = null, DateTimeOffset? retryAfter = null) : base(message)
    {
        PairingRequired = pairingRequired;
        Code = code;
        HttpStatus = httpStatus;
        RetryAfter = retryAfter;
    }

    public bool PairingRequired { get; }
    public string? Code { get; }
    public HttpStatusCode? HttpStatus { get; }
    public DateTimeOffset? RetryAfter { get; }
}
