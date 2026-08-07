# 🎮 Player Service - Executive Summary

This project implements a distributed backend service for managing player data, designed to support high-concurrency environments. The system is built using a microservices architecture with **C# .NET 8** and the **Microsoft Orleans** Virtual Actor framework.

The primary goal was to solve complex concurrency challenges in managing user state—such as atomic score updates, gift transfers, robust idempotency mechanisms, and leaderboard synchronization—in a highly scalable, fault-tolerant, and maintainable manner.

---

## 🚀 Architecture Evolution: From Monolith to Orleans

**The Thought Process:**
Initially, my approach was to build a standard, straightforward API managing everything In-Memory. However, as I drafted the design document, I realized that the constraint of having no database or external cache (like Redis) seemingly forced a **Monolithic** architecture. I began designing complex mechanisms for thread locks, local concurrency management, and an in-memory leaderboard.

Feeling that a monolithic approach was counter-intuitive for modern environments, I proactively reached out to the interviewer for clarification. Once I confirmed the expectation was indeed a **Microservices** architecture, a major challenge arose: **How do we manage distributed state without external storage?** A player's API requests could hit any pod in the cluster, each only aware of its own local memory.

This challenge led me to pivot and adopt **Microsoft Orleans**. This framework fundamentally shifted the architecture, providing an elegant solution. The Virtual Actor model allows microservices to seamlessly communicate and share internal state, inherently solving the complex challenges of request routing, state synchronization, and concurrency across a distributed environment without needing an external database.

---

## ⚖️ Policy Decisions

To meet the system constraints, several key business and technical policies were established:

1. **Session Expiry & "Online" Status:** A player's session has a Time-To-Live (TTL) of **3 minutes**. Every action performed by the player resets this TTL. This decision stems from the assignment's constraints: there is no explicit `logout` endpoint and no reliable way to detect when a client disconnects. Thus, TTL serves as a lightweight, connectionless mechanism to infer who is currently "online" to receive gifts.
2. **Multi-Device Login:** We enforce a strict "One Active Session" policy. If a player logs in from a new device, the previous session token is overwritten and invalidated. The old device will receive unauthorized errors.
3. **Idempotency Bounds:** To prevent unbounded memory growth, the Idempotency Ledger is capped at **10 minutes** or **256 requests (FIFO)** per player. 
   * *Trade-off:* If a severely delayed retry arrives after its record is evicted, it will be processed again. This is a deliberate trade-off prioritizing memory stability over edge-case deduplication in a purely in-memory architecture.
4. **Leaderboard Staleness:** The leaderboard guarantees a maximum staleness of **1 second** under normal load. 
   * *Complexity:* Background updates run in `2 * O(log U)` (updating both the sender and receiver in a SortedSet). Fetching the leaderboard via the API is `O(1)` since it serves a cached, pre-computed view.

---

## 1. Login and Sessions

To manage session lifecycle, device constraints, and connectionless "online" state, we implemented an interaction between two core grains: `DeviceGrain` and `PlayerGrain`.

### The Flow
When a login request arrives, the Auth controller forwards it to the specific `DeviceGrain`.

1. **Preventing Concurrent Logins (Same Device):** 
   The `DeviceGrain` checks if an active token already exists. Because Orleans guarantees single-threaded execution per grain activation, two identical login requests arriving at the exact same millisecond will be processed sequentially. The first request succeeds, and the second is immediately rejected.
2. **Session Binding:**
   If the device is available, it generates a new session token and calls `PlayerGrain.BindSessionAsync()`.
3. **Handling Multi-Device Overrides:**
   The `PlayerGrain` also executes on a single thread. If it detects the player was already bound to *another* device, it accepts the new binding and returns the old session details (`superseded`). The new `DeviceGrain` then explicitly calls `ReleaseAsync()` on the old `DeviceGrain` to aggressively terminate its token. 

This unidirectional, tree-like architecture (Device → Player) avoids deadlocks while ensuring atomic session management.
