import { AspireTerminalProvider } from "../utils/AspireTerminalProvider";
import { CliPathResolutionTarget } from '../utils/cliPathVariables';

export async function initCommand(terminalProvider: AspireTerminalProvider, target: CliPathResolutionTarget) {
    await terminalProvider.sendAspireCommandToAspireTerminal('init', true, undefined, { target });
};