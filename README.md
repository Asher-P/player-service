# Player Service

.NET 8 Web API over **Microsoft Orleans** (virtual actors) for a highly concurrent mobile
puzzle-game backend: sessions, scores, gifting and a leaderboard, with no database.

Implementation plan: [docs/plan/player-service-plan.md](docs/plan/player-service-plan.md).

> **Status: Phases 1-3 complete.** The topology is real and running - a co-hosted silo, three
> grains, transactions, memory streams, the leaderboard projection and the pod-local push cache -
> sessions are fully implemented (one session per device, supersede across devices, sliding
> 2-minute TTL), and score updates are atomic and idempotent: concurrent adds sum exactly, and a
> duplicate `requestId` replays the original outcome byte-for-byte. Gifting and the leaderboard
> projection land in phases 4-5; every stub is marked `TODO(Phase N)` in code. See
> [Phase status](#phase-status).

---

## Run it

```bash
dotnet run --project src/PlayerService.Api
```

Listens on `http://localhost:5080`. The API pod **is** an Orleans silo (co-hosted); there is no
separate silo process to start.

```bash
dotnet test
```

Runs the suite against a real **two-silo** `InProcessTestCluster`.

### Smoke test

```bash
curl -s -X POST http://localhost:5080/login -H "Content-Type: application/json" -d '{"playerId":"p1","deviceId":"d1"}'
```

Then send the returned token as `X-Session-Token` on every other request:

```bash
curl -s http://localhost:5080/players/p1/stats -H "X-Session-Token: p1.<guid>"
```

---

## API surface

All endpoints except `POST /login` require the `X-Session-Token` header.

| Endpoint | Success | Client-error statuses | Retry-safe? |
| --- | --- | --- | --- |
| `POST /login` | `200` + token | `400` bad body, `409` device already has an active session | No (handle the 409) |
| `GET /players/{id}/stats` | `200` + stats | `401` missing/expired/superseded token, `403` token belongs to another player | Yes |
| `POST /players/{id}/stats/score` | `200` + current stats | `400` non-positive points, `401`, `403`, `503` transaction aborted | **Yes** - same `requestId` returns the same result |
| `POST /players/{id}/gifts` | `200` + result | `400` self-gift / non-positive, `401`, `403`, `404` unknown recipient, `409` recipient offline **or** insufficient funds, `503` transaction aborted after retries | **Yes** - same `requestId` returns the original outcome |
| `GET /leaderboard` | `200` + top N + `ComputedAt` | `401` | Yes (wait-free local read) |

Status codes tell the client whether a retry is worth it: `400` client bug, never retry; `401`
re-authenticate; `403` wrong player, never retry; `409` state conflict, retry only after the
condition changes; `503` transient cluster contention, retry with backoff (safe, because the
operation is idempotent).

Two deviations from the plan's §5.7 table, both deliberate:

- **`403` added.** A token authenticates exactly one player, and that player may only act on their
  own resource - otherwise a valid token would be a key to every player's state.
- **`404` dropped from `/stats`.** Grains are virtual: any playerId activates with the seeded 1000
  points, so "unknown player" is not observable. Combined with the `403` rule a caller can only
  read their own stats, which makes the case unreachable. `404` remains meaningful for gifting,
  where an unknown *recipient* is detectable (never logged in).

---

## Phase status

| Phase | Scope | State |
| --- | --- | --- |
| 1 | Skeleton, Orleans topology, contracts, 2-silo test cluster | **Done** |
| 2 | Sessions and auth (duplicate-device 409, supersede/release, sliding TTL) | **Done** |
| 3 | Atomic + idempotent score updates | `AddPointsAsync` live: transactional apply, ledger check/write/prune, publishes `PlayerScoreUpdated` |
| 4 | Gifting via Orleans transactions | `POST .../gifts` returns `501`; debit/credit and `GiftService` are `TODO(Phase 4)` |
| 5 | Leaderboard ingest + push cache | Streams, subscription, timer and cache all live; the projection's apply step is `TODO(Phase 5)`, so the board stays empty |
| 6 | Concurrency harness, observability, README | Not started |

---

## Solution layout

```
src/PlayerService.Abstractions   grain interfaces, wire models, events, stream identity
src/PlayerService.Grains         PlayerGrain, DeviceGrain, LeaderboardGrain + internals
src/PlayerService.Api            controllers, DI/silo composition, auth filter, cache, gift service
tests/PlayerService.Tests        xUnit over a two-silo InProcessTestCluster
```

---

<!-- The sections below are the headings the final README must fill (plan §6, Phase 6). -->

## The Orleans concurrency model, and why there are no lost updates

_TODO(Phase 6)._

## Why there is no lock ordering, and what replaced it

_TODO(Phase 6)._

## Idempotency: two simultaneous duplicates, and how records stay bounded

_TODO(Phase 6)._

## The recipient-online guarantee, stated exactly

_TODO(Phase 6)._

## Leaderboard: structure, complexity and staleness

_TODO(Phase 6)._

## The push-cache strategy and what invalidates it

_TODO(Phase 6)._

## Session expiry policy

_TODO(Phase 6)._

## Assumptions

_TODO(Phase 6)._

---

## Local environment note

The projects target `net8.0` per the assignment. This machine has only the .NET 9/10 runtimes
installed, so `Directory.Build.props` sets `<RollForward>LatestMajor</RollForward>`: the assemblies
are still net8.0, they just run on the newest installed runtime. Remove it if you have the .NET 8
runtime and prefer the exact-version behaviour.
