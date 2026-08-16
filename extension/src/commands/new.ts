import { AspireTerminalProvider } from "../utils/AspireTerminalProvider";
import { CliPathResolutionTarget } from '../utils/cliPathVariables';

export async function newCommand(terminalProvider: AspireTerminalProvider, target: CliPathResolutionTarget) {
    await terminalProvider.sendAspireCommandToAspireTerminal('new', true, undefined, { target });
};
