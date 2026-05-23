---
description: Search the expertise corpus for prior knowledge on a topic
argument-hint: "<query>"
---
Search the agent-expertise-api for prior knowledge relevant to: **$@**

1. Call the `expertise_search_semantic` tool first with `q: "$@"` — it handles paraphrases and conceptual queries well.
2. If the semantic results look weak (fewer than two hits, or none above a clearly-relevant threshold), fall back to `expertise_search` with `q: "$@"` for keyword-exact matching.
3. Summarise the top 3 hits with: id, title, severity, entryType, and a one-line takeaway. If any hit looks directly applicable to the current task, quote the salient sentence verbatim and cite the id.
4. If nothing relevant is found, say so explicitly — do not invent precedent.

Do not call `expertise_create` from this template; that is `/expertise-create`.
