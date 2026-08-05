# Player Service

.NET 8 Web API over **Microsoft Orleans** (virtual actors) for a highly concurrent mobile
puzzle-game backend: sessions, scores, gifting and a leaderboard, with no database.

Implementation plan: [docs/plan/player-service-plan.md](docs/plan/player-service-plan.md).

> **Status: Phases 1-5 complete - the service is functionally done.** Sessions (one per device,
> supersede across devices, sliding 2-minute TTL), atomic and idempotent score updates, gifting as a
> distributed transaction (points conserved, balances never negative, `p1↔p2` in a tight loop does
> not deadlock, replays return the original outcome byte-for-byte), and a push leaderboard whose
> ingest is a single cluster-wide writer and whose reads are wait-free pod-local snapshots that
> converge across pods. Phase 6 remains: the concurrency harness, observability, and filling in the
> README sections below. See [Phase status](#phase-status).

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

## Two findings from Phase 4 worth stating plainly

**Orleans' default lock timeouts silently defeat the retry design.** The gift methods carry
`[ResponseTimeout("00:00:05")]` per the plan, but Orleans resolves transactional lock contention at
`LockTimeout` 8 s / `LockAcquireTimeout` 10 s by default. A transaction queued behind a contended
player therefore hit the *call* timeout first, so contention surfaced as a raw `TimeoutException`
rather than the clean abort the retry loop is built around - and each retry burned the full 5 s.
Under a 60-gift burst this exhausted the budget and failed. Both values are now configured well
below the response timeout (2 s / 1 s) in
[OrleansHostExtensions.cs](src/PlayerService.Api/Extensions/OrleansHostExtensions.cs), and the retry
budget is sized to actually de-conflict a hot pair (8 attempts, 100 ms exponential + full jitter).
The same burst now passes in a fraction of the time. Fail fast, back off properly, retry more.

**`UnknownRecipient` is activation-local, and that is a real limitation.** "Never logged in" is
detected by the recipient grain having no bound session, and sessions are plain activation state by
design (they are read, never written, inside the gift transaction, so they carry no rollback
hazard). After the recipient's activation is collected, a known-but-offline player is reported as
unknown - a `404` where a `409` would be more accurate. The gift is correctly refused either way;
only the status code is affected. Moving session liveness into transactional state would fix it and
would cost every gift a second transactional participant.

---

## Phase status

| Phase | Scope | State |
| --- | --- | --- |
| 1 | Skeleton, Orleans topology, contracts, 2-silo test cluster | **Done** |
| 2 | Sessions and auth (duplicate-device 409, supersede/release, sliding TTL) | **Done** |
| 3 | Atomic + idempotent score updates | `AddPointsAsync` live: transactional apply, ledger check/write/prune, publishes `PlayerScoreUpdated` |
| 4 | Gifting via Orleans transactions | `GiftService` live: API-layer transaction, debit + credit + ledger write, bounded retry on abort |
| 5 | Leaderboard ingest + push cache | **Done** - stream ingest applies absolute scores to `Dictionary` + `SortedSet`, broadcasts Top-N on change, pods converge |
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
