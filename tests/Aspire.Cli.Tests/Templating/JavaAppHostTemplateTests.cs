// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.Templating;

public class JavaAppHostTemplateTests
{
    /// <summary>
    /// The Java runtime spec launches the AppHost with <c>java -cp .java-build AppHost</c>, which names
    /// the class in the default package. A <c>package</c> declaration compiles the class into a
    /// subdirectory instead, so the launch fails with <c>ClassNotFoundException: AppHost</c> even though
    /// compilation succeeded.
    /// </summary>
    [Fact]
    public void JavaStarterAppHost_IsInTheDefaultPackageSoTheRunnerCanLoadIt()
    {
        var lines = File.ReadAllLines(GetJavaStarterAppHostPath());

        Assert.DoesNotContain(lines, line => line.TrimStart().StartsWith("package ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Without a package declaration the AppHost no longer shares a package with the generated SDK, so
    /// it has to import it explicitly or every generated type fails to resolve.
    /// </summary>
    [Fact]
    public void JavaStarterAppHost_ImportsTheGeneratedSdkPackage()
    {
        var lines = File.ReadAllLines(GetJavaStarterAppHostPath());

        Assert.Contains("import aspire.*;", lines.Select(line => line.Trim()));
    }

    private static string GetJavaStarterAppHostPath()
    {
        var path = Path.Combine(GetRepoRoot(), "src", "Aspire.Cli", "Templating", "Templates", "java-starter", "AppHost.java");
        Assert.True(File.Exists(path), $"Expected the java-starter AppHost at {path}");

        return path;
    }

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
