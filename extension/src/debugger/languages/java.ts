import * as path from 'path';
import * as vscode from 'vscode';
import { javaDebugExtensionId, javaLanguageExtensionId } from '../../capabilities';
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, isJavaLaunchConfiguration, JavaLaunchConfiguration } from "../../dcp/types";
import { invalidLaunchConfiguration, javaDisplayName, javaLabel } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";

// Commands contributed by redhat.java. They only exist once the language server has activated, so
// every call site has to tolerate them being missing.
const JAVA_EXECUTE_WORKSPACE_COMMAND = 'java.execute.workspaceCommand';
const JAVA_RESOLVE_BUILD_FILES_COMMAND = 'vscode.java.resolveBuildFiles';
const JAVA_PROJECT_CONFIGURATION_UPDATE_COMMAND = 'java.projectConfiguration.update';

// Long enough for a cold language server import on a large multi-module project, short enough that a
// mode transition that never comes does not look like a hang.
const javaLanguageServerReadyTimeoutMs = 30_000;

// Subset of the redhat.java extension API surface we use.
// https://github.com/redhat-developer/vscode-java#extension-api
interface JavaExtensionApi {
    serverMode: string;
    serverReady: () => Promise<boolean>;
}

async function getJavaExtensionApi(): Promise<JavaExtensionApi | null> {
    const extension = vscode.extensions.getExtension<JavaExtensionApi>(javaLanguageExtensionId);

    if (!extension) {
        return null;
    }

    if (!extension.isActive) {
        // Activation can fail (no JDK, corrupt workspace metadata, ...). Treat that the same as the
        // extension being absent so the launch can still proceed without classpath refresh.
        await extension.activate();
    }

    return extension.exports ?? null;
}

async function waitForJavaLanguageServerReady(): Promise<boolean> {
    try {
        const api = await getJavaExtensionApi();

        if (!api) {
            extensionLogOutputChannel.warn(`The Java language server (${javaLanguageExtensionId}) is not installed or exposes no API.`);
            return false;
        }

        extensionLogOutputChannel.info(`Java language server is in ${api.serverMode} mode, waiting for readiness...`);

        // serverReady() resolves when the *standard* language server is ready. In LightWeight mode that
        // transition is triggered by opening a project file, which may never happen, so an unbounded await
        // would hold the resource launch open indefinitely. The refresh this gates is only a convenience,
        // so a timeout degrades to launching without it rather than blocking the user's F5.
        // serverReady() resolves false when the server settled into a mode that will never serve
        // classpath queries, so the resolved value is honoured rather than treated as a bare signal.
        const ready = await Promise.race([
            api.serverReady().then(result => result !== false),
            new Promise<false>(resolve => setTimeout(() => resolve(false), javaLanguageServerReadyTimeoutMs))
        ]);

        if (!ready) {
            extensionLogOutputChannel.warn(`The Java language server did not become ready within ${javaLanguageServerReadyTimeoutMs}ms (mode: ${api.serverMode}).`);
        }

        return ready;
    } catch (e) {
        extensionLogOutputChannel.warn(`Error waiting for Java language server readiness: ${e}`);
    }

    return false;
}

async function updateJavaProjectConfiguration(buildTool: string): Promise<void> {
    const buildFiles = await vscode.commands.executeCommand<string[]>(
        JAVA_EXECUTE_WORKSPACE_COMMAND,
        JAVA_RESOLVE_BUILD_FILES_COMMAND
    );

    if (!buildFiles?.length) {
        extensionLogOutputChannel.info(`The Java language server reported no ${buildTool} build files to refresh.`);
        return;
    }

    extensionLogOutputChannel.info(`Updating ${buildTool} project configuration for ${buildFiles.length} build file(s)...`);

    for (const buildFile of buildFiles) {
        await vscode.commands.executeCommand(JAVA_PROJECT_CONFIGURATION_UPDATE_COMMAND, vscode.Uri.parse(buildFile));
    }
}

// Refreshing the classpath is a convenience for fresh clones and for projects whose build files
// changed since the language server last imported them; nothing about launching depends on it.
// redhat.java is therefore treated as optional at runtime: when it is missing or still starting, its
// commands are not registered and executeCommand rejects with "command not found", which would
// otherwise surface as an opaque failure that aborts the whole resource launch.
async function tryRefreshJavaProjectConfiguration(launchConfig: JavaLaunchConfiguration): Promise<void> {
    // A null build_tool means the resource runs a prebuilt JAR, so there are no build files to
    // reimport and no reason to pay for language server startup.
    if (!launchConfig.build_tool) {
        extensionLogOutputChannel.info('Skipping Java project configuration refresh because the resource does not declare a build tool.');
        return;
    }

    if (!await waitForJavaLanguageServerReady()) {
        extensionLogOutputChannel.warn(`Skipping the ${launchConfig.build_tool} project configuration refresh because the Java language server is unavailable. Launching anyway.`);
        return;
    }

    try {
        await updateJavaProjectConfiguration(launchConfig.build_tool);
    } catch (e) {
        extensionLogOutputChannel.warn(`Failed to refresh the ${launchConfig.build_tool} project configuration: ${e}. Launching anyway.`);
    }
}

