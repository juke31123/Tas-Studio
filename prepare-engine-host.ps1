param(
    [Parameter(Mandatory=$true)][string]$UpstreamRoot,
    [string]$ManagedDir = "C:\Program Files (x86)\Steam\steamapps\common\IGTAP an Incremental Game That's Also a Platformer\IGTAPfullGame_Data\Managed",
    [switch]$RegisterForGame
)
$ErrorActionPreference = 'Stop'
$pinned = '134d96c6011dd729e75f86f95d6fc16382538aff'
$upstream = (Resolve-Path -LiteralPath $UpstreamRoot).Path
$commitOutput = & git -c "safe.directory=$upstream" -C $upstream rev-parse HEAD
$commit = if ($commitOutput) { $commitOutput.Trim() } else { '' }
if ($LASTEXITCODE -ne 0 -or $commit -ne $pinned) { throw "Reference checkout must be pinned to $pinned." }
if (& git -c "safe.directory=$upstream" -C $upstream diff --name-only HEAD) { throw 'Reference checkout has tracked changes. Use a clean pinned checkout.' }
if (!(Test-Path -LiteralPath (Join-Path $ManagedDir 'Assembly-CSharp.dll'))) { throw 'Installed game assembly is missing.' }
$hostRoot = Join-Path $PSScriptRoot 'Engine.Host'
$cache = Join-Path $hostRoot 'obj\reference'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$tracked = & git -c "safe.directory=$upstream" -C $upstream ls-files -- shared enginesim/Prep enginesim/Shim enginesim/Box2D enginesim/Runtime
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate the pinned dependency files.' }
foreach ($relative in $tracked) {
    $destination = Join-Path $cache $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $upstream $relative) -Destination $destination -Force
}
foreach ($file in @('Directory.Build.props', 'enginesim\cuts.txt', 'enginesim\hooks.txt', 'enginesim\static-allow.txt')) {
    Copy-Item -LiteralPath (Join-Path $upstream $file) -Destination (Join-Path $cache $file) -Force
}
$compatibility = Join-Path $hostRoot 'Compatibility'
$escapedCompatibility = [System.Security.SecurityElement]::Escape($compatibility)
$escapedCore = [System.Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'Core'))
@"
<Project>
  <ItemGroup Condition="'`$(AssemblyName)' == 'IGTAP.UnityShim'">
    <Compile Include="$escapedCompatibility\FrameControls.cs" />
    <Compile Include="$escapedCompatibility\MenuPresentation.cs" />
    <Compile Include="$escapedCompatibility\HierarchyPose.cs" />
    <Compile Include="$escapedCompatibility\BodySleep.cs" />
  </ItemGroup>
  <ItemGroup Condition="'`$(AssemblyName)' == 'IGTAP.EngineSim.Runtime'">
    <Compile Include="$escapedCompatibility\FrameRuntime.cs" />
    <Compile Include="$escapedCompatibility\SchedulerChecks.cs" />
    <Compile Include="$escapedCompatibility\FrameMovers.cs" />
    <Compile Include="$escapedCompatibility\FrameAnimation.cs" />
    <Compile Include="$escapedCompatibility\FramePhysics.cs" />
    <Compile Include="$escapedCompatibility\FrameEconomy.cs" />
    <Compile Include="$escapedCompatibility\FrameCoverage.cs" />
    <Compile Include="$escapedCompatibility\FrameSearch.cs" />
    <Compile Include="$escapedCompatibility\FrameCheckpoints.cs" />
    <Compile Include="$escapedCore\Simulation\NativeFrameClock.cs" />
    <Compile Include="$escapedCore\Simulation\AnimationTimeline.cs" />
    <Compile Include="$escapedCore\Simulation\CoroutineSnapshot.cs" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $cache 'Directory.Build.targets') -Encoding UTF8

