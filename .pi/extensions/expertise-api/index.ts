/**
 * pi extension: expertise-api
 *
 * Exposes the agent-expertise-api as typed pi tools so users can search,
 * create, update, approve, reject, and delete expertise entries from
 * inside the pi coding agent without dropping to curl.
 *
 * Pairs with the action skill at .agents/skills/expertise-api/ (issue #147).
 * The skill is the curl-based fallback for harnesses without pi extensions;
 * this extension is the native pi path.
 *
 * Env contract (same as the skill):
 *   EXPERTISE_API_BASE_URL  Origin of the API (no trailing slash).
 *   EXPERTISE_API_TOKEN     Bearer token: OIDC JWT or LocalDev "dev:t:s+s".
 *
 * Auto-sources ~/.config/expertise-api/secrets.env (or
 * $EXPERTISE_API_SECRETS_FILE) on extension load.
 *
 * All tools target the live API; failures return a structured error
 * message rather than throwing, so the LLM can decide whether to retry,
 * surface to the user, or escalate.
 */

import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { StringEnum } from "@earendil-works/pi-ai";
import { Type } from "typebox";

// ---------------------------------------------------------------------------
// Secrets file: source EXPERTISE_API_BASE_URL / _TOKEN from a KEY=VALUE file
// if not already in process.env. Matches the skill's shell behavior.
// ---------------------------------------------------------------------------

function loadSecretsFile(): void {
	const secretsPath =
		process.env.EXPERTISE_API_SECRETS_FILE ??
		path.join(os.homedir(), ".config", "expertise-api", "secrets.env");
	if (!fs.existsSync(secretsPath)) return;
	let contents: string;
	try {
		contents = fs.readFileSync(secretsPath, "utf8");
	} catch {
		return;
	}
	for (const rawLine of contents.split(/\r?\n/)) {
		const line = rawLine.trim();
		if (line === "" || line.startsWith("#")) continue;
		const eq = line.indexOf("=");
		if (eq <= 0) continue;
		const key = line.slice(0, eq).trim();
		let value = line.slice(eq + 1).trim();
		// Strip a single surrounding pair of single or double quotes.
		if (
			(value.startsWith('"') && value.endsWith('"')) ||
			(value.startsWith("'") && value.endsWith("'"))
		) {
			value = value.slice(1, -1);
		}
		if (process.env[key] === undefined) {
			process.env[key] = value;
		}
	}
}

// ---------------------------------------------------------------------------
// HTTP helper: typed fetch with bearer auth, JSON parse, structured errors.
// ---------------------------------------------------------------------------

interface ApiCallResult {
	ok: boolean;
	status: number;
	body: unknown; // parsed JSON if Content-Type matches, otherwise raw string
	rawText?: string;
}

/**
 * Idempotency-Key shape validator. Mirrors the server-side
 * IdempotencyKeyValidator (IETF draft-ietf-httpapi-idempotency-key-
 * header-06 §2.2 / ADR-010): 1–255 chars, printable ASCII (0x21–0x7E),
 * no whitespace, no control characters. Returns true if the key is
 * shape-valid; false otherwise.
 *
 * Defense-in-depth for the `init.headers['Idempotency-Key']` override
 * path: no current call site supplies caller-controlled keys, but if a
 * future tool handler does, a CRLF-bearing or oversized value should
 * fail fast client-side rather than reach the server (which would 400).
 */
const IDEMPOTENCY_KEY_PATTERN: RegExp = /^[\x21-\x7E]{1,255}$/;
export function isValidIdempotencyKey(key: string): boolean {
	return IDEMPOTENCY_KEY_PATTERN.test(key);
}

