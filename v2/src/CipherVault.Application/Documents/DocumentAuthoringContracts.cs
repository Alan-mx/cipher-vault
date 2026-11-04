namespace CipherVault.Application.Documents;

public readonly record struct OptionalField<T>(
    bool IsSpecified,
    T? Value)
{
    public static OptionalField<T> Missing { get; } = new(false, default);

    public static OptionalField<T> Of(T value) => new(true, value);
}

public sealed record DocumentPartialUpdate(
    OptionalField<string> Title,
    OptionalField<string> VirtualPath,
    OptionalField<string> Content,
    OptionalField<IReadOnlyList<string>> Tags);
