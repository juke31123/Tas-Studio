using System;
using System.Collections.Generic;

namespace Igtap.Tas.Core.Simulation
{
    // Developer-only wire data for native/offline physics micro-tests. No game
    // objects, expected movie positions or gameplay callbacks enter this model.
    public sealed class CollisionFixture
    {
        public int Version = 1;
        public float Step;
        public int VelocityIterations, PositionIterations;
        public float ContactOffset;
        public float MaxTranslation, MaxRotation, MaxLinearCorrection, MaxAngularCorrection, Baumgarte, ToiBaumgarte, BounceThreshold;
        public List<CollisionCase> Cases = new List<CollisionCase>();
    }
    public sealed class CollisionCase
    {
        public string Name = "";
        public bool FloorFirst = true;
        public float FloorX, FloorY, FloorWidth, FloorHeight;
        public float X, Y, VX, VY;
        public int Steps = 8;
        public List<CollisionSample> Native = new List<CollisionSample>();
    }
    public sealed class CollisionSample
    {
        public int Step;
        public string X = "", Y = "", VX = "", VY = "", Mass = "", CenterX = "", CenterY = "";
        public List<CollisionContact> Contacts = new List<CollisionContact>();
    }
    public sealed class CollisionContact
    {
        public string X = "", Y = "", NX = "", NY = "", Separation = "", NormalImpulse = "", TangentImpulse = "";
    }
}
