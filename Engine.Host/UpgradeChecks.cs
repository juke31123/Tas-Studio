using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using IGTAP.EngineSim;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;

// Developer check against an owned native purchase fixture. It also verifies
// the search/preview guard and rollback, which plain unguarded traces cannot.
internal static class UpgradeChecks
{
    internal static int Run(string directory)
    {
        try
        {
            var trace=SimulationTrace.Read(Path.Combine(directory,"native.trace.json"));EngineFixture.Validate(directory,trace);
            var fixture=SearchFiles.Read<Newtonsoft.Json.Linq.JObject>(Path.Combine(directory,"upgrade-box-validation.json"));
            if(!trace.CaptureAllUpgradeBoxes || (string)fixture["Mode"]!="wall-jump")throw new InvalidDataException("Use a native Wall jump purchase fixture with all-box witnesses.");
            var sim=EngineFixture.Load(directory,trace);
            using var world=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"world.json")));
            sim.ConfigureStudioSearchGuards(world.RootElement,true,true);
            var box=sim.World.All<upgradeBox>().Single(b=>b.GetInstanceID()==(int)fixture["Box"]);
            typeof(Simulation).GetMethod("Touch",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)
                .Invoke(sim,new object[]{box});
            var root=sim.CaptureStudioState();
            string Hash()=>SearchFiles.Hash(EngineFixture.Sample(sim,0,0,"check",true,true,true,true,true).Fields);
            string original=Hash();
            double funds=globalStats.currencyLookup[globalStats.Currencies.Cash],cost=box.upgradeCost;
            double paid=VmanScript.isCurrentlyVman?0:cost;
            void Step()
            {
                for(int frame=0;frame<trace.Inputs.Count;frame++)
                {
                    if(trace.PreparedNextFrames.Count!=0)sim.ConfigureStudioEndPacing(trace.PreparedNextFrames[frame]);
                    sim.StepStudioFrame(EngineFixture.Input(trace.Inputs[frame],frame==0?trace.Previous:trace.Inputs[frame-1]));
                }
            }
            Step();
            if(!sim.Player.wallJumpUnlocked || box.TimesUsed!=1 || box.isActive || globalStats.currencyLookup[globalStats.Currencies.Cash]!=funds-paid)
                throw new Exception("Guarded Wall jump purchase differs.");
            sim.RestoreStudioState(root);if(Hash()!=original)throw new Exception("Purchase rollback differs.");
            // Change only the private fixture to a still-unsupported affordable
            // purchase. The guard must run before spending or granting it.
            box.movementUpgrade=upgradeBox.movementUpgrades.dash;
            bool rejected=false;
            try{Step();}catch(NotSupportedException error){rejected=error.Message.StartsWith("Purchase requires a native mechanic fixture:");}
            if(!rejected || box.TimesUsed!=0 || globalStats.currencyLookup[globalStats.Currencies.Cash]!=funds)
                throw new Exception("An unsupported affordable purchase was not rejected before its effects.");
            sim.RestoreStudioState(root);if(Hash()!=original || box.movementUpgrade!=upgradeBox.movementUpgrades.wallJump)
                throw new Exception("Rejected-purchase rollback differs.");
            SearchFiles.Write(Path.Combine(directory,"upgrade-check.json"),new{Passed=true,trace.Fps,WallJumpPurchase=true,
                CurrencyExact=true,UnsupportedPurchaseRejected=true,RejectedPurchaseSpentNothing=true,AllBoxRollback=true});
            Console.WriteLine("PASS: guarded Wall jump purchase, currency, unsupported-purchase rejection and complete rollback at "+trace.Fps+" FPS.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
