# Geoblitz Geo — Flight Deck

A single-view Angular console for the Geoblitz Geo API: click-to-query nearest cities,
alt-drag/slider radius search, and a two-click ruler, all rendered on a dark-themed OpenStreetMap
view with a live HUD (engine time, HTTP time, cache HIT/MISS, allocation figures).

See [`../docs/frontend.md`](../docs/frontend.md) for architecture, HUD semantics, gesture
details, and API contracts.

## Running it

```bash
npm ci      # first run only
npm start   # ng serve → http://localhost:4200
```

The API must be running separately (see the repo root `README.md` / `docs/frontend.md` "Running
it" for the exact command); the web app talks to it via the hardcoded `GEO_API_BASE_URL` token
documented in `docs/frontend.md`.

## Testing

```bash
npm test -- --watch=false   # Vitest, single run (used in CI)
npm test                    # Vitest, watch mode
```
