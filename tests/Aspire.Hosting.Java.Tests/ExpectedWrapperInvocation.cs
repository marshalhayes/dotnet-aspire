// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Java.Tests;

/// <summary>
/// The command line a build tool wrapper launch is expected to produce on the current platform.
/// </summary>
/// <remarks>
/// Unix launches go through <c>sh</c>, because a wrapper committed from Windows checks out without an
/// executable bit, so the wrapper path moves from the command into the first argument. Windows launches
/// the <c>.cmd</c> and <c>.bat</c> wrappers through the command interpreter, because a batch file started
/// with redirected stdout can silently produce no output, and passes the wrapper relative to the working
/// directory so <c>cmd.exe</c> never sees a quoted first token.
/// <para>
/// Tests state the wrapper, the working directory, and the tool's own arguments, and let this decide the
/// shape, so neither platform is asserted against the other's command line.
/// </para>
/// </remarks>
internal static class ExpectedWrapperInvocation
{
    public static string Command()
        => OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
            : "sh";

    public static string[] Args(string wrapperPath, string workingDirectory, params string[] toolArgs)
        => OperatingSystem.IsWindows()
            ? ["/c", Path.GetRelativePath(workingDirectory, wrapperPath), .. toolArgs]
            : [wrapperPath, .. toolArgs];
}
