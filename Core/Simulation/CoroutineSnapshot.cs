using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Igtap.Tas.Core.Simulation
{
    // Copy compiler-generated iterator/closure state, preserving aliases and
    // cycles. Engine-owned object references remain owned by the same world.
    internal static class CoroutineSnapshot
    {
        private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type,FieldInfo[]> Layouts =
            new System.Collections.Concurrent.ConcurrentDictionary<Type,FieldInfo[]>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type,bool> ValueArrays =
            new System.Collections.Concurrent.ConcurrentDictionary<Type,bool>();
        private static FieldInfo[] Layout(Type type) => Layouts.GetOrAdd(type,key => {
            var fields=new List<FieldInfo>();
            for(var owner=key;owner!=null && owner!=typeof(object);owner=owner.BaseType)
                fields.AddRange(owner.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly));
            return fields.ToArray();
        });
        private static bool ContainsOnlyValues(Type type) => ValueArrays.GetOrAdd(type,key => {
            if(key.IsPrimitive || key.IsEnum || key==typeof(decimal))return true;
            if(!key.IsValueType)return false;
            foreach(var field in Layout(key))if(!ContainsOnlyValues(field.FieldType))return false;
            return true;
        });
        private sealed class Identity : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        public static IEnumerator Copy(IEnumerator body, Func<object, bool> external)
            => CopyAll(new[] { body }, external)[0];

        public static IEnumerator[] CopyAll(IEnumerator[] bodies, Func<object, bool> external)
        {
            if (bodies == null || external == null) throw new ArgumentNullException();
            var copied = new Dictionary<object, object>(new Identity());
            object Visit(object value)
            {
                if (value == null || value is string || external(value)) return value;
                Type type = value.GetType();
                if (type.IsPrimitive || type.IsEnum || value is decimal) return value;
                if (copied.TryGetValue(value, out object previous)) return previous;
                if (value is Array source)
                {
                    var array = (Array)source.Clone(); copied.Add(value, array);
                    if(ContainsOnlyValues(type.GetElementType()))return array;
                    var indices = new int[source.Rank];
                    for (int axis = 0; axis < indices.Length; axis++) indices[axis] = source.GetLowerBound(axis);
                    for (int item = 0; item < source.Length; item++)
                    {
                        array.SetValue(Visit(source.GetValue(indices)), indices);
                        for (int axis = indices.Length - 1; axis >= 0; axis--)
                        {
                            if (++indices[axis] <= source.GetUpperBound(axis)) break;
                            indices[axis] = source.GetLowerBound(axis);
                        }
                    }
                    return array;
                }
                if (!type.IsValueType && !(value is IEnumerator) && !type.IsDefined(typeof(CompilerGeneratedAttribute), false))
                    throw new NotSupportedException("Coroutine snapshot needs a copier: " + type.FullName);
                object copy = Clone.Invoke(value, null); copied.Add(value, copy);
                foreach (var field in Layout(type)) field.SetValue(copy, Visit(field.GetValue(value)));
                return copy;
            }
            var result = new IEnumerator[bodies.Length];
            for (int i = 0; i < bodies.Length; i++)
                result[i] = (IEnumerator)Visit(bodies[i] ?? throw new ArgumentNullException(nameof(bodies)));
            return result;
        }
    }
}
