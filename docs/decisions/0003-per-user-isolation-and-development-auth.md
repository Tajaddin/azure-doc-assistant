# 0003. Per-user isolation is enforced in storage queries, and development auth mints Entra-shaped tokens

Date: 2026-10-17. Status: accepted.

## Context

Documents are private to the person who uploaded them. In production identity comes from Microsoft
Entra ID (the posting's environment); locally and in CI there is no tenant.

## Decision

- Every table carries `owner_id`; every repository and index method takes it, and the SQL filters on it.
  There is no "admin" path that omits it. The owner id is the Entra `oid` claim when present, otherwise
  `sub`, read once per request (`ClaimsPrincipal.OwnerId()`).
- `Auth:Mode=entra` configures `Microsoft.Identity.Web` JWT bearer validation against the tenant and the
  API app registration. `Auth:Mode=dev` enables `POST /auth/dev-token`, which mints an HS256 token with the
  same claim shape (`oid`, `sub`, `name`, `preferred_username`) for a named test user; the same name always
  maps to the same `oid`, so a developer keeps their documents across tokens.
- Blobs are stored under `{ownerId}/{documentId}.pdf`; the container is private and nothing is served by
  URL. The blob-triggered Function reads both ids from the path and re-checks the record.
- Uploads are validated (extension, size, `%PDF` magic), questions are length-limited, and the chat and
  upload endpoints are rate limited per user.

## Consequences

- The integration test `Users_cannot_see_each_others_documents` proves the property end to end: a second
  user sees no documents, gets 404 on the first user's ids, and gets no sources or citations when asking.
- The dev token issuer is a real attack surface if it leaked into production, so it only exists when
  `Auth:Mode=dev`; the Bicep sets `entra` and the Static Web App build sets `VITE_AUTH_MODE=entra`.
- Role- or group-based sharing is not modelled; a document has exactly one owner.
