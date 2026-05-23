---
description: Approve a Draft expertise entry (requires expertise.write.approve)
argument-hint: "<id> [visibility]"
---
Approve the draft expertise entry with id **$1**. Visibility argument (optional): **$2**

Steps:

1. First call `expertise_get` with `id: "$1"` and show me the entry's title, domain, entryType, severity, body summary, and current `reviewState`.
2. If `reviewState` is not `Draft`, stop and report the actual state — `expertise_approve` will return 409 on non-Draft entries.
3. If the second argument above is `Private` or `Shared`, pass `visibility: "$2"` to `expertise_approve`. If empty or any other value, omit `visibility` so the server applies its default (`Private`).
   - Only set `Shared` if the entry is genuinely useful across tenants. Shared entries are visible to every caller; private entries are visible only within the owning tenant.
4. Call `expertise_approve` with `id: "$1"` (and `visibility` if applicable per step 3).
5. Report the response: `id`, new `reviewState`, `visibility`, and `reviewedAt` timestamp.

If you do not hold the `expertise.write.approve` scope the API will return 403; surface that and stop.

To reject instead of approve, ask the invoker for a rejection reason and call `expertise_reject` directly (no slash-command template; the reason must be 1-2000 chars).
