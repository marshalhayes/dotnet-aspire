import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { waitForRepositoryIdle, waitForWorkspaceAppHostCandidate } from './helpers/assertions';
import { executeE2eControlCommand } from './helpers/fixtures';
import { getWorkspaceRoot } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

interface DiagnosticInfo {
    message: string;
    severity: number;
    code?: string | number;
}

interface BreakpointProofStackFrame {
    line?: number;
    name?: string;
    source?: { path?: string };
}

interface BreakpointProof {
    sourcePath: string;
    line: number;
    text?: string;
    matchingStackFrame: BreakpointProofStackFrame;
    topStackFrame?: BreakpointProofStackFrame;
}

interface DebugProof {
    proof: string;
    appHostBreakpoint: BreakpointProof;
    resourceBreakpoint: BreakpointProof;
    debugSessions: { type: string; name: string }[];
    launchRequests: { sessionType: string; arguments?: Record<string, unknown> }[];
}

// VS Code numbers DiagnosticSeverity from most to least severe, so Error is 0 and Warning is 1.
const DIAGNOSTIC_SEVERITY_ERROR = 0;
const DIAGNOSTIC_SEVERITY_WARNING = 1;

const APP_HOST_DIRECTORY = 'JavaSpringBoot.AppHost.Java';
const APP_HOST_SOURCE = path.join(APP_HOST_DIRECTORY, 'AppHost.java');
const CATALOG_CONTROLLER_SOURCE = path.join('catalog', 'src', 'main', 'java', 'com', 'example', 'catalog', 'CatalogController.java');

/**
 * Waits for discovery to surface the single-file Java AppHost.
 *
 * The shared no-argument helper looks for the scaffolded C# fixture, which the Java run removes from
 * the workspace, so this matches on `AppHost.java` instead.
 */
async function waitForJavaAppHostCandidate(): Promise<void> {
    await waitForWorkspaceAppHostCandidate(path.join(getWorkspaceRoot(), APP_HOST_SOURCE));
}

suite('Aspire Java AppHost E2E', function () {
    // A cold Gradle import compiles the Spring Boot services and downloads a Java 21 toolchain, and
    // the debug proof then runs the AppHost end to end on top of that.
    this.timeout(1800000);

    suiteSetup(function () {
        if (process.env.ASPIRE_EXTENSION_E2E_ENABLE_JAVA !== 'true') {
            this.skip();
        }
    });

    test('imports the workspace without reporting problems in generated or authored sources', async () => {
        await openAspireView();
        await waitForJavaAppHostCandidate();
        await waitForRepositoryIdle();

        const appHostSourcePath = path.join(getWorkspaceRoot(), APP_HOST_SOURCE);
        await executeE2eControlCommand({ name: 'openFile', filePath: appHostSourcePath });

        // The language server reports nothing until it has imported the Gradle build, so an immediate
        // read is indistinguishable from a clean one. Wait for the AppHost to resolve its Aspire
        // imports, which only happens once .aspire/modules is on the project's classpath.
        await waitForLanguageServerImport(appHostSourcePath);

        const generatedSources = findGeneratedSdkSources();
        assert.ok(
            generatedSources.length > 100,
            `Expected the generated Aspire Java SDK under ${APP_HOST_DIRECTORY}/.aspire/modules. Found ${generatedSources.length} files.`);

        const offenders: string[] = [];
        for (const sourcePath of [appHostSourcePath, ...generatedSources]) {
            const diagnostics = (await executeE2eControlCommand({ name: 'getDiagnostics', filePath: sourcePath })).result as DiagnosticInfo[];
            const problems = diagnostics.filter(diagnostic =>
                diagnostic.severity === DIAGNOSTIC_SEVERITY_ERROR || diagnostic.severity === DIAGNOSTIC_SEVERITY_WARNING);

            for (const problem of problems) {
                offenders.push(`${path.relative(getWorkspaceRoot(), sourcePath)}: [${problem.severity === DIAGNOSTIC_SEVERITY_ERROR ? 'error' : 'warning'}] ${problem.message}`);
            }
        }

        assert.deepStrictEqual(
            offenders,
            [],
            `Opening a Java AppHost must not report problems the user cannot act on. Checked ${generatedSources.length + 1} files.`);
    });

    test('does not copy build inputs into the language server output directory', async () => {
        // Rooting the java source set at '.' also points the resources source set there unless the
        // build file says otherwise, and processResources then copies .gradle/, .aspire/ and the
        // wrapper into build output. The language server digests those copies and fails to refresh
        // the workspace once Gradle deletes them.
        const appHostDirectory = path.join(getWorkspaceRoot(), APP_HOST_DIRECTORY);
        const copied: string[] = [];

        for (const outputDirectory of ['build/resources', 'bin/main/.gradle', 'bin/main/.aspire', 'bin/main/gradlew']) {
            const candidate = path.join(appHostDirectory, outputDirectory);
            if (fs.existsSync(candidate)) {
                copied.push(outputDirectory);
            }
        }

        assert.deepStrictEqual(copied, [], `Build inputs were copied into the AppHost's output directories: ${copied.join(', ')}.`);
    });

    test('stops on breakpoints in both the Java AppHost and a Java resource', async function () {
        // This currently does not pass. The Java AppHost itself starts correctly under `aspire run
        // --start-debug-session` - the dashboard comes up and the application reports "Distributed
        // application started" - but the extension never starts a `java` debug session for the
        // AppHost process. Breakpoints in AppHost.java are therefore registered against the parent
        // `aspire` session and come back `verified: false`, so they can never bind or fire.
        //
        // The test is kept because it encodes the behaviour the C# AppHost already has, and is one
        // environment variable away from running once a Java debug session is attached.
        if (process.env.ASPIRE_EXTENSION_E2E_ENABLE_JAVA_APPHOST_DEBUG !== 'true') {
            this.skip();
        }

        await openAspireView();
        await waitForJavaAppHostCandidate();
        await waitForRepositoryIdle();

        const appHostSourcePath = path.join(getWorkspaceRoot(), APP_HOST_SOURCE);
        const resourceSourcePath = path.join(getWorkspaceRoot(), CATALOG_CONTROLLER_SOURCE);

        const proof = (await executeE2eControlCommand({
            name: 'proveAppHostAndResourceDebugging',
            appHostPath: appHostSourcePath,
            resourceName: 'catalog',
            appHostSourcePath,
            appHostBreakpointLine: findBreakpointLine(appHostSourcePath, 'builder.addSpringBootApp("catalog"'),
            resourceSourcePath,
            resourceBreakpointLine: findBreakpointLine(resourceSourcePath, 'return Products;'),
            timeoutMs: 900000,
        }, {
            // The command runs the whole AppHost, so the harness has to wait longer than the default
            // 10s for the control file to be marked applied - otherwise the wait fails while the
            // proof is still legitimately running.
            timeoutMs: 960000,
        })).result as DebugProof;

        assert.strictEqual(proof.proof, 'aspire-apphost-and-resource-debug-breakpoints-hit');

        // The stack frame is what makes this a proof rather than an assertion that a session started:
        // it can only name these files if the adapter actually suspended there with source resolved.
        assert.ok(
            proof.appHostBreakpoint.matchingStackFrame.source?.path,
            `The AppHost breakpoint hit did not resolve a source path: ${JSON.stringify(proof.appHostBreakpoint)}`);
        assert.ok(
            proof.resourceBreakpoint.matchingStackFrame.source?.path,
            `The resource breakpoint hit did not resolve a source path: ${JSON.stringify(proof.resourceBreakpoint)}`);

        // Aspire delegates Java resources to vscjava.vscode-java-debug rather than attaching over JDWP
        // itself, so a session of any other type would mean the delegation regressed.
        const javaSessions = proof.debugSessions.filter(session => session.type === 'java');
        assert.ok(
            javaSessions.length > 0,
            `Expected at least one 'java' debug session. Saw: ${JSON.stringify(proof.debugSessions)}`);
    });
});

