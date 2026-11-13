using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;
using UnityEngine;

// Owned fixtures in a disposable test game. Never included in the release mod.
internal static class PreviewInteractionProbe
{
    internal static string Mode;
    internal static void Prepare(string root)
    {
        string path=Path.Combine(root,"preview-interaction.txt"); Mode=File.Exists(path)?File.ReadAllText(path).Trim():null;
        if (Mode==null || Mode=="reset") return;
        if(Mode.StartsWith("wall-jump") || Mode=="upgrade-unsupported-poor"){UpgradeBoxProbe.Prepare(Mode);return;}
        var player=UnityEngine.Object.FindFirstObjectByType<Movement>();
        var go=new GameObject("Tas Studio owned spike fixture");
        go.layer=LayerMask.NameToLayer("Spikes"); if(go.layer<0)throw new Exception("Missing native Spikes layer.");
        go.transform.position=player.GetComponent<Rigidbody2D>().position;
        var collider=go.AddComponent<BoxCollider2D>(); collider.size=new Vector2(128,128); collider.isTrigger=true;
        go.AddComponent<spikeScript>();
    }
    internal static FrameInput Input(int frame, FrameInput otherwise)
    {
        if (Mode=="reset") {if(frame==4) return new FrameInput {X=1,Held=Buttons.Reset}; return otherwise;}
        if (Mode=="spike" || Mode=="spike-dash") return new FrameInput {Held=Mode=="spike-dash" && frame==0?Buttons.Dash:0};
        if(Mode?.StartsWith("wall-jump")==true || Mode=="upgrade-unsupported-poor")return default;
        return otherwise;
    }
    internal static void Check(SimulationTrace trace,string directory)
    {
        if(Mode==null)return;
        if(Mode.StartsWith("wall-jump") || Mode=="upgrade-unsupported-poor"){UpgradeBoxProbe.Check(trace,directory,Mode);return;}
        var samples=trace.Samples.Where(s=>s.Phase=="after-pu").ToArray();
        int death=Array.FindIndex(samples,s=>s.Fields["movement/isDead"]=="True");
        if(Mode.StartsWith("spike") && death<0)throw new Exception("Owned spike fixture did not kill the native player.");
        SearchFiles.Write(Path.Combine(directory,"preview-interaction.json"),new {Mode,Passed=true,FirstFatalPu=death<0?-1:death+1,
            FirstFatalFrame=death<0?-1:samples[death].Frame,ResetFrame=Mode=="reset"?4:-1,
            ResetPuCount=Mode=="reset"?samples.Count(s=>s.Frame==4):-1});
    }
}