# Changes apply exclusively to the generated, ignored build cache. Keep these
# substitutions exact so an upstream change cannot silently alter the adapter.
$miscPath = Join-Path $cache 'enginesim\Shim\Engine\Misc.cs'
$misc = [IO.File]::ReadAllText($miscPath)
$substitutions = [ordered]@{
    'public static class PlayerPrefs' = 'public static partial class PlayerPrefs'
    'public static bool HasKey(string key) {' = 'public static bool HasKey(string key) { if (key == "Fullscreen") return false;'
    'public bool WasPressedThisFrame() { return false; }' = 'public bool WasPressedThisFrame() { return IGTAP.EngineSim.StudioFrameControls.IsPressed(this); } public bool WasPressedThisDynamicUpdate() { return IGTAP.EngineSim.StudioFrameControls.IsPressed(this); }'
    'public bool WasReleasedThisFrame() { return false; }' = 'public bool WasReleasedThisFrame() { return IGTAP.EngineSim.StudioFrameControls.IsReleased(this); }'
    'public bool IsPressed() { return false; }' = 'public bool IsPressed() { return IGTAP.EngineSim.StudioFrameControls.IsHeld(this); }'
    'return _resumeDeltaTime ?? fixedDeltaTime;' = 'return _resumeDeltaTime ?? IGTAP.EngineSim.StudioFrameControls.Delta;'
    'public static float time { get; set; }' = 'public static float time { get { return IGTAP.EngineSim.StudioFrameControls.Now; } set { IGTAP.EngineSim.StudioFrameControls.Now = value; } }'
    'public static float unscaledDeltaTime { get { return fixedDeltaTime; } }' = 'public static float unscaledDeltaTime { get { return IGTAP.EngineSim.StudioFrameControls.UnscaledDelta; } }'
}
foreach ($old in $substitutions.Keys) {
    if (($misc.Split([string[]]@($old), [StringSplitOptions]::None).Length - 1) -ne 1) { throw "Unexpected shim surface: $old" }
    $misc = $misc.Replace($old, $substitutions[$old])
}
[IO.File]::WriteAllText($miscPath, $misc)
$colorPath = Join-Path $cache 'enginesim\Shim\Unity\Color.cs'
$color = [IO.File]::ReadAllText($colorPath)
if (!$color.Contains('public struct Color :')) { throw 'Unexpected reference Color declaration.' }
[IO.File]::WriteAllText($colorPath, $color.Replace('public struct Color :', 'public partial struct Color :'))
$corePath = Join-Path $cache 'enginesim\Shim\Engine\Core.cs'
$core = [IO.File]::ReadAllText($corePath)
$hierarchy = [ordered]@{
    'public class Transform : Component, IEnumerable' = 'public partial class Transform : Component, IEnumerable'
    'm_LocalPosition = value - m_Parent.m_Position;' = 'm_LocalPosition = FromWorld(m_Parent, value);'
    'm_Position = m_Parent.m_Position + m_LocalPosition;' = 'm_Position = ToWorld(m_Parent, m_LocalPosition);'
    'm_Position = m_Parent == null ? value : m_Parent.m_Position + value;' = 'm_Position = ToWorld(m_Parent, value);'
}
foreach ($old in $hierarchy.Keys) {
    if (($core.Split([string[]]@($old),[StringSplitOptions]::None).Length-1) -ne 1) { throw "Unexpected transform hierarchy surface: $old" }
    $core=$core.Replace($old,$hierarchy[$old])
}
foreach ($rotation in @(@('m_Rotation','m_LocalRotation'),@('m_LocalRotation','m_Rotation'))) {
    $current=$rotation[0]; $other=$rotation[1]
    $old="set { Changing(); $current = value; $other = value; Changed(true); }"
    if (($core.Split([string[]]@($old),[StringSplitOptions]::None).Length-1) -ne 1) { throw 'Unexpected rotation setter surface.' }
    # Native identity rotation writes (for example spike contacts) do not
    # teleport the interpolated body to its rounded transform position.
    $same="$current.x == value.x && $current.y == value.y && $current.z == value.z && $current.w == value.w"
    $core=$core.Replace($old,"set { if ($same) return; Changing(); $current = value; $other = value; Changed(true); }")
}
[IO.File]::WriteAllText($corePath,$core)
$bodyPath=Join-Path $cache 'enginesim\Shim\Engine\Physics2D.cs'
$body=[IO.File]::ReadAllText($bodyPath)
$bodyDeclaration='public sealed class Rigidbody2D : Component'
if(($body.Split([string[]]@($bodyDeclaration),[StringSplitOptions]::None).Length-1) -ne 1) { throw 'Unexpected Rigidbody2D declaration.' }
[IO.File]::WriteAllText($bodyPath,$body.Replace($bodyDeclaration,'public sealed partial class Rigidbody2D : Component'))
$physicsPath = Join-Path $cache 'enginesim\Runtime\PhysicsWorld.cs'
$physics = [IO.File]::ReadAllText($physicsPath)
foreach ($old in @('public sealed class PhysicsWorld :', 'int message = (p.Trigger ? 0 : 3) + phase;')) {
    if (($physics.Split([string[]]@($old), [StringSplitOptions]::None).Length - 1) -ne 1) { throw "Unexpected physics surface: $old" }
}
$physics = $physics.Replace('public sealed class PhysicsWorld :', 'public sealed partial class PhysicsWorld :').Replace(
    'int message = (p.Trigger ? 0 : 3) + phase;', 'StudioQueued(p); StudioContact?.Invoke(p.A.Collider, p.B.Collider, phase); int message = (p.Trigger ? 0 : 3) + phase;')