/**
 * Inject an Idempotency-Key header on POST writes per ADR-010 (issue #206).
 *
 * Scoped to POST to match the server-side IdempotencyEndpointFilter
 * (attached only to /expertise, /expertise/{id}/approve, and
 * /expertise/{id}/reject). A caller-supplied 'Idempotency-Key' is
 * preserved verbatim so tool handlers that own a retry loop can pin one
 * key across attempts and hit the server-side replay cache instead of
 * double-writing. Caller-supplied keys are shape-validated; an invalid
 * value throws synchronously (apiCall wraps the call site so this
 * surfaces as a typed error to the tool result, not an unhandled
 * rejection).
 *
 * Exported (vs inlined into apiCall) so unit tests can assert the
 * contract without mocking fetch.
 */
export function applyIdempotencyKey(
	headers: Headers,
	method: string | undefined,
): void {
	const upper: string = (method ?? "GET").toUpperCase();
	if (upper !== "POST") {
		return;
	}
	const existing: string | null = headers.get("Idempotency-Key");
	if (existing !== null) {
		if (!isValidIdempotencyKey(existing)) {
			throw new Error(
				"Idempotency-Key shape invalid: must be 1-255 printable ASCII characters (0x21-0x7E), no whitespace or control characters.",
			);
		}
		return;
	}
	headers.set("Idempotency-Key", crypto.randomUUID());
}

function getBaseUrl(): string {
	const raw = process.env.EXPERTISE_API_BASE_URL;
	if (!raw || raw.trim() === "") {
		throw new Error(
			"EXPERTISE_API_BASE_URL is not set. Export it or write it to ~/.config/expertise-api/secrets.env.",
		);
	}
	const normalised = raw.replace(/\/+$/, "");
	// Refuse to send the bearer over cleartext unless explicitly pointed at a
	// loopback host (developer mode). Closes the misconfiguration footgun
	// flagged by security-review on PR #179: a base URL of 'http://...' would
	// otherwise leak the token in cleartext on the wire.
	let parsed: URL;
	try {
		parsed = new URL(normalised);
	} catch {
		throw new Error(`EXPERTISE_API_BASE_URL is not a valid URL: ${raw}`);
	}
	const isLoopback =
		parsed.hostname === "localhost" ||
		parsed.hostname === "127.0.0.1" ||
		parsed.hostname === "[::1]";
	if (parsed.protocol !== "https:" && !isLoopback) {
		throw new Error(
			`EXPERTISE_API_BASE_URL must use https:// (or a loopback host for dev). Got: ${raw}`,
		);
	}
	return normalised;
}

function getToken(): string {
	const tok = process.env.EXPERTISE_API_TOKEN;
	if (!tok || tok.trim() === "") {
		throw new Error(
			"EXPERTISE_API_TOKEN is not set. Export it or write it to ~/.config/expertise-api/secrets.env.",
		);
	}
	return tok;
}

async function apiCall(
	pathAndQuery: string,
	init: RequestInit = {},
	signal?: AbortSignal,
): Promise<ApiCallResult> {
	const base: string = getBaseUrl();
	const token: string = getToken();
	const url: string = `${base}${pathAndQuery}`;
	const headers: Headers = new Headers(init.headers ?? {});
	headers.set("Authorization", `Bearer ${token}`);
	headers.set("Accept", "application/json");
	if (init.body !== undefined && !headers.has("Content-Type")) {
		headers.set("Content-Type", "application/json");
	}

	// Idempotency-Key on POST writes (ADR-010, issue #206). Scoped to POST
	// to match the server-side IdempotencyEndpointFilter, which is
	// attached only to /expertise, /expertise/{id}/approve, and
	// /expertise/{id}/reject. Caller-supplied header (via init.headers)
	// is preserved so tool handlers that own a retry loop can pin one key
	// across attempts and benefit from server-side replay.
	applyIdempotencyKey(headers, init.method);

	const response: Response = await fetch(url, { ...init, headers, signal });
	const rawText = await response.text();
	let body: unknown = rawText;
	const ct = response.headers.get("Content-Type") ?? "";
	if (ct.includes("application/json") && rawText.length > 0) {
		try {
			body = JSON.parse(rawText);
		} catch {
			// Leave body as the raw text so the caller can surface it verbatim.
		}
	}
	return {
		ok: response.ok,
		status: response.status,
		body,
		rawText,
	};
}

