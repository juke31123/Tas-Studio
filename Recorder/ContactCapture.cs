using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;
using Newtonsoft.Json;
using UnityEngine;

namespace Igtap.Tas.Recorder
{
    internal sealed class ContactCapture : IDisposable
    {
        private const string HarmonyId="Tas.Studio.Recorder.contacts";
        private const int Limit=20000;
        private static ContactCapture _active;
        private readonly RecorderController _controller;
        private readonly Harmony _harmony=new Harmony(HarmonyId);
        private StreamWriter _writer;
        private string _directory;
        private int _count,_dropped;
        private string _error="";
        private sealed class Event
        {
            public int Frame,Pu,Target,Other;
            public string Method,Phase,Time,FixedTime;
            public SortedDictionary<string,string> Before,After;
        }
        public ContactCapture(RecorderController controller)
        {
            _controller=controller;_active=this;
            try {
                foreach(var type in typeof(Movement).Assembly.GetTypes().Where(t=>typeof(MonoBehaviour).IsAssignableFrom(t)))
                foreach(var method in type.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)) {
                    bool contact=method.Name=="OnTriggerEnter2D" || method.Name=="OnTriggerExit2D" || method.Name=="OnCollisionEnter2D" || method.Name=="OnCollisionExit2D";
                    bool spring=type==typeof(Movement) && method.Name.IndexOf("Spring",StringComparison.OrdinalIgnoreCase)>=0;
                    bool purchase=type==typeof(upgradeBox) && method.Name=="buyUpgrade";
                    if((!contact && !spring && !purchase) || method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody()==null)continue;
                    _harmony.Patch(method,prefix:new HarmonyMethod(typeof(ContactCapture),nameof(Before)),postfix:new HarmonyMethod(typeof(ContactCapture),nameof(After)));
                }
            } catch {Dispose();throw;}
        }
        public void Begin(string directory)
        {
            End();_directory=directory;_count=_dropped=0;_error="";
            _writer=new StreamWriter(new GZipStream(new FileStream(Path.Combine(directory,"contacts.jsonl.gz"),FileMode.CreateNew,FileAccess.Write,FileShare.Read),System.IO.Compression.CompressionLevel.Fastest),new UTF8Encoding(false));
        }
        private static SortedDictionary<string,string> Fields(object target)
        {var fields=new SortedDictionary<string,string>(StringComparer.Ordinal);SimulationFieldCapture.AddScript(fields,target,"state");return fields;}
        private static void Before(object __instance,MethodBase __originalMethod,object[] __args,out Event __state)
        {
            __state=null;var active=_active;if(active?._writer==null || !active._controller.Recording)return;
            if(active._controller.Player==null)return;
            try {
                var target=__instance as Component;if(target==null)return;
                var other=__args.OfType<Collider2D>().FirstOrDefault() ?? __args.OfType<Collision2D>().FirstOrDefault()?.collider;
                var player=active._controller.Player.gameObject;
                if(target.gameObject!=player && other?.gameObject!=player && other?.attachedRigidbody?.gameObject!=player)return;
                if(active._count>=Limit){active._dropped++;return;}
                __state=new Event {Frame=active._controller.Frame,Pu=active._controller.Pu,Target=target.GetInstanceID(),Other=other==null ? 0 : other.GetInstanceID(),
                    Method=__originalMethod.DeclaringType.FullName+"."+__originalMethod.Name,Phase=Time.inFixedTimeStep ? "fixed" : "frame",
                    Time=SimulationTraceComparer.Double(Time.timeAsDouble),FixedTime=SimulationTraceComparer.Double(Time.fixedTimeAsDouble),Before=Fields(target)};
            } catch(Exception e){active._error=e.Message;}
        }
        private static void After(object __instance,Event __state)
        {
            if(__state==null || _active?._writer==null)return;
            try {__state.After=Fields(__instance);_active._writer.WriteLine(JsonConvert.SerializeObject(__state,SearchFiles.Settings));_active._count++;}
            catch(Exception e){_active._error=e.Message;}
        }
        public void End()
        {
            if(_writer==null)return;
            try {_writer.Dispose();}
            finally {_writer=null;SearchFiles.Write(Path.Combine(_directory,"contacts-status.json"),new {Events=_count,DroppedAtLimit=_dropped,Error=_error,Scope="Player enter/exit contacts, purchases and Movement spring helpers; stay callbacks are intentionally omitted."});}
        }
        public void Dispose()
        {try{End();}finally{_harmony.UnpatchAll(HarmonyId);if(ReferenceEquals(_active,this))_active=null;}}
    }
}
