# 🎮 Player Service - Executive Summary

This project implements a distributed backend service for managing player data, designed to support high-concurrency environments. The system is built using a microservices architecture with **C# .NET 8** and the **Microsoft Orleans** Virtual Actor framework.

The primary goal was to solve complex concurrency challenges in managing user state—such as atomic score updates, gift transfers, robust idempotency mechanisms, and leaderboard synchronization—in a highly scalable, fault-tolerant, and maintainable manner.

---

## 🚀 Architecture Evolution: From Monolith to Orleans

**The Thought Process:**
Initially, my approach was to build a standard, straightforward API managing everything In-Memory. However, as I drafted the design document, I realized that the constraint of having no database or external cache (like Redis) seemingly forced a **Monolithic** architecture. I began designing complex mechanisms for thread locks, local concurrency management, and an in-memory leaderboard.

Feeling that a monolithic approach was counter-intuitive for modern environments, I proactively reached out to the interviewer for clarification. Once I confirmed the expectation was indeed a **Microservices** architecture, a major challenge arose: **How do I manage distributed state without external storage?** A player's API requests could hit any pod in the cluster, each only aware of its own local memory.

This challenge led me to pivot and adopt **Microsoft Orleans**. This framework fundamentally shifted the architecture, providing an elegant solution. The Virtual Actor model allows microservices to seamlessly communicate and share internal state, inherently solving the complex challenges of request routing, state synchronization, and concurrency across a distributed environment without needing an external database.

---

## ⚖️ Policy Decisions

To meet the system constraints, several key business and technical policies were established:

1. **Session Expiry & "Online" Status:** A player's session has a Time-To-Live (TTL) of **3 minutes**. Every action performed by the player resets this TTL. This decision stems from the assignment's constraints: there is no explicit `logout` endpoint and no reliable way to detect when a client disconnects. Thus, TTL serves as a lightweight, connectionless mechanism to infer who is currently "online" to receive gifts.
2. **Multi-Device Login:** I enforce a strict "One Active Session" policy. If a player logs in from a new device, the previous session token is overwritten and invalidated. The old device will receive unauthorized errors.
3. **Idempotency Bounds:** To prevent unbounded memory growth, the Idempotency Ledger is capped at **10 minutes** or **256 requests (FIFO)** per player. 
   * *Trade-off:* If a severely delayed retry arrives after its record is evicted, it will be processed again. This is a deliberate trade-off prioritizing memory stability over edge-case deduplication in a purely in-memory architecture.
4. **Leaderboard Staleness:** The leaderboard guarantees a maximum staleness of **1 second** under normal load. 
   * *Complexity:* Background updates run in `2 * O(log U)` (updating both the sender and receiver in a SortedSet). Fetching the leaderboard via the API is `O(1)` since it serves a cached, pre-computed view.

---

## 1. Login and Sessions

To manage session lifecycle, device constraints, and connectionless "online" state, I implemented an interaction between two core grains: `DeviceGrain` and `PlayerGrain`.

### The Flow
When a login request arrives, the Auth controller forwards it to the specific `DeviceGrain`.

1. **Preventing Concurrent Logins (Same Device):** 
   The `DeviceGrain` checks if an active token already exists. Because Orleans guarantees single-threaded execution per grain activation, two identical login requests arriving at the exact same millisecond will be processed sequentially. The first request succeeds, and the second is immediately rejected.
2. **Session Binding:**
   If the device is available, it generates a new session token and calls `PlayerGrain.BindSessionAsync()`.
3. **Handling Multi-Device Overrides:**
   The `PlayerGrain` also executes on a single thread. If it detects the player was already bound to *another* device, it accepts the new binding and returns the old session details (`superseded`). The new `DeviceGrain` then explicitly calls `ReleaseAsync()` on the old `DeviceGrain` to aggressively terminate its token. 

