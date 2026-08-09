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

> 📖 **Read More:** Deep dive into the [Lock Ordering rules](README.md#lock-ordering-not-needed-at-the-activation-layer-required-at-the-state-layer) in the main README.

---

## 4. Scalable Leaderboard (Push-Cache)

The requirement was to provide an always-available O(1) read endpoint for the leaderboard with a maximum staleness of 1 second, entirely without an external database.

I solved this by implementing an inverted **Push-Cache** architecture:

1. **Centralized State (Single Writer):** A single `LeaderboardGrain` acts as the sole writer in the cluster, maintaining a `SortedSet`. Point updates are asynchronously streamed to it as absolute values (not deltas, preserving idempotency).
2. **Background Broadcasting (Push):** Every second, a timer inside the grain checks for changes. If the leaderboard mutated, it broadcasts the updated Top-N `LeaderboardSnapshot` via an Orleans Stream to all active pods.
3. **Zero-Wait Reads (O(1)):** Every pod runs a background service (`LeaderboardCache`) that subscribes to this broadcast stream. Upon receiving a new snapshot, it simply overwrites a `volatile` reference in its local memory.
4. **Preventing Cache Stampedes:** When a client calls `GET /leaderboard`, the controller immediately serves the `volatile` reference from local memory. There are zero network hops, zero grain calls, and zero computations during the HTTP request. The cache is updated entirely via background pushes, so a traffic spike will never trigger an expensive recalculation (cache stampede).

> 📖 **Read More:** Deep dive into the [Leaderboard Structure](README.md#leaderboard-structure-complexity-and-staleness) and [Push-Cache Strategy](README.md#the-push-cache-strategy-and-what-invalidates-it) in the main README.
