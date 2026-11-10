using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using UnityEngine;

namespace IGTAP.EngineSim
{
    public sealed partial class SimSnapshot
    {
        // Worker-owned native frame/scheduler state; never exported as a route.
        internal StudioSnapshot StudioFrame;
    }
    public sealed partial class OfflineSolver
    {
        public readonly ConcurrentQueue<Exception> StudioErrors = new ConcurrentQueue<Exception>();
        public int StudioArchiveCells => System.Threading.Volatile.Read(ref _cells);
        public bool StudioStopped => _stop;
    }
    public sealed partial class Simulation
    {
        private Func<StudioInput, bool> _studioSearchAdvance;
        private Func<bool> _studioSearchStop;
        private StudioSnapshot _studioSearchRoot;
        private int _studioSearchHorizon, _studioSearchFirst, _studioSearchFingerprint;
        private float _studioSearchX, _studioSearchY, _studioSearchRadius;
        private bool _studioSearchExhausted;
        private Func<int,float> _studioSearchRank;

        public void ConfigureStudioOfflineSearch(Func<StudioInput, bool> advance, Func<bool> stop, int horizon, float x, float y, float radius,Func<int,float> rank=null)
        {
            _studioSearchRank=rank;
            _studioSearchAdvance = advance; _studioSearchStop = stop; _studioSearchHorizon = horizon;
            _studioSearchX = x; _studioSearchY = y; _studioSearchRadius = radius;
            _studioSearchFirst = _studioFrame; _studioSearchRoot = CaptureStudioState();
            // Section searches score actual rendered frames, independently of
            // the course timer and the upstream input-hook tick convention.
            TargetCourse = 0;
            TargetFinish = new CaptureRect(new CapturePos(x, y), radius * 2, radius * 2);
        }
        public bool StudioSearchInputsExhausted => _studioSearchAdvance == null ? Engine.InputsExhausted : _studioSearchExhausted;
        public float StudioSearchElapsed(courseScript active) => _studioSearchAdvance == null ? Elapsed(active) : (float)(_studioFrame - _studioSearchFirst) / _studioFps;
        // Upstream convergence includes its own scheduler. Until that shortcut
        // includes our native frame scheduler too, disable it rather than merge
        // branches that only happen to share body position and velocity.
        public int StudioSearchFingerprint() => _studioSearchAdvance == null ? Fingerprint() : unchecked(++_studioSearchFingerprint);
        public SimSnapshot StudioSearchCapture(int tick)
        {
            if (_studioSearchAdvance == null) return Capture(tick);
            var state = CaptureStudioState(); state.World.Tick = tick; state.World.StudioFrame = state; return state.World;
        }
        public RunResult StudioSearchRun(IReadOnlyList<InputTick> inputs, SimSnapshot from = null, Func<int, bool> afterTick = null, int extraTicks = 400, Action<int> onStep = null)
        {
            if (_studioSearchAdvance == null) return Run(inputs, from, afterTick, extraTicks, onStep);
            var state = from?.StudioFrame ?? _studioSearchRoot;
            int first = from?.Tick ?? 0;
            if (first >= inputs.Count) { state = _studioSearchRoot; first = 0; }
            RestoreStudioState(state); _studioSearchExhausted = false;
            int stepped = 0, finish = -1;
            string end = "inputs out";
            if (afterTick?.Invoke(first) != false)
            for (int i = first; i < Math.Min(inputs.Count, _studioSearchHorizon); i++)
            {
                if (_studioSearchStop()) { end = "Search stopped"; break; }
                var input = StudioSearchInput(inputs[i], _studioInput);
                if (!_studioSearchAdvance(input)) { end = "Unsupported input or Player death"; break; }
                stepped++; onStep?.Invoke(i + 1);
                var body = Physics.PlayerBody(); var origin = WorldOriginInScene;
                double dx = body.px - origin.x - _studioSearchX, dy = body.py - origin.y - _studioSearchY;
                if (StudioHasCheckpoints?StudioCheckpointsComplete:dx * dx + dy * dy <= _studioSearchRadius * _studioSearchRadius) { finish = i; break; }
                _studioSearchExhausted = i + 1 >= inputs.Count;
                if (afterTick?.Invoke(i + 1) == false) break;
            }
            return new RunResult { Finished = finish >= 0, FinishTick = finish, Time = finish>=0&&_studioSearchRank!=null?_studioSearchRank(_studioFrame-_studioSearchFirst):StudioSearchElapsed(null),
                PhysicsTicks = _studioPus - _studioSearchRoot.Pus, Stepped = stepped, EndReason = finish >= 0 ? null : end };
        }
        public static StudioInput StudioSearchInput(InputTick input, StudioInput previous)
        {
            // In this adapter one InputTick is one movie frame. Macro lock,
            // freeze and spring offsets are rejected at the host boundary.
            if(input.reset || input.pause!=0 || input.pauseTick!=0 || input.pauseFrame!=0 || input.dashJump!=0 || input.springDash!=0 || input.lockX.HasValue || input.lockY.HasValue || input.turnX.HasValue || input.turnY.HasValue)
                throw new NotSupportedException("Macro-tick offsets need an explicit movie-frame translation.");
            int held = (input.held ? 1 : 0) | (input.dash ? 4 : 0);
            return new StudioInput { X = input.x, Y = input.y, Held = held, Press = held & ~previous.Held, Release = previous.Held & ~held };
        }
    }
}
