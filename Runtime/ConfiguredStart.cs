using System;
using System.Linq;
using System.Reflection;
using Igtap.Tas.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Igtap.Tas.Runtime
{
    // Release levels 11-15 reuse course 1-5 checkpoints with Overgrown terrain.
    // Coordinates are resolved from the actual start gate, not copied.
    internal static class ConfiguredStart
    {
        public const string MenuScene = "Assets/Scenes/MainMenu.unity";
        public static string SceneFor(TasStartSetup setup) => "Assets/Scenes/Overworld.unity";
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public static void Apply(Movement player, TasStartSetup setup)
        {
            setup.Validate();
            if (player.gameObject.scene.path != SceneFor(setup)) throw new InvalidOperationException("Player is not in the selected gameplay scene.");
            var position = player.respawnPoint;
            if (setup.Course != 0)
            {
                var courses = UnityEngine.Object.FindObjectsByType<courseScript>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Where(c => c.gameObject.scene == player.gameObject.scene && c.courseNumber == setup.SourceCourse).ToArray();
                if (courses.Length != 1)
                    throw new InvalidOperationException("Could not uniquely find the start of course " + setup.Course + ".");
                var course = courses[0];
                // The start gate is authoritative even when resetPoint is unset.
                var gates = course.GetComponentsInChildren<startGate>(true)
                    .Where(g => g.GetComponentInParent<courseScript>() == course).ToArray();
                var reset = gates.Length == 1
                    ? (typeof(startGate).GetField("resetPoint", Fields)?.GetValue(gates[0]) as GameObject)?.transform
                    : null;
                reset = reset ?? course.resetPoint;
                if (reset == null) throw new InvalidOperationException("Course " + setup.Course + " has no start checkpoint.");
                position = reset.position;
            }
            // Save loading is suppressed for this reset. Skip Movement's debug
            // presets, which otherwise override the selected abilities and zone.
            (typeof(Movement).GetField("init", Fields) ?? throw new MissingFieldException("Movement.init")).SetValue(player, false);
            Singleton<globalStats>.Instance.currentA1State = setup.Overgrown ? globalStats.area1states.Overgrown : globalStats.area1states.normal;
            // This is the same field Movement.load assigns from _currentZone in
            // playerdata.txt, set before Movement.Start calls ZoneLoader.LoadZone.
            Singleton<ZoneLoader>.Instance.activeZone = setup.ZoneToLoad;
            ApplyAbilities(player,setup);
            ConfigureVman(setup);
            player.respawnPoint = position;
            player.courseResetPoint = Vector2.zero;
            player.transform.position = new Vector3(position.x, position.y, player.transform.position.z);
            var body = player.GetComponent<Rigidbody2D>();
            if (body == null) throw new InvalidOperationException("The selected player has no physics body.");
            body.position = position; body.linearVelocity = Vector2.zero;
            player.Velocity = player.momentum = Vector2.zero;
            player.cam.setup(position, player.cam.camSize);
        }
        internal static void ApplyAbilities(Movement player,TasStartSetup setup)
        {
            ApplyWorldUpgrades(player.gameObject.scene, setup);
            player.wallJumpUnlocked = setup.WallJump;
            player.doubleJumpUnlocked = setup.DoubleJump;
            player.dashUnlocked = setup.Dash;
            player.omniDashUnlocked = setup.OmniDash;
            player.blockSwapUnlocked = setup.BlockSwap;
            player.lightActive = setup.Light;
            player.maxAirJumps = setup.DoubleJump ? setup.AirJumps : 0;
            player.maxAirDashes = setup.Dash ? setup.AirDashes : 0;
            player.airJumpsLeft = player.maxAirJumps;
            player.airDashesLeft = player.maxAirDashes;
        }
        private static void ConfigureVman(TasStartSetup setup)
        {
            VmanScript.isCurrentlyVman=setup.VmanMode!=0;VmanScript.metaVman=false;
            VmanScript.vmanScore=0;VmanScript.vmanTimeLeft=0;
            if(setup.VmanMode==0)return;
            double duration=setup.VmanMode==2?-2:setup.VmanSeconds-1;
            globalStats.globalUpgradeDict[globalStats.globalUpgradeSet.vmanTime]=duration;
            VmanScript.VmanStatingUpgrades=new System.Collections.Generic.Dictionary<globalStats.globalUpgradeSet,double>{{globalStats.globalUpgradeSet.vmanTime,duration}};
            var owner=Singleton<VmanScript>.Instance??throw new InvalidOperationException("The VMAN controller is unavailable.");
            (typeof(VmanScript).GetMethod("OnEnteredVmanSpeedrun",Fields)??throw new MissingMethodException("VmanScript.OnEnteredVmanSpeedrun")).Invoke(owner,null);
        }
        private static void ApplyWorldUpgrades(Scene scene, TasStartSetup setup)
        {
            globalStats.globalUpgradeDict[globalStats.globalUpgradeSet.zipMoversUnlocked] = setup.Zippers ? 1 : 0;
            globalStats.globalUpgradeDict[globalStats.globalUpgradeSet.unlockJiggleDrops] = setup.RefillOrbs ? 1 : 0;
            // Unlock every native orb group, including later overgrown groups.
            var sequence = typeof(JiggleDropScript).GetField("ActivationSequence", Fields)
                ?? throw new MissingFieldException("JiggleDropScript.ActivationSequence");
            int lastGroup = UnityEngine.Object.FindObjectsByType<JiggleDropScript>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(o => o.gameObject.scene == scene).Select(o => (int)sequence.GetValue(o)).DefaultIfEmpty(0).Max();
            globalStats.globalUpgradeDict[globalStats.globalUpgradeSet.moreRefreshOrbs] = setup.RefillOrbs ? lastGroup : 0;
        }
        public static void FinishWorldSetup(TasStartSetup setup)
        {
            if (setup == null) return;
            // Zone loading can activate objects after Movement.Start. Refresh
            // usability/visuals after their Start methods, before TAS warmup.
            var refresh = typeof(JiggleDropScript).GetMethod("SetActiveVisualState", Fields)
                ?? throw new MissingMethodException("JiggleDropScript.SetActiveVisualState");
            foreach (var orb in UnityEngine.Object.FindObjectsByType<JiggleDropScript>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (orb.gameObject.scene.path == SceneFor(setup) && orb.isActiveAndEnabled)
                    refresh.Invoke(orb, new object[] { false });
        }
    }
}
