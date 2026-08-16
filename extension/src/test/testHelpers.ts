import * as vscode from 'vscode';

export function languageIdForPath(filePath: string): string {
    if (filePath.endsWith('.cs')) { return 'csharp'; }
    if (filePath.endsWith('.ts')) { return 'typescript'; }
    if (filePath.endsWith('.rs')) { return 'rust'; }
    if (filePath.endsWith('.java')) { return 'java'; }
    if (filePath.endsWith('.py')) { return 'python'; }
    if (filePath.endsWith('.go')) { return 'go'; }
    return 'javascript';
}

export function createMockDocument(content: string, filePath: string): vscode.TextDocument {
    const lines = content.split('\n');
    return {
        uri: vscode.Uri.file(filePath),
        fileName: filePath,
        isUntitled: false,
        languageId: languageIdForPath(filePath),
        version: 1,
        isDirty: false,
        isClosed: false,
        eol: vscode.EndOfLine.LF,
        lineCount: lines.length,
        encoding: 'utf-8',
        save: () => Promise.resolve(false),
        lineAt: (lineOrPos: number | vscode.Position) => {
            const lineNum = typeof lineOrPos === 'number' ? lineOrPos : lineOrPos.line;
            const text = lines[lineNum] || '';
            return {
                lineNumber: lineNum,
                text,
                range: new vscode.Range(lineNum, 0, lineNum, text.length),
                rangeIncludingLineBreak: new vscode.Range(lineNum, 0, lineNum + 1, 0),
                firstNonWhitespaceCharacterIndex: text.search(/\S/),
                isEmptyOrWhitespace: text.trim().length === 0,
            } as vscode.TextLine;
        },
        offsetAt: (position: vscode.Position) => {
            let offset = 0;
            for (let i = 0; i < position.line && i < lines.length; i++) {
                offset += lines[i].length + 1;
            }
            return offset + position.character;
        },
        positionAt: (offset: number) => {
            let remaining = offset;
            for (let i = 0; i < lines.length; i++) {
                if (remaining <= lines[i].length) {
                    return new vscode.Position(i, remaining);
                }
                remaining -= lines[i].length + 1;
            }
            return new vscode.Position(lines.length - 1, lines[lines.length - 1].length);
        },
        getText: (range?: vscode.Range) => {
            if (!range) {
                return content;
            }
            const startOffset = lines.slice(0, range.start.line).reduce((sum, line) => sum + line.length + 1, 0) + range.start.character;
            const endOffset = lines.slice(0, range.end.line).reduce((sum, line) => sum + line.length + 1, 0) + range.end.character;
            return content.substring(startOffset, endOffset);
        },
        getWordRangeAtPosition: () => undefined,
        validateRange: (range: vscode.Range) => range,
        validatePosition: (position: vscode.Position) => position,
        notebook: undefined as any,
    } as vscode.TextDocument;
}

/**
 * Builds a workspace folder whose `fsPath` is exactly `fsPath`, whatever host the tests run on.
 *
 * `vscode.Uri.file('/repo/a').fsPath` renders with the host's separators, so on Windows it comes back
 * as `\\repo\\a`. Any test that hands the result to code taking an explicit platform argument then
 * stops testing that argument: the path is already Windows-shaped before the POSIX branch sees it.
 */
export function createWorkspaceFolder(name: string, fsPath: string, index: number = 0): vscode.WorkspaceFolder {
    const uri = vscode.Uri.file(fsPath);

    return {
        // Shadows the Uri prototype's fsPath getter and leaves every other member intact.
        uri: Object.create(uri, { fsPath: { value: fsPath, enumerable: true } }) as vscode.Uri,
        name,
        index,
    };
}
