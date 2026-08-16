import * as assert from 'assert';
import { VSBrowser } from 'vscode-extension-tester';
import { getJavaAppHostSourcePath, prepareJavaWorkspace } from './helpers/java';
import { closeAllEditors, waitForCodeLensText } from './helpers/vscode';

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

        // openResources returns before the tab exists and the lenses are produced asynchronously after
        // that, so both are polled rather than read once.
        const texts = await waitForCodeLensText('AppHost.java', 'bypass Aspire');

        assert.ok(
            texts.some(text => text.includes('bypass Aspire')),
            `expected the Run/Debug bypass warning, got: ${JSON.stringify(texts)}`);
    });
});
