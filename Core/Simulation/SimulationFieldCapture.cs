using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Collections;

namespace Igtap.Tas.Core.Simulation
{
    // Shared by the native and retargeted hosts; type identity is intentionally
    // irrelevant because Unity types belong to different assemblies there.
    public static class SimulationFieldCapture
    {
        private const BindingFlags InstanceFields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type,string),FieldInfo> Fields=new System.Collections.Concurrent.ConcurrentDictionary<(Type,string),FieldInfo>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type,string),PropertyInfo> Properties=new System.Collections.Concurrent.ConcurrentDictionary<(Type,string),PropertyInfo>();
        private sealed class CapturedField {internal FieldInfo Field;internal FieldInfo[] Axes;internal int Kind;}
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type,CapturedField[]> Scripts=new System.Collections.Concurrent.ConcurrentDictionary<Type,CapturedField[]>();
        private static readonly string[] OrbFields={"cooldown","usable","refreshDash","refreshJump","ActivationSequence"};
        private static readonly string[] CashFields={"TimesUsed","upgradeCost","boxCooldown","isActive","Cap"};
        private static FieldInfo Field(Type type,string name)=>Fields.GetOrAdd((type,name),key=>key.Item1.GetField(key.Item2,InstanceFields)??throw new MissingFieldException(key.Item1.FullName,key.Item2));
        private static PropertyInfo Property(Type type,string name)=>Properties.GetOrAdd((type,name),key=>key.Item1.GetProperty(key.Item2)??throw new MissingMemberException(key.Item1.FullName,key.Item2));
        private static CapturedField[] Script(Type type)=>Scripts.GetOrAdd(type,key=>{
            var result=new List<CapturedField>();
            foreach(var field in key.GetFields(InstanceFields))
            {
                if(field.DeclaringType.Assembly!=key.Assembly)continue;
                var valueType=field.FieldType;int kind=valueType==typeof(float)?1:valueType==typeof(double)?2:0;
                FieldInfo[] axes=null;
                if(valueType.FullName=="UnityEngine.Vector2")axes=new[]{Field(valueType,"x"),Field(valueType,"y")};
                else if(valueType.FullName=="UnityEngine.Vector3")axes=new[]{Field(valueType,"x"),Field(valueType,"y"),Field(valueType,"z")};
                else if(kind==0 && !valueType.IsEnum && !valueType.IsPrimitive)continue;
                result.Add(new CapturedField{Field=field,Axes=axes,Kind=kind});
            }
            return result.ToArray();
        });
        public static object AddMover(SortedDictionary<string, string> into, object mover, int id)
        {
            string key = "mover/" + id.ToString(CultureInfo.InvariantCulture);
            AddScript(into, mover, key);
            object transform = Property(mover.GetType(),"transform").GetValue(mover);
            object body = Field(mover.GetType(),"body").GetValue(mover)
                ?? throw new InvalidOperationException("Moving platform has no physics body.");
            void Vector(object owner, string property, string suffix)
            {
                object value = Property(owner.GetType(),property).GetValue(owner);
                AddVector(into, key + "/" + suffix, (float)Field(value.GetType(),"x").GetValue(value), (float)Field(value.GetType(),"y").GetValue(value));
            }
            Vector(transform, "position", "position"); Vector(transform, "localPosition", "local-position");
            Vector(body, "position", "body"); Vector(body, "linearVelocity", "velocity");
            return body;
        }
        public static void AddOrb(SortedDictionary<string, string> into, object orb, int id)
        {
            string prefix = "orb/" + id.ToString(CultureInfo.InvariantCulture) + "/";
            foreach (string name in OrbFields)
            {
                var field = Field(orb.GetType(),name);
                object value = field.GetValue(orb);
                into[prefix + name] = value is float f ? SimulationTraceComparer.Float(f) : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }
        public static void AddPlayer(SortedDictionary<string, string> into, object player) => AddScript(into, player, "movement");
        // Purchase witnesses deliberately omit visual-only Update fields such as
        // isOnscreen/canAfford. Exported world data supplies immutable box config.
        public static void AddCashBox(SortedDictionary<string,string> into,object box,int id)
        {
            string prefix="upgrade-box/"+id+"/";
            foreach(string name in CashFields)
            {
                object value=Field(box.GetType(),name).GetValue(box);
                into[prefix+name]=value is float f?SimulationTraceComparer.Float(f):value is double d?SimulationTraceComparer.Double(d):Convert.ToString(value,CultureInfo.InvariantCulture);
            }
        }
        public static void AddLocalUpgrades(SortedDictionary<string,string> into,object owner,int id)
        {
            string key="local-upgrades/"+id;
            var values=(IDictionary)Field(owner.GetType(),"localUpgradeDict").GetValue(owner);
            if(values==null){into[key+"/missing"]="true";return;}
            into[key+"/count"]=values.Count.ToString(CultureInfo.InvariantCulture);
            foreach(DictionaryEntry item in values)into[key+"/"+Convert.ToInt64(item.Key,CultureInfo.InvariantCulture)]=SimulationTraceComparer.Double(Convert.ToDouble(item.Value,CultureInfo.InvariantCulture));
        }
        public static void AddScript(SortedDictionary<string, string> into, object player, string prefix)
        {
            foreach (var capture in Script(player.GetType()))
            {
                object value = capture.Field.GetValue(player); string key = prefix + "/" + capture.Field.Name;
                if(capture.Kind==1)into[key]=SimulationTraceComparer.Float((float)value);
                else if(capture.Kind==2)into[key]=SimulationTraceComparer.Double((double)value);
                else if(capture.Axes!=null)foreach(var axis in capture.Axes)into[key+"/"+axis.Name]=SimulationTraceComparer.Float((float)axis.GetValue(value));
                else into[key]=Convert.ToString(value,CultureInfo.InvariantCulture);
            }
        }
        public static void AddVector(SortedDictionary<string, string> into, string key, float x, float y)
        { into[key + "/x"] = SimulationTraceComparer.Float(x); into[key + "/y"] = SimulationTraceComparer.Float(y); }
        public static void AddRandom(SortedDictionary<string, string> into, object state)
        {
            for (int i = 0; i < 4; i++)
            {
                var field = Field(state.GetType(),"s"+i);
                into["random/s" + i] = Convert.ToString(field.GetValue(state), CultureInfo.InvariantCulture);
            }
        }
        public static void AddEconomy(SortedDictionary<string, string> into, IDictionary currencies, IDictionary upgrades)
        {
            void Capture(string key, IDictionary source)
            {
                if (source == null) { into[key + "/missing"] = "true"; return; }
                foreach (DictionaryEntry entry in source)
                    into[key + "/" + Convert.ToInt64(entry.Key, CultureInfo.InvariantCulture)] = SimulationTraceComparer.Double(Convert.ToDouble(entry.Value, CultureInfo.InvariantCulture));
            }
            Capture("economy/currencies", currencies); Capture("economy/upgrades", upgrades);
        }
    }
}
