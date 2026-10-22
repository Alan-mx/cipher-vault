using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CipherVault.Host.Security;

public enum ApprovalOperationKind
{
    PurgeNote,
    CreateBackup,
    RestoreBackup,
    ExportPlaintext,
    PreviewImport,
    CommitImport
}

public enum ApprovalState
{
    Pending,
    Approved,
    Denied,
    Expired,
    Consumed,
    Revoked
}

public sealed record PreparedOperation(
    Guid OperationId,
    ApprovalOperationKind Kind,
    Guid ProcessSessionId,
    Guid? VaultSessionId,
    string CanonicalParametersJson,
    string ParametersHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ApprovalState State);

public sealed record HostApprovalRequest(
    Guid ApprovalId,
    string NonceB64Url,
    Guid OperationId,
    ApprovalOperationKind Kind,
    Guid ProcessSessionId,
    string ParametersHash,
    string DisplayTitle,
    string DisplayDetails,
    Guid? VaultSessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ApprovalState State);

public sealed record HostApprovalDecision(
    Guid ApprovalId,
    string NonceB64Url,
    bool Approved,
    Guid BrokerProcessSessionId,
    DateTimeOffset DecidedAt,
    string? DenialReason);

public enum ApprovalConsumeStatus
{
    Consumed,
    NotFound,
    NotApproved,
    Expired,
    ParameterMismatch,
    IdentityMismatch
}

public sealed record ApprovalConsumeResult(
    ApprovalConsumeStatus Status,
    PreparedOperation? Operation,
    string? ReasonCode);

public interface IApprovalPrompt
{
    Task<HostApprovalDecision> RequestAsync(
        HostApprovalRequest request,
        CancellationToken cancellationToken = default);
}

