import * as assert from 'assert';
import * as path from 'path';
import { EditorView, TextEditor, VSBrowser } from 'vscode-extension-tester';
import { waitForWorkspaceAppHostCandidate } from './helpers/assertions';
import { getWorkspaceRoot } from './helpers/paths';
import { closeAllEditors } from './helpers/vscode';

const APP_HOST_SOURCE = path.join('JavaSpringBoot.AppHost.Java', 'AppHost.java');

suite('Java AppHost CodeLens E2E', function () {
    this.timeout(180000);

    suiteSetup(function () {
        if (process.env.ASPIRE_EXTENSION_E2E_ENABLE_JAVA !== 'true') {
            this.skip();
        }
    });

    suiteTeardown(async () => {
        await closeAllEditors();
    });

    test('shows the entry point warning on a Java AppHost', async () => {
        const appHostPath = path.join(getWorkspaceRoot(), APP_HOST_SOURCE);

        await waitForWorkspaceAppHostCandidate(appHostPath, 180000);
        await VSBrowser.instance.openResources(appHostPath);
        const editor = await new EditorView().openEditor('AppHost.java') as TextEditor;

        const lenses = await editor.getCodeLenses();
        const texts = await Promise.all(lenses.map(lens => lens.getText()));

        assert.ok(
            texts.some(text => text.includes('bypass Aspire')),
            `expected the Run/Debug bypass warning, got: ${JSON.stringify(texts)}`);
    });
});
