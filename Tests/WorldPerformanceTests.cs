using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;

internal static class WorldPerformanceTests
{
    // Frozen reference byte format. Kept in tests, never a runtime fallback.
    internal static WorldWitness LegacyCompact(WorldWitness input)
    {
        var result=new WorldWitness();var groups=new SortedDictionary<string,StringBuilder>(StringComparer.Ordinal);
        string Group(string key)=>key.Contains("Collider2D#")?"scene-colliders":key.Contains("Rigidbody2D#")?"scene-bodies":
            key.Contains("/currentPlayerPath/")||key.Contains("/currentPlayerScales/")?"course-paths":null;
        void Add(string group,string key,string value,char kind)
        {if(!groups.TryGetValue(group,out var b))groups[group]=b=new StringBuilder();b.Append(kind).Append(key.Length).Append(':').Append(key).Append(value.Length).Append(':').Append(value);}
        foreach(var pair in input.Exact){var group=Group(pair.Key);if(group==null)result.Exact[pair.Key]=pair.Value;else Add(group,pair.Key,pair.Value??"null",'s');}
        foreach(var pair in input.Numbers){var group=Group(pair.Key);if(group==null)result.Numbers[pair.Key]=pair.Value;else Add(group,pair.Key,pair.Value.ToString("R",CultureInfo.InvariantCulture),'n');}
        foreach(var group in groups){using var hash=SHA256.Create();result.Exact["digest/"+group.Key]=Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(group.Value.ToString())));}
        return result;
    }
    public static void Run()
    {
        int checks=0;void Check(bool value,string why){if(!value)throw new Exception(why);checks++;}
        var originalCulture=CultureInfo.CurrentCulture;var input=new WorldWitness();
        input.Exact["cash"]="9007199254740993";input.Exact["null-kept"]=null;input.Numbers["timer"]=-0d;
        input.Exact["/衝突😀/BoxCollider2D#1/empty"]="";input.Exact["/衝突😀/BoxCollider2D#1/null"]=null;
        input.Exact["/orphan\uD800/Rigidbody2D#0/name"]="\uDC00";
        for(int i=0;i<3000;i++)
        {
            input.Numbers["/scene/"+(i%31)+"/BoxCollider2D#"+i+"/position/x"]=(float)(i*.123456789);
            input.Exact["/scene/BoxCollider2D#"+i+"/enabled"]=(i%2==0).ToString();
            input.Numbers["/course/currentPlayerPath/"+i+"/y"]=-i*.731;
        }
        input.Numbers["/BoxCollider2D#0/shared"]=double.PositiveInfinity;input.Exact["/BoxCollider2D#0/shared"]="separate exact value";
        try
        {
            foreach(string culture in new[]{"en-US","de-DE","tr-TR","ar-SA"})
            {
                CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(culture);
                string expected=SearchFiles.Hash(LegacyCompact(input));
                Check(SearchFiles.Hash(input.CompactForReplay())==expected,"Streaming digest preserves the previous byte format under "+culture+".");
                var direct=new WorldWitnessBuilder(true);
                foreach(var pair in input.Numbers)direct.Number(pair.Key,pair.Value);
                foreach(var pair in input.Exact)direct.Exact(pair.Key,pair.Value);
                direct.Exact("/BoxCollider2D#0/shared","overwritten");direct.Exact("/BoxCollider2D#0/shared","separate exact value");
                Check(SearchFiles.Hash(direct.Build())==expected,"Direct capture preserves ordering, duplicate writes, numeric/exact namespaces and Unicode.");
            }
        }
        finally{CultureInfo.CurrentCulture=originalCulture;}
        var full=new WorldWitnessBuilder(false);full.Number("/BoxCollider2D#0/x",1);full.Exact("/BoxCollider2D#0/enabled","True");
        Check(full.Build().Numbers.Count==1 && !full.Build().Exact.ContainsKey("digest/scene-colliders"),"Full savestates retain bulk geometry fields.");
        var checkedBulk=new WorldWitnessBuilder(true,true);bool rejected=false;
        try{checkedBulk.Number("/BoxCollider2D#0/x",double.NaN);}catch(InvalidOperationException){rejected=true;}
        Check(rejected,"Direct hashing cannot hide a non-finite geometry value from validation.");
        var empty=new WorldWitness();Check(SearchFiles.Hash(empty.CompactForReplay())==SearchFiles.Hash(empty),"Empty worlds add no synthetic digest groups.");
        var changed=new WorldWitness();changed.Numbers["/BoxCollider2D#0/x"]=2;
        Check(full.Build().CompactForReplay().Difference(changed.CompactForReplay())!=null,"A geometry change is still rejected by its digest.");
        var cache=new WorldWitnessCache();var stable=new WorldWitness();stable.Numbers["/BoxCollider2D#0/x"]=1;stable.Exact["/Rigidbody2D#1/active"]="True";
        for(int step=0;step<12;step++)
        {
            if(step==2)stable.Numbers["/BoxCollider2D#0/x"]=-0d;
            if(step==3)stable.Numbers["/BoxCollider2D#0/x"]=0d;
            if(step==4)stable.Exact.Remove("/Rigidbody2D#1/active");
            if(step==5)stable.Numbers["/course/currentPlayerScales/99/x"]=3.14159;
            if(step==6)stable.Numbers.Remove("/course/currentPlayerScales/99/x");
            if(step==7)stable.Exact["/new/BoxCollider2D#4/active"]="False";
            if(step==8)cache.Clear();
            using var memo=new WorldWitnessBuilder(true,true,cache);
            foreach(var pair in stable.Numbers)memo.Number(pair.Key,pair.Value);
            foreach(var pair in stable.Exact)memo.Exact(pair.Key,pair.Value);
            Check(SearchFiles.Hash(memo.Build())==SearchFiles.Hash(LegacyCompact(stable)),"Encoding reuse still observes changed values, signed zero and object/history removal.");
        }
        using(var abandoned=new WorldWitnessBuilder(true,true,cache))abandoned.Number("/BoxCollider2D#0/x",9);
        using(var recovered=new WorldWitnessBuilder(true,true,cache))Check(recovered.Build().Exact.Count==0,"An abandoned capture releases its cache and cannot leak stale digest fields.");
        Console.WriteLine("World capture/digest performance checks passed: "+checks);
    }
}
