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
import { applyIdempotencyKey, isValidIdempotencyKey } from "./index.js";

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

test("applyIdempotencyKey: caller-supplied header with lowercase key still triggers override path", () => {
	// Fetch Headers is case-insensitive; the override check uses
	// headers.get("Idempotency-Key") which normalises and finds the
	// lowercase entry. This locks in the contract against future
	// refactors that might switch to a plain object lookup.
	const headers: Headers = new Headers({ "idempotency-key": "caller-pinned-lowercase" });
	applyIdempotencyKey(headers, "POST");
	assert.equal(headers.get("Idempotency-Key"), "caller-pinned-lowercase");
});

test("applyIdempotencyKey: rejects caller-supplied key with leading whitespace (validator vs undici)", () => {
	// Note: Headers.set() itself rejects values containing CRLF or NUL
	// at the platform level (undici enforces RFC 7230). The validator
	// covers the cases undici does not: length, whitespace, non-ASCII.
	const headers: Headers = new Headers();
	headers.set("Idempotency-Key", "has internal space");
	assert.throws(() => applyIdempotencyKey(headers, "POST"), /shape invalid/);
});

test("applyIdempotencyKey: rejects caller-supplied empty-string key", () => {
	const headers: Headers = new Headers();
	headers.set("Idempotency-Key", "");
	assert.throws(() => applyIdempotencyKey(headers, "POST"), /shape invalid/);
});

test("applyIdempotencyKey: rejects caller-supplied oversized (>255) key", () => {
	const headers: Headers = new Headers();
	headers.set("Idempotency-Key", "x".repeat(256));
	assert.throws(() => applyIdempotencyKey(headers, "POST"), /shape invalid/);
});

test("applyIdempotencyKey: accepts caller-supplied key at the 255-char boundary", () => {
	const headers: Headers = new Headers();
	const longKey: string = "x".repeat(255);
	headers.set("Idempotency-Key", longKey);
	applyIdempotencyKey(headers, "POST");
	assert.equal(headers.get("Idempotency-Key"), longKey);
});

test("isValidIdempotencyKey: shape matrix", () => {
	assert.equal(isValidIdempotencyKey("550e8400-e29b-41d4-a716-446655440000"), true);
	assert.equal(isValidIdempotencyKey("a"), true);
	assert.equal(isValidIdempotencyKey("x".repeat(255)), true);
	assert.equal(isValidIdempotencyKey(""), false);
	assert.equal(isValidIdempotencyKey("x".repeat(256)), false);
	assert.equal(isValidIdempotencyKey("has space"), false);
	assert.equal(isValidIdempotencyKey("has\ttab"), false);
	assert.equal(isValidIdempotencyKey("has\nnewline"), false);
	assert.equal(isValidIdempotencyKey("has\rcr"), false);
	assert.equal(isValidIdempotencyKey("\x7f"), false); // DEL
	assert.equal(isValidIdempotencyKey("\x00"), false); // NUL
	assert.equal(isValidIdempotencyKey("caf\u00e9"), false); // non-ASCII
});
