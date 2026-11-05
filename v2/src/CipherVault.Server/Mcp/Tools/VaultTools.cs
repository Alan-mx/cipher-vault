using System.ComponentModel;
using CipherVault.Application.Vault;
using CipherVault.Server.Api;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CipherVault.Server.Mcp.Tools;

[McpServerToolType]
public sealed class VaultTools
{
    private readonly VaultLifecycleService lifecycle;
    private readonly McpToolInvoker invoker;

    public VaultTools(
        VaultLifecycleService lifecycle,
        McpToolInvoker invoker)
    {
        this.lifecycle = lifecycle;
        this.invoker = invoker;
    }

    [McpServerTool(Name = McpToolNames.VaultStatus)]
    [Description("Return the safe Vault state. This tool never refreshes activity and never returns Knowledge, Source, or Draft content.")]
    public Task<CallToolResult> VaultStatus(
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.VaultStatus,
            async (_, token) =>
                SafeVaultStatusMapper.Map(
                    await lifecycle.GetStatusAsync(token)),
            cancellationToken);
}
