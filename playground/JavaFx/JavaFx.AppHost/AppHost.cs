// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

builder.AddJavaApp("javafx", "../desktop", "target/javafx-desktop-1.0.0.jar")
    .WithMavenBuild("-B", "-ntp", "-DskipTests", "package")
    .WithMainClass("com.example.javafx.JavaFxLauncher")
    // JavaFX module options are launcher options, not application arguments. They have to be present
    // both when Aspire starts `java -jar` and when VS Code bypasses the Maven wrapper for debugging.
    .WithJvmArgs("--module-path", "target/javafx-modules", "--add-modules", "javafx.controls,javafx.fxml");

#if !SKIP_DASHBOARD_REFERENCE
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();
