import * as assert from 'assert';
import * as vscode from 'vscode';
import {
    ExpandedConfiguredCliPath,
    expandConfiguredCliPath,
    getCliExecutableCandidates,
    getCliPathTargetKey,
    windowCliPathTarget,
    workspaceFolderCliPathTarget,
} from '../utils/cliPathVariables';

function makeFolder(name: string, fsPath: string, index: number = 0): vscode.WorkspaceFolder {
    return { uri: vscode.Uri.file(fsPath), name, index };
}

suite('cliPathVariables tests', () => {

    suite('CliPathResolutionTarget contract', () => {

        test('windowCliPathTarget has kind === "window"', () => {
            assert.strictEqual(windowCliPathTarget.kind, 'window');
        });

        test('workspaceFolderCliPathTarget has kind === "workspaceFolder" and correct workspaceFolder', () => {
            const folder = makeFolder('myRepo', '/repo');
            const target = workspaceFolderCliPathTarget(folder);

            assert.strictEqual(target.kind, 'workspaceFolder');
            if (target.kind === 'workspaceFolder') {
                assert.strictEqual(target.workspaceFolder, folder);
            }
        });

    });

    suite('expandConfiguredCliPath', () => {

        test('expands ${workspaceFolder} and normalizes path traversal on linux', () => {
            const configuredPath = '${workspaceFolder}/../../artifacts/bin/Aspire.Cli/Debug/net10.0/aspire';
            const folder = makeFolder('JavaSpringBoot', '/repo/playground/JavaSpringBoot');
            const target = workspaceFolderCliPathTarget(folder);

            const result = expandConfiguredCliPath(configuredPath, target, [folder], 'linux');

            assert.strictEqual(result.configuredPath, configuredPath);
            assert.strictEqual(result.resolvedPath, '/repo/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire');
            assert.strictEqual(result.error, undefined);
        });

        test('expands ${workspaceFolder:tools} to the uniquely-named folder, independent of operation target', () => {
            const configuredPath = '${workspaceFolder:tools}/bin/aspire';
            const toolsFolder = makeFolder('tools', '/repo/tools', 1);
            const playgroundFolder = makeFolder('playground', '/repo/playground', 0);
            const target = workspaceFolderCliPathTarget(playgroundFolder);

            const result = expandConfiguredCliPath(configuredPath, target, [playgroundFolder, toolsFolder], 'linux');

            assert.strictEqual(result.resolvedPath, '/repo/tools/bin/aspire');
            assert.strictEqual(result.error, undefined);
        });

        test('window target with two open folders returns undefined resolvedPath and error mentioning multiple workspace folders', () => {
            const configuredPath = '${workspaceFolder}/bin/aspire';
            const folder1 = makeFolder('alpha', '/repo/alpha', 0);
            const folder2 = makeFolder('beta', '/repo/beta', 1);

            const result = expandConfiguredCliPath(configuredPath, windowCliPathTarget, [folder1, folder2], 'linux');

            assert.strictEqual(result.resolvedPath, undefined);
            assert.ok(result.error, 'expected an error message');
            assert.ok(
                result.error.toLowerCase().includes('multiple') || result.error.toLowerCase().includes('ambiguous'),
                `error should mention multiple folders; got: ${result.error}`
            );
        });

        test('plain relative path is returned unchanged with no resolvedPath or error', () => {
            const configuredPath = '../../artifacts/aspire';

            const result = expandConfiguredCliPath(configuredPath, windowCliPathTarget, [], 'linux');

            assert.strictEqual(result.configuredPath, configuredPath);
            assert.strictEqual(result.resolvedPath, undefined);
            assert.strictEqual(result.error, undefined);
        });

        test('window target in a single-folder workspace expands unqualified token', () => {
            const folder = makeFolder('repo', '/repo', 0);

            const result = expandConfiguredCliPath('${workspaceFolder}/bin/aspire', windowCliPathTarget, [folder], 'linux');

            assert.strictEqual(result.resolvedPath, '/repo/bin/aspire');
            assert.strictEqual(result.error, undefined);
        });

        test('unknown token is rejected with an error', () => {
            const result = expandConfiguredCliPath('${customVar}/aspire', windowCliPathTarget, [], 'linux');

            assert.strictEqual(result.resolvedPath, undefined);
            assert.ok(result.error, 'expected an error message');
        });

        test('missing named folder is rejected with an error', () => {
            const folder = makeFolder('root', '/repo', 0);

            const result = expandConfiguredCliPath('${workspaceFolder:nonexistent}/aspire', windowCliPathTarget, [folder], 'linux');

            assert.strictEqual(result.resolvedPath, undefined);
            assert.ok(result.error, 'expected an error message');
        });

        test('ambiguous named folder (two folders with same name) is rejected with an error', () => {
            const folder1 = makeFolder('tools', '/repo/alpha/tools', 0);
            const folder2 = makeFolder('tools', '/repo/beta/tools', 1);

            const result = expandConfiguredCliPath('${workspaceFolder:tools}/aspire', windowCliPathTarget, [folder1, folder2], 'linux');

            assert.strictEqual(result.resolvedPath, undefined);
            assert.ok(result.error, 'expected an error message');
            assert.ok(
                result.error.toLowerCase().includes('ambiguous') || result.error.toLowerCase().includes('multiple'),
                `error should mention ambiguity; got: ${result.error}`
            );
        });

    });

    suite('getCliExecutableCandidates', () => {

        test('Windows extensionless path returns exact, .exe, .cmd, .bat in order', () => {
            const candidates = getCliExecutableCandidates('C:\\tools\\aspire', 'win32');

            assert.deepStrictEqual(candidates, [
                'C:\\tools\\aspire',
                'C:\\tools\\aspire.exe',
                'C:\\tools\\aspire.cmd',
                'C:\\tools\\aspire.bat',
            ]);
        });

        test('Windows path already ending in .exe returns only itself', () => {
            const candidates = getCliExecutableCandidates('C:\\tools\\aspire.exe', 'win32');

            assert.deepStrictEqual(candidates, ['C:\\tools\\aspire.exe']);
        });

        test('non-Windows path returns only itself', () => {
            const candidates = getCliExecutableCandidates('/usr/local/bin/aspire', 'linux');

            assert.deepStrictEqual(candidates, ['/usr/local/bin/aspire']);
        });

    });

    suite('getCliPathTargetKey', () => {

        test('window target key is "window"', () => {
            assert.strictEqual(getCliPathTargetKey(windowCliPathTarget), 'window');
        });

        test('workspaceFolder target key is "workspaceFolder:${folder.uri.toString()}"', () => {
            const folder = makeFolder('myFolder', '/repo');
            const target = workspaceFolderCliPathTarget(folder);

            assert.strictEqual(getCliPathTargetKey(target), `workspaceFolder:${folder.uri.toString()}`);
        });

    });

});
