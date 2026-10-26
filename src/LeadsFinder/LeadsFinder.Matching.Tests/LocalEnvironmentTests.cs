using LeadsFinder.Data;
using Microsoft.Extensions.Configuration;

namespace LeadsFinder.Matching.Tests;

public sealed class LocalEnvironmentTests
{
    [Fact]
    public void ParsesNestedKeysAndLiteralTokensWithEqualsAndHashes()
    {
        var values = LocalEnvironment.Parse([
            "# comment", "export Service__ApiKey = 'abc=123#fragment$literal'", "REGION=ap-southeast-2 # comment"
        ]);
        Assert.Equal("abc=123#fragment$literal", values["Service:ApiKey"]);
        Assert.Equal("ap-southeast-2", values["REGION"]);
    }

    [Fact]
    public void SingleQuotesPreserveWindowsPathsAndDoubleQuotesSupportEscapes()
    {
        var values = LocalEnvironment.Parse([@"PATH_VALUE='C:\maps\northern-zone.osm.pbf'", "QUOTED=\"a\\\"b\\nnext\" # comment"]);
        Assert.Equal(@"C:\maps\northern-zone.osm.pbf", values["PATH_VALUE"]);
        Assert.Equal("a\"b\nnext", values["QUOTED"]);
    }

    [Fact]
    public void BlankEntriesPreserveFallbackConfiguration()
    {
        var values = LocalEnvironment.Parse(["LEADSFINDER_DB=", "EMPTY=''", "COMMENT= # explanation"]);
        Assert.Empty(values);
    }

    [Fact]
    public void ValuesAreNotExpandedOrExecuted()
    {
        var values = LocalEnvironment.Parse(["TOKEN=${HOME}$(whoami)`literal`"]);
        Assert.Equal("${HOME}$(whoami)`literal`", values["TOKEN"]);
    }

    [Theory]
    [InlineData("PRIVATE_KEY=\"private-value")]
    [InlineData("PRIVATE_KEY='private-value' unexpected")]
    [InlineData("PRIVATE_KEY private-value")]
    public void MalformedEntriesReportOnlyLineNumber(string line)
    {
        var error = Assert.Throws<FormatException>(() => LocalEnvironment.Parse(["# heading", line]));
        Assert.Contains("line 2", error.Message);
        Assert.DoesNotContain("private-value", error.Message);
        Assert.DoesNotContain("PRIVATE_KEY", error.Message);
    }

    [Fact]
    public void HostEnvironmentAndCommandLineOverrideDotEnvWithoutMutatingProcessEnvironment()
    {
        var key = "LEADSFINDER_ENV_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            Environment.SetEnvironmentVariable(key, "os-value");
            var values = LocalEnvironment.Parse([$"{key}=file-value"]);
            using var config = new ConfigurationManager();
            config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = "fallback" })
                .AddInMemoryCollection(values).AddEnvironmentVariables().AddCommandLine([$"--{key}", "cli-value"]);
            Assert.Equal("cli-value", config[key]);
            using var envConfig = new ConfigurationManager();
            envConfig.AddInMemoryCollection(values).AddEnvironmentVariables();
            Assert.Equal("os-value", envConfig[key]);
            Assert.Equal("os-value", Environment.GetEnvironmentVariable(key));
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [Fact]
    public void DiscoveryUsesRepositoryRootInsteadOfNestedDotEnvFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "leadsfinder-env-test-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "src", "LeadsFinder");
        var nested = Path.Combine(project, "LeadsFinder.Api");
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllText(Path.Combine(project, "LeadsFinder.slnx"), "<Solution />");
            File.WriteAllText(Path.Combine(root, ".env"), "REGION=root-value");
            File.WriteAllText(Path.Combine(nested, ".env"), "REGION=nested-value");
            Assert.Equal("root-value", LocalEnvironment.Read(nested)["REGION"]);
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("leadsfinder-env-test-", Path.GetFileName(root));
            Directory.Delete(root, recursive: true);
        }
    }
}
