// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.TypeSystem;

namespace Aspire.Cli.Tests.Projects;

public class JavaAppHostToolchainResolverTests(ITestOutputHelper outputHelper)
{
    private static string WriteWrapper(string directory, string wrapperName)
    {
        var path = Path.Combine(directory, wrapperName);
        File.WriteAllText(path, "");

        return path;
    }

    /// <summary>
    /// Asserts the wrapper invocation, accounting for Windows running the batch wrapper through the
    /// command interpreter rather than launching it directly.
    /// </summary>
    private static void AssertWrapperInvocation(string wrapperPath, string appHostDirectory, string[] toolArgs, CommandSpec actual)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", actual.Command);
            Assert.Equal(
                ["/c", Path.GetRelativePath(appHostDirectory, wrapperPath), .. toolArgs],
                actual.Args);

            return;
        }

        Assert.Equal(wrapperPath, actual.Command);
        Assert.Equal(toolArgs, actual.Args);
    }

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
            // Mirrors JavaLanguageSupport.GetRuntimeSpec: the build-tool compile command is derived
            // from this one, so it has to have the same shape for the derivation to be meaningful.
            PreExecute =
            [
                new CommandSpec
                {
                    Command = "javac",
                    Args = ["--release", "25", "-d", ".java-build", "@.aspire/modules/sources.txt", "{appHostFile}"]
                }
            ],
            Execute = new CommandSpec
            {
                Command = "java",
                Args = ["-cp", ".java-build", "AppHost"]
            }
        };
    }

    [Fact]
    public void Resolve_WithNoBuildFile_UsesJavacSoAJdkIsTheOnlyRequirement()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        Assert.Equal(JavaAppHostToolchain.Javac, JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot).Toolchain);
    }

    [Fact]
    public void Resolve_WithPomXml_UsesMaven()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");

        Assert.Equal(JavaAppHostToolchain.Maven, JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot).Toolchain);
    }

    [Theory]
    [InlineData("build.gradle")]
    [InlineData("build.gradle.kts")]
    public void Resolve_WithGradleBuildFile_UsesGradle(string buildFileName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, buildFileName), "");

        Assert.Equal(JavaAppHostToolchain.Gradle, JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot).Toolchain);
    }

    [Fact]
    public void Resolve_WithBothBuildFiles_PrefersMaven()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");
        File.WriteAllText(Path.Combine(workspace.Path, "build.gradle"), "");

        Assert.Equal(JavaAppHostToolchain.Maven, JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot).Toolchain);
    }

    [Fact]
    public void Resolve_IgnoresABuildFileInTheParentDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");

        var appHostDirectory = workspace.CreateDirectory("apphost");

        // A pom.xml above the AppHost usually belongs to an unrelated project that merely contains the
        // AppHost folder, so inheriting it would build the wrong thing.
        Assert.Equal(JavaAppHostToolchain.Javac, JavaAppHostToolchainResolver.Resolve(appHostDirectory).Toolchain);
    }

    [Fact]
    public void ApplyToRuntimeSpec_ForJavac_LeavesTheSpecUntouched()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var baseSpec = CreateJavacRuntimeSpec();

        // Existing single-file AppHosts must keep working byte for byte; adopting a build tool is opt-in.
        Assert.Same(baseSpec, JavaAppHostToolchainResolver.ApplyToRuntimeSpec(baseSpec, JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot), workspace.WorkspaceRoot));
    }

    [Fact]
    public void ApplyToRuntimeSpec_ForMaven_RestoresCompilesThenLaunchesAPlainJvm()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");
        var wrapper = WriteWrapper(workspace.Path, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw");

        var spec = JavaAppHostToolchainResolver.ApplyToRuntimeSpec(CreateJavacRuntimeSpec(), JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot), workspace.WorkspaceRoot);

        Assert.Equal("Java (Maven)", spec.DisplayName);

        AssertWrapperInvocation(
            wrapper,
            workspace.Path,
            ["-B", "-q", "dependency:copy-dependencies", $"-DoutputDirectory={Path.Combine("target", "aspire-deps")}", "-DincludeScope=runtime"],
            spec.InstallDependencies!);

        var compile = Assert.Single(spec.PreExecute!);
        // Compilation stays with javac even under Maven: the build tool cannot be told about the
        // generated SDK under .aspire/modules from the command line. The javac options and source
        // arguments are inherited from the base spec so the two toolchains cannot drift.
        Assert.Equal("javac", compile.Command);
        Assert.Equal(
            [
                "--release", "25",
                "-classpath", Path.Combine("target", "aspire-deps", "*"),
                "-sourcepath", $".{Path.PathSeparator}{Path.Combine("src", "main", "java")}",
                "-d", Path.Combine("target", "classes"),
                "@.aspire/modules/sources.txt",
                "{appHostFile}"
            ],
            compile.Args);

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

        File.WriteAllText(Path.Combine(workspace.Path, "build.gradle"), "");
        var wrapper = WriteWrapper(workspace.Path, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");

        var spec = JavaAppHostToolchainResolver.ApplyToRuntimeSpec(CreateJavacRuntimeSpec(), JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot), workspace.WorkspaceRoot);

        Assert.Equal("Java (Gradle)", spec.DisplayName);
        AssertWrapperInvocation(
            wrapper,
            workspace.Path,
            ["-q", "--init-script", JavaAppHostToolchainResolver.GradleInitScriptRelativePath, "aspireCopyDependencies"],
            spec.InstallDependencies!);

        var compile = Assert.Single(spec.PreExecute!);
        Assert.Equal("javac", compile.Command);
        Assert.Equal(
            [
                "--release", "25",
                "-classpath", Path.Combine("build", "aspire-deps", "*"),
                "-sourcepath", $".{Path.PathSeparator}{Path.Combine("src", "main", "java")}",
                "-d", Path.Combine("build", "classes", "java", "main"),
                "@.aspire/modules/sources.txt",
                "{appHostFile}"
            ],
            compile.Args);

        Assert.Equal("java", spec.Execute.Command);
        Assert.Equal(
            ["-cp", $"{Path.Combine("build", "classes", "java", "main")}{Path.PathSeparator}{Path.Combine("build", "aspire-deps", "*")}", "AppHost"],
            spec.Execute.Args);
    }

    [Fact]
    public void ApplyToRuntimeSpec_PreservesTheExtensionLaunchCapabilitySoTheAppHostStaysDebuggable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");
        WriteWrapper(workspace.Path, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw");

        var spec = JavaAppHostToolchainResolver.ApplyToRuntimeSpec(CreateJavacRuntimeSpec(), JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot), workspace.WorkspaceRoot);

        Assert.Equal("java", spec.ExtensionLaunchCapability);
    }

    [Theory]
    [InlineData(true, "mvnw", "mvn -N wrapper:wrapper")]
    [InlineData(false, "gradlew", "gradle wrapper")]
    public void GetToolInvocation_WithoutAWrapper_IsRejected(bool useMaven, string wrapperName, string generateCommand)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var toolchain = useMaven ? JavaAppHostToolchain.Maven : JavaAppHostToolchain.Gradle;

        // A globally installed tool is deliberately not used: the wrapper pins the version the repository
        // builds with, and falling back silently would make the AppHost build machine-dependent.
        var ex = Assert.Throws<InvalidOperationException>(
            () => JavaAppHostToolchainResolver.GetToolInvocation(workspace.WorkspaceRoot, workspace.WorkspaceRoot, toolchain));

        Assert.Contains(wrapperName, ex.Message);
        Assert.Contains(generateCommand, ex.Message);
    }

    [Theory]
    [InlineData(true, "mvnw", "mvnw.cmd")]
    [InlineData(false, "gradlew", "gradlew.bat")]
    public void GetToolInvocation_WithAWrapper_UsesIt(bool useMaven, string wrapperName, string windowsWrapperName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var toolchain = useMaven ? JavaAppHostToolchain.Maven : JavaAppHostToolchain.Gradle;

        var expectedWrapper = OperatingSystem.IsWindows() ? windowsWrapperName : wrapperName;
        var wrapperPath = Path.Combine(workspace.Path, expectedWrapper);
        File.WriteAllText(wrapperPath, "");

        var invocation = JavaAppHostToolchainResolver.GetToolInvocation(workspace.WorkspaceRoot, workspace.WorkspaceRoot, toolchain);

        if (OperatingSystem.IsWindows())
        {
            // The wrappers are batch files, which produce no output when launched directly with
            // redirected stdout, so the command interpreter runs them instead. The wrapper is passed
            // relative to the working directory so cmd.exe never sees a quoted first token.
            Assert.Equal(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", invocation.Command);
            Assert.Equal(["/c", expectedWrapper], invocation.PrefixArgs);
        }
        else
        {
            // Started without a shell, so a bare "mvnw" would be looked up on PATH and never found.
            Assert.Equal(wrapperPath, invocation.Command);
            Assert.Empty(invocation.PrefixArgs);
        }
    }

    [Fact]
    public async Task EnsureToolchainFilesExistAsync_ForGradle_WritesTheInitScriptAndOverwritesAStaleOne()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "build.gradle"), "");
        WriteWrapper(workspace.Path, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");
        var scriptPath = Path.Combine(workspace.Path, ".aspire", "aspire-gradle-init.gradle");

        // The .aspire directory does not exist yet, which is why this is not a RuntimeSpec migration file.
        await JavaAppHostToolchainResolver.EnsureToolchainFilesExistAsync(JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot), CancellationToken.None);

        Assert.Contains("aspireCopyDependencies", await File.ReadAllTextAsync(scriptPath));

        await File.WriteAllTextAsync(scriptPath, "// stale");
        await JavaAppHostToolchainResolver.EnsureToolchainFilesExistAsync(JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot), CancellationToken.None);

        Assert.Contains("aspireCopyDependencies", await File.ReadAllTextAsync(scriptPath));
    }

    [Fact]
    public async Task EnsureToolchainFilesExistAsync_ForGradle_StagesWithSyncSoUpgradedDependenciesDoNotAccumulate()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "build.gradle"), "");
        WriteWrapper(workspace.Path, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");

        await JavaAppHostToolchainResolver.EnsureToolchainFilesExistAsync(JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot), CancellationToken.None);

        var script = await File.ReadAllTextAsync(Path.Combine(workspace.Path, ".aspire", "aspire-gradle-init.gradle"));

        // The whole directory is the classpath, so a Copy would leave the previous version of an
        // upgraded dependency behind and load it alongside the new one.
        Assert.Contains("tasks.register(\"aspireCopyDependencies\", Sync)", script);
    }

    [Fact]
    public async Task EnsureToolchainFilesExistAsync_ForMaven_ClearsPreviouslyStagedDependencies()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");

        var dependencyDirectory = Path.Combine(workspace.Path, "target", "aspire-deps");
        Directory.CreateDirectory(dependencyDirectory);
        var staleJar = Path.Combine(dependencyDirectory, "library-1.0.jar");
        await File.WriteAllTextAsync(staleJar, "");

        await JavaAppHostToolchainResolver.EnsureToolchainFilesExistAsync(
            JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot),
            CancellationToken.None);

        // dependency:copy-dependencies only ever adds, so library-1.0.jar would survive an upgrade to
        // library-2.0.jar and both would be on the AppHost's dir/* classpath.
        Assert.False(Directory.Exists(dependencyDirectory));
    }

    [Fact]
    public async Task EnsureToolchainFilesExistAsync_ForMaven_WithNothingStagedYet_DoesNotThrow()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");

        await JavaAppHostToolchainResolver.EnsureToolchainFilesExistAsync(
            JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot),
            CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "target")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnsureToolchainFilesExistAsync_ForANonGradleToolchain_WritesNothing(bool useJavac)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        if (!useJavac)
        {
            File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");
        }

        await JavaAppHostToolchainResolver.EnsureToolchainFilesExistAsync(
            JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot),
            CancellationToken.None);

        Assert.Empty(workspace.WorkspaceRoot.GetDirectories());
    }

    [Theory]
    [InlineData("pom.xml", true)]
    [InlineData("build.gradle", false)]
    public void Resolve_WithTheConventionalSourceLayout_FindsTheBuildFileAtTheProjectRoot(string buildFileName, bool expectMaven)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var expected = expectMaven ? JavaAppHostToolchain.Maven : JavaAppHostToolchain.Gradle;
        File.WriteAllText(Path.Combine(workspace.Path, buildFileName), "");
        var appHostDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "src", "main", "java"));

        // src/main/java is a build tool's source root by convention, so the build file above it is
        // this project's, not an unrelated one that happens to contain the AppHost.
        var resolution = JavaAppHostToolchainResolver.Resolve(appHostDirectory);

        Assert.Equal(expected, resolution.Toolchain);
        Assert.Equal(workspace.Path, resolution.ProjectDirectory.FullName);
    }

    [Fact]
    public void Resolve_WithABuildFileThreeLevelsUpThatIsNotASourceRoot_UsesJavac()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");
        var appHostDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "a", "b", "c"));

        Assert.Equal(JavaAppHostToolchain.Javac, JavaAppHostToolchainResolver.Resolve(appHostDirectory).Toolchain);
    }

    [Fact]
    public void ApplyToRuntimeSpec_WithTheConventionalSourceLayout_PointsTheToolAtTheProjectRoot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");
        WriteWrapper(workspace.Path, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw");
        var appHostDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "src", "main", "java"));

        var resolution = JavaAppHostToolchainResolver.Resolve(appHostDirectory);
        var spec = JavaAppHostToolchainResolver.ApplyToRuntimeSpec(CreateJavacRuntimeSpec(), resolution, appHostDirectory);

        // Commands run from the AppHost directory, so Maven has to be pointed back at the project root
        // and the classpath has to climb back out of src/main/java.
        var toProjectRoot = Path.Combine("..", "..", "..");
        Assert.Contains("-f", spec.InstallDependencies!.Args);
        Assert.Contains(Path.Combine(toProjectRoot, "pom.xml"), spec.InstallDependencies.Args);

        // -DoutputDirectory is deliberately *not* rewritten: Maven resolves a relative outputDirectory
        // against the project's base directory, not the working directory, so climbing out of
        // src/main/java here would stage the jars three levels above the project.
        Assert.Contains($"-DoutputDirectory={Path.Combine("target", "aspire-deps")}", spec.InstallDependencies.Args);

        // javac and java both resolve their paths against the working directory, so these do climb out.
        Assert.Equal(
            [
                "--release", "25",
                "-classpath", Path.Combine(toProjectRoot, "target", "aspire-deps", "*"),
                "-sourcepath", $".{Path.PathSeparator}{Path.Combine("src", "main", "java")}",
                "-d", Path.Combine(toProjectRoot, "target", "classes"),
                "@.aspire/modules/sources.txt",
                "{appHostFile}"
            ],
            Assert.Single(spec.PreExecute!).Args);
        Assert.Equal(
            [
                "-cp",
                $"{Path.Combine(toProjectRoot, "target", "classes")}{Path.PathSeparator}{Path.Combine(toProjectRoot, "target", "aspire-deps", "*")}",
                "AppHost"
            ],
            spec.Execute.Args);
    }

    [Fact]
    public async Task EnsureToolchainFilesExistAsync_WithTheConventionalSourceLayout_WritesTheScriptNextToTheBuildFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "build.gradle"), "");
        WriteWrapper(workspace.Path, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");
        var appHostDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "src", "main", "java"));

        var resolution = JavaAppHostToolchainResolver.Resolve(appHostDirectory);
        await JavaAppHostToolchainResolver.EnsureToolchainFilesExistAsync(resolution, CancellationToken.None);

        // The --init-script argument is resolved relative to the AppHost directory, so the script has to
        // be where that argument points: alongside the build file it augments.
        var scriptPath = Path.Combine(workspace.Path, ".aspire", "aspire-gradle-init.gradle");
        Assert.Contains("aspireCopyDependencies", await File.ReadAllTextAsync(scriptPath));

        var spec = JavaAppHostToolchainResolver.ApplyToRuntimeSpec(CreateJavacRuntimeSpec(), resolution, appHostDirectory);
        var initScriptArgument = spec.InstallDependencies!.Args[Array.IndexOf(spec.InstallDependencies.Args, "--init-script") + 1];
        Assert.Equal(scriptPath, Path.GetFullPath(Path.Combine(appHostDirectory.FullName, initScriptArgument)));
    }
}
