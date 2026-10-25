import { z, type ZodRawShape, type ZodTypeAny } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  approvalPolicy, ApprovalRequiredError, ApprovalValidationError,
  getActiveDrawingFingerprint, hasApprovalRisk,
} from "./approvalPolicy.js";
import { captureToolHandler, isLoggedApprovalRefusal, markLoggedApprovalRefusal } from "./toolHandlerRegistry.js";
import { maybeStoreReportResource } from "./reportResourceStore.js";
import { idempotencyStore } from "./idempotencyStore.js";
import { createLogger } from "../utils/logger.js";
import { createRequestId, runWithRequestId } from "../utils/requestContext.js";
import { withInstanceRequest, resolveInstanceEndpoint, activeEndpoint } from "../utils/instanceSession.js";
import type { ToolCapability, ToolCatalogEntry, ToolDomain } from "./toolMetadata.js";

type JsonObject = Record<string, unknown>;

interface ToolProgressExtra {
  _meta?: { progressToken?: string | number };
  signal?: AbortSignal;
  sendNotification?: (notification: {
    method: "notifications/progress";
    params: {
      progressToken: string | number;
      progress: number;
      total?: number;
      message?: string;
    };
  }) => Promise<void>;
}

const MUTATING_CAPABILITIES = new Set<ToolCapability>([
  "create",
  "edit",
  "delete",
  "manage",
  "generate",
  "register",
  "export",
  "import",
]);
const log = createLogger("DomainRuntime");

export interface DomainActionDefinition<TArgs = JsonObject> {
  action: string;
  inputSchema: z.ZodType<TArgs>;
  prepare?: (args: TArgs, rawArgs: JsonObject) => Promise<{ args: TArgs; approvalParameters: JsonObject }>;
  progressHeartbeat?: boolean;
  execute: (args: TArgs, context?: { approval?: "not_required" | "token" | "client" }) => Promise<unknown>;
  responseSchema?: ZodTypeAny;
  capabilities: ToolCapability[];
  requiresActiveDrawing: boolean;
  safeForRetry: boolean;
  pluginMethods?: string[];
}

export interface DomainToolExposure {
  toolName: string;
  displayName: string;
  description: string;
  inputShape: ZodRawShape;
  supportedActions: string[];
  resolveAction: (rawArgs: JsonObject) => { action: string; args: JsonObject };
  capabilities?: ToolCapability[];
  operations?: string[];
  pluginMethods?: string[];
  requiresActiveDrawing?: boolean;
  safeForRetry?: boolean;
  status?: "implemented" | "planned";
}

export interface DomainToolDefinition {
  domain: ToolDomain;
  actions: Record<string, DomainActionDefinition>;
  exposures: DomainToolExposure[];
}

function uniqueStrings(values: Iterable<string | undefined>): string[] | undefined {
  const unique = [...new Set([...values].filter((value): value is string => Boolean(value)))];
  return unique.length > 0 ? unique : undefined;
}

function uniqueCapabilities(values: Iterable<ToolCapability | undefined>): ToolCapability[] {
  return [...new Set([...values].filter((value): value is ToolCapability => Boolean(value)))];
}

function buildToolErrorResult(toolName: string, actionName: string | undefined, error: unknown) {
  const message = error instanceof Error ? error.message : String(error);
  const typedErrorCode = error && typeof error === "object" && "code" in error
    && typeof error.code === "string"
    ? error.code
    : undefined;
  const errorCode = typedErrorCode ?? inferDomainErrorCode(message);
  const typedRpcCode = error && typeof error === "object" && "rpcCode" in error
    && typeof error.rpcCode === "number"
    ? error.rpcCode
    : undefined;
  const rpcCode = typedRpcCode ?? inferRpcErrorCode(errorCode);
  const scopedName = actionName ? `${toolName} action '${actionName}'` : toolName;

  return {
    content: [
      {
        type: "text" as const,
        text: `${scopedName} failed: ${message}`,
      },
    ],
    isError: true,
    errorCode,
    rpcCode,
  };
}

