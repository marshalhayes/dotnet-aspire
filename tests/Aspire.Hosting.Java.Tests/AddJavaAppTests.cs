// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001

using System.IO.Compression;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Java.Tests;

public class AddJavaAppTests
{
    // An absolute path on the current platform. A POSIX-style literal would be rewritten to C:\opt\...
    // by Path.GetFullPath on Windows, so the expected value has to be platform-specific.
    private static readonly string s_absoluteAgentPath =
        OperatingSystem.IsWindows() ? @"C:\opt\otel\agent.jar" : "/opt/otel/agent.jar";

    private static string AbsoluteAgentPath => s_absoluteAgentPath;

    // ---- Launch mode -------------------------------------------------------

    [Fact]
    public async Task AddJavaApp_MavenGoal_LaunchesThroughTheWrapper()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultMavenWrapper);

        var app = builder.AddJavaApp("api", tempDir.Path).WithMavenGoal("spring-boot:run");

        // The command has to become the wrapper. Leaving it as "java" while the goal was still contributed
        // as an argument produced the uninvokable command line "java spring-boot:run".
        Assert.Equal(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultMavenWrapper), app.Resource.Command);
        Assert.Equal(["spring-boot:run"], await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Fact]
    public async Task AddJavaApp_GradleTask_LaunchesThroughTheWrapper()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultGradleWrapper);

        var app = builder.AddJavaApp("api", tempDir.Path).WithGradleTask("bootRun", "--no-daemon");

        Assert.Equal(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultGradleWrapper), app.Resource.Command);
        Assert.Equal(["bootRun", "--no-daemon"], await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Fact]
    public async Task VerifyManifest_AddJavaAppWithJar()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory, "app.jar");

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "java",
              "args": [
                "-jar",
                "app.jar"
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifest_AddJavaAppWithJarAndArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory, "app.jar", ["--server.port=8080"]);

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "java",
              "args": [
                "-jar",
                "app.jar",
                "--server.port=8080"
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    // ---- Resource properties ------------------------------------------------

    [Fact]
    public void AddJavaApp_SetsResourceName()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("myapi", AppContext.BaseDirectory);

        Assert.Equal("myapi", app.Resource.Name);
    }

    [Fact]
    public void AddJavaApp_UsesJavaAsCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        Assert.Equal("java", app.Resource.Command);
    }

    [Fact]
    public void AddJavaApp_ResolvesWorkingDirectoryFullPath()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path);

        var expectedPath = Path.GetFullPath(tempDir.Path, builder.AppHostDirectory);
        Assert.Equal(expectedPath, app.Resource.WorkingDirectory);
    }

    [Fact]
    public void AddJavaApp_ImplementsIResourceWithServiceDiscovery()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        Assert.IsAssignableFrom<IResourceWithServiceDiscovery>(app.Resource);
    }

    [Fact]
    public void AddJavaApp_ImplementsIContainerFilesDestinationResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        Assert.IsAssignableFrom<IContainerFilesDestinationResource>(app.Resource);
    }

    [Fact]
    public async Task AddJavaApp_WithoutLaunchMode_ThrowsWhenArgumentsAreGathered()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        // A bare "java" with no arguments prints the JVM usage text and exits, so the failure is raised
        // where it can name the resource and the fix.
        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ArgumentEvaluator.GetArgumentListAsync(app.Resource));

        Assert.Equal(
            "Java application 'api' has no launch mode configured. Call WithMavenGoal or WithGradleTask to run it through a build tool, or use the AddJavaApp overload that takes a jarPath to run a prebuilt JAR.",
            exception.Message);
    }

    [Fact]
    public async Task AddJavaAppWithJar_ArgsAreJarAndUserArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory, "app.jar", ["--port=9090"]);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["-jar", "app.jar", "--port=9090"], args);
    }

    [Fact]
    public async Task AddJavaAppWithJar_NoUserArgs_OnlyJarArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory, "app.jar");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["-jar", "app.jar"], args);
    }

    // ---- WithMavenGoal ------------------------------------------------------

    [Fact]
    public void WithMavenGoalShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<JavaAppResource> builder = null!;

        var action = () => builder.WithMavenGoal("spring-boot:run");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithMavenGoalShouldThrowWhenGoalIsNullOrEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path);

        var nullAction = () => app.WithMavenGoal(null!);
        var emptyAction = () => app.WithMavenGoal(string.Empty);

        var nullEx = Assert.Throws<ArgumentNullException>(nullAction);
        Assert.Equal("goal", nullEx.ParamName);

        var emptyEx = Assert.Throws<ArgumentException>(emptyAction);
        Assert.Equal("goal", emptyEx.ParamName);
    }

    [Fact]
    public async Task WithMavenGoal_PassesGoalAsArgument()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Contains("spring-boot:run", args);
    }

    [Fact]
    public async Task WithMavenGoal_WithArgs_IncludesGoalAndArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run", "-DskipTests");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Contains("spring-boot:run", args);
        Assert.Contains("-DskipTests", args);
    }

    // ---- WithGradleTask -----------------------------------------------------

    [Fact]
    public void WithGradleTaskShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<JavaAppResource> builder = null!;

        var action = () => builder.WithGradleTask("bootRun");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithGradleTaskShouldThrowWhenTaskIsNullOrEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path);

        var nullAction = () => app.WithGradleTask(null!);
        var emptyAction = () => app.WithGradleTask(string.Empty);

        var nullEx = Assert.Throws<ArgumentNullException>(nullAction);
        Assert.Equal("task", nullEx.ParamName);

        var emptyEx = Assert.Throws<ArgumentException>(emptyAction);
        Assert.Equal("task", emptyEx.ParamName);
    }

    [Fact]
    public async Task WithGradleTask_PassesTaskAsArgument()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleTask("bootRun");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Contains("bootRun", args);
    }

    [Fact]
    public async Task WithGradleTask_WrapperPathIsResolved()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultGradleWrapper);

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleTask("bootRun");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // WithCommand sets the wrapper as the command, args contain only the task
        var expectedWrapper = Path.GetFullPath(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultGradleWrapper));
        Assert.Equal(expectedWrapper, app.Resource.Command);
        Assert.Contains("bootRun", args);
    }

    [Fact]
    public async Task WithMavenGoal_WrapperPathIsResolved()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultMavenWrapper);

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // WithCommand sets the wrapper as the command, args contain only the goal
        var expectedWrapper = Path.GetFullPath(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultMavenWrapper));
        Assert.Equal(expectedWrapper, app.Resource.Command);
        Assert.Contains("spring-boot:run", args);
    }

    [Fact]
    public async Task WithGradleTask_WithArgs_IncludesTaskAndArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleTask("bootRun", "--no-daemon");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Contains("bootRun", args);
        Assert.Contains("--no-daemon", args);
    }

    [Fact]
    public void WithGradleTask_ThrowsWhenJarPathIsSet()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path, "app.jar");

        var action = () => app.WithGradleTask("bootRun");

        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(
            "WithGradleTask cannot be used when a JAR path has been specified. Use either the AddJavaApp overload that takes a jarPath, or WithGradleTask, not both.",
            exception.Message);
    }

    [Fact]
    public void WithMavenGoal_ThrowsWhenJarPathIsSet()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path, "app.jar");

        var action = () => app.WithMavenGoal("spring-boot:run");

        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(
            "WithMavenGoal cannot be used when a JAR path has been specified. Use either the AddJavaApp overload that takes a jarPath, or WithMavenGoal, not both.",
            exception.Message);
    }

    [Fact]
    public void WithGradleTask_ThrowsWhenMavenGoalIsAlreadyConfigured()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path).WithMavenGoal("spring-boot:run");

        var action = () => app.WithGradleTask("bootRun");

        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(
            "WithGradleTask cannot be used when the application is already configured to launch with Maven. A Java application is launched by a single build tool.",
            exception.Message);
    }

    [Fact]
    public void WithMavenGoal_ThrowsWhenGradleTaskIsAlreadyConfigured()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path).WithGradleTask("bootRun");

        var action = () => app.WithMavenGoal("spring-boot:run");

        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(
            "WithMavenGoal cannot be used when the application is already configured to launch with Gradle. A Java application is launched by a single build tool.",
            exception.Message);
    }

    // ---- WithWrapperPath ----------------------------------------------------

    [Fact]
    public void WithWrapperPathShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<JavaAppResource> builder = null!;

        var action = () => builder.WithWrapperPath("custom-mvnw");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithWrapperPathShouldThrowWhenWrapperScriptIsNullOrEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var nullAction = () => app.WithWrapperPath(null!);
        var emptyAction = () => app.WithWrapperPath(string.Empty);

        var nullEx = Assert.Throws<ArgumentNullException>(nullAction);
        Assert.Equal("wrapperScript", nullEx.ParamName);

        var emptyEx = Assert.Throws<ArgumentException>(emptyAction);
        Assert.Equal("wrapperScript", emptyEx.ParamName);
    }

    [Fact]
    public async Task WithWrapperPath_OverridesMavenDefaultWrapper()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithWrapperPath("scripts/custom-mvnw")
            .WithMavenGoal("spring-boot:run");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // WithCommand sets the custom wrapper as the command
        var expectedWrapper = Path.GetFullPath(Path.Combine(tempDir.Path, "scripts/custom-mvnw"));
        Assert.Equal(expectedWrapper, app.Resource.Command);
        Assert.Contains("spring-boot:run", args);
    }

    [Fact]
    public async Task WithWrapperPath_OverridesGradleDefaultWrapper()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithWrapperPath("scripts/custom-gradlew")
            .WithGradleTask("bootRun");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // WithCommand sets the custom wrapper as the command
        var expectedWrapper = Path.GetFullPath(Path.Combine(tempDir.Path, "scripts/custom-gradlew"));
        Assert.Equal(expectedWrapper, app.Resource.Command);
        Assert.Contains("bootRun", args);
    }

    // ---- WithJvmArgs --------------------------------------------------------

    [Fact]
    public void WithJvmArgsShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<JavaAppResource> builder = null!;

        var action = () => builder.WithJvmArgs(["-Xmx512m"]);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithJvmArgsShouldThrowWhenArgsIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var action = () => app.WithJvmArgs(null!);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal("args", exception.ParamName);
    }

    [Fact]
    public async Task WithJvmArgs_SetsJavaToolOptions()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithJvmArgs(["-Xmx512m", "-Xms256m"]);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("-Xmx512m -Xms256m", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithJvmArgs_EmptyArgs_DoesNotSetJavaToolOptions()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithJvmArgs([]);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.False(envVars.ContainsKey("JAVA_TOOL_OPTIONS"));
    }

    [Fact]
    public async Task WithJvmArgs_MultipleCalls_MergeValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithJvmArgs(["-Xmx512m"])
            .WithJvmArgs(["-Xms256m"]);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("-Xmx512m -Xms256m", envVars["JAVA_TOOL_OPTIONS"]);
    }

    // ---- WithOtelAgent ------------------------------------------------------

    [Fact]
    public void WithOtelAgentShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<JavaAppResource> builder = null!;

        var action = () => builder.WithOtelAgent("/opt/otel/agent.jar");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithOtelAgentShouldThrowWhenAgentPathIsNullOrWhiteSpace()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        Assert.Throws<ArgumentException>(() => app.WithOtelAgent("  "));
    }

    [Fact]
    public async Task AddJavaApp_ConfiguresOtlpExporterWithoutAnAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        // The OTLP exporter is wired by AddJavaApp itself, so a Java application reports telemetry
        // through Micrometer/OTel SDK instrumentation even when the Java agent is not used.
        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.True(envVars.ContainsKey("OTEL_EXPORTER_OTLP_ENDPOINT"));
    }

    [Fact]
    public async Task WithOtelAgent_WithAgentPath_SetsJavaAgentInToolOptions()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithOtelAgent(AbsoluteAgentPath);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"-javaagent:{AbsoluteAgentPath}", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithOtelAgent_CalledTwice_UsesOnlyTheLastAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        // The annotation replaces, so the second call has to win outright. Two -javaagent: entries
        // would start the JVM with both agents attached, which double-instruments the application.
        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithOtelAgent("/opt/otel/first.jar")
            .WithOtelAgent(AbsoluteAgentPath);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"-javaagent:{AbsoluteAgentPath}", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithOtelAgent_WithAgentPath_CombinedWithJvmArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithJvmArgs(["-Xmx512m"])
            .WithOtelAgent(AbsoluteAgentPath);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"-Xmx512m -javaagent:{AbsoluteAgentPath}", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithMavenGoal_WithoutAWrapperOnDisk_IsRejectedWhenTheResourceStarts()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory(withWrappers: false);

        // A globally installed Maven is deliberately not used as a fallback: the wrapper pins the tool
        // version in the repository, so the AppHost, CI, and the published image all build with the same
        // one. Failing here names the fix instead of silently building with whatever is on the machine.
        var app = builder.AddJavaApp("api", tempDir.Path).WithMavenGoal("spring-boot:run");

        using var built = builder.Build();

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(app.Resource, built.Services),
                CancellationToken.None));

        Assert.Contains("has no mvnw", ex.Message);
        Assert.Contains("mvn -N wrapper:wrapper", ex.Message);
    }

    [Fact]
    public async Task WithGradleTask_WithoutAWrapperOnDisk_IsRejectedWhenTheResourceStarts()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory(withWrappers: false);

        var app = builder.AddJavaApp("api", tempDir.Path).WithGradleTask("bootRun");

        using var built = builder.Build();

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(app.Resource, built.Services),
                CancellationToken.None));

        Assert.Contains("has no gradlew", ex.Message);
        Assert.Contains("gradle wrapper", ex.Message);
    }

    [Fact]
    public async Task WithWrapperPath_AfterTheBuildTool_WorksWhenTheProjectHasNoDefaultWrapper()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory(withWrappers: false);

        var customWrapper = Path.Combine(tempDir.Path, "tools", "mvnw");
        Directory.CreateDirectory(Path.GetDirectoryName(customWrapper)!);
        File.WriteAllText(customWrapper, "#!/bin/sh\n");

        // WithWrapperPath is documented as order-independent. Resolving the wrapper eagerly inside
        // WithMavenGoal made that untrue for the one project shape where the override actually matters:
        // a project whose only wrapper is the custom one threw before the override could be applied.
        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run")
            .WithWrapperPath(Path.Combine("tools", "mvnw"));

        using var built = builder.Build();

        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(app.Resource, built.Services),
            CancellationToken.None);

        // Path.GetFullPath matches the normalization the resource applies, and resolves the symlinked
        // temp directory the same way on both sides so the comparison is about the wrapper, not the path.
        Assert.Equal(Path.GetFullPath(customWrapper), app.Resource.Command);
    }

    [Fact]
    public async Task WithWrapperPath_PointingAtAMissingFile_IsRejectedWhenTheResourceStarts()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory(withWrappers: false);

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run")
            .WithWrapperPath("tools/mvnw");

        using var built = builder.Build();

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(app.Resource, built.Services),
                CancellationToken.None));

        // The override is what is wrong here, so the message points at WithWrapperPath rather than
        // telling the user to generate a wrapper they did not ask for.
        Assert.Contains(nameof(JavaHostingExtensions.WithWrapperPath), ex.Message);
        Assert.DoesNotContain("mvn -N wrapper:wrapper", ex.Message);
    }

    [Fact]
    public void WithWrapperPath_IsHonouredEvenWhenNoWrapperExistsOnDisk()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory(withWrappers: false);

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithWrapperPath("/opt/maven/bin/mvn")
            .WithMavenGoal("spring-boot:run");

        // An explicit override is a deliberate choice and must win over the default wrapper probe, which
        // would otherwise reject this project for shipping no mvnw.
        Assert.Equal("/opt/maven/bin/mvn", app.Resource.Command);
    }

    [Fact]
    public void AddJavaApp_RequestsSystemCertificateTrustScope()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        // -Djavax.net.ssl.trustStore replaces the JVM's trust anchors rather than adding to them, so the
        // generated bundle has to contain the system roots as well. Under the default Append scope the
        // bundle would hold only Aspire's own certificates and the JVM would stop trusting every public
        // CA -- which also breaks Maven Central and Gradle distribution downloads, because
        // JAVA_TOOL_OPTIONS is inherited by the build tool's JVM.
        Assert.True(app.Resource.TryGetLastAnnotation<CertificateAuthorityCollectionAnnotation>(out var certAnnotation));
        Assert.Equal(CertificateTrustScope.System, certAnnotation.Scope);
    }

    [Fact]
    public async Task AddJavaApp_WithAppendCertificateTrustScope_DoesNotOverrideTheJvmTrustStore()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var javaApp = builder.AddJavaApp("api", AppContext.BaseDirectory);

        using var app = builder.Build();

        Assert.True(javaApp.Resource.TryGetLastAnnotation<CertificateTrustConfigurationCallbackAnnotation>(out var annotation));

        var envVars = new Dictionary<string, object>();
        await annotation.Callback(new CertificateTrustConfigurationCallbackAnnotationContext
        {
            ExecutionContext = new DistributedApplicationExecutionContext(
                new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)
                {
                    Services = app.Services
                }),
            Resource = javaApp.Resource,
            Arguments = [],
            EnvironmentVariables = envVars,
            CertificateBundlePath = ReferenceExpression.Create($"/etc/ssl/aspire/bundle.p12"),
            CertificateDirectoriesPath = ReferenceExpression.Create($"/etc/ssl/aspire/certs"),
            Scope = CertificateTrustScope.Append,
            CancellationToken = default
        });

        // Under Append the bundle holds only Aspire's own certificates. Pointing the JVM at it would
        // drop every public certificate authority, so the override is skipped entirely rather than
        // applied against an incomplete bundle.
        Assert.Empty(envVars);
    }

    [Fact]
    public async Task WithOtelAgent_AgentPathContainingSpaces_IsQuotedForTheJvm()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var agentPath = OperatingSystem.IsWindows()
            ? @"C:\opt\java agents\opentelemetry-javaagent.jar"
            : "/opt/java agents/opentelemetry-javaagent.jar";

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithOtelAgent(agentPath);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        // The JVM splits JAVA_TOOL_OPTIONS on whitespace but honours double quotes, so an unquoted path
        // containing a space aborts startup with "Unrecognized option: agents/opentelemetry-javaagent.jar"
        // before any application code runs. -javaagent: has no '=' so the whole option is quoted.
        Assert.Equal($"\"-javaagent:{agentPath}\"", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithJvmArgs_ValueContainingSpaces_IsQuotedAfterTheAssignment()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithJvmArgs(["-Dapp.data.dir=/var/lib/my app"]);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        // For an option with an '=' only the value is quoted. Quoting the whole option would make the JVM
        // treat "-Dapp.data.dir" as part of the property name and the property would silently not be set.
        Assert.Equal("-Dapp.data.dir=\"/var/lib/my app\"", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithOtelAgent_RelativeAgentPath_IsMadeAbsoluteInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithOtelAgent(Path.Combine("target", "agent", "opentelemetry-javaagent.jar"));

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        // JAVA_TOOL_OPTIONS is inherited by every JVM started beneath the resource, and build tools start
        // JVMs from directories other than the application directory. The Gradle daemon in particular
        // starts from its own distribution directory, so a relative -javaagent: path fails to resolve and
        // the daemon dies during VM initialization rather than reporting a normal build failure.
        var expected = Path.GetFullPath(
            Path.Combine(app.Resource.WorkingDirectory, "target", "agent", "opentelemetry-javaagent.jar"));

        Assert.Equal($"-javaagent:{expected}", envVars["JAVA_TOOL_OPTIONS"]);
        Assert.True(Path.IsPathFullyQualified(expected));
    }

    [Fact]
    public async Task WithOtelAgent_RelativeAgentPath_PointsAtContainerPathInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithOtelAgent("target/agent/opentelemetry-javaagent.jar");

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        // The path has to be interpreted inside the container, so a build-machine path would be wrong.
        // The generated Dockerfile copies the build-produced agent to a fixed location, and this has to
        // agree with it or the container starts a JVM pointing at a JAR that is not in the image.
        Assert.Equal("-javaagent:/app/agent.jar", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithOtelAgent_AbsoluteAgentPath_IsLeftUnchangedInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithOtelAgent(AbsoluteAgentPath);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        // An absolute path cannot have come out of the build context, so it is the base image's or a
        // mount's responsibility and rewriting it would break that arrangement.
        Assert.Equal($"-javaagent:{AbsoluteAgentPath}", envVars["JAVA_TOOL_OPTIONS"]);
    }

    // ---- WithMavenBuild / WithGradleBuild -----------------------------------

    [Fact]
    public void WithMavenBuild_CreatesMavenBuildResourceInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithMavenBuild();

        Assert.Contains(builder.Resources, r => r.Name == "api-maven-build");
        Assert.IsType<MavenBuildResource>(builder.Resources.First(r => r.Name == "api-maven-build"));
    }

    [Fact]
    public void WithMavenBuild_CustomArgs_CreatesBuildResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithMavenBuild("clean", "install", "-DskipTests");

        var buildResource = builder.Resources.First(r => r.Name == "api-maven-build");
        Assert.IsType<MavenBuildResource>(buildResource);
    }

    [Fact]
    public void WithGradleBuild_CreatesGradleBuildResourceInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithGradleBuild();

        Assert.Contains(builder.Resources, r => r.Name == "api-gradle-build");
        Assert.IsType<GradleBuildResource>(builder.Resources.First(r => r.Name == "api-gradle-build"));
    }

    [Fact]
    public void WithGradleBuild_CustomArgs_CreatesBuildResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithGradleBuild("clean", "assemble", "--info");

        var buildResource = builder.Resources.First(r => r.Name == "api-gradle-build");
        Assert.IsType<GradleBuildResource>(buildResource);
    }

    [Fact]
    public void WithMavenBuild_DoesNotCreateBuildResourceInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithMavenBuild();

        Assert.DoesNotContain(builder.Resources, r => r.Name == "api-maven-build");
    }

    [Fact]
    public void WithGradleBuild_DoesNotCreateBuildResourceInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithGradleBuild();

        Assert.DoesNotContain(builder.Resources, r => r.Name == "api-gradle-build");
    }

    [Fact]
    public void WithMavenBuild_BuildResourceHasParentRelationship()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenBuild();

        var buildResource = builder.Resources.First(r => r.Name == "api-maven-build");
        Assert.True(buildResource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        Assert.Contains(relationships, r => r.Type == "Parent" && r.Resource == app.Resource);
    }

    [Fact]
    public void WithGradleBuild_BuildResourceHasParentRelationship()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleBuild();

        var buildResource = builder.Resources.First(r => r.Name == "api-gradle-build");
        Assert.True(buildResource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        Assert.Contains(relationships, r => r.Type == "Parent" && r.Resource == app.Resource);
    }

    // ---- JAR path -----------------------------------------------------------

    [Fact]
    public async Task AddJavaApp_WithJarPath_LaunchesTheJar()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory, "target/app.jar");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal("java", app.Resource.Command);
        Assert.Equal(["-jar", "target/app.jar"], args);
    }

    // ---- VS Code debugging --------------------------------------------------

    [Fact]
    public void AddJavaApp_InRunMode_SupportsDebugging()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var annotation = app.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal("java", annotation!.LaunchConfigurationType);
    }

    [Fact]
    public void AddJavaApp_InPublishMode_DoesNotAddDebuggingAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish).WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var annotation = app.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.Null(annotation);
    }

    // ---- Chaining multiple methods ------------------------------------------

    [Fact]
    public async Task WithMavenGoal_ThenWithJvmArgs_SetsBothConfigurations()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run")
            .WithJvmArgs(["-Xmx1g"]);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);
        Assert.Contains("spring-boot:run", args);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        Assert.Equal("-Xmx1g", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithGradleTask_ThenWithOtelAgent_SetsBothConfigurations()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleTask("bootRun")
            .WithOtelAgent(AbsoluteAgentPath);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);
        Assert.Contains("bootRun", args);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        Assert.Equal($"-javaagent:{AbsoluteAgentPath}", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithWrapperPath_ThenWithMavenGoal_UsesCustomWrapper()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithWrapperPath("tools/mvn")
            .WithMavenGoal("spring-boot:run");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // WithCommand sets the custom wrapper as the command
        var expectedWrapper = Path.GetFullPath(Path.Combine(tempDir.Path, "tools/mvn"));
        Assert.Equal(expectedWrapper, app.Resource.Command);
        Assert.Contains("spring-boot:run", args);
    }

    // ---- Manifest with Maven/Gradle goals -----------------------------------

    [Fact]
    public async Task VerifyManifest_WithMavenGoal()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run");

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        // The manifest should show the maven wrapper as the command with the goal as args.
        var args = manifest?["args"]?.AsArray();
        Assert.NotNull(args);
        Assert.Contains("spring-boot:run", args!.Select(a => a?.ToString()));
    }

    [Fact]
    public async Task VerifyManifest_WithGradleTask()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleTask("bootRun");

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var args = manifest?["args"]?.AsArray();
        Assert.NotNull(args);
        Assert.Contains("bootRun", args!.Select(a => a?.ToString()));
    }

    private static async Task<JavaLaunchConfiguration> GetLaunchConfigurationAsync(IResourceBuilder<JavaAppResource> app)
    {
        var annotation = Assert.Single(app.Resource.Annotations.OfType<SupportsDebuggingAnnotation>());
        Assert.Equal("java", annotation.LaunchConfigurationType);

        var context = new LaunchConfigurationCallbackContext(
            ExecutableLaunchMode.Debug,
            app.Resource,
            new Dictionary<string, string>(),
            CancellationToken.None);

        return Assert.IsType<JavaLaunchConfiguration>(await annotation.LaunchConfigurationProducer(context));
    }

    /// <summary>
    /// Writes a JAR whose manifest declares <paramref name="mainClass"/>. A JAR is a ZIP archive, so
    /// the entry only has to exist at META-INF/MANIFEST.MF with the documented Name: value shape.
    /// </summary>
    private static string WriteJarWithManifest(string directory, string fileName, string? mainClass, bool wrapLongValue = false)
    {
        var jarPath = Path.Combine(directory, fileName);
        using var archive = ZipFile.Open(jarPath, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("META-INF/MANIFEST.MF").Open());

        writer.Write("Manifest-Version: 1.0\r\n");
        if (mainClass is not null)
        {
            if (wrapLongValue)
            {
                // The manifest format limits a line to 72 bytes and continues longer values on the
                // next line with a single leading space, which is not part of the value.
                var split = mainClass.Length / 2;
                writer.Write($"Main-Class: {mainClass[..split]}\r\n {mainClass[split..]}\r\n");
            }
            else
            {
                writer.Write($"Main-Class: {mainClass}\r\n");
            }
        }

        writer.Write("\r\n");
        return jarPath;
    }

    [Fact]
    public async Task AddJavaApp_WithMavenBuild_SendsNoMainClassSoTheIdeResolvesItFromTheProject()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultMavenWrapper);

        var app = builder.AddJavaApp("api", tempDir.Path).WithMavenGoal("spring-boot:run");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        Assert.Null(launchConfiguration.MainClass);
        Assert.Null(launchConfiguration.ClassPaths);
        Assert.Equal("maven", launchConfiguration.BuildTool);
        Assert.Equal(tempDir.Path, launchConfiguration.WorkingDirectory);
    }

    [Fact]
    public async Task AddJavaApp_WithJar_PutsTheArchiveOnTheClasspathAndLaunchesItsManifestMainClass()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        var jarPath = WriteJarWithManifest(tempDir.Path, "api.jar", "com.example.catalog.CatalogApplication");

        var app = builder.AddJavaApp("api", tempDir.Path, "api.jar");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        // The archive itself is never the main class: the debug adapter documents that attribute as a
        // fully qualified class name or a .java path, so a JAR path leaves it unable to resolve an
        // entry point. The archive belongs on the classpath instead.
        Assert.Equal("com.example.catalog.CatalogApplication", launchConfiguration.MainClass);
        Assert.Equal(jarPath, Assert.Single(launchConfiguration.ClassPaths!));
        Assert.Null(launchConfiguration.BuildTool);
    }

    [Fact]
    public async Task AddJavaApp_WithJar_ReadsAMainClassThatTheManifestWrappedAcrossLines()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        const string MainClass = "com.example.catalog.averylongpackagename.that.forces.wrapping.CatalogApplication";
        WriteJarWithManifest(tempDir.Path, "api.jar", MainClass, wrapLongValue: true);

        var app = builder.AddJavaApp("api", tempDir.Path, "api.jar");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        Assert.Equal(MainClass, launchConfiguration.MainClass);
    }

    [Fact]
    public async Task AddJavaApp_WithJar_WithMainClass_PrefersTheExplicitMainClassOverTheManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        var jarPath = WriteJarWithManifest(tempDir.Path, "api.jar", "org.springframework.boot.loader.JarLauncher");

        var app = builder.AddJavaApp("api", tempDir.Path, "api.jar")
            .WithMainClass("com.example.catalog.CatalogApplication");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        Assert.Equal("com.example.catalog.CatalogApplication", launchConfiguration.MainClass);
        Assert.Equal(jarPath, Assert.Single(launchConfiguration.ClassPaths!));
    }

    [Fact]
    public async Task AddJavaApp_WithJar_ThatIsMissingOrHasNoMainClass_StillSendsTheClasspath()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        WriteJarWithManifest(tempDir.Path, "no-main.jar", mainClass: null);

        var noMainClass = builder.AddJavaApp("no-main", tempDir.Path, "no-main.jar");
        var missing = builder.AddJavaApp("missing", tempDir.Path, "does-not-exist.jar");

        // Neither case is fatal: the IDE can still resolve an entry point from the project, and
        // failing the launch over an unreadable archive would be worse than letting it try.
        var noMainClassConfiguration = await GetLaunchConfigurationAsync(noMainClass);
        Assert.Null(noMainClassConfiguration.MainClass);
        Assert.Equal(Path.Combine(tempDir.Path, "no-main.jar"), Assert.Single(noMainClassConfiguration.ClassPaths!));

        var missingConfiguration = await GetLaunchConfigurationAsync(missing);
        Assert.Null(missingConfiguration.MainClass);
        Assert.Equal(Path.Combine(tempDir.Path, "does-not-exist.jar"), Assert.Single(missingConfiguration.ClassPaths!));
    }
}
