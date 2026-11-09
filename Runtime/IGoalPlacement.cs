using System;
namespace Igtap.Tas.Runtime
{
    internal interface IGoalPlacement : IDisposable
    {
        bool Active { get; }
        void Begin();
        void End();
        void Tick();
        void Pan(float x, float y);
    }
}