$physics = $physics.Replace('            FindLapsedTouches();', '            if (StudioFrames) _lapsed.Clear(); else FindLapsedTouches();')
$mixedSources = 'if (live == s.Sources.Length && s.Data.SourceShapes != null) return s.Data.SourceShapes;'
$movedShape = 'if (!_sim.Rebased && OffLoaded(s) && _sim.Engine.IsRunning)'
foreach ($old in @($mixedSources, $movedShape)) {
    if (($physics.Split([string[]]@($old), [StringSplitOptions]::None).Length - 1) -ne 1) { throw "Unexpected geometry guard surface: $old" }
}
$physics = $physics.Replace($mixedSources, $mixedSources + ' if (StudioFrames) throw new NotSupportedException("Unrepresented composite source combination: " + s.Collider.name);')
$physics = $physics.Replace($movedShape, 'if (StudioFrames && !_sim.Rebased && OffLoaded(s)) throw new NotSupportedException("Unrepresented moved shape: " + s.Collider.name); ' + $movedShape)
$fixtureGuard = '            s.FixturesAs = null;'
$messageGuard = '                _sim.Touch(handlers[i].script);'
foreach($old in @($fixtureGuard,$messageGuard)) {
    if (($physics.Split([string[]]@($old),[StringSplitOptions]::None).Length-1) -ne 1) { throw "Unexpected lifecycle guard surface: $old" }
}
$physics=$physics.Replace($fixtureGuard,'            StudioCheckGeometry(s);' + "`n" + $fixtureGuard).Replace($messageGuard,'                _sim.StudioCheckMessage(handlers[i].script, argument);' + "`n" + $messageGuard)
$dispatch='                handlers[i].call(argument);'
if (($physics.Split([string[]]@($dispatch),[StringSplitOptions]::None).Length-1) -ne 1) { throw 'Unexpected callback dispatch surface.' }
$physics=$physics.Replace($dispatch,'                StudioMessage?.Invoke(handlers[i].script, message, argument, false);' + "`n" + $dispatch + "`n" + '                StudioMessage?.Invoke(handlers[i].script, message, argument, true);')
$externalGuard='            CollectPlayerTouch();'
if (($physics.Split([string[]]@($externalGuard),[StringSplitOptions]::None).Length-1) -ne 1) { throw 'Unexpected custom-map contact guard surface.' }
$physics=$physics.Replace($externalGuard,$externalGuard+"`n"+'            StudioCheckExternalContacts();')
$initialBuild='                Rebuild(state);'
if (($physics.Split([string[]]@($initialBuild),[StringSplitOptions]::None).Length-1) -ne 1) { throw 'Unexpected initial fixture registration surface.' }
# The supported TAS bootstrap registers terrain before actors. Resource object
# enumeration does not retain that physics order. Defer only fixture creation;
# collider IDs and gameplay callback ordering remain in their original order.
# Native micro-tests cover both contact orientations; full route traces gate
# this registration model rather than assuming an ordering from instance IDs.
$deferredBuild='            }' + "`n" + '            foreach (var state in _colliders.Values.OrderBy(s => s.Body == null || s.Body.Rigidbody.bodyType == RigidbodyType2D.Static ? 0 : 1).ThenBy(s => s.Id))' + "`n" + '            {' + "`n" + $initialBuild
$physics=$physics.Replace($initialBuild,$deferredBuild)
$bodySync='                if (!b.TransformDirty && !b.ScaleDirty) continue;'
$bodyWritten='                t.StoreWorldPosition(new Vector3(p.x, p.y, t.m_Position.z));'
foreach ($old in @($bodySync,$bodyWritten)) {
    if (($physics.Split([string[]]@($old),[StringSplitOptions]::None).Length-1) -ne 1) { throw "Unexpected body pose surface: $old" }
}
$physics=$physics.Replace($bodySync,'                StudioBeforeBodySync?.Invoke(b.Rigidbody);' + "`n" + $bodySync)
$physics=$physics.Replace($bodyWritten,$bodyWritten + "`n" + '                StudioBodyPoseWritten?.Invoke(b.Rigidbody);')
$allowSleep='allowSleep = true,'
if(($physics.Split([string[]]@($allowSleep),[StringSplitOptions]::None).Length-1) -ne 1) { throw 'Unexpected body sleep policy.' }
$physics=$physics.Replace($allowSleep,'allowSleep = rb.sleepMode != RigidbodySleepMode2D.NeverSleep,')
[IO.File]::WriteAllText($physicsPath, $physics)
$contactManagerPath=Join-Path $cache 'enginesim\Box2D\src\Dynamics\World\ContactManager.cs'
$contactManager=[IO.File]::ReadAllText($contactManagerPath)
# Measured in the native isolated wake fixture: a new solid pair wakes its
# bodies even before touching. Keep this with the containment rule below;
# changing only wake-up prematurely wakes Test30's first zipper.
$contactInsert='            // Insert into the world, at the end (see m_contactList).'
if(($contactManager.Split([string[]]@($contactInsert),[StringSplitOptions]::None).Length-1) -ne 1) {throw 'Unexpected new-contact wake surface.'}
$contactManager=$contactManager.Replace($contactInsert,"            if (!fixtureA.IsSensor() && !fixtureB.IsSensor()) { bodyA.SetAwake(true); bodyB.SetAwake(true); }`n"+$contactInsert)
$contactStart=$contactManager.IndexOf($contactInsert,[StringComparison]::Ordinal)
$contactEnd=$contactManager.IndexOf('            // Connect to island graph.',$contactStart,[StringComparison]::Ordinal)
if($contactEnd -le $contactStart){throw 'Unexpected world contact insertion surface.'}
# Native rendered-frame terrain contacts are visited newest first. The
# reference turbo host appends them, which changes the first contact returned
# to Movement.OnCollisionStay2D at composite corners.
$contactHead=@'
            // Insert at the head; retain both ends for removal and snapshots.
            c.m_prev = null;
            c.m_next = m_contactList;
            if (m_contactList != null) m_contactList.m_prev = c;
            else m_contactTail = c;
            m_contactList = c;

