using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using UnityEngine;

namespace IGTAP.EngineSim
{
    public sealed partial class Simulation
    {
        internal bool _studioSearchGuards;
        private bool _studioSpringWitnesses;
        private bool _studioUpgradeWitnesses;
        private static readonly FieldInfo StudioMoverType=typeof(PlatformMover).GetField("PlatformType",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)
            ?? throw new MissingFieldException("PlatformMover.PlatformType");
        private static readonly object StudioZipType=Enum.Parse(StudioMoverType.FieldType,"zipMover");
        private static readonly FieldInfo StudioUpgradeTree=typeof(upgradeBox).GetField("upgradeTree",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)
            ?? throw new MissingFieldException("upgradeBox.upgradeTree");
        public void ConfigureStudioSearchGuards(JsonElement definition, bool springWitnesses = false,bool upgradeWitnesses=false)
        {
            var missing=new HashSet<int>();
            foreach(var node in definition.GetProperty("objects").EnumerateArray())
            foreach(var component in node.GetProperty("components").EnumerateArray())
            {
                if(!component.TryGetProperty("shapes",out var shapes)) continue;
                bool source=component.GetProperty("props").TryGetProperty("compositeOperation",out var operation) && operation.GetInt32()!=0;
                if(!source && !shapes.GetProperty("list").EnumerateArray().Any() && !component.TryGetProperty("geometrySource",out _)) missing.Add(component.GetProperty("id").GetInt32());
            }
            Physics.StudioMissingGeometry=missing;
            Physics.StudioExternalMechanics=new Dictionary<int,string>();
            if(definition.TryGetProperty("externalMechanics",out var external))
                foreach(var row in external.EnumerateArray())Physics.StudioExternalMechanics[row.GetProperty("collider").GetInt32()]=string.Join(", ",row.GetProperty("types").EnumerateArray().Select(t=>t.GetString()));
            foreach(var data in World.Colliders)
                if(data.Collider.enabled && data.Collider.gameObject.activeInHierarchy && missing.Contains(data.Collider.GetInstanceID()))
                    throw new NotSupportedException("Active geometry was not exported: " + data.Collider.name);
            _studioSearchGuards=true; _studioSpringWitnesses=springWitnesses;_studioUpgradeWitnesses=upgradeWitnesses;
            Physics.StudioCheckExistingMechanics();
            Hooks.HitSpring=(movement,up) => {
                if (!springWitnesses) throw new NotSupportedException("Spring contact requires native spring-state witnesses.");
                Events |= StepEvents.SpringHit; SpringUp = up;
            };
        }
        internal void StudioCheckMessage(MonoBehaviour target,object argument)
        {
            if(!_studioSearchGuards) return;
            var other=argument is Collider2D c ? c : argument is Collision2D collision ? collision.collider : null;
            if(other==null || other.gameObject!=Player.gameObject && other.attachedRigidbody?.gameObject!=Player.gameObject) return;
            StudioCheckMechanic(target);
        }
        internal void StudioCheckMechanic(MonoBehaviour target)
        {
            if(target is SpringScript && !_studioSpringWitnesses)
                throw new NotSupportedException("Spring contact requires native spring-state witnesses.");
            if(target is PlatformMover mover && Equals(StudioMoverType.GetValue(mover),StudioZipType)) return;
            // Passive contact only updates the native cooldown/debris. Check
            // unsupported effects at the actual affordable purchase boundary.
            if(target is upgradeBox) return;
            switch(target.GetType().Name)
            {
                case "PlatformMover": case "TeleporterScript":
                    throw new NotSupportedException("Contact requires a native mechanic fixture: " + target.GetType().Name + " (" + target.name + ").");
            }
        }
        internal void StudioCheckUpgradePurchase(upgradeBox box)
        {
            if(!_studioSearchGuards) return;
            if(box.upgrade==localUpgrades.localUpgradeSet.cashPerLoop &&
                (!box.buyMax||box.neverBuyMax) && !VmanScript.isCurrentlyVman)
            {
                if(!_studioUpgradeWitnesses)throw new NotSupportedException("CashPerLoop contact requires native purchase-state witnesses. Capture a new search from this build.");
                return;
            }
            if(box.upgrade==localUpgrades.localUpgradeSet.Movement && box.movementUpgrade==upgradeBox.movementUpgrades.wallJump &&
                (!box.buyMax||box.neverBuyMax) && box.Cap==1 && StudioUpgradeTree.GetValue(box)==null)
            {
                if(!_studioUpgradeWitnesses)throw new NotSupportedException("Wall jump purchase requires native purchase-state witnesses. Refresh the snapshot.");
                return;
            }
            throw new NotSupportedException("Purchase requires a native mechanic fixture: upgradeBox ("+box.name+").");
        }
    }
    public sealed partial class PhysicsWorld
    {
        internal HashSet<int> StudioMissingGeometry;
        internal Dictionary<int,string> StudioExternalMechanics;
        private void StudioCheckExternalContacts()
        {
            if(StudioExternalMechanics==null||!_sim._studioSearchGuards)return;
            foreach(var pair in _pairsNow.Concat(_pairsBefore))
            {
                var actor=pair.A.Body==_player?pair.B:pair.B.Body==_player?pair.A:null;
                if(actor!=null&&StudioExternalMechanics.TryGetValue(actor.Id,out var types))throw new NotSupportedException("Custom-map interaction needs a simulator adapter: "+types+" ("+actor.Collider.name+").");
            }
        }
        internal void StudioCheckExistingMechanics()
        {
            StudioCheckExternalContacts();
            // Guards are enabled after the verified prefix. A platform already
            // carrying the player need not emit another enter callback.
            foreach(var pair in _touching.Concat(_pairsBefore).Distinct()) {
                var actor=pair.A.Body==_player ? pair.B : pair.B.Body==_player ? pair.A : null;
                if(actor==null)continue;
                foreach(var script in CallbackScripts(actor.Collider)) _sim.StudioCheckMechanic(script);
            }
        }
        private void StudioCheckGeometry(ColliderState state)
        {
            if(StudioMissingGeometry!=null && Live(state) && StudioMissingGeometry.Contains(state.Collider.GetInstanceID()))
                throw new NotSupportedException("Unexported collider was activated: " + state.Collider.name);
        }
    }
}