// path.isAbsolute resolves against the *host* platform, but the app host can hand us a Windows path
// while the extension runs on POSIX (remote/WSL/container scenarios), so check both flavours.
// path.win32.isAbsolute also accepts POSIX-rooted paths, but being explicit keeps the intent clear.
function isAbsolutePath(value: string): boolean {
    return path.win32.isAbsolute(value) || path.posix.isAbsolute(value);
}

// main_class is either a fully qualified class name (com.example.Api), optionally prefixed with a
// module name (app/com.example.Api), or the path of a .java source file. Only the class name is
// worth showing in the Call Stack view; a file path is less specific than the project directory the
// user recognises.
function isFullyQualifiedClassName(mainClass: string): boolean {
    return mainClass.includes('.')
        && !mainClass.toLowerCase().endsWith('.java')
        && !isAbsolutePath(mainClass);
}

function getProjectFile(launchConfig: ExecutableLaunchConfiguration): string {
    if (isJavaLaunchConfiguration(launchConfig)) {
        // The Java project directory is the only path the app host sends. It also feeds the central
        // cwd derivation in prepareDebugSession, which the callback below then overrides explicitly.
        return launchConfig.working_directory || '';
    }

    throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
}

export const javaDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'java',
    debugAdapter: 'java',
    extensionId: javaDebugExtensionId,

    getDisplayName: (launchConfig: ExecutableLaunchConfiguration) => {
        if (!isJavaLaunchConfiguration(launchConfig)) {
            return javaLabel;
        }

        const mainClass = launchConfig.main_class;
        if (mainClass && isFullyQualifiedClassName(mainClass)) {
            return javaDisplayName(mainClass);
        }

        // asRelativePath keeps the Call Stack view readable. Rendering the directory through
        // vscode.Uri.file(...).toString() instead produces a percent-encoded URI such as
        // "Java: file:///c%3A/repo/api".
        const workingDirectory = launchConfig.working_directory;

        return workingDirectory ? javaDisplayName(vscode.workspace.asRelativePath(workingDirectory)) : javaLabel;
    },

    getSupportedFileTypes: () => ['.java'],

    getProjectFile: (launchConfig) => getProjectFile(launchConfig),

    createDebugSessionConfigurationCallback: async (
        launchConfig: ExecutableLaunchConfiguration,
        args: string[] | undefined,
        _env: { name: string; value: string }[],
        launchOptions: { debug: boolean;[key: string]: any },
        debugConfiguration: AspireResourceExtendedDebugConfiguration
    ): Promise<void> => {
        if (!isJavaLaunchConfiguration(launchConfig)) {
            extensionLogOutputChannel.info(`The resource type was not java for ${JSON.stringify(launchConfig)}`);
            throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
        }

        await tryRefreshJavaProjectConfiguration(launchConfig);

        debugConfiguration.type = 'java';
        // The app host always sends "launch" today, but the wire schema allows "attach", so honour
        // whatever it sends rather than hard-coding the current behaviour.
        debugConfiguration.request = launchConfig.request ?? 'launch';
        debugConfiguration.noDebug = !launchOptions.debug;

        if (launchConfig.working_directory) {
            debugConfiguration.cwd = launchConfig.working_directory;
        }

        // vscjava.vscode-java-debug requires mainClass to start a launch session, and accepts a fully
        // qualified class name, optionally prefixed with a module name, or the path of a .java source
        // file.
        // https://github.com/microsoft/vscode-java-debug/blob/main/Configuration.md#main
        //
        // When it is absent the adapter resolves the entry point itself, and in a workspace holding
        // several Java resources it finds one main class per project and prompts the user to pick.
        // projectName narrows that search to this resource's project, and the app host sends it
        // whenever it could read the project's name out of its build file -- with or without a main
        // class. Scoping matters in both cases: without a main class it stops the prompt, and with one
        // it stops the adapter failing with "Main class ... isn't unique in the workspace" when the
        // same class is visible through two projects.
        // https://github.com/microsoft/vscode-java-debug/blob/main/Configuration.md#projectname
        if (launchConfig.main_class) {
            debugConfiguration.mainClass = launchConfig.main_class;
        }

        if (launchConfig.project_name) {
            debugConfiguration.projectName = launchConfig.project_name;
        }

        // A resource that runs a prebuilt JAR has no language server project containing its classes,
        // so the adapter cannot resolve the classpath on its own and would launch a JVM that fails
        // with NoClassDefFoundError. Sending the archive explicitly is what makes such a resource
        // debuggable; Maven and Gradle resources omit this and let the adapter resolve the project.
        // https://github.com/microsoft/vscode-java-debug/blob/main/Configuration.md#classpaths
        if (launchConfig.class_paths?.length) {
            debugConfiguration.classPaths = launchConfig.class_paths;
        }

        // JVM options, kept separate from `args` because the adapter passes these to the JVM and
        // `args` to main(String[]). Merging them would hand "-Xmx512m" to the application.
        // https://github.com/microsoft/vscode-java-debug/blob/main/Configuration.md#vmargs
        if (launchConfig.vm_args?.length) {
            debugConfiguration.vmArgs = launchConfig.vm_args;
        }

        // These are the application's own arguments. The app host strips the mvnw/gradlew wrapper
        // arguments for java launch configurations (the wrappers fork a second JVM that a debugger
        // attached to the wrapper would never see), so everything left here belongs to main(String[]).
        // https://github.com/microsoft/vscode-java-debug/blob/main/Configuration.md#arguments
        debugConfiguration.args = args ?? [];

        // `env` is deliberately left alone. prepareDebugSession already set it to
        // mergeEnvs(getEnvironmentWithoutE2EBridgeVariables(), env), i.e. the full inherited
        // environment with the resource's variables layered on top. Reassigning it from `env` alone
        // would launch the JVM without PATH or JAVA_HOME, so the adapter could not find `java`.
    }
};

