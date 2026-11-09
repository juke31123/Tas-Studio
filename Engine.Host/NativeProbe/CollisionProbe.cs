using System;
using System.IO;
using System.Linq;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static class CollisionProbe
{
    private static float F(int bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
    public static void Capture(string directory, int playerLayer)
    {
        var fixture = new CollisionFixture { Step = Time.fixedDeltaTime, ContactOffset = Physics2D.defaultContactOffset,
            VelocityIterations = Physics2D.velocityIterations, PositionIterations = Physics2D.positionIterations,
            MaxTranslation = Physics2D.maxTranslationSpeed, MaxRotation = Physics2D.maxRotationSpeed,
            MaxLinearCorrection = Physics2D.maxLinearCorrection, MaxAngularCorrection = Physics2D.maxAngularCorrection,
            Baumgarte = Physics2D.baumgarteScale, ToiBaumgarte = Physics2D.baumgarteTOIScale, BounceThreshold = Physics2D.bounceThreshold };
        fixture.Cases.Add(new CollisionCase { Name = "Test30-landing", FloorX = -1476.37891f, FloorY = -53.1865234f, FloorWidth = 274, FloorHeight = 20,
            X = -1340.03735f, Y = -8.806521f, VX = -460, VY = F(unchecked((int)0xc1d7fffa)) });
        fixture.Cases.Add(new CollisionCase { Name = "translated-landing", FloorX = -136.341553f, FloorY = -44.3800049f, FloorWidth = 274, FloorHeight = 20,
            X = 0, Y = 0, VX = -460, VY = F(unchecked((int)0xc1d7fffa)) });
        fixture.Cases.Add(new CollisionCase { Name = "power-two-floor", FloorX = -127.341553f, FloorY = -44.3800049f, FloorWidth = 256, FloorHeight = 20,
            X = 0, Y = 0, VX = -460, VY = F(unchecked((int)0xc1d7fffa)) });
        foreach (var test in fixture.Cases.ToArray()) fixture.Cases.Add(new CollisionCase { Name=test.Name+"-body-first", FloorFirst=false,
            FloorX=test.FloorX,FloorY=test.FloorY,FloorWidth=test.FloorWidth,FloorHeight=test.FloorHeight,X=test.X,Y=test.Y,VX=test.VX,VY=test.VY });
        string randomBefore = JsonUtility.ToJson(UnityEngine.Random.state);
        foreach (var test in fixture.Cases) Run(test, fixture.Step, playerLayer);
        WakeTest(directory, fixture.Step, playerLayer);
        if (randomBefore != JsonUtility.ToJson(UnityEngine.Random.state)) throw new InvalidOperationException("Collision micro-test changed game RNG.");
        SearchFiles.Write(Path.Combine(directory, "collision-fixture.json"), fixture);
    }
    private static void Run(CollisionCase test, float step, int playerLayer)
    {
        var scene = SceneManager.CreateScene("Tas Studio collision micro-test " + test.Name, new CreateSceneParameters(LocalPhysicsMode.Physics2D));
        GameObject floor = null, player = null;
        var material = new PhysicsMaterial2D("Collision micro-test") { friction = 0, bounciness = 0 };
        try
        {
            floor = new GameObject("Floor"); SceneManager.MoveGameObjectToScene(floor, scene); floor.layer = 7;
            floor.transform.position = new Vector3(test.FloorX, test.FloorY, 0);
            void Surface() { var surface = floor.AddComponent<BoxCollider2D>(); surface.size = new Vector2(test.FloorWidth, test.FloorHeight); surface.sharedMaterial = material; }
            if(test.FloorFirst) Surface();
            player = new GameObject("Body"); SceneManager.MoveGameObjectToScene(player, scene); player.layer = playerLayer;
            player.transform.position = new Vector3(test.X, test.Y, 0); player.transform.localScale = new Vector3(-1, 1, 1);
            var body = player.AddComponent<Rigidbody2D>(); body.gravityScale = 0; body.constraints = RigidbodyConstraints2D.FreezeRotation;
            body.collisionDetectionMode = CollisionDetectionMode2D.Continuous; body.sleepMode = RigidbodySleepMode2D.NeverSleep; body.useAutoMass = true;
            var primary = player.AddComponent<BoxCollider2D>(); primary.size = new Vector2(41, 72); primary.offset = new Vector2(5, 2); primary.sharedMaterial = material;
            primary.includeLayers = 1 << floor.layer;
            var secondary = player.AddComponent<BoxCollider2D>(); secondary.size = new Vector2(40, 54); secondary.offset = new Vector2(5, 0); secondary.sharedMaterial = material;
            secondary.excludeLayers = 1 << floor.layer;
            if(!test.FloorFirst) Surface();
            var contacts = new ContactPoint2D[16];
            for (int i = 0; i < test.Steps; i++)
            {
                body.linearVelocity = new Vector2(test.VX, test.VY);
                if (!scene.GetPhysicsScene2D().Simulate(step)) throw new InvalidOperationException("Isolated physics step was rejected.");
                var sample = new CollisionSample { Step = i, X = B(body.position.x), Y = B(body.position.y), VX = B(body.linearVelocity.x), VY = B(body.linearVelocity.y),
                    Mass = B(body.mass), CenterX = B(body.centerOfMass.x), CenterY = B(body.centerOfMass.y) };
                int count = body.GetContacts(contacts);
                for (int c = 0; c < count; c++) { var p = contacts[c]; sample.Contacts.Add(new CollisionContact {
                    X = B(p.point.x), Y = B(p.point.y), NX = B(p.normal.x), NY = B(p.normal.y), Separation = B(p.separation), NormalImpulse = B(p.normalImpulse), TangentImpulse = B(p.tangentImpulse) }); }
                test.Native.Add(sample);
            }
        }
        finally
        {
            if (player != null) UnityEngine.Object.DestroyImmediate(player);
            if (floor != null) UnityEngine.Object.DestroyImmediate(floor);
            UnityEngine.Object.DestroyImmediate(material); SceneManager.UnloadSceneAsync(scene);
        }
    }
    private static string B(float value) => SimulationTraceComparer.Float(value);
    private static void WakeTest(string directory,float step,int playerLayer)
    {
        var scene=SceneManager.CreateScene("Tas Studio wake micro-test",new CreateSceneParameters(LocalPhysicsMode.Physics2D));
        GameObject platform=null,player=null;
        try {
            platform=new GameObject("Sleeping platform"); SceneManager.MoveGameObjectToScene(platform,scene); platform.layer=7;
            var actor=platform.AddComponent<Rigidbody2D>();actor.bodyType=RigidbodyType2D.Kinematic;actor.gravityScale=0;
            var a=platform.AddComponent<BoxCollider2D>();a.size=Vector2.one;a.excludeLayers=510;
            actor.Sleep(); bool before=actor.IsSleeping();
            player=new GameObject("Approaching body"); SceneManager.MoveGameObjectToScene(player,scene);player.layer=playerLayer;player.transform.position=new Vector3(1.05f,0,0);
            var body=player.AddComponent<Rigidbody2D>();body.gravityScale=0;body.constraints=RigidbodyConstraints2D.FreezeRotation;
            var b=player.AddComponent<BoxCollider2D>();b.size=Vector2.one;b.includeLayers=128;
            scene.GetPhysicsScene2D().Simulate(step);
            var points=new ContactPoint2D[16];var colliders=new Collider2D[16];
            int pointCount=actor.GetContacts(points),colliderCount=actor.GetContacts(colliders);
            SearchFiles.Write(Path.Combine(directory,"native-wake-micro.json"),new {BeforeSleeping=before,AfterSleeping=actor.IsSleeping(),PointCount=pointCount,ColliderCount=colliderCount,Touching=a.IsTouching(b)});
        }
        finally {if(player!=null)UnityEngine.Object.DestroyImmediate(player);if(platform!=null)UnityEngine.Object.DestroyImmediate(platform);SceneManager.UnloadSceneAsync(scene);}
    }
}
