# 017 — Embedding model swap: jina-embeddings-v2-small-en at a 6144-token ceiling

## Status

Accepted (2026-07-23)

## Context and Problem Statement

The API embeds entries with bge-micro-v2 (384-dim, 512-token architectural window)
via `Microsoft.SemanticKernel.Connectors.Onnx`. The connector silently truncates
input beyond `MaximumTokens` (#429), which forced hard write caps of
`MaxBodyLength = 1500` / `MaxTitleLength = 200` characters. Those caps are the
binding constraint on entry authoring: real agent-generated fix/caveat entries
routinely want more than 1500 characters of body. Issue #437 asked whether a
long-context embedding model could raise the ceiling — and by how much —
without changing the embedding architecture (in-process ONNX, WordPiece
vocab.txt, the existing SK connector).

## Decision Drivers

- Must run **unmodified** through the existing connector: WordPiece `vocab.txt`
  tokenizer, ONNX graph accepting `{input_ids, attention_mask, token_type_ids}`,
  pooling limited to the connector's Mean/Max/MeanSqrt (no CLS).
- Retrieval quality on the real corpus must not regress (golden-set harness,
  #432) and should improve on long documents (needle-in-document test).
- A2 single-host deployment: embedding is in-process; peak RSS during a
  ceiling-filling embed is a real host constraint (O(n²) attention memory).
- The ceiling should be a **compile-time constant**, chosen once from ground
  truth, not a tunable that invites drift.

## Considered Options

### Model

1. **jina-embeddings-v2-small-en** (33M params, 512-dim, ALiBi positions to
   8192, mean pooling, no query prefixes) — **chosen**
2. nomic-embed-text-v1.5 (137M, 768-dim, rotary, 4-way task prefixes)
3. gte-base-en-v1.5 (rejected: ONNX graph lacks `token_type_ids`; CLS pooling —
   connector hardcodes both)
4. granite-embedding-r2 (rejected: BPE tokenizer; connector is WordPiece-only)
5. Keep bge-micro-v2 (status quo)

### Ceiling (`MaximumTokens`)

4096 vs 6144 vs 8192, measured empirically (all tables in the #437 issue
comments, 2026-07-23).

## Decision Outcome

**jina-embeddings-v2-small-en, `MaximumTokens = 6144`, derived caps
`MaxBodyLength = 16000` / `MaxTitleLength = 200`.**

### Why jina-v2-small over nomic (the a-priori favorite)

Empirical, on this repo's corpus and harnesses:

- Golden set (30 queries): jina semantic recall@10 **1.000** / MRR **0.925** vs
  bge 1.000/0.894 — no regression, slight MRR gain.
- Needle-in-document (8 docs, needles at 400–8000 chars): jina avg rank **1.5**
  (5/8 rank-1) vs nomic 3.0 and truncated bge 3.38. Nomic runs safely at a true
  8k window (verified 8190 encoded tokens, no degenerate vectors) but its
  needle precision was no better than truncated bge — long window without
  long-window retrieval value.
- jina runs through the connector unmodified (model.onnx + vocab.txt +
  `MaximumTokens`); mean pooling is the connector default. The root
  `model.onnx` (token-level outputs, 129,809,014 bytes) is required — the
  sibling `model-w-mean-pooling.onnx` bakes pooling into the graph and would
  be pooled twice by the connector.
- FP32 (~130 MB) vs bge's quantized 17.4 MB: accepted. The spike measured
  quality and cost on this exact artifact; a quantized variant would be an
  unverified substitution.

**Liveliness:** Maintenance-only (upstream focus moved to jina-v3+), single
vendor. Risk Medium, accepted: the artifact is pinned by SHA-256, fully
self-hosted, and the fallback path (ML.Tokenizers + OnnxRuntime behind the
existing `IEmbeddingGenerator` seam, ~120–190 LoC) is documented in #437.

### Why 6144 — the ground-truth ceiling

Measured on the live corpus (n=692 entries) and a token-calibrated needle
matrix (#437 second findings comment):

| Ceiling | Corpus coverage | Ceiling-filling embed cost | In-window needle precision |
| --- | --- | --- | --- |
| 4096 | 98.7% — **below p99 (4,347 tokens)** | 575 ms / 5.4 GB RSS | loses 6k/7.5k depths |
| **6144** | **99.71% — the plateau** | **1.16 s / 12.1 GB RSS** | **matches 8192** |
| 8192 | 99.71% — identical to 6144 | 1.86 s / 19.9 GB RSS | one measured mean-pooling dilution regression |

8192 buys zero coverage (the only two entries beyond 6144 tokens exceed 8192
too), costs 65% more peak RSS, and measurably *hurt* one always-in-window
needle via mean-pooling dilution. 4096 sits below the corpus p99. 6144 is the
smallest ceiling that captures everything capturable — a durable constant, not
a tunable.

### Derived caps

Same method as #429, re-based on the 6144 window: reserve ~70 worst-case tokens
for a 200-char title plus specials; body budget ≈ 6,000 tokens; at the measured
worst-case density of 2.97 chars/token, `MaxBodyLength = 16000` chars lands at
≈ 5,390 worst-case body tokens — comfortable headroom inside the window.
`MaxTitleLength` stays 200 (#436 rationale unchanged).

### Migration (two-phase, forward-only)

1. **One atomic release** (this ADR's PR set): EF migration — drop the HNSW
   index, `UPDATE ... SET "Embedding" = NULL` (pgvector's typmod cast rejects
   `ALTER COLUMN TYPE` over live 384-dim values), retype to `vector(512)`,
   recreate the HNSW index (NULLs are not indexed; it fills incrementally as
   embeddings return) — plus model files, `MaximumTokens`, cap raise, and
   removal of the bge query-instruction prefix (jina uses none; PR #431's
   prefix was bge-specific).
2. **Operator reembed** (`expertise-apictl reembed` / `dotnet run -- reembed`)
   immediately after install. During the window, every read path already
   filters `Embedding != null`: keyword search is unaffected; semantic/hybrid
   gracefully surface only reembedded rows; embed-on-write uses the new model
   from the first request. Dedup can only under-fire (miss), never misfire.
   Measured wall-clock for ~700 entries: single-digit minutes (≤15 min bound);
   the host needs RSS headroom for the ~12 GB p99 transient.

**Rollback is NOT `install.sh`'s binary swap.** Once the migration commits,
embeddings are nulled and the column is `vector(512)`; the previous binary's EF
model does not match. Recovery from a bad rollout is roll-forward (fix and
reembed — embeddings are regenerable, content is the source of truth) or full
`backup`/`restore` from a pre-upgrade backup. Take a backup before upgrading.

### Consequences

- `Deduplication:SemanticThreshold = 0.10` was calibrated to bge's distance
  geometry; it is re-derived against the jina space after the production
  reembed (#457). Until then dedup under-fires only. *(Resolved — see
  Amendment 1 below: retuned to 0.05 on 2026-07-24.)*
- The eval gates (`RetrievalEvalTests`, window-aware `NeedleEvalTests`, #437
  PR-B) are the merge gate for this and any future retrieval change; they are
  `EXPERTISE_EVAL=1` opt-in, so running them is a release-checklist step, not
  CI-automatic.
- CI model downloads grow ~17 MB → ~130 MB; mitigated by the `actions/cache`
  keyed on `download-models.sh` (added with #456's fix).
- Helm/k8s resource sizing is NOT covered — tracked in #458; A2 native service
  remains the hosting model of record.

## Amendment 1 (2026-07-24): `Deduplication:SemanticThreshold` retuned 0.10 → 0.05 (#457)

The Consequences section deferred the dedup-threshold retune to #457, gated on
the production reembed. Measured on the live corpus (604 approved embedded
entries, within-domain nearest-neighbor cosine distances over the stored
jina-v2-small vectors, pgvector `<=>`):

- **True duplicates cluster at ≈ 0** — 131 entries with NN distance < 0.01,
  130/131 with byte-identical bodies (accumulated while dedup under-fired,
  e.g. during the swap's NULL-embedding window).
- **Hand-labeled near-dups (retitles/light rewordings of the same fact)
  extend to ≤ 0.048**; the valley 0.01–0.05 holds only 6 entries, all labeled
  near-dup.
- **The closest genuinely distinct same-domain neighbors begin at ≈ 0.051**,
  with the distinct mass at 0.06–0.13 peaking around 0.10–0.13. The bge-era
  0.10 default sits INSIDE that mass: 272/604 entries (45%) have a legitimate
  distinct neighbor within 0.10, i.e. under jina geometry 0.10 would false-409
  roughly half of comparable future submissions.
- Synthetic pair measurement with the real model (`DedupThresholdEvalTests`)
  agrees: light rewordings 0.016–0.048, moderate rewordings 0.052–0.065, full
  paraphrases 0.13–0.16, distinct same-topic pairs ≥ 0.12.

**Decision: 0.05** — the valley midpoint. Biased low deliberately: a false 409
rejects a legitimate write (the costly, user-visible failure); a missed
near-dup lands as a Draft in the curator review queue (noise). Moderate and
full rewordings of the same fact are accepted under-fire — they are
geometrically inseparable from distinct entries under this model. Changes to
the threshold or model must re-run `EXPERTISE_EVAL=1
dotnet test --filter DedupThresholdEval` and re-derive against the live corpus
per the method above (recorded in #457).

## Related

- #437 (spike + ground-truth sweeps — both findings comments), #429 (silent
  truncation + original cap derivation), #455/#456 (prerequisites), #457
  (threshold retune), #458 (Helm sizing), #345 (reembed vintage detection)
- ADR-016 (hybrid RRF search — the eval-gated precedent)
- PR #431 (bge query prefix — reverted by this swap for the new model)
