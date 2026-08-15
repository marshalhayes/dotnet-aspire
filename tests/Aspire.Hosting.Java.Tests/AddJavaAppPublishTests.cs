// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.Hosting.Tests.Utils;
using System.Diagnostics;
using System.Text.Json;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Java.Tests;

public class AddJavaAppPublishTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task VerifyPublish_GeneratesAMavenBuildAndJreRuntimePair()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.StartsWith("FROM --platform=$BUILDPLATFORM docker.io/library/eclipse-temurin:21-jdk AS build", content);
        Assert.Contains("\nFROM docker.io/library/eclipse-temurin:21-jre\n", content);
        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_GeneratesAGradleBuild()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WriteGradleBuild(source, """
                java {
                    toolchain {
                        languageVersion = JavaLanguageVersion.of(17)
                    }
                }
                """),
            configureResource: app => app.WithGradleTask("bootRun"));

        Assert.StartsWith("FROM --platform=$BUILDPLATFORM docker.io/library/eclipse-temurin:17-jdk AS build", content);
        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_CopiesABuildProducedOtelAgentIntoTheRuntimeImage()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app
                .WithMavenGoal("spring-boot:run")
                .WithOtelAgent("target/agent/opentelemetry-javaagent.jar"));

        // The agent is produced by the build, so it exists only in the build stage. Without this COPY the
        // published container starts a JVM pointing at a JAR that is not in the image and dies during VM
        // initialization with "Error opening zip file or JAR manifest missing".
        Assert.Contains(
            "COPY --from=build /app/target/agent/opentelemetry-javaagent.jar /app/agent.jar",
            content);
        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_StripsExactlyOneLeadingDotSlashFromTheOtelAgentPath()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app
                .WithMavenGoal("spring-boot:run")
                .WithOtelAgent("./target/agent/opentelemetry-javaagent.jar"));

        Assert.Contains(
            "COPY --from=build /app/target/agent/opentelemetry-javaagent.jar /app/agent.jar",
            content);
    }

    [Fact]
    public void AnOtelAgentPathOutsideTheBuildContextIsRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run")
            .WithOtelAgent("../agents/opentelemetry-javaagent.jar");

        // The Docker build context is the application directory, so "../" can never be copied forward.
        // Trimming the leading dots instead would emit a COPY for "agents/opentelemetry-javaagent.jar" --
        // a path the author never wrote -- and fail the container build with a confusing message.
        var exception = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.TryGetBuildProducedAgentPath(app.Resource, out _));

        Assert.Contains("../agents/opentelemetry-javaagent.jar", exception.Message);
        Assert.Contains("outside the application directory", exception.Message);
    }

    [Fact]
    public async Task VerifyPublish_DoesNotCopyAnAbsoluteOtelAgentPath()
    {
        var agentPath = OperatingSystem.IsWindows() ? @"C:\opt\otel\agent.jar" : "/opt/otel/agent.jar";

        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app
                .WithMavenGoal("spring-boot:run")
                .WithOtelAgent(agentPath));

        // An absolute path cannot have come out of the build context, so there is nothing to copy from.
        Assert.DoesNotContain("/app/agent.jar", content);
    }

    [Fact]
    public async Task VerifyPublish_UsesTheWrapperWhenTheProjectShipsOne()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source =>
            {
                WritePom(source, javaVersion: "21");
            },
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        // sh ./mvnw rather than ./mvnw: a wrapper checked out from a Windows clone arrives without the
        // executable bit, and invoking the interpreter directly does not depend on the file mode.
        Assert.StartsWith("FROM --platform=$BUILDPLATFORM docker.io/library/eclipse-temurin:21-jdk AS build", content);
        Assert.Contains("sh ./mvnw -B -ntp -DskipTests package", content);
        await Verify(content);
    }

    [Fact]
    public void PublishingAProjectWithoutAWrapperIsRejectedRatherThanUsingTheImageTool()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        File.WriteAllText(Path.Combine(sourceDir.FullName, "build.gradle"), "");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // No goal is configured, so nothing resolves a wrapper before the image is generated: the tool is
        // detected from the build file on disk and this is the only check that runs.
        var app = builder.AddJavaApp("api", sourceDir.FullName, "build/libs/api.jar");

        var ex = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveContainerBuildForTesting(app.Resource, sourceDir.FullName));

        // A globally installed Gradle in the build image would build the project with a different version
        // than the developer used, which is exactly what the wrapper exists to prevent.
        Assert.Contains("there is no gradlew", ex.Message);
        Assert.Contains("gradle wrapper", ex.Message);
    }

    [Fact]
    public async Task VerifyPublish_ReusesTheArgumentsConfiguredForTheHostBuildStep()
    {
        // The host-side build step only runs in run mode, but the arguments it was given describe how this
        // project produces a deployable artifact, so the container build runs the same ones.
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run").WithMavenBuild("-Pprod", "package"));

        Assert.Contains("sh ./mvnw -Pprod package", content);
        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_DetectsTheBuildToolFromDiskWhenOnlyAJarPathWasGiven()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            jarPath: "target/worker.jar");

        Assert.Contains("sh ./mvnw -B -ntp -DskipTests package", content);
        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_CopiesTheExplicitArtifactWhenWithJarArtifactIsUsed()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run").WithJarArtifact("target/custom-name.jar"));

        Assert.Contains("cp 'target/custom-name.jar' /build/app.jar", content);
        // The glob selection is what WithJarArtifact exists to replace, so it must not also be emitted.
        Assert.DoesNotContain("expected exactly one application JAR", content);
    }

    [Fact]
    public async Task VerifyPublish_FailsTheContainerBuildWhenTheJarIsAmbiguous()
    {
        // Spring Boot's plugin writes app.jar next to the base plugin's app-plain.jar. The -plain suffix is
        // filtered, but anything still ambiguous has to stop the build rather than pick one arbitrarily and
        // produce an image that exits with "no main manifest attribute".
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.Contains("grep -Ev '(-plain|-sources|-javadoc)\\.jar$'", content);
        Assert.Contains("exit 1", content);
    }

    [Fact]
    public async Task VerifyPublish_HonoursWithDockerfileBaseImage()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app
                .WithMavenGoal("spring-boot:run")
                .WithDockerfileBaseImage("docker.io/library/amazoncorretto:21", "docker.io/library/amazoncorretto:21-alpine"));

        Assert.StartsWith("FROM --platform=$BUILDPLATFORM docker.io/library/amazoncorretto:21 AS build", content);
        Assert.Contains("\nFROM docker.io/library/amazoncorretto:21-alpine\n", content);
        // Alpine's busybox tools take different switches than the glibc images the JRE default uses.
        Assert.Contains("addgroup -S app && adduser -S -G app app", content);
        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_RunsAsANonRootUser()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.Contains("useradd --system --gid 999 --uid 999 --no-create-home app", content);
        Assert.Contains("\nUSER app\n", content);
    }

    [Fact]
    public async Task VerifyPublish_UsesTheExecFormEntrypointSoTheJvmReceivesSigterm()
    {
        // With the shell form the JVM is not PID 1 and never sees SIGTERM, so Spring's shutdown hooks are
        // skipped and the container is killed after the stop timeout instead.
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.Contains("""ENTRYPOINT ["java","-jar","/app/app.jar"]""", content);
    }

    [Fact]
    public async Task VerifyPublish_EmitsABuildContextIgnoreThatExcludesBuildOutputDirectories()
    {
        var ignore = await PublishBuildContextIgnoreAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.NotNull(ignore);
        await Verify(ignore);
    }

    [Fact]
    public async Task VerifyPublish_LeavesAnAuthoredDockerignoreAlone()
    {
        // A <dockerfile>.dockerignore replaces the context root's .dockerignore rather than merging with it,
        // so generating one would silently drop every rule the author wrote.
        var ignore = await PublishBuildContextIgnoreAsync(
            configureSource: source =>
            {
                WritePom(source, javaVersion: "21");
                File.WriteAllText(Path.Combine(source, ".dockerignore"), "secrets/\n");
            },
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.Null(ignore);
    }

    [Fact]
    public async Task VerifyPublish_LeavesAnAuthoredDockerfileAlone()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        WritePom(sourceDir.FullName, javaVersion: "21");
        File.WriteAllText(Path.Combine(sourceDir.FullName, "Dockerfile"), "FROM scratch\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddJavaApp("api", sourceDir.FullName).WithMavenGoal("spring-boot:run");
        builder.Build().Run();

        Assert.False(File.Exists(Path.Combine(outputDir.FullName, "api.Dockerfile")));
    }

    [Fact]
    public async Task VerifyPublish_ProducesAContainerManifestEntry()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        WritePom(sourceDir.FullName, javaVersion: "21");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddJavaApp("api", sourceDir.FullName)
               .WithMavenGoal("spring-boot:run")
               .WithHttpEndpoint(targetPort: 8080, env: "SERVER_PORT");

        builder.Build().Run();

        var manifest = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "aspire-manifest.json"), TestContext.Current.CancellationToken);

        await Verify(manifest, "json")
            .ScrubLinesWithReplace(line => line.Contains("\"context\"", StringComparison.Ordinal) ? "      \"context\": \"{sourceDirectory}\"," : line);
    }

    [Theory]
    [InlineData("<java.version>17</java.version>", "17")]
    [InlineData("<maven.compiler.release>21</maven.compiler.release>", "21")]
    [InlineData("<maven.compiler.target>1.8</maven.compiler.target>", "8")]
    // A property reference cannot be expanded here and must not reach a FROM instruction, so it falls back.
    [InlineData("<maven.compiler.target>${java.version}</maven.compiler.target>", JavaVersionDetector.DefaultJavaVersion)]
    [InlineData("", JavaVersionDetector.DefaultJavaVersion)]
    public void JavaVersionDetector_ReadsTheTargetReleaseFromAPom(string properties, string expected)
    {
        using var appDirectory = new TempJavaAppDirectory();

        File.WriteAllText(Path.Combine(appDirectory.Path, "pom.xml"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <artifactId>demo</artifactId>
              <properties>
                {properties}
              </properties>
            </project>
            """);

        Assert.Equal(expected, JavaVersionDetector.Detect(appDirectory.Path));
    }

    [Theory]
    [InlineData("java { toolchain { languageVersion = JavaLanguageVersion.of(21) } }", "21")]
    [InlineData("java.toolchain.languageVersion.set(JavaLanguageVersion.of(17))", "17")]
    [InlineData("sourceCompatibility = JavaVersion.VERSION_1_8", "8")]
    [InlineData("sourceCompatibility = JavaVersion.VERSION_21", "21")]
    [InlineData("sourceCompatibility = '17'", "17")]
    [InlineData("targetCompatibility = 1.8", "8")]
    [InlineData("", JavaVersionDetector.DefaultJavaVersion)]
    public void JavaVersionDetector_ReadsTheTargetReleaseFromAGradleBuildScript(string script, string expected)
    {
        using var appDirectory = new TempJavaAppDirectory();

        File.WriteAllText(Path.Combine(appDirectory.Path, "build.gradle"), script);

        Assert.Equal(expected, JavaVersionDetector.Detect(appDirectory.Path));
    }

    [Fact]
    public void JavaVersionDetector_PrefersThePomWhenBothBuildFilesArePresent()
    {
        using var appDirectory = new TempJavaAppDirectory();

        File.WriteAllText(Path.Combine(appDirectory.Path, "pom.xml"), """
            <project><properties><java.version>17</java.version></properties></project>
            """);
        File.WriteAllText(Path.Combine(appDirectory.Path, "build.gradle"), "sourceCompatibility = '21'");

        Assert.Equal("17", JavaVersionDetector.Detect(appDirectory.Path));
    }

    [Fact]
    public void JavaVersionDetector_FallsBackWhenThePomCannotBeParsed()
    {
        // A malformed POM is the build tool's problem to report; publishing must not fail before the
        // container build has even started.
        using var appDirectory = new TempJavaAppDirectory();

        File.WriteAllText(Path.Combine(appDirectory.Path, "pom.xml"), "<project>");

        Assert.Equal(JavaVersionDetector.DefaultJavaVersion, JavaVersionDetector.Detect(appDirectory.Path));
    }

    [Fact]
    public void ResolveBuildTool_ThrowsWhenNoBuildToolCanBeFound()
    {
        using var appDirectory = new TempJavaAppDirectory();

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", appDirectory.Path, "target/api.jar");

        var exception = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveBuildTool(app.Resource, appDirectory.Path));

        Assert.Equal(
            $"The Java application 'api' cannot be published because no build tool was found. " +
            $"Add a pom.xml or build.gradle to '{appDirectory.Path}', or call WithMavenBuild or WithGradleBuild " +
            "to state how the deployable JAR is produced.",
            exception.Message);
    }

    [Theory]
    [InlineData("build.gradle")]
    [InlineData("build.gradle.kts")]
    [InlineData("settings.gradle")]
    [InlineData("settings.gradle.kts")]
    public void ResolveBuildTool_DetectsGradleFromAnyOfItsBuildFiles(string fileName)
    {
        using var appDirectory = new TempJavaAppDirectory();
        File.WriteAllText(Path.Combine(appDirectory.Path, fileName), "");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", appDirectory.Path, "build/libs/api.jar");

        var (tool, args) = JavaDockerfileGenerator.ResolveBuildTool(app.Resource, appDirectory.Path);

        Assert.Equal(JavaBuildTool.Gradle, tool);
        Assert.Equal(["--no-daemon", "-x", "test", "build"], args);
    }

    [Fact]
    public void ResolveBuildTool_PrefersTheConfiguredBuildStepOverWhatIsOnDisk()
    {
        // A pom.xml can sit next to a build.gradle in a repository that is mid-migration, so an explicit
        // WithGradleBuild has to win over the file that happens to be checked first.
        using var appDirectory = new TempJavaAppDirectory();
        File.WriteAllText(Path.Combine(appDirectory.Path, "pom.xml"), "<project />");
        File.WriteAllText(Path.Combine(appDirectory.Path, "build.gradle"), "");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", appDirectory.Path).WithGradleTask("bootRun").WithGradleBuild("bootJar");

        var (tool, args) = JavaDockerfileGenerator.ResolveBuildTool(app.Resource, appDirectory.Path);

        Assert.Equal(JavaBuildTool.Gradle, tool);
        Assert.Equal(["bootJar"], args);
    }

    [Fact]
    public async Task ApplicationArgumentsSurvivePublishingWhileLaunchToolArgumentsDoNot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        WritePom(sourceDir.FullName, javaVersion: "21");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddJavaApp("worker", sourceDir.FullName, "target/worker.jar", ["--interval-seconds", "10"])
               .WithMavenBuild();

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // PublishAsDockerFile swaps the executable for a container that shares this annotation collection,
        // so the published resource carries the same name.
        var published = Assert.Single(model.Resources.OfType<ContainerResource>(), r => r.Name == "worker");
        var args = await ArgumentEvaluator.GetArgumentListAsync(published, app.Services);

        // PublishAsDockerFile clears the arguments because they routinely contain host paths, and it does so
        // when AddJavaApp runs. Anything added afterwards — which is every application argument, including
        // the ones passed to the jarPath overload — is appended after that clear and therefore survives,
        // while the launch tool arguments registered before it do not. The image's ENTRYPOINT is the JVM, so these
        // reach main(String[]) exactly as they do when the resource runs on the host.
        Assert.Equal(["--interval-seconds", "10"], args);
    }

    [Fact]
    public void PublishingWithoutAWrapperIsRejected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        File.WriteAllText(Path.Combine(sourceDir.FullName, "pom.xml"), "<project/>");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", sourceDir.FullName, "target/api.jar");

        var ex = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveContainerBuildForTesting(app.Resource, sourceDir.FullName));

        Assert.Contains("there is no mvnw", ex.Message);
        Assert.Contains("mvn -N wrapper:wrapper", ex.Message);
    }

    [Fact]
    public void PublishingWithAWrapperOutsideTheBuildContextIsRejected()    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var siblingDir = workspace.CreateDirectory("sibling");
        WritePom(sourceDir.FullName, javaVersion: "21");
        WriteWrapper(siblingDir.FullName, "mvnw");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", sourceDir.FullName)
                         .WithMavenGoal("spring-boot:run")
                         .WithWrapperPath(Path.Combine("..", "sibling", "mvnw"));

        var ex = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveContainerBuildForTesting(app.Resource, sourceDir.FullName));

        // Only files under the context are uploaded to the daemon, so a wrapper outside it is not in the
        // image and the build would fail partway through with an opaque "not found".
        Assert.Contains("is outside the build context", ex.Message);
    }

    [Theory]
    [InlineData("../outside.jar")]
    [InlineData("./../outside.jar")]
    [InlineData("..\\outside.jar")]
    [InlineData("nested/../../outside.jar")]
    public void PublishingAJarOutsideTheBuildContextIsRejected(string jarPath)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", sourceDir.FullName, jarPath);

        // Normalizing with TrimStart('.', '/') used to eat the traversal itself, turning "../outside.jar"
        // into "outside.jar" so this check never fired and the image silently COPYd the wrong file.
        var ex = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.TryGetPrebuiltJarPath(app.Resource, sourceDir.FullName, out _));

        Assert.Contains("is outside the build context", ex.Message);
    }

    [Theory]
    [InlineData("target/worker.jar", "target/worker.jar")]
    [InlineData("./target/worker.jar", "target/worker.jar")]
    [InlineData("target\\worker.jar", "target/worker.jar")]
    public void PublishingAJarInsideTheBuildContextKeepsItsContextRelativePath(string jarPath, string expected)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", sourceDir.FullName, jarPath);

        // A leading "./" is a normal way to write a context-relative path and must survive the tightened
        // normalization, and Windows separators still have to become POSIX ones for the container.
        Assert.True(JavaDockerfileGenerator.TryGetPrebuiltJarPath(app.Resource, sourceDir.FullName, out var resolved));
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public async Task PublishingAJarPathPrefixedWithDotSlashStillResolvesInsideTheContext()
    {
        var content = await PublishDockerfileAsync(jarPath: "./target/worker.jar");

        // A leading "./" is a normal way to write a context-relative path and must survive normalization.
        Assert.Contains("COPY target/worker.jar /app/app.jar", content);
    }

    [Fact]
    public async Task PublishingWithAWindowsBatchWrapperUsesThePosixSibling()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source =>
            {
                WritePom(source, javaVersion: "21");
                WriteWrapper(source, "mvnw");
                WriteWrapper(source, "mvnw.cmd");
            },
            configureResource: app => app.WithMavenGoal("spring-boot:run").WithWrapperPath("mvnw.cmd"));

        // Selecting the batch wrapper is reasonable on Windows, but the build stage is Linux and cannot
        // execute it. Maven and Gradle ship both scripts, so the POSIX sibling is used for the image.
        Assert.Contains("sh ./mvnw ", content);
        Assert.DoesNotContain("mvnw.cmd", content);
    }

    [Fact]
    public void PublishingWithAWindowsBatchWrapperAndNoPosixSiblingIsRejected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        WritePom(sourceDir.FullName, javaVersion: "21");

        // WritePom ships the POSIX wrapper because publishing normally needs one. Remove it so the batch
        // wrapper really is the only one present.
        File.Delete(Path.Combine(sourceDir.FullName, "mvnw"));
        WriteWrapper(sourceDir.FullName, "mvnw.cmd");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", sourceDir.FullName)
                         .WithMavenGoal("spring-boot:run")
                         .WithWrapperPath("mvnw.cmd");

        var ex = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveContainerBuildForTesting(app.Resource, sourceDir.FullName));

        Assert.Contains("Windows batch script", ex.Message);
    }

    [Theory]
    [InlineData("maven", "mvnw", ".mvn", "maven-wrapper.properties")]
    [InlineData("gradle", "gradlew", "gradle", "gradle-wrapper.properties")]
    public void PublishingRejectsAWrapperWithoutItsPropertiesFile(
        string tool,
        string wrapperName,
        string supportDirectory,
        string propertiesName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        WriteWrapper(sourceDir.FullName, wrapperName);
        Directory.Delete(Path.Combine(sourceDir.FullName, supportDirectory), recursive: true);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", sourceDir.FullName);

        _ = tool is "maven"
            ? app.WithMavenGoal("spring-boot:run")
            : app.WithGradleTask("bootRun");

        var ex = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveContainerBuildForTesting(app.Resource, sourceDir.FullName));

        Assert.Contains($"{supportDirectory}/wrapper/{propertiesName}", ex.Message);
    }

    [Fact]
    public async Task PublishingHonoursAWrapperSelectedWithWithWrapperPath()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source =>
            {
                WritePom(source, javaVersion: "21");
                Directory.CreateDirectory(Path.Combine(source, "scripts"));
                WriteWrapper(Path.Combine(source, "scripts"), "custom-mvnw");
            },
            configureResource: app => app
                .WithMavenGoal("spring-boot:run")
                .WithWrapperPath(Path.Combine("scripts", "custom-mvnw")));

        // Without this the container silently built with a different Maven than the host did.
        Assert.Contains("sh ./scripts/custom-mvnw", content);
    }

    [Fact]
    public async Task VerifyPublish_WithAPrebuiltJarAndNoBuildTool_CopiesTheJarWithoutABuildStage()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source =>
            {
                Directory.CreateDirectory(Path.Combine(source, "target"));
                File.WriteAllText(Path.Combine(source, "target", "worker.jar"), "");
            },
            jarPath: Path.Combine("target", "worker.jar"));

        // A runnable application must stay publishable. Requiring a build tool here made
        // AddJavaApp(name, dir, jarPath) unpublishable even though it runs.
        Assert.DoesNotContain("AS build", content);
        Assert.Contains("COPY target/worker.jar /app/app.jar", content);
        await Verify(content);
    }

    [Fact]
    public async Task PublishingAPrebuiltJarReincludesItAndItsDirectoriesInTheBuildContext()
    {
        var ignore = await PublishBuildContextIgnoreAsync(
            configureSource: source =>
            {
                Directory.CreateDirectory(Path.Combine(source, "target"));
                File.WriteAllText(Path.Combine(source, "target", "worker.jar"), "");
            },
            jarPath: Path.Combine("target", "worker.jar"));

        // "target" is excluded by default because it is routinely hundreds of megabytes, and Docker does
        // not descend into an excluded directory, so re-including only the JAR would never match.
        Assert.NotNull(ignore);
        Assert.Contains("\n!target\n", ignore);
        Assert.Contains("\n!target/worker.jar\n", ignore);
    }

    [Fact]
    public async Task APrebuiltJarAlongsideAPomIsStillBuiltInTheImage()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            jarPath: Path.Combine("target", "api.jar"));

        // A JAR path next to a pom.xml names the artifact the build produces, not one that already exists,
        // so the image has to build it rather than copy a file that is not in the context.
        Assert.Contains("AS build", content);
        Assert.Contains("sh ./mvnw", content);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.ContainerImageBuild)]
    [OuterloopTest("Builds and runs a Docker image to verify the generated Java Dockerfile works")]
    public async Task VerifyPublish_PrebuiltJarImageBuildsAndRunsWithItsArguments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        await File.WriteAllTextAsync(
            Path.Combine(sourceDir.FullName, "App.java"),
            """
            public class App {
                public static void main(String[] args) {
                    System.out.println("runtime ok: " + String.join(" ", args));
                }
            }
            """,
            TestContext.Current.CancellationToken);

        // The JAR is produced inside a JDK container so this test needs Docker and nothing else. A JDK on
        // the agent would work too, but only the runtime image's JDK is guaranteed to emit class files the
        // runtime image can load.
        var buildJarResult = await RunDockerCommandAsync(
            $"run --rm -v {sourceDir.FullName}:/work -w /work docker.io/library/eclipse-temurin:{JavaVersionDetector.DefaultJavaVersion}-jdk " +
            "sh -c \"javac -d classes App.java && mkdir -p target && jar --create --file target/app.jar --main-class App -C classes .\"",
            sourceDir.FullName);
        Assert.True(buildJarResult.ExitCode == 0, $"Building the test JAR failed.\nStdout: {buildJarResult.Stdout}\nStderr: {buildJarResult.Stderr}");

        using (var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest"))
        {
            builder.AddJavaApp("api", sourceDir.FullName, Path.Combine("target", "app.jar"), ["--greeting", "hello"]);
            builder.Build().Run();
        }

        // The image never bakes in the application arguments, exactly like every other container
        // resource: they are part of the deployment spec, so the manifest carries them and the runtime
        // appends them to the entrypoint. Reading them back and passing them to docker run is what makes
        // this test cover the published pair rather than the image alone.
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "aspire-manifest.json"), TestContext.Current.CancellationToken));
        var manifestArgs = manifest.RootElement
            .GetProperty("resources").GetProperty("api").GetProperty("args")
            .EnumerateArray().Select(arg => arg.GetString()!).ToArray();

        Assert.Equal(["--greeting", "hello"], manifestArgs);

        // Copied into the build context under the names docker build expects, so the generated ignore
        // file is exercised too. Its target re-includes are what let the JAR into the image at all.
        File.Copy(
            Path.Combine(outputDir.FullName, "api.Dockerfile"),
            Path.Combine(sourceDir.FullName, "Dockerfile"));
        File.Copy(
            Path.Combine(outputDir.FullName, "api.Dockerfile.dockerignore"),
            Path.Combine(sourceDir.FullName, ".dockerignore"));

        var imageName = $"aspire-java-test-{Guid.NewGuid():N}";

        try
        {
            var buildResult = await RunDockerCommandAsync($"build --network=host -t {imageName} -f Dockerfile .", sourceDir.FullName);
            Assert.True(buildResult.ExitCode == 0, $"Docker build failed with exit code {buildResult.ExitCode}.\nStdout: {buildResult.Stdout}\nStderr: {buildResult.Stderr}");

            // No network, so a passing run cannot depend on anything being downloaded at start up.
            var runResult = await RunDockerCommandAsync($"run --rm --network=none {imageName} {string.Join(' ', manifestArgs)}", sourceDir.FullName);
            Assert.True(runResult.ExitCode == 0, $"Docker run failed with exit code {runResult.ExitCode}.\nStdout: {runResult.Stdout}\nStderr: {runResult.Stderr}");

            // PublishAsDockerFile clears the executable's arguments, so this also pins that the application
            // arguments added after it survive publishing and reach the JAR through the entrypoint.
            Assert.Contains("runtime ok: --greeting hello", runResult.Stdout);
        }
        finally
        {
            await RunDockerCommandAsync($"rmi {imageName}", sourceDir.FullName);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDockerCommandAsync(string arguments, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(process);

        // Both streams are read concurrently so a full pipe buffer cannot deadlock the build output.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private async Task<string> PublishDockerfileAsync(
        Action<string>? configureSource = null,
        string? jarPath = null,
        Func<IResourceBuilder<JavaAppResource>, IResourceBuilder<JavaAppResource>>? configureResource = null)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        configureSource?.Invoke(sourceDir.FullName);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");

        var app = jarPath is null
            ? builder.AddJavaApp("api", sourceDir.FullName)
            : builder.AddJavaApp("api", sourceDir.FullName, jarPath);

        configureResource?.Invoke(app);

        builder.Build().Run();

        return await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"), TestContext.Current.CancellationToken);
    }

    private async Task<string?> PublishBuildContextIgnoreAsync(
        Action<string>? configureSource = null,
        string? jarPath = null,
        Func<IResourceBuilder<JavaAppResource>, IResourceBuilder<JavaAppResource>>? configureResource = null)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        configureSource?.Invoke(sourceDir.FullName);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");

        var app = jarPath is null
            ? builder.AddJavaApp("api", sourceDir.FullName)
            : builder.AddJavaApp("api", sourceDir.FullName, jarPath);

        configureResource?.Invoke(app);

        builder.Build().Run();

        var ignorePath = Path.Combine(outputDir.FullName, "api.Dockerfile.dockerignore");

        return File.Exists(ignorePath)
            ? await File.ReadAllTextAsync(ignorePath, TestContext.Current.CancellationToken)
            : null;
    }

    private static void WritePom(string sourceDirectory, string javaVersion)
    {
        // Publishing requires a wrapper, so every project that publishes has to ship one.
        WriteWrapper(sourceDirectory, "mvnw");
        File.WriteAllText(Path.Combine(sourceDirectory, "pom.xml"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <groupId>com.example</groupId>
              <artifactId>api</artifactId>
              <version>0.0.1-SNAPSHOT</version>
              <properties>
                <java.version>{javaVersion}</java.version>
              </properties>
            </project>
            """);
    }

    private static void WriteGradleBuild(string sourceDirectory, string contents)
    {
        WriteWrapper(sourceDirectory, "gradlew");
        File.WriteAllText(Path.Combine(sourceDirectory, "build.gradle"), contents);
    }

    private static void WriteWrapper(string sourceDirectory, string wrapperName)
    {
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, wrapperName), "#!/bin/sh\nexit 0\n");

        // A real wrapper always ships the properties file that pins the tool version, and publishing
        // requires it so the distribution can be unpacked in its own image layer.
        var gradle = wrapperName.Contains("gradle", StringComparison.OrdinalIgnoreCase);
        var supportDirectory = Path.Combine(sourceDirectory, gradle ? "gradle" : ".mvn", "wrapper");

        Directory.CreateDirectory(supportDirectory);
        File.WriteAllText(
            Path.Combine(supportDirectory, gradle ? "gradle-wrapper.properties" : "maven-wrapper.properties"),
            "distributionUrl=https\\://example.invalid/tool-bin.zip\n");
    }
}
