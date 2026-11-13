# Upgrade-box simulation (0.19.9)

Wall jump purchases now work in trajectory forecasts and section searches. The
supported movement box has one purchase, no tree activation, and buy-max either
disabled or explicitly overridden. Normal, timed VMAN and practice VMAN are
validated; VMAN retains the native affordability check but does not spend the
purchase price. The existing non-VMAN single-purchase CashPerLoop support remains.

The simulator checks an unsupported upgrade only after the native cooldown,
affordability and active-state conditions permit a purchase. Merely touching an
inactive or unaffordable box can therefore continue normally. An unsupported
affordable purchase stops the forecast or rejects the candidate before its
purchase effects. This is not a blanket allowance for every movement upgrade,
prestige, buy-max continuation, tree activation or door transition.

New snapshots observe every upgrade box's purchase count, price, cooldown,
active state and cap, alongside player abilities, local/global upgrades,
currencies and random state. The capture flag distinguishes this witness set
from earlier cash-only diagnostic exports. Movie storage and replay hashes are
unchanged; no recorded witness is overwritten to force a match.

## Validation

The disposable native probe relocates an initialized game trigger to isolate
its purchase callback. Solid siblings are disabled to avoid an artificial
initial ceiling penetration. Unity gets a paused loop to settle the moved
collider before geometry capture. Test fixtures disable their achievement/secret
flags; the driver restores the installed mod and all Savedata folders.

At 50 and 280 FPS, native and external traces match all 324 samples across these
six cases: successful Wall jump purchase, insufficient funds, inactive Wall jump
box, unaffordable unsupported Dash box, timed VMAN purchase, practice VMAN
purchase. Restore and shuffled-worker checks pass for every case. The guarded
simulator additionally verifies the successful purchase, unchanged currency on
a rejected unsupported purchase, and complete rollback in all three modes.

Separate live-preview runs remain enabled over 200 observed frames per FPS and
show the correct unlock and currency result. Their longer full-world trace
comparisons still detect an unrelated distant mover sleep-state mismatch. That
guard is retained; these longer runs are not claimed as full-world parity.
All 3,036 workspace checks pass.

Developer purchase checks use:

```powershell
.\Test-Simulator.ps1 -FrameRates @(50,280) -CaptureFrames 12 -TrajectorySmoke -PreviewInteraction wall-jump
.\Engine.Host\bin\Release\net10.0\TasStudio.EngineHost.exe upgrade-check <native-fixture-directory>
```

For passive contacts use `wall-jump-poor`, `wall-jump-inactive` or
`upgrade-unsupported-poor`. Add `-VmanMode 1` or `-VmanMode 2` to the purchase
fixture for timed/practice VMAN. These are developer diagnostics, not settings
required to use the mod.
