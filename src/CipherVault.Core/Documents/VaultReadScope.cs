namespace CipherVault.Core.Documents;

public sealed class VaultReadScope
{
    private static readonly VaultReadScope UnrestrictedScope = new([]);

    private VaultReadScope(IReadOnlyList<string> folders)
    {
        Folders = folders;
        Fingerprint = folders.Count == 0
            ? string.Empty
            : CursorCodec.Fingerprint(string.Join('\u001F', folders));
    }

    public static VaultReadScope Unrestricted => UnrestrictedScope;

    public IReadOnlyList<string> Folders { get; }

    public bool IsRestricted => Folders.Count > 0;

    public string Fingerprint { get; }

    public static VaultReadScope Restricted(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var normalized = folders
            .Select(folder => VirtualPathPolicy.NormalizeFolder(folder)
                .ToLowerInvariant())
            .Where(folder => folder.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(folder => folder, StringComparer.Ordinal)
            .ToArray();
        return normalized.Length == 0
            ? Unrestricted
            : new VaultReadScope(normalized);
    }

    public bool Allows(string virtualPath)
    {
        if (!IsRestricted)
        {
            return true;
        }
        var normalized = virtualPath
            .Replace('\\', '/')
            .Trim('/')
            .ToLowerInvariant();
        return Folders.Any(folder =>
            string.Equals(normalized, folder, StringComparison.Ordinal) ||
            normalized.StartsWith(folder + "/", StringComparison.Ordinal));
    }
}
