using System;
using System.Linq;
using System.Text.Json;
using System.Reflection;

namespace IGTAP.EngineSim
{
    public sealed partial class Simulation
    {
        internal void StudioFloatingNumberRandom()
        {
            if (!Physics.StudioFrames) return;
            var spawner=Singleton<FloatingNumberScript>.Instance ?? throw new NotSupportedException("Floating-number RNG owner is missing.");
            bool enabled=(bool)typeof(FloatingNumberScript).GetField("ActuallySpawnText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(spawner);
            if (!enabled) return;
            // The bound game method makes six unconditional float Range calls
            // for angular/linear velocity, drag and lifetime after this guard.
            // Their values affect text only; their random-state writes affect
            // later gameplay rolls and must remain in payout order.
            for (int draw=0; draw<6; draw++) UnityEngine.Random.Draw();
        }
        public void ConfigureStudioEconomy()
        {
            // The world export already contains the live clone/local-upgrade
            // fields. Empty overrides mean read those fields, not zero them.
            var definition=JsonSerializer.SerializeToElement(new {
                currencies=globalStats.currencyLookup.Select(p => new object[] {(int)p.Key,p.Value}).ToArray(),
                boxes=Array.Empty<object>(), clones=Array.Empty<object>()
            });
            Shop.LoadStart(definition, (_, __) => throw new InvalidOperationException("Unexpected economy field override."));
            Shop.StudioSyncCurrencies();
        }
    }
    public sealed partial class Shop
    {
        internal void StudioSyncLocals()
        {
            if(Economy==null)return;
            // Economy owns branchable values. Keep the original game objects
            // coherent too, for native script reads and purchase witnesses.
            foreach(var local in _sim.World.All<localUpgrades>())
            {
                var dict=local.localUpgradeDict;if(dict==null)continue;
                foreach(var key in dict.Keys.ToArray())dict[key]=Economy.LocalOf(local,key);
            }
        }
        internal void StudioSyncCurrencies()
        {
            foreach (var currency in globalStats.currencyLookup.Keys.ToArray())
                globalStats.currencyLookup[currency]=Available((int)currency);
        }
    }
}
