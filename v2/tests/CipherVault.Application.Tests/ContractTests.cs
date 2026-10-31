using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class ContractTests
{
    [Fact]
    public void CursorRoundTripsAndBindsKindAndFingerprint()
    {
        var payload = new CursorPayload("alpha", Guid.NewGuid());
        var cursor = CursorCodec.Encode(
            "catalog",
            CursorCodec.Fingerprint("folder"),
            payload);

        Assert.True(CursorCodec.TryDecode(
            cursor,
            "catalog",
            CursorCodec.Fingerprint("folder"),
            out CursorPayload? decoded));
        Assert.Equal(payload, decoded);
        Assert.False(CursorCodec.TryDecode(
            cursor,
            "search",
            CursorCodec.Fingerprint("folder"),
            out CursorPayload? wrongKind));
        Assert.False(CursorCodec.TryDecode(
            cursor,
            "catalog",
            CursorCodec.Fingerprint("other"),
            out CursorPayload? wrongFingerprint));
        Assert.False(CursorCodec.TryDecode(
            cursor + "x",
            "catalog",
            CursorCodec.Fingerprint("folder"),
            out CursorPayload? malformed));
    }

    [Fact]
    public void ActorAndExceptionCarryOnlySafeDetails()
    {
        var actor = new VaultActorContext(VaultActorKind.UserUi, "req-1");
        Assert.Equal(VaultActorKind.UserUi, actor.Kind);
        Assert.Equal("req-1", actor.CorrelationId);

        var exception = new VaultException(
            VaultErrorCode.RevisionConflict,
            "revision",
            details: new Dictionary<string, object?>
            {
                ["currentRevision"] = 7
            });
        Assert.Equal(7, exception.Details["currentRevision"]);
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            exception.Details);

        var invalidActor = Assert.Throws<VaultException>(
            () => new VaultActorContext((VaultActorKind)99));
        Assert.Equal(VaultErrorCode.InvalidInput, invalidActor.Code);
    }

    private sealed record CursorPayload(string Key, Guid DocumentId);
}
