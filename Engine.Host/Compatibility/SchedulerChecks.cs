using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace IGTAP.EngineSim
{
    public sealed partial class Simulation
    {
        private sealed class SchedulerActor : MonoBehaviour
        {
            internal readonly List<string> Log = new List<string>();
            internal Action Callback;
            private void A(){Log.Add("A");Callback?.Invoke();}
            private void B()=>Log.Add("B");
            private void C()=>Log.Add("C");
            internal IEnumerator Routine(){Log.Add("start");yield return null;Log.Add("frame");yield return new WaitForFixedUpdate();Log.Add("fixed");}
        }
        public static int StudioSchedulerChecks()
        {
            int checks=0;
            void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks++;}
            var actor=new GameObject("Scheduler checks").AddComponent<SchedulerActor>();
            var scheduler=new StudioScheduler(_=>{});
            scheduler.Invoke(actor,"A",1,-1);scheduler.Invoke(actor,"B",1,-1);scheduler.Invoke(actor,"C",1,-1);
            scheduler.Now=.99;scheduler.Fire();Check(actor.Log.Count==0,"An invoke fired before its deadline.");
            scheduler.Now=.99999995;scheduler.Fire();Check(actor.Log.Count==0,"An invoke fired early because its deadline was within a floating-point epsilon.");
            actor.Callback=()=>{scheduler.CancelInvoke(actor,"C");scheduler.Invoke(actor,"C",0,-1);};
            scheduler.Now=scheduler.InvokeAnchor=1;scheduler.Fire();
            Check(string.Join(",",actor.Log)=="A,B","Due invokes lost stable order or executed a replacement in the same dispatch.");
            scheduler.Fire();Check(string.Join(",",actor.Log)=="A,B,C","An invoke added by a callback did not run in the following dispatch.");
            actor.Log.Clear();actor.Callback=()=>{scheduler.Invoke(actor,"C",0,-1);scheduler.Fire();};
            scheduler.Invoke(actor,"A",0,-1);scheduler.Fire();
            Check(string.Join(",",actor.Log)=="A,C","Nested dispatch corrupted the outer scratch buffer.");
            actor.Callback=null;actor.Log.Clear();scheduler.Invoke(actor,"B",1,.5f);var saved=scheduler.Capture();
            scheduler.Now=scheduler.InvokeAnchor=2;scheduler.Fire();scheduler.Fire();
            Check(string.Join(",",actor.Log)=="B","Repeating invoke ran twice in one due interval.");
            scheduler.Restore(saved);actor.Log.Clear();scheduler.Now=scheduler.InvokeAnchor=2;scheduler.Fire();
            Check(string.Join(",",actor.Log)=="B","Restore lost a pending timer deadline.");
            scheduler.CancelInvoke(actor,null);actor.Log.Clear();scheduler.StartCoroutine(actor,actor.Routine());
            scheduler.Resume(StudioScheduler.Wait.Frame);Check(string.Join(",",actor.Log)=="start","A frame yield resumed during its starting frame.");
            scheduler.Frame++;scheduler.Resume(StudioScheduler.Wait.Frame);scheduler.Resume(StudioScheduler.Wait.Frame);
            Check(string.Join(",",actor.Log)=="start,frame","A frame coroutine resumed more than once.");
            scheduler.Resume(StudioScheduler.Wait.Fixed);Check(string.Join(",",actor.Log)=="start,frame,fixed","A fixed-phase yield resumed in the wrong phase.");
            return checks;
        }
    }
}
