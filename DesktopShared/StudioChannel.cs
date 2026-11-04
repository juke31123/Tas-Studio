using System;
using System.Collections.Generic;

namespace Igtap.Tas.DesktopModel
{
    // In-process boundary: only detached snapshots/commands cross UI threads.
    // Neither the native window nor this mailbox holds any Unity objects.
    internal sealed class StudioChannel
    {
        private readonly Queue<Command> _commands = new Queue<Command>();
        private readonly object _gate = new object();
        private int _sequence;
        public volatile Snapshot Latest;
        public volatile int Page;
        public volatile int RequestedPage = -1;
        public volatile bool IsOpen, Starting;
        public volatile string Error = "";
        public bool Send(Command command)
        {
            lock (_gate)
            {
                if (!IsOpen || _commands.Count >= 32) return false;
                command.Id = Guid.NewGuid().ToString("N");
                command.Sequence = ++_sequence;
                _commands.Enqueue(command.Copy()); return true;
            }
        }
        public bool TryTake(out Command command)
        {
            lock (_gate) { command = _commands.Count > 0 ? _commands.Dequeue() : null; return command != null; }
        }
        public void Clear() { lock (_gate) _commands.Clear(); Latest = null; }
    }
    internal interface IStudioWindow : IDisposable { void Open(); }
}
