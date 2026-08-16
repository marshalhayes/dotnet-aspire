import * as assert from 'assert';
import * as vscode from 'vscode';
import { getParserForDocument } from '../editor/parsers/AppHostResourceParser';
import '../editor/parsers/javaAppHostParser';
import { createMockDocument } from './testHelpers';

// Both playground AppHosts are JEP 512 implicitly declared classes, but they differ in ways the
// parser has to absorb: `var` vs explicit types, and `CreateBuilder()` vs `CreateBuilder(args)`.
const implicitClassAppHost = [
    'import aspire.*;',
    '',
    'void main() throws Exception {',
    '    var builder = DistributedApplication.CreateBuilder();',
    '',
    '    var catalog = builder.addSpringBootApp("catalog", "./catalog")',
    '        .withOtelAgentDefaultPath()',
    '        .withExternalHttpEndpoints();',
    '',
    '    builder.addJavaAppWithJar("worker", "./worker", "target/worker.jar");',
    '',
    '    builder.build().run();',
    '}',
].join('\n');

const explicitClassAppHost = [
    'import aspire.*;',
    '',
    'public class AppHost {',
    '    public static void main(String[] args) throws Exception {',
    '        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);',
    '        NodeAppResource app = builder.addNodeApp("app", "./api", "src/index.ts");',
    '        builder.build().run();',
    '    }',
    '}',
].join('\n');

function javaDoc(content: string): vscode.TextDocument {
    return createMockDocument(content, '/repo/AppHost.java');
}

suite('Java AppHost parser', () => {
    test('recognises an implicitly declared AppHost', async () => {
        const parser = await getParserForDocument(javaDoc(implicitClassAppHost));

        assert.ok(parser, 'expected a parser for a Java AppHost');
        assert.deepStrictEqual(parser.getSupportedExtensions(), ['.java']);
    });

    test('recognises a conventional class AppHost, which is the Maven and Gradle project shape', async () => {
        const parser = await getParserForDocument(javaDoc(explicitClassAppHost));

        assert.ok(parser, 'expected a parser for a class-based Java AppHost');
    });

    test('does not claim a Java file that never builds an application', async () => {
        const parser = await getParserForDocument(javaDoc('class Helper {\n    void main() {\n    }\n}'));

        assert.strictEqual(parser, undefined);
    });

    test('extracts every resource with its name, method and anchor line', async () => {
        const document = javaDoc(implicitClassAppHost);
        const parser = await getParserForDocument(document);
        const resources = await parser!.parseResources(document);

        assert.deepStrictEqual(resources.map(r => [r.name, r.methodName, r.kind, r.statementStartLine]), [
            ['catalog', 'addSpringBootApp', 'resource', 5],
            ['worker', 'addJavaAppWithJar', 'resource', 9],
        ]);
    });

    test('anchors a multi-line fluent chain on the declaration, not the chained call', async () => {
        const document = javaDoc(implicitClassAppHost);
        const parser = await getParserForDocument(document);
        const resources = await parser!.parseResources(document);

        assert.strictEqual(resources[0].range.start.line, 5, 'the range starts on the addSpringBootApp line');
        assert.strictEqual(resources[0].statementStartLine, 5, 'and the statement starts on the var declaration');
    });

    test('extracts resources declared with an explicit type', async () => {
        const document = javaDoc(explicitClassAppHost);
        const parser = await getParserForDocument(document);
        const resources = await parser!.parseResources(document);

        assert.deepStrictEqual(resources.map(r => r.name), ['app']);
    });

    test('classifies addStep as a pipeline step', async () => {
        const document = javaDoc('void main() {\n    var builder = DistributedApplication.CreateBuilder();\n    builder.addStep("deploy");\n}');
        const parser = await getParserForDocument(document);
        const resources = await parser!.parseResources(document);

        assert.deepStrictEqual(resources.map(r => [r.name, r.kind]), [['deploy', 'pipelineStep']]);
    });

    test('ignores commented-out and quoted resource calls', async () => {
        const document = javaDoc([
            'void main() {',
            '    var builder = DistributedApplication.CreateBuilder();',
            '    // builder.addRedis("commented");',
            '    /* builder.addPostgres("blocked"); */',
            '    var sample = "builder.addRedis(\\"quoted\\")";',
            '    builder.addRedis("real");',
            '}',
        ].join('\n'));
        const parser = await getParserForDocument(document);
        const resources = await parser!.parseResources(document);

        assert.deepStrictEqual(resources.map(r => r.name), ['real']);
    });

    test('ignores an add call whose first argument is not a literal name', async () => {
        const document = javaDoc('void main() {\n    var builder = DistributedApplication.CreateBuilder();\n    builder.addRedis(nameVariable);\n}');
        const parser = await getParserForDocument(document);
        const resources = await parser!.parseResources(document);

        assert.deepStrictEqual(resources, []);
    });

    test('finds the builder statement line for both AppHost shapes', async () => {
        const implicitDoc = javaDoc(implicitClassAppHost);
        const explicitDoc = javaDoc(explicitClassAppHost);

        assert.strictEqual(await (await getParserForDocument(implicitDoc))!.findBuilderStatementLine!(implicitDoc), 3);
        assert.strictEqual(await (await getParserForDocument(explicitDoc))!.findBuilderStatementLine!(explicitDoc), 4);
    });

    test('finds the entry point line for both AppHost shapes', async () => {
        const implicitDoc = javaDoc(implicitClassAppHost);
        const explicitDoc = javaDoc(explicitClassAppHost);

        assert.strictEqual(await (await getParserForDocument(implicitDoc))!.findAppHostEntryPointLine!(implicitDoc), 2);
        assert.strictEqual(await (await getParserForDocument(explicitDoc))!.findAppHostEntryPointLine!(explicitDoc), 3);
    });

    test('filters offsets that fall inside comments and strings', async () => {
        const content = 'void main() {\n    var builder = DistributedApplication.CreateBuilder();\n    // addRedis\n    builder.addRedis("real");\n}';
        const document = javaDoc(content);
        const parser = await getParserForDocument(document);

        const commentOffset = content.indexOf('addRedis');
        const codeOffset = content.indexOf('addRedis("real")');

        assert.deepStrictEqual(await parser!.filterActiveOffsets!(document, [commentOffset, codeOffset]), [codeOffset]);
    });
});
