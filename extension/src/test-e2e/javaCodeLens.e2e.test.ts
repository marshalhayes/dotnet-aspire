import * as assert from 'assert';
import { EditorView, TextEditor, VSBrowser } from 'vscode-extension-tester';
import { getJavaAppHostSourcePath, prepareJavaWorkspace } from './helpers/java';
import { closeAllEditors } from './helpers/vscode';

suite('Java AppHost CodeLens E2E', function () {
    this.timeout(300000);

    suiteSetup(async () => {
        await prepareJavaWorkspace();
    });

    suiteTeardown(async () => {
        await closeAllEditors();
    });

    test('shows the entry point warning on a Java AppHost', async () => {
        const appHostPath = getJavaAppHostSourcePath();

        await VSBrowser.instance.openResources(appHostPath);
        const editor = await new EditorView().openEditor('AppHost.java') as TextEditor;

        const lenses = await editor.getCodeLenses();
        const texts = await Promise.all(lenses.map(lens => lens.getText()));

        assert.ok(
            texts.some(text => text.includes('bypass Aspire')),
            `expected the Run/Debug bypass warning, got: ${JSON.stringify(texts)}`);
    });
});
