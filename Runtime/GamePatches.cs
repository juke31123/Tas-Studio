using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Igtap.Tas.Runtime
{
    // Patches are in memory only. Game files on disk are never rewritten.
    internal sealed class GamePatches : IDisposable
    {
        private const string HarmonyId = "Tas.Studio.runtime";
        private const string OwnerKey = "Tas.Studio.exclusive-recording-runtime";
        private bool _ownsRuntime;
        private readonly Harmony _harmony = new Harmony(HarmonyId);
        internal static TasSession Session;
        private static bool _menuUpdate;
        public int PatchedMethods { get; private set; }
        private static readonly BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        public void Install(TasSession session)
        {
            // The standalone recorder links this runtime into another assembly.
            // Two owners would rewrite input/time twice and corrupt recordings.
            if(AppDomain.CurrentDomain.GetData(OwnerKey)!=null)
                throw new InvalidOperationException("Another Tas Studio/Validation Recorder runtime is active. Enable only one of these mods.");
            AppDomain.CurrentDomain.SetData(OwnerKey,this); _ownsRuntime=true;
            Session = session;
            try
            {
                foreach (var type in typeof(Movement).Assembly.GetTypes())
                foreach (var method in type.GetMethods(Declared))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                    bool tick = typeof(MonoBehaviour).IsAssignableFrom(type) && method.GetParameters().Length == 0 &&
                        (method.Name == "Update" || method.Name == "FixedUpdate" || method.Name == "LateUpdate");
                    bool iterator = typeof(IEnumerator).IsAssignableFrom(type) && method.Name == "MoveNext" && method.ReturnType == typeof(bool);
                    bool zoneIterator = iterator && BelongsTo(type, typeof(ZoneLoader));
                    bool moverIterator = iterator && BelongsTo(type, typeof(PlatformMover));
                    bool clock = UsesClockOrInput(method);
                    // Jump/respawn helpers schedule timers outside Update too.
                    bool movement = type == typeof(Movement);
                    bool save = type == typeof(Saveloader) && (method.Name == "autosave" || method.Name == "manualSave");
                    save |= typeof(SaveableObject).IsAssignableFrom(type) && method.Name == "save";
                    bool load = typeof(SaveableObject).IsAssignableFrom(type) && method.Name == "load";
                    bool playerStart = type == typeof(Movement) && method.Name == "Start";
                    bool footstep = type == typeof(PlayerAnimationEventProxy) && method.Name == "RelayFootstep";
                    bool menuToggle = type == typeof(pauseMenuScript) && method.Name == "menuButtonPressed";
                    bool menuUpdate = type == typeof(pauseMenuScript) && method.Name == "Update";
                    if (!tick && !iterator && !clock && !movement && !save && !load && !playerStart && !footstep && !menuToggle) continue;
                    var prefix = menuToggle ? nameof(AllowMenuButton) : save ? nameof(AllowSave) : load ? nameof(AllowLoad) : playerStart ? nameof(PreparePlayerStart) : footstep ? nameof(AllowAnimationEvent) : zoneIterator ? nameof(AllowZoneCoroutine) : iterator ? nameof(AllowCoroutine) : tick ? nameof(AllowTick) : null;
                    _harmony.Patch(method,
                        prefix: prefix == null ? null : new HarmonyMethod(typeof(GamePatches), prefix),
                        postfix: zoneIterator ? new HarmonyMethod(typeof(GamePatches), nameof(ZoneCoroutineAdvanced)) : moverIterator ? new HarmonyMethod(typeof(GamePatches), nameof(MoverCoroutineAdvanced)) : playerStart ? new HarmonyMethod(typeof(GamePatches), nameof(PlayerStarted)) : movement && !method.IsStatic ? new HarmonyMethod(typeof(GamePatches), nameof(MovementCalled)) : null,
                        transpiler: zoneIterator ? new HarmonyMethod(typeof(GamePatches), nameof(RewriteZoneWaits)) : clock || movement ? new HarmonyMethod(typeof(GamePatches), nameof(RewriteCalls)) : null,
                        finalizer: menuUpdate ? new HarmonyMethod(typeof(GamePatches), nameof(MenuUpdateFinished)) : null);
                    PatchedMethods++;
                }
            }
            catch { Dispose(); throw; }
        }

        private static bool AllowTick(object __instance, MethodBase __originalMethod)
        {
            var s = Session;
            if (s == null || !s.Active) return true;
            if (s.Frozen) return false;
            // The game's menu update consumes the recorded Pause action at its
            // native Update position, including while game timeScale is zero.
            if (__instance is pauseMenuScript && __originalMethod.Name == "Update") _menuUpdate = true;
            if (__instance is Movement && __originalMethod.Name == "FixedUpdate")
            { s.CountFixedTick(); if (s.Search?.Verifying == true) s.Search.ObserveResources(s.Adapter.SearchResources()); }
            return true;
        }
        private static Exception MenuUpdateFinished(Exception __exception) { _menuUpdate = false; return __exception; }
        private static bool AllowMenuButton()
        {
            if (Session == null || !Session.Active || Session.WaitingForLoad || Session.Mode == SessionMode.Warmup) return true;
            if (_menuUpdate && !Session.Frozen) return true;
            Session.RequestMenuToggle();
            return false;
        }
        private static bool AllowCoroutine(ref bool __result)
        {
            if (Session == null || !Session.Active || !Session.Frozen) return true;
            // Returning false would END the coroutine. Preserve its current yield instead.
            __result = true;
            return false;
        }
        private static bool AllowSave() => Session == null || !Session.Active;
        // Animator initialization can emit footstep events even at timeScale=0.
        // They must not advance the world's RNG while loading or editor-paused.
        // During movie frames the original sound and random draws are preserved.
        private static bool AllowAnimationEvent() => Session == null || !Session.Active || !Session.Frozen;
        private static bool AllowLoad() => Session == null || !Session.SuppressSavedProgressLoad;
        private static void PreparePlayerStart(Movement __instance)
        {
            var s = Session;
            if (s != null && s.Active && s.Mode == SessionMode.Loading && s.Movie.StartSetup != null)
                try { ConfiguredStart.Apply(__instance, s.Movie.StartSetup); }
                catch (Exception error) { s.NotifySetupError(error.Message); }
        }
        private static void PlayerStarted() => Session?.NotifyPlayerStarted();

        private static bool BelongsTo(Type type, Type owner)
        {
            for (; type != null; type = type.DeclaringType) if (type == owner) return true;
            return false;
        }
        private static bool AllowZoneCoroutine(object __instance, ref bool __result)
        {
            if (Session != null && Session.WaitingForLoad)
            {
                Session.NotifyZoneLoadStarted(__instance);
                return true; // Only the zone loader may run inside the frozen startup barrier.
            }
            return AllowCoroutine(ref __result);
        }
        private static void ZoneCoroutineAdvanced(object __instance, bool __result)
        {
            if (!__result) Session?.NotifyZoneLoadFinished(__instance);
        }
        private static void MoverCoroutineAdvanced(IEnumerator __instance, bool __result, bool __runOriginal)
        { if (__runOriginal) SimulationCoroutineCapture.Observe(__instance, __result); }
        private static IEnumerable<CodeInstruction> RewriteZoneWaits(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in RewriteCalls(instructions))
            {
                if (instruction.opcode == OpCodes.Newobj && instruction.operand is ConstructorInfo ctor &&
                    ctor.DeclaringType == typeof(WaitForSeconds) && ctor.GetParameters().Length == 1 && ctor.GetParameters()[0].ParameterType == typeof(float))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(GamePatches), nameof(ZoneLoadWait));
                }
                yield return instruction;
            }
        }
        // ZoneLoader's final loading-screen delay must finish at timeScale=0.
        // Runtime zone transitions retain the original scaled wait.
        private static object ZoneLoadWait(float seconds) => Session != null && Session.WaitingForLoad
            ? (object)new WaitForSecondsRealtime(seconds) : new WaitForSeconds(seconds);

        private static IEnumerable<CodeInstruction> RewriteCalls(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo method)
                {
                    if (method.DeclaringType == typeof(PlayerPrefs))
                    {
                        var parameters = Array.ConvertAll(method.GetParameters(), p => p.ParameterType);
                        var wrapper = typeof(RunPreferences).GetMethod(method.Name, parameters);
                        if (wrapper != null) instruction.operand = wrapper;
                    }
                    string replacement = null;
                    if (method.DeclaringType == typeof(Time))
                    {
                        if (method.Name == "get_unscaledDeltaTime") replacement = nameof(UnscaledDelta);
                        if (method.Name == "get_unscaledTime") replacement = nameof(UnscaledTime);
                        if (method.Name == "get_unscaledTimeAsDouble") replacement = nameof(UnscaledTimeDouble);
                    }
                    if (method.DeclaringType == typeof(InputAction))
                    {
                        if (method.Name == "WasPressedThisFrame") replacement = nameof(Pressed);
                        if (method.Name == "WasPressedThisDynamicUpdate") replacement = nameof(PressedDynamic);
                        if (method.Name == "WasReleasedThisFrame") replacement = nameof(Released);
                        if (method.Name == "IsPressed") replacement = nameof(Held);
                        if (method.Name == "ReadValue" && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(Vector2)) replacement = nameof(Move);
                    }
                    if (method.DeclaringType == typeof(MonoBehaviour))
                    {
                        if (method.Name == "Invoke") replacement = nameof(InvokeMovement);
                        if (method.Name == "CancelInvoke") replacement = method.GetParameters().Length == 0 ? nameof(CancelAllMovement) : nameof(CancelMovement);
                    }
                    if (replacement != null)
                    {
                        // Mutate in place to preserve branch labels and exception blocks.
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(GamePatches), replacement);
                    }
                }
                yield return instruction;
            }
        }
        private static bool UsesClockOrInput(MethodInfo method)
        {
            // Parse operand lengths so an integer/string token cannot look like a call.
            // Avoid Harmony's optional ILGenerator API: the shipped Mono build uses
            // mscorlib ILGenerator while this mod targets netstandard2.1.
            var il = method.GetMethodBody().GetILAsByteArray();
            for (int offset = 0; offset < il.Length;)
            {
                ushort code = il[offset++];
                if (code == 0xfe) code = (ushort)(0xfe00 | il[offset++]);
                var op = Opcodes[code];
                if (op == OpCodes.Call || op == OpCodes.Callvirt)
                {
                    var target = method.Module.ResolveMethod(BitConverter.ToInt32(il, offset));
                    if (target.DeclaringType == typeof(InputAction)) return true;
                    if (target.DeclaringType == typeof(PlayerPrefs)) return true;
                    if (target.DeclaringType == typeof(Time) && (target.Name == "get_unscaledDeltaTime" || target.Name == "get_unscaledTime" || target.Name == "get_unscaledTimeAsDouble")) return true;
                }
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: offset += 1; break;
                    case OperandType.InlineVar: offset += 2; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: offset += 8; break;
                    case OperandType.InlineSwitch: offset += 4 + 4 * BitConverter.ToInt32(il, offset); break;
                    default: offset += 4; break;
                }
            }
            return false;
        }
        private static readonly Dictionary<ushort, OpCode> Opcodes = BuildOpcodes();
        private static bool OwnsTimers(MonoBehaviour target) => target is Movement && Session != null && Session.Active;
        private static void InvokeMovement(MonoBehaviour target, string method, float delay)
        {
            if (!OwnsTimers(target)) { target.Invoke(method, delay); return; }
            target.Invoke(method, delay);
            Session.MovementTimers.ObserveNative(target, method, delay, Session.ScaledNow);
        }
        private static void MovementCalled(Movement __instance, MethodBase __originalMethod)
        {
            if (!OwnsTimers(__instance)) return;
            Session.MovementTimers.NativeCalled(__instance, __originalMethod.Name);
            if (Session.Search?.Verifying == true) Session.Search.ObserveResources(Session.Adapter.SearchResources());
        }
        private static void CancelMovement(MonoBehaviour target, string method)
        {
            if (OwnsTimers(target)) Session.MovementTimers.Cancel(target, method);
            target.CancelInvoke(method);
        }
        private static void CancelAllMovement(MonoBehaviour target)
        {
            if (OwnsTimers(target)) Session.MovementTimers.Cancel(target);
            target.CancelInvoke();
        }
        private static Dictionary<ushort, OpCode> BuildOpcodes()
        {
            var result = new Dictionary<ushort, OpCode>();
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.GetValue(null) is OpCode op) result[unchecked((ushort)op.Value)] = op;
            return result;
        }
        public static float UnscaledDelta() => Session != null && Session.Active ? Session.UnscaledDelta : Time.unscaledDeltaTime;
        public static float UnscaledTime() => (float)UnscaledTimeDouble();
        public static double UnscaledTimeDouble() => Session != null && Session.Active ? Session.UnscaledNow : Time.unscaledTimeAsDouble;
        public static bool Held(InputAction action)
        {
            var s = Session;
            var button = s != null && s.Active ? s.Adapter.ButtonFor(action) : Core.Buttons.None;
            return button != Core.Buttons.None ? !s.Frozen && s.CurrentInput.Down(button) : action.IsPressed();
        }
        public static Vector2 Move(InputAction action) => Session != null && Session.Active && Session.Adapter.IsMove(action)
            ? new Vector2(Session.CurrentInput.X, Session.CurrentInput.Y) : action.ReadValue<Vector2>();
        public static bool Pressed(InputAction action)
        {
            var s = Session;
            var button = s != null && s.Active ? s.Adapter.ButtonFor(action) : Core.Buttons.None;
            return button != Core.Buttons.None ? !s.Frozen && s.CurrentInput.Pressed(button, s.PreviousInput) : action.WasPressedThisFrame();
        }
        public static bool Released(InputAction action)
        {
            var s = Session;
            var button = s != null && s.Active ? s.Adapter.ButtonFor(action) : Core.Buttons.None;
            return button != Core.Buttons.None ? !s.Frozen && s.CurrentInput.Released(button, s.PreviousInput) : action.WasReleasedThisFrame();
        }
        public static bool PressedDynamic(InputAction action)
        {
            var s = Session;
            var button = s != null && s.Active ? s.Adapter.ButtonFor(action) : Core.Buttons.None;
            return button != Core.Buttons.None ? !s.Frozen && s.CurrentInput.Pressed(button, s.PreviousInput) : action.WasPressedThisDynamicUpdate();
        }
        public void Dispose()
        {
            if(!_ownsRuntime)return;
            try { _harmony.UnpatchAll(HarmonyId); }
            finally { Session=null; _menuUpdate=false; _ownsRuntime=false;
                if(ReferenceEquals(AppDomain.CurrentDomain.GetData(OwnerKey),this)) AppDomain.CurrentDomain.SetData(OwnerKey,null); }
        }
    }
}
