import * as assert from 'assert';
import { VSBrowser } from 'vscode-extension-tester';
import { getJavaAppHostSourcePath, prepareJavaWorkspace, waitForJavaLanguageServerImport } from './helpers/java';
import { closeAllEditors, waitForCodeLensText } from './helpers/vscode';

suite('Java AppHost CodeLens E2E', function () {
    // Matches the other Java specs: the language server import below is allowed 15 minutes on a cold
    // runner, so a 5 minute suite budget would abort the setup rather than the thing it waits for.
    this.timeout(1800000);

    suiteSetup(async () => {
        await prepareJavaWorkspace();

        // VS Code renders one merged CodeLens set per document, so a `java` file shows nothing at all
        // until every registered provider has answered - including redhat.java's. On a cold CI runner
        // that server is still importing, which is how this spec timed out with `CodeLenses: (none)`
        // while the Aspire lens itself was ready. The other two Java specs already wait here.
        await waitForJavaLanguageServerImport();
    });

    suiteTeardown(async () => {
        await closeAllEditors();
    });

    test('shows the entry point warning on a Java AppHost', async () => {
        const appHostPath = getJavaAppHostSourcePath();

        await VSBrowser.instance.openResources(appHostPath);

        // openResources returns before the tab exists and the lenses are produced asynchronously after
        // that, so both are polled rather than read once. The suiteSetup guarantees the Java language
        // server reached Standard mode, but VS Code still has to run the merged provider pass on this
        // document afterwards, so this allows more than the 60s default.
        const texts = await waitForCodeLensText('AppHost.java', 'bypass Aspire', 180000);

        assert.ok(
            texts.some(text => text.includes('bypass Aspire')),
            `expected the Run/Debug bypass warning, got: ${JSON.stringify(texts)}`);
    });
});