public static class CanonicalJson
{
    public static string Normalize(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Indented = false }))
        {
            Write(writer, value);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string ComputeSha256(JsonElement value)
    {
        var normalized = Normalize(value);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(
                                 item => item.Name,
                                 StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    Write(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException(
                    "Unsupported JSON value kind.",
                    nameof(value));
        }
    }
}

public sealed class ApprovalStore
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;
    private readonly Dictionary<Guid, PreparedOperation> _operations = [];
    private readonly Dictionary<Guid, HostApprovalRequest> _approvals = [];

    public ApprovalStore(
        TimeProvider? timeProvider = null,
        TimeSpan? lifetime = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetime = lifetime ?? DefaultLifetime;
        if (_lifetime <= TimeSpan.Zero || _lifetime > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }
    }

    public PreparedOperation Prepare(
        ApprovalOperationKind kind,
        Guid processSessionId,
        Guid? vaultSessionId,
        JsonElement parameters)
    {
        if (processSessionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Approval requires a process session.",
                nameof(processSessionId));
        }
        var now = _timeProvider.GetUtcNow();
        var operation = new PreparedOperation(
            Guid.NewGuid(),
            kind,
            processSessionId,
            vaultSessionId,
            CanonicalJson.Normalize(parameters),
            CanonicalJson.ComputeSha256(parameters),
            now,
            now + _lifetime,
            ApprovalState.Pending);
        lock (_sync)
        {
            PruneExpired(now);
            _operations[operation.OperationId] = operation;
        }
        return operation;
    }

    public HostApprovalRequest CreateRequest(
        PreparedOperation operation,
        string displayTitle,
        string displayDetails)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayDetails);
        lock (_sync)
        {
            if (!_operations.TryGetValue(operation.OperationId, out var current) ||
                current.State != ApprovalState.Pending ||
                current.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                throw new InvalidOperationException(
                    "Prepared operation is no longer pending.");
            }
            var request = new HostApprovalRequest(
                Guid.NewGuid(),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_'),
                current.OperationId,
                current.Kind,
                current.ProcessSessionId,
                current.ParametersHash,
                displayTitle,
                displayDetails,
                current.VaultSessionId,
                current.CreatedAt,
                current.ExpiresAt,
                ApprovalState.Pending);
            _approvals[request.ApprovalId] = request;
            return request;
        }
    }

    public HostApprovalDecision Decide(
        HostApprovalRequest request,
        bool approved,
        Guid brokerProcessSessionId,
        string? denialReason = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (brokerProcessSessionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Broker process session is required.",
                nameof(brokerProcessSessionId));
        }
        lock (_sync)
        {
            if (!_approvals.TryGetValue(request.ApprovalId, out var current) ||
                current.State != ApprovalState.Pending ||
                !_operations.TryGetValue(current.OperationId, out var operation) ||
                operation.State != ApprovalState.Pending ||
                !FixedTimeEquals(current.NonceB64Url, request.NonceB64Url))
            {
                throw new InvalidOperationException(
                    "Approval request is invalid or already decided.");
            }
            var now = _timeProvider.GetUtcNow();
            if (current.ExpiresAt <= now)
            {
                SetTerminal(current.OperationId, ApprovalState.Expired);
                throw new InvalidOperationException("Approval request has expired.");
            }
            var decision = new HostApprovalDecision(
                current.ApprovalId,
                current.NonceB64Url,
                approved,
                brokerProcessSessionId,
                now,
                approved ? null : denialReason ?? "user-denied");
            _approvals[current.ApprovalId] = current with
            {
                State = approved ? ApprovalState.Approved : ApprovalState.Denied
            };
            SetOperationState(
                current.OperationId,
                approved ? ApprovalState.Approved : ApprovalState.Denied);
            return decision;
        }
    }

    public ApprovalConsumeResult Consume(
        Guid operationId,
        ApprovalOperationKind kind,
        Guid processSessionId,
        Guid? vaultSessionId,
        string parametersHash)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            PruneExpired(now);
            if (!_operations.TryGetValue(operationId, out var operation))
            {
                return Fail(ApprovalConsumeStatus.NotFound, "approval_not_found");
            }
            if (operation.State == ApprovalState.Expired)
            {
                return Fail(ApprovalConsumeStatus.Expired, "approval_expired");
            }
            if (operation.State != ApprovalState.Approved)
            {
                return Fail(
                    operation.State == ApprovalState.Consumed
                        ? ApprovalConsumeStatus.NotFound
                        : ApprovalConsumeStatus.NotApproved,
                    operation.State == ApprovalState.Consumed
                        ? "approval_consumed"
                        : "approval_not_approved");
            }
            if (operation.ProcessSessionId != processSessionId ||
                operation.VaultSessionId != vaultSessionId)
            {
                return Fail(
                    ApprovalConsumeStatus.IdentityMismatch,
                    "approval_parameter_mismatch");
            }
            if (operation.Kind != kind ||
                !string.Equals(
                    operation.ParametersHash,
                    parametersHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Fail(
                    ApprovalConsumeStatus.ParameterMismatch,
                    "approval_parameter_mismatch");
            }
            operation = operation with { State = ApprovalState.Consumed };
            _operations[operationId] = operation;
            return new ApprovalConsumeResult(
                ApprovalConsumeStatus.Consumed,
                operation,
                ReasonCode: null);
        }
    }

    public int RevokeAll()
    {
        lock (_sync)
        {
            var count = 0;
            foreach (var operation in _operations.Values.ToArray())
            {
                if (operation.State is ApprovalState.Pending or ApprovalState.Approved)
                {
                    _operations[operation.OperationId] = operation with
                    {
                        State = ApprovalState.Revoked
                    };
                    count++;
                }
            }
            return count;
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var operation in _operations.Values.ToArray())
        {
            if (operation.State is ApprovalState.Pending or ApprovalState.Approved &&
                operation.ExpiresAt <= now)
            {
                _operations[operation.OperationId] = operation with
                {
                    State = ApprovalState.Expired
                };
            }
        }
    }

    private void SetOperationState(Guid operationId, ApprovalState state)
    {
        _operations[operationId] = _operations[operationId] with { State = state };
    }

    private void SetTerminal(Guid operationId, ApprovalState state)
    {
        _operations[operationId] = _operations[operationId] with { State = state };
    }

    private static ApprovalConsumeResult Fail(
        ApprovalConsumeStatus status,
        string reasonCode)
    {
        return new ApprovalConsumeResult(status, Operation: null, reasonCode);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