'@
$contactTail=$contactManager.Substring($contactStart,$contactEnd-$contactStart)
$contactManager=$contactManager.Substring(0,$contactStart)+"            if (StudioNewestFirst) {`n"+$contactHead+"`n            } else {`n"+$contactTail+"            }`n"+$contactManager.Substring($contactEnd)
$contactField='        public b2Contact m_contactList;'
if(($contactManager.Split([string[]]@($contactField),[StringSplitOptions]::None).Length-1) -ne 1){throw 'Unexpected contact policy surface.'}
$contactManager=$contactManager.Replace($contactField,$contactField+"`n"+'        public bool StudioNewestFirst;')
[IO.File]::WriteAllText($contactManagerPath,$contactManager)
$worldPath=Join-Path $cache 'enginesim\Runtime\World.cs'
$world=[IO.File]::ReadAllText($worldPath)
foreach($old in @('        public float FixedDeltaTime;','            Time.fixedDeltaTime = FixedDeltaTime;')){
    if(($world.Split([string[]]@($old),[StringSplitOptions]::None).Length-1) -ne 1){throw 'Unexpected custom-map physics metadata surface.'}
}
$world=$world.Replace('        public float FixedDeltaTime;','        public float FixedDeltaTime; public bool StudioCustomMap;').Replace('            Time.fixedDeltaTime = FixedDeltaTime;','            Time.fixedDeltaTime = FixedDeltaTime; StudioCustomMap = root.TryGetProperty("customMap", out _);')
[IO.File]::WriteAllText($worldPath,$world)
$physics=[IO.File]::ReadAllText($physicsPath)
$continuous='            _b2.SetContinuousPhysics(true);'
if(($physics.Split([string[]]@($continuous),[StringSplitOptions]::None).Length-1) -ne 1){throw 'Unexpected custom-map contact policy initialization.'}
$physics=$physics.Replace($continuous,$continuous+"`n"+'            _b2.m_contactManager.StudioNewestFirst = world.StudioCustomMap;')
[IO.File]::WriteAllText($physicsPath,$physics)
$treePath=Join-Path $cache 'enginesim\Box2D\src\Collision\DynamicTree.cs'
$tree=[IO.File]::ReadAllText($treePath)
$treeStart=$tree.IndexOf("`t`t`tif (treeAabb.Contains(aabb))",[StringComparison]::Ordinal)
if($treeStart -lt 0) {throw 'Unexpected broad-phase containment surface.'}
$treeEnd=$tree.IndexOf("`t`t`tTouch(proxy);",$treeStart,[StringComparison]::Ordinal)
if($treeEnd -lt $treeStart) {throw 'Unexpected broad-phase containment surface.'}
# Preserve the existing predictive bound while it contains the actual AABB.
# The newer shrink heuristic destroys native speculative-pair lifetime.
$tree=$tree.Substring(0,$treeStart)+"`t`t`tif (treeAabb.Contains(aabb)) return false;`r`n`r`n"+$tree.Substring($treeEnd)
[IO.File]::WriteAllText($treePath,$tree)
$shopPath = Join-Path $cache 'enginesim\Runtime\Shop.cs'
$shop = [IO.File]::ReadAllText($shopPath)
if (!$shop.Contains('public sealed class Shop')) { throw 'Unexpected reference Shop declaration.' }
$syncLocal = 'bool unlocksChange = false;'
if (!$shop.Contains($syncLocal)) { throw 'Unexpected purchase synchronization boundary.' }
$shop=$shop.Replace($syncLocal,'if (_sim.Physics.StudioFrames) StudioSyncLocals(); '+$syncLocal)
$purchaseGuard='if (box.upgrade == localUpgrades.localUpgradeSet.GLOBAL && box.globalUpgrade == globalStats.globalUpgradeSet.spawnNewAtom && !AtomPrestige(box, currency)) return;'
if (!$shop.Contains($purchaseGuard)) { throw 'Unexpected affordable purchase boundary.' }
$shop=$shop.Replace($purchaseGuard,'if (_sim.Physics.StudioFrames) _sim.StudioCheckUpgradePurchase(box); '+$purchaseGuard)
[IO.File]::WriteAllText($shopPath, $shop.Replace('public sealed class Shop', 'public sealed partial class Shop'))
# Keep the original OfflineSolver's archive/mutations/splicing and workers.
# Its execution boundary uses movie frames and our verified scheduler instead
# of MacroEngine input ticks. Only the ignored dependency copy is changed.
$simulationPath = Join-Path $cache 'enginesim\Runtime\Simulation.cs'
$simulation = [IO.File]::ReadAllText($simulationPath)
if (!$simulation.Contains('public sealed class SimSnapshot')) { throw 'Unexpected snapshot declaration.' }
[IO.File]::WriteAllText($simulationPath, $simulation.Replace('public sealed class SimSnapshot', 'public sealed partial class SimSnapshot'))
$solverPath = Join-Path $cache 'enginesim\Runtime\OfflineSolver.cs'
$solver = [IO.File]::ReadAllText($solverPath)
$solverChanges = [ordered]@{
    'public sealed class OfflineSolver' = 'public sealed partial class OfflineSolver'
    '_sim.Capture(tick)' = '_sim.StudioSearchCapture(tick)'
    '_sim.Run(inputs, from,' = '_sim.StudioSearchRun(inputs, from,'
    '_sim.Elapsed(' = '_sim.StudioSearchElapsed('
    '_sim.Fingerprint()' = '_sim.StudioSearchFingerprint()'
    '_sim.Engine.InputsExhausted' = '_sim.StudioSearchInputsExhausted'
    '_goalMode = sim.Goals != null;' = '_goalMode = sim.Goals != null && !sim.StudioHasCheckpoints;'
    'List<InputTick> inputs = _runInputs;' = 'if (_sim.StudioHasCheckpoints) _waypoints = _sim.StudioCheckpoint; List<InputTick> inputs = _runInputs;'
    'if (path.Step(_sim.PlayerStepFrom, p, _sim.Progress, ref _waypoints, !path.OnCourseOnly || _sim.TimingCourse() != null)) _avoided = true;' = 'if (_sim.StudioHasCheckpoints) _waypoints = _sim.StudioCheckpoint; else if (path.Step(_sim.PlayerStepFrom, p, _sim.Progress, ref _waypoints, !path.OnCourseOnly || _sim.TimingCourse() != null)) _avoided = true;'
    'var worker = new Worker(this, index, _createSimulation(), seed, seeds, _partitions[index], plateau);' = 'try { var worker = new Worker(this, index, _createSimulation(), seed, seeds, _partitions[index], plateau);'
    'worker.Loop();' = 'worker.Loop(); } catch (Exception error) { StudioErrors.Enqueue(error); Stop(); }'
}
foreach ($old in $solverChanges.Keys) {
    if (!$solver.Contains($old)) { throw "Unexpected upstream solver boundary: $old" }
    $solver = $solver.Replace($old, $solverChanges[$old])
}
[IO.File]::WriteAllText($solverPath, $solver)
$economyPath = Join-Path $cache 'enginesim\Runtime\Economy.cs'
$economy = [IO.File]::ReadAllText($economyPath)
$abort = 'if (_sim.Engine.IsRunning) _sim.Engine.Abort("Unmodeled economy: " + what);'
if (!$economy.Contains($abort)) { throw 'Unexpected economy failure surface.' }
$economy = $economy.Replace($abort, 'if (_sim.Physics.StudioFrames) throw new NotSupportedException("Unmodeled economy: " + what); ' + $abort)
$cashPayout = 'Add(globalStats.Currencies.Cash, Math.Ceiling(reward));'
$dustPayout = 'Add(globalStats.Currencies.CloneDust, Math.Ceiling(cd));'
foreach ($old in @($cashPayout, $dustPayout)) {
    if (($economy.Split([string[]]@($old), [StringSplitOptions]::None).Length - 1) -ne 1) { throw "Unexpected payout surface: $old" }
}
$economy = $economy.Replace($cashPayout, $cashPayout + ' if (reward > 0) _sim.StudioFloatingNumberRandom();').Replace($dustPayout, $dustPayout + ' _sim.StudioFloatingNumberRandom();')
[IO.File]::WriteAllText($economyPath, $economy)
$allowPath = Join-Path $cache 'enginesim\static-allow.txt'
$allow = [IO.File]::ReadAllLines($allowPath) | Where-Object { !$_.StartsWith('UnityEngine.Time::<time>k__BackingField ') }
[IO.File]::WriteAllLines($allowPath, $allow)
# Keep the original afterimage bookkeeping during fidelity work. Its private
# scalar/list state is observed by the paired traces; do not hide a mismatch.
$cutsPath = Join-Path $cache 'enginesim\cuts.txt'
[IO.File]::WriteAllLines($cutsPath, ([IO.File]::ReadAllLines($cutsPath) | Where-Object { $_ -ne 'Movement::HandleAfterimages' -and $_ -ne 'Movement::TimeStop' }))
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '..\.tools\dotnet-home'
$env:APPDATA = Join-Path $PSScriptRoot '..\.tools\appdata'
$game = Split-Path -Parent (Split-Path -Parent $ManagedDir)
& dotnet build (Join-Path $cache 'enginesim\Runtime\IGTAP.EngineSim.Runtime.csproj') -c Release --nologo "-p:IgtapGame=$game"
if ($LASTEXITCODE -ne 0) { throw 'Engine dependency build failed.' }
& dotnet build (Join-Path $hostRoot 'TasStudio.EngineHost.csproj') -c Release --nologo "-p:EngineRoot=$cache" "-p:ManagedDir=$ManagedDir"
if ($LASTEXITCODE -ne 0) { throw 'Tas Studio frame host build failed.' }
$manifest = @{ ReferenceCommit = $pinned; GameSha256 = (Get-FileHash -LiteralPath (Join-Path $ManagedDir 'Assembly-CSharp.dll') -Algorithm SHA256).Hash; FrameAdapter = 1; Redistributable = $false }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $hostRoot 'bin\Release\net10.0\engine-host.manifest.json') -Encoding UTF8
Write-Host 'Local frame host prepared. Generated game assemblies must not be included in a release ZIP.'
if($RegisterForGame) {
    $data=Join-Path $game 'Recharge\Mods\Tas.Studio\data'
    if(!(Test-Path -LiteralPath (Join-Path $game 'Recharge\Mods\Tas.Studio\mod.json'))) { throw 'Tas Studio is not installed in the selected game.' }
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    $registration=Join-Path $data 'engine-host.json'
    @{Executable=[IO.Path]::GetFullPath((Join-Path $hostRoot 'bin\Release\net10.0\TasStudio.EngineHost.exe'))} | ConvertTo-Json | Set-Content -LiteralPath $registration -Encoding UTF8
    Write-Host "Registered the local host in $registration. Restart the game to use it."
}
