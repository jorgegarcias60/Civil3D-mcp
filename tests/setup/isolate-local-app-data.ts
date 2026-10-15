import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

// Tests never read this machine's real Civil 3D discovery files: a running Civil 3D adds an instance,
// which changes routing and approval results (24 approval tests failed while Civil 3D was open).
process.env.CIVIL3D_TEST_REAL_LOCALAPPDATA ??= process.env.LOCALAPPDATA ?? "";
process.env.LOCALAPPDATA = mkdtempSync(join(tmpdir(), "c3d-test-localappdata-"));
