export const buildMcpConfig = (origin: string, endpointPath: string): string =>
  JSON.stringify(
    {
      transport: "streamable_http",
      url: `${origin}${endpointPath}`,
      headers: {
        Authorization: "Bearer <MCP_TOKEN>",
      },
    },
    null,
    2,
  );
