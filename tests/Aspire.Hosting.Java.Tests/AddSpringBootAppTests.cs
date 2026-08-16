// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Java.Tests;

public class AddSpringBootAppTests
{
    [Fact]
    public async Task AddSpringBootApp_MavenProject_LaunchesThroughSpringBootRun()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddSpringBootApp("catalog", tempDir.Path);

        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultMavenWrapper), tempDir.Path, "spring-boot:run"), await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Fact]
    public async Task AddSpringBootApp_GradleProject_LaunchesThroughBootRun()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("build.gradle", "plugins { id 'org.springframework.boot' }");

        var app = builder.AddSpringBootApp("orders", tempDir.Path);

        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultGradleWrapper), tempDir.Path, "bootRun"), await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Fact]
    public async Task AddSpringBootApp_KotlinGradleProject_IsDetectedAsGradle()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("build.gradle.kts", "plugins { id(\"org.springframework.boot\") }");

        var app = builder.AddSpringBootApp("orders", tempDir.Path);

        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
    }

    [Theory]
    [InlineData("build.gradle")]
    [InlineData("build.gradle.kts")]
    [InlineData("settings.gradle")]
    [InlineData("settings.gradle.kts")]
    public void BuildToolDetection_GradleMarkersAgreeBetweenRunAndPublish(string marker)
    {
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write(marker, "");

        using var runBuilder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        var runApp = runBuilder.AddSpringBootApp("catalog", tempDir.Path);
        var runTool = Assert.Single(runApp.Resource.Annotations.OfType<JavaBuildToolAnnotation>()).Tool;

        using var publishBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var publishApp = publishBuilder.AddJavaApp("catalog", tempDir.Path, "build/libs/catalog.jar");
        var (publishTool, _) = JavaDockerfileGenerator.ResolveBuildTool(publishApp.Resource, tempDir.Path);

        Assert.Equal(JavaBuildTool.Gradle, runTool);
        Assert.Equal(runTool, publishTool);
    }

    [Theory]
    [InlineData("pom.xml", "-B", "-ntp", "-DskipTests", "package")]
    [InlineData("build.gradle", "build", "-x", "test")]
    public async Task AddSpringBootApp_BuildsBeforeStartingAndSkipsTests(string buildFile, params string[] expectedArgs)
    {
        // A build runs on every AppHost start, so running the test suite each time would put the whole
        // suite in front of every debug session.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write(buildFile, "");

        builder.AddSpringBootApp("catalog", tempDir.Path);

        var buildResource = Assert.Single(builder.Resources, r => r.Name.EndsWith("-build", StringComparison.Ordinal));

        var wrapper = Path.Combine(
            tempDir.Path,
            buildFile == "pom.xml" ? JavaHostingExtensions.s_defaultMavenWrapper : JavaHostingExtensions.s_defaultGradleWrapper);

        Assert.Equal(
            ExpectedWrapperInvocation.Args(wrapper, tempDir.Path, expectedArgs),
            await ArgumentEvaluator.GetArgumentListAsync(buildResource));
    }

    [Fact]
    public void AddSpringBootApp_DeclaresHttpEndpointThroughServerPort()
    {
        // Spring Boot reads SERVER_PORT, which is how the port Aspire allocates reaches the application
        // without any code in the application itself.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddSpringBootApp("catalog", tempDir.Path);

        var endpoint = Assert.Single(app.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("http", endpoint.Name);
        Assert.Equal("SERVER_PORT", endpoint.TargetPortEnvironmentVariable);

        // Pinning a target port would make two Spring Boot services collide on a real port on the machine,
        // because these run as host processes rather than containers.
        Assert.Null(endpoint.TargetPort);
    }

    [Fact]
    public void AddSpringBootApp_AddsNoHealthCheck()
    {
        // /actuator/health only exists with spring-boot-starter-actuator. Adding it unconditionally would
        // leave applications without that dependency permanently unhealthy and stall every WaitFor on them.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddSpringBootApp("catalog", tempDir.Path);

        Assert.Empty(app.Resource.Annotations.OfType<HealthCheckAnnotation>());
    }

    [Fact]
    public void AddSpringBootApp_NoBuildFile_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddSpringBootApp("catalog", tempDir.Path));

        Assert.Equal(
            $"Directory '{tempDir.Path}' contains no pom.xml, build.gradle, build.gradle.kts, settings.gradle, or settings.gradle.kts, " +
            "so the build tool for resource 'catalog' cannot be detected. Check the path, or use AddJavaApp for an application laid out differently.",
            ex.Message);
    }

    [Fact]
    public void BuildToolDetection_BothBuildToolsAreRejectedInRunAndPublish()
    {
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");
        tempDir.Write("build.gradle", "");

        using var runBuilder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        var runException = Assert.Throws<InvalidOperationException>(
            () => runBuilder.AddSpringBootApp("catalog", tempDir.Path));

        using var publishBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var publishApp = publishBuilder.AddJavaApp("catalog", tempDir.Path, "target/catalog.jar");
        var publishException = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveBuildTool(publishApp.Resource, tempDir.Path));

        Assert.Equal(
            $"Directory '{tempDir.Path}' contains both Maven and Gradle build files, so the build tool for resource 'catalog' is ambiguous. " +
            "Use AddJavaApp and call WithMavenBuild, WithGradleBuild, WithMavenGoal, or WithGradleTask to choose one explicitly.",
            runException.Message);
        Assert.Equal(runException.Message, publishException.Message);
    }

    [Theory]
    [InlineData("pom.xml", "target")]
    [InlineData("build.gradle", "build")]
    public async Task WithOtelAgent_NoPath_ResolvesTheBuildToolsOutputDirectory(string buildFile, string outputDirectory)
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write(buildFile, "");

        var app = builder.AddSpringBootApp("catalog", tempDir.Path).WithOtelAgent();

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        var expected = Path.GetFullPath(Path.Combine(tempDir.Path, outputDirectory, "agent", "opentelemetry-javaagent.jar"));
        Assert.Equal($"-javaagent:{expected}", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Theory]
    [InlineData("pom.xml", "target")]
    [InlineData("build.gradle", "build")]
    public async Task WithOtelAgent_NoPath_ResolvesTheBuildToolConfiguredAfterIt(string buildFile, string outputDirectory)
    {
        // The build tool is deliberately configured after the agent. WithWrapperPath promises order
        // independence, and the agent overload has no reason to be the one method that does not.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write(buildFile, "");

        var app = builder.AddJavaApp("catalog", tempDir.Path).WithOtelAgent();

        if (buildFile is "pom.xml")
        {
            app.WithMavenGoal("spring-boot:run").WithMavenBuild();
        }
        else
        {
            app.WithGradleTask("bootRun").WithGradleBuild();
        }

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        var expected = Path.GetFullPath(Path.Combine(tempDir.Path, outputDirectory, "agent", "opentelemetry-javaagent.jar"));
        Assert.Equal($"-javaagent:{expected}", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithOtelAgent_NoPath_WithoutABuild_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        // Resolution is deferred so the build tool can be configured afterwards, so the failure for a
        // resource that never configures one surfaces when the environment is evaluated.
        var app = builder.AddJavaApp("api", tempDir.Path).WithOtelAgent();

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance));

        Assert.Contains("has no Maven or Gradle build configured", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddSpringBootApp_RemainsAJavaAppResource()
    {
        // The helper is AddJavaApp with the Spring Boot defaults applied, so every other With… method has
        // to keep working on the result.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddSpringBootApp("catalog", tempDir.Path)
            .WithJvmArgs("-Xmx256m")
            .WithExternalHttpEndpoints();

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("-Xmx256m", envVars["JAVA_TOOL_OPTIONS"]);
        Assert.True(Assert.Single(app.Resource.Annotations.OfType<EndpointAnnotation>()).IsExternal);
    }
}
