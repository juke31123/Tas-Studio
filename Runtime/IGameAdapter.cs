using Igtap.Tas.Core;

namespace Igtap.Tas.Runtime
{
    // Keep game knowledge here so a new IGTAP build does not change file/editor code.
    internal interface IGameAdapter
    {
        bool Bind();
        bool IsAlive { get; }
        FrameInput ReadPhysicalInput();
        StateProbe Probe();
    }
}