function inferDomainErrorCode(message: string): string {
  if (/timed out|timeout/i.test(message)) return "CIVIL3D.TIMEOUT";
  if (/failed to connect|connection (?:failed|closed)|plugin not running/i.test(message)) return "CIVIL3D.UNAVAILABLE";
  if (/approval required|already exists|conflict/i.test(message)) return "CIVIL3D.CONFLICT";
  if (/not found|does not exist/i.test(message)) return "CIVIL3D.OBJECT_NOT_FOUND";
  if (/not registered|unknown tool/i.test(message)) return "CIVIL3D.METHOD_NOT_FOUND";
  if (/missing required|required fields?|invalid (?:input|parameter)|validation/i.test(message)) return "CIVIL3D.INVALID_INPUT";
  return "CIVIL3D.INTERNAL_ERROR";
}

function inferRpcErrorCode(domainCode: string): number {
  if (domainCode === "CIVIL3D.METHOD_NOT_FOUND") return -32601;
  if (domainCode === "CIVIL3D.INVALID_INPUT") return -32602;
  if (domainCode === "CIVIL3D.UNAVAILABLE") return -32001;
  if (domainCode === "CIVIL3D.OBJECT_NOT_FOUND") return -32004;
  if (domainCode === "CIVIL3D.TIMEOUT") return -32008;
  if (domainCode === "CIVIL3D.CONFLICT") return -32009;
  if (domainCode === "CIVIL3D.CANCELLED") return -32010;
  return -32000;
}

async function executeExposure(
  definition: DomainToolDefinition,
  exposure: DomainToolExposure,
  rawArgs: JsonObject,
  options: { directClientCall: boolean },
  progressExtra?: ToolProgressExtra,
) {
  const resolved = exposure.resolveAction(rawArgs);
  const actionName = resolved.action;

  if (!exposure.supportedActions.includes(actionName)) {
    throw new Error(
      `Unsupported action '${actionName}' for tool '${exposure.toolName}'. ` +
      `Supported actions: ${exposure.supportedActions.join(", ")}.`,
    );
  }

  const actionDefinition = definition.actions[actionName];
  if (!actionDefinition) {
    throw new Error(`Action '${actionName}' is not defined for domain '${definition.domain}'.`);
  }

  const parsedArgs = actionDefinition.inputSchema.parse(resolved.args);
  const prepared = actionDefinition.prepare
    ? await actionDefinition.prepare(parsedArgs, rawArgs)
    : { args: parsedArgs, approvalParameters: rawArgs };
  const idempotencyKey = typeof rawArgs.idempotencyKey === "string"
    ? rawArgs.idempotencyKey
    : undefined;
  if (idempotencyKey && !actionDefinition.safeForRetry) {
    const error = new Error(`Action '${actionName}' does not support idempotent retries.`) as Error & { code: string };
    error.code = "CIVIL3D.INVALID_INPUT";
    throw error;
  }

  const executeOnce = async () => {
    const approval = await approvalPolicy.authorize(
      {
        toolName: exposure.toolName,
        action: actionName,
        capabilities: actionDefinition.capabilities,
        safeForRetry: actionDefinition.safeForRetry,
        requiresActiveDrawing: actionDefinition.requiresActiveDrawing,
      },
      prepared.approvalParameters,
      options,
    );
    // Approval refusals log only their outcome, before any execution starts.
    log.info("Tool request started", { requestId: createRequestId(), tool: exposure.toolName });
    await emitProgress(
      progressExtra,
      50,
      100,
      `${exposure.displayName}: validated and executing '${actionName}'.`,
    );
    let heartbeatSeconds = 0;
    let heartbeatPending: Promise<void> = Promise.resolve();
    const heartbeat = actionDefinition.progressHeartbeat === true
      && progressExtra?._meta?.progressToken !== undefined
      && typeof progressExtra.sendNotification === "function"
      ? setInterval(() => {
        heartbeatSeconds += 15;
        const progress = 50 + 50 * heartbeatSeconds / (heartbeatSeconds + 60);
        heartbeatPending = heartbeatPending.then(() => emitProgress(
          progressExtra, progress, 100,
          `${exposure.displayName}: Civil 3D is still executing '${actionName}'.`,
        ));
      }, 15_000)
      : undefined;
    heartbeat?.unref();
    let response: unknown;
    try {
      response = await actionDefinition.execute(prepared.args, { approval });
    } finally {
      if (heartbeat) clearInterval(heartbeat);
      await heartbeatPending;
    }
    const validatedResponse = actionDefinition.responseSchema
      ? actionDefinition.responseSchema.parse(response)
      : response;
    const serializedResponse = JSON.stringify(validatedResponse, null, 2);
    const reportResource = maybeStoreReportResource(actionName, serializedResponse);

    return {
      content: [
        { type: "text" as const, text: serializedResponse },
        ...(reportResource ? [{
          type: "resource_link" as const,
          uri: reportResource.uri,
          name: reportResource.name,
          description: "Structured Civil 3D result retained for MCP resource retrieval.",
          mimeType: "application/json",
          size: reportResource.size,
        }] : []),
      ],
      structuredContent: { action: actionName, result: validatedResponse },
    };
  };

  if (!idempotencyKey) return executeOnce();
  const signature = { ...rawArgs };
  delete signature.approvalToken;
  delete signature.idempotencyKey;
  const drawingScope = actionDefinition.requiresActiveDrawing
    ? await getActiveDrawingFingerprint()
    : "drawing-independent";
  let endpoint = activeEndpoint();
  if (actionDefinition.pluginMethods?.length) {
    try { endpoint = await resolveInstanceEndpoint(); }
    catch (error) {
      // Health must still report its unattached status, without caching that status.
      if ((error as { code?: string }).code === "CIVIL3D.NOT_ATTACHED") return executeOnce();
      throw error;
    }
  }
  const instanceScope = endpoint
    ? JSON.stringify([endpoint.pid ?? null, endpoint.startedAtUtc ?? null, endpoint.port, endpoint.legacyFallback === true])
    : "local";
  return idempotencyStore.execute(
    `${exposure.toolName}:${actionName}:${instanceScope}:${drawingScope}`,
    idempotencyKey,
    signature,
    executeOnce,
  );
}