/**
 * Standard tool-return helper. Renders successful responses as pretty JSON
 * (LLM-friendly) and failed responses as a structured error block including
 * the HTTP status + response body so the model can reason about what to do.
 */
function toolResult(result: ApiCallResult, op: string) {
	if (result.ok) {
		return {
			content: [
				{
					type: "text" as const,
					text:
						typeof result.body === "string"
							? result.body
							: JSON.stringify(result.body, null, 2),
				},
			],
			details: { status: result.status },
		};
	}
	const bodyText =
		typeof result.body === "string"
			? result.body
			: JSON.stringify(result.body, null, 2);
	return {
		content: [
			{
				type: "text" as const,
				text: `error: ${op} returned HTTP ${result.status}\n${bodyText}`,
			},
		],
		isError: true,
		details: { status: result.status },
	};
}

function errorResult(op: string, err: unknown) {
	const msg = err instanceof Error ? err.message : String(err);
	return {
		content: [
			{
				type: "text" as const,
				text: `error: ${op}: ${msg}`,
			},
		],
		isError: true,
		details: {},
	};
}

function buildQuery(params: Record<string, string | number | boolean | undefined>): string {
	const qs = new URLSearchParams();
	for (const [k, v] of Object.entries(params)) {
		if (v === undefined || v === null || v === "") continue;
		qs.set(k, String(v));
	}
	const s = qs.toString();
	return s === "" ? "" : `?${s}`;
}

// ---------------------------------------------------------------------------
// Schemas — mirror the OpenAPI document hand-written for v1; codegen
// from openapi.json is a follow-up per #148 acceptance criteria.
// ---------------------------------------------------------------------------

const EntryTypeEnum = StringEnum([
	"IssueFix",
	"Caveat",
	"Requirement",
	"Pattern",
] as const);

const SeverityEnum = StringEnum(["Info", "Warning", "Critical"] as const);

const VisibilityEnum = StringEnum(["Private", "Shared"] as const);

const CreateEntryParams = Type.Object({
	domain: Type.String({
		description: "Logical grouping (e.g. 'shared', 'azure-devops', 'iac').",
	}),
	title: Type.String({ minLength: 1, maxLength: 200 }),
	body: Type.String({
		minLength: 1,
		description: "Markdown body. Embedding is generated from title+body.",
	}),
	entryType: EntryTypeEnum,
	severity: SeverityEnum,
	source: Type.String({
		description:
			"Self-reported origin (informational only post-rebuild). E.g. 'agent-session-2026-05'.",
	}),
	sourceVersion: Type.Optional(
		Type.String({
			description:
				"Staleness signal — e.g. 'PgBouncer 1.21.0', 'EF Core 10.0.1'.",
		}),
	),
	tags: Type.Optional(
		Type.Array(Type.String(), {
			description: "Free-form tags (text[] with GIN index).",
		}),
	),
	tenant: Type.Optional(
		Type.String({
			description:
				"Optional tenant override. Only 'shared' is accepted; all other tenants are server-assigned from the caller's token. Requires expertise.write.approve and bypasses the draft queue (created Approved, not Draft).",
		}),
	),
});

const UpdateEntryParams = Type.Object({
	id: Type.String({ description: "Entry id (GUID)." }),
	// PATCH semantics — every field optional.
	domain: Type.Optional(Type.String()),
	title: Type.Optional(Type.String({ minLength: 1, maxLength: 200 })),
	body: Type.Optional(Type.String({ minLength: 1 })),
	entryType: Type.Optional(EntryTypeEnum),
	severity: Type.Optional(SeverityEnum),
	source: Type.Optional(Type.String()),
	sourceVersion: Type.Optional(Type.String()),
	tags: Type.Optional(Type.Array(Type.String())),
});

