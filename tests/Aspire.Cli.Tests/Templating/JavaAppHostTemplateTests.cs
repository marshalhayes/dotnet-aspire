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

    /// <summary>
    /// The generated SDK declares <c>package aspire;</c>, so an editor can only resolve it when
    /// <c>.aspire/modules</c> is a source root. javac does not need this because the CLI names every
    /// generated file explicitly in its argument file, but the Java language server builds from the
    /// project model instead and reports "package aspire does not exist" against an AppHost that runs
    /// perfectly well. The template recommends the Java extension pack, so the scaffolded project has
    /// to arrive with the source root already registered.
    /// </summary>
    [Fact]
    public void JavaStarterTemplate_RegistersTheGeneratedSdkAsASourceRoot()
    {
        var path = Path.Combine(GetJavaStarterDirectory(), ".vscode", "settings.json");
        Assert.True(File.Exists(path), $"Expected the java-starter VS Code settings at {path}");

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var sourcePaths = document.RootElement
            .GetProperty("java.project.sourcePaths")
            .EnumerateArray()
            .Select(element => element.GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal([".", ".aspire/modules"], sourcePaths);
    }

    private static string GetJavaStarterAppHostPath()
    {
        var path = Path.Combine(GetJavaStarterDirectory(), "AppHost.java");
        Assert.True(File.Exists(path), $"Expected the java-starter AppHost at {path}");

        return path;
    }

    private static string GetJavaStarterDirectory()
        => Path.Combine(GetRepoRoot(), "src", "Aspire.Cli", "Templating", "Templates", "java-starter");

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
