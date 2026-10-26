using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Documents;

namespace CipherVault.Core.Tests;

public sealed class FrontMatterPerformanceTests
{
    [Fact]
    public void RunFrontMatterBaselineWhenEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CIPHER_VAULT_RUN_FRONTMATTER_PERF"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var iterations = ReadInt(
            "CIPHER_VAULT_FRONTMATTER_PERF_ITERATIONS",
            10_000,
            100,
            100_000);
        var content = CreateContent();
        for (var index = 0; index < 100; index++)
        {
            Assert.True(FrontMatterDocument.TryParse(content, out _));
        }

        var watch = Stopwatch.StartNew();
        for (var index = 0; index < iterations; index++)
        {
            if (!FrontMatterDocument.TryParse(content, out _))
            {
                throw new InvalidOperationException(
                    "Frontmatter benchmark input did not parse.");
            }
        }
        watch.Stop();

        var output = JsonSerializer.Serialize(new
        {
            iterations,
            fields = 52,
            totalMs = watch.ElapsedMilliseconds,
            microsecondsPerParse =
                watch.Elapsed.TotalMicroseconds / iterations
        });
        Console.WriteLine($"FRONTMATTER_BENCHMARK={output}");
    }

    private static string CreateContent()
    {
        var builder = new StringBuilder("---\n");
        for (var index = 0; index < 50; index++)
        {
            builder.Append("field")
                .Append(index)
                .Append(": value")
                .Append(index)
                .Append('\n');
        }
        builder.Append(
            """
            description: |
              First line
              ---
              Second line
            tags:
              - Security
              - MCP
            ---
            # Body
            """);
        return builder.ToString();
    }

    private static int ReadInt(
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }
}
