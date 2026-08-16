// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Java.Tests;

public class JavaBuildToolResolverTests
{
    [Theory]
    [InlineData(nameof(JavaBuildTool.Maven), false, "mvnw")]
    [InlineData(nameof(JavaBuildTool.Maven), true, "mvnw.cmd")]
    [InlineData(nameof(JavaBuildTool.Gradle), false, "gradlew")]
    [InlineData(nameof(JavaBuildTool.Gradle), true, "gradlew.bat")]
    public void ResolveWrapperPath_UsesTheRequestedPlatformsDefault(
        string toolName,
        bool isWindows,
        string expectedWrapperName)
    {
        using var tempDir = new TempJavaAppDirectory(withWrappers: false);
        var resource = new JavaAppResource("api", tempDir.Path);
        var tool = Enum.Parse<JavaBuildTool>(toolName);

        var wrapperPath = JavaBuildToolResolver.ResolveWrapperPath(resource, tool, isWindows);

        Assert.Equal(Path.GetFullPath(Path.Combine(tempDir.Path, expectedWrapperName)), wrapperPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveWrapperPath_UsesWithWrapperPathOnEveryPlatform(bool isWindows)
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory(withWrappers: false);
        var app = builder.AddJavaApp("api", tempDir.Path).WithWrapperPath("tools/custom-wrapper");

        var wrapperPath = JavaBuildToolResolver.ResolveWrapperPath(app.Resource, JavaBuildTool.Maven, isWindows);

        Assert.Equal(Path.GetFullPath(Path.Combine(tempDir.Path, "tools", "custom-wrapper")), wrapperPath);
    }
}
