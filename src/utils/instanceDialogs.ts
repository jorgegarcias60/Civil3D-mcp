import { access } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { windowsScriptFile } from "./instanceLauncher.js";

export interface InstanceDialog {
  title: string;
  className: string;
  buttons: string[];
  text?: string[];
  method?: "uia_invoke" | "button_notify" | "bm_click";
  action?: "ignore_invoked" | "ignore_unavailable";
  issues?: string[];
  error?: string;
}

// Titles of the prompts the scanner answers (its $knownPrompts table); matched trimmed and case-insensitively.
export const XREF_PROMPT = "References - Not Found Files";
export const SHX_PROMPT = "Missing SHX Files";
export const RECOVERY_PROMPT = "Drawing Recovery";
export const titleIs = (dialog: Pick<InstanceDialog, "title">, title: string) =>
  dialog.title.trim().toLowerCase() === title.toLowerCase();

export interface DialogScanOptions {
  /** Report dialogs and their buttons but never click anything. Default false. */
  reportOnly?: boolean;
}

export interface DialogScanResult {
  dialogs: InstanceDialog[];
  missingXrefs: string[];
}

/** Scans only windows belonging to the proven process identity. Answers known prompts unless reportOnly. */
export async function scanInstanceDialogs(pid: number, startedAtUtc: string, options: DialogScanOptions = {}): Promise<DialogScanResult> {
  if (!Number.isSafeInteger(pid) || pid <= 0 || !Number.isFinite(Date.parse(startedAtUtc))) {
    throw new Error("A valid process ID and UTC start time are required to scan dialogs.");
  }
  // Run as a file: with its Win32 helpers the scanner is too long for -EncodedCommand (spawn ENAMETOOLONG, live 2026-10-15).
  let script = fileURLToPath(new URL("./scan-instance-dialogs.ps1", import.meta.url));
  try { await access(script); }
  catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
    // Vitest imports the source module before the build asset exists.
    script = fileURLToPath(new URL("../../scripts/scan-instance-dialogs.ps1", import.meta.url));
  }
  const result = JSON.parse(await windowsScriptFile(script, { pid, startedAtUtc, reportOnly: options.reportOnly === true })) as DialogScanResult;
  if (!result || !Array.isArray(result.dialogs) || !Array.isArray(result.missingXrefs)) {
    throw new Error("Invalid instance dialog scan result.");
  }
  return result;
}
