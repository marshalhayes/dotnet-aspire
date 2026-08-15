// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Java;

/// <summary>
/// Provides language support for Java AppHosts.
/// Implements scaffolding, detection, and runtime configuration.
/// </summary>
internal sealed class JavaLanguageSupport : ILanguageSupport
{
    /// <summary>
    /// The language/runtime identifier for Java.
    /// </summary>
    private const string LanguageId = "java";

    /// <summary>
    /// The code generation target language. This maps to the ICodeGenerator.Language property.
    /// </summary>
    private const string CodeGenTarget = "Java";

    private const string LanguageDisplayName = "Java";
    private static readonly string[] s_detectionPatterns = ["AppHost.java"];

    /// <inheritdoc />
    public string Language => LanguageId;

    /// <inheritdoc />
    public Dictionary<string, string> Scaffold(ScaffoldRequest request)
    {
        var files = new Dictionary<string, string>();

        files[".gitignore"] = """
            .java-build/
            .aspire/
            """;

        files["AppHost.java"] = """
            // Aspire Java AppHost
            // For more information, see: https://aspire.dev
            
            import aspire.*;

            void main(String[] args) throws Exception {
                var builder = DistributedApplication.CreateBuilder(args);

                // Add your resources here, for example:
                // var redis = builder.addRedis("cache");
                // var postgres = builder.addPostgres("db");

                builder.build().run();
            }
            """;

        // Create apphost.run.json with random ports
        var random = request.PortSeed.HasValue
            ? new Random(request.PortSeed.Value)
            : Random.Shared;

        var httpsPort = random.Next(10000, 65000);
        var httpPort = random.Next(10000, 65000);
        var otlpPort = random.Next(10000, 65000);
        var resourceServicePort = random.Next(10000, 65000);

        files["apphost.run.json"] = $$"""
            {
              "profiles": {
                "https": {
                  "applicationUrl": "https://localhost:{{httpsPort}};http://localhost:{{httpPort}}",
                  "environmentVariables": {
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:{{otlpPort}}",
                    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:{{resourceServicePort}}"
                  }
                }
              }
            }
            """;

        return files;
    }

    /// <inheritdoc />
    public DetectionResult Detect(string directoryPath)
    {
        var appHostPath = Path.Combine(directoryPath, "AppHost.java");
        if (!File.Exists(appHostPath))
        {
            return DetectionResult.NotFound;
        }

        return DetectionResult.Found(LanguageId, "AppHost.java");
    }

    /// <summary>
    /// Directory that the generated SDK sources and the AppHost are compiled into.
    /// </summary>
    private const string BuildOutputDirectory = ".java-build";

    /// <summary>
    /// Compiler options used to build the AppHost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scaffolded AppHost is a compact source file with an instance <c>main</c> method, which
    /// requires Java 25. That feature was previewed in Java 21 through 24 (JEP 445, 463, 477, and
    /// 495) and finalized in Java 25 by <see href="https://openjdk.org/jeps/512">JEP 512</see>, so
    /// <c>--enable-preview</c> is deliberately absent: passing it here compiles no preview feature
    /// and only risks stamping the class files with the preview minor version (65535), which binds
    /// them to one exact JDK release and forces the flag at run time too.
    /// </para>
    /// <para>
    /// <c>--release</c> is used rather than <c>--source</c> because only <c>--release</c> also
    /// constrains the visible API surface. With <c>--source</c> alone a newer JDK still compiles
    /// against its own class library, so an AppHost can bind to APIs that do not exist in Java 25
    /// and then fail at run time on a conforming Java 25 runtime.
    /// </para>
    /// </remarks>
    private const string JavacOptions = "--release 25";

    /// <inheritdoc />
    public RuntimeSpec GetRuntimeSpec()
    {
        return new RuntimeSpec
        {
            Language = LanguageId,
            DisplayName = LanguageDisplayName,
            CodeGenLanguage = CodeGenTarget,
            DetectionPatterns = s_detectionPatterns,
            // No separate install step - compilation happens in Execute
            InstallDependencies = null,
            Execute = new CommandSpec
            {
                // Use a shell to compile and run in sequence
                // On Windows, use cmd /c; on Unix, use sh -c
                Command = OperatingSystem.IsWindows() ? "cmd" : "sh",
                Args = OperatingSystem.IsWindows()
                    ? ["/c", $"if not exist {BuildOutputDirectory} mkdir {BuildOutputDirectory} && javac {JavacOptions} -d {BuildOutputDirectory} @.aspire\\modules\\sources.txt AppHost.java && java -cp {BuildOutputDirectory} AppHost {{args}}"]
                    : ["-c", $"mkdir -p {BuildOutputDirectory} && javac {JavacOptions} -d {BuildOutputDirectory} @.aspire/modules/sources.txt AppHost.java && java -cp {BuildOutputDirectory} AppHost {{args}}"]
            }
        };
    }
}
