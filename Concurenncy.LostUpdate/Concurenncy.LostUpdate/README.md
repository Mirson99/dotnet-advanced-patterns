# Module 01: Concurrency Control & Data Consistency in .NET

Demonstrating the **Lost Update** anomaly under concurrent load, along with production-grade mitigation strategies using **Optimistic Concurrency (EF Core Concurrency Tokens)** and **Pessimistic Locking (SQL Server UPDLOCK/ROWLOCK)**.

---

## 1. Problem Statement: The Lost Update Anomaly

When multiple threads or distributed instances handle requests against the same database entity concurrently, standard read-modify-write patterns break down:

1. **Thread A** reads `BankAccount.Amount = 100`.
2. **Thread B** reads `BankAccount.Amount = 100` before Thread A commits.
3. Both threads validate business rules (`Amount >= 10`) in application memory.
4. Both deduct 10 and execute `UPDATE BankAccounts SET Amount = 90 WHERE Id = 1`.
5. **Outcome:** Two successful withdrawals took place, but the account balance decremented only once (90 instead of 80). Business invariants are violated, resulting in financial loss.

---

## 2. Solutions Implemented

### Strategy A: Optimistic Concurrency Control (OCC)
Best suited for **low-to-moderate contention** scenarios where conflicts are rare.

* **Mechanism:**
    * Added a `byte[] RowVersion` property configured via `.IsRowVersion()` in EF Core.
    * SQL Server maps this to the `ROWVERSION` (8-byte binary counter) data type, auto-incremented by the database engine on every row mutation.
    * EF Core automatically appends the tracked version to the `WHERE` clause:
      ```sql
      UPDATE [BankAccounts] 
      SET [Amount] = @p0 
      WHERE [Id] = @p1 AND [RowVersion] = @p2;
      ```
* **Conflict Resolution:**
    * When a concurrent write increments the version, subsequent writes affect 0 rows.
    * EF Core intercepts `@@ROWCOUNT == 0` and throws a `DbUpdateConcurrencyException`.
    * The application catches the exception and executes a retry loop with **exponential backoff and jitter** to reload fresh state and re-evaluate business rules.

---

### Strategy B: Pessimistic Concurrency Control (PCC)
Required for **high-contention / high-throughput** hotspots (e.g., flash sales, single-balance ticketing) where repeated optimistic retries cause severe CPU and network thrashing.

* **Mechanism:**
    * Explicitly executed inside a transactional scope (`BeginTransactionAsync`).
    * Acquired an update lock directly using SQL Server table hints via `FromSqlInterpolated`:
      ```sql
      SELECT * FROM BankAccounts WITH (UPDLOCK, ROWLOCK) WHERE Id = @Id;
      ```
    * `UPDLOCK`: Signals intent to update, serializing incoming updates while permitting uncommitted/dirty reads if allowed by isolation levels.
    * `ROWLOCK`: Prevents lock escalation to page- or table-level locks.
* **Outcome:**
    * Competing threads block at the database level until the holding transaction issues `COMMIT` or `ROLLBACK`.
    * Zero concurrency exceptions and zero retry loops needed at the application layer.

---

## 3. Trade-offs & Deadlock Prevention

| Metric / Aspect | Optimistic (`RowVersion`) | Pessimistic (`UPDLOCK`) |
| :--- | :--- | :--- |
| **Throughput (Low Contention)** | High (Zero locking overhead) | Lower (Lock management overhead) |
| **Behavior Under High Contention** | Degrades (CPU spikes, retry storms) | Predictable (Queued execution) |
| **Locking Overhead** | None (Lock-free at read time) | Database connection held open longer |
| **Deadlock Risk** | Low (Occurs only at write time) | **High** (Lock acquisition order conflicts) |

### Preventing Deadlocks in Pessimistic Workflows:
Deadlocks emerge when concurrent transactions acquire locks across multiple resources in reverse orders (e.g., Thread 1 locks A then B; Thread 2 locks B then A).

* **Enforced Lock Ordering:** Always sort resources deterministically (e.g., by ascending Primary Key / UUID) before requesting pessimistic locks:
  ```csharp
  var firstId = Math.Min(sourceId, targetId);
  var secondId = Math.Max(sourceId, targetId);

  // Lock strictly in deterministic order:
  await LockAccountAsync(firstId);
  await LockAccountAsync(secondId);
  ```

Keep Transactions Lean: Never execute remote HTTP calls, disk I/O, or heavy computational logic inside an active transactional lock boundary.

Transient Fault Handling: Configure resilience policies (e.g., Polly) to detect SQL error code 1205 (Deadlock victim) and retry with randomized delay.