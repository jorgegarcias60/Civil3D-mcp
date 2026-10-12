import { readdirSync, readFileSync } from "node:fs";
import { PassThrough } from "node:stream";
import ts from "typescript";
import { describe, expect, it, vi } from "vitest";
import { drainServerStderr } from "../scripts/server-stderr.mjs";

// Inspect each constructor independently, including transports inside nested blocks.
function undrainedTransports(source: string): string[] {
  const file = ts.createSourceFile("script.mjs", source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const failures: string[] = [];
  const importsDrain = file.statements.some((statement) => ts.isImportDeclaration(statement)
    && ts.isStringLiteral(statement.moduleSpecifier) && statement.moduleSpecifier.text === "./server-stderr.mjs"
    && statement.importClause?.namedBindings && ts.isNamedImports(statement.importClause.namedBindings)
    && statement.importClause.namedBindings.elements.some((binding) => binding.name.text === "drainServerStderr"));
  function visit(node: ts.Node) {
    if (ts.isNewExpression(node) && node.expression.getText(file) === "StdioClientTransport") {
      const options = node.arguments?.[0];
      const pipesStderr = options && ts.isObjectLiteralExpression(options) && options.properties.some((property) =>
        ts.isPropertyAssignment(property) && property.name.getText(file).replace(/["']/g, "") === "stderr"
        && ts.isStringLiteral(property.initializer) && property.initializer.text === "pipe");
      if (pipesStderr) {
        const declaration = node.parent;
        const name = ts.isVariableDeclaration(declaration) && ts.isIdentifier(declaration.name) ? declaration.name.text : undefined;
        let statement: ts.Node = node;
        while (statement.parent && !ts.isBlock(statement.parent) && !ts.isSourceFile(statement.parent)) statement = statement.parent;
        const parent = statement.parent;
        const statements = parent && (ts.isBlock(parent) || ts.isSourceFile(parent)) ? parent.statements : [];
        const next = statements[statements.indexOf(statement as ts.Statement) + 1];
        const expected = `${name}.stderr`;
        let consumes = false;
        if (next && name) {
          function findConsumer(candidate: ts.Node) {
            if (ts.isCallExpression(candidate)) {
              const callee = candidate.expression.getText(file);
              const args = candidate.arguments;
              consumes ||= (importsDrain && callee === "drainServerStderr" && args[0]?.getText(file) === expected)
                || (callee === `${expected}.on` && args.length >= 2 && ts.isStringLiteral(args[0]) && args[0].text === "data")
                || callee === `${expected}.resume` || callee === `${expected}.pipe`;
            }
            ts.forEachChild(candidate, findConsumer);
          }
          findConsumer(next);
        }
        if (!consumes) failures.push(name ?? "unassigned transport");
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(file);
  return failures;
}

describe("script server stderr drains", () => {
  const scripts = new URL("../scripts/", import.meta.url);
  const files = readdirSync(scripts, { recursive: true }).filter((file) => typeof file === "string" && file.endsWith(".mjs")) as string[];
  it.each(files)("%s consumes each piped MCP transport immediately", (file) => {
    const source = readFileSync(new URL(file.replace(/\\/g, "/"), scripts), "utf8");
    expect(undrainedTransports(source)).toEqual([]);
  });

  it("rejects the original undrained transport and an undrained nested child", () => {
    const source = 'const transport = new StdioClientTransport({ stderr: "pipe" });\nawait client.connect(transport);';
    expect(undrainedTransports(source)).toEqual(["transport"]);
    expect(undrainedTransports(`import { drainServerStderr } from "./server-stderr.mjs";
      const transport = new StdioClientTransport({ stderr: "pipe" });
      const tail = drainServerStderr(transport.stderr);
      async function check() { const badTransport = new StdioClientTransport({ stderr: "pipe" }); await client.connect(badTransport); }`))
      .toEqual(["badTransport"]);
  });

  it("drains beyond the pipe capacity, stays quiet and retains only 200 bounded lines", () => {
    const stream = new PassThrough();
    const output = vi.spyOn(console, "error").mockImplementation(() => undefined);
    try {
      const printTail = drainServerStderr(stream);
      for (let i = 0; i < 300; i++) stream.write(`line-${i}: ${"x".repeat(1500)}\n`);
      expect(stream.readableLength).toBe(0);
      expect(output).not.toHaveBeenCalled();
      printTail();
      const tail = String(output.mock.calls[0][0]).split("\n").slice(1);
      expect(tail).toHaveLength(200);
      expect(tail[0]).toMatch(/^line-100:/);
      expect(tail[199]).toMatch(/^line-299:/);
      stream.write("y".repeat(100_000));
      stream.write("z".repeat(100_000));
      printTail();
      const partialTail = String(output.mock.calls[1][0]).split("\n").slice(1);
      expect(partialTail).toHaveLength(200);
      expect(partialTail[199]).toBe("z".repeat(4096));
      stream.write("\n");
      printTail();
      expect(String(output.mock.calls[2][0]).split("\n").at(-1)).toBe("z".repeat(4096));
    } finally {
      output.mockRestore();
      stream.destroy();
    }
  });
});
