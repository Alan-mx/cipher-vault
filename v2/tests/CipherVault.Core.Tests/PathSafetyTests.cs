using CipherVault.Core.Errors;
using CipherVault.Core.Security;

namespace CipherVault.Core.Tests;

public sealed class PathSafetyTests
{
    [Fact]
    public void RelationDetectsEqualsChildrenAndAncestors()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-path-{Guid.NewGuid():N}");
        var data = Path.Combine(root, "data");
        var child = Path.Combine(data, "child");

        Assert.Equal(
            new PathRelation(true, false, false),
            PathSafety.GetRelation(data, data));
        Assert.Equal(
            new PathRelation(false, true, false),
            PathSafety.GetRelation(child, data));
        Assert.Equal(
            new PathRelation(false, false, true),
            PathSafety.GetRelation(data, child));
        Assert.Equal(
            new PathRelation(false, false, false),
            PathSafety.GetRelation(
                Path.Combine(root, "data"),
                Path.Combine(root, "data-other")));
    }

    [Fact]
    public void RelativePathIsRejectedWhenAbsoluteIsRequired()
    {
        var exception = Assert.Throws<VaultException>(
            () => PathSafety.Canonicalize(
                "relative/path",
                "test path",
                requireAbsolute: true));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public void SymbolicLinkComponentIsRejectedWhenSupported()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-link-{Guid.NewGuid():N}");
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException
                    or IOException
                    or PlatformNotSupportedException)
            {
                return;
            }

            var pathException = Assert.Throws<VaultException>(
                () => PathSafety.EnsureNoLinkComponents(
                    Path.Combine(link, "vault.db")));
            Assert.Equal(VaultErrorCode.InvalidInput, pathException.Code);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            if (Directory.Exists(target))
            {
                Directory.Delete(target);
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
