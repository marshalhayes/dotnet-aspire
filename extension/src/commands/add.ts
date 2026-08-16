import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { AppHostCommandTarget } from '../utils/appHostArgs';
import { CliPathResolutionTarget } from '../utils/cliPathVariables';

export async function addCommand(
    terminalProvider: AspireTerminalProvider,
    _editorCommandProvider: AspireEditorCommandProvider,
    appHost: AppHostCommandTarget,
    target: CliPathResolutionTarget,
) {
    await terminalProvider.sendAspireCommandToAspireTerminal('add', true, appHost.args, { target });
}
