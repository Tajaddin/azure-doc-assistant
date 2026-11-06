using System.Text.RegularExpressions;

namespace DocAssistant.UnitTests;

/// <summary>Files in the repository, for tests that check committed configuration rather than code.</summary>
public static class RepoFiles
{
    public static string Root { get; } = FindRoot();

    public static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocAssistant.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

/// <summary>The Bicep templates cannot be deployed from the tests; these pin the properties that broke or exposed them.</summary>
public class InfraTests
{
    [Fact]
    public void Deploy_identity_trusts_the_subject_github_sends_for_the_deploy_job()
    {
        // A job that names an environment gets the subject repo:<owner>/<repo>:environment:<name>, not the branch.
        var environment = Regex.Match(RepoFiles.Read(".github", "workflows", "deploy.yml"), @"(?m)^\s+environment:\s*(\S+)\s*$").Groups[1].Value;
        Assert.Equal("production", environment);
        var subject = Regex.Match(RepoFiles.Read("infra", "bicep", "resources.bicep"), @"subject:\s*'([^']+)'").Groups[1].Value;
        Assert.Equal($"repo:${{githubRepository}}:environment:{environment}", subject);
    }
}
