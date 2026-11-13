using System;
using Igtap.Tas.Core;
using UnityEngine;

namespace Igtap.Tas.Runtime
{
    // One sample at the end of a rendered frame; desktop rendering only reads
    // it. This is reusable by a future hitbox/trajectory renderer without rerunning
    // game code or adding any preview side effects to a recording.
    internal sealed class PlayerStatsMonitor
    {
        private readonly IMotionPredictor _predictor;
        public PlayerMotion Current { get; private set; }
        public MotionPrediction Prediction { get; private set; }
        public PlayerActionAvailability Actions { get; private set; }
        public FrameInput Input { get; private set; }
        public string InputSource { get; private set; }
        public string Message { get; private set; } = "Waiting for a player sample.";
        public int MovieFrame { get; private set; }
        public bool InTas { get; private set; }
        public PlayerStatsMonitor(IMotionPredictor predictor = null) { _predictor = predictor ?? new MovementPredictor(); }

        public void Refresh(TasSession session, bool force = false)
        {
            Current = null; Prediction = null; Actions = null; Input = default; InputSource = "Unavailable";
            if (!force) return;
            if (session.IsMainMenu) { Message = "Enter a gameplay scene to inspect the player."; return; }
            if (session.WaitingForLoad || session.Mode == SessionMode.Warmup)
            { Message = "Waiting for scene loading and TAS startup to finish."; return; }
            try
            {
                // Active sessions already have a canonical bound player. Outside
                // a session Bind handles replacement players and scene changes.
                if ((!session.Active && !session.Adapter.Bind()) || !session.Adapter.IsAlive)
                { Message = "No active player is available."; return; }
                Current = PlayerTelemetry.Capture(session.Adapter.Player, session.Active ? session.MovementTimers : null, out var parameters);
                var actionContext=PlayerTelemetry.ActionContext(session.Adapter.Player);
                Actions = PlayerActionAvailability.Evaluate(Current, parameters, actionContext);
                MovieFrame = session.Cursor; InTas = session.Active;
                FrameInput previous = session.Active ? session.CurrentInput : session.Adapter.ReadPhysicalInput();
                Input = session.PreviewInput(out string source); InputSource = source;

                float scale = session.PreviewTimeScale;
                float frameSeconds = session.Active ? session.StepSeconds * scale : Time.unscaledDeltaTime * scale;
                float fixedSeconds = Time.fixedDeltaTime;
                // A frame can contain zero, one or several physics ticks. Use the
                // current accumulator phase rather than silently assuming one.
                double phase = session.Active ? session.PuPhase : Time.timeAsDouble - Time.fixedTimeAsDouble;
                float remainder = (float)Math.Max(0, Math.Min(phase, fixedSeconds * .999999));
                if (session.Mode == SessionMode.Complete || session.Mode == SessionMode.Desync)
                    Prediction = new MotionPrediction(null, "The run is stopped at completion or desync.", 0, 0);
                else if (scale <= 0)
                    Prediction = new MotionPrediction(null, "Game pause/hitstop is active; the next advancing frame is unknown.", 0, 0);
                else if (session.Adapter.Player.pauseMenu != null && session.Adapter.Player.pauseMenu.menuOpen)
                    Prediction = new MotionPrediction(null, "Close the game menu to predict movement.", 0, 0);
                else
                {
                    var forecast=_predictor.Predict(Current, parameters, Input, previous, frameSeconds, fixedSeconds, remainder);
                    Prediction=forecast?.Available==true ? new MotionPrediction(forecast.Next,forecast.Explanation,forecast.PhysicsTicks,forecast.FrameSeconds,
                        forecast.Actions ?? PlayerActionAvailability.Evaluate(forecast.Next,parameters,actionContext)) : forecast;
                }
                if (session.Active && session.Trajectory?.Enabled == true && scale > 0 &&
                    session.Mode != SessionMode.Complete && session.Mode != SessionMode.Desync &&
                    !session.Adapter.Player.GamePaused && session.Adapter.Player.pauseMenu?.menuOpen != true)
                    Prediction = session.Trajectory.Predict(Current, Input, MovieFrame);
                Message = session.Active ? "Next TAS frame estimate (as if resumed/stepped)." : "Next rendered frame estimate using the current frame duration. Native timer expiry is not modeled outside TAS.";
            }
            catch (Exception error)
            {
                // Optional telemetry must never stop/save/reload a working run.
                Prediction = null; Message = "Player stats unavailable: " + error.Message;
            }
        }
    }
}
