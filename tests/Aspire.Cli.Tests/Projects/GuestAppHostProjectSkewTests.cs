// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.Projects;

public class GuestAppHostProjectSkewTests
{
    [Theory]
    [InlineData("13.1.0", "13.1.0", false)]
    [InlineData("13.1.0-preview.1.26218.1", "13.1.0-preview.1.26218.1", false)]
    // Build metadata (everything after '+') is SemVer-spec ignored for precedence.
    [InlineData("13.1.0-preview.1.26218.1+abc", "13.1.0-preview.1.26218.1+def", false)]
    // Issue #16709 reproduction: same M.M.P prerelease tag with different daily build numbers
    // is detected as skew (this was the exact failure case).
    [InlineData("13.1.0-preview.1.26218.1", "13.1.0-preview.1.26227.1", true)]
    // Release vs prerelease of the same M.M.P is skew.
    [InlineData("13.1.0", "13.1.0-preview.1", true)]
    [InlineData("13.1.0-preview.1", "13.1.0", true)]
    [InlineData("13.1.0", "13.2.0", true)]
    [InlineData("13.1.0", "14.0.0", true)]
    [InlineData("13.1.0", "13.1.1", true)]
    public void IsKnownIncompatibleSkew_DetectsMajorMinorPatchAndPrereleaseChanges(string cli, string sdk, bool expected)
    {
        var result = GuestAppHostProject.IsKnownIncompatibleSkew(cli, sdk);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsKnownIncompatibleSkew_FallsBackToStringCompareForUnparseable()
    {
        Assert.True(GuestAppHostProject.IsKnownIncompatibleSkew("not-a-version", "also-not-a-version-but-different"));
        Assert.False(GuestAppHostProject.IsKnownIncompatibleSkew("identical", "identical"));
    }

    [Theory]
    [InlineData("13.1.0+build.5", "13.1.0")]
    [InlineData("13.1.0-preview.1+sha.abc123", "13.1.0-preview.1")]
    [InlineData("13.1.0", "13.1.0")]
    public void NormalizeVersion_StripsBuildSuffix(string input, string expected)
    {
        Assert.Equal(expected, GuestAppHostProject.NormalizeVersion(input));
    }

    [Theory]
    [InlineData("Could not load type 'Aspire.TypeSystem.CommandUpToDateCheck' from assembly 'Aspire.TypeSystem, Version=13.4.5.0'.")]
    [InlineData("Method not found: 'Void Aspire.Hosting.Something.set_Value(System.String)'.")]
    public void TryDescribeAppHostServerAssemblySkew_ExplainsMissingAspireMembers(string message)
    {
        var hint = GuestAppHostProject.TryDescribeAppHostServerAssemblySkew(new InvalidOperationException(message));

        Assert.NotNull(hint);
        Assert.Contains("older than the Aspire packages", hint);
    }

    [Fact]
    public void TryDescribeAppHostServerAssemblySkew_FindsTheCauseThroughInnerExceptions()
    {
        var inner = new InvalidOperationException("Could not load type 'Aspire.TypeSystem.CommandUpToDateCheck' from assembly 'Aspire.TypeSystem'.");
        var hint = GuestAppHostProject.TryDescribeAppHostServerAssemblySkew(new InvalidOperationException("wrapped", inner));

        Assert.NotNull(hint);
    }

    [Theory]
    [InlineData("Could not load type 'Contoso.Widgets.Thing' from assembly 'Contoso.Widgets'.")]
    [InlineData("Connection refused.")]
    public void TryDescribeAppHostServerAssemblySkew_IgnoresUnrelatedFailures(string message)
    {
        Assert.Null(GuestAppHostProject.TryDescribeAppHostServerAssemblySkew(new InvalidOperationException(message)));
    }
}
