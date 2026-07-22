# Retrieval Improvements Assessment — 2026-07-22

**Status:** Assessment only — no decisions made, nothing implemented. Each adopted
recommendation requires its own plan and (where it changes retrieval architecture)
an ADR before implementation.

**Question addressed:** Given what this API is used for — AI agents storing and
retrieving a running log of fixes, caveats, patterns, and requirements at task
time — are there better or more flexible retrieval approaches than, or
complementary to, the current vector search?

**Method:** Three-perspective parallel research (repo design review grounded in
the actual code, .NET/PostgreSQL stack implementability analysis, and a survey of
the 2025–2026 retrieval / agent-memory literature), synthesized 2026-07-22.

## Context that shaped every judgment

- Corpus: hundreds to low thousands of short, curated, single-topic entries.
- Callers are themselves LLM agents issuing short technical queries (error
  codes, flag names, API names) — a query profile where pure dense retrieval is
  weakest and where the caller can populate structured filters directly.
- Rich structured metadata already exists (`Domain`, `Tags`, `EntryType`,
  `Severity`, `Tenant`, `ReviewState`, `DeprecatedAt`).
- Constraint: no cloud AI on the retrieval path; single Postgres backend
  (design decision recorded in DESIGN.md); minimal operational surface
  (consistent with the ADR-007 posture).

## Ranked recommendations

### 1. Fix the missing BGE query-instruction prefix (likely live recall bug)

`EmbeddingService` embeds documents correctly, but the semantic search endpoint
embeds the raw query string with no instruction prefix. BGE-family models
(including bge-micro-v2) are trained asymmetrically: the query side should carry
the retrieval instruction (canonically
`"Represent this sentence for searching relevant passages: "`); the document
side should not. Omitting it measurably degrades retrieval.

- Code-only, no migration, no re-embedding — stored document embeddings remain
  valid. Add a `GenerateQueryEmbeddingAsync` path used only where a *query* is
  embedded.
- Verify the exact prefix wording against the bge-micro-v2 model card.
- `DeduplicationService` compares document-vs-document (symmetric) and should
  keep the unprefixed path.

### 2. Add metadata filters to both search endpoints

`BuildListQuery` (plain `GET /expertise`) supports `domain` / `tags` /
`entryType` / `severity`, but neither `KeywordSearchAsync` nor
`SemanticSearchAsync` accepts any of them — search and structured filtering are
mutually exclusive today. Because the callers are LLM agents that know the
taxonomy from the API schema and skill docs, explicit filter query parameters
are the right flexibility mechanism (and the reason an LLM "self-query"
translation layer would be redundant here). Additive, non-breaking `WHERE`
predicates applied before ranking.

### 3. Hybrid RRF endpoint (`GET /expertise/search/hybrid`), additive, fused in C#

All three research angles independently converged on hybrid sparse+dense
retrieval with Reciprocal Rank Fusion (`1/(k+rank)`, k≈60) as the consensus
architectural improvement — it directly fixes dense retrieval's documented
weakness on exact identifiers, which is exactly this system's query profile.

Implementation posture agreed across angles:

- **Additive endpoint, not a replacement.** Replacing the existing endpoints
  would trip the `oasdiff` breaking-change gate and would remove precise
  keyword-only lookup, which agents legitimately use.
- **Fuse in C# over the two existing repository methods, not one hybrid SQL
  CTE.** A combined raw-SQL query would force the currently LINQ-translatable
  vector arm (`CosineDistance()`) into raw SQL and would duplicate the
  tenant-scoping `WHERE` logic in a third location, weakening the ADR-001
  primary safeguard (the EF global query filter does not apply to
  `FromSql*` queries at all). Two bounded queries run concurrently and fused
  in-process are immaterial at this corpus size.