// ---------------------------------------------------------------------------
// Extension entry point.
// ---------------------------------------------------------------------------

export default function (pi: ExtensionAPI): void {
	loadSecretsFile();

	// --- expertise_search -------------------------------------------------
	pi.registerTool({
		name: "expertise_search",
		label: "Expertise Search",
		description:
			"Search expertise entries. If `q` is provided, runs a keyword full-text search via /expertise/search?q=. Otherwise lists entries filtered by domain / tags / entryType / severity via /expertise. Returns up to the server's default page size.",
		promptSnippet:
			"Search the expertise corpus by keyword or filter (domain/tags/type/severity).",
		promptGuidelines: [
			"Use expertise_search before solving any non-trivial problem to check for prior tribal knowledge — it is cheap and often saves rediscovery.",
			"Prefer expertise_search_hybrid as the default; use expertise_search when you specifically want keyword-only matching or filter-only listing.",
		],
		parameters: Type.Object({
			q: Type.Optional(
				Type.String({
					description:
						"Full-text query. If present, /expertise/search is used and other filters are ignored.",
				}),
			),
			domain: Type.Optional(Type.String()),
			tags: Type.Optional(
				Type.String({
					description: "Comma-separated tag list (any-match).",
				}),
			),
			entryType: Type.Optional(EntryTypeEnum),
			severity: Type.Optional(SeverityEnum),
			includeDeprecated: Type.Optional(Type.Boolean()),
		}),
		async execute(_id, params, signal) {
			try {
				const qs = buildQuery({
					q: params.q,
					domain: params.domain,
					tags: params.tags,
					entryType: params.entryType,
					severity: params.severity,
					includeDeprecated: params.includeDeprecated,
				});
				const route = params.q ? "/expertise/search" : "/expertise";
				const result = await apiCall(`${route}${qs}`, { method: "GET" }, signal);
				return toolResult(result, "expertise_search");
			} catch (err) {
				return errorResult("expertise_search", err);
			}
		},
	});

	// --- expertise_search_semantic ---------------------------------------
	pi.registerTool({
		name: "expertise_search_semantic",
		label: "Expertise Search (Semantic)",
		description:
			"Semantic vector search via /expertise/search/semantic?q=. The server embeds the query in-process (bge-micro-v2, 384-dim) and ranks by cosine similarity. Use for conceptual or paraphrased queries.",
		promptSnippet:
			"Semantic (vector) search over expertise — use for conceptual queries.",
		promptGuidelines: [
			"Use expertise_search_semantic when the user's query is a concept or paraphrase rather than likely-exact keywords.",
		],
		parameters: Type.Object({
			q: Type.String({
				minLength: 1,
				description: "Free-text query — server embeds and ranks by similarity.",
			}),
			limit: Type.Optional(
				Type.Integer({
					minimum: 1,
					maximum: 100,
					description: "Max results (server default 10).",
				}),
			),
			includeDeprecated: Type.Optional(Type.Boolean()),
		}),
		async execute(_id, params, signal) {
			try {
				const qs = buildQuery({
					q: params.q,
					limit: params.limit,
					includeDeprecated: params.includeDeprecated,
				});
				const result = await apiCall(
					`/expertise/search/semantic${qs}`,
					{ method: "GET" },
					signal,
				);
				return toolResult(result, "expertise_search_semantic");
			} catch (err) {
				return errorResult("expertise_search_semantic", err);
			}
		},
	});

	// --- expertise_search_hybrid ------------------------------------------
	pi.registerTool({
		name: "expertise_search_hybrid",
		label: "Expertise Search (Hybrid)",
		description:
			"Hybrid search via /expertise/search/hybrid?q=. Keyword and semantic arms are fused server-side with Reciprocal Rank Fusion (ADR-016), so it covers exact identifiers AND paraphrases in one call. Recommended default search.",
		promptSnippet:
			"Hybrid keyword+semantic search over expertise — the recommended default.",
		promptGuidelines: [
			"Prefer expertise_search_hybrid as the default search — it covers both exact identifiers and conceptual paraphrases; fall back to the keyword or semantic tools only when you specifically want one arm's behavior.",
		],
		parameters: Type.Object({
			q: Type.String({
				minLength: 1,
				description: "Free-text query — identifiers and paraphrases both work.",
			}),
			domain: Type.Optional(Type.String()),
			tags: Type.Optional(
				Type.String({
					description: "Comma-separated tags; every tag must match.",
				}),
			),
			entryType: Type.Optional(
				Type.Union([
					Type.Literal("IssueFix"),
					Type.Literal("Caveat"),
					Type.Literal("Requirement"),
					Type.Literal("Pattern"),
				]),
			),
			severity: Type.Optional(
				Type.Union([
					Type.Literal("Info"),
					Type.Literal("Warning"),
					Type.Literal("Critical"),
				]),
			),
			limit: Type.Optional(
				Type.Integer({
					minimum: 1,
					maximum: 100,
					description: "Max results (server default 10).",
				}),
			),
			includeDeprecated: Type.Optional(Type.Boolean()),
		}),
		async execute(_id, params, signal) {
			try {
				const qs = buildQuery({
					q: params.q,
					domain: params.domain,
					tags: params.tags,
					entryType: params.entryType,
					severity: params.severity,
					limit: params.limit,
					includeDeprecated: params.includeDeprecated,
				});
				const result = await apiCall(
					`/expertise/search/hybrid${qs}`,
					{ method: "GET" },
					signal,
				);
				return toolResult(result, "expertise_search_hybrid");
			} catch (err) {
				return errorResult("expertise_search_hybrid", err);
			}
		},
	});

	// --- expertise_get ----------------------------------------------------
	pi.registerTool({
		name: "expertise_get",
		label: "Expertise Get",
		description:
			"Fetch a single expertise entry by id via /expertise/{id}. Returns 404 if the entry does not exist OR is in another tenant (cross-tenant existence is hidden).",
		promptSnippet: "Fetch one expertise entry by id.",
		parameters: Type.Object({
			id: Type.String({ description: "Entry id (GUID)." }),
		}),
		async execute(_id, params, signal) {
			try {
				const enc = encodeURIComponent(params.id);
				const result = await apiCall(`/expertise/${enc}`, { method: "GET" }, signal);
				return toolResult(result, "expertise_get");
			} catch (err) {
				return errorResult("expertise_get", err);
			}
		},
	});

	// --- expertise_create -------------------------------------------------
	pi.registerTool({
		name: "expertise_create",
		label: "Expertise Create",
		description:
			"Create a Draft expertise entry via POST /expertise. The server generates an embedding from title+body and may return 409 if a near-duplicate exists in the caller's tenant. Requires expertise.write.draft.",
		promptSnippet:
			"Create a draft expertise entry. Caller must have expertise.write.draft.",
		promptGuidelines: [
			"Use expertise_create after solving a non-obvious problem so the next agent can find the answer. Title should be a short imperative (e.g. 'PgBouncer transaction mode breaks advisory locks').",
		],
		parameters: CreateEntryParams,
		async execute(_id, params, signal) {
			try {
				const result = await apiCall(
					"/expertise",
					{ method: "POST", body: JSON.stringify(params) },
					signal,
				);
				return toolResult(result, "expertise_create");
			} catch (err) {
				return errorResult("expertise_create", err);
			}
		},
	});

	// --- expertise_update -------------------------------------------------
	pi.registerTool({
		name: "expertise_update",
		label: "Expertise Update",
		description:
			"Update an entry via PATCH /expertise/{id}. PATCH semantics — only provided fields are applied. If title or body change the embedding is regenerated. A write.draft-only caller editing an Approved/Rejected entry resets it to Draft (ADR-003).",
		promptSnippet: "PATCH an expertise entry. Only provided fields are applied.",
		parameters: UpdateEntryParams,
		async execute(_id, params, signal) {
			try {
				const { id, ...patchBody } = params;
				const enc = encodeURIComponent(id);
				const result = await apiCall(
					`/expertise/${enc}`,
					{ method: "PATCH", body: JSON.stringify(patchBody) },
					signal,
				);
				return toolResult(result, "expertise_update");
			} catch (err) {
				return errorResult("expertise_update", err);
			}
		},
	});

	// --- expertise_approve -----------------------------------------------
	pi.registerTool({
		name: "expertise_approve",
		label: "Expertise Approve",
		description:
			"Transition a Draft entry to Approved via POST /expertise/{id}/approve. Requires expertise.write.approve. Optional `visibility` ('Private' default | 'Shared') is forwarded in the request body to choose post-approval visibility. Returns 409 if the entry is not in Draft state (state machine).",
		promptSnippet:
			"Approve a draft expertise entry. Requires expertise.write.approve.",
		parameters: Type.Object({
			id: Type.String({ description: "Entry id (GUID)." }),
			visibility: Type.Optional(VisibilityEnum),
		}),
		async execute(_id, params, signal) {
			try {
				const enc = encodeURIComponent(params.id);
				const init: RequestInit = { method: "POST" };
				if (params.visibility !== undefined) {
					init.body = JSON.stringify({ visibility: params.visibility });
				}
				const result = await apiCall(
					`/expertise/${enc}/approve`,
					init,
					signal,
				);
				return toolResult(result, "expertise_approve");
			} catch (err) {
				return errorResult("expertise_approve", err);
			}
		},
	});

	// --- expertise_reject -------------------------------------------------
	pi.registerTool({
		name: "expertise_reject",
		label: "Expertise Reject",
		description:
			"Transition a Draft entry to Rejected via POST /expertise/{id}/reject. Requires a non-empty rejectionReason (1-2000 chars) and the expertise.write.approve scope. Returns 409 if not Draft.",
		promptSnippet:
			"Reject a draft expertise entry with a reason. Requires expertise.write.approve.",
		parameters: Type.Object({
			id: Type.String({ description: "Entry id (GUID)." }),
			rejectionReason: Type.String({
				minLength: 1,
				maxLength: 2000,
				description: "Why the entry was rejected. 1-2000 chars.",
			}),
		}),
		async execute(_id, params, signal) {
			try {
				const enc = encodeURIComponent(params.id);
				const result = await apiCall(
					`/expertise/${enc}/reject`,
					{
						method: "POST",
						body: JSON.stringify({ rejectionReason: params.rejectionReason }),
					},
					signal,
				);
				return toolResult(result, "expertise_reject");
			} catch (err) {
				return errorResult("expertise_reject", err);
			}
		},
	});

	// --- expertise_delete -------------------------------------------------
	pi.registerTool({
		name: "expertise_delete",
		label: "Expertise Delete",
		description:
			"Soft-delete an entry via DELETE /expertise/{id} (sets DeprecatedAt). Shared-tenant entries require expertise.write.approve; otherwise expertise.write.draft is sufficient for own-tenant entries.",
		promptSnippet:
			"Soft-delete an expertise entry. Sets DeprecatedAt; can be undone server-side.",
		parameters: Type.Object({
			id: Type.String({ description: "Entry id (GUID)." }),
		}),
		async execute(_id, params, signal) {
			try {
				const enc = encodeURIComponent(params.id);
				const result = await apiCall(
					`/expertise/${enc}`,
					{ method: "DELETE" },
					signal,
				);
				return toolResult(result, "expertise_delete");
			} catch (err) {
				return errorResult("expertise_delete", err);
			}
		},
	});
}
