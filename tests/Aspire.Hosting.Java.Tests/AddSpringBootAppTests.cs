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

        Assert.Equal(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultMavenWrapper), app.Resource.Command);
        Assert.Equal(["spring-boot:run"], await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Fact]
    public async Task AddSpringBootApp_GradleProject_LaunchesThroughBootRun()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("build.gradle", "plugins { id 'org.springframework.boot' }");

        var app = builder.AddSpringBootApp("orders", tempDir.Path);

        Assert.Equal(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultGradleWrapper), app.Resource.Command);
        Assert.Equal(["bootRun"], await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Fact]
    public async Task AddSpringBootApp_KotlinGradleProject_IsDetectedAsGradle()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("build.gradle.kts", "plugins { id(\"org.springframework.boot\") }");

        var app = builder.AddSpringBootApp("orders", tempDir.Path);

        Assert.Equal(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultGradleWrapper), app.Resource.Command);
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

        Assert.Equal(expectedArgs, await ArgumentEvaluator.GetArgumentListAsync(buildResource));
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

        Assert.Contains("no pom.xml, build.gradle, or build.gradle.kts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddSpringBootApp_BothBuildFiles_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");
        tempDir.Write("build.gradle", "");

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddSpringBootApp("catalog", tempDir.Path));

        Assert.Contains("both a Maven and a Gradle build file", ex.Message, StringComparison.Ordinal);
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

        AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        var expected = Path.GetFullPath(Path.Combine(tempDir.Path, outputDirectory, "agent", "opentelemetry-javaagent.jar"));
        Assert.Equal($"-javaagent:{expected}", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public void WithOtelAgent_NoPath_WithoutABuild_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path);

        var ex = Assert.Throws<InvalidOperationException>(() => app.WithOtelAgent());

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

        AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("-Xmx256m", envVars["JAVA_TOOL_OPTIONS"]);
        Assert.True(Assert.Single(app.Resource.Annotations.OfType<EndpointAnnotation>()).IsExternal);
    }

    // Endpoints are allocated by the orchestrator at run time. Environment variable evaluation waits on that
    // allocation, so a test that never starts the application has to supply it or the evaluation never returns.
    private static void AllocateEndpoints(IResource resource)
    {
        foreach (var endpoint in resource.Annotations.OfType<EndpointAnnotation>())
        {
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 8080, targetPortExpression: "8080");
        }
    }
}
