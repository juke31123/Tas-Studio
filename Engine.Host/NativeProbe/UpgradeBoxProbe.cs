using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;
using UnityEngine;

// Relocate an existing, initialized game box inside the disposable test scene.
// No synthetic purchase method, asset stub, save file or input recording is used.
internal static class UpgradeBoxProbe
{
    private static int _id;
    private static double _cost, _funds;
    private static bool _initialWall;
    private static bool _vman;
    private static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    internal static void Prepare(string mode)
    {
        var player=UnityEngine.Object.FindFirstObjectByType<Movement>();
        var kind=mode=="upgrade-unsupported-poor"?upgradeBox.movementUpgrades.dash:upgradeBox.movementUpgrades.wallJump;
        var tree=typeof(upgradeBox).GetField("upgradeTree",Flags);
        var box=UnityEngine.Object.FindObjectsByType<upgradeBox>(FindObjectsSortMode.None)
            .First(b=>b.upgrade==localUpgrades.localUpgradeSet.Movement && b.movementUpgrade==kind && b.Cap==1 && !b.buyMax && tree.GetValue(b)==null);
        var collider=box.GetComponents<Collider2D>().FirstOrDefault(c=>c.enabled && c.isTrigger);
        if(collider==null)throw new Exception("Native movement box trigger is missing.");
        // Isolate the purchase callback: placing the whole solid box around
        // the player would create an artificial ceiling/floor penetration.
        foreach(var solid in box.GetComponents<Collider2D>().Where(c=>!c.isTrigger))solid.enabled=false;
        box.transform.position+=(Vector3)(player.GetComponent<Rigidbody2D>().position-(Vector2)collider.bounds.center);
        box.TimesUsed=0;box.isActive=mode!="wall-jump-inactive";
        // The test validates gameplay only and must not award Steam progress.
        typeof(upgradeBox).GetField("triggerAchievement",Flags).SetValue(box,false);
        typeof(upgradeBox).GetField("isSecret",Flags).SetValue(box,false);
        typeof(upgradeBox).GetField("boxCooldown",Flags).SetValue(box,-.01f);
        if(kind==upgradeBox.movementUpgrades.wallJump)player.wallJumpUnlocked=false;
        _initialWall=player.wallJumpUnlocked;
        _vman=VmanScript.isCurrentlyVman;
        _id=box.GetInstanceID();_cost=box.upgradeCost;
        _funds=mode.EndsWith("poor")?0:_cost+100;
        var currency=(globalStats.Currencies)typeof(upgradeBox).GetField("upgradeCurrency",Flags).GetValue(box);
        globalStats.currencyLookup[currency]=_funds;
    }
    internal static void Check(SimulationTrace trace,string directory,string mode)
    {
        if(!trace.CaptureAllUpgradeBoxes)throw new Exception("Movement-box witnesses were not captured.");
        string key="upgrade-box/"+_id+"/";
        var end=trace.Samples.Last(s=>s.Phase=="frame-end");
        bool purchase=mode=="wall-jump";
        int uses=int.Parse(end.Fields[key+"TimesUsed"]);
        bool wall=bool.Parse(end.Fields["movement/wallJumpUnlocked"]);
        if(uses!=(purchase?1:0) || wall!=(purchase||_initialWall))throw new Exception("Native movement-box purchase did not match the fixture.");
        double Number(string name)=>BitConverter.Int64BitsToDouble(Convert.ToInt64(end.Fields[name],16));
        if(Number("economy/currencies/0")!=_funds-(purchase&&!_vman?_cost:0))throw new Exception("Native purchase currency differs.");
        SearchFiles.Write(Path.Combine(directory,"upgrade-box-validation.json"),new{Passed=true,Mode=mode,Box=_id,
            Purchased=purchase,Vman=_vman,Uses=uses,WallJumpUnlocked=wall,StartingCurrency=_funds,Cost=_cost,FinalCurrency=Number("economy/currencies/0")});
    }
}
