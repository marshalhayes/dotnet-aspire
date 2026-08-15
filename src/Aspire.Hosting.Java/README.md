# Java hosting integration

Use this integration to model, configure, and orchestrate a Java application resource in an Aspire solution.

## Getting started

### Prerequisites

A **JDK** must be available on the PATH of the machine running the AppHost. Aspire does not install one.

Applications are launched through the **Maven or Gradle wrapper** (`mvnw`/`gradlew`) checked into the
project, which needs nothing else installed. A wrapper is required: Aspire deliberately does not fall back
to a globally installed `mvn` or `gradle`, because the wrapper pins the tool version in the repository so
the AppHost, CI, and the published container image all build with the same one. Add a wrapper with
`mvn -N wrapper:wrapper` or `gradle wrapper`, or select one elsewhere with `WithWrapperPath(...)`.

For VS Code debugging, install
[Language Support for Java](https://marketplace.visualstudio.com/items?itemName=redhat.java) and
[Debugger for Java](https://marketplace.visualstudio.com/items?itemName=vscjava.vscode-java-debug).

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Java` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Java
```

## Usage example

Then, in the AppHost, add a Java application resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Detects Maven or Gradle from the build file, builds the app, launches it through the Spring Boot
// plugin, and declares an HTTP endpoint through SERVER_PORT, which is the port Spring Boot listens on.
var catalog = builder.AddSpringBootApp("catalog", "../catalog")
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.Frontend>("frontend")
    .WithReference(catalog)
    .WaitFor(catalog);

builder.Build().Run();
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

const catalog = await builder.addSpringBootApp("catalog", "../catalog");
await catalog.withExternalHttpEndpoints();

const orders = await builder.addSpringBootApp("orders", "../orders");
await orders.withReference(catalog);
await orders.waitFor(catalog);

await builder.build().run();
```

`appDirectory` is the process working directory and the publish build context, so everything the build
needs must live inside it.

### Spring Boot

`AddSpringBootApp` is `AddJavaApp` with the four calls every Spring Boot service repeats already made.
It reads the build file in the directory to decide between Maven and Gradle, so the AppHost never
restates something the project already declares:

```csharp
builder.AddSpringBootApp("catalog", "../catalog");
```

is the same as:

```csharp
builder.AddJavaApp("catalog", "../catalog")
    .WithMavenBuild("-B", "-ntp", "-DskipTests", "package")
    .WithMavenGoal("spring-boot:run")
    .WithHttpEndpoint(env: "SERVER_PORT");
```

The build skips tests, because it runs every time the AppHost starts and a full suite in front of every
debug session gets old quickly. Call `WithMavenBuild`/`WithGradleBuild` afterwards to choose your own
arguments.

No health check is added. `/actuator/health` only responds when the application depends on
`spring-boot-starter-actuator`, and adding it unconditionally would leave applications without that
dependency permanently unhealthy — which silently stalls every `WaitFor` on them. Add it yourself when
the actuator is present:

```csharp
builder.AddSpringBootApp("catalog", "../catalog")
    .WithHttpHealthCheck("/actuator/health");
```

Use `AddJavaApp` directly for anything else: a different Spring Boot plugin goal, a project laid out so
the build file is not in the app directory, or a framework that is not Spring Boot.

### Quarkus

`AddQuarkusApp` is the Quarkus equivalent, and detects the build tool the same way:

```csharp
builder.AddQuarkusApp("inventory", "../inventory");
```

is the same as:

```csharp
builder.AddJavaApp("inventory", "../inventory")
    .WithMavenBuild("-B", "-ntp", "-DskipTests", "package")
    .WithMavenGoal("quarkus:dev")
    .WithEnvironment("QUARKUS_PROFILE", "dev")
    .WithEnvironment("QUARKUS_OBSERVABILITY_ENABLED", "false")
    .WithHttpEndpoint(env: "QUARKUS_HTTP_PORT");
```

The application runs in Quarkus dev mode, so live coding works while the AppHost is running. Quarkus Dev
Services stay enabled but do not activate for anything Aspire supplies, because a Dev Service only starts
when the configuration it would provide is absent — and `WithReference` supplies it.

The observability Dev Service is the exception, and is turned off. Left on, an application that depends on
`quarkus-opentelemetry` pulls `grafana/otel-lgtm` (roughly 600 MB), starts it through Testcontainers, and
then repoints the exporter at that container — so telemetry never reaches the Aspire dashboard and a
container is left behind. Aspire is already the observability stack, so the Dev Service has nothing to add.

`AddQuarkusApp` also mirrors the OTLP endpoint, protocol, headers and service name onto the
`QUARKUS_OTEL_*` environment variables. `quarkus-opentelemetry` reads its own `quarkus.otel.*`
configuration and ignores the standard `OTEL_*` names, so without this it keeps exporting to its
`localhost:4317` default and every export fails. No Java agent is needed for a Quarkus application, and
`WithOtelAgent` should not be combined with the extension.

`QUARKUS_PROFILE=dev` is set as an environment variable rather than left to the goal, because the VS Code
debugger launches the packaged application directly rather than through `quarkus:dev`; setting it here
means both resolve the same `%dev.` configuration. It is not set when publishing, where the image must
resolve production configuration.

No health check is added, for the same reason as Spring Boot: `/q/health` only responds when the
application depends on `quarkus-smallrye-health`. Add it yourself when that extension is present:

```csharp
builder.AddQuarkusApp("inventory", "../inventory")
    .WithHttpHealthCheck("/q/health");
```

`WithOtelAgent` is usually unnecessary for Quarkus. The `quarkus-opentelemetry` extension is compiled
into the application and reads the same `OTEL_*` environment variables Aspire already supplies, so
telemetry works with no AppHost configuration at all.

### Launch modes

A resource runs in exactly one of three ways, and configuring a second one throws:

| Mode | How to select it | What runs |
| --- | --- | --- |
| Prebuilt JAR | `AddJavaApp(name, appDirectory, jarPath)` | `java -jar <jarPath>` |
| Maven goal | `WithMavenGoal("spring-boot:run")` | `mvnw spring-boot:run` |
| Gradle task | `WithGradleTask("bootRun")` | `gradlew bootRun` |

Arguments passed to `AddJavaApp` or `WithArgs(...)` belong to the application. Arguments for the build
tool are passed to `WithMavenGoal`/`WithGradleTask`, which keeps the two sets separable — the IDE needs
to drop the wrapper's arguments when it launches the JVM directly to debug it.

### Running an image someone else built

When the application ships as a container image — built by a separate pipeline, or by a team that hands
you an image rather than source — use `AddJavaContainerApp` instead. Aspire runs the image as-is and
never rebuilds it, so the JAR, the JDK, and any OpenTelemetry agent all come from the image:

```csharp
builder.AddJavaContainerApp("catalog", "mycompany/catalog", "1.4.0")
    .WithHttpEndpoint(targetPort: 8080)
    .WithReference(db)
    .WithJvmArgs("-Xmx512m");
```

No endpoint is declared for you, because the port is a property of the image; 8080 is the default for
Spring Boot and Quarkus. `WithOtelAgent` does not apply here — it copies an agent out of the build
context, and there is no build. If the image already carries the agent, turn it on with
`WithJvmArgs("-javaagent:/app/opentelemetry-javaagent.jar")`.

### Building before running

```csharp
builder.AddJavaApp("worker", "../worker", "target/worker.jar")
    .WithMavenBuild("-B", "-ntp", "-DskipTests", "package");
```

`WithMavenBuild`/`WithGradleBuild` add a child resource that runs before the application starts, and the
application waits for it to succeed. The same arguments also drive the container build during
`aspire publish`, so they should produce the deployable artifact — `build -x test` rather than `classes`
for Gradle, for example.

### Options

| Method | Effect |
| --- | --- |
| `WithMavenGoal(string goal, params string[] args)` | Launches through `mvnw` with the given goal |
| `WithGradleTask(string task, params string[] args)` | Launches through `gradlew` with the given task |
| `WithMavenBuild(params string[] args)` | Builds with Maven before the app runs, and in the published container |
| `WithGradleBuild(params string[] args)` | Builds with Gradle before the app runs, and in the published container |
| `WithWrapperPath(string wrapperScript)` | Selects a custom wrapper path. May be called before or after the build tool is configured. Must stay inside the app directory for an app that will be published, because that directory is the container build context |
| `WithMainClass(string mainClass)` | The fully qualified class the IDE launches when debugging |
| `WithJarArtifact(string jarPath)` | Names the JAR the container build should deploy, when the build produces more than one |
| `WithJvmArgs(params string[] args)` | Appends JVM arguments through `JAVA_TOOL_OPTIONS`. Also available on `AddJavaContainerApp` |
| `WithOtelAgent(string agentPath)` | Runs the app under the OpenTelemetry Java agent |
| `WithOtelAgent()` | Same, with the agent at `target/agent/` (Maven) or `build/agent/` (Gradle) |

### Telemetry

`AddJavaApp` configures the OTLP exporter environment on its own, which is all a manually instrumented
application needs. `WithOtelAgent(...)` additionally runs the
[OpenTelemetry Java agent](https://github.com/open-telemetry/opentelemetry-java-instrumentation), which
instruments common frameworks with no code change:

```csharp
builder.AddSpringBootApp("catalog", "../catalog")
    .WithOtelAgent("target/agent/opentelemetry-javaagent.jar");
```

The no-argument `WithOtelAgent()` uses the conventional location for the build tool in use —
`target/agent/opentelemetry-javaagent.jar` for Maven, `build/agent/opentelemetry-javaagent.jar` for
Gradle — so a build that copies the agent there needs no path:

```csharp
builder.AddSpringBootApp("catalog", "../catalog")
    .WithOtelAgent();
```

The agent is not downloaded for you. Fetch it as a build dependency so it exists before the application
starts — a relative path is resolved against the app directory when running locally, and the published
container copies the build's agent into the image. Because the agent is applied through
`JAVA_TOOL_OPTIONS`, which every JVM beneath the resource inherits, the build tool's own JVM is
instrumented as well.

### Debugging

Debugging is enabled automatically by `AddJavaApp` — use the normal Aspire "Start Debugging" flow in
VS Code. The IDE launches the JVM directly rather than through the wrapper, because `spring-boot:run`
and `bootRun` fork a second JVM that a debugger attached to the wrapper would never see. Set
`WithMainClass(...)` to say which class to launch; for a prebuilt JAR the archive is put on the
debugger's classpath and its manifest's `Main-Class` is launched, which is what `java -jar` does.

### Publishing

`aspire publish` and `aspire deploy` build the app into a container. An app that runs should publish with
no extra configuration: if the app directory contains a `Dockerfile` it is used as-is, otherwise one is
generated that builds the project inside the container. The container runs as a non-root `app` user, and
the JVM is PID 1 so it receives `SIGTERM` directly and shutdown hooks run.

The generated build stage reuses the wrapper and the arguments from `WithMavenBuild`/`WithGradleBuild`,
falling back to `package` or `build` when neither is configured. Dependency caches are kept in a BuildKit
cache mount rather than baked into a layer.

The deployed JAR is whichever one the build produces, ignoring `-plain`, `-sources`, and `-javadoc`
artifacts. When that is still ambiguous the build fails and names the candidates; select one with
`WithJarArtifact(...)`.

#### Base images

The Java release is read from `pom.xml` or the Gradle build file, and defaults to 21.

| Stage | Default |
| --- | --- |
| Build | `docker.io/library/eclipse-temurin:{version}-jdk` |
| Runtime | `docker.io/library/eclipse-temurin:{version}-jre` |

A plain JDK image is enough for the build stage because the build always runs through the project's own
wrapper, and the wrapper downloads the Maven or Gradle version the repository pins.

Override both stages in a single call:

```csharp
builder.AddJavaApp("catalog", "../catalog")
    .WithDockerfileBaseImage(
        buildImage: "example/java-build:latest",
        runtimeImage: "example/java-runtime:latest");
```

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/frameworks/java/
* [Aspire documentation](https://aspire.dev/)
* [Maven Wrapper](https://maven.apache.org/wrapper/)
* [Gradle Wrapper](https://docs.gradle.org/current/userguide/gradle_wrapper.html)

## Feedback & contributing

https://github.com/microsoft/aspire
