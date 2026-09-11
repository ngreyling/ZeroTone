# Agent / review notes (ZeroTone)

Guidance for automated and human code review of this repository.

## Intentional non-goals (do not flag as omissions)

### Tracked drain with bounded dual re-arm (no orphan pile-up)

**Intentional. Do not reintroduce start-and-forget orphans or unbounded dual stacking.**

**Where:** `KeepAliveAudioService` control thread (`TryReconcileOnce` → drain slot `_drainingWorker` / `_drainingCts` → `TryJoinDrain`). Constants: `WorkerJoinTimeout` (3s first join after demote), `WorkerJoinPollQuantum` (100ms while the app is live), `WorkerJoinDisposeDrainTimeout` (2s first drain wait on `Dispose`), `ControlShutdownTimeout` (5s on `Dispose`).

**Policy:**

1. On stop/replace: cancel the registered worker. **Demote into the drain slot only when the drain is free** (serial). If the drain is already occupied, keep the cancelled worker registered until the drain clears, then demote (at most **two** live keep-alive threads: one registered + one draining).
2. Join the drain with a bound (`WorkerJoinTimeout` after demote; poll quantum while live). Control thread only — UI stays non-blocking.
3. If join **succeeds**: dispose CTS, clear drain, clear ambient `PreviousSessionClosing` (see **Drain ambient vs other LastIssue** below).
4. If join **times out** and the drain holds that generation: **track it** (do not forget). Publish ambient `LastIssue` **“A previous session is still closing...”**. If desire is true and the registered slot is empty, **may start a new worker** (temporary dual — fast re-arm).
5. Further replaces while the drain is still live: **do not** start a third generation; poll-join until the drain clears, then demote/start once at the **latest** epoch.
6. No `Thread.Abort`. **While the app is live, never drop tracking** of a timed-out drain. On **`Dispose` only**, a timed-out join may **drop tracking** so shutdown can still join the other generation:
   - First drain wait is **`WorkerJoinDisposeDrainTimeout` (2s)** — not the 100ms live poll.
   - Then demote the registered worker (if any) and join with **`WorkerJoinTimeout` (3s)**.
   - Each of those two joins may abandon if still wedged. **Process exit may leave up to two short-lived tails; never a third.** `ControlShutdownTimeout` stays **5s** (2s + 3s; a little overhead may clip the second join).
   - Desire is already off; Dispose never starts a new worker. This is not live dual re-arm.
   - **Headless linger after chrome teardown is accepted.** Interactive Exit / Close hides the tray and closes the window first (`PrepareToExit`); `Program.Main` then `Dispose`s audio and joins for up to 5s. Task Manager may still show `ZeroTone.exe` with no window and no tray. Healthy teardown is fast; 5s is the wedged ceiling. Do not join before hiding the tray (that would freeze Exit). Do not skip `ControlShutdownTimeout` to make Task Manager instant.

**Drain ambient vs other LastIssue (intentional priority):**

While the drain worker is **still alive**, control publishes `PreviousSessionClosing` as `LastIssue` (**drain-first**). Lifecycle/teardown is the actionable bottleneck (dual re-arm or wait-at-cap); concurrent open/stream failures or mute on the new generation are secondary until the slot frees. **Do not** flag “drain ambient overwrites open/stream `LastIssue` while drain is live” as a bug.

When publishing drain ambient would displace a different `LastIssue`, that issue is **stashed** (`_issueDeferredUnderDrain`). When the drain **clears** (successful join or dispose abandon of previous-session ambient):

- If still **engaged** and phase is **Starting** or **Reconnecting**, restore the stashed issue (avoids a blank tooltip until the next supervisor `ReportIssue`).
- Otherwise clear (healthy dual recovery, Stop, etc.). Full `ClearLastIssue` always drops the stash.

Mute sampling already defers while drain is live (`SampleSessionAttenuation`); open/stream may still `ReportIssue` and then be re-hidden by drain ambient until the stash restore on clear.

**Why this is intentional for ZeroTone:**

- Fast re-arm on the first sticky teardown (device switch / resume) still wins over waiting forever for wedged COM.
- Unbounded orphan accumulation under Bluetooth flap is **not** acceptable — the drain registry + cap fix that class of bug.
- Silence dual is low impact for one overlap; stacking many sessions is not.
- At process **exit**, joining the current session after a real try on the old drain beats hanging forever or pretending only one wedged generation can remain. Up to two brief post-exit tails is the same cap as live keep-alive (registered + drain). Chrome is already gone; a headless process for up to 5s is that join, not a leaked instance.
- Ambient previous-session text keeps the UI honest during dual or wait-at-cap; stash/restore keeps reconnect reasons after the drain ends.

**Do not recommend** as routine cleanup: start-and-forget after join timeout **while live**; multi-orphan lists without a cap; `Thread.Abort`; unbounded UI-thread joins; strict single-flight that always delays first re-arm; showing open/stream failure *instead of* drain ambient while the drain is still live; enforcing “exactly one Dispose tail” by skipping the current-session join; joining the control thread **before** hiding the tray / closing the window; skipping or shortening `ControlShutdownTimeout` so Task Manager clears instantly — unless revisit criteria below are met.

**Revisit only if** one or more of these become true:

1. Real reports that even one dual overlap causes double mixer / exclusive thrash worth always waiting (strict single-flight).
2. Field evidence the cap of two is still too high under pathology.
3. Product gains non-silence or multi-endpoint keep-alive where any dual is more harmful.
4. A cleaner Dispose/exit story requires longer shutdown waits.

If revisited, prefer documented single-flight deferred start or a different cap, with UI staying non-blocking. Update this section, `KeepAliveAudioService` comments, and README status notes together.

### Control-thread unexpected exit self-heals (respawn + wake)

**Intentional. Do not flag as missing fail-closed controller teardown or recommend clearing desire on control-thread death.**

**Where:** `KeepAliveAudioService.ControlMain` `finally`.

**What happens:** If the control thread exits while the service is still live (`!_disposed && !_controlExitRequested`), it clears its slot, starts a **successor** controller, and `_controlWake.Set()` so reconcile can run. `Dispose` sets `_controlExitRequested` (and `_disposed`) before joining, so clean shutdown does **not** respawn.

