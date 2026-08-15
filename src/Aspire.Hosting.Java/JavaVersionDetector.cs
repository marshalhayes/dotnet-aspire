// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Aspire.Hosting.Java;

/// <summary>
/// Determines which Java release a project targets, so the generated container image builds and runs it
/// on a matching JDK and JRE.
/// </summary>
/// <remarks>
/// Detection is best effort and never fails: an unreadable or unrecognised build file falls back to
/// <see cref="DefaultJavaVersion"/>. Callers can always override the images with
/// <c>WithDockerfileBaseImages</c>.
/// </remarks>
internal static partial class JavaVersionDetector
{
    /// <summary>
    /// The Java release used when a project's build files declare none. Chosen as a broadly supported
    /// long-term support release, which is what current Spring Boot and Quarkus releases target by
    /// default, rather than the newest one.
    /// </summary>
    internal const string DefaultJavaVersion = "21";

    public static string Detect(string appDirectory)
    {
        return DetectFromPom(Path.Combine(appDirectory, "pom.xml"))
            ?? DetectFromGradle(Path.Combine(appDirectory, "build.gradle"))
            ?? DetectFromGradle(Path.Combine(appDirectory, "build.gradle.kts"))
            ?? DefaultJavaVersion;
    }

    /// <summary>
    /// Reads the target release from a Maven POM.
    /// </summary>
    /// <remarks>
    /// Three spellings are common and all appear in Spring Initializr output:
    /// <code language="xml">
    /// &lt;properties&gt;
    ///   &lt;java.version&gt;21&lt;/java.version&gt;
    ///   &lt;maven.compiler.release&gt;21&lt;/maven.compiler.release&gt;
    /// &lt;/properties&gt;
    /// &lt;!-- or, on the compiler plugin itself --&gt;
    /// &lt;configuration&gt;&lt;release&gt;21&lt;/release&gt;&lt;/configuration&gt;
    /// </code>
    /// Legacy POMs write <c>1.8</c> rather than <c>8</c>; both map to the <c>8</c> image tag.
    /// Property references such as <c>&lt;release&gt;${java.version}&lt;/release&gt;</c> are not expanded —
    /// the literal properties are checked first, so the reference is only reached when nothing else matched.
    /// </remarks>
    private static string? DetectFromPom(string pomPath)
    {
        if (!File.Exists(pomPath))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(pomPath);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            // A malformed or unreadable POM is the build tool's problem to report, not a reason to fail
            // publishing before the container build has even started.
            return null;
        }

