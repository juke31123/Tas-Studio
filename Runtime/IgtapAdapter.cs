using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Igtap.Tas.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Igtap.Tas.Runtime
{
    internal sealed class IgtapAdapter : IGameAdapter, IEngineSearchCapture, ISearchMapCapture
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly string[] ActionFields = { "moveAction", "jumpAction", "jumpAction2", "dashAction", "dashAction2", "resetAction" };
        private static readonly Buttons[] ActionButtons = { Buttons.None, Buttons.Jump, Buttons.Jump2, Buttons.Dash, Buttons.Dash2, Buttons.Reset, Buttons.Menu };
        private static readonly FieldInfo PauseActionField = typeof(pauseMenuScript).GetField("pauseAction", Fields)
            ?? throw new NotSupportedException("Unsupported game build: pauseMenuScript.pauseAction is missing.");
        // Transient movement state and physics parameters, excluding cosmetic audio/particles.
        private static readonly string[] ProbeFields = {
            "coyoteFrames", "wallCoyoteFrames", "wallCoyoteDir", "dashBuffer", "jumpBuffer", "jumpCutBuffer",
            "dashFramesRemaining", "dashCooldown", "WaitingForDashInput", "dashDirection", "preDashMomentum",
            "wallJumpMovementLockRemaining", "wallJumpDirection", "wallJumpsLeft", "timeInAir", "recentlyJumped",
            "canHyper", "lastHyperY", "DashBounceFrames", "isDead", "respawnPoint", "courseResetPoint",
            "runSpeed", "jumpForce", "gravity", "dashSpeed", "dashFrames", "dashTimeStopTime",
            "wallJumpUnlocked", "doubleJumpUnlocked", "dashUnlocked", "blockSwapUnlocked", "omniDashUnlocked",
            "maxAirJumps", "maxAirDashes", "movingPlatformVelocity", "isBlueActiveOnRespawn"
        };
        private readonly FieldInfo[] _probeFields;
        private readonly FieldInfo _deadField = RequireField("isDead");
        private readonly InputAction[] _actions = new InputAction[7];
        private Rigidbody2D _body;
        private readonly StringBuilder _probeBuilder = new StringBuilder(2048);
        public Movement Player { get; private set; }
        public bool IsAlive => Player != null && Player.isActiveAndEnabled;
        public bool OnMainMenu => UnityEngine.SceneManagement.SceneManager.GetActiveScene().path == ConfiguredStart.MenuScene;
        public string MainMenuScene => ConfiguredStart.MenuScene;
        public string SceneFor(TasStartSetup setup) => ConfiguredStart.SceneFor(setup);
        public void FinishWorldSetup(TasStartSetup setup) => ConfiguredStart.FinishWorldSetup(setup);
        public void PrepareStart(TasStartSetup setup,bool recording)=>CustomMaps.PrepareStart(setup,recording);
        public bool PrepareCustomMap(TasStartSetup setup)=>CustomMaps.LoadDuringBarrier(setup,Player);
        public void ValidateLoadedMap(TasStartSetup setup,bool verifyFiles=false)=>CustomMaps.RequireLoaded(setup,verifyFiles);
        public string LoadedMapLabel=>CustomMaps.CurrentLabel;
        public string LoadedMapKey=>CustomMaps.CurrentKey;
        public bool CanLoadScene(string scene) => Application.CanStreamedLevelBeLoaded(scene);
        private bool _configuredEnvironment;
        private int _originalDifficulty;
        private bool _originalVman;
        private bool _originalMetaVman;
        private double _originalVmanTime,_originalVmanScore;
        private Dictionary<globalStats.globalUpgradeSet,double> _originalVmanUpgrades;
        private Movement.placesToStart _originalOverride;
        public int CurrentDifficulty => globalStats.difficultyLevel;
        public void BeginRunEnvironment(int difficulty, bool fresh)
        {
            _originalDifficulty = globalStats.difficultyLevel;
            _originalVman = VmanScript.isCurrentlyVman;
            _originalMetaVman=VmanScript.metaVman;_originalVmanTime=VmanScript.vmanTimeLeft;_originalVmanScore=VmanScript.vmanScore;_originalVmanUpgrades=VmanScript.VmanStatingUpgrades;
            CustomMaps.Begin();
            _originalOverride = globalStats.placesToStartOverride;
            _configuredEnvironment = true;
            globalStats.difficultyLevel = difficulty;
            if (fresh)
            {
                VmanScript.isCurrentlyVman = false;
                VmanScript.metaVman=false;VmanScript.vmanScore=VmanScript.vmanTimeLeft=0;
                globalStats.placesToStartOverride = Movement.placesToStart.NORMAL;
            }
        }
        public void RestoreStartEnvironment()
        {
            if (!_configuredEnvironment) return;
            globalStats.difficultyLevel = _originalDifficulty;
            VmanScript.isCurrentlyVman = _originalVman;
            VmanScript.metaVman=_originalMetaVman;VmanScript.vmanTimeLeft=_originalVmanTime;VmanScript.vmanScore=_originalVmanScore;VmanScript.VmanStatingUpgrades=_originalVmanUpgrades;
            CustomMaps.End();
            globalStats.placesToStartOverride = _originalOverride;
            _configuredEnvironment = false;
        }

        public IgtapAdapter()
        {
            _probeFields = ProbeFields.Select(RequireField).ToArray();
            foreach (var name in ActionFields) RequireField(name);
        }
        private static FieldInfo RequireField(string name) => typeof(Movement).GetField(name, Fields)
            ?? throw new NotSupportedException("Unsupported game build: Movement." + name + " is missing.");
        public bool Bind()
        {
            Player = UnityEngine.Object.FindFirstObjectByType<Movement>();
            if (!IsAlive) return false;
            if (Player.gameObject.scene.handle != UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle) return false;
            _body = Player.GetComponent<Rigidbody2D>();
            if (_body == null) return false;
            for (int i = 0; i < ActionFields.Length; i++)
            {
                _actions[i] = RequireField(ActionFields[i]).GetValue(Player) as InputAction;
                if (_actions[i] == null) return false; // Movement.Start has not run yet.
            }
            if (Player.pauseMenu == null) return false;
            _actions[6] = PauseActionField.GetValue(Player.pauseMenu) as InputAction;
            if (_actions[6] == null) return false;
            return true;
        }
        public void PrepareSimulation()
        {
            // Preserve the scene's interpolation. Movement reads its interpolated
            // transform for jumps, wall contacts and frame-based step-ups.
            Physics2D.SyncTransforms();
        }
        public void ReleaseSimulation()
        {
            WorldCheckpoint.Reset();
        }
        public object Diagnostics()
        {
            if (!IsAlive || _body == null) return null;
            var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in _probeFields) fields[field.Name] = Convert.ToString(field.GetValue(Player), CultureInfo.InvariantCulture);
            return new {
                BodyX = _body.position.x, BodyY = _body.position.y,
                TransformX = Player.transform.position.x, TransformY = Player.transform.position.y,
                BodyVelocityX = _body.linearVelocity.x, BodyVelocityY = _body.linearVelocity.y,
                Interpolation = _body.interpolation.ToString(), Sleeping = _body.IsSleeping(),
                Fields = fields
            };
        }
        public Buttons ButtonFor(InputAction action)
        {
            for (int i = 1; i < _actions.Length; i++) if (ReferenceEquals(_actions[i], action)) return ActionButtons[i];
            return Buttons.None;
        }
        public bool IsMove(InputAction action) => ReferenceEquals(_actions[0], action);
        public FrameInput ReadPhysicalInput()
        {
            var move = _actions[0].ReadValue<Vector2>();
            var result = new FrameInput { X = move.x, Y = move.y, ExactEdges = true };
            for (int i = 1; i < _actions.Length; i++)
            {
                if (_actions[i].IsPressed()) result.Held |= ActionButtons[i];
                if (_actions[i].WasPressedThisFrame()) result.Press |= ActionButtons[i];
                if (_actions[i].WasReleasedThisFrame()) result.Release |= ActionButtons[i];
            }
            return result;
        }
        public StateProbe Probe()
        {
            if (!IsAlive) return null;
            var p = Player;
            var builder = _probeBuilder; builder.Clear();
            var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in _probeFields)
            {
                builder.Append(field.Name).Append('=');
                var value = field.GetValue(p);
                string text = value is Vector2 v ? Number(v.x) + "," + Number(v.y)
                    : value is float f ? Number(f) : Convert.ToString(value, CultureInfo.InvariantCulture);
                fields[field.Name] = text;
                builder.Append(text).Append(';');
            }
            var position=_body.position; var render=p.transform.position;var velocity=_body.linearVelocity;var control=p.Velocity;
            return new StateProbe {
                X = position.x, Y = position.y,
                RenderX = render.x, RenderY = render.y,
                BodyVelocityX = velocity.x, BodyVelocityY = velocity.y,
                VelocityX = control.x, VelocityY = control.y,
                MomentumX = p.momentum.x, MomentumY = p.momentum.y,
                Grounded = p.onGround, OnWall = p.OnWall, Dashing = p.dashActive, FacingRight = p.facingRight,
                AirJumps = p.airJumpsLeft, AirDashes = p.airDashesLeft, Cutscene = (int)p.cutsceneMode,
                MenuOpen = p.pauseMenu != null && p.pauseMenu.menuOpen, GamePaused = p.GamePaused,
                MovementHash = Hash(Encoding.UTF8.GetBytes(builder.ToString())), MovementFields = fields
            };
        }
        public WorldWitness CaptureWorld() => WorldCheckpoint.Capture();
        public WorldWitness CaptureReplayWorld() => WorldCheckpoint.CaptureReplay();
        public MotionVector SearchWorldPosition() => SearchExport.PlayerWorld(Player);
        public Core.Search.AbilityResources SearchResources() => new Core.Search.AbilityResources { AirJumps = Player.airJumpsLeft,
            Dashes = Player.airDashesLeft, WallJumps = Player.wallJumpsLeft, Alive = IsAlive && !(bool)_deadField.GetValue(Player) };
        public void CaptureSearch(TasSession session, Core.Search.SearchJob job) => SearchExport.Capture(session, job);
        public Core.Search.SearchBody[] CaptureSearchMap(TasSession session){var job=new Core.Search.SearchJob{Fps=session.Movie.FrameRate,Options=session.Search.Options.Copy()};SearchExport.Capture(session,job,true);return job.Geometry;}
        private bool _enginePreviousHistory;
        public void BeginEngineCapture() { _enginePreviousHistory=SimulationCapture.CollectHistory; SimulationCoroutineCapture.Clear(); SimulationCapture.BeginHistory(); SimulationCapture.CollectHistory=true; }
        public void ExportEngineBootstrap(TasSession session,string directory) => SimulationCapture.Export(session,directory);
        public Core.Simulation.SimulationSample CaptureEngineFrame(TasSession session,int frame,int pus) => SimulationCapture.Sample(session,frame,pus,"frame-end");
        public void CaptureEngineSummary(TasSession session,Core.Search.SearchJob job) => SearchExport.CaptureEngineSummary(session,job);
        public void EndEngineCapture() => SimulationCapture.CollectHistory=_enginePreviousHistory;
        private static string Number(float value) => Math.Round(value, 3).ToString("R", CultureInfo.InvariantCulture);
        internal static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
