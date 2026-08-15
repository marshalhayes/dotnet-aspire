// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Java.Tests;

/// <summary>
/// The command line a build tool wrapper launch is expected to produce on the current platform.
/// </summary>
/// <remarks>
/// Unix launches go through <c>sh</c> because a wrapper committed from Windows checks out without an
/// executable bit, so the wrapper path moves from the command into the first argument. Windows runs the
/// <c>.cmd</c> and <c>.bat</c> wrappers directly. Tests state the wrapper and the tool's own arguments
/// and let this decide the shape, so neither platform is asserted against the other's command line.
/// </remarks>
internal static class ExpectedWrapperInvocation
{
    public static string Command(string wrapperPath)
        => OperatingSystem.IsWindows() ? wrapperPath : "sh";

    public static string[] Args(string wrapperPath, params string[] toolArgs)
        => OperatingSystem.IsWindows() ? toolArgs : [wrapperPath, .. toolArgs];
}
