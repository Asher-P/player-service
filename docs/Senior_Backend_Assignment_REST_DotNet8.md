# Backend Home Assignment
Player service — .NET 8 Web API

## Context
You are building a backend service for a mobile puzzle game. It manages player sessions, scores, gifting between players, and a leaderboard.

The thing we care about most: **mobile clients are unreliable and chatty**. They retry on timeout, they send the same request twice, they go to sleep mid-flight and deliver a request thirty seconds late, and several of them can be updating the same player at the same moment. The API has no memory of what a client did a second ago except what you store. Most of the requirements below are about that.

.NET 8 Web API, in-memory storage, no database. Every player starts with **1000 points** on first login.

## API

| Endpoint | Purpose |
| --- | --- |
| `POST /login` | Body `{ "playerId", "deviceId" }`. Returns a session token. |
| `GET /players/{playerId}/stats` | Score, gifts sent, gifts received, last active timestamp. |
| `POST /players/{playerId}/stats/score` | Body `{ "points": 100, "requestId": "{guid}" }`. Adds points. |
| `POST /players/{playerId}/gifts` | Body `{ "toPlayerId": "p_2", "points": 50, "requestId": "{guid}" }`. Gifts points to another player, **only if that player is currently online**. |
| `GET /leaderboard` | Top players by score. |

Requests other than `POST /login` carry the session token. You may extend any response body with whatever fields your design needs.

## Requirements

### 1. Login and sessions
* **One active session per `deviceId`**. A login for a device that already has an active session is rejected.
* There is no connection to tell you the client is gone. A device that crashes must not be locked out forever, but a device that is simply idle for a minute must not lose its session either. Decide the policy and defend it.
* Two identical logins arriving at the same instant must not both succeed.
* Requests carrying an expired or superseded token are rejected.
* Document your policy for the same `playerId` logging in from a second device.
* Sessions are also the definition of "online" used by the gift endpoint, so expiry has to be something the rest of the service can ask about cheaply and correctly.

### 2. Score updates
* Assume many concurrent requests adding points to the **same** player. Updates must be atomic — **no lost updates**.
* **Idempotent per `{playerId, requestId}`**. A repeat must not add the points again; it returns the current stats.
* The duplicate does not politely wait its turn. A client that times out retries immediately, so **two copies of the same request can be in flight simultaneously**. Both must return the same answer and the points must be applied exactly once. Look closely at whether your idempotency check is itself atomic.
* Your idempotency records cannot grow forever. Bound them, and say what happens when a replay arrives after its record is gone.

### 3. Gifting
This is the core of the assignment. Assume heavy concurrent gifting across random player pairs.
* Debit the sender, credit the recipient. The pair must be **atomic**: no interleaving may ever leave points debited but not credited, or credited twice. **Total points in the system are conserved**.
* **A balance must never go negative**. The check and the debit have to be one atomic step — two concurrent gifts from a player holding 100 points, each sending 100, must not both succeed.
* **The recipient must be online at the time of the gift**, otherwise the request is rejected. Note that their session can expire in the gap between your check and your write. Say what your guarantee actually is and make the code match it — we are not looking for a check that is already stale by the time it is used.
* `p_1 -> p_2` and `p_2 -> p_1` arriving at the same time **must not deadlock**, nor must a burst of gifts across many overlapping pairs.
* **Idempotent per `{senderId, requestId}`**. A retry must not gift twice. Careful: a retry must return the *original* outcome — it must not be re-evaluated against current state and start failing because the recipient has since gone offline or because the sender's balance has changed.
* Reject self-gifting, unknown recipients and non-positive amounts, with status codes that tell the client whether retrying is worth it.
* Say in the README how you would assert conservation of points in a test.

### 4. Leaderboard
* Returns the top N by score. It must **not sort all players on every request**. Explain your data structure and its complexity for both reads and score changes.
* Remember that a single gift moves two players at once, so the structure has to absorb paired updates, not just increments.
* State your staleness guarantee explicitly. Right after a player's score changes, must their next `GET /leaderboard` reflect it? How stale can the answer be, and why is that acceptable?
* If you cache it: what invalidates the cache, and what happens when the cached entry expires at the moment several thousand requests are in flight? Make sure the answer isn't "all of them recompute it".

### 5. Behaviour under load
* Thousands of requests per second, heavily skewed — a small number of players receive most of the traffic.
* A single hot player must not serialize the whole service. No global lock across all players.
* Reads of one player must not queue behind writes to a different one.
* Say which responses are safe for a client to retry and which are not, and make your status codes reflect that.
* Add caching wherever you think it earns its keep.

## Deliverables
* Clean, runnable code — `dotnet run` and it works.
* Tests or a small harness that demonstrate, under real concurrency:
  * N parallel score posts — the total is exactly right, nothing lost;
  * the same `requestId` fired 100 times in parallel — applied once, 100 consistent responses;
  * many parallel gifts across random pairs — total points conserved, no balance ever negative;
  * `p_1 <-> p_2` gifting each other in a tight loop from both sides — no deadlock;
  * a replayed gift `requestId` after the recipient has gone offline — returns the original result, no second transfer;
  * a gift to an offline player — rejected, and no points moved;
  * the leaderboard is correct after a burst of concurrent scores and gifts;
  * a duplicate `deviceId` login is rejected.
* A brief README covering: your concurrency model and why there are no lost updates or deadlocks; your lock ordering, if you lock; how idempotency works, including two simultaneous duplicates, and how the records stay bounded; the exact guarantee you provide on the recipient-is-online check; your leaderboard structure, complexity and staleness guarantee; your caching and invalidation strategy; your session expiry policy; and any assumptions.

## Out of scope
No database, no real authentication (the token can be a GUID), no UI, no deployment, no rate limiting unless you want it.

## Optional — only if you have time
* Per-player rate limiting on gifts.
* A benchmark showing throughput and latency under concurrent load on a hot player.
* Metrics or logging you would actually want in production for this service.

> **Time:** we expect around 4–6 hours. Don't gold-plate it. If you deliberately skip something, write that in the README — we would rather see clear reasoning about a trade-off than an unfinished attempt at everything.
