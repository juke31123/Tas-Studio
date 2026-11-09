using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine.LowLevel;
namespace Igtap.Tas.Recorder
{
    internal sealed class PassiveHooks : IDisposable
    {
        private const string Id="Tas.Studio.Recorder.passive";private readonly Harmony _harmony=new Harmony(Id);private static RecorderController _controller;private static Action _hotkeys;
        private sealed class EarlyMarker{}private sealed class BeforeMarker{}private sealed class AfterMarker{}private sealed class EndMarker{}
        public PassiveHooks(RecorderController controller,Action hotkeys)
        {
            _controller=controller;_hotkeys=hotkeys;try{
                _harmony.Patch(typeof(Movement).GetMethod("FixedUpdate",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public),prefix:new HarmonyMethod(typeof(PassiveHooks),nameof(BeforeFixed)));
                _harmony.Patch(typeof(Movement).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public),prefix:new HarmonyMethod(typeof(PassiveHooks),nameof(BeforeInput)));
                var loop=PlayerLoop.GetCurrentPlayerLoop();if(!Insert(ref loop,typeof(UnityEngine.PlayerLoop.EarlyUpdate),typeof(EarlyMarker),Early,2)||!Insert(ref loop,typeof(UnityEngine.PlayerLoop.FixedUpdate.Physics2DFixedUpdate),typeof(BeforeMarker),Before,0)||!Insert(ref loop,typeof(UnityEngine.PlayerLoop.FixedUpdate.Physics2DFixedUpdate),typeof(AfterMarker),After,1)||!Insert(ref loop,typeof(UnityEngine.PlayerLoop.PostLateUpdate),typeof(EndMarker),End,2))throw new NotSupportedException("Native observation points are missing.");PlayerLoop.SetPlayerLoop(loop);
            }catch{Dispose();throw;}
        }
        private static void Safe(Action action){try{action();}catch(Exception e){_controller?.RuntimeError(e);}}
        private static void Early()=>Safe(()=>{_hotkeys?.Invoke();_controller?.BeginFrame();});private static void Before()=>Safe(()=>_controller?.BeforePhysics());private static void After()=>Safe(()=>_controller?.AfterPhysics());private static void End()=>Safe(()=>_controller?.EndFrame());
        // Void prefixes cannot skip the original methods or replace their input.
        private static void BeforeFixed(Movement __instance)=>Safe(()=>_controller?.BeforeFixed(__instance));private static void BeforeInput(Movement __instance)=>Safe(()=>_controller?.BeforeInput(__instance));
        private static bool Insert(ref PlayerLoopSystem node,Type target,Type marker,PlayerLoopSystem.UpdateFunction callback,int mode)
        {
            if(mode==2 && node.type==target){var list=new List<PlayerLoopSystem>(node.subSystemList??Array.Empty<PlayerLoopSystem>());list.Add(new PlayerLoopSystem{type=marker,updateDelegate=callback});node.subSystemList=list.ToArray();return true;}
            if(node.subSystemList==null)return false;for(int i=0;i<node.subSystemList.Length;i++){if(mode!=2 && node.subSystemList[i].type==target){var list=new List<PlayerLoopSystem>(node.subSystemList);list.Insert(i+mode,new PlayerLoopSystem{type=marker,updateDelegate=callback});node.subSystemList=list.ToArray();return true;}if(Insert(ref node.subSystemList[i],target,marker,callback,mode))return true;}return false;
        }
        private static void Remove(ref PlayerLoopSystem node){if(node.subSystemList==null)return;var list=new List<PlayerLoopSystem>();foreach(var child in node.subSystemList){if(child.type==typeof(EarlyMarker)||child.type==typeof(BeforeMarker)||child.type==typeof(AfterMarker)||child.type==typeof(EndMarker))continue;var copy=child;Remove(ref copy);list.Add(copy);}node.subSystemList=list.ToArray();}
        public void Dispose(){try{_harmony.UnpatchAll(Id);}finally{var loop=PlayerLoop.GetCurrentPlayerLoop();Remove(ref loop);PlayerLoop.SetPlayerLoop(loop);_controller=null;_hotkeys=null;}}
    }
}
