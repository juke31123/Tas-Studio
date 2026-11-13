using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Igtap.Tas.Core
{
    // Full savestates retain every scalar. Periodic replay observations can
    // collect bulk fields directly, avoiding a full sorted tree that is then
    // immediately discarded. The digest byte stream is exactly the old format.
    public sealed class WorldWitnessBuilder : IDisposable
    {
        private readonly WorldWitness _world=new WorldWitness();
        private readonly WorldWitnessCache _cache;
        private readonly bool _validate;
        private bool _built,_released;
        public WorldWitnessBuilder(bool compact,bool validateNumbers=false,WorldWitnessCache cache=null)
        {if(compact){_cache=cache??new WorldWitnessCache();_cache.Begin();}_validate=validateNumbers;}
        private static int Group(string key)=>key.Contains("Collider2D#")?0:key.Contains("Rigidbody2D#")?1:
            key.Contains("/currentPlayerPath/")||key.Contains("/currentPlayerScales/")?2:-1;
        public void Exact(string key,string value)
        {
            if(_released)throw new ObjectDisposedException(nameof(WorldWitnessBuilder));
            if(_built)throw new InvalidOperationException("Witness already built.");
            int group=_cache==null?-1:Group(key);
            if(group<0)_world.Exact[key]=value;else _cache.Exact(group,key,value??"null");
        }
        public void Number(string key,double value)
        {
            if(_released)throw new ObjectDisposedException(nameof(WorldWitnessBuilder));
            if(_validate && (double.IsNaN(value)||double.IsInfinity(value)))throw new InvalidOperationException("Non-finite savestate value: "+key);
            if(_built)throw new InvalidOperationException("Witness already built.");
            int group=_cache==null?-1:Group(key);
            if(group<0)_world.Numbers[key]=value;else _cache.Number(group,key,value);
        }
        public WorldWitness Build()
        {
            if(_built)return _world;
            if(_released)throw new ObjectDisposedException(nameof(WorldWitnessBuilder));
            try
            {
                if(_cache!=null)for(int i=0;i<3;i++)
                {
                    string digest=_cache.Digest(i);
                    if(digest!=null)_world.Exact["digest/"+(i==0?"scene-colliders":i==1?"scene-bodies":"course-paths")]=digest;
                }
                _built=true;return _world;
            }
            finally{Dispose();}
        }
        public void Dispose(){if(!_released){_released=true;_cache?.End();}}
    }

    // Cache only encoding, never a field's observation. Every capture supplies
    // every current value; removals/new keys rebuild the ordinal layout. No Unity
    // references or old world witnesses are retained here.
    public sealed class WorldWitnessCache
    {
        private sealed class Token
        {
            internal string Key,Text;internal long Bits,Seen;internal byte[] Bytes;
        }
        private sealed class Fields
        {
            internal readonly Dictionary<string,Token> Values=new Dictionary<string,Token>(StringComparer.Ordinal);
            internal Token[] Ordered=Array.Empty<Token>();internal bool Changed;internal int Seen;
            internal Token Observe(string key,long epoch)
            {
                if(!Values.TryGetValue(key,out var token)){token=new Token{Key=key};Values.Add(key,token);Changed=true;}
                if(token.Seen!=epoch){token.Seen=epoch;Seen++;}return token;
            }
            internal Token[] Finish(long epoch)
            {
                if(Seen!=Values.Count)
                {foreach(var key in Values.Where(p=>p.Value.Seen!=epoch).Select(p=>p.Key).ToArray())Values.Remove(key);Changed=true;}
                if(Changed){Ordered=Values.Values.ToArray();Array.Sort(Ordered,(a,b)=>StringComparer.Ordinal.Compare(a.Key,b.Key));Changed=false;}
                return Ordered;
            }
        }
        private readonly Fields[] _exact={new Fields(),new Fields(),new Fields()},_numbers={new Fields(),new Fields(),new Fields()};
        private readonly byte[] _buffer=new byte[65536];
        private bool _busy;private long _epoch;private int _owner;
        internal void Begin()
        {
            int thread=System.Threading.Thread.CurrentThread.ManagedThreadId;
            if(_busy || _owner!=0 && _owner!=thread)throw new InvalidOperationException("Witness encoding cache must stay on its owning capture thread.");
            _owner=thread;_busy=true;_epoch++;
            foreach(var f in _exact)f.Seen=0;foreach(var f in _numbers)f.Seen=0;
        }
        internal void End()=>_busy=false;
        public void Clear()
        {if(_busy)throw new InvalidOperationException("A capture is using this cache.");foreach(var f in _exact.Concat(_numbers)){f.Values.Clear();f.Ordered=Array.Empty<Token>();f.Changed=false;}}
        private static byte[] Encode(char kind,string key,string value)=>Encoding.UTF8.GetBytes(kind+key.Length.ToString(CultureInfo.CurrentCulture)+":"+key+value.Length.ToString(CultureInfo.CurrentCulture)+":"+value);
        internal void Exact(int group,string key,string value)
        {var t=_exact[group].Observe(key,_epoch);if(t.Bytes==null || t.Text!=value){t.Text=value;t.Bytes=Encode('s',t.Key,value);}}
        internal void Number(int group,string key,double value)
        {
            var t=_numbers[group].Observe(key,_epoch);long bits=BitConverter.DoubleToInt64Bits(value);
            if(t.Bytes==null || t.Bits!=bits){t.Bits=bits;t.Bytes=Encode('n',t.Key,value.ToString("R",CultureInfo.InvariantCulture));}
        }
        internal string Digest(int group)
        {
            var exact=_exact[group].Finish(_epoch);var numbers=_numbers[group].Finish(_epoch);
            if(exact.Length+numbers.Length==0)return null;
            using var hash=SHA256.Create();int used=0;
            void Add(byte[] bytes)
            {
                int at=0;while(at<bytes.Length)
                {
                    int count=Math.Min(bytes.Length-at,_buffer.Length-used);Buffer.BlockCopy(bytes,at,_buffer,used,count);used+=count;at+=count;
                    if(used==_buffer.Length){hash.TransformBlock(_buffer,0,used,null,0);used=0;}
                }
            }
            foreach(var t in exact)Add(t.Bytes);foreach(var t in numbers)Add(t.Bytes);
            hash.TransformFinalBlock(_buffer,0,used);return Convert.ToBase64String(hash.Hash);
        }
    }
}
