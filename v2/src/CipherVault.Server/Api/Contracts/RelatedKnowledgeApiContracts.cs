using CipherVault.Core.RelatedKnowledge;

namespace CipherVault.Server.Api.Contracts;

public sealed record RelatedDocumentsResponse(
    IReadOnlyList<RelatedDocumentItem> Items);
