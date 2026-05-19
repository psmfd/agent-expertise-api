/**
 * Unit tests for applyIdempotencyKey (issue #206 / ADR-010).
 *
 * Runs via `node --test` (Node 20+ built-in runner). No new dependencies.
 * The transpilation happens via the loader configured in `npm test`.
 *
 * Contract being asserted:
 *   - On POST without a caller-supplied Idempotency-Key, inject a fresh
 *     UUID v4 from crypto.randomUUID().
 *   - On GET / PATCH / DELETE / HEAD / OPTIONS, do nothing.
 *   - On POST WITH a caller-supplied Idempotency-Key, leave it untouched
 *     (the override path that lets tool handlers pin a key across retries).
 *   - Method matching is case-insensitive (the API accepts "post" / "POST"
 *     identically; the helper normalises before comparison).
 */

import { test } from "node:test";
import assert from "node:assert/strict";
import { applyIdempotencyKey } from "./index.js";

const UUID_V4_PATTERN: RegExp =
	/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

test("applyIdempotencyKey: injects UUID on POST when header absent", () => {
	const headers: Headers = new Headers();
	applyIdempotencyKey(headers, "POST");
	const value: string | null = headers.get("Idempotency-Key");
	assert.ok(value !== null, "expected Idempotency-Key header to be set");
	assert.match(value as string, UUID_V4_PATTERN);
});

test("applyIdempotencyKey: case-insensitive method match (lowercase 'post')", () => {
	const headers: Headers = new Headers();
	applyIdempotencyKey(headers, "post");
	assert.ok(headers.has("Idempotency-Key"));
});

test("applyIdempotencyKey: does NOT inject on GET (explicit)", () => {
	const headers: Headers = new Headers();
	applyIdempotencyKey(headers, "GET");
	assert.equal(headers.get("Idempotency-Key"), null);
});

test("applyIdempotencyKey: does NOT inject when method undefined (defaults to GET)", () => {
	const headers: Headers = new Headers();
	applyIdempotencyKey(headers, undefined);
	assert.equal(headers.get("Idempotency-Key"), null);
});

test("applyIdempotencyKey: does NOT inject on PATCH (server filter is POST-only)", () => {
	const headers: Headers = new Headers();
	applyIdempotencyKey(headers, "PATCH");
	assert.equal(headers.get("Idempotency-Key"), null);
});

test("applyIdempotencyKey: does NOT inject on DELETE (server filter is POST-only)", () => {
	const headers: Headers = new Headers();
	applyIdempotencyKey(headers, "DELETE");
	assert.equal(headers.get("Idempotency-Key"), null);
});

test("applyIdempotencyKey: preserves caller-supplied Idempotency-Key on POST", () => {
	const headers: Headers = new Headers({ "Idempotency-Key": "caller-pinned-key-v1" });
	applyIdempotencyKey(headers, "POST");
	assert.equal(headers.get("Idempotency-Key"), "caller-pinned-key-v1");
});

test("applyIdempotencyKey: two POSTs produce different keys (proves we mint, not memoise)", () => {
	const h1: Headers = new Headers();
	const h2: Headers = new Headers();
	applyIdempotencyKey(h1, "POST");
	applyIdempotencyKey(h2, "POST");
	const k1: string | null = h1.get("Idempotency-Key");
	const k2: string | null = h2.get("Idempotency-Key");
	assert.ok(k1 !== null && k2 !== null);
	assert.notEqual(k1, k2, "expected two independent POSTs to mint distinct keys");
});
