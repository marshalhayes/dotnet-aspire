// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.TypeSystem;

namespace Aspire.Cli.Tests.Projects;

public class JavaAppHostToolchainResolverTests(ITestOutputHelper outputHelper)
{
    private static RuntimeSpec CreateJavacRuntimeSpec()
    {
        return new RuntimeSpec
        {
            Language = "java",
            DisplayName = "Java",
            CodeGenLanguage = "Java",
            DetectionPatterns = ["AppHost.java", "src/main/java/AppHost.java"],
            InstallDependencies = null,
            ExtensionLaunchCapability = "java",
            Execute = new CommandSpec
            {
                Command = "sh",
                Args = ["-c", "mkdir -p .java-build && javac --release 25 -d .java-build @.aspire/modules/sources.txt \"{appHostFile}\" && java -cp .java-build AppHost {args}"]
            }
        };
    }

    [Fact]
    public void Resolve_WithNoBuildFile_UsesJavacSoAJdkIsTheOnlyRequirement()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        Assert.Equal(JavaAppHostToolchain.Javac, JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot));
    }

    [Fact]
    public void Resolve_WithPomXml_UsesMaven()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");

        Assert.Equal(JavaAppHostToolchain.Maven, JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot));
    }

    [Theory]
    [InlineData("build.gradle")]
    [InlineData("build.gradle.kts")]
    public void Resolve_WithGradleBuildFile_UsesGradle(string buildFileName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, buildFileName), "");

        Assert.Equal(JavaAppHostToolchain.Gradle, JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot));
    }

    [Fact]
    public void Resolve_WithBothBuildFiles_PrefersMaven()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");
        File.WriteAllText(Path.Combine(workspace.Path, "build.gradle"), "");

        Assert.Equal(JavaAppHostToolchain.Maven, JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot));
    }

    [Fact]
    public void Resolve_IgnoresABuildFileInTheParentDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");

        var appHostDirectory = workspace.CreateDirectory("apphost");

        // A pom.xml above the AppHost usually belongs to an unrelated project that merely contains the
        // AppHost folder, so inheriting it would build the wrong thing.
        Assert.Equal(JavaAppHostToolchain.Javac, JavaAppHostToolchainResolver.Resolve(appHostDirectory));
    }

    [Fact]
    public void ApplyToRuntimeSpec_ForJavac_LeavesTheSpecUntouched()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var baseSpec = CreateJavacRuntimeSpec();

        // Existing single-file AppHosts must keep working byte for byte; adopting a build tool is opt-in.
        Assert.Same(baseSpec, JavaAppHostToolchainResolver.ApplyToRuntimeSpec(baseSpec, JavaAppHostToolchain.Javac, workspace.WorkspaceRoot));
    }

    [Fact]
    public void ApplyToRuntimeSpec_ForMaven_RestoresCompilesThenLaunchesAPlainJvm()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var spec = JavaAppHostToolchainResolver.ApplyToRuntimeSpec(CreateJavacRuntimeSpec(), JavaAppHostToolchain.Maven, workspace.WorkspaceRoot);

        Assert.Equal("Java (Maven)", spec.DisplayName);

        Assert.Equal("mvn", spec.InstallDependencies!.Command);
        Assert.Equal(
            ["-B", "-q", "dependency:copy-dependencies", $"-DoutputDirectory={Path.Combine("target", "aspire-deps")}", "-DincludeScope=runtime"],
            spec.InstallDependencies.Args);

        var compile = Assert.Single(spec.PreExecute!);
        Assert.Equal("mvn", compile.Command);
        Assert.Equal(["-B", "-q", "compile"], compile.Args);

        // The AppHost is launched directly rather than through mvn exec:java so console signals reach
        // it, and without a {args} placeholder so the CLI appends real argv entries.
        Assert.Equal("java", spec.Execute.Command);
        Assert.Equal(
            ["-cp", $"{Path.Combine("target", "classes")}{Path.PathSeparator}{Path.Combine("target", "aspire-deps", "*")}", "AppHost"],
            spec.Execute.Args);
        Assert.DoesNotContain(spec.Execute.Args, arg => arg.Contains("{args}", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyToRuntimeSpec_ForGradle_UsesTheGradleOutputLayout()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var spec = JavaAppHostToolchainResolver.ApplyToRuntimeSpec(CreateJavacRuntimeSpec(), JavaAppHostToolchain.Gradle, workspace.WorkspaceRoot);

        Assert.Equal("Java (Gradle)", spec.DisplayName);
        Assert.Equal("gradle", spec.InstallDependencies!.Command);
        Assert.Equal(
            ["-q", "--init-script", JavaAppHostToolchainResolver.GradleInitScriptRelativePath, "aspireCopyDependencies"],
            spec.InstallDependencies.Args);

        var compile = Assert.Single(spec.PreExecute!);
        Assert.Equal(["-q", "classes"], compile.Args);

        Assert.Equal("java", spec.Execute.Command);
        Assert.Equal(
            ["-cp", $"{Path.Combine("build", "classes", "java", "main")}{Path.PathSeparator}{Path.Combine("build", "aspire-deps", "*")}", "AppHost"],
            spec.Execute.Args);
    }

    [Fact]
    public void ApplyToRuntimeSpec_PreservesTheExtensionLaunchCapabilitySoTheAppHostStaysDebuggable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var spec = JavaAppHostToolchainResolver.ApplyToRuntimeSpec(CreateJavacRuntimeSpec(), JavaAppHostToolchain.Maven, workspace.WorkspaceRoot);

        Assert.Equal("java", spec.ExtensionLaunchCapability);
    }

    [Theory]
    [InlineData(true, "mvn")]
    [InlineData(false, "gradle")]
    public void GetToolCommand_WithoutAWrapper_FallsBackToTheToolOnPath(bool useMaven, string expected)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var toolchain = useMaven ? JavaAppHostToolchain.Maven : JavaAppHostToolchain.Gradle;

        Assert.Equal(expected, JavaAppHostToolchainResolver.GetToolCommand(workspace.WorkspaceRoot, toolchain));
    }

    [Theory]
    [InlineData(true, "mvnw", "mvnw.cmd")]
    [InlineData(false, "gradlew", "gradlew.bat")]
    public void GetToolCommand_WithAWrapper_UsesItByAbsolutePath(bool useMaven, string wrapperName, string windowsWrapperName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var toolchain = useMaven ? JavaAppHostToolchain.Maven : JavaAppHostToolchain.Gradle;

        // Started without a shell, so a bare "mvnw" would be looked up on PATH and never found.
        var expectedWrapper = OperatingSystem.IsWindows() ? windowsWrapperName : wrapperName;
        var wrapperPath = Path.Combine(workspace.Path, expectedWrapper);
        File.WriteAllText(wrapperPath, "");

        Assert.Equal(wrapperPath, JavaAppHostToolchainResolver.GetToolCommand(workspace.WorkspaceRoot, toolchain));
    }

    [Fact]
    public async Task EnsureToolchainFilesExistAsync_ForGradle_WritesTheInitScriptAndOverwritesAStaleOne()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var scriptPath = Path.Combine(workspace.Path, ".aspire", "aspire-gradle-init.gradle");

        // The .aspire directory does not exist yet, which is why this is not a RuntimeSpec migration file.
        await JavaAppHostToolchainResolver.EnsureToolchainFilesExistAsync(workspace.WorkspaceRoot, JavaAppHostToolchain.Gradle, CancellationToken.None);

        Assert.Contains("aspireCopyDependencies", await File.ReadAllTextAsync(scriptPath));

        await File.WriteAllTextAsync(scriptPath, "// stale");
        await JavaAppHostToolchainResolver.EnsureToolchainFilesExistAsync(workspace.WorkspaceRoot, JavaAppHostToolchain.Gradle, CancellationToken.None);

        Assert.Contains("aspireCopyDependencies", await File.ReadAllTextAsync(scriptPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnsureToolchainFilesExistAsync_ForANonGradleToolchain_WritesNothing(bool useJavac)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var toolchain = useJavac ? JavaAppHostToolchain.Javac : JavaAppHostToolchain.Maven;

        await JavaAppHostToolchainResolver.EnsureToolchainFilesExistAsync(workspace.WorkspaceRoot, toolchain, CancellationToken.None);

        Assert.Empty(workspace.WorkspaceRoot.GetDirectories());
    }
}
