using System;
using System.IO;
using System.Text.Json;
using IGTAP.EngineSim;
using Igtap.Tas.Core;
using Igtap.Tas.Core.Search;
using Igtap.Tas.Core.Simulation;

internal static class EngineFixture
{
    public static void Validate(string directory,SimulationTrace reference)
    {
        reference.Validate();
        if(SimulationTraceComparer.FileHash(Path.Combine(directory,"world.json"))!=reference.WorldHash ||
            SimulationTraceComparer.FileHash(Path.Combine(directory,"animation.json"))!=reference.AnimationHash) throw new InvalidDataException("Native engine asset hashes differ.");
        var manifest=SearchFiles.Read<Newtonsoft.Json.Linq.JObject>(Path.Combine(AppContext.BaseDirectory,"engine-host.manifest.json"));
        string game=(string)manifest["GameSha256"];
        if(Convert.ToBase64String(Convert.FromHexString(game))!=reference.GameHash && !string.Equals(game,reference.GameHash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The prepared host belongs to another game build.");
    }
    // Construct on the worker that owns the world. Mutable game/shim statics
    // are per thread; an engine snapshot may never migrate to another worker.
    public static Simulation Load(string directory, SimulationTrace reference)
    {
        var simulation = new Simulation(World.Load(Path.Combine(directory, "world.json")));
        using var start = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "start.json")));
        using var world = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "world.json")));
        var time = world.RootElement.GetProperty("time");
        simulation.LoadStart(start.RootElement, canonical: false);
        simulation.ConfigureStudioFrames(reference.Fps, reference.StartPhase, Input(reference.Previous, default),
            time.GetProperty("nativeTime").GetDouble(), time.GetProperty("nativeFixedTime").GetDouble(),
            time.GetProperty("captureTicks").GetInt64(), reference.AdvanceMode == "continuous");
        if(start.RootElement.TryGetProperty("interpolationValid",out var interpolate)) simulation.ConfigureStudioInterpolation(interpolate.GetBoolean());
        simulation.ConfigureStudioMoverContinuations(start.RootElement);
        simulation.ConfigureStudioMoverSleep(start.RootElement);
        using var animation = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "animation.json")));
        simulation.ConfigureStudioAnimation(animation.RootElement);
        simulation.ConfigureStudioEconomy();
        return simulation;
    }

    public static StudioInput Input(FrameInput input, FrameInput previous)
    {
        int press = 0, release = 0;
        foreach (Buttons button in new[] { Buttons.Jump, Buttons.Jump2, Buttons.Dash, Buttons.Dash2, Buttons.Reset, Buttons.Menu })
        { if (input.Pressed(button, previous)) press |= (int)button; if (input.Released(button, previous)) release |= (int)button; }
        return new StudioInput { X = input.X, Y = input.Y, Held = (int)input.Held, Press = press, Release = release };
    }

    public static SimulationSample Sample(Simulation simulation, int frame, int pu, string phase, bool orbs = false, bool movers = false, bool springs = false)
    {
        var sample = new SimulationSample { Frame = frame, Pu = pu, Phase = phase };
        SimulationFieldCapture.AddPlayer(sample.Fields, simulation.Player);
        var b = simulation.Physics.PlayerBody(); var p = simulation.Player.transform.position; var origin = simulation.WorldOriginInScene;
        SimulationFieldCapture.AddVector(sample.Fields, "body/position", b.px, b.py);
        SimulationFieldCapture.AddVector(sample.Fields, "body/velocity", b.vx, b.vy);
        SimulationFieldCapture.AddVector(sample.Fields, "render/position", p.x, p.y);
        SimulationFieldCapture.AddVector(sample.Fields, "origin", origin.x, origin.y);
        sample.Fields["clock/native-time"] = SimulationTraceComparer.Double(phase == "before-pu" || phase == "after-pu" ? simulation.StudioNativeFixedTime : simulation.StudioNativeTime);
        sample.Fields["clock/fixed-time"] = SimulationTraceComparer.Double(simulation.StudioNativeFixedTime);
        sample.Fields["clock/capture-ticks"] = simulation.StudioCaptureTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SimulationFieldCapture.AddRandom(sample.Fields, UnityEngine.Random.state);
        SimulationFieldCapture.AddEconomy(sample.Fields, globalStats.currencyLookup, globalStats.globalUpgradeDict);
        if (orbs) foreach (var orb in simulation.StudioOrbs) SimulationFieldCapture.AddOrb(sample.Fields, orb, orb.GetInstanceID());
        if (springs) foreach (var spring in simulation.World.All<SpringScript>()) SimulationFieldCapture.AddScript(sample.Fields, spring, "spring/" + spring.GetInstanceID());
        if (movers) foreach (var mover in simulation.World.All<PlatformMover>()) {
            var body=(UnityEngine.Rigidbody2D)SimulationFieldCapture.AddMover(sample.Fields, mover, mover.GetInstanceID());
            sample.Fields["mover/"+mover.GetInstanceID()+"/sleeping"]=(!simulation.Physics.StudioBodyAwake(body)).ToString();
        }
        return sample;
    }
}
