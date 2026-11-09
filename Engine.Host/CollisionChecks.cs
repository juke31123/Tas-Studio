using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Box2D;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;

internal static class CollisionChecks
{
    public static int Run(string directory)
    {
        var fixture = SearchFiles.Read<CollisionFixture>(Path.Combine(directory,"collision-fixture.json"));
        if(fixture.Version!=1 || fixture.Cases.Count==0 || fixture.Step<=0) throw new InvalidDataException("Invalid collision micro-fixture.");
        b2Settings.maxTranslation=fixture.MaxTranslation; b2Settings.maxRotation=fixture.MaxRotation*(MathF.PI/180);
        b2Settings.maxLinearCorrection=fixture.MaxLinearCorrection; b2Settings.maxAngularCorrection=fixture.MaxAngularCorrection*(MathF.PI/180);
        b2Settings.baumgarte=fixture.Baumgarte; b2Settings.toiBaumgarte=fixture.ToiBaumgarte; b2Settings.velocityThreshold=fixture.BounceThreshold;
        var reports = new List<object>(); bool defaultPassed=true, controlsSeparated=true;
        foreach(var test in fixture.Cases)
        foreach(string mode in new[]{"exported-world-polygon","local-polygon","local-box","opposite-order-world-polygon"})
        {
            var world = new b2World(b2Vec2.Zero); world.SetContinuousPhysics(true);
            bool local=mode=="local-polygon" || mode=="local-box";
            bool floorFirst=mode=="opposite-order-world-polygon" ? !test.FloorFirst : test.FloorFirst;
            var ground=world.CreateBody(new b2BodyDef {position=local ? new b2Vec2(test.FloorX,test.FloorY) : b2Vec2.Zero});
            b2PolygonShape Box(float x,float y,float width,float height,bool primitive)
            {
                var shape=new b2PolygonShape();
                if(primitive) shape.SetAsBox(width*.5f,height*.5f,new b2Vec2(x,y),0);
                else shape.Set(new[]{new b2Vec2(x+width*.5f,y-height*.5f),new b2Vec2(x+width*.5f,y+height*.5f),
                    new b2Vec2(x-width*.5f,y+height*.5f),new b2Vec2(x-width*.5f,y-height*.5f)},4);
                shape.m_radius=fixture.ContactOffset; return shape;
            }
            var floorFixture=new b2FixtureDef {shape=Box(local ? 0 : test.FloorX,local ? 0 : test.FloorY,test.FloorWidth,test.FloorHeight,mode=="local-box"),friction=0};
            if(floorFirst) ground.CreateFixture(floorFixture);
            var body=world.CreateBody(new b2BodyDef {type=b2BodyType.b2_dynamicBody,position=new b2Vec2(test.X,test.Y),fixedRotation=true,gravityScale=0,bullet=true,allowSleep=false});
            body.CreateFixture(new b2FixtureDef {shape=Box(-5,2,41,72,false),density=1,friction=0});
            body.CreateFixture(new b2FixtureDef {shape=Box(-5,0,40,54,false),density=1,friction=0,filter=new b2Filter {maskBits=0}});
            if(!floorFirst) ground.CreateFixture(floorFixture);
            var rows=new List<CollisionSample>();
            for(int i=0;i<test.Steps;i++)
            {
                body.SetLinearVelocity(new b2Vec2(test.VX,test.VY)); world.Step(fixture.Step,fixture.VelocityIterations,fixture.PositionIterations);
                var p=body.GetPosition(); var v=body.GetLinearVelocity();
                var row=new CollisionSample {Step=i,X=B(p.x),Y=B(p.y),VX=B(v.x),VY=B(v.y),Mass=B(body.m_mass),CenterX=B(body.m_sweep.localCenter.x),CenterY=B(body.m_sweep.localCenter.y)};
                for(var c=world.GetContactList();c!=null;c=c.GetNext()) if(c.IsTouching()) {
                    var m=new b2WorldManifold();c.GetWorldManifold(m);var manifold=c.GetManifold();
                    for(int j=0;j<manifold.pointCount;j++) row.Contacts.Add(new CollisionContact {X=B(m.points[j].x),Y=B(m.points[j].y),NX=B(m.normal.x),NY=B(m.normal.y),
                        Separation=B(m.separations[j]),NormalImpulse=B(manifold.points[j].normalImpulse),TangentImpulse=B(manifold.points[j].tangentImpulse)});
                }
                rows.Add(row);
            }
            bool pass=test.Native.Count==rows.Count && test.Native.Zip(rows).All(pair=>Same(pair.First,pair.Second));
            if(mode=="opposite-order-world-polygon") controlsSeparated&=!pass; else defaultPassed&=pass;
            Console.WriteLine(test.Name+" / "+mode+": "+(pass ? "PASS" : "differs"));
            for(int i=0;i<rows.Count;i++) Console.WriteLine("  "+i+": Y "+test.Native[i].Y+"/"+rows[i].Y+", VY "+test.Native[i].VY+"/"+rows[i].VY);
            reports.Add(new {test.Name,Mode=mode,Passed=pass,Rows=rows});
        }
        SearchFiles.Write(Path.Combine(directory,"collision-comparison.json"),new {Passed=defaultPassed && controlsSeparated,NativeCases=fixture.Cases.Count,OrderControlsSeparated=controlsSeparated,Reports=reports});
        return defaultPassed && controlsSeparated ? 0 : 1;
    }
    private static bool Same(CollisionSample a,CollisionSample b) => a.X==b.X && a.Y==b.Y && a.VX==b.VX && a.VY==b.VY && a.Mass==b.Mass && a.CenterX==b.CenterX && a.CenterY==b.CenterY;
    private static string B(float value)=>SimulationTraceComparer.Float(value);
}
