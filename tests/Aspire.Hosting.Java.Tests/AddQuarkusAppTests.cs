// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Java.Tests;

public class AddQuarkusAppTests
{
    [Fact]
    public async Task AddQuarkusApp_MavenProject_LaunchesInDevMode()
    {
        // Dev mode is what "run my Quarkus application locally" means: it is the only mode with live coding,
        // and it is what the Quarkus documentation tells every reader to start with.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultMavenWrapper), tempDir.Path, "quarkus:dev"), await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Fact]
    public async Task AddQuarkusApp_GradleProject_LaunchesInDevMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("build.gradle", "plugins { id 'io.quarkus' }");

        var app = builder.AddQuarkusApp("pricing", tempDir.Path);

        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultGradleWrapper), tempDir.Path, "quarkusDev"), await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Theory]
    [InlineData("pom.xml", "-B", "-ntp", "-DskipTests", "package")]
    [InlineData("build.gradle", "build", "-x", "test")]
    public async Task AddQuarkusApp_BuildsBeforeStartingAndSkipsTests(string buildFile, params string[] expectedArgs)
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write(buildFile, "");

        builder.AddQuarkusApp("inventory", tempDir.Path);

        var buildResource = Assert.Single(builder.Resources, r => r.Name.EndsWith("-build", StringComparison.Ordinal));

        var wrapper = Path.Combine(
            tempDir.Path,
            buildFile == "pom.xml" ? JavaHostingExtensions.s_defaultMavenWrapper : JavaHostingExtensions.s_defaultGradleWrapper);

        Assert.Equal(
            ExpectedWrapperInvocation.Args(wrapper, tempDir.Path, expectedArgs),
            await ArgumentEvaluator.GetArgumentListAsync(buildResource));
    }

    [Fact]
    public void AddQuarkusApp_DeclaresHttpEndpointThroughQuarkusHttpPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        var endpoint = Assert.Single(app.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("http", endpoint.Name);
        Assert.Equal("QUARKUS_HTTP_PORT", endpoint.TargetPortEnvironmentVariable);
        Assert.Null(endpoint.TargetPort);
    }

    [Fact]
    public void AddQuarkusApp_GradleProject_DeclaresHttpEndpointThroughQuarkusHttpPort()
    {
        // The endpoint has to be declared for both build tools. Attaching it to only one branch of the
        // detection is an easy mistake that leaves half of all applications with no endpoint at all.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("build.gradle", "plugins { id 'io.quarkus' }");

        var app = builder.AddQuarkusApp("pricing", tempDir.Path);

        var endpoint = Assert.Single(app.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("QUARKUS_HTTP_PORT", endpoint.TargetPortEnvironmentVariable);
    }

    [Fact]
    public async Task AddQuarkusApp_SetsTheDevProfileInRunMode()
    {
        // The IDE launches the packaged application rather than quarkus:dev, so the profile has to be set
        // as an environment variable for both to resolve the same %dev. configuration.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("dev", envVars["QUARKUS_PROFILE"]);
    }

    [Fact]
    public async Task AddQuarkusApp_DoesNotSetTheDevProfileWhenPublishing()
    {
        // A published image runs the packaged application, which must resolve prod configuration.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.False(envVars.ContainsKey("QUARKUS_PROFILE"));
    }

    [Fact]
    public async Task AddQuarkusApp_DisablesTheObservabilityDevServiceInRunMode()
    {
        // Left on, the Dev Service pulls grafana/otel-lgtm and repoints the exporter at that container,
        // so every span and metric lands somewhere the Aspire dashboard cannot see.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("false", envVars["QUARKUS_OBSERVABILITY_ENABLED"]);
    }

    [Fact]
    public async Task AddQuarkusApp_MirrorsTheOtlpConfigurationOntoTheNamesQuarkusReads()
    {
        // quarkus-opentelemetry reads quarkus.otel.*, not the standard OTEL_* names, so without the mirror
        // it keeps exporting to its own localhost:4317 default.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(envVars["OTEL_EXPORTER_OTLP_ENDPOINT"], envVars["QUARKUS_OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Assert.Equal(envVars["OTEL_EXPORTER_OTLP_PROTOCOL"], envVars["QUARKUS_OTEL_EXPORTER_OTLP_PROTOCOL"]);
        Assert.Equal(envVars["OTEL_SERVICE_NAME"], envVars["QUARKUS_OTEL_SERVICE_NAME"]);
        Assert.Equal(envVars["OTEL_RESOURCE_ATTRIBUTES"], envVars["QUARKUS_OTEL_RESOURCE_ATTRIBUTES"]);
    }

    [Fact]
    public async Task AddQuarkusApp_DoesNotMirrorTheOtlpConfigurationWhenPublishing()
    {
        // WithOtlpExporter contributes nothing in publish mode, so there is nothing to mirror. The mirror
        // must not invent a value either: an OTLP endpoint baked in here would be the AppHost's, not the
        // one the compute environment goes on to supply. A deployed application maps the value in its own
        // application.properties instead, which both Quarkus playgrounds do.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.False(envVars.ContainsKey("QUARKUS_OTEL_EXPORTER_OTLP_ENDPOINT"));
        Assert.False(envVars.ContainsKey("QUARKUS_OBSERVABILITY_ENABLED"));
    }

    [Fact]
    public void AddQuarkusApp_AddsNoHealthCheck()
    {
        // /q/health only exists with the smallrye-health extension. Adding it unconditionally would leave
        // applications without that extension permanently unhealthy and stall every WaitFor on them.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        Assert.Empty(app.Resource.Annotations.OfType<HealthCheckAnnotation>());
    }

    [Fact]
    public void AddQuarkusApp_NoBuildFile_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddQuarkusApp("inventory", tempDir.Path));

        Assert.Equal(
            $"Directory '{tempDir.Path}' contains no pom.xml, build.gradle, build.gradle.kts, settings.gradle, or settings.gradle.kts, " +
            "so the build tool for resource 'inventory' cannot be detected. Check the path, or use AddJavaApp for an application laid out differently.",
            ex.Message);
    }

    [Fact]
    public async Task AddQuarkusApp_RemainsAJavaAppResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path)
            .WithJvmArgs("-Xmx256m")
            .WithExternalHttpEndpoints();

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("-Xmx256m", envVars["JAVA_TOOL_OPTIONS"]);
    }
}
