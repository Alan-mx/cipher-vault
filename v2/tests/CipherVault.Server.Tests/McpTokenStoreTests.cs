using System.Text.Json;
using CipherVault.Core.Errors;
using CipherVault.Server.Hosting;
using CipherVault.Server.Mcp;

namespace CipherVault.Server.Tests;

public sealed class McpTokenStoreTests
{
    [Fact]
    public async Task MissingFileIsUnconfigured()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-token-{Guid.NewGuid():N}",
            "mcp-token.json");
        var store = await McpTokenStore.LoadAsync(path);

        Assert.False(store.GetStatus().Configured);
        Assert.False(store.TryAuthenticate("anything"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RotationPersistsDigestOnlyAndRotatesCredential()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-token-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "mcp-token.json");
        var time = new ManualTimeProvider();
        try
        {
            var store = await McpTokenStore.LoadAsync(path, time);
            var first = await store.RotateAsync();
            Assert.Equal(43, first.Token.Length);
            Assert.True(store.TryAuthenticate(first.Token));
            Assert.False(store.TryAuthenticate("wrong"));

            var fileText = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(first.Token, fileText, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(fileText);
            Assert.Equal(
                McpTokenStore.HashToken(first.Token),
                json.RootElement.GetProperty("digest").GetString());

            time.Advance(TimeSpan.FromMinutes(5));
            var second = await store.RotateAsync();
            Assert.True(store.TryAuthenticate(second.Token));
            Assert.False(store.TryAuthenticate(first.Token));
            var status = store.GetStatus();
            Assert.Equal(first.CreatedAtUtc, status.CreatedAtUtc);
            Assert.Equal(time.GetUtcNow(), status.RotatedAtUtc);

            var reloaded = await McpTokenStore.LoadAsync(path, time);
            Assert.True(reloaded.TryAuthenticate(second.Token));
            Assert.False(reloaded.TryAuthenticate(first.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedWriteKeepsOldToken()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-token-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "mcp-token.json");
        var time = new ManualTimeProvider();
        try
        {
            var original = await McpTokenStore.LoadAsync(path, time);
            var oldToken = (await original.RotateAsync()).Token;
            var file = JsonSerializer.Deserialize<McpTokenFile>(
                await File.ReadAllTextAsync(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var state = McpTokenStore.ToRuntimeState(file);
            var failing = new McpTokenStore(
                path,
                state,
                time,
                (_, _, _) => throw new IOException("simulated"));

            var exception = await Assert.ThrowsAsync<VaultException>(
                () => failing.RotateAsync());
            Assert.Equal(VaultErrorCode.StorageError, exception.Code);
            Assert.True(failing.TryAuthenticate(oldToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptFileFailsStartupClosed()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-token-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "mcp-token.json");
        try
        {
            await File.WriteAllTextAsync(path, "{not-json");
            var exception = await Assert.ThrowsAsync<ServerStartupException>(
                () => McpTokenStore.LoadAsync(path));
            Assert.Equal(ServerStartupCode.McpTokenFailure, exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentRotationLeavesExactlyOneActiveToken()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-token-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "mcp-token.json");
        try
        {
            var store = await McpTokenStore.LoadAsync(
                path,
                new ManualTimeProvider());
            var rotations = await Task.WhenAll(
                store.RotateAsync(),
                store.RotateAsync());

            Assert.Single(rotations, item => store.TryAuthenticate(item.Token));
            var reloaded = await McpTokenStore.LoadAsync(path);
            Assert.Single(rotations, item => reloaded.TryAuthenticate(item.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