async function emitProgress(
  extra: ToolProgressExtra | undefined,
  progress: number,
  total: number,
  message: string,
): Promise<void> {
  const progressToken = extra?._meta?.progressToken;
  if (progressToken === undefined || typeof extra?.sendNotification !== "function") {
    return;
  }

  try {
    await extra.sendNotification({
      method: "notifications/progress",
      params: { progressToken, progress, total, message },
    });
  } catch (error) {
    console.warn(`Failed to send MCP progress notification for '${message}':`, error);
  }
}

export function buildExposureOutputSchema(
  definition: DomainToolDefinition,
  exposure: DomainToolExposure,
): ZodTypeAny {
  const actionNames = exposure.supportedActions;
  const actionSchema = actionNames.length > 0
    ? z.enum(actionNames as [string, ...string[]])
    : z.string();
  const responseSchemas = actionNames.map((actionName) =>
    definition.actions[actionName]?.responseSchema
  );
  // An action without a response schema can return anything, so the whole result is unknown. A union that
  // contains z.unknown() must not be built: the JSON Schema converter drops it, the advertised schema then lists
  // only "action" with additionalProperties false, and strict MCP clients (Hermes, the Python SDK) reject every
  // reply that carries "result".
  const definedSchemas = responseSchemas.filter((schema): schema is ZodTypeAny => Boolean(schema));
  const resultSchema = definedSchemas.length === 0 || definedSchemas.length < responseSchemas.length
    ? z.unknown()
    : definedSchemas.length === 1
      ? definedSchemas[0]
      : z.union(definedSchemas as unknown as [ZodTypeAny, ZodTypeAny, ...ZodTypeAny[]]);

  return z.object({ action: actionSchema, result: resultSchema });
}

export function buildExposureAnnotations(
  definition: DomainToolDefinition,
  exposure: DomainToolExposure,
) {
  const actions = exposure.supportedActions
    .map((actionName) => definition.actions[actionName])
    .filter((action): action is DomainActionDefinition => Boolean(action));
  const isReadOnly = actions.every((action) =>
    action.capabilities.every((capability) => !MUTATING_CAPABILITIES.has(capability))
  );
  const isDestructive = actions.some((action) => hasApprovalRisk({
    toolName: exposure.toolName,
    action: action.action,
    capabilities: action.capabilities,
    safeForRetry: action.safeForRetry,
  }));

  return {
    title: exposure.displayName,
    readOnlyHint: isReadOnly,
    destructiveHint: isDestructive,
    idempotentHint: actions.length > 0 && actions.every((action) => action.safeForRetry),
    openWorldHint: false,
  };
}

export function registerDomainTools(server: McpServer, definition: DomainToolDefinition) {
  registerSelectedDomainTools(server, definition, definition.exposures);
}

