using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Igtap.Tas.Core
{
    // Dictionary iteration order is not replay state. Normalize it when making
    // source/cache fingerprints so loading a dictionary under another culture
    // cannot change a hash while every key and value remains identical.
    internal sealed class CanonicalDictionaryJson : JsonConverter
    {
        public override bool CanRead=>false;
        public override bool CanConvert(Type type)=>typeof(IDictionary).IsAssignableFrom(type)&&type.GetInterfaces().Any(t=>t.IsGenericType&&t.GetGenericTypeDefinition()==typeof(IDictionary<,>)&&t.GetGenericArguments()[0]==typeof(string));
        public override void WriteJson(JsonWriter writer,object value,JsonSerializer serializer)
        {
            var dictionary=(IDictionary)value;var keys=new List<string>(dictionary.Count);
            foreach(string key in dictionary.Keys)keys.Add(key);keys.Sort(StringComparer.Ordinal);
            writer.WriteStartObject();foreach(string key in keys){writer.WritePropertyName(key);serializer.Serialize(writer,dictionary[key]);}writer.WriteEndObject();
        }
        public override object ReadJson(JsonReader reader,Type type,object existing,JsonSerializer serializer)=>throw new NotSupportedException();
    }
}