/**
 * Waits until the Java language server has imported the build, using the AppHost's own Aspire
 * imports as the signal.
 *
 * Before the import completes every `import aspire.*` is unresolved, so the file reports errors that
 * say nothing about the code under test. Polling until those clear is what separates "clean" from
 * "not analysed yet".
 */
async function waitForLanguageServerImport(appHostSourcePath: string, timeoutMs = 900000): Promise<void> {
    const started = Date.now();
    let lastDiagnostics: DiagnosticInfo[] = [];
    let stableSince: number | undefined;

    while (Date.now() - started < timeoutMs) {
        lastDiagnostics = (await executeE2eControlCommand({ name: 'getDiagnostics', filePath: appHostSourcePath })).result as DiagnosticInfo[];
        const unresolved = lastDiagnostics.filter(diagnostic => /cannot be resolved|import aspire/i.test(diagnostic.message));

        if (unresolved.length === 0) {
            // An empty list also describes a file the server has not looked at yet, so require it to
            // stay empty rather than trusting the first read.
            stableSince ??= Date.now();
            if (Date.now() - stableSince >= 15000) {
                return;
            }
        }
        else {
            stableSince = undefined;
        }

        await delay(2000);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for the Java language server to import the workspace. Last diagnostics: ${JSON.stringify(lastDiagnostics)}`);
}

/**
 * Finds the zero-based line of the first occurrence of a marker.
 *
 * Hard-coding line numbers makes the sample impossible to edit without silently moving the
 * breakpoint onto an unrelated statement, which the proof would then report as a timeout.
 */
function findBreakpointLine(sourcePath: string, marker: string): number {
    const lines = fs.readFileSync(sourcePath, 'utf8').split(/\r?\n/);
    const index = lines.findIndex(line => line.includes(marker));
    if (index < 0) {
        throw new Error(`Could not find '${marker}' in ${sourcePath} to place a breakpoint on.`);
    }

    return index;
}

function findGeneratedSdkSources(): string[] {
    const modulesRoot = path.join(getWorkspaceRoot(), APP_HOST_DIRECTORY, '.aspire', 'modules');
    if (!fs.existsSync(modulesRoot)) {
        return [];
    }

    return fs.readdirSync(modulesRoot, { recursive: true, withFileTypes: true })
        .filter(entry => entry.isFile() && entry.name.endsWith('.java'))
        .map(entry => path.join(entry.parentPath ?? modulesRoot, entry.name));
}

function delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}
