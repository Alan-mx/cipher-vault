using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CipherVault.Core.Backup;
using CipherVault.Core.Security;

namespace CipherVault.Server.Backup;

public sealed class GitBackupStateStore
{
    public const int MaxStateFileBytes = 8 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new UtcDateTimeOffsetConverter() }
    };

    public async Task<GitBackupStateLoadResult> LoadAsync(
        string statePath,
        string repositoryFingerprint,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(statePath))
        {
            return Reset(
                repositoryFingerprint,
                nowUtc,
                safeErrorCode: null);
        }

        try
        {
            var info = new FileInfo(statePath);
            if (info.Length is <= 0 or > MaxStateFileBytes)
            {
                throw new JsonException();
            }

            VaultFilePermissions.ProtectFile(statePath);
            var bytes = await File.ReadAllBytesAsync(statePath, cancellationToken);
            if (bytes.Length is <= 0 or > MaxStateFileBytes)
            {
                throw new JsonException();
            }

            EnsureNoDuplicateProperties(bytes);
            var state = JsonSerializer.Deserialize<GitBackupPersistedState>(
                bytes,
                JsonOptions) ?? throw new JsonException();
            Validate(state);
            if (!string.Equals(
                    state.RepositoryFingerprint,
                    repositoryFingerprint,
                    StringComparison.Ordinal))
            {
                return Reset(
                    repositoryFingerprint,
                    nowUtc,
                    safeErrorCode: null);
            }

            return new GitBackupStateLoadResult(
                Normalize(state),
                WasReset: false,
                SafeErrorCode: null);
        }
        catch (JsonException)
        {
            return Reset(
                repositoryFingerprint,
                nowUtc,
                GitBackupErrorCodes.StateCorrupt);
        }
        catch (FormatException)
        {
            return Reset(
                repositoryFingerprint,
                nowUtc,
                GitBackupErrorCodes.StateCorrupt);
        }
    }

    public async Task SaveAsync(
        string statePath,
        GitBackupPersistedState state,
        CancellationToken cancellationToken = default)
    {
        Validate(state);
        var normalized = Normalize(state);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
        if (bytes.Length > MaxStateFileBytes)
        {
            throw new JsonException("Git backup state exceeds the size limit.");
        }

        await AtomicFileWriter.WriteAsync(statePath, bytes, cancellationToken);
        VaultFilePermissions.ProtectFile(statePath);
    }

    private static GitBackupStateLoadResult Reset(
        string repositoryFingerprint,
        DateTimeOffset nowUtc,
        string? safeErrorCode) =>
        new(
            GitBackupPersistedState.CreateInitial(
                repositoryFingerprint,
                nowUtc),
            WasReset: true,
            SafeErrorCode: safeErrorCode);

    private static void EnsureNoDuplicateProperties(byte[] bytes)
    {
        using var document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException();
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new JsonException();
            }
        }
    }

    private static void Validate(GitBackupPersistedState state)
    {
        if (state.Version != GitBackupPersistedState.CurrentVersion
            || !IsLowerHex(state.RepositoryFingerprint, 64)
            || state.ConsecutiveFailures is < 0 or > 20
            || state.LastErrorCode is not null
                && !GitBackupErrorCodes.All.Contains(state.LastErrorCode))
        {
            throw new FormatException();
        }

        foreach (var timestamp in new[]
        {
            state.FirstDirtyAtUtc,
            state.LastMutationAtUtc,
            state.LastSuccessAtUtc,
            state.LastPushAtUtc,
            state.LastAttemptAtUtc,
            state.NextAttemptAtUtc
        })
        {
            if (timestamp is { Offset.Ticks: not 0 })
            {
                throw new FormatException();
            }
        }

        if (state.Dirty
            && (state.FirstDirtyAtUtc is null
                || state.LastMutationAtUtc is null
                || state.LastMutationAtUtc < state.FirstDirtyAtUtc))
        {
            throw new FormatException();
        }

        if (!state.Dirty
            && (state.FirstDirtyAtUtc is not null
                || state.LastMutationAtUtc is not null))
        {
            throw new FormatException();
        }

        if (state.PendingLocalBackupFileName is { } fileName)
        {
            if (!BackupFileName.TryParse(fileName, out _))
            {
                throw new FormatException();
            }
        }

        if (state.PendingPushCommitSha is { } pendingSha
            && !IsCommitSha(pendingSha))
        {
            throw new FormatException();
        }

        if (state.LastCommitSha is { } lastSha
            && !IsCommitSha(lastSha))
        {
            throw new FormatException();
        }
    }

    private static GitBackupPersistedState Normalize(
        GitBackupPersistedState state) =>
        state with
        {
            FirstDirtyAtUtc = state.FirstDirtyAtUtc?.ToUniversalTime(),
            LastMutationAtUtc = state.LastMutationAtUtc?.ToUniversalTime(),
            LastSuccessAtUtc = state.LastSuccessAtUtc?.ToUniversalTime(),
            LastPushAtUtc = state.LastPushAtUtc?.ToUniversalTime(),
            LastAttemptAtUtc = state.LastAttemptAtUtc?.ToUniversalTime(),
            NextAttemptAtUtc = state.NextAttemptAtUtc?.ToUniversalTime()
        };

    private static bool IsCommitSha(string value) =>
        IsLowerHex(value, 40) || IsLowerHex(value, 64);

    private static bool IsLowerHex(string? value, int length) =>
        value is not null
        && value.Length == length
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private sealed class UtcDateTimeOffsetConverter :
        JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String
                || !DateTimeOffset.TryParseExact(
                    reader.GetString(),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var value))
            {
                throw new JsonException();
            }

            return value.ToUniversalTime();
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options)
        {
            var formatted = value
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);
            writer.WriteStringValue(
                formatted.EndsWith("+00:00", StringComparison.Ordinal)
                    ? formatted[..^6] + "Z"
                    : formatted);
        }
    }
}
