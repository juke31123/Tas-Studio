using System;
using System.Threading;

namespace Igtap.Tas.Core.Search
{
    // A world never migrates between thread-pool threads. Work/results cross
    // this boundary; engine objects and native snapshot tokens do not.
    internal sealed class BeamWorkers : IDisposable
    {
        internal sealed class Worker
        {
            internal readonly int Index;
            internal readonly AutoResetEvent Ready = new AutoResetEvent(false), Done = new AutoResetEvent(false);
            internal readonly Thread Thread;
            internal Action<Worker> Work;
            internal Exception Error;
            internal bool Exit;
            internal ISearchSimulation Simulation;
            internal readonly Func<ISearchSimulation> Create;
            internal Worker(int index, Func<ISearchSimulation> create)
            {
                Index = index; Create = create;
                Thread = new Thread(Loop) { IsBackground = true, Name = "Tas Studio Beam " + index };
                Thread.Start();
            }
            internal ISearchSimulation Sim => Simulation ?? (Simulation = Create());
            private void Loop()
            {
                while (true)
                {
                    Ready.WaitOne();
                    if (Exit) return;
                    try { Work(this); }
                    catch (Exception error) { Error = error; }
                    finally { Work = null; Done.Set(); }
                }
            }
        }
        internal readonly Worker[] Items;
        internal BeamWorkers(int count, Func<ISearchSimulation> create)
        {
            Items = new Worker[count];
            for (int i = 0; i < count; i++) Items[i] = new Worker(i, create);
        }
        internal void Run(int index, Action<Worker> action)
        {
            var worker = Items[index]; worker.Error = null; worker.Work = action; worker.Ready.Set();
            worker.Done.WaitOne(); if (worker.Error != null) throw worker.Error;
        }
        internal void RunAll(Action<Worker> action)
        {
            foreach (var worker in Items) { worker.Error = null; worker.Work = action; worker.Ready.Set(); }
            foreach (var worker in Items) worker.Done.WaitOne();
            foreach (var worker in Items) if (worker.Error != null) throw worker.Error;
        }
        // Start with each world's own histories. Idle workers can reconstruct
        // another queue's immutable history, never use its native snapshot.
        internal long RunTasks(System.Collections.Generic.List<int>[] queues, bool steal, Action<Worker,int> action)
        {
            var next=new int[queues.Length]; long stolen=0;
            RunAll(worker => {
                void Drain(int queue)
                {
                    while(true)
                    {
                        int index=Interlocked.Increment(ref next[queue])-1;
                        if(index>=queues[queue].Count)return;
                        if(queue!=worker.Index)Interlocked.Increment(ref stolen);
                        action(worker,queues[queue][index]);
                    }
                }
                Drain(worker.Index);
                if(steal)for(int offset=1;offset<queues.Length;offset++)Drain((worker.Index+offset)%queues.Length);
            });
            return stolen;
        }
        public void Dispose()
        {
            foreach (var worker in Items) { worker.Exit = true; worker.Ready.Set(); }
            foreach (var worker in Items) { worker.Thread.Join(); worker.Ready.Dispose(); worker.Done.Dispose(); }
        }
    }
}
