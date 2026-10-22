using System.IO.Pipes;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Session;

public sealed record AdminPipeServerOptions(
    string PipeName,
    PipeSecurity Security,
    string ServiceInstanceId,
    string SecurityMode,
    string ServiceVersion,
    VerifiedProcessIdentity ServiceIdentity,
    IReadOnlyList<ProcessVerificationPolicy> AllowedClientPolicies,
    string OwnerSid,
    string ServiceSid,
    TimeSpan SessionIdleLifetime,
    TimeSpan SessionHardLifetime,
    Func<AdminSession, CancellationToken, Task<AdminPipeStatusData>>? StatusProvider = null,
    Func<
        AdminSession,
        string,
        CancellationToken,
        Task<HostSessionStatusData>>? LockProvider = null,
    Func<
        AdminSession,
        HostCredentialPreference,
        CancellationToken,
        Task<HostSessionStatusData>>? UnlockProvider = null,
    Func<
        AdminSession,
        string,
        CancellationToken,
        Task<HostInitializeData>>? InitializeProvider = null,
    Func<
        AdminSession,
        string,
        System.Text.Json.JsonElement,
        CancellationToken,
        Task<object>>? MethodProvider = null,
    AdminSessionStore? AdminSessions = null,
    Func<Task>? StopHostCallback = null);