// Splits the Java AppHost launch command the CLI sends into the pieces the debug adapter needs.
//
// Every Java AppHost toolchain (plain javac, Maven, and Gradle) produces the same shape, because
// JavaLanguageSupport compiles in a separate pre-execute step and JavaAppHostToolchainResolver
// launches directly rather than through `mvn exec:java` or `gradle run`:
//
//   ["java", "-cp", "/path/.java-build", "AppHost", "--operation", "run", "--socket", "/tmp/x.sock"]
//   ["java", "-cp", "target/classes:target/aspire-deps/*", "AppHost", "--operation", "run", ...]
//
// The classpath value can also follow "-classpath" or "--class-path", and JVM options may appear
// before the main class, so the main class is located as the first non-option argument that is not
// itself an option's value. Returns null when the command does not match, which keeps an
// unrecognised command on the non-debug launch path instead of starting a JVM with wrong arguments.
export function parseJavaAppHostCommand(args: string[]): { mainClass: string; classPaths: string[]; vmArgs: string[]; appHostArgs: string[] } | null {
    // args[0] is the "java" executable itself, prepended by the CLI.
    if (args.length < 2) {
        return null;
    }

    // Only a direct JVM invocation can be turned into a launch configuration. A build-tool wrapper
    // ("./mvnw exec:java", "./gradlew run") forks its own JVM, so its arguments are the tool's rather
    // than the JVM's and the first bare token is a goal or task, not a main class. Without this check
    // "exec:java" would be handed to the debug adapter as the class to launch.
    // The path may be absolute (a JAVA_HOME-qualified launcher), so compare only the file name.
    const executable = args[0].split(/[\\/]/).pop() ?? args[0];
    if (executable.toLowerCase().replace(/\.exe$/, '') !== 'java') {
        return null;
    }

    const classPathOptions = new Set(['-cp', '-classpath', '--class-path']);
    let classPaths: string[] = [];
    const vmArgs: string[] = [];

    for (let i = 1; i < args.length; i++) {
        const arg = args[i];

        if (classPathOptions.has(arg)) {
            // The JVM accepts the platform path separator here, and expands a trailing "/*" itself.
            const value = args[i + 1];
            if (value === undefined) {
                return null;
            }

            classPaths = value.split(path.delimiter).filter(entry => entry.length > 0);
            i++;
            continue;
        }

        if (arg.startsWith('-')) {
            vmArgs.push(arg);
            continue;
        }

        // First bare token after the options is the main class; everything after it is the
        // application's own arguments, which the JVM never interprets.
        return { mainClass: arg, classPaths, vmArgs, appHostArgs: args.slice(i + 1) };
    }

    return null;
}