This unidirectional, tree-like architecture (Device → Player) avoids deadlocks while ensuring atomic session management.
> 📖 **Read More:** For deeper technical details, see the [Session Expiry Policy](README.md#session-expiry-policy) section in the main README.

---

## 2. Score Updates & Idempotency

A primary challenge is handling heavy concurrent score updates for a single player without encountering "Lost Updates" or applying the same points twice when a client retries a request (using the same `requestId`).

The solution heavily utilizes **Orleans Transactions** combined with the `[Reentrant]` attribute on the `PlayerGrain`:

1. **Atomic Processing & Non-Blocking Design:**
   All updates for a specific player are routed to their unique `PlayerGrain`. While the grain handles requests on a single thread, the `[Reentrant]` attribute allows it to accept incoming requests concurrently without blocking the queue while waiting for I/O.
2. **Transactional Serializability:**
   State changes are managed via `ITransactionalState`. Even though requests are received concurrently, the Orleans transaction manager enforces serializable data access. The logic for idempotency checks and point additions is executed inside a fully synchronous block, acting as a single, uninterrupted atomic operation.
3. **Handling Simultaneous Duplicate Requests:**
   If a client fires two identical requests simultaneously:
   * **The Winning Request:** Enters the atomic block, verifies the `requestId` is new, adds the points, logs the transaction in the ledger, and persists the state.
   * **The Losing Request:** Is automatically rejected by the transaction manager due to a conflict, triggering a transparent, high-speed retry within Orleans. Upon retry, it reads the newly updated state, sees the `requestId` already exists in the ledger, safely skips the point addition, and returns the cached response.
4. **Single Grain Performance Under Load:**
   The ceiling for a sub-second response time is a burst of ~490 requests or a sustained rate of ~450-500/s. This derives directly from a service rate of ~490 transactions/second. A safe operating point is ~400/s; pushing to 450/s reaches ~95% utilization where any variance causes significant queue spikes.

This architecture ensures high throughput, absolutely no lost updates, and robust idempotency without manually managing complex thread locks.
> 📖 **Read More:** Explore the [Orleans Concurrency Model](README.md#the-orleans-concurrency-model-and-why-there-are-no-lost-updates) and [Idempotency Details](README.md#idempotency-two-simultaneous-duplicates-and-how-records-stay-bounded) in the main README.

---

## 3. Gifting & Distributed Transactions

The Gifting endpoint is the most complex part of the system, involving atomic point transfers between two independent `PlayerGrain` instances. I solved four major concurrency and consistency challenges here:

1. **Transaction Atomicity & Preventing Negative Balances:**
   The transfer is wrapped in an Orleans Transaction (`ITransactionManager.RunTransaction`). Inside this transaction, the sender's grain checks if the balance is sufficient and debits it in a single statement. If the balance is too low, the transaction aborts. This guarantees that total points in the system are strictly conserved, and a balance can never drop below zero, even under heavy concurrent load.
2. **Deadlock Prevention (External Transaction Coordination):**
   Instead of having Player A's grain call Player B's grain directly (which risks a circular deadlock if B gifts A simultaneously), I elevated the transaction coordination to the API layer (`GiftService`). The service acts as the orchestrator, sorts the `senderId` and `recipientId` alphabetically, and sequentially enlists the grains. This completely avoids grain-to-grain circular calls and guarantees locks are acquired in the exact same alphabetical order every time.
3. **The "Online" Guarantee:**
   The requirement states a recipient must be online at the exact moment of the gift. The recipient's session TTL is verified *inside* the transaction block during the credit phase. Because it's evaluated synchronously within the commit phase, it guarantees the player is definitively "online" at the exact moment the points are moved.
4. **Robust Idempotency (Retries):**
   If a request is a retry (same `requestId`), it's identified immediately during the debit phase. The transaction deliberately aborts and throws a specific replay exception. The service catches this exception and returns the *original* cached outcome from the ledger. This ensures that even if a recipient goes offline during a network retry, the retry correctly returns the original "Success" result instead of failing retroactively.
5. **Conservation Assert (Tests):**
   Transaction atomicity is mathematically proven in the E2E test `Many_parallel_gifts_across_random_pairs_conserve_points_and_never_go_negative`. The test is based on three steps:
   * **Initialization:** Connect 8 players with a Seed Balance and calculate the "total points" in the system.
   * **Concurrent Burst:** Fire 60 simultaneous random gift transfer requests between the players.
   * **Assertion:** Once transactions settle, recalculate all balances and assert that the "current total" is exactly equal to the "initial total", and no balance dropped below zero. This proves no points were lost or duplicated due to race conditions.

> 📖 **Read More:** Deep dive into the [Lock Ordering rules](README.md#lock-ordering-not-needed-at-the-activation-layer-required-at-the-state-layer) in the main README.

---

## 4. Scalable Leaderboard (Push-Cache)

The assignment required preventing full sorting on every request, handling paired updates (gifts), explicitly defining a staleness guarantee, and preventing cache stampedes.

I solved these challenges using an inverted **Push-Cache** architecture:

1. **Data Structure & Complexity:** The data is managed by a single `LeaderboardGrain` maintaining a `SortedSet`. Adding or updating a score requires an `O(log N)` search and insert. Fetching data from the server is done directly from the local cache in `O(1)` complexity.
2. **Paired Updates:** Gift transactions broadcast the absolute (updated) scores of both the sender and the recipient simultaneously via Orleans Streams. The Leaderboard Grain listens to these events and updates its internal structure consistently without needing delta calculations.
3. **Staleness Guarantee:** The system guarantees a maximum staleness of up to 1 second. A client fetching the leaderboard immediately after updating a score might not see the change instantly. This delay (Eventual Consistency) is perfectly acceptable in multiplayer games and provides excellent user experience.
4. **Cache Stampede Prevention:** Instead of a cache expiring and causing thousands of clients to simultaneously trigger a recalculation against the central Grain, the system uses a Push approach. A timer in the `LeaderboardGrain` pushes the updated Top-N snapshot to all servers in the cluster every second. When a client calls `GET /leaderboard`, it simply reads a local memory variable. There is no active "cache expiration" requiring computation, thus guaranteeing no bottlenecks due to read load.
5. **Crash Recovery Trade-off:** Because this system operates entirely In-Memory without any external storage service (like a Database or Redis), there is an inherent trade-off: if the `LeaderboardGrain` or the Pod hosting it crashes, the entire leaderboard history is lost. The Grain will resurrect and start building the table from scratch, based solely on new score updates streaming in. This is an unavoidable architectural compromise given the assignment constraints.

> 📖 **Read More:** Deep dive into the [Leaderboard Structure](README.md#leaderboard-structure-complexity-and-staleness) and [Push-Cache Strategy](README.md#the-push-cache-strategy-and-what-invalidates-it) in the main README.

---

## 5. Behavior Under Load

The architecture was designed to handle thousands of requests per second, accommodating heavily skewed traffic where certain "Hot Players" receive most of the load:

1. **No Global Lock & Isolation:** Thanks to the Virtual Actors architecture, each `PlayerGrain` operates as a completely independent entity with its own message queue. There is no global lock in the system. Reads or writes to Player A will never queue or block behind Player B. A "hot" player will, at most, slow down its own request processing, while the rest of the system responds with zero interference.
2. **Retry Policy & Status Codes:** The system guides clients on when it is safe to retry via HTTP status codes:
   * **400 (Bad Request) / 401 (Unauthorized) / 404 (Not Found):** Logical errors (invalid session, non-existent player). **Do not retry**.
   * **409 (Conflict):** Transactional conflict or temporary insufficient funds. **Safe to retry**.
   * **503 (Service Unavailable):** Localized overload (queue full for a specific grain). **Safe to retry** (Exponential Backoff recommended).
   * **200 (OK):** Thanks to our strict Idempotency mechanism, retrying the exact same request will always return 200 with the original data, so it is **completely safe** to retry writes.
3. **Caching Strategy:** 
   * **Leaderboard:** Managed via Push-Cache in local memory, saving 100% of processing time on reads.
   * **Sessions:** Session validation is performed without hitting a DB, querying directly against the Grains located in the fast memory of the cluster.

---

## 6. Deliverables

The system provides a complete and proven response to all required deliverables:

### Runnable Code
The system can be run in two simple ways (in both, the Orleans Dashboard is always available at [http://localhost:5080/dashboard](http://localhost:5080/dashboard)):
* **Quick Run:** Run `dotnet run` from the `src/PlayerService.Api` folder. The system will start immediately.
* **Full Observability Run:** Recommended to run via Docker using `docker-compose up` from the root directory. This spins up the API alongside advanced monitoring systems available immediately:
  * Traces via **Jaeger** at [http://localhost:16687](http://localhost:16687) (select the `player-service`).
  * Metrics via **Grafana** at [http://localhost:3501](http://localhost:3501) (includes a pre-built dashboard).

### Test Harness
To prove the handling of high concurrency, over 50 tests were written, including a Black-box E2E suite running against a live service, covering every required scenario:
* **N parallel score posts (no loss):** Proven by `N_parallel_score_posts_add_up_exactly`.
* **Same `requestId` fired 100 times:** Proven by `The_same_requestId_fired_100_times_in_parallel_is_applied_once` (applies once, all get 200).
* **Random parallel gifts (points conserved, no negative):** Proven by `Many_parallel_gifts_across_random_pairs_conserve_points_and_never_go_negative`.
* **Tight loop mutual gifting (`p1<->p2`) no deadlock:** Proven by `p1_and_p2_gifting_each_other_in_a_tight_loop_do_not_deadlock`.
* **Replayed gift after recipient disconnects:** Proven by `A_replayed_gift_after_the_recipient_went_offline_returns_the_original_result`.
* **Gift to offline player:** Rejected with no point transfer in `A_gift_to_an_offline_player_is_rejected_and_moves_no_points`.
* **Leaderboard correctness after a burst:** Explicitly tested in the `E2E.LeaderboardTests` class.
* **Concurrent duplicate logins on same device:** Rejected in `A_duplicate_deviceId_login_is_rejected`.

> 📖 **Read More:** For the full mapping table and explanation of the dual-layer testing architecture, see the [Tests](README.md#tests) section in the main document.

---

## 7. Performance, Observability & Logs

The system is equipped with a deep Observability layer based on OpenTelemetry, enabling precise tracking of performance and issues in a distributed environment:

1. **Metrics in Grafana:** The system exposes custom metrics (such as response times, rejected gifts, or leaderboard load) that are scraped by Prometheus and displayed on a dedicated Grafana dashboard. This provides real-time visibility into system health and bottlenecks.
2. **Distributed Traces in Jaeger:** Every HTTP request is assigned a unique Trace ID that follows it throughout its lifecycle across the cluster, including internal hops between Grains. For instance, if a gift transaction aborts due to contention, the Jaeger trace explicitly shows where the conflict occurred and how quickly the internal retry was executed.
3. **Alert Logs (`Alert-Logs`):** Beyond metrics and traces, the system utilizes Serilog to write critical alerts and errors to physical files. These logs are stored in the `Alert-Logs` directory inside `src/PlayerService.Api` (and volume-mapped outwards in Docker). This allows third-party tools or administrators to be immediately notified of severe exceptions or unexpected behaviors outside the normal flow.

### Hot Player Benchmark Report
To demonstrate the system's capabilities under extreme load, a benchmark was executed simulating massive traffic directed at specific "Hot Players" (concurrent scores and gifts). The results of this test were gathered and summarized in a detailed HTML report, showcasing response times, success rates, and the behavior of the transaction manager under immense pressure.

> 📊 **View the Benchmark Report:** Click the link below to open the detailed HTML report directly from the project files:
> [hot-player-benchmark-report.html](docs/hot-player-benchmark-report.html)
