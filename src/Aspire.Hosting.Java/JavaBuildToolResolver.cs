// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Java;

/// <summary>
/// Resolves Java build tools from project files.
/// </summary>
internal static class JavaBuildToolResolver
{
    private static readonly string[] s_gradleBuildFileNames =
    [
        "build.gradle",
        "build.gradle.kts",
        "settings.gradle",
        "settings.gradle.kts"
    ];

    /// <summary>
    /// Returns the build tool declared by files in <paramref name="appDirectory"/>, or
    /// <see langword="null"/> when none is declared.
    /// </summary>
    internal static JavaBuildTool? Detect(string appDirectory, string resourceName)
    {
        var hasMaven = File.Exists(Path.Combine(appDirectory, "pom.xml"));
        var hasGradle = s_gradleBuildFileNames.Any(fileName => File.Exists(Path.Combine(appDirectory, fileName)));

        // Ambiguous projects are rejected rather than guessed. Maven-first detection made publish produce
        // a different artifact than run mode for the same directory, while an explicit build or launch API
        // records the author's choice for both paths.
        if (hasMaven && hasGradle)
        {
            throw new InvalidOperationException(
                $"Directory '{appDirectory}' contains both Maven and Gradle build files, so the build tool for resource '{resourceName}' is ambiguous. " +
                "Use AddJavaApp and call WithMavenBuild, WithGradleBuild, WithMavenGoal, or WithGradleTask to choose one explicitly.");
        }

        return (hasMaven, hasGradle) switch
        {
            (true, false) => JavaBuildTool.Maven,
            (false, true) => JavaBuildTool.Gradle,
            _ => null
        };
    }
}