- **Prerequisites:** repository search methods must return scores/ranks (both
  currently discard them before mapping), and `KeywordSearchAsync` needs a
  `LIMIT` (currently unbounded, unlike semantic's clamped 1–100). Exposing the
  fused score in the response shape is also useful to agent callers.

### 4. Free FTS upgrades (do alongside any of the above)

- `plainto_tsquery` → `websearch_to_tsquery`: phrase quoting, `OR`,
  `-negation`; never throws on malformed input; same indexed `SearchVector`
  column, no migration.
- `ts_rank` → `ts_rank_cd`: cover-density (proximity-aware) ranking performs
  better on the short, few-word queries agents issue.

### 5. Golden-query evaluation harness (gates everything else)

Hand-label 30–100 real/representative queries with expected entry IDs; compute
recall@5 / MRR with a small script. At this corpus size no LLM judge is needed.
This is the mechanism that makes every subsequent retrieval decision
(especially reranking) evidence-based rather than assumed.

### 6. Consider cheaply: recency-aware ranking + supersession gap

- `DeprecatedAt` is a binary include/exclude filter; nothing prefers newer
  entries at ranking time. A mild recency tie-break in the hybrid score
  (pattern borrowed from the Stanford generative-agents
  recency/relevance/importance formula; `Severity` is already an importance
  proxy) fits a "running log" — a tie-break, not a hard boost.
- Related schema gap worth its own future ADR: no supersession link
  (`SupersededByEntryId` or similar), so a newer entry can never formally
  replace an older semantically-similar one.

## Explicitly rejected (with reasons)

| Option | Verdict | Reason |
| --- | --- | --- |
| Cross-encoder reranking (in-process ONNX) | Defer, evaluation-gated | Real technique, marginal at a curated corpus of this size; needs a second resident model (~90 MB+ vs the 17 MB embedder), a hand-built inference path (no SemanticKernel cross-encoder abstraction in the pinned connector version), ~100–300 ms added latency for top-20. Revisit only if the golden-query harness shows top-5 ordering errors hybrid fusion does not fix. |
| GraphRAG / knowledge-graph retrieval | No | Built for multi-hop synthesis over large corpora; these entries are independent point-lookup facts. 2025–26 literature is explicit that it matches naive RAG on factual lookups at a multiple of the cost. |
| mem0 / Letta / Zep / LangMem-style memory frameworks | No (borrow patterns only) | They bootstrap temporal/provenance modeling for unstructured conversational memory; this system already has that structurally (`ReviewState`, `Severity`, `DeprecatedAt`, audit log with before/after hashes). |
| HyDE / multi-query expansion | No | Adds an LLM call per query for a benefit the literature frames as narrow; the failing query class it targets (vague vocabulary-gap queries) is not this system's profile, and hybrid fusion addresses the actual weakness more directly. |
| LLM self-query filter translation | No | The callers are already LLMs that can populate `domain`/`entryType`/`severity`/`tags` parameters directly — the technique is redundant here, not merely low-value. |
| Separate vector database (Qdrant/Weaviate/etc.) | No | Contradicts the single-Postgres-backend design decision for no measurable gain at this scale. |
| HNSW tuning / IVFFlat / halfvec quantization | No action | Existing HNSW config is correct and effectively moot under ~10k rows; exact scan would already be sub-millisecond. If `hnsw.ef_search` tuning is ever adopted, `SET LOCAL` must run inside the same transaction as the query under PgBouncer transaction-mode pooling. |

## Caveats / unverified claims

- **Body-length truncation (CONFIRMED, #429 closed 2026-07-22):** empirically
  verified by bisection against the shipped model — the connector silently
  truncates at exactly 512 total tokens (510 content + `[CLS]`/`[SEP]`) with no
  exception or log at any level, corroborated at source level
  (`BertOnnxOptions.MaximumTokens` default 512; fixed-capacity span into
  `FastBertTokenizer.Encode`; no throw-on-overflow option exists). Fixed as
  predicted: `MaxBodyLength = 1500` guard (hard 400) on create/PATCH/batch,
  derived from measured 2.97–4.24 chars/token density on the 60 longest real
  entries. Follow-ups: Title bound (#436), long-context model swap (#437).
- Reranker latency/memory figures above are estimates for MiniLM-L-6-class
  models on modern CPU, not measurements on the target hosts.
- Related open defect on the search path: #329 (missing `q` returns 500
  instead of 400).

## Suggested sequencing (when action is planned)

1. Query-prefix fix + `websearch_to_tsquery` + `ts_rank_cd` (small,
   non-breaking, likely measurable).
2. Metadata filters on both search endpoints.
3. Score exposure in repository return shapes.
4. Hybrid RRF endpoint built on 2+3, with optional recency tie-break.
5. Golden-query harness built alongside 1 and used to gate 4 and any future
   reranking decision.

Each step lands with its own ADR/doc-impact classification per the working
conventions; DESIGN.md's API Surface and Known Gotchas sections are the
expected sync surfaces.

## Key sources (landscape survey)

- ParadeDB — Hybrid Search in PostgreSQL: The Missing Manual
- Jonathan Katz — Hybrid search with PostgreSQL and pgvector
- Towards Data Science — Do You Really Need GraphRAG?
- EmergentMind — Hypothetical Document Embeddings (HyDE) and knowledge-leakage caveats
- Vectorize.io — Mem0 vs Zep comparison (temporal-memory benchmarks)
- Stanford generative-agents memory architecture (recency/relevance/importance scoring)
