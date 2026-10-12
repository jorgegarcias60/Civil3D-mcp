import { createHash, randomUUID } from "node:crypto";
import { withApplicationConnection } from "../utils/ConnectionManager.js";
import { Civil3DRpcError } from "../utils/SocketClient.js";
import { createLogger } from "../utils/logger.js";
import type { ToolCapability } from "./toolMetadata.js";

type JsonObject = Record<string, unknown>;

export interface ApprovalTarget {
  toolName: string;
  action: string;
  capabilities: ToolCapability[];
  safeForRetry: boolean;
  requiresActiveDrawing?: boolean;
}

interface ApprovalGrant {
  toolName: string;
  action: string;
  parametersHash: string;
  drawingFingerprint: string;
  expiresAt: number;
}

export interface ApprovalReceipt {
  approvalToken: string;
  expiresAt: string;
  drawingFingerprint: string;
}

export class ApprovalRequiredError extends Error {}

export class ApprovalValidationError extends Error {}

export type DrawingFingerprintProvider = () => Promise<string>;

const APPROVAL_TOKEN_TTL_MS = 5 * 60 * 1000;
const NO_ACTIVE_DRAWING_FINGERPRINT = "no-active-drawing";
export const RUN_CODE_APPROVAL_ENV = "CIVIL3D_RUN_CODE_APPROVAL";
const log = createLogger("ApprovalPolicy");
let warnedUnknownMode = false;

export function runCodeApprovalMode(): "token" | "client" {
  const mode = process.env[RUN_CODE_APPROVAL_ENV]?.trim().toLowerCase();
  if (!mode || mode === "token") return "token";
  if (mode === "client") return "client";
  if (!warnedUnknownMode) {
    warnedUnknownMode = true;
    log.warn(`Unknown ${RUN_CODE_APPROVAL_ENV} value; using token mode.`, { value: mode });
  }
  return "token";
}
const MUTATING_CAPABILITIES = new Set<ToolCapability>([
  "create",
  "edit",
  "delete",
  "manage",
  "import",
  "export",
]);
const EXPLICIT_APPROVAL_ACTION = /(?:^|_)(?:delete|remove|import|export|save|new|undo|redo|overwrite|replace|publish|promote|sync|fix|remediate)(?:_|$)/i;

function stableJson(value: unknown): string {
  if (Array.isArray(value)) {
    return `[${value.map((item) => stableJson(item)).join(",")}]`;
  }

  if (value && typeof value === "object") {
    const object = value as Record<string, unknown>;
    return `{${Object.keys(object)
      .sort()
      .map((key) => `${JSON.stringify(key)}:${stableJson(object[key])}`)
      .join(",")}}`;
  }

  return JSON.stringify(value);
}

function hashParameters(parameters: JsonObject): string {
  const normalized = { ...parameters };
  delete normalized.approvalToken;
  return createHash("sha256").update(stableJson(normalized)).digest("hex");
}

// Tools whose gate CIVIL3D_APPROVAL_MODE=disabled does not lift, and that the
// HTTP bridge refuses without MCP_HTTP_TOKEN. Client mode only applies to a
// direct MCP client call; internal and HTTP handlers still require a token.
export const ALWAYS_GATED_TOOLS: ReadonlySet<string> = new Set<string>(["civil3d_run_code"]);
export const CLIENT_APPROVABLE_TOOLS: ReadonlySet<string> = new Set<string>(["civil3d_run_code"]);

export function isApprovalRequired(target: ApprovalTarget): boolean {
  if (ALWAYS_GATED_TOOLS.has(target.toolName)) {
    return true;
  }

  if (process.env.CIVIL3D_APPROVAL_MODE === "disabled") {
    return false;
  }

  return hasApprovalRisk(target);
}

export function hasApprovalRisk(target: ApprovalTarget): boolean {
  const mutatesState = target.capabilities.some((capability) => MUTATING_CAPABILITIES.has(capability));
  return (
    target.capabilities.some((capability) => ["delete", "import", "export"].includes(capability)) ||
    EXPLICIT_APPROVAL_ACTION.test(target.action) ||
    (!target.safeForRetry && mutatesState)
  );
}

