using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Igtap.Tas.Runtime
{
    internal static class SearchExport
    {
        public static void CaptureEngineSummary(TasSession session,SearchJob job)
        {
            var player=session.Adapter.Player;
            var motion=PlayerTelemetry.Capture(player,session.MovementTimers,out var parameters);
            var body=player.GetComponent<Rigidbody2D>(); var world=PlayerWorld(player); motion.Position=world;
            var shapes=player.GetComponents<Collider2D>().Where(c => c.enabled && !c.isTrigger).ToArray();
            if(shapes.Length==0 || motion.Dead) throw new InvalidOperationException("The native search needs an alive player with a collider.");
            var bounds=shapes[0].bounds; foreach(var collider in shapes.Skip(1)) bounds.Encapsulate(collider.bounds);
            job.Start=new SearchState {Player=motion, Previous=session.CurrentInput, Phase=session.PuPhase, Scale=session.PreviewTimeScale};
            job.HalfWidth=bounds.extents.x; job.HalfHeight=bounds.extents.y; job.OffsetX=bounds.center.x-body.position.x; job.OffsetY=bounds.center.y-body.position.y;
            job.Parameters=parameters; job.MaxDashes=player.maxAirDashes; job.MaxAirJumps=player.maxAirJumps;
            job.WallJumpUnlocked=player.wallJumpUnlocked; job.OmniDashUnlocked=player.omniDashUnlocked;
            job.Coverage=new[] {"Experimental game-code host. The native prefix is verified before search.",
                "Unexported geometry activation and unvalidated mechanic contacts stop branches. Native candidate verification is mandatory."};
        }
        public static MotionVector PlayerWorld(Movement player)
        {
            var position = player.GetComponent<Rigidbody2D>().position;
            var origin = UnityEngine.Object.FindFirstObjectByType<FloatingOrigin>();
            if (origin != null) position -= (Vector2)origin.currentOrigin;
            return new MotionVector(position.x, position.y);
        }
        public static void Capture(TasSession session, SearchJob job)
        {
            var player = session.Adapter.Player;
            var motion = PlayerTelemetry.Capture(player, session.MovementTimers, out var parameters);
            var body = player.GetComponent<Rigidbody2D>();
            if (player.pauseMenu?.menuOpen == true || player.GamePaused || motion.Dead || motion.Dashing || motion.Cutscene != 0)
                throw new NotSupportedException("The initial external simulator needs an alive, unpaused player outside a cutscene or dash.");
            var origin = UnityEngine.Object.FindFirstObjectByType<FloatingOrigin>();
            Vector2 shift = origin == null ? Vector2.zero : (Vector2)origin.currentOrigin;
            var colliders = player.GetComponents<Collider2D>().Where(c => c.enabled && !c.isTrigger).ToArray();
            if (colliders.Length == 0) throw new InvalidOperationException("The active player collider is missing.");
            var bounds = colliders[0].bounds;
            foreach (var collider in colliders.Skip(1)) bounds.Encapsulate(collider.bounds);
            job.HalfWidth = bounds.extents.x; job.HalfHeight = bounds.extents.y;
            job.OffsetX = bounds.center.x - body.position.x; job.OffsetY = bounds.center.y - body.position.y;
            motion.Position = new MotionVector(body.position.x - shift.x, body.position.y - shift.y);
            job.Start = new SearchState { Player = motion, Previous = session.CurrentInput, Phase = session.PuPhase,
                Pus = session.PuCount, Time = session.LogicalTime, Scale = session.PreviewTimeScale };
            if (job.Start.Scale != 1) throw new NotSupportedException("The initial external simulator requires normal game timeScale at the start frame.");
            job.Parameters = parameters; job.MaxDashes = player.maxAirDashes; job.MaxAirJumps = player.maxAirJumps;
            job.WallJumpUnlocked = player.wallJumpUnlocked; job.OmniDashUnlocked = player.omniDashUnlocked;
            var shapes = new List<SearchBody>();
            foreach (var collider in UnityEngine.Object.FindObjectsByType<Collider2D>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (collider == null || collider.attachedRigidbody == body || collider.transform.IsChildOf(player.transform) ||
                    collider.gameObject.scene != SceneManager.GetActiveScene() || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                // Export all query/collision candidates conservatively. Unknown
                // triggers/movers are barriers, not pass-through geometry.
                bool interacts = !Physics2D.GetIgnoreLayerCollision(player.gameObject.layer, collider.gameObject.layer);
                int playerLayer = 1 << player.gameObject.layer, worldLayer = 1 << collider.gameObject.layer;
                bool worldAccepts = (interacts || (collider.includeLayers.value & playerLayer) != 0) && (collider.excludeLayers.value & playerLayer) == 0;
                bool playerAccepts = colliders.Any(c => (interacts || (c.includeLayers.value & worldLayer) != 0) && (c.excludeLayers.value & worldLayer) == 0);
                if (!worldAccepts || !playerAccepts) continue;
                if (collider.compositeOperation != Collider2D.CompositeOperation.None && collider.composite != null) continue;
                if (collider.isTrigger && NonMovementTrigger(collider)) continue;
                var box = collider as BoxCollider2D; var b = collider.bounds;
                if (b.size.x <= 0 || b.size.y <= 0) continue;
                bool staticBox = box != null && Math.Abs(Mathf.DeltaAngle(collider.transform.eulerAngles.z, 0)) < .001f &&
                    (collider.attachedRigidbody == null || collider.attachedRigidbody.bodyType == RigidbodyType2D.Static) && !collider.usedByEffector;
                string kind = !collider.isTrigger && staticBox ? "solid" : "unsupported";
                if (collider.GetComponentInParent<spikeScript>() != null || collider.gameObject.CompareTag("Spikes")) kind = "hazard";
                bool staticShape = (collider.attachedRigidbody == null || collider.attachedRigidbody.bodyType == RigidbodyType2D.Static) && !collider.usedByEffector;
                if (staticShape && (collider is PolygonCollider2D || collider is CompositeCollider2D || collider is BoxCollider2D))
                {
                    var group = new PhysicsShapeGroup2D(); int count = collider.GetShapes(group); bool represented = count > 0;
                    var exported = new List<SearchBody>();
                    for (int i = 0; i < count; i++)
                    {
                        var shape = group.GetShape(i); var points = new List<Vector2>(); group.GetShapeVertices(i, points);
                        var worldPoints = points.Select(v => { var world = group.localToWorldMatrix.MultiplyPoint3x4(v); return new MotionVector(world.x - shift.x, world.y - shift.y); }).ToArray();
                        var polygons = new List<MotionVector[]>();
                        if (shape.shapeType == PhysicsShapeType2D.Polygon && points.Count >= 3) polygons.Add(worldPoints);
                        else if (shape.shapeType == PhysicsShapeType2D.Edges && points.Count >= 2)
                        {
                            for (int edge = 0; edge < worldPoints.Length - 1; edge++)
                            {
                                var a = worldPoints[edge]; var z = worldPoints[edge + 1]; var difference = z + a * -1;
                                if (difference.Magnitude < .00001f) continue;
                                float magnitude = Math.Max(Math.Max(Math.Abs(a.X), Math.Abs(a.Y)), Math.Max(Math.Abs(z.X), Math.Abs(z.Y)));
                                float thickness = Math.Max(.002f, magnitude * .0000005f);
                                var normal = new MotionVector(-difference.Y, difference.X) * (thickness / difference.Magnitude);
                                polygons.Add(new[] { a + normal, z + normal, z + normal * -1, a + normal * -1 });
                            }
                        }
                        else { represented = false; break; }
                        foreach (var vertices in polygons)
                            exported.Add(new SearchBody { Id = collider.name + ":" + i + ":" + exported.Count, Kind = collider.isTrigger ? kind : "solid", Vertices = vertices, Radius = shape.radius,
                                MinX = vertices.Min(v => v.X), MinY = vertices.Min(v => v.Y), MaxX = vertices.Max(v => v.X), MaxY = vertices.Max(v => v.Y) });
                    }
                    if (represented) { shapes.AddRange(exported); continue; }
                }
                // Goal/gate and decorative triggers cannot be treated as proven
                // inert. Native verification and future providers decide effects.
                shapes.Add(new SearchBody { Id = collider.name + "#" + collider.GetInstanceID(), Kind = kind,
                    MinX = b.min.x - shift.x, MinY = b.min.y - shift.y, MaxX = b.max.x - shift.x, MaxY = b.max.y - shift.y });
            }
            job.Geometry = shapes.ToArray();
            job.Coverage = new[] { "Experimental static platform model; not Unity-equivalent.",
                "Moving platforms, arbitrary polygon/composite shapes and unknown triggers stop affected rollouts.",
                "Known course/checkpoint/camera/clone-visibility triggers are omitted from movement exploration; their economy/camera/RNG effects are not simulated.",
                "All candidates require native goal verification; no glitch simulation yet." };
            job.Validate();
        }
        private static bool NonMovementTrigger(Collider2D collider)
        {
            // Movement-only exploration may pass these triggers. This is an
            // explicit coverage limit, not a statement that they have no world
            // effects. Native replay still executes their original callbacks.
            var known = new[] { "startGate", "endGate", "checkpointScript", "camZoneScript", "camSizeTrigger", "clonesLoD",
                "GenericSpriteDisabler", "LoadingPopupScript", "MusicController" };
            var scripts = collider.GetComponents<MonoBehaviour>().AsEnumerable();
            if (collider.attachedRigidbody != null) scripts = scripts.Concat(collider.attachedRigidbody.GetComponents<MonoBehaviour>());
            var handlers = scripts.Where(s => s != null && s.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(m => m.Name == "OnTriggerEnter2D" || m.Name == "OnTriggerStay2D" || m.Name == "OnTriggerExit2D")).ToArray();
            return handlers.Length > 0 && handlers.All(s => known.Contains(s.GetType().Name));
        }
    }
}
