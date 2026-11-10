using System;
namespace IGTAP.EngineSim
{
    public sealed partial class Simulation
    {
        private float[][] _studioCheckpoints=Array.Empty<float[]>();
        private int _studioCheckpoint;
        public bool StudioHasCheckpoints=>_studioCheckpoints.Length>0;
        public int StudioCheckpoint=>_studioCheckpoint;
        public void ConfigureStudioCheckpoints(float[][] points)
        {
            _studioCheckpoints=Array.ConvertAll(points,p=>(float[])p.Clone());_studioCheckpoint=0;AdvanceStudioCheckpoint();
        }
        private void AdvanceStudioCheckpoint()
        {
            var body=Physics.PlayerBody();var origin=WorldOriginInScene;
            while(_studioCheckpoint<_studioCheckpoints.Length)
            {
                var p=_studioCheckpoints[_studioCheckpoint];double x=body.px-origin.x-p[0],y=body.py-origin.y-p[1];
                if(x*x+y*y>p[2]*p[2])break;_studioCheckpoint++;
            }
        }
        private bool StudioCheckpointsComplete=>!StudioHasCheckpoints || _studioCheckpoint==_studioCheckpoints.Length;
    }
}
