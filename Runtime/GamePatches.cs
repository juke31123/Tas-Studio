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
        private readonly Harmony _harmony = new Harmony(HarmonyId);
        internal static TasSession Session;
        public int PatchedMethods { get; private set; }
        private static readonly BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        public void Install(TasSession session)
        {
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
                    bool clock = UsesUnscaledClock(method);
                    // Jump/respawn helpers schedule timers outside Update too.
                    bool movement = type == typeof(Movement);
                    bool save = type == typeof(Saveloader) && (method.Name == "autosave" || method.Name == "manualSave");
                    save |= typeof(SaveableObject).IsAssignableFrom(type) && method.Name == "save";
                    bool load = typeof(SaveableObject).IsAssignableFrom(type) && method.Name == "load";
                    bool playerStart = type == typeof(Movement) && method.Name == "Start";
                    if (!tick && !iterator && !clock && !movement && !save && !load && !playerStart) continue;
                    var prefix = save ? nameof(AllowSave) : load ? nameof(AllowLoad) : playerStart ? nameof(PreparePlayerStart) : zoneIterator ? nameof(AllowZoneCoroutine) : iterator ? nameof(AllowCoroutine) : tick ? nameof(AllowTick) : null;
                    _harmony.Patch(method,
                        prefix: prefix == null ? null : new HarmonyMethod(typeof(GamePatches), prefix),
                        postfix: zoneIterator ? new HarmonyMethod(typeof(GamePatches), nameof(ZoneCoroutineAdvanced)) : playerStart ? new HarmonyMethod(typeof(GamePatches), nameof(PlayerStarted)) : null,
                        transpiler: zoneIterator ? new HarmonyMethod(typeof(GamePatches), nameof(RewriteZoneWaits)) : clock || movement ? new HarmonyMethod(typeof(GamePatches), nameof(RewriteCalls)) : null);
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
            // Pause/menu input is outside the movie schema. TAS controls own pausing.
            if (__instance is pauseMenuScript) return false;
            if (__instance is Movement && __originalMethod.Name == "FixedUpdate") s.CountFixedTick();
            return true;
        }
        private static bool AllowCoroutine(ref bool __result)
        {
            if (Session == null || !Session.Active || !Session.Frozen) return true;
            // Returning false would END the coroutine. Preserve its current yield instead.
            __result = true;
            return false;
        }
        private static bool AllowSave() => Session == null || !Session.Active;
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
                        if (method.Name == "WasReleasedThisFrame") replacement = nameof(Released);
                        if (method.Name == "ReadValue" && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(Vector2)) replacement = nameof(Move);
                    }
                    if (method.DeclaringType == typeof(MonoBehaviour))
                    {
                        if (method.Name == "Invoke") replacement = nameof(InvokeMovement);
                        if (method.Name == "CancelInvoke") replacement = method.GetParameters().Length == 0 ? nameof(CancelAllMovement) : nameof(CancelMovement);
                        if (method.Name == "IsInvoking") replacement = method.GetParameters().Length == 0 ? nameof(IsAnyMovementInvoking) : nameof(IsMovementInvoking);
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
        private static bool UsesUnscaledClock(MethodInfo method)
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
            var callback = AccessTools.Method(target.GetType(), method, Type.EmptyTypes)
                ?? throw new MissingMethodException(target.GetType().Name, method);
            Session.MovementTimers.Schedule(target, method, delay, () => {
                if (target != null) callback.Invoke(target, null);
            });
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
        private static bool IsMovementInvoking(MonoBehaviour target, string method) => OwnsTimers(target)
            ? Session.MovementTimers.Contains(target, method) : target.IsInvoking(method);
        private static bool IsAnyMovementInvoking(MonoBehaviour target) => OwnsTimers(target)
            ? Session.MovementTimers.Contains(target) : target.IsInvoking();
        private static Dictionary<ushort, OpCode> BuildOpcodes()
        {
            var result = new Dictionary<ushort, OpCode>();
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.GetValue(null) is OpCode op) result[unchecked((ushort)op.Value)] = op;
            return result;
        }
        public static float UnscaledDelta() => Session != null && Session.Active ? (Session.Frozen ? 0 : Session.StepSeconds) : Time.unscaledDeltaTime;
        public static float UnscaledTime() => (float)UnscaledTimeDouble();
        public static double UnscaledTimeDouble() => Session != null && Session.Active ? Session.LogicalTime : Time.unscaledTimeAsDouble;
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
        public void Dispose() { _harmony.UnpatchAll(HarmonyId); Session = null; }
    }
}