**Why:** Desire true with no controller (e.g. fault mid-replace after detaching the worker) would otherwise strand keep-alive until Start/Stop/RestartIfEngaged. This is **controller** self-heal only — not outer audio-worker respawn after poison-pill worker exit (see that section).

**Do not recommend** as routine cleanup: fail-closed desire clear on control death, unbounded join of the old controller, or conflating this with sticky worker outer-respawn.

### Fail-open when single-instance kernel objects cannot be created

**Accepted design trade-off. Do not flag as a bug or recommend fail-closed startup by default.**

**Where:** `SingleInstanceGuard.TryEnterPrimary` / `TryEnterPrimaryAfterAbandoned` → `CreateUnrestricted()`. Kernel names: `Local\zerotone.SingleInstance` (mutex), `Local\zerotone.Activate` (manual-reset event). Wired from `Program.Main`.

**Normal path (preferred):**

1. Mutex created new → this process is **primary** (exclusivity held).
2. Activate event created → secondary launches `Set` the event; primary restores the window.
3. Mutex already exists → **secondary**: `TryRequestActivate` then exit (or MessageBox if the event cannot be opened).

`Local\` scope is **per Windows logon session** (not `Global\`). Separate sessions correctly get their own primary.

**Partial degradation (still exclusive):** mutex succeeds, activate event create fails → primary runs **without** second-launch IPC. Secondary cannot restore the window and may show “ZeroTone is already running.” Dual process is still prevented.

**Fail-open (no exclusivity):** if creating/opening the **mutex** throws (`UnauthorizedAccessException`, `IOException`, `WaitHandleCannotBeOpenedException`) or the abandoned-mutex recovery path cannot establish ownership, `CreateUnrestricted()` returns a guard with **no** mutex and **no** event. `TryEnterPrimary` still returns **true** — the process **runs the full app**. Every subsequent launch that also fails open can become another full instance (dual tray icons, dual keep-alive, concurrent settings saves).

**Why fail-open is intentional for ZeroTone:**

- Availability of keep-alive beats exclusive ownership when the session object namespace cannot host the mutex (locked-down ACLs, rare namespace failures, hostile/broken environments).
- Keep-alive is mix-format **silence / near-silence**. Dual process is mainly a **UX/support** problem (two tray icons, last-writer settings), not a severe audio-quality failure.
- Fail-closed (“cannot start because single-instance infrastructure failed”) bricks the product for users who cannot fix kernel-object policy.
- Normal double-click-while-running is the **secondary** path (mutex works); fail-open is only the infrastructure-failure path and is expected to be rare on consumer Windows.

**Do not recommend** as routine cleanup: exit if the mutex cannot be created; `Global\` mutexes; process-list scanning as a primary exclusivity mechanism; one-shot warning UI; or “strict single-instance” modes — unless revisit criteria below are met.

**Revisit only if** one or more of these become true:

1. Real reports of double tray icons / dual keep-alive / settings thrash attributable to unrestricted mode (not ordinary secondary-launch bugs).
2. Enterprise or locked-down fleets where mutex create fails often enough that dual instance is a support burden.
3. Product gains non-silence or multi-endpoint behaviour where dual process is more harmful.
4. Explicit product decision that “won’t start” is acceptable when exclusivity objects are unavailable.

If revisited, prefer a **documented** policy change (fail-closed message, or soft notice while still running) over silent process-list heuristics. Update this section, `SingleInstanceGuard` / `Program` comments, and any user-facing notes together.

### Silence mode uses digital zeros, not `AUDCLNT_BUFFERFLAGS_SILENT`

**Intentional. Do not “optimize” Silence to the Silent buffer flag.**

**Where:** `KeepAliveAudioService.WriteFrames` for `AudioType.Silence`. Interop still defines `AudClntBufferFlags.Silent` (`WasapiRenderInterop`) for completeness; the keep-alive success path always releases with `AudClntBufferFlags.None`.

**What Silence does:** `GetBuffer` → fill the packet with mix-format **digital zeros** → `ReleaseBuffer(frames, None)`. Packets are ordinary shared-mode audio content that happens to be digital black.

**What we do not do:** skip the fill (or ignore buffer contents) and call `ReleaseBuffer` with `AUDCLNT_BUFFERFLAGS_SILENT`. That flag tells the **Windows audio engine** to treat the packet as silence regardless of buffer data — a convenience for apps that are idle, not a better definition of keep-alive silence.

**Why zeros + `None` is the product choice:**

- Keep-alive wants continuous, ordinary render activity that looks like playback, not an engine-optimized “this client has no signal” path.
- Equivalence of Silent packets vs zero-filled packets for endpoint power, HDMI/S/PDIF blanking, Bluetooth sleep, etc. is **not** guaranteed in documentation; ZeroTone prefers the boring, transparent path.
- Devices that ignore pure digital silence already have **Inaudible Sound** (near-zero tone). Do not repurpose the Silent flag for that role.
- Clearing ~100 ms of mix-format audio is negligible; the Silent flag’s micro-optimization is irrelevant here.

**Do not recommend** as cleanup: Silence → `AUDCLNT_BUFFERFLAGS_SILENT`, zeros **and** Silent together, or removing the unused enum value solely because it is unused.

**Revisit only if** field evidence shows zero-filled shared-mode packets fail keep-alive where Silent packets succeed (unlikely and must be device-proven), or product requirements change. If revisited, update this section, `WriteFrames` / interop comments, and the README Audio Type notes together.

### Spontaneous worker exit clears keep-alive desire (no outer respawn)

**Accepted design. Do not flag as a bug or recommend “sticky intent” / outer worker respawn by default.**

**Where:** `KeepAliveAudioService.WorkerMain` `finally`, only when the exiting thread still owns the registered session **and** that generation is still the current epoch (`ReferenceEquals(_cts, cts)` && `_appliedEpoch == _sessionEpoch`). Sets `_desiredEngaged = false`, clears the worker slot, publishes `IsEngaged = false` and `Phase = Stopped`, then **disposes that generation’s CTS** (after dropping `_cts`). Demoted / parked generations must **not** dispose — control drain-join owns CTS lifetime. If the slot is still registered but the epoch was already bumped (`Start` / `RestartIfEngaged` replace in flight), `finally` does nothing: desire and **Reconnecting** / **Starting** stay; control demotes the dead thread and starts the new epoch.

**Two recovery layers (do not conflate them):**

| Layer | Location | Recovers from |
|-------|----------|----------------|
| **Inner** | Constant / pulsed supervisors inside one worker | Open/stream failures (device loss, exclusive mode, CoCreate / open-time COM throw, …) → stay engaged, **Reconnecting**, backoff / next pulse |
| **Outer** | Control thread + `_desiredEngaged` | Start / Stop / `RestartIfEngaged` / Dispose / **owning worker gone** |

Normal keep-alive resilience is the **inner** loop. The outer plane is session lifecycle (which worker generation should exist), not a second reconnect supervisor.

**What happens on spontaneous end of the owning worker** (unexpected exception outside supervisors, or any path that returns from the supervisor without the control thread having already detached the CTS — e.g. the “Completed with unlimited duration” guard):

1. Desire is revoked (`_desiredEngaged = false`) — treated like user Stop for intent.
2. UI goes **Stopped** (honest; may retain `LastIssue` until the next Start — short shared fault suffix on label / tray / menu; full text on the main status tooltip).
3. `RestartIfEngaged` (device-follow / power resume) **no-ops** until the user (or launch preference path) calls **Start** again.
4. The control thread does **not** immediately spawn a replacement worker just because the slot is empty.
5. The owning worker disposes its CTS after clearing `_cts`. Drain/park paths still dispose only after join (or leave CTS for an abandoned exit tail).

**Same fail-closed outcome if `Thread.Start` throws** on the control thread before the generation runs (typically OOM). Reconcile rolls back `_worker` / `_cts` / `_appliedEpoch`, revokes desire, publishes Stopped, sets `LastIssue` from the exception, and disposes the unused CTS. It does **not** retry `Thread.Start` or leave a dead registered slot (that would idle forever with desire still true). This is not an inner reconnect and not a new outer supervisor.

**What does not hit this path (by design):**

- Open/stream failures in Constant or Pulsed — supervisor stays alive and reconnects. This includes thrown COM during open (enumerator CoCreate, Activate/GetService cast) as `OpenFailed` from `RunSession`, not only PreserveSig HRESULTs. Unexpected throws *after* a proven `IAudioClient.Start` still escape (this fail-closed path).
- Stop / `RestartIfEngaged` / Dispose — control usually detaches `_cts` before the old `finally` runs; `ReferenceEquals` fails, so the old worker does **not** clear a newer session’s desire. If `finally` still sees `_cts` after an epoch bump (replace requested, not yet demoted), it must **not** revoke desire or clear the slot — control demotes the dead thread.
- Tracked-drain generation (dual-session / drain note above) — same ownership check; a draining worker’s `finally` must not clear desire for the registered generation.

**Why fail-closed at the outer layer is intentional:**

- Leaving `_desiredEngaged == true` after an unexplained thread death would make reconcile **respawn workers forever** (outer crash loop), hammering WASAPI after a poison-pill fault.
- Product prioritizes honest **Stopped** over a hidden outer supervisor that can thrash unattended.
- “Engaged until I press Stop” is implemented as engaged while the keep-alive **implementation generation** is still viable; inner reconnect covers the device/driver failures keep-alive exists for.
- Sticky intent + outer respawn (with or without a respawn budget) is a larger product/control-plane change, not a drive-by fix.

**Do not recommend** as routine cleanup: sticky `_desiredEngaged` on every worker `finally`, control-thread auto-restart without a documented budget, retrying `Thread.Start` after it throws, or treating every Stopped as a bug when the worker exited while still owning the session (or never started).

**Revisit only if** one or more of these become true:

1. Field reports of keep-alive going Stopped with no user action after long unattended runs, attributable to worker-thread death rather than inner reconnect exhaustion (inner does not “exhaust” — it loops until cancel).
2. Explicit hardening for multi-week set-and-forget where outer respawn is required.
3. Product requirements demand “engaged until Stop” as a hard guarantee even across unexpected thread faults.

If revisited, prefer sticky desire + wake control + **Reconnecting** + issue retained, with an **outer respawn budget** (then fail closed), UI staying non-blocking — not an unbounded silent restart loop. Update this section, `WorkerMain` / type remarks on `KeepAliveAudioService`, and any user-facing status notes together.

### Worker phase / LastIssue publishes are ownership- and desire-gated

**Intentional. Do not ungate demoted or stopping workers “to surface more status.”**

**Where:** `KeepAliveAudioService` worker helpers `SetPhase` / `ReportIssue` / `ClearLastIssuePreservingDrainAmbient` / `ClearAttenuationIssueOnly` / `SampleSessionAttenuation` / `NotifyStreamingStarted` — each requires `TryBeginOwnerPublish_NoLock(owner)`:

```text
ReferenceEquals(_cts, owner) && _desiredEngaged && _appliedEpoch == _sessionEpoch
```

**Why:**

- Control may demote a cancelled generation into the drain (or park it) while it is still inside blocking COM (`IAudioClient.Start`, etc.). Cancel is cooperative; the thread can still return success and would otherwise paint **Running** / **Reconnecting** / stale `LastIssue` for a **newer** registered generation (replace / dual re-arm).
- `Start` / `RestartIfEngaged` bump `_sessionEpoch` and cancel the registered CTS immediately, but `_cts` stays attached until control demotes. Without the epoch check, the outgoing generation could still `NotifyStreamingStarted` → **Running** (wrong device / unproven new epoch), especially while control is blocked in a drain join. Epoch mismatch denies that publish; the new worker is allowed again when control sets `_appliedEpoch = targetEpoch`.
- Between user **Stop** (desire false, optimistic Stopped) and demote, a still-registered worker must not re-upgrade phase or resurrect issues.
- `WorkerMain` `finally` remains a separate path: ownership of the **current** epoch (`ReferenceEquals(_cts, cts)` && `_appliedEpoch == _sessionEpoch`), **no** desire check — it must revoke desire, force Stopped, and dispose its CTS on spontaneous death of the live generation. A superseded generation (epoch already bumped) must not revoke desire, clear the slot, or dispose the CTS.
- Control-thread phase/issue updates (Start/Stop/Dispose/reconcile/drain ambient) are not worker publishes and stay ungated by this rule.
- Defense in depth: after successful `IAudioClient.Start`, re-check cancel before clear/notify; `Start` / `RestartIfEngaged` cancel the token so that check can see replace-in-flight; reconcile with desire off clears `LastIssue` when no drain is live.

**Do not recommend** as routine cleanup: publishing phase/issues from drain/parked generations; gating `finally` with `_desiredEngaged`; using `token.IsCancellationRequested` alone as the generation id (ownership is `_cts`; epoch is an additional current-generation check).

**What does not hit this path (by design):** control optimistic Start/Stop UI; drain-first `PreviousSessionClosing`; owning-generation reconnect `ReportIssue` / `SetPhase(Reconnecting)`.

If revisited (e.g. demoted gens must surface a one-shot teardown reason), prefer a control-thread-only issue channel — not ungating worker publishes. Update this section and `KeepAliveAudioService` publish helpers together.

### Keep-alive and device-follow target Multimedia role only

**Intentional product scope. Console and Communications defaults are non-goals. Do not flag Multimedia-only as an omission or recommend multi-role keep-alive / follow by default.**

**Where:**

| Concern | API / type | Role used |
|---------|------------|-----------|
| Open keep-alive endpoint | `KeepAliveAudioService.RunSession` → `GetDefaultAudioEndpoint(Render, …)` | **`ERole.Multimedia` only** |
| Device-follow default changes | `DefaultAudioDeviceMonitor.OnDefaultDeviceChanged` → `PlaybackGraphInvalidator.Signal` | **Multimedia only** (Console / Communications ignored) |
| Tracked endpoint state/remove | `DefaultAudioDeviceMonitor` + `_lastMultimediaDefaultId` → `Signal` | Id of the **Multimedia** default |
| Power resume re-arm | `MainForm` → `PlaybackGraphInvalidator.Signal` (not via the monitor) | N/A (graph may be stale) |
| Output-device label | `DefaultPlaybackDeviceName` (thread-pool probe; UI applies the string) | Friendly name of the **Multimedia** default |

Interop defines `ERole.Console` and `ERole.Communications` (`CoreAudioNotificationInterop`) for COM completeness; keep-alive and follow never open or arm on those roles.

**Graph invalidation bus:** COM device-follow and power-resume both signal `PlaybackGraphInvalidator` (500 ms debounce). MainForm handles `Invalidated` with a background name probe + `RestartIfEngaged` + one deferred re-pin. **Do not** call `GetDefaultAudioEndpoint` on the UI thread — a wedge freezes the message pump. That applies to `DefaultPlaybackDeviceName` (friendly-name probe stays on the thread pool; the label may stay stale until it completes) **and** to `DefaultAudioDeviceMonitor`’s initial `_lastMultimediaDefaultId` snapshot. `Start()` on the UI thread is **registration only** (CoCreate + `RegisterEndpointNotificationCallback`). The tracked-id snapshot runs on a thread-pool worker with a **separate** enumerator. **Do not** query the UI-STA enumerator from the pool (that marshals `GetDefaultAudioEndpoint` back onto the pump). Until the snapshot lands (or Multimedia `OnDefaultDeviceChanged`), tracked state/remove may miss; default-change and resume still work. **Do not** gate power-resume on successful `DefaultAudioDeviceMonitor.Start` / `_listening` — if COM registration fails, only automatic device-follow is lost; resume re-arm must still work. Ctor `Start()` may be retried once on first proven Running (one-shot) and again on resume-driven `Invalidated` while not listening (UI thread only). That is registration retry, not an unbounded `Start()` or `RestartIfEngaged` loop. Resume re-arm and follow retry must not wait on the name probe or the id snapshot. Outer vs inner recovery depth is intentional — see **Graph invalidation and recovery layers** below.

**What ZeroTone does:** streams mix-format silence / near-silence on the **Multimedia** default playback device, and restarts that session when the Multimedia default (or its tracked endpoint state) changes, or on power-resume via the shared invalidation bus.

**What ZeroTone does not do (non-goals):**

1. **Open** a keep-alive session on the Console default or the Communications default.
2. **Follow** Console-only or Communications-only default-device changes (no restart solely because those roles moved).
3. **Dual- or triple-role** keep-alive (one process holding silence on more than one role’s default at once).
4. A settings UI to “target role: Console / Multimedia / Communications” — unless product requirements change (see revisit).

**Why Multimedia-only is intentional for ZeroTone:**

- The product problem is **media path** idle / renegotiation (S/PDIF, HDMI, Bluetooth A/V, etc.) so the **first second of real content** is not clipped. That maps to the Windows **Multimedia** role (music, movies, general content playback), not VoIP.
- On typical consumer Windows, the user’s “default playback device” keeps **Console and Multimedia on the same endpoint**. Targeting Multimedia is the everyday media default; a separate Console stream would usually be **redundant**.
- **Communications** often **diverges** on purpose (USB headset as Default Communications Device while TV/speakers remain the media default). Keep-alive or follow on Communications would commonly hold or thrash a **headset** while the user’s problem device is the **media** sink — wrong target for ZeroTone.
- Ignoring Communications-only (and Console-only) default notifications avoids **spurious session restarts** when the user plugs a headset for a call or Windows fires multiple role callbacks for one physical change. Multimedia-only is a **filter**, not incomplete follow of “the” device.
- Multi-role open would mean **multiple shared-mode sessions**, extra volume-mixer rows, and worse dual-session / teardown behaviour for little gain on the core use case.
- Named multi-device selection and simultaneous keep-alive on arbitrary endpoints remain separate non-goals (see README); role expansion is not a substitute for that.

**Do not recommend** as routine cleanup: also `GetDefaultAudioEndpoint(…, Console)`, follow all three `ERole` values, open Communications by default, dual silence on Console+Multimedia “to be safe,” snapshotting the tracked id via `GetDefaultAudioEndpoint` inside UI-thread `Start()`, or calling `GetDefaultAudioEndpoint` on the registered STA enumerator from a worker.

**Revisit only if** one or more of these become true:

1. Credible field reports that keep-alive lands on the wrong endpoint because **Console and Multimedia disagree** on real user machines (not theoretical API completeness).
2. Product decision to serve **communications-device** idle (headset-only drop) as a first-class scenario.
3. Explicit advanced option for role target or multi-role keep-alive, with UX and lifecycle cost accepted.
4. Windows / ecosystem behaviour changes so media content systematically uses a non-Multimedia default while Multimedia stays elsewhere.

If revisited, prefer a **documented** single choice (e.g. optional role, or follow-by-endpoint-id after any role change that affects the current target) over silently opening every role. Update this section, `KeepAliveAudioService` / `PlaybackGraphInvalidator` / `DefaultAudioDeviceMonitor` / `DefaultPlaybackDeviceName` remarks, and the README “Follow multimedia default device” section together.

### Graph invalidation and recovery layers

**Intentional layered design. Do not flag “only one deferred UI retry after device-follow” as thin total recovery, or recommend an unbounded outer `RestartIfEngaged` train by default.**

**Where:** `PlaybackGraphInvalidator` → `MainForm.PlaybackGraph_Invalidated` / `RestartKeepAliveAfterGraphInvalidation` / `ScheduleGraphInvalidationRetry` (1.5 s); long-lived open/stream resilience in `KeepAliveAudioService` supervisors (`RunConstantSupervisor` / `RunPulsedSupervisor`).

**Two planes (do not conflate them):**

| Plane | Role | What it recovers |
|-------|------|------------------|
| **Outer (graph pin)** | Debounced invalidation → session **replace** while desire is true | Default device / endpoint graph may be stale (Multimedia default change, tracked endpoint state/remove, power resume). Re-queries the current Multimedia default by tearing down and starting a new worker generation. |
| **Inner (worker)** | Supervisors inside one worker generation | Open/stream failures on the **current** generation (device loss, exclusive mode, transient COM, …) without needing another graph event. |

**Outer schedule (handoff accelerator, not the deep safety net):**

1. Producers call `PlaybackGraphInvalidator.Signal` (COM follow and/or power resume).
2. **500 ms** debounce coalesces Bluetooth storms and resume+COM bursts into one `Invalidated`.
3. MainForm: label refresh + immediate `RestartIfEngaged` (no-op if desire is false). **`RestartIfEngaged` sets phase to `Reconnecting`** (not sticky Running, not cold-start Starting) until the new generation proves `IAudioClient.Start`.
4. **One** deferred re-pin at **1.5 s** (`_graphInvalidationRetryTimer`), re-armed if invalidations keep arriving — not a multi-shot backoff train.
5. Further outer replaces happen only on **new** invalidation signals (or user Start / audio-option change restart), not on a repeating outer timer.

**Phase on replace (intentional):** Running means the **current registered generation** has proven `IAudioClient.Start`. Session replace while desire is already true (graph pin, power resume, audio-option restart, etc.) must leave Running and use **Reconnecting** until the new gen proves Start. **Starting** is cold engage only (`Start` / Start on Launch) and stays Starting through first-open retries until that engage proves Start — do **not** demote Starting → Reconnecting on the first failed open. Do **not** flag “graph replace should use Starting” or reintroduce sticky green Running across epoch replace. Pulsed inter-burst gaps stay **Running** when healthy (same generation).

**Inner recovery (pattern-dependent):**

| Pattern | After open/stream failure while still desired |
|---------|-----------------------------------------------|
| **Constant** | Stay engaged; exponential backoff **1 s … 10 s**, retry until cancel. Phase stays **Starting** until the first proven `IAudioClient.Start` of a cold engage; after a proven stream, failures use **Reconnecting**. This is the **deep** recovery path for set-and-forget handoffs. |
| **Pulsed** | Stay engaged; skip the failed burst, next open on the **next ~10 s wall-clock period** (plus the same outer double-shot when a graph event fires). Same Starting-until-first-proof / Reconnecting-after-proven-loss rule as Constant. Intentionally sparser — low-duty-cycle poke, not Constant-class reconnect. |

**Why this split is intentional:**

- Outer `RestartIfEngaged` **cancels** the current worker and bumps `_sessionEpoch`. A multi-retry outer loop would fight the inner supervisor (abort in-flight open/backoff), amplify flaky Bluetooth churn, and raise dual-session risk under bounded worker join.
- Once a generation is running, **Constant** already retries open/stream until Stop; “only two outer attempts” is not total recovery depth.
- **Pulsed** recovery is shallower by product design (1 s every 10 s). Prefer **Constant** when robust handoff recovery matters; do not “fix” Pulsed by silently adding Constant-like outer trains.
- The 1.5 s single re-pin is a **slow Bluetooth / half-ready graph** accelerator after the debounced event — not a substitute for inner reconnect.

**Do not recommend** as routine cleanup: N-shot outer `RestartIfEngaged` trains; outer retry while already healthy **Running** without a new graph signal; conflating Pulsed period gaps with missing Constant reconnect; or unbounded UI timers that replace sessions forever.

**Revisit only if** one or more of these become true:

1. Field reports that keep-alive stays failed after device switch / sleep resume **while still engaged**, attributable to needing another **default re-pin** after the 1.5 s shot with **no further COM/resume signals** (not merely long Constant backoff or Pulsed period gaps).
2. Product decision that Pulsed must recover as aggressively as Constant (then prefer phase-gated denser tries while **Reconnecting**, not a global shorter pulse for healthy steady state — unless product timing changes).
3. Evidence that 1.5 s is systematically too early or too late for common Bluetooth stacks and a different single delay (or one extra phase-gated re-pin) is justified.

If revisited, prefer **phase-gated** extra re-pin (e.g. only while Starting/Reconnecting) or pattern-specific policy, documented alongside this section, `MainForm` graph-invalidation comments, and the README “Follow multimedia default device” recovery notes — not an unbounded silent outer restart loop.

### Session mute / volume: detect-and-report only

**Intentional. Do not force-unmute or rewrite session volume as routine cleanup.**

**Where:** `KeepAliveAudioService.RunSession` after a successful `IAudioClient.Start` — best-effort `IAudioClient.GetService(ISimpleAudioVolume)`, then poll ~every 500 ms in the render loop (`SampleSessionAttenuation`). Kinds: `KeepAliveIssueKind.SessionMuted` / `SessionVolumeZero`. UI: while phase stays green **Running**, main status label, tray icon hover, and tray menu **Status** share the same short display phrase (`Running (But muted in volume mixer)` / `Running (But zero in volume mixer)`); longer `LastIssue` text remains on the main status tooltip.

**What ZeroTone does:**

- **Read** this process’s session mute flag and master volume (`GetMute` / `GetMasterVolume` only).
- If muted → ambient `LastIssue` (mute message wins over zero volume).
- Else if master volume ≤ ~0.0001 → ambient `LastIssue` for zero volume.
- Else clear **only** attenuation kinds (`ClearAttenuationIssueOnly`) so open/stream failures are not wiped by a healthy volume sample.
- Phase stays **Running**; desire and buffer writes continue.

**What ZeroTone does not do:**

1. Call `SetMute` / `SetMasterVolume` (no force-unmute, no volume restore).
2. Observe **endpoint / device / system** master mute (session row only).
3. Change phase to Reconnecting or stop the worker because of mixer attenuation.
4. Restart keep-alive or invalidate the playback graph when mute/volume changes.
5. Set a custom mixer display name/icon (`IAudioSessionControl`) — product defaults remain.

**Why detect-and-report is intentional:**

- **Running** means the shared-mode client is healthy and writing — not “effective keep-alive energy is guaranteed on the wire.” Mixer mute / 0% can still defeat picky sinks (especially **Inaudible Sound**).
- Forcing unmute fights intentional user mute and is hostile for a silence utility.
- Ambient status matches the honesty model used for Starting / Reconnecting without conflating mute with stream failure.

**Do not recommend** as routine cleanup: force-unmute on Start or while engaged; fail open/stream when volume is low; a new **Muted** phase/icon; outer `RestartIfEngaged` on mute; or endpoint-mute monitoring — unless revisit criteria below are met.

**Revisit only if** one or more of these become true:

1. Field reports that users routinely mute ZeroTone by accident and need a stronger nudge or optional “keep session unmuted” setting.
2. Product decision that keep-alive must win over the mixer (force-unmute with documented UX).
3. Credible need to observe endpoint/system mute (different COM surface than `ISimpleAudioVolume`).

If revisited, prefer an **opt-in** setting or stronger ambient copy over silent force-unmute. Update this section, `ISimpleAudioVolume` interop remarks, `SampleSessionAttenuation` / mapper comments, and the README Windows volume mixer notes together.

### Tray re-registration on `TaskbarCreated` is owned by WinForms `NotifyIcon`

**Not required in app code. Do not flag missing `TaskbarCreated` handling as an omission.**

**Where:** `MainForm` / designer `NotifyIcon` (`trayIcon`). Visible tray icon with tooltip and context menu; no direct `Shell_NotifyIcon` usage.

**What Windows requires (raw shell):** When Explorer recreates the taskbar (and on some primary-DPI changes), it broadcasts the registered window message **`TaskbarCreated`**. Apps that call `Shell_NotifyIcon` themselves must re-`NIM_ADD` their icons.

**What ZeroTone relies on instead:** `System.Windows.Forms.NotifyIcon` owns a private native window that already:

1. Registers for `TaskbarCreated`.
2. On that message, clears its internal “already added” state and re-adds the icon when `Visible` is true and an icon is set.

That path lives in the Windows Desktop shared framework (confirmed present in current .NET WinForms builds), not in `MainForm.WndProc`. Grepping the app for `TaskbarCreated` and finding nothing is expected and **not** a defect.

**Do not recommend** as routine cleanup: app-level `RegisterWindowMessage("TaskbarCreated")` in `MainForm`; toggling `trayIcon.Visible` / re-assigning `Icon` on a hand-rolled shell message; or raw `Shell_NotifyIcon` “to fix Explorer restart” while still using `NotifyIcon`.

**Revisit only if** one or more of these become true:

1. Product drops `NotifyIcon` for raw `Shell_NotifyIcon` or a tray library that does **not** document re-registration on taskbar recreate.
2. Real field reports of a **permanent** missing tray icon after Explorer restart on a supported .NET Desktop runtime, attributable to missing re-add (not user-hidden notification icons, not process exit).
3. A documented WinForms regression removes `NotifyIcon`’s `TaskbarCreated` handling and a temporary app workaround is required.

If revisited, prefer fixing or wrapping the tray component once, documented here and next to `trayIcon` setup — not duplicate re-add logic that fights `NotifyIcon`’s internal add/modify state.

### PE / shortcut / mixer identity uses the green bar-chart icon

**Intentional. Do not flag the Explorer / shortcut icon matching Running (green) as a defect, and do not recommend a separate “neutral brand” .ico.**

**Where:** `ZeroTone.csproj` `ApplicationIcon` → `Resources\bar-chart-green.ico`. Windows volume mixer uses the executable / window identity (`IAudioSessionControl` is not set). Live tray / window chrome still switches green / amber / red with phase (`bar-chart-green.ico` / `bar-chart-amber.ico` / `bar-chart-red.ico`).

**What ZeroTone does:** The green bar-chart is both the **product mark** (Explorer, shortcuts, default mixer row while streaming) and the **Running** tray/window glyph.

**What ZeroTone does not do:** Ship a fourth “neutral” identity icon so the file on disk does not look “on” when keep-alive is Stopped.

**Why this is intentional for ZeroTone:**

- One recognizable mark. The mixer row while **Running** already matches the PE (see README Windows volume mixer notes).
- Live phase is the tray / window icons, not the executable resource. A stopped process has no tray state until launched; Explorer showing green does not mean keep-alive is engaged.
- A separate brand asset would be another multi-res .ico to keep in sync with the Running glyph for little user benefit.

**Do not recommend** as routine cleanup: a grey / outline PE icon “so shortcuts do not look Running”; using red or amber as `ApplicationIcon`.

**Revisit only if** product wants a distinct brand lockup, or credible field confusion that a green shortcut means keep-alive is on.

If revisited, update this section, `ApplicationIcon`, `BarChartIconGlyph` remarks, and the README mixer / Resources notes together.

### About dialog stays a `MessageBox` (in-session scale change may look soft)

**Intentional. Do not replace About with a custom `Form` or `TaskDialog` to chase PerMonitorV2 text sharpness.**

**Where:** `MainForm.menuAbout_Click` → `MessageBox.Show` (no owner). Same USER32 `MessageBoxW` surface as the other in-app warnings (`PersistSettings`, `MaybeWarnSettingsReset`, `WarnStartupRegistrationFailed`, secondary-launch “already running,” crash UI).

**What ZeroTone does:** Tray **About** is a native `MessageBox` (version, tagline, one-paragraph product text). `MainForm` is PerMonitorV2 (`ApplicationHighDpiMode`, `OnDpiChanged`, icon reload, tab layout). That handling does **not** apply to About.

**What ZeroTone does not do:** A dedicated About `Form`, WinForms `TaskDialog`, or extra DPI work around `MessageBox` (owner HWND, thread awareness dance, bitmap-scale workarounds).

**Why the text can look soft:** `MessageBoxW` remains **system-DPI** even inside a PerMonitorV2 process (OS limitation; WinForms will not fix it). System DPI for this process is latched at **ZeroTone launch** (not live per-monitor DPI, and not necessarily the sign-in scale). If the user then changes Display scaling without restarting ZeroTone, Windows **bitmap-scales** the dialog to the new monitor DPI. 150% → 100% downscale is the mild “soft” look; the reverse is more obviously blurry. Passing `this` as owner would only affect parent/centering, not rasterization. Restarting ZeroTone after the scale matches the display makes About sharp again; sign-out is not required for a new process.

**Why MessageBox is the product choice:**

- About is rare chrome on a small tray utility. Native dialog, keyboard, and contrast come for free.
- A second `Form` would inherit the same PerMonitorV2 layout/HWND cost already paid by `MainForm` (hidden-tray parent, mixed-DPI restore, font/bounds after live scale change) for a window most users never open.
- `TaskDialog` is sharper on DPI change but heavier chrome and still not an About page.

**Do not recommend** as routine cleanup: custom About `Form`; `TaskDialog`; `MessageBox.Show(this, …)` “for DPI”; `SetThreadDpiAwarenessContext` around About; treating soft About text after an in-session scale change as a ZeroTone defect.

**Revisit only if** one or more of these become true:

1. About grows (license text, links, copyable version) so a `Form` is justified on content, not sharpness.
2. Real support reports that in-session Display scaling leaving About soft is a problem users care about.
3. Windows/`MessageBoxW` becomes PerMonitorV2 (then no app change is needed).

If revisited, prefer a small AutoScale Dpi About `Form` owned by the same PerMonitorV2 path as `MainForm` — not a one-off DPI hack around `MessageBox`. Update this section and `menuAbout_Click` remarks together.

### UI marshal waits for an HWND (`InvokeRequired` is false without one)

**Intentional. Do not run WinForms work on the caller when the form has no handle, and do not `CreateHandle` from a background thread to “fix” marshal.**

**Where:** `MainForm.MarshalKeepAliveUi` / `PlaybackGraph_Invalidated` / `FlushPendingUiMarshals` / `MaybeApplyStartOnLaunch`.

**What WinForms does:** `Control.InvokeRequired` is **false** when `IsHandleCreated` is false (no HWND to compare threads). A naive `if (InvokeRequired) BeginInvoke; else action()` then runs `action()` on the **audio worker or invalidator thread-pool**. That can create controls/timers off the UI thread.

**What ZeroTone does:**

1. **Start on Launch** is **evaluated once** on the first `OnHandleCreated`, not in ctor `LoadState` (no HWND yet on a windowed launch). The one-shot is consumed even when the preference is off. Tray-minimize may create the handle during `LoadState` and apply start before the ctor returns — still after HWND exists. A later handle recreate (style change, rare shell recreate) must not re-read the checkbox and start keep-alive.
2. Without a handle, keep-alive UI apply and graph invalidation set pending flags under `_uiMarshalGate` and return. Re-check after `InvokeRequired` (and on `BeginInvoke` `InvalidOperationException`) so a handle destroy cannot fall through to WinForms on the worker.
3. `OnHandleCreated` flushes pending work on the UI thread, then evaluates Start on Launch (first create only). Flush order is graph first, then **always** keep-alive chrome (live sample), then any completed name probe. Graph re-arm may already have scheduled chrome; `RestartIfEngaged` no-ops when desire is off and must not drop a deferred IsEngaged/Phase/LastIssue apply (HWND recreate). A second `ScheduleApplyKeepAliveUi` is idempotent.
4. `BeginInvoke` also catches `InvalidOperationException` (handle torn down mid-close).

**Do not recommend** as routine cleanup: `CreateHandle` from a worker; treating `InvokeRequired == false` as “safe to touch controls”; moving Start on Launch back into `LoadState`; or app-level `TaskbarCreated` (see previous section).

**Revisit only if** handle recreation must re-run Start on Launch (it must not — once per lifetime), or a future host creates the form off the STA UI thread.

### Tray hide/restore must not toggle `ShowInTaskbar`

**Intentional. Do not set `ShowInTaskbar = false` on close-to-tray / minimize-to-tray.**

**Where:** `MainForm.MinimizeToTrayOrTaskbar` / `RestoreWindow`.

**What ZeroTone does:** Hide the form (`Hide()`, stay `FormWindowState.Normal`). A hidden Normal window is already off the taskbar and Alt+Tab. Restore is `Show()` plus the existing foreground dance. Saved `WindowLeft`/`WindowTop` are re-applied so restore lands on the same monitor. `ShowInTaskbar = false` is allowed **only before the first HWND exists** (start-minimized) so `CreateParams` omits `WS_EX_APPWINDOW` and the shell does not flash a taskbar button.

**What ZeroTone does not do:** Toggle `Form.ShowInTaskbar` around tray hide **after a handle exists**. That setter **recreates the HWND**. On mixed-DPI (e.g. primary 125% / secondary 250%) the new handle is often created at the primary (or hide-time) DPI while fonts/chrome pick up the restore monitor — clipped labels, tiny checkbox/radio glyphs on every tab.

**Why:** Close-to-tray is Hide, not minimize-iconic. `ShowInTaskbar = false` after the HWND exists is the minimize-without-a-button pattern; it is not needed here and is the PerMonitorV2 footgun that mangled restore onto a high-DPI secondary.

**Do not recommend** as routine cleanup: `ShowInTaskbar = false` on an existing handle “to be sure” the button is gone; `WS_EX_TOOLWINDOW` via `RecreateHandle` for tray hide.

**Revisit only if** a supported Windows build keeps a visible taskbar button or Alt+Tab entry for a hidden Normal `WS_EX_APPWINDOW` form. Prefer `ITaskbarList.DeleteTab` (no handle recreate) over toggling `ShowInTaskbar`.

### MainForm ctor failure unsubscribes and hides the tray before the exception escapes

**Intentional. Do not leave SystemEvents / audio / invalidator handlers attached, or a Visible NotifyIcon, if construction throws after `InitializeComponent`.**

**Where:** `MainForm` ctor `try` after `InitializeComponent` (icons, subscribe, `LoadState`); catch calls `DetachExternalEvents` then `TryHideTrayForExit`. `Program.Main` `using`s the form so a throw after ctor success (activate listener / `Application.Run`) still `Dispose`s chrome. `Application.Run(Form)` also disposes; a second `Dispose` is idempotent.

**Why:** Subscribe order is load-bearing (`Invalidated` before `deviceMonitor.Start`; audio/power before `LoadState` so Start on Launch events are heard). `InitializeComponent` sets `trayIcon.Visible = true`. If ctor throws, `Program` never receives the instance and never reaches `Application.Run` / `OnFormClosed`, but `finally` still `Dispose`s audio. Detach so those services cannot raise into a half-built form and so `SystemEvents` cannot root it for the unwind. Hide the tray so a failed launch does not leave a ghost icon.

**Do not recommend** as routine cleanup: moving subscriptions to after `LoadState` (drops early COM / Start-on-Launch events); `CreateHandle` in the ctor catch; skipping `TryHideTrayForExit` because `Dispose` “should” run (`new` threw — no reference).

## Naming: preferences vs engaged vs phase

Use these terms consistently in code and reviews. Do **not** reintroduce overloaded names such as `AppSettings.IsRunning`, `IsHidden`, or `AudioType.Silent`.

| Term | Meaning |
|------|---------|
| `AppSettings.StartOnLaunch` / JSON `startOnLaunch` | Preference: start keep-alive when the app launches. Applied once on first `OnHandleCreated`, not in ctor `LoadState` |
| Start with Windows (`WindowsStartupRegistration`) | Live OS registration: `HKCU\...\Run` value `ZeroTone` plus `StartupApproved` so Task Manager disable is honest. **Not** an `AppSettings` / JSON field (`startWithWindows` is invalid). Do **not** merge with `StartOnLaunch`. Default off; fail-open on registry errors (revert checkbox, one warning) |
| `AppSettings.MinimizeOnLaunch` / `minimizeOnLaunch` | Preference: start minimized |
| `AppSettings.MinimizeToSystemTray` / `minimizeToSystemTray` | Preference: minimize goes to tray (not taskbar) |
| `AppSettings.MinimizeOnClose` / `minimizeOnClose` | Preference: Close minimizes instead of exit |
| `KeepAliveAudioService.IsEngaged` | Live: keep-alive is on (starting, streaming, **or** reconnecting) |
| `RestartIfEngaged` | Restart session only while keep-alive is still **desired** (intent), not “if streaming”. Sets **Reconnecting** (generation unproven); never cold-start **Starting** |
| `KeepAlivePhase` (`Stopped` / `Starting` / `Running` / `Reconnecting`) | Session phase for UI status and icons. **Starting** = cold engage only (`Start` / Start on Launch), stream not yet proven — including failed open retries until the first proven `IAudioClient.Start`. **Running** = current registered generation has proven `IAudioClient.Start` (or healthy Pulsed gap) — client health, not “not muted”. **Reconnecting** = open/stream failure **after** a proven stream, **or** intentional session replace while desire was already on. Do **not** flip Starting → Reconnecting on the first failed open of a cold Start. Starting and Reconnecting share amber chrome (`bar-chart-amber.ico`; glyph only for non-embedded sizes). UI may coalesce **both** amber phases (~120 ms) so a fast open never flashes amber; engagement chrome (Start/Stop) is never deferred. Do **not** reintroduce Starting-only coalesce or sticky Running across replace |
| `LastIssue` session attenuation (`SessionMuted` / `SessionVolumeZero`) | Ambient mixer notice while **Running** (detect-and-report only; no force-unmute). Not a stream-open failure and not a phase change |
| `LastIssue` retained after fail-closed **Stopped** | Owning-worker `finally` / `Thread.Start` failure may keep a non-ambient `LastIssue` until the next Start (user Stop clears it when no drain is live). UI may show a short shared kind suffix on the main label, tray hover, and tray menu (e.g. `Stopped (Unexpected error)`); full text / HRESULT stay on the main status tooltip. Not a phase change and not mute/drain ambient |
| `LastIssue` previous session (`PreviousSessionClosing`) | Ambient while a cancelled worker is still draining (tracked drain). Message: **“A previous session is still closing...”**. **Drain-first** while drain is live (overwrites other `LastIssue`; displaced issue stashed and restored on drain clear if still Starting/Reconnecting). Prefer over mute ambient while drain is live. Not a stream-open failure and not a phase change |
| Worker UI publish ownership | Phase / `LastIssue` from a worker generation only when `ReferenceEquals(_cts, owner) && _desiredEngaged && _appliedEpoch == _sessionEpoch`. Demoted/stopping/superseded gens tear down COM silently. `Start` / `Stop` / `RestartIfEngaged` / `Dispose` cancel the registered CTS immediately (control still demotes/joins). `WorkerMain` `finally` is ownership of the current epoch (may clear desire and **dispose its CTS**); superseded gens leave the slot for control. Do **not** ungate demoted publishes |
| `AudioType.Silence` / JSON `"silence"` | Digital zeros as normal WASAPI packets (UI: **Silence**); not JSON `"silent"`, not `AUDCLNT_BUFFERFLAGS_SILENT` |
| `AudioPattern.Constant` / `Pulsed` / JSON `audioPattern` | Keep-alive timing pattern (UI group: **Pattern**); Continuous stream vs 1 s every 10 s |
| UI Start / Stop (`RequestStartKeepAlive` / `RequestStopKeepAlive`) | Absolute commands on `KeepAliveAudioService.Start` / `Stop`. Click path (`HandleStartStopClick`) chooses the command from **applied chrome** (`_uiAppliedIsEngaged`), not by sampling live `IsEngaged`. Start on Launch calls start directly. Do **not** reintroduce observe-then-invert toggle-on-`IsEngaged` |

JSON keys match camelCase `AppSettings` property names and are **case-sensitive**. **No** legacy key read aliases (old ZeroTone files with previous names are not migrated). Unknown or wrong-case names (`StartOnLaunch`, `isRunning`, …) take the same corrupt-file reset as an unknown enum word (`JsonUnmappedMemberHandling.Disallow`). `audioType` / `audioPattern` are camelCase **strings only** (`allowIntegerValues: false`); undefined members after load take the same reset. Do **not** accept `0`/`1` as Silence/Inaudible (or Constant/Pulsed).