        // Element names are matched without their namespace so both the Maven 4 POM namespace and the
        // long-standing http://maven.apache.org/POM/4.0.0 namespace are handled.
        //
        // Every element with a matching name is considered, not just the first: a POM often declares
        // <release>${java.version}</release> on the compiler plugin and a literal elsewhere, and stopping
        // at the unresolvable property reference would fall back to the default version instead.
        // Ordered by how directly each one decides the bytecode version, most direct first, because the
        // runtime image has to be at least what the compiler actually emitted.
        //
        // java.version is last despite being the most recognisable. It is not a Maven property at all:
        // it works only because spring-boot-starter-parent maps it onto the real one with
        // <maven.compiler.release>${java.version}</maven.compiler.release>. A POM that sets both
        // java.version and maven.compiler.release overrides that mapping, so Maven compiles to the
        // latter and reading java.version would pick a runtime too old to load the classes.
        // https://docs.spring.io/spring-boot/maven-plugin/using.html
        foreach (var (name, mustBePluginConfiguration) in ((string, bool)[])
        [
            // Explicit plugin configuration beats the property that merely supplies the parameter's
            // default, and release beats target within the plugin.
            // https://maven.apache.org/plugins/maven-compiler-plugin/compile-mojo.html
            //
            // <release> and <target> are only meaningful inside the compiler plugin's <configuration>.
            // Matched merely by having a <configuration> parent they would also pick up unrelated
            // plugins: maven-antrun-plugin's canonical configuration is literally
            // <configuration><target>...</target></configuration>, holding Ant XML rather than a Java
            // release, and any plugin is free to name a <release> of its own.
            ("release", true),
            ("maven.compiler.release", false),
            ("target", true),
            ("maven.compiler.target", false),
            ("java.version", false),
        ])
        {
            foreach (var element in document.Descendants().Where(e => string.Equals(e.Name.LocalName, name, StringComparison.Ordinal)))
            {
                if (mustBePluginConfiguration && !IsCompilerPluginConfiguration(element.Parent))
                {
                    continue;
                }

                if (Normalize(element.Value) is { } version)
                {
                    return version;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Determines whether an element is a <c>&lt;configuration&gt;</c> belonging to
    /// <c>maven-compiler-plugin</c>.
    /// </summary>
    /// <remarks>
    /// Both places a compiler configuration can appear are accepted, because both really set the release:
    /// <code>
    /// &lt;plugin&gt;
    ///   &lt;artifactId&gt;maven-compiler-plugin&lt;/artifactId&gt;
    ///   &lt;configuration&gt;&lt;release&gt;21&lt;/release&gt;&lt;/configuration&gt;      &lt;!-- plugin level --&gt;
    ///   &lt;executions&gt;&lt;execution&gt;
    ///     &lt;configuration&gt;&lt;release&gt;21&lt;/release&gt;&lt;/configuration&gt;    &lt;!-- execution level --&gt;
    ///   &lt;/execution&gt;&lt;/executions&gt;
    /// &lt;/plugin&gt;
    /// </code>
    /// The plugin is identified by <c>artifactId</c> alone: <c>groupId</c> defaults to
    /// <c>org.apache.maven.plugins</c> and is routinely omitted for the core plugins.
    /// See https://maven.apache.org/plugins/maven-compiler-plugin/compile-mojo.html.
    /// </remarks>
    private static bool IsCompilerPluginConfiguration(XElement? configuration)
    {
        if (!string.Equals(configuration?.Name.LocalName, "configuration", StringComparison.Ordinal))
        {
            return false;
        }

        for (var ancestor = configuration!.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (!string.Equals(ancestor.Name.LocalName, "plugin", StringComparison.Ordinal))
            {
                continue;
            }

            return ancestor.Elements().Any(e =>
                string.Equals(e.Name.LocalName, "artifactId", StringComparison.Ordinal)
                && string.Equals(e.Value.Trim(), "maven-compiler-plugin", StringComparison.Ordinal));
        }

        return false;
    }

    /// <summary>
    /// Reads the target release from a Gradle build script.
    /// </summary>
    /// <remarks>
    /// Groovy and Kotlin DSL spellings that appear in Spring Initializr and Gradle's own documentation:
    /// <code>
    /// java { toolchain { languageVersion = JavaLanguageVersion.of(21) } }
    /// java.sourceCompatibility = JavaVersion.VERSION_21
    /// sourceCompatibility = '17'
    /// targetCompatibility = 1.8
    /// </code>
    /// The toolchain is checked first because it pins the JDK Gradle actually compiles with, whereas
    /// source/target compatibility only constrain the bytecode level.
    /// </remarks>
    private static string? DetectFromGradle(string buildScriptPath)
    {
        if (!File.Exists(buildScriptPath))
        {
            return null;
        }

        string contents;
        try
        {
            contents = File.ReadAllText(buildScriptPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        contents = StripComments(contents);

        if (ToolchainRegex().Match(contents) is { Success: true } toolchain)
        {
            return Normalize(toolchain.Groups[1].Value);
        }

        if (JavaVersionEnumRegex().Match(contents) is { Success: true } enumMatch)
        {
            return Normalize(enumMatch.Groups[1].Value.Replace('_', '.'));
        }

        if (CompatibilityRegex().Match(contents) is { Success: true } compatibility)
        {
            return Normalize(compatibility.Groups[1].Value);
        }

        return null;
    }

    /// <summary>
    /// Blanks out line and block comments so a commented-out setting cannot be mistaken for an active one.
    /// </summary>
    /// <remarks>
    /// The version patterns are applied to the raw script rather than a parsed model, so without this a
    /// leftover line wins over the setting that is actually in effect:
    /// <code>
    /// java {
    ///     toolchain {
    ///         // languageVersion = JavaLanguageVersion.of(17)
    ///         languageVersion = JavaLanguageVersion.of(21)
    ///     }
    /// }
    /// </code>
    /// String literals are tracked so that the <c>//</c> inside a repository URL, and any <c>/*</c> inside
    /// a string, are not treated as comment starts — the latter would otherwise swallow the rest of the
    /// file. Single, double, and triple-quoted forms are recognized, covering both the Groovy and the
    /// Kotlin DSL. Groovy's slashy strings (<c>/pattern/</c>) are not recognized; they do not appear in the
    /// toolchain or compatibility declarations this reads.
    /// </remarks>
    private static string StripComments(string contents)
    {
        var builder = new StringBuilder(contents.Length);
        var index = 0;

        while (index < contents.Length)
        {
            var current = contents[index];

            if (current is '/' && index + 1 < contents.Length)
            {
                if (contents[index + 1] is '/')
                {
                    while (index < contents.Length && contents[index] is not ('\n' or '\r'))
                    {
                        index++;
                    }

                    continue;
                }

                if (contents[index + 1] is '*')
                {
                    var end = contents.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    index = end < 0 ? contents.Length : end + 2;
                    // A newline stands in for the comment so that the text on either side of a block
                    // comment cannot be joined into a single line and match as one declaration.
                    builder.Append('\n');
                    continue;
                }
            }

            if (current is '"' or '\'')
            {
                var delimiter = new string(current, contents.AsSpan(index).StartsWith(new string(current, 3), StringComparison.Ordinal) ? 3 : 1);
                builder.Append(delimiter);
                index += delimiter.Length;

                // The literal's contents are copied through unchanged - only comments are removed - so a
                // version written as a quoted string, such as sourceCompatibility = '17', still matches.
                while (index < contents.Length)
                {
                    if (contents[index] is '\\' && index + 1 < contents.Length)
                    {
                        builder.Append(contents, index, 2);
                        index += 2;
                        continue;
                    }

                    if (contents.AsSpan(index).StartsWith(delimiter, StringComparison.Ordinal))
                    {
                        builder.Append(delimiter);
                        index += delimiter.Length;
                        break;
                    }

                    builder.Append(contents[index]);
                    index++;
                }

                continue;
            }

            builder.Append(current);
            index++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Maps a declared release to the numeric form used in container image tags.
    /// </summary>
    /// <remarks>
    /// Java 8 and earlier are written <c>1.8</c> in build files but tagged <c>8</c> in images
    /// (<c>eclipse-temurin:8-jre</c>). Anything that is not a plain release number is rejected so a
    /// property reference such as <c>${java.version}</c> cannot end up inside a <c>FROM</c> instruction.
    /// </remarks>
    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();

        if (value.StartsWith("1.", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value.Length > 0 && value.All(char.IsAsciiDigit) ? value : null;
    }

    // Matches: languageVersion = JavaLanguageVersion.of(21)  and  languageVersion.set(JavaLanguageVersion.of(21))
    [GeneratedRegex(@"JavaLanguageVersion\.of\(\s*(\d+)\s*\)")]
    private static partial Regex ToolchainRegex();

    // Matches: sourceCompatibility = JavaVersion.VERSION_21  and  VERSION_1_8
    [GeneratedRegex(@"JavaVersion\.VERSION_(\d+(?:_\d+)?)")]
    private static partial Regex JavaVersionEnumRegex();

    // Matches: sourceCompatibility = '17'   targetCompatibility = 17   sourceCompatibility = "1.8"
    [GeneratedRegex(@"(?:source|target)Compatibility\s*(?:=|\.set\()\s*['""]?(\d+(?:\.\d+)?)['""]?")]
    private static partial Regex CompatibilityRegex();
}
