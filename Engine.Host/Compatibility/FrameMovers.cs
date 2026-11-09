using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace IGTAP.EngineSim
{
    internal struct MoverFrameState
    {
        public Rigidbody2D Body;
        public Vector3 From, To, Generated, Render, Local;
        public Quaternion GeneratedRotation;
        public bool Interpolate, Pending;
    }
    public sealed partial class Simulation
    {
        private MoverFrameState[] _studioMovers = Array.Empty<MoverFrameState>();
        private readonly Dictionary<Rigidbody2D, int> _studioMoverIndex = new Dictionary<Rigidbody2D, int>();
        private void ConfigureStudioMovers(bool interpolate)
        {
            var field = typeof(PlatformMover).GetField("body", BindingFlags.Instance | BindingFlags.NonPublic);
            _studioMovers = World.All<PlatformMover>().Select(m => (Rigidbody2D)field.GetValue(m)).Where(b => b != null).Distinct()
                .Select(body => {
                    var at = body.position; var t = body.transform;
                    return new MoverFrameState { Body=body, From=new Vector3(at.x,at.y,t.m_Position.z), To=new Vector3(at.x,at.y,t.m_Position.z),
                        Generated=t.m_Position, GeneratedRotation=t.m_Rotation, Interpolate=interpolate };
                }).ToArray();
            _studioMoverIndex.Clear();
            for (int i=0; i<_studioMovers.Length; i++) _studioMoverIndex.Add(_studioMovers[i].Body,i);
            Physics.StudioBeforeBodySync = body => {
                if (!_studioMoverIndex.TryGetValue(body,out int i) || !Physics.StudioBodyTransformDirty(body)) return;
                ref var state = ref _studioMovers[i];
                if (!Physics.StudioDiscardUnchangedTransform(body,state.Generated,state.GeneratedRotation))
                { state.Interpolate=false; state.Pending=true; }
            };
            Physics.StudioBodyPoseWritten = body => {
                if (!_studioMoverIndex.TryGetValue(body,out int i)) return;
                _studioMovers[i].Generated=body.transform.m_Position;
                _studioMovers[i].GeneratedRotation=body.transform.m_Rotation;
            };
        }
        private void BeforeStudioMoverPhysics()
        {
            for(int i=0; i<_studioMovers.Length; i++) {
                var at=_studioMovers[i].Body.position;
                _studioMovers[i].From=new Vector3(at.x,at.y,_studioMovers[i].Body.transform.m_Position.z);
                _studioMovers[i].Pending=false;
            }
        }
        private void AfterStudioMoverPhysics()
        {
            for(int i=0; i<_studioMovers.Length; i++) {
                var at=_studioMovers[i].Body.position;
                _studioMovers[i].To=new Vector3(at.x,at.y,_studioMovers[i].Body.transform.m_Position.z);
                _studioMovers[i].Interpolate=true;
            }
        }
        private bool StudioMoverWritesPending() => _studioMovers.Any(s => Physics.StudioBodyTransformDirty(s.Body));
        public void ConfigureStudioMoverSleep(System.Text.Json.JsonElement start)
        {
            if(!start.TryGetProperty("moverSleep",out var sleep)) return;
            var seen=new System.Collections.Generic.HashSet<Rigidbody2D>();
            foreach(var row in sleep.EnumerateArray()) {
                var body=World.Objects[row.GetProperty("id").GetInt32()] as Rigidbody2D ?? throw new InvalidOperationException("Missing mover sleep owner.");
                if(!_studioMoverIndex.ContainsKey(body) || !seen.Add(body)) throw new InvalidOperationException("Invalid or duplicate mover sleep owner.");
                Physics.StudioSetBodySleep(body,row.GetProperty("sleeping").GetBoolean(),row.GetProperty("restSeconds").GetSingle());
            }
            if(seen.Count!=_studioMovers.Length) throw new InvalidOperationException("Native mover sleep history is incomplete.");
        }
        private void RenderStudioMovers(float alpha)
        {
            for(int i=0; i<_studioMovers.Length; i++) {
                ref var state = ref _studioMovers[i];
                if(!state.Body.gameObject.activeInHierarchy) continue;
                if(state.Pending) {
                    var p=state.Body.position; state.From=state.To=new Vector3(p.x,p.y,state.Body.transform.m_Position.z);
                    state.Pending=false; state.Interpolate=false;
                }
                if(!state.Interpolate || !Physics.StudioBodyAwake(state.Body)) {
                    state.Generated=state.Body.transform.m_Position; state.GeneratedRotation=state.Body.transform.m_Rotation; continue;
                }
                var at=state.To;
                if(state.Interpolate && state.Body.interpolation==RigidbodyInterpolation2D.Interpolate)
                    at=new Vector3(Blend(state.From.x,state.To.x,alpha),Blend(state.From.y,state.To.y,alpha),Blend(state.From.z,state.To.z,alpha));
                else if(state.Body.interpolation==RigidbodyInterpolation2D.Extrapolate)
                    throw new NotSupportedException("Moving-platform extrapolation needs a native fixture.");
                state.Body.transform.StoreWorldPosition(at);
                state.Generated=state.Body.transform.m_Position; state.GeneratedRotation=state.Body.transform.m_Rotation;
            }
        }
        private void ShiftStudioMovers(Vector3 by)
        { for(int i=0; i<_studioMovers.Length; i++) { _studioMovers[i].From-=by; _studioMovers[i].To-=by; } }
        private MoverFrameState[] CaptureStudioMovers()
        {
            var copy=(MoverFrameState[])_studioMovers.Clone();
            for(int i=0; i<copy.Length; i++) { copy[i].Render=copy[i].Body.transform.m_Position; copy[i].Local=copy[i].Body.transform.m_LocalPosition; }
            return copy;
        }
        private void RestoreStudioMovers(MoverFrameState[] states)
        {
            _studioMovers=(MoverFrameState[])states.Clone();
            foreach(var state in _studioMovers) { state.Body.transform.m_Position=state.Render; state.Body.transform.m_LocalPosition=state.Local; }
        }
    }
    public sealed partial class PhysicsWorld
    {
        public Action<Rigidbody2D> StudioBeforeBodySync, StudioBodyPoseWritten;
        public bool StudioBodyTransformDirty(Rigidbody2D body) => State(body).TransformDirty;
        public bool StudioBodyAwake(Rigidbody2D body) => State(body).Body.IsAwake();
        public void StudioSetBodySleep(Rigidbody2D body,bool sleeping,float restSeconds)
        {
            if(!float.IsFinite(restSeconds) || restSeconds<0) throw new InvalidOperationException("Invalid native body rest history.");
            var native=State(body).Body; native.SetAwake(!sleeping); native.m_sleepTime=restSeconds;
        }
        public bool StudioDiscardUnchangedTransform(Rigidbody2D body,Vector3 generated,Quaternion rotation)
        {
            var state=State(body); var t=body.transform;
            if(!SameBits(t.m_Position,generated) || !SameBits(t.m_Rotation,rotation)) return false;
            state.TransformDirty=false; return true;
        }
    }
}
