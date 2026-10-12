// Attach before starting the child: the SDK exposes its PassThrough immediately.
// Bound both line count and line length, including an unterminated last line.
export function drainServerStderr(stream, label = "MCP server") {
  const maxLines = 200;
  const maxLineChars = 4096;
  const lines = [];
  let pending = "";
  stream.setEncoding("utf8");
  stream.on("data", (chunk) => {
    const parts = chunk.split("\n");
    for (let i = 0; i < parts.length; i++) {
      pending = (pending + parts[i]).slice(-maxLineChars);
      if (i < parts.length - 1) {
        lines.push(pending);
        if (lines.length > maxLines) lines.shift();
        pending = "";
      }
    }
  });
  return function printTail() {
    const tail = [...lines, ...(pending ? [pending] : [])].slice(-maxLines);
    if (tail.length) console.error(`${label} stderr (last ${maxLines} lines, up to ${maxLineChars} characters each):\n${tail.join("\n")}`);
  };
}
