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
    internal sealed class IgtapAdapter : IGameAdapter
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly string[] ActionFields = { "moveAction", "jumpAction", "jumpAction2", "dashAction", "dashAction2", "resetAction" };
        private static readonly Buttons[] ActionButtons = { Buttons.None, Buttons.Jump, Buttons.Jump2, Buttons.Dash, Buttons.Dash2, Buttons.Reset };
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
        private readonly InputAction[] _actions = new InputAction[6];
        private Rigidbody2D _body;
        public Movement Player { get; private set; }
        public bool IsAlive => Player != null && Player.isActiveAndEnabled;
        public bool OnMainMenu => UnityEngine.SceneManagement.SceneManager.GetActiveScene().path == ConfiguredStart.MenuScene;
        public string MainMenuScene => ConfiguredStart.MenuScene;
        public string SceneFor(TasStartSetup setup) => ConfiguredStart.SceneFor(setup);
        public void FinishWorldSetup(TasStartSetup setup) => ConfiguredStart.FinishWorldSetup(setup);
        public bool CanLoadScene(string scene) => Application.CanStreamedLevelBeLoaded(scene);
        private bool _configuredEnvironment;
        private int _originalDifficulty;
        private bool _originalVman;
        private Movement.placesToStart _originalOverride;
        public int CurrentDifficulty => globalStats.difficultyLevel;
        public void BeginRunEnvironment(int difficulty, bool fresh)
        {
            _originalDifficulty = globalStats.difficultyLevel;
            _originalVman = VmanScript.isCurrentlyVman;
            _originalOverride = globalStats.placesToStartOverride;
            _configuredEnvironment = true;
            globalStats.difficultyLevel = difficulty;
            if (fresh)
            {
                VmanScript.isCurrentlyVman = false;
                globalStats.placesToStartOverride = Movement.placesToStart.NORMAL;
            }
        }
        public void RestoreStartEnvironment()
        {
            if (!_configuredEnvironment) return;
            globalStats.difficultyLevel = _originalDifficulty;
            VmanScript.isCurrentlyVman = _originalVman;
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
            for (int i = 0; i < _actions.Length; i++)
            {
                _actions[i] = RequireField(ActionFields[i]).GetValue(Player) as InputAction;
                if (_actions[i] == null) return false; // Movement.Start has not run yet.
            }
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
            var builder = new StringBuilder();
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
            return new StateProbe {
                X = _body.position.x, Y = _body.position.y,
                RenderX = p.transform.position.x, RenderY = p.transform.position.y,
                BodyVelocityX = _body.linearVelocity.x, BodyVelocityY = _body.linearVelocity.y,
                VelocityX = p.Velocity.x, VelocityY = p.Velocity.y,
                MomentumX = p.momentum.x, MomentumY = p.momentum.y,
                Grounded = p.onGround, OnWall = p.OnWall, Dashing = p.dashActive, FacingRight = p.facingRight,
                AirJumps = p.airJumpsLeft, AirDashes = p.airDashesLeft, Cutscene = (int)p.cutsceneMode,
                MovementHash = Hash(Encoding.UTF8.GetBytes(builder.ToString())), MovementFields = fields
            };
        }
        public WorldWitness CaptureWorld() => WorldCheckpoint.Capture();
        private static string Number(float value) => Math.Round(value, 3).ToString("R", CultureInfo.InvariantCulture);
        internal static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