export async function getActiveDrawingFingerprint(): Promise<string> {
  const drawingInfo = await withApplicationConnection(async (client) => {
    // Ask the ungated health endpoint before touching drawing state. Older
    // plugin builds never answer getDrawingInfo when no document is open (the
    // request wedges the host execution gate), so surface NO_DRAWING here
    // instead of waiting out CIVIL3D_COMMAND_TIMEOUT.
    const health = await client.sendCommand("getCivil3DHealth", {});
    if (health && typeof health === "object" && (health as JsonObject).drawingLoaded === false) {
      throw new Civil3DRpcError("No active drawing is open in Civil 3D.", "CIVIL3D.NO_DRAWING", -32001);
    }
    return await client.sendCommand("getDrawingInfo", {});
  });
  return createHash("sha256").update(stableJson(drawingInfo)).digest("hex");
}

function isNoActiveDrawingError(error: unknown): boolean {
  const code = error && typeof error === "object" && "code" in error
    ? String(error.code)
    : "";
  const message = error instanceof Error ? error.message : String(error);
  return code === "CIVIL3D.NO_DRAWING" || code === "CIVIL3D.NO_ACTIVE_DRAWING" ||
    /no active (?:civil 3d )?drawing|no active (?:civil 3d )?document/i.test(message);
}

export class ApprovalPolicyService {
  private readonly grants = new Map<string, ApprovalGrant>();

  public constructor(
    private readonly drawingFingerprint: DrawingFingerprintProvider = getActiveDrawingFingerprint,
    private readonly now: () => number = () => Date.now(),
  ) {}

  private async fingerprintFor(target: ApprovalTarget): Promise<string> {
    try {
      return await this.drawingFingerprint();
    } catch (error) {
      if (target.requiresActiveDrawing === false && isNoActiveDrawingError(error)) {
        return NO_ACTIVE_DRAWING_FINGERPRINT;
      }
      throw error;
    }
  }

  public async requestApproval(
    target: ApprovalTarget,
    parameters: JsonObject,
    ttlMs = APPROVAL_TOKEN_TTL_MS,
  ): Promise<ApprovalReceipt> {
    if (!isApprovalRequired(target)) {
      throw new ApprovalValidationError(
        `'${target.toolName}' action '${target.action}' does not require approval. Execute it directly.`,
      );
    }

    const drawingFingerprint = await this.fingerprintFor(target);
    const expiresAt = this.now() + ttlMs;
    const approvalToken = randomUUID();
    this.grants.set(approvalToken, {
      toolName: target.toolName,
      action: target.action,
      parametersHash: hashParameters(parameters),
      drawingFingerprint,
      expiresAt,
    });

    return {
      approvalToken,
      expiresAt: new Date(expiresAt).toISOString(),
      drawingFingerprint,
    };
  }

  public async authorize(
    target: ApprovalTarget,
    parameters: JsonObject,
    options: { directClientCall?: boolean } = {},
  ): Promise<"not_required" | "token" | "client"> {
    if (!isApprovalRequired(target)) {
      return "not_required";
    }

    const approvalToken = typeof parameters.approvalToken === "string" ? parameters.approvalToken : undefined;
    if (!approvalToken) {
      if (options.directClientCall === true && CLIENT_APPROVABLE_TOOLS.has(target.toolName)
        && runCodeApprovalMode() === "client") return "client";
      throw new ApprovalRequiredError(
        `Approval required for '${target.toolName}' action '${target.action}'. ` +
        "Call civil3d_request_approval with the same toolName, action, and parameters, then retry with approvalToken." +
        (target.toolName === "civil3d_run_code" && runCodeApprovalMode() === "client"
          ? " In client mode, calls through civil3d_orchestrate, civil3d_docs or the HTTP bridge still need a token."
          : ""),
      );
    }

    const grant = this.grants.get(approvalToken);
    if (!grant) {
      throw new ApprovalValidationError("Approval token is missing, expired, or has already been used.");
    }

    this.grants.delete(approvalToken);

    if (grant.expiresAt <= this.now()) {
      throw new ApprovalValidationError("Approval token has expired. Request a new approval.");
    }

    if (
      grant.toolName !== target.toolName ||
      grant.action !== target.action ||
      grant.parametersHash !== hashParameters(parameters)
    ) {
      throw new ApprovalValidationError("Approval token does not match this tool action and its parameters.");
    }

    const activeDrawingFingerprint = await this.fingerprintFor(target);
    if (grant.drawingFingerprint !== activeDrawingFingerprint) {
      throw new ApprovalValidationError("The active drawing changed after approval. Request approval for the current drawing.");
    }
    return "token";
  }

  public async enforce(
    target: ApprovalTarget,
    parameters: JsonObject,
    options: { directClientCall?: boolean } = {},
  ): Promise<void> {
    await this.authorize(target, parameters, options);
  }
}

export const approvalPolicy = new ApprovalPolicyService();
