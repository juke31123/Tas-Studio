using System;
using UnityEngine;
using System.Linq;
using Box2D;

namespace IGTAP.EngineSim
{
    public sealed partial class PhysicsWorld
    {
        // Native rendered-frame contacts retain their collider-pair identity
        // across a script transform write before the next PU. A temporary
        // broad-phase separation must not turn a continuing stay into enter.
        public bool StudioFrames;
        // Optional diagnostics only; candidates never depend on this observer.
        public Action<Collider2D, Collider2D, int> StudioContact;
        public object StudioLastContact;
        private void StudioQueued(Pair pair)
        {
            if (StudioContact == null) return;
            StudioLastContact=new {Touching=_touching.Contains(pair), Lapsed=_lapsed.Contains(pair),
                AShifted=pair.A.Body?.Shifted, BShifted=pair.B.Body?.Shifted,
                AReshaped=pair.A.Reshaped, BReshaped=pair.B.Reshaped,
                Proxies=ProxiesOverlap(pair.A,pair.B)};
        }
        public object[] StudioGateContacts()
        {
            var result = new System.Collections.Generic.List<object>();
            for (var c = _b2.GetContactList(); c != null; c = c.GetNext())
            {
                var a=(ColliderState)c.GetFixtureA().UserData; var b=(ColliderState)c.GetFixtureB().UserData;
                if (a.Body != _player && b.Body != _player) continue;
                if (a.Collider.GetComponent<startGate>() == null && b.Collider.GetComponent<startGate>() == null) continue;
                result.Add(new {A=a.Collider.GetInstanceID(), B=b.Collider.GetInstanceID(), Enabled=c.IsEnabled(),
                    Touching=c.IsTouching(), Overlap=Overlap(c,c.GetFixtureA(),c.GetFixtureB())});
            }
            return result.ToArray();
        }
        public object[] StudioPlayerContacts()
        {
            var result=new System.Collections.Generic.List<object>(); var manifold=new b2WorldManifold();
            for(var c=_b2.GetContactList(); c!=null; c=c.GetNext())
            {
                var a=(ColliderState)c.GetFixtureA().UserData; var b=(ColliderState)c.GetFixtureB().UserData;
                if(!c.IsTouching() || c.GetFixtureA().IsSensor() || c.GetFixtureB().IsSensor() || a.Body!=_player && b.Body!=_player) continue;
                bool selfA=a.Body==_player; var self=selfA ? a : b; var other=selfA ? b : a;
                c.GetWorldManifold(manifold); var local=c.GetManifold();
                for(int i=0;i<local.pointCount;i++) result.Add(new {Self=self.Collider.GetInstanceID(),Other=other.Collider.GetInstanceID(),OtherName=other.Collider.name,
                    X=manifold.points[i].x,Y=manifold.points[i].y,NX=(selfA ? -1 : 1)*manifold.normal.x,NY=(selfA ? -1 : 1)*manifold.normal.y,
                    Separation=manifold.separations[i],NormalImpulse=local.points[i].normalImpulse,TangentImpulse=local.points[i].tangentImpulse});
            }
            return result.ToArray();
        }
        public object[] StudioMoverPairs()
        {
            var result=new System.Collections.Generic.List<object>();
            for(var c=_b2.GetContactList();c!=null;c=c.GetNext()) {
                var a=(ColliderState)c.GetFixtureA().UserData;var b=(ColliderState)c.GetFixtureB().UserData;
                if(a.Body!=_player && b.Body!=_player)continue;
                var actor=a.Body==_player ? b : a; var player=a.Body==_player ? a : b;
                if(actor.Body==null || actor.Collider.GetComponent<PlatformMover>()==null)continue;
                result.Add(new {Player=player.Collider.GetInstanceID(),Actor=actor.Collider.GetInstanceID(),Touching=c.IsTouching(),Awake=actor.Body.Body.IsAwake()});
            }
            return result.ToArray();
        }
        public object StudioCollider(Collider2D collider)
        {
            var s = _colliders[collider]; var p = (s.Body?.Body ?? _ground).GetPosition();
            var t=collider.transform.position; var scale=collider.transform.lossyScale;
            var size=(collider as BoxCollider2D)?.size ?? Vector2.zero; var offset=collider.offset;
            return new { Id=collider.GetInstanceID(), collider.name, collider.enabled, Active=collider.gameObject.activeInHierarchy,
                X=p.x, Y=p.y, Transform=new {t.x,t.y,t.z}, Scale=new {scale.x,scale.y,scale.z},
                Size=new {size.x,size.y}, Offset=new {offset.x,offset.y},
                Shapes=s.Fixtures.Select(f => f.GetShape() is b2PolygonShape poly ? poly.m_vertices.Take(poly.m_count).Select(v => new {v.x,v.y}).ToArray() : null).ToArray() };
        }
    }
}
