# Player Service

.NET 8 Web API over **Microsoft Orleans** (virtual actors) for a highly concurrent mobile
puzzle-game backend: sessions, scores, gifting and a leaderboard, with no database.

Implementation plan: [docs/plan/player-service-plan.md](docs/plan/player-service-plan.md).

> **Status: complete.** Sessions (one per device, supersede across devices, sliding 3-minute TTL),
> atomic and idempotent score updates, gifting as a distributed transaction (points conserved,
> balances never negative, `p1↔p2` in a tight loop does not deadlock, replays return the original
> outcome byte-for-byte), a push leaderboard whose ingest is a single cluster-wide writer and whose
> reads are wait-free pod-local snapshots converging across pods, **39 tests** on a two-silo cluster
> plus an HTTP-level harness, a **15-test black-box end-to-end suite** that re-establishes every
> concurrency guarantee against a running service over HTTP, and OpenTelemetry metrics and tracing.

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

```bash
dotnet test tests/PlayerService.E2ETests
```

Runs the end-to-end suite against the service started above, on `http://localhost:5080`, over
plain HTTP with no shared types — see [End-to-end suite](#end-to-end-suite-against-a-running-service).
It skips itself if nothing is listening.

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
| 3 | Atomic + idempotent score updates | **Done** - transactional apply, ledger check/write/prune, publishes `PlayerScoreUpdated` |
| 4 | Gifting via Orleans transactions | **Done** - API-layer transaction, debit + credit + ledger write, bounded retry on abort |
| 5 | Leaderboard ingest + push cache | **Done** - stream ingest applies absolute scores to `Dictionary` + `SortedSet`, broadcasts Top-N on change, pods converge |
| 6 | Concurrency harness, observability, README | **Done** - 39 tests incl. HTTP-level harness, 15 end-to-end tests against a live service, OpenTelemetry metrics + tracing, this README |

---

## Solution layout

```
src/PlayerService.Abstractions   grain interfaces, wire models, events, stream identity
src/PlayerService.Grains         PlayerGrain, DeviceGrain, LeaderboardGrain + internals
src/PlayerService.Api            controllers, DI/silo composition, auth filter, cache, gift service
tests/PlayerService.Tests        xUnit: two-silo InProcessTestCluster + WebApplicationFactory
tests/PlayerService.E2ETests     xUnit: black-box HTTP suite against a service you started
deploy/                          docker-compose stack: Jaeger, Prometheus, Grafana + provisioning
Dockerfile                       multi-stage; builds on SDK 10, runs on the real .NET 8 runtime
```

---

## Tests

```bash
dotnet test
```

39 in-process tests plus 15 end-to-end tests, with no `Thread.Sleep`-based timing anywhere: time is
either advanced by hand through an injected `TimeProvider`, or polled to a deadline. The end-to-end
suite needs a service to be running and skips itself when there is not one, so `dotnet test` on the
whole solution is green either way.

| Suite | Cluster | What it establishes |
| --- | --- | --- |
| `TopologyTests` | 2-silo | The topology is real: two silos, grains resolve, streams subscribe |
| `SessionTests` | 2-silo | Duplicate-device login rejected, supersede across devices, release |
| `SessionExpiryTests` | 2-silo, **manual clock** | Sliding TTL, lazy expiry, mutual-supersede does not deadlock |
| `ScoreTests` | 2-silo | N parallel adds sum exactly; 100 parallel duplicates apply once |
| `GiftTests` | 2-silo | Conservation, no negative balance, `p1↔p2` terminates, replay-after-offline, offline/overdraw rejected |
| `LeaderboardTests` | 2-silo, cache per silo | Ingest correctness, gift moves both players, **both pods converge** |
| `ApiContractTests` | `WebApplicationFactory` | The status-code map over real HTTP: 200/400/401/403/404/409 |
| `E2E.ScoreConcurrencyTests` | **live service** | 50 posts in parallel land on 50 distinct balances; the same `requestId` ×100 applies once |
| `E2E.GiftConcurrencyTests` | **live service** | Conservation and counters over 60 parallel gifts, `p1↔p2` tight loop, overdraw race, duplicate `requestId` race |
| `E2E.SessionTests` | **live service** | Duplicate `deviceId` rejected (sequentially and under a 16-way race), supersede, cross-player `403` |
| `E2E.LeaderboardTests` | **live service** | Ranks dense and ordered, and the board converges on what `/stats` reports after a mixed burst |
| `E2E.OfflineGiftTests` | **live service**, lapsed sessions | Replay after the recipient went offline, offline rejection moves nothing, rejections replay as rejections |

Two silos everywhere is deliberate. On one silo, "single activation cluster-wide" is
indistinguishable from a local dictionary, and none of the guarantees above would be under test.

### Concurrency scenarios from the brief

Every scenario is asserted twice: once in-process against the cluster, where the failure mode is
readable, and once end-to-end against a running service over HTTP, where the client's own view is
the only thing being trusted.

| Scenario | In-process test | End-to-end test |
| --- | --- | --- |
| N concurrent score adds on one player, nothing lost | `N_parallel_score_posts_sum_exactly` | `N_parallel_score_posts_add_up_exactly` |
| Same `requestId` ×100 simultaneously, applied once | `Duplicate_requestId_fired_in_parallel_applies_once` | `The_same_requestId_fired_100_times_in_parallel_is_applied_once` |
| Duplicate `deviceId` login in parallel, one wins | `Concurrent_logins_on_one_device_grant_exactly_one_session` | `A_duplicate_deviceId_login_is_rejected`, `Simultaneous_logins_on_one_device_yield_exactly_one_token` |
| Points conserved under concurrent random-pair gifts | `Many_concurrent_gifts_across_random_pairs_conserve_points` | `Many_parallel_gifts_across_random_pairs_conserve_points_and_never_go_negative` |
| `p_1→p_2` and `p_2→p_1` in a tight loop, no deadlock | `Gifts_in_both_directions_between_one_pair_do_not_deadlock` | `p1_and_p2_gifting_each_other_in_a_tight_loop_do_not_deadlock` |
| Balance never negative under concurrent overdraw | `Two_simultaneous_gifts_that_together_overdraw_let_only_one_through` | `Two_simultaneous_gifts_that_together_overdraw_let_exactly_one_through` |
| Gift to offline player rejected, no points moved | `A_gift_to_an_offline_player_is_rejected_and_moves_no_points` | `A_gift_to_an_offline_player_is_rejected_and_moves_no_points` |
| Replay after recipient went offline returns original | `A_gift_replayed_after_the_recipient_went_offline_still_returns_the_original` | `A_replayed_gift_after_the_recipient_went_offline_returns_the_original_result` |
| Leaderboard correct after a concurrent burst | `The_board_converges_on_the_true_scores_after_a_burst` | `The_leaderboard_is_correct_after_a_burst_of_concurrent_scores_and_gifts` |
| Both pods agree on the same Top-N | `Both_pods_converge_on_the_same_snapshot` | — (needs two pods; the local service is one) |

### End-to-end suite against a running service

`tests/PlayerService.E2ETests` is the same list of guarantees asserted the way a game client would
find out about them: over HTTP, against a process nobody in the test owns.

```bash
dotnet run --project src/PlayerService.Api      # in one terminal
dotnet test tests/PlayerService.E2ETests        # in another
```

15 tests, ~3 minutes. It has **no `ProjectReference`** — the wire contract is re-declared in
`Wire.cs`, so a server-side rename fails a test here instead of silently recompiling. Two knobs,
both environment variables:

| Variable | Default | Why |
| --- | --- | --- |
| `PLAYERSERVICE_E2E_BASEURL` | `http://localhost:5080` | Point the suite at a deployed pod instead |
| `PLAYERSERVICE_E2E_SESSION_TTL` | `00:03:00` | Must match the service's `Sessions:Ttl` (see below) |

Three details are worth stating, because they are what makes the results mean anything:

- **A burst is a burst.** Every "N in parallel" test builds all N tasks first and releases them
  through one gate, over a connection pool that was warmed beforehand — otherwise "100 in parallel"
  degrades into 100 in a fast loop, staggered by TCP handshakes, and proves nothing. The N-parallel
  score test additionally asserts that the N responses form N *distinct* balances: a lost update
  shows up as a repeated rung on the ladder, which a sum check alone can miss when two errors cancel.
- **Offline costs a TTL, and the suite pays it once.** There is no logout endpoint by design, so the
  only honest route to "offline" over HTTP is to stop touching a session and let it lapse. Both
  offline scenarios share a single wait in `OfflineRecipientFixture`, which logs in two recipients
  together and detects the transition by probing with 1-point gifts until the answer flips from
  `200` to `409 Recipient offline` — no fixed sleep, and the probes keep the activations warm so the
  rejection under test is *offline* and not *collected* (which is the `404` discussed above). Set
  `Sessions:Ttl` to its 1-minute floor and tell the suite via `PLAYERSERVICE_E2E_SESSION_TTL` to
  cut the run to a third. The other collection runs in parallel with the wait, so the suite costs about one
  TTL in total rather than one TTL plus everything else.
- **A red suite means the service is wrong.** If nothing is listening, every test reports as
  *skipped*, not failed, so `dotnet test` across the solution stays honest on a machine where only
  the in-process suites can run.

---

## Observability

The whole stack — service, Jaeger, Prometheus, Grafana — comes up with one command:

```bash
docker compose -f deploy/docker-compose.yml up --build
```

To iterate on the service itself, bring up only the backends and run it from the host; the
`Development` settings already point at the host ports below, so traces and metrics land in the same
place either way:

```bash
docker compose -f deploy/docker-compose.yml up -d jaeger prometheus grafana
```

| | URL | Notes |
| --- | --- | --- |
| API | http://localhost:5080 | `/health` is the one unauthenticated route |
| **Orleans Dashboard** | http://localhost:5080/dashboard | Served by the pod itself — the cluster's own view |
| Grafana | http://localhost:3501 | Anonymous admin; the **Player Service** dashboard is pre-provisioned |
| Jaeger | http://localhost:16687 | Pick the `player-service` service |
| Prometheus | http://localhost:9101 | |

Host ports are variables in [`deploy/.env`](deploy/.env) with defaults that deliberately avoid the
conventional ones (16686, 4317, 9090, 3000), because a machine that already runs a stray Jaeger or
Prometheus is the common case and a port clash is an unhelpful first experience.

The container runs on the **real .NET 8 runtime** (`aspnet:8.0`), so `RollForward` — which exists
only because this dev machine has no .NET 8 installed — is a no-op there.

**Prometheus receives OTLP directly** (`--web.enable-otlp-receiver`), so the service pushes metrics
to it rather than being scraped. That removes the OpenTelemetry Collector that would otherwise be
needed just to bridge push to pull, at the cost of requiring Prometheus v3. Metric names are
normalised to classic form (`playerservice.gift.aborts` → `playerservice_gift_aborts_total`);
preserving the original names is possible but forces every selector containing a dot to be quoted.

Against your own Jaeger on the conventional ports instead — override the endpoints, since the
`Development` defaults assume the shifted ones:

```bash
docker run -d --name jaeger -p 16686:16686 -p 4317:4317 -p 4318:4318 jaegertracing/jaeger:2.11.0
```

```bash
Observability__TracesOtlpEndpoint=http://localhost:4318/v1/traces dotnet run --project src/PlayerService.Api
```

**Nothing is ever exported to the console.** Metric export runs on a timer, so a console exporter
prints continuously whether or not anything happened — it buried the service's own logs under
~117k lines in a two-minute run. Telemetry goes to a collector or nowhere.

| Setting | Default | Effect |
| --- | --- | --- |
| `Observability:Enabled` | `true` | Registers the OpenTelemetry providers at all |
| `Observability:TracesOtlpEndpoint` | *(empty)* | Jaeger's OTLP receiver; empty disables trace export |
| `Observability:MetricsOtlpEndpoint` | *(empty)* | A metrics backend; empty means collected but not shipped |
| `Observability:UseHttpProtobuf` | `false` | Use OTLP over HTTP (port `4318`) instead of gRPC (`4317`) |
| `Observability:MetricExportIntervalSeconds` | `15` | Metric push cadence; must stay ≤ the Grafana datasource's `timeInterval` |

Both endpoints are empty in the base settings on purpose: an endpoint nobody is listening on is worse
than none, because the exporter's failures surface on an EventSource rather than in the log, so the
service looks healthy while shipping nothing. The two environments that *do* have a collector fill
them in — [`appsettings.Development.json`](src/PlayerService.Api/appsettings.Development.json) with
the host ports the compose stack publishes, and [`docker-compose.yml`](deploy/docker-compose.yml)
with the container names.

`MetricExportIntervalSeconds` looks like an exporter detail and is really a dashboard setting. The
OpenTelemetry default is 60s, and the service pushes rather than being scraped, so that cadence *is*
the sample spacing Prometheus stores. Grafana derives `$__rate_interval` as
`max($__interval + timeInterval, 4 * timeInterval)`, which at the provisioned `timeInterval: 15s`
bottoms out at 60s — and a 60s window over 60s-spaced samples usually holds one point, while `rate()`
needs two. The panels render "No data" rather than an error, which is a slow way to find out. Pushing
every 15s puts four samples in the narrowest window Grafana will ask for. If either number changes,
change the other:
[`datasources.yml`](deploy/grafana/provisioning/datasources/datasources.yml).

Traces and metrics have **separate** endpoints on purpose. Jaeger stores traces, not metrics; sending
metrics to it would produce a steady stream of failed-export errors rather than data. Point
`MetricsOtlpEndpoint` at a Prometheus/OTLP metrics collector when there is one. Until then the
instruments still record — recording to an unobserved instrument is a no-op, so the instrumentation
stays on the tested path rather than rotting behind a flag.

- **`Microsoft.Orleans` meter** — grain call latency (`orleans-app-requests-latency-*`), activation
  counts, and `orleans-transactions-started` / `-successful` / `-failed` / `-throttled`.
- **`PlayerService` meter** — the things Orleans cannot see: `gift.attempts` vs `gift.aborts`,
  `gift.attempts_per_request` (a histogram whose tail is what predicts 503s), and `gift.outcomes` /
  `score.outcomes` tagged by result, so replay rate shows how chatty the clients really are.
- **Orleans Dashboard** (`Microsoft.Orleans.Dashboard`, first-party, version-matched to the runtime)
  — served by the pod itself at `/dashboard`. It answers the questions Prometheus is the wrong shape
  for: which grain *types* are activated right now, and call rate / latency / exception count broken
  down **per grain method** (`PlayerGrain.AddPointsAsync` distinct from `PlayerGrain.DebitForGiftAsync`),
  plus a live log stream. Enabled by `Observability:DashboardEnabled`, and off in tests.

  It is **unauthenticated** and mapped as a minimal-API endpoint, so the MVC session filter does not
  cover it — it exposes grain types, activation counts and logs to anyone who can reach the port.
  Fine behind a private network or an authenticating ingress; not fine on a public listener. Where
  real authentication exists, add `.RequireAuthorization()` to the mapped endpoint.

- **Tracing** — `silo.AddActivityPropagation()` carries the ambient `Activity` across grain calls,
  so one HTTP request is one trace. A single gift trace in Jaeger contains the whole two-phase
  commit: `POST players/{playerId}/gifts` over `ValidateAndSlideSessionAsync`,
  `DebitForGiftAsync`, `CreditFromGiftAsync`, `CompleteGiftAsync`, and the transaction manager's
  `Prepare` / `Prepared` / `PrepareAndCommit` / `Confirm` — rather than a dozen unrelated traces.

Abort rate is the metric worth watching. An abort followed by a successful retry is invisible to the
client and is **not** an error — it is the design working. What matters is the ratio: aborts climbing
against attempts means the retry budget is approaching exhaustion, and that is a configuration
decision (`Gifting:MaxAttempts`, `RetryBaseDelay`), not a code change.

### Alert log

Everything the service logs goes to the console, as before. A **second sink takes only `Warning` and
above** and writes it to a dated file, so a day of trouble reads without scrolling past a day of
success:

```
src/PlayerService.Api/Alert-Logs/alerts-20260806.log
```

Two mechanisms fill it, and they are deliberately different. The sink is filtered by **level**, so
any component logging at `Warning` or above lands there for free — including code written later. And
[`UseAlertRequestLogging`](src/PlayerService.Api/Extensions/AlertLoggingExtensions.cs) adds one event
per request whose level comes from the **response status**: `4xx` is a rejection and logs `Warning`,
`5xx` or an escaped exception logs `Error`, everything else stays at `Information` and never reaches
the file. A request can therefore be recorded as rejected even where the code that rejected it says
nothing:

```
2026-08-06 18:58:28.892 +03:00 [WRN] PlayerService.Api.Auth.SessionAuthFilter Rejected GET /players/p1/stats: missing or malformed session token {...}
2026-08-06 18:58:28.929 +03:00 [WRN] Serilog.AspNetCore.RequestLoggingMiddleware HTTP GET /players/p1/stats responded 401 in 59.1054 ms {"TraceId":"98f1f705b079dfb8bcf5dd637d4e138f",...}
```

Where a rejection has a *reason* the status code cannot carry, it is logged at the site: a malformed
token and an expired one are both `401`, and which of the two is happening is what decides whether
anyone needs to act. Gift rejections log their `GiftRejection` the same way. Request lines carry the
`TraceId` Jaeger indexes on, so an alert leads straight to the span that produced it.

| Setting | Default | Effect |
| --- | --- | --- |
| `AlertLog:Enabled` | `true` | Attaches the file sink at all; the console is unaffected |
| `AlertLog:Directory` | `Alert-Logs` | Relative paths resolve against the content root |
| `AlertLog:MinimumLevel` | `Warning` | The cut-off for "this is an alert" |
| `AlertLog:RetainedFileCountLimit` | `30` | One file per day, so a 30-day window |

Serilog's rolling file sink appends the date before the extension, which is why the files are
`alerts-20260806.log` rather than `2026-08-06.log`: a literal date in the path would have to be
computed at startup and would then never roll over at midnight in a long-running service. Level
minimums for the rest of the pipeline live in the `Serilog` section of
[`appsettings.json`](src/PlayerService.Api/appsettings.json), replacing the old `Logging` section,
which Serilog does not read. The compose stack binds the container's `/app/Alert-Logs` to
`Alert-Logs/` at the repo root, so the file outlives the container.

Worth seeing concretely. Firing 50 simultaneous gifts at a **single pair** of players through the
containerised service produced **258 attempts for 54 gifts — a 79% abort rate** — and every gift
still applied, with points exactly conserved and a mean of 4.8 attempts each. That is what healthy
contention looks like on this design: the runtime resolves a hot pair by aborting and retrying, and
the client sees only `200`s. The same numbers with `MaxAttempts` at 3 would have produced 503s, which
is precisely why the panel is on the dashboard.

---

## The Orleans concurrency model, and why there are no lost updates

A **grain** is a virtual actor: an object with a durable identity (here, `playerId`) that Orleans
guarantees has **at most one activation cluster-wide**. `GetGrain<IPlayerGrain>("p1")` from any pod
routes to that one activation, wherever it lives. You never ask which pod owns a player, because the
question is not expressible.

Within an activation, Orleans runs **one turn at a time**. A turn is the synchronous stretch of a
method between `await` points. Concurrent requests to the same grain queue and execute one after
another — not in parallel with locks around them, but genuinely one at a time.

That is the whole lost-update story:

```csharp
state.Balance += points;   // read-modify-write, no lock, and correct
```

A lost update requires two threads to interleave between the read and the write. On a grain there is
no second thread: the runtime's per-activation queue is the serialization, and it holds across the
cluster rather than within one process. `N_parallel_score_posts_sum_exactly` fires 50 concurrent
adds at one player and the total is exact.

Two consequences worth naming:

- **There is no global lock, and could not be.** Activations are independent. A hot player
  serializes only itself; player A's queue has nothing to do with player B's. Reads of one player
  never queue behind writes to another because they are different queues on possibly different
  silos.
- **`[Reentrant]` on `PlayerGrain` does not weaken this.** It is an Orleans hard requirement for
  grains using `ITransactionalState`: the transaction protocol delivers prepare/commit callbacks
  into the grain while it is still awaiting other participants, so a non-reentrant grain would
  deadlock against its own transaction. It buys interleaving *with the transaction runtime*. The
  invariant that keeps it safe is stated on the interface and honored by every method: **anything
  mutating plain, non-transactional state completes its read-modify-write within one turn, with no
  `await` in between.** Transactional state is protected by transaction isolation instead, which is
  a stronger guarantee and does not depend on turn boundaries.

## Why there is no lock ordering, and what replaced it

Lock ordering exists to break cycles in a *wait-for* graph. It is only necessary if something is
held while something else is acquired. This design has no such thing, for two independent reasons.

**There is no lock to order.** Per-player mutual exclusion is the runtime's activation queue, not a
`lock` we took. There is no ordinal comparison of player IDs anywhere in the codebase, and searching
for one is how you confirm it.

**The call graph is a tree, so no cycle exists to break.** Gifting could have been
`senderGrain.SendGift()` calling `recipientGrain.Credit()`. That is a genuine cycle: `p_1→p_2` and
`p_2→p_1` firing together deadlock at the activation level, which is exactly what the brief tests
for. Instead, `GiftService` — at the **API layer** — opens the transaction and calls both grains
itself:

```
GiftService ──▶ sender    (debit)
            ──▶ recipient (credit)
            ──▶ sender    (record outcome)
```

Sender never calls recipient. With no edge between them, there is no cycle, and activation-level
deadlock is not merely avoided but unrepresentable.

**Contention resolves by abort, not by blocking.** Two transactions touching the same pair in
opposite order conflict at the transactional-state layer. Orleans aborts one; it never blocks both.
`GiftService` retries with exponential, fully-jittered backoff, which is safe *precisely because the
operation is idempotent* — if the aborted attempt had in fact committed, the retry finds the ledger
entry and returns the original outcome. `Gifts_in_both_directions_between_one_pair_do_not_deadlock`
runs both directions concurrently and every request terminates.

One thing this cost us, and it is worth stating: Orleans' **default** lock timeouts (8 s / 10 s)
exceed the gift methods' 5 s `[ResponseTimeout]`, so contention surfaced as a call timeout rather
than a clean abort, and burned the whole retry budget. Both are now configured below the response
timeout. Removing lock ordering does not remove the need to think about timeouts — it relocates it.

## Idempotency: two simultaneous duplicates, and how records stay bounded

Keys are `score:{requestId}` and `gift:{requestId}`, in a ledger stored **inside the player's
transactional state**. The grain key already scopes them to one player, so `{playerId, requestId}`
and `{senderId, requestId}` are naturally distinct namespaces on distinct grains. The stored value
is the **exact outcome DTO the original attempt returned**, so a replay is byte-for-byte stable
rather than merely "also successful".

**Two duplicates in flight at the same instant.** Both target the same grain key, so Orleans routes
both to the same activation:

- *Score adds* run in different **turns**. The check and the apply are in the same turn, so there is
  no window between them. Whichever runs second sees the ledger entry the first wrote and returns
  it. `Duplicate_requestId_fired_in_parallel_applies_once` fires 100 at once: one applies, 99
  replay, all 100 responses identical.
- *Gifts* run in different **transactions** against the same transactional state under serializable
  isolation. The second either observes the committed entry, or conflicts, aborts, retries, and then
  observes it. There is no interleaving in which both miss and both apply.

**The entry commits with the effect it describes.** The ledger write is in the same transaction as
the debit. If the transaction aborts, the entry rolls back with it — we never record a success that
did not happen. The old `IMemoryCache` design had to argue for this; here it is structural.

**Rejections are the deliberate exception.** A rejected gift *aborts* its transaction, so an
in-transaction record would roll back along with the rejection it describes, and the replay would
re-run the whole attempt. Rejections are therefore recorded in a **separate, non-transactional**
write issued after the abort. This asymmetry is intentional and is the one place the "record commits
with its effect" rule is knowingly broken — because for a rejection, there is no effect to commit
with.

**Bounding.** FIFO capped at **256 entries per player** with a **10-minute TTL**, pruned lazily on
each write. No per-player timer: millions of grain timers is itself the anti-pattern. Combined with
`CollectionAge` of 15 minutes, an idle player's activation and its ledger leave memory entirely.

**What that costs.** Once a record is gone — TTL, cap overflow, or a collected activation — a late
replay is treated as brand new and may re-apply. This is acceptable because the TTL vastly exceeds
the client retry window (seconds, per the brief), and it is a deliberate bound-memory-first trade
rather than an oversight.

## The recipient-online guarantee, stated exactly

> **If the recipient's session had not expired as of the transaction's commit point, the gift
> applies. An expiry occurring strictly after commit does not roll it back.**

What makes it non-stale is *where* the check runs: on the **recipient's own activation, inside the
same transaction as the credit**, reading that grain's own session expiry. It is not a lookup in a
shared table that could be out of date by the time the credit lands. If the recipient is offline,
`CreditFromGiftAsync` throws, the entire transaction aborts, and **nothing is debited** — verified by
`A_gift_to_an_offline_player_is_rejected_and_moves_no_points`.

What is **not** promised: that the recipient is still online a moment later. No design can promise
that; a session can lapse the instant after commit. The guarantee is about staleness relative to the
write, not about the future.

The session fields are **read, never written**, inside the gift transaction, so the fact that they
are plain non-transactional state introduces no rollback hazard.

One honest limitation. "Never logged in" is inferred from the recipient grain having no bound
session, and that state is activation-local. After the activation is collected, a known-but-offline
player reports as `UnknownRecipient` (404) rather than `Offline` (409). The gift is correctly refused
either way — only the status code is affected. Fixing it means moving liveness into transactional
state, at the cost of a second transactional participant on every gift.

## Leaderboard: structure, complexity and staleness

One `LeaderboardGrain` (key `0`) is the cluster's sole writer, holding two collections:

| Structure | Purpose | Complexity |
| --- | --- | --- |
| `Dictionary<string, PlayerScore>` | O(1) lookup of a player's *current* entry, so the stale one can be found | O(1) |
| `SortedSet<PlayerScore>` ordered by score desc, playerId asc | The ranking | O(log U) remove + O(log U) add |

Applying one score is therefore **O(log U)** in the number of players, and reading Top-N off the
front of the set is **O(N)**. Nothing sorts all players per request, ever.

Two details that are load-bearing rather than incidental:

- **The tie-breaker is required, not cosmetic.** A `SortedSet` treats "compares equal" as "the same
  element", so without `playerId` in the comparison, two players on the same score would collapse
  into one entry.
- **The set holds every player, not just the top N.** A player who gifts points away must be able to
  fall out of the top N and later climb back in. Keeping only N entries would make that
  unrecoverable.

**Ingest is single-writer**, so no locks and no concurrent collections — the same property the
in-process `BackgroundService` had, now with cluster-wide scope. Events carry **absolute** scores,
never deltas, which is what makes the projection idempotent under Orleans' at-least-once stream
delivery: a redelivered event re-states the truth instead of double-counting. A gift arrives as
**one** event carrying both sides, so the pair is absorbed together rather than showing an instant
where the points exist twice.

**Staleness: bounded by stream delivery + the 1 s broadcast interval, so typically under ~1.1 s.**
The bound is returned to the client as `ComputedAt`, so it is observable rather than merely
documented. This is the deliberate trade: a strongly-consistent read would reintroduce the hot grain
the entire design exists to avoid, and a leaderboard is inherently an approximate, read-mostly view.

The one real bottleneck is **ingest** — every score change funnels into one activation. Stream
batching absorbs a lot and 1 s coalescing keeps the publish side cheap. If ingest ever saturates,
the escape hatch is to shard into M `LeaderboardShardGrain`s keyed by `hash(playerId) % M`, each
publishing its local top-N to a merge grain. Documented, measured before adopted, not built
speculatively.

## The push-cache strategy and what invalidates it

`GET /leaderboard` **never touches the leaderboard grain**. Millions of readers pulling from one
activation is the textbook hot-grain anti-pattern. Inverting it:

1. A grain timer fires every **1 s**. If the set changed since the last publish, the grain broadcasts
   a `LeaderboardSnapshot` to `leaderboard/top`.
2. `LeaderboardCache` — an `IHostedService` on **every** pod — subscribes and swaps a
   `volatile LeaderboardSnapshot` reference on each message. Reference assignment is atomic in .NET;
   `volatile` guarantees other threads see the new one.
3. The controller returns that field. **O(1), wait-free, zero network hops, zero grain calls.**

Read throughput is therefore independent of the grain entirely and scales with pod count. This is a
fix, not a mitigation: the bottleneck is removed rather than made rarer.

**What invalidates it: nothing.** That is the point, and it is why there is no cache stampede. A
stampede needs an expiry that triggers recomputation — many readers finding an empty cache and all
recomputing at once. Here the snapshot is *always present and always pre-computed*; it is replaced
by a push, never invalidated by a read. There is no code path in which serving a request causes work.

Two supporting details:

- **Priming.** On startup each pod pulls once with `GetTopAsync()`, so it is never blank waiting for
  the first broadcast. That is one call per pod *lifetime*, not per request.
- **Publish-on-change.** A quiet cluster broadcasts nothing, so idle pods cost nothing.
- **Monotonic application.** The cache ignores a snapshot older than the one it holds, so
  out-of-order delivery cannot move a pod backwards in time.

`Both_pods_converge_on_the_same_snapshot` asserts what the model claims: two pods reach the *truth*
(not merely agree with each other, which agreeing on stale data would also satisfy) and land on the
same broadcast.

## Session expiry policy

- **One session per device.** `IDeviceGrain`, keyed by `deviceId`, is the login gate. Its single
  activation is the atomic primitive `ConcurrentDictionary.TryAdd` used to be — except it now holds
  across the whole cluster. Simultaneous logins serialize into separate turns; the second sees the
  live session and the API returns **409**.
- **Second device supersedes.** A new-device login binds a fresh grant on the player grain, which
  returns the displaced one so the caller can release that device. The prior token stops validating
  immediately. Rationale: it preserves "one online session per player", which is what keeps the gift
  online-check a single unambiguous read. Rejecting instead would be defensible; supersede was
  chosen for UX and a cleaner predicate.
- **Sliding TTL of 3 minutes.** Any authenticated request slides the expiry. A device idle for one
  minute keeps its session (TTL > 60 s, as required); a crashed device stops sliding and lapses
  after 3 minutes rather than being locked out forever.
- **Expiry is lazy** — evaluated on read, never swept. A timer per player is the same anti-pattern
  the idempotency ledger avoids. `CollectionAge` reclaims the memory of anything genuinely idle.
- **Token format `{playerId}.{guid}`.** The auth filter parses the prefix locally and makes exactly
  **one** grain call, `ValidateAndSlideSessionAsync`, which validates and slides in a single turn. No
  token-lookup grain, no directory scan.
- **`[AlwaysInterleave]` on session methods** so authentication never queues behind a slow score
  transaction on a hot player. Safe because those methods touch only plain session fields and
  contain no `await`.

That last point is load-bearing in a way worth flagging: `ReleaseAsync` **must** interleave. Two
devices can supersede each other simultaneously, and if release queued behind that device's own
in-flight login, the two grains would wait on each other — a real deadlock. This was verified by
removing the attribute and watching
`Devices_superseding_each_other_simultaneously_do_not_deadlock` hang, then restoring it.

## Assumptions

- **Player and device IDs are opaque strings.** No ordering assumption is needed anywhere, since
  nothing is lock-ordered.
- **A session token is a bearer credential, not real auth.** Per the brief. It authenticates exactly
  one player, and that player may act only on their own resources (hence the `403`).
- **Clients retry within seconds**, so a 10-minute idempotency TTL vastly exceeds the replay window.
  A replay arriving after eviction is treated as new — accepted, and bounded memory is the reason.
- **A terminal outcome is terminal.** A rejected gift replays as the same rejection even if the
  condition has since cleared. A client wanting a genuinely new attempt uses a new `requestId`.
- **Transaction aborts under contention are expected, not exceptional.** The retry budget (8
  attempts, jittered) and the resulting `503` are part of the contract.
- **State is lost on restart.** Memory providers only, per the "no database" constraint. Swapping in
  `AddRedisGrainStorage` is a configuration change, not a code change, because no grain sees the
  provider.
- **The leaderboard is rebuildable**, so its projection is not persisted and its broadcast timer is
  a grain timer rather than a durable reminder.
- **`N = 100`** for Top-N, configurable. The grain holds all players regardless.

### Honest note on Orleans for this brief

For a genuinely single-process service, the in-memory design with a `ConcurrentDictionary` and
per-player locks would have been cheaper and equally correct. Orleans is substantially more
machinery. What it buys is that the concurrency guarantees are **inherited from the runtime rather
than hand-written** — no lock ordering to get right, no atomic-check-then-act to argue about — and
that the same design survives horizontal scale-out, which the locking version does not: a double
lock cannot span two silos, so gifting would have had to be redesigned rather than rehosted.

---

## Local environment note

The shipped projects target `net8.0` per the assignment. This machine has only the .NET 9/10
runtimes installed, so `Directory.Build.props` sets `<RollForward>LatestMajor</RollForward>`: the
assemblies are still net8.0, they just run on the newest installed runtime. Remove it if you have
the .NET 8 runtime and prefer exact-version behaviour.

**The test project is the one exception: it targets `net10.0`.** `WebApplicationFactory` hosts the
API in-process, so its test host has to match the ASP.NET Core shared framework the process actually
runs on — which, given the roll-forward above, is 10.0.x. An 8.0.x test host against the 10.0 runtime
throws `PipeWriter 'ResponseBodyPipeWriter' does not implement PipeWriter.UnflushedBytes` on every
JSON response, because `System.Text.Json` 10 requires an API the older host does not have. Nothing
shipped is affected — `src/` is still net8.0, and a net10.0 test project consumes those net8.0
libraries normally. On a machine with the .NET 8 runtime installed, the test project can go back to
net8.0 with an 8.0.x `Microsoft.AspNetCore.Mvc.Testing`.