function createExposureHandler(
  definition: DomainToolDefinition,
  exposure: DomainToolExposure,
  options: { directClientCall: boolean },
) {
  return async (rawArgs: JsonObject, extra?: unknown) => {
    const progressExtra = extra && typeof extra === "object"
      ? extra as ToolProgressExtra
      : undefined;
    const requestId = createRequestId();
    const startedAt = performance.now();
    return withInstanceRequest(() => runWithRequestId(requestId, async () => {
      try {
        await emitProgress(progressExtra, 0, 100, `${exposure.displayName}: request received.`);
        const result = await executeExposure(definition, exposure, rawArgs, options, progressExtra);
        await emitProgress(progressExtra, 100, 100, `${exposure.displayName}: completed.`);
        log.info("Tool request completed", {
          requestId,
          tool: exposure.toolName,
          durationMs: Math.round(performance.now() - startedAt),
        });
        return { ...result, _meta: { requestId } };
      } catch (error) {
        const actionName =
          typeof rawArgs.action === "string"
            ? String(rawArgs.action)
            : exposure.supportedActions.length === 1
              ? exposure.supportedActions[0]
              : undefined;

        await emitProgress(progressExtra, 100, 100, `${exposure.displayName}: failed.`);
        const details = {
          requestId,
          tool: exposure.toolName,
          action: actionName,
          durationMs: Math.round(performance.now() - startedAt),
          error: error instanceof Error ? error.message : String(error),
        };
        const approvalRefusal = error instanceof ApprovalRequiredError || error instanceof ApprovalValidationError;
        const alreadyLogged = isLoggedApprovalRefusal(error);
        if (approvalRefusal && !alreadyLogged) {
          log.warn("Tool request refused by approval policy", details);
        } else if (!alreadyLogged) {
          log.error("Tool request failed", {
            ...details,
            stack: error instanceof Error ? error.stack : undefined,
          });
        }
        const result = { ...buildToolErrorResult(exposure.toolName, actionName, error), _meta: { requestId } };
        return approvalRefusal || alreadyLogged ? markLoggedApprovalRefusal(result) : result;
      }
    }, progressExtra?.signal));
  };
}

export function captureDomainToolHandlers(definition: DomainToolDefinition) {
  for (const exposure of definition.exposures) {
    captureToolHandler(exposure.toolName, createExposureHandler(definition, exposure, { directClientCall: false }));
  }
}

export function registerSelectedDomainTools(
  server: McpServer,
  definition: DomainToolDefinition,
  exposures: DomainToolExposure[],
) {
  for (const exposure of exposures) {
    server.registerTool(
      exposure.toolName,
      {
        title: exposure.displayName,
        description: exposure.description,
        inputSchema: {
          ...exposure.inputShape,
          approvalToken: z.string().min(1).optional(),
          idempotencyKey: z.string().min(1).max(128).optional(),
        },
        outputSchema: buildExposureOutputSchema(definition, exposure),
        annotations: buildExposureAnnotations(definition, exposure),
      },
      createExposureHandler(definition, exposure, { directClientCall: true }),
    );
  }
}

export function buildDomainToolCatalogEntries(definition: DomainToolDefinition): ToolCatalogEntry[] {
  return definition.exposures.map((exposure) => {
    const supportedActionDefinitions = exposure.supportedActions
      .map((actionName) => definition.actions[actionName])
      .filter((action): action is DomainActionDefinition => Boolean(action));

    return {
      toolName: exposure.toolName,
      displayName: exposure.displayName,
      description: exposure.description,
      domain: definition.domain,
      capabilities: exposure.capabilities ?? uniqueCapabilities(
        supportedActionDefinitions.flatMap((action) => action.capabilities),
      ),
      operations: exposure.operations ?? (exposure.supportedActions.length > 1
        ? exposure.supportedActions
        : undefined),
      pluginMethods: exposure.pluginMethods ?? uniqueStrings(
        supportedActionDefinitions.flatMap((action) => action.pluginMethods ?? []),
      ),
      requiresActiveDrawing: exposure.requiresActiveDrawing
        ?? supportedActionDefinitions.some((action) => action.requiresActiveDrawing),
      safeForRetry: exposure.safeForRetry
        ?? supportedActionDefinitions.every((action) => action.safeForRetry),
      status: exposure.status ?? "implemented",
    };
  });
}
