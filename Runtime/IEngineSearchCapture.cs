using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;

namespace Igtap.Tas.Runtime
{
    internal interface IEngineSearchCapture
    {
        void BeginEngineCapture();
        void ExportEngineBootstrap(TasSession session,string directory);
        SimulationSample CaptureEngineFrame(TasSession session,int frame,int pus);
        void CaptureEngineSummary(TasSession session,SearchJob job);
        void EndEngineCapture();
    }
}
