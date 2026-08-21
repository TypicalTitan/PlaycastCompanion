# Integrating Playcast Companion into Playcast

This document is for Playcast (or any host product) engineers evaluating
whether to adopt this behavior natively. Short version: **the hard parts are
already isolated behind one small interface, and the product already knows
the one fact this app spends most of its code discovering.**

## What the app does, in one sentence

While a specific Windows account has a session (your guest account), hold
every RGB ecosystem at black and show a Discord presence; when the session
ends, hand everything back.

## Where the seams are

```
SessionWatcher      -> "is the guest logged on?"  (WTS notifications + enumeration)
ILightingBackend    -> one hold/release loop per ecosystem  (6 implementations)
DiscordPresence     -> Discord Rich Presence over local IPC
DiscordShimServer   -> optional guest-session shim that learns which game is running
TrayAppContext      -> orchestration + tray/settings UI
```

`ILightingBackend` is the seam that matters:

```csharp
public interface ILightingBackend
{
    string DisplayName { get; }
    bool Enabled { get; }
    bool IsHolding { get; }
    string StatusText { get; }
    void StartBlackout();
    Task StopBlackoutAsync();
}
```

`LightingBackendBase` supplies the retry/heartbeat loop; each backend is a thin
protocol adapter (Razer Chroma REST, SteelSeries GameSense REST, OpenRGB TCP,
Logitech/Corsair P/Invoke, Windows LampArray). **You don't need `SessionWatcher`
at all** — your GuestModeManager already knows the exact moment guest mode
enters and tears down, which is strictly better than an outside observer
watching WTS.

## Three integration options (cheapest first)

### 1. Sidecar process (near-zero code change)
Ship the companion exe alongside the host and have the service launch it with a
control flag at guest enter/teardown. Today the app auto-detects the session;
adding an explicit `--stealth on|off` mode that forces hold/release (ignoring
WTS) is a ~50-line change in `TrayAppContext`/`Program`. Pros: no porting,
isolated failure domain, MIT-licensed code you can vendor. Cons: a second
process to deploy and keep in sync.

### 2. Port the backends into your existing process
The three REST/TCP backends (Chroma, GameSense, OpenRGB) are pure protocol
code — each is under ~150 lines and ports to C++ or TypeScript in an afternoon.
Logitech/Corsair are vendor-DLL calls; Windows Dynamic Lighting is a WinRT API
available from C++/WinRT. Call them from `GuestModeManagerEnter` /
`GuestModeManagerTeardown`. Pros: single process, native feel. Cons: you own the
vendor-SDK surface area and its edge cases (documented in each backend's
header comment and in `README.md`).

### 3. Reference the .NET assembly
If any part of the host is .NET, `PlaycastCompanion.dll` can be referenced
directly; construct the backends and drive `StartBlackout`/`StopBlackoutAsync`.
The WinForms UI is separable (everything UI is in `MainForm.cs`/`TrayAppContext.cs`).

## Effort estimate
| Option | Engineering effort | Risk |
|---|---|---|
| Sidecar + `--stealth` flag | ~1 day incl. installer wiring | Low |
| Port backends to C++/TS | ~3–5 days for all six, ~1 day for Chroma alone | Medium (vendor quirks) |
| Reference the assembly | ~1 day | Low, but requires .NET in the host |

## Licensing you'd inherit
- **This code:** MIT — vendor, modify, ship, no copyleft.
- **Vendor SDKs:** each ecosystem's own developer terms apply to *you* as the
  integrator (Razer Chroma SDK license, SteelSeries GameSense, Logitech LED SDK,
  Corsair iCUE SDK). This repo redistributes none of their binaries. For a
  commercial product, review each SDK's license — some require registration or
  attribution. See `THIRD_PARTY_NOTICES.md`.
- **OpenRGB:** this app speaks OpenRGB's network protocol and links no OpenRGB
  code, so OpenRGB's GPL does not reach the integrator.
- **Discord:** Rich Presence needs *your own* Discord Application ID under
  Discord's Developer Terms. The optional game-pass-through shim emulates a
  Discord IPC endpoint for guest-session games; it's off by default and, for a
  commercial product, is the one piece worth a counsel review against
  Discord's terms before shipping (or simply leave it out — it's fully
  separable).
- **Trademarks:** the repo ships no vendor logos. Playcast obviously controls
  its own branding and can rebrand freely.

## Things to keep if you port
- The `OperationCanceledException when (ct.IsCancellationRequested)` filter:
  `HttpClient` timeouts also throw `TaskCanceledException`, and treating them
  as "stop" silently kills the loop (this was a real bug once).
- Chroma heartbeat < 15 s, re-apply every few seconds (Synapse fights back).
- Dynamic Lighting must skip devices a vendor engine already drives, or two
  engines fight over the same hardware (`ExcludeVendorOwnedDevices`).
- Run unelevated; nothing here needs admin.
