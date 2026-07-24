/**
 * Unit tests for resolveToken (issue #464).
 *
 * Runs via `node --test` (same harness as apiCall.test.ts). The env is
 * injected as a plain object, so no process.env mutation and no fetch
 * mocking is needed; the file cases use real temp files.
 *
 * Contract being asserted (mirrors the skill's _resolve_token in
 * .agents/skills/expertise-api/scripts/lib/common.sh):
 *   1. EXPERTISE_API_TOKEN set and non-empty wins over the file.
 *   2. EXPERTISE_API_TOKEN_FILE is read when the token is unset/empty,
 *      with trailing whitespace stripped.
 *   3. Missing/unreadable file throws naming the path (no silent
 *      fall-through to "not set").
 *   4. Empty file throws.
 *   5. Neither variable set throws naming both variables.
 */

import { test } from "node:test";
import assert from "node:assert/strict";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { resolveToken } from "./index.js";

const workDir: string = fs.mkdtempSync(path.join(os.tmpdir(), "resolve-token-"));
process.on("exit", () => {
	fs.rmSync(workDir, { recursive: true, force: true });
});

test("resolveToken: reads the token from EXPERTISE_API_TOKEN_FILE", () => {
	const tokenFile = path.join(workDir, "token1");
	fs.writeFileSync(tokenFile, "file-token-value\n");
	assert.equal(
		resolveToken({ EXPERTISE_API_TOKEN_FILE: tokenFile }),
		"file-token-value",
	);
});

test("resolveToken: explicit EXPERTISE_API_TOKEN beats the file", () => {
	const tokenFile = path.join(workDir, "token2");
	fs.writeFileSync(tokenFile, "file-token-value\n");
	assert.equal(
		resolveToken({
			EXPERTISE_API_TOKEN: "explicit-wins",
			EXPERTISE_API_TOKEN_FILE: tokenFile,
		}),
		"explicit-wins",
	);
});

test("resolveToken: whitespace-only EXPERTISE_API_TOKEN falls through to the file", () => {
	const tokenFile = path.join(workDir, "token3");
	fs.writeFileSync(tokenFile, "file-token-value\n");
	assert.equal(
		resolveToken({
			EXPERTISE_API_TOKEN: "   ",
			EXPERTISE_API_TOKEN_FILE: tokenFile,
		}),
		"file-token-value",
	);
});

test("resolveToken: missing file throws naming the path", () => {
	const missing = path.join(workDir, "does-not-exist");
	assert.throws(
		() => resolveToken({ EXPERTISE_API_TOKEN_FILE: missing }),
		new RegExp(`missing or unreadable file: .*does-not-exist`),
	);
});

test("resolveToken: empty file throws", () => {
	const emptyFile = path.join(workDir, "empty");
	fs.writeFileSync(emptyFile, "");
	assert.throws(
		() => resolveToken({ EXPERTISE_API_TOKEN_FILE: emptyFile }),
		/EXPERTISE_API_TOKEN_FILE is empty/,
	);
});

test("resolveToken: whitespace-only file content throws as empty", () => {
	const blankFile = path.join(workDir, "blank");
	fs.writeFileSync(blankFile, " \t\n");
	assert.throws(
		() => resolveToken({ EXPERTISE_API_TOKEN_FILE: blankFile }),
		/EXPERTISE_API_TOKEN_FILE is empty/,
	);
});

test("resolveToken: trailing whitespace stripped from file token", () => {
	const paddedFile = path.join(workDir, "padded");
	fs.writeFileSync(paddedFile, "padded-token \t\n\n");
	assert.equal(
		resolveToken({ EXPERTISE_API_TOKEN_FILE: paddedFile }),
		"padded-token",
	);
});

test("resolveToken: leading ~/ in the file path expands to the home directory", () => {
	// Parity with the shell skill, where secrets.env is sourced and
	// `VAR=~/path` tilde-expands. Uses a real file under os.homedir() so the
	// expansion is exercised end-to-end, then cleans it up.
	const relName = `.resolve-token-test-${process.pid}.jwt`;
	const realPath = path.join(os.homedir(), relName);
	fs.writeFileSync(realPath, "tilde-token\n");
	try {
		assert.equal(
			resolveToken({ EXPERTISE_API_TOKEN_FILE: `~/${relName}` }),
			"tilde-token",
		);
	} finally {
		fs.rmSync(realPath, { force: true });
	}
});

test("resolveToken: neither variable set throws naming both", () => {
	assert.throws(
		() => resolveToken({}),
		/EXPERTISE_API_TOKEN is not set \(set it, or point EXPERTISE_API_TOKEN_FILE/,
	);
});
