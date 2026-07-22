# Hybrid search via C#-side Reciprocal Rank Fusion as an additive endpoint

- Status: accepted
- Date: 2026-07-22

## Context and Problem Statement

The API serves AI-agent callers issuing short technical queries. The two existing
search endpoints are complementary and individually incomplete: keyword full-text
search hits exact identifiers (error codes, flag names) but returns nothing for
multi-word paraphrases (`websearch_to_tsquery` ANDs all terms), while semantic
vector search covers paraphrases but smooths over rare exact tokens. The
golden-query evaluation harness (#425) measured this concretely: keyword recall@10
0.367 (identifier queries only), semantic recall@10 1.000 — on disjoint failure
modes. The 2026-07 retrieval assessment
([docs/research/2026-07-retrieval-assessment.md](../docs/research/2026-07-retrieval-assessment.md))
identified hybrid sparse+dense fusion as the consensus fix. How should the two
retrieval modes be combined? (#428)

## Considered Options

- **C#-side Reciprocal Rank Fusion over the two existing repository queries, as a
  new additive endpoint** (`GET /expertise/search/hybrid`)
- **Single hybrid SQL query** (CTE combining `ts_rank_cd` and pgvector cosine
  distance with weighted score fusion or in-SQL RRF)
- **Replace the two existing endpoints** with the hybrid as the only search path

## Decision Outcome

Chosen option: "C#-side RRF as a new additive endpoint", because:

- **RRF over weighted score fusion**: `ts_rank_cd` is unbounded and
  document-length dependent; cosine similarity is bounded. Weighted fusion needs
  per-query score normalization that is fragile across query shapes. RRF
  (`score = Σ 1/(k + rank)`, k=60) is rank-based, scale-free, and the standard
  answer for fusing two independently-ranked lists.
- **C# fusion over a hybrid SQL CTE**: a single hybrid query would force the
  vector arm (currently LINQ-translatable via `CosineDistance()`) into raw SQL
  and duplicate the tenant-scoping `WHERE` logic in yet another location. The EF
  global query filter does not apply to `FromSql*` queries, so every raw-SQL
  surface must hand-maintain the ADR-001 tenant safeguard — two independently
  correct, independently tested arms fused in process keep that surface minimal.
  At a corpus of hundreds-to-low-thousands of rows, the second round trip is
  immaterial. The two arm queries run sequentially on the scoped `DbContext`
  (EF contexts are not thread-safe; parallelizing them would require a second
  scope for no measurable gain at this scale).
- **Additive over replacement**: replacing the existing endpoints would be an
  OpenAPI breaking change and would remove precise keyword-only lookup, which
  agent callers legitimately use for exact strings. All three search modes
  remain available; the hybrid endpoint is the recommended default for agents.
- **Recency tie-break**: equal fused scores (common when items appear in only
  one arm at the same rank) order by `UpdatedAt` descending — a mild
  running-log freshness preference that never overrides a relevance
  difference. A stronger recency *boost* was considered and rejected: this is
  a knowledge log, not a news feed, and the supersession-link design (#430) is
  the structural answer to staleness.
- The fused hit reuses the `score` response field (#427) with RRF-sum
  semantics — like the other modes, comparable only within one response.

### Consequences

- Good, because agent retrieval gets keyword-exactness and semantic recall in
  one call — the measured miss class of each arm is covered by the other.
- Good, because tenant scoping stays in exactly two audited arm queries; the
  fusion layer never touches the database.
- Good, because the candidate depth (top 50 per arm) and `k` are single
  constants in `RankFusion`, tunable without schema or endpoint changes.
- Bad, because hybrid latency is the sum of both arms (one ONNX inference plus
  two queries); the endpoint therefore shares the `semantic-search` rate-limit
  policy.
- Bad, because three search endpoints must be documented and maintained; the
  skill and pi-extension surfaces grow accordingly.
