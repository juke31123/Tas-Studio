using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Igtap.Tas.Core.Simulation;

internal static class CoroutineSnapshotTests
{
    [CompilerGenerated]
    private sealed class Closure { public int Value; public Closure Alias, Cycle; public object External; }
    private sealed class Iterator : IEnumerator
    {
        public Closure State;
        public object Current => State.Value;
        public bool MoveNext() { State.Value++; return true; }
        public void Reset() => throw new NotSupportedException();
    }
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
        bool External(object value) => value is Token;
        IEnumerator RealClosure()
        {
            int count = 0;
            return Iterate();
            IEnumerator Iterate() { while (true) { count++; yield return count; } }
        }
        var original = RealClosure(); original.MoveNext();
        var saved = CoroutineSnapshot.Copy(original, External);
        original.MoveNext(); original.MoveNext();
        var restored = CoroutineSnapshot.Copy(saved, External); restored.MoveNext();
        Check((int)restored.Current == 2 && (int)original.Current == 3,
            "Iterator locals and nested closure fields must be isolated from the original branch.");
        var again = CoroutineSnapshot.Copy(saved, External); again.MoveNext();
        Check((int)again.Current == 2, "Restoring a saved coroutine must not mutate the saved copy.");
        var token = new Token(); var closure = new Closure { Value = 7, External = token };
        closure.Alias = closure.Cycle = closure;
        var roots = new IEnumerator[] { new Iterator { State = closure }, new Iterator { State = closure } };
        var copies = CoroutineSnapshot.CopyAll(roots, External);
        var a = (Iterator)copies[0]; var b = (Iterator)copies[1];
        Check(!ReferenceEquals(a.State, closure), "Mutable closure must be copied.");
        Check(ReferenceEquals(a.State, b.State), "Coroutines sharing a closure must share its copied instance.");
        Check(ReferenceEquals(a.State, a.State.Cycle) && ReferenceEquals(a.State, a.State.Alias),
            "Snapshot must preserve aliases and cycles.");
        Check(ReferenceEquals(a.State.External, token), "Engine-owned references must retain their world identity.");
        a.MoveNext();
        Check((int)b.Current == 8 && closure.Value == 7, "Shared copied state must evolve without contaminating the source.");
        var array = new object[] { closure, token, null }; array[2] = array; closure.External = array;
        var arrayCopy = ((Iterator)CoroutineSnapshot.Copy(roots[0], External)).State;
        var values = (object[])arrayCopy.External;
        Check(!ReferenceEquals(values, array) && ReferenceEquals(values[0], arrayCopy), "Coroutine arrays must copy elements and preserve closure aliases.");
        Check(ReferenceEquals(values[1], token) && ReferenceEquals(values[2], values), "Array cycles and engine-owned handles retain their intended identity.");
        values[0] = null;
        Check(ReferenceEquals(array[0], closure), "Changing a restored array cannot contaminate its saved branch.");
        var matrix = Array.CreateInstance(typeof(object), new[] { 2, 1 }, new[] { -1, 3 }); matrix.SetValue(token, -1, 3); closure.External = matrix;
        var matrixCopy = (Array)((Iterator)CoroutineSnapshot.Copy(roots[0], External)).State.External;
        Check(matrixCopy.GetLowerBound(0) == -1 && ReferenceEquals(matrixCopy.GetValue(-1, 3), token), "Multidimensional coroutine arrays preserve bounds and object references.");
        closure.External = new List<int> { 1 };
        bool rejected = false;
        try { CoroutineSnapshot.Copy(roots[0], External); } catch (NotSupportedException) { rejected = true; }
        Check(rejected, "Unmodelled mutable objects must fail rather than leak state between search branches.");
        Console.WriteLine("Coroutine snapshot checks passed: " + checks);
    }
    private sealed class Token { }
}
