using System;
using Box2D;
using UnityEngine;
using Igtap.Tas.Core.Simulation;

internal static class CompatibilityChecks
{
    public static int Run()
    {
        int checks=0;
        void Check(bool value,string message) {if(!value)throw new InvalidOperationException(message);checks++;}
        var zone=new GameObject("Zone"); var group=new GameObject("Platform parent"); var platform=new GameObject("Platform");
        group.transform.SetParent(zone.transform,false); platform.transform.SetParent(group.transform,false);
        zone.transform.localPosition=new Vector3(-30186.60156f,-5792.572266f,0);
        group.transform.localPosition=new Vector3(28469,5623,0); platform.transform.localPosition=Vector3.zero;
        Check(SimulationTraceComparer.Float(platform.transform.position.x)=="c4d6b340","Nested local pose must reproduce the native zone translation.");
        platform.transform.position=new Vector3(BitConverter.Int32BitsToSingle(unchecked((int)0xc4d6b346)),-169.572266f,0);
        Check(SimulationTraceComparer.Float(platform.transform.localPosition.x)=="00000000","Native hierarchy conversion rounds the small platform-local residual through the large parent translation.");
        Check(SimulationTraceComparer.Float(platform.transform.position.x)=="c4d6b340","Rendered transform rounding must not be confused with the physical body position.");

        b2Settings.timeToSleep=.5f;
        var world=new b2World(b2Vec2.Zero);
        var body=world.CreateBody(new b2BodyDef {type=b2BodyType.b2_kinematicBody,allowSleep=true});
        const float step=.0199999921f;
        for(int i=0;i<25;i++)world.Step(step,8,3);
        Check(body.IsAwake(),"A resting actor must not sleep before its native delay.");
        object saved=world.SaveState(); world.Step(step,8,3);
        Check(!body.IsAwake(),"A resting actor must enter sleep at the measured threshold.");
        world.LoadState(saved); body.SetLinearVelocity(new b2Vec2(1,0)); world.Step(step,8,3);
        Check(body.IsAwake(),"A moving branch must wake the platform.");
        world.LoadState(saved); world.Step(step,8,3);
        Check(!body.IsAwake(),"A/B/A restore must include the pending sleep transition.");
        bool SpeculativeWake(bool sensor)
        {
            var testWorld=new b2World(b2Vec2.Zero); var shape=new b2PolygonShape(); shape.SetAsBox(.5f,.5f);
            var platformBody=testWorld.CreateBody(new b2BodyDef {type=b2BodyType.b2_kinematicBody,awake=false});
            platformBody.CreateFixture(new b2FixtureDef {shape=shape,isSensor=sensor});
            var approaching=testWorld.CreateBody(new b2BodyDef {type=b2BodyType.b2_dynamicBody,position=new b2Vec2(1.05f,0),allowSleep=false});
            approaching.CreateFixture(new b2FixtureDef {shape=shape,density=1});
            testWorld.Step(step,8,3);
            Check(testWorld.GetContactList()!=null && !testWorld.GetContactList().IsTouching(),"Speculative fixture must have a broad-phase pair without physical touching.");
            return platformBody.IsAwake();
        }
        Check(SpeculativeWake(false),"A new solid broad-phase pair must wake the sleeping platform before physical touching.");
        Check(!SpeculativeWake(true),"Sensor pairs must not wake sleeping platforms.");
        var tree=new b2DynamicTree();
        var box=new b2AABB(new b2Vec2(0,0),new b2Vec2(1,1));int proxy=tree.CreateProxy(box,null);
        var displaced=new b2AABB(new b2Vec2(1,0),new b2Vec2(2,1));
        Check(tree.MoveProxy(proxy,displaced,new b2Vec2(1,0)),"Moving outside the old proxy must create a predicted broad-phase bound.");
        var retained=tree.GetFatAABB(proxy);
        Check(!tree.MoveProxy(proxy,displaced,b2Vec2.Zero),"Stopping inside an existing bound must preserve native contact-pair lifetime.");
        Check(tree.GetFatAABB(proxy).upperBound.x==retained.upperBound.x,"An unchanged contained proxy must retain its predictive extent.");
        var held=IGTAP.EngineSim.Simulation.StudioSearchInput(new IGTAP.EngineSim.InputTick{x=.5f,y=-1,held=true,dash=true},new IGTAP.EngineSim.StudioInput{Held=1});
        Check(held.X==.5f && held.Y==-1 && held.Held==5 && held.Press==4 && held.Release==0,"Archive input edges are sampled once per movie frame, including analog input.");
        var released=IGTAP.EngineSim.Simulation.StudioSearchInput(default,held);
        Check(released.Held==0 && released.Release==5,"Releasing a movie-frame input retains both jump and dash edges.");
        bool rejected=false;try{IGTAP.EngineSim.Simulation.StudioSearchInput(new IGTAP.EngineSim.InputTick{dashJump=3},default);}catch(NotSupportedException){rejected=true;}
        Check(rejected,"Upstream macro-tick technique offsets must not silently become movie frames.");
        Console.WriteLine("Prepared-host compatibility checks passed: "+checks); return 0;
    }
}
