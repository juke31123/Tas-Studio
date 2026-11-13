using System;
using System.IO;
using System.Text;
using Igtap.Tas.Core.Simulation;

namespace Igtap.Tas.Core
{
    // Ephemeral IPC, never a movie/save format. Fixed typed fields avoid the
    // JSON object graph and repeated property names for every forecast point.
    public static class TrajectoryWire
    {
        public const int Version = 1;
        private const int Magic = 0x54535030 + Version, MaxBytes = 16 * 1024 * 1024;
        public static bool WriteRequest(string path, TrajectoryRequest value, TrajectoryJournal.Snapshot history = null) => Write(path, 1, w =>
        {
            value.Validate(); w.Write(value.Id); w.Write(value.Frame); w.Write(value.Frames); w.Write(value.NextFramePrepared); Input(w, value.Input);
            int length=history?.Count ?? value.Prefix.Length;
            if(length>20000)throw new ArgumentException("Trajectory history exceeds limit.");
            w.Write(length); int span=0,seen=0; FrameInput previous=default;
            var rows=history==null ? (System.Collections.Generic.IEnumerable<FrameInput>)value.Prefix : history.Rows();
            foreach(var row in rows)
            {
                TrajectoryRequest.Check(row); seen++;
                if(span!=0 && !TrajectoryRequest.Same(previous,row)) {w.Write(span);Input(w,previous);span=0;}
                previous=row;span++;
            }
            if(seen!=length)throw new ArgumentException("Trajectory input count differs from its history.");
            if(span!=0){w.Write(span);Input(w,previous);}
            w.Write(value.Expected.Fields.Count);
            foreach (var field in value.Expected.Fields) { Text(w, field.Key); Text(w, field.Value); }
        });
        public static bool ReadRequest(string path, out TrajectoryRequest value) => Read(path, 1, r =>
        {
            var request = new TrajectoryRequest { Id=r.ReadInt64(), Frame=r.ReadInt32(), Frames=r.ReadInt32(), NextFramePrepared=r.ReadBoolean(), Input=Input(r) };
            int length = Count(r, 20000); request.Prefix = new FrameInput[length];
            for (int i = 0; i < length;)
            {
                int span = Count(r, length - i); if (span == 0) throw new InvalidDataException("Empty trajectory input span.");
                var input = Input(r); for (int j = 0; j < span; j++) request.Prefix[i++] = input;
            }
            request.Expected = new SimulationSample(); int fields = Count(r, 20000);
            for (int i = 0; i < fields; i++) request.Expected.Fields.Add(Text(r), Text(r));
            request.Validate(); return request;
        }, out value);
        public static bool WriteResult(string path, TrajectoryResult value) => Write(path, 2, w =>
        {
            w.Write(value.Id); w.Write(value.Frame); w.Write(value.Fps); Input(w, value.Input);
            w.Write(value.Ready); w.Write(value.SnapshotEstimate); w.Write(value.SimulationMilliseconds);
            Text(w, value.Message); Text(w, value.End); w.Write(value.Points.Length);
            foreach (var point in value.Points)
            {
                w.Write(point.Pus); Motion(w, point.Motion); w.Write(point.Actions != null);
                if (point.Actions != null)
                { w.Write((byte)point.Actions.Jump); w.Write(point.Actions.CanDash); Text(w, point.Actions.JumpText); Text(w, point.Actions.DashText); }
            }
        });
        public static bool ReadResult(string path, out TrajectoryResult value) => Read(path, 2, r =>
        {
            var result = new TrajectoryResult { Id=r.ReadInt64(), Frame=r.ReadInt32(), Fps=r.ReadInt32(), Input=Input(r),
                Ready=r.ReadBoolean(), SnapshotEstimate=r.ReadBoolean(), SimulationMilliseconds=r.ReadDouble(), Message=Text(r), End=Text(r) };
            int length = Count(r, 2001); result.Points = new TrajectoryPoint[length];
            for (int i = 0; i < length; i++)
            {
                var point = new TrajectoryPoint { Pus=r.ReadInt64(), Motion=Motion(r) };
                if (r.ReadBoolean())
                {
                    var jump=(PlayerJumpKind)r.ReadByte(); bool dash=r.ReadBoolean();
                    if (jump < PlayerJumpKind.None || jump > PlayerJumpKind.Hyper) throw new InvalidDataException("Invalid jump type.");
                    point.Actions=new PlayerActionAvailability(jump, Text(r), dash, Text(r));
                }
                result.Points[i]=point;
            }
            if (result.Id <= 0 || result.Frame < 0 || result.Ready && length == 0) throw new InvalidDataException("Invalid trajectory result.");
            return result;
        }, out value);
        private static bool Write(string path, byte kind, Action<BinaryWriter> body)
        {
            string temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                { writer.Write(Magic); writer.Write(kind); body(writer); writer.Flush(); if (stream.Length > MaxBytes) throw new InvalidDataException("Trajectory message exceeds limit."); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                return true;
            }
            catch (IOException) { return false; }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
        }
        private static bool Read<T>(string path, byte kind, Func<BinaryReader,T> body, out T value)
        {
            value=default;
            try
            {
                using var stream=new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaxBytes) return false;
                using var reader=new BinaryReader(stream, Encoding.UTF8);
                if (reader.ReadInt32()!=Magic || reader.ReadByte()!=kind) return false;
                T result=body(reader); if (stream.Position!=stream.Length) return false;
                value=result; return true;
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }
        private static int Count(BinaryReader r, int max)
        { int value=r.ReadInt32(); if (value < 0 || value > max) throw new InvalidDataException("Invalid trajectory length."); return value; }
        private static void Text(BinaryWriter w, string value)
        { var bytes=Encoding.UTF8.GetBytes(value ?? ""); if (bytes.Length>4096) throw new InvalidDataException("Trajectory text exceeds limit."); w.Write(bytes.Length); w.Write(bytes); }
        private static string Text(BinaryReader r)
        { int count=Count(r,4096); var bytes=r.ReadBytes(count); if (bytes.Length!=count) throw new EndOfStreamException(); return Encoding.UTF8.GetString(bytes); }
        private static void Input(BinaryWriter w, FrameInput input)
        { w.Write(input.X); w.Write(input.Y); w.Write((byte)input.Held); w.Write(input.ExactEdges); w.Write((byte)input.Press); w.Write((byte)input.Release); }
        private static FrameInput Input(BinaryReader r) => new FrameInput { X=r.ReadSingle(),Y=r.ReadSingle(),Held=(Buttons)r.ReadByte(),ExactEdges=r.ReadBoolean(),Press=(Buttons)r.ReadByte(),Release=(Buttons)r.ReadByte() };
        private static void Vector(BinaryWriter w, MotionVector v) { w.Write(v.X); w.Write(v.Y); }
        private static MotionVector Vector(BinaryReader r) => new MotionVector(r.ReadSingle(),r.ReadSingle());
        private static void Motion(BinaryWriter w, PlayerMotion m)
        {
            Vector(w,m.Position); Vector(w,m.Velocity); Vector(w,m.ControlVelocity); Vector(w,m.Momentum); Vector(w,m.PlatformVelocity); Vector(w,m.DashDirection);
            int flags=(m.Grounded?1:0)|(m.OnWall?2:0)|(m.Dashing?4:0)|(m.Dead?8:0)|(m.FacingRight?16:0)|(m.RecentlyJumped?32:0)|
                (m.NormalJump?64:0)|(m.JumpBuffered?128:0)|(m.JumpCutBuffered?256:0)|(m.DashBuffered?512:0)|(m.OnMovingPlatform?1024:0)|(m.FinalWallCling?2048:0)|(m.PhysicsSimulated?4096:0);
            w.Write((ushort)flags); w.Write(m.Cutscene); w.Write(m.AirJumps); w.Write(m.AirDashes); w.Write(m.DashBounceFrames);
            w.Write(m.DashFramesRemaining); w.Write(m.FramesSinceDash); w.Write(m.DashCooldown); w.Write(m.WallJumpLock); w.Write(m.WallJumpDirection); w.Write(m.WallCoyoteFrames);
            w.Write(m.JumpBufferExpires); w.Write(m.JumpCutExpires); w.Write(m.DashBufferExpires); w.Write(m.RecentJumpExpires);
        }
        private static PlayerMotion Motion(BinaryReader r)
        {
            var m=new PlayerMotion {Position=Vector(r),Velocity=Vector(r),ControlVelocity=Vector(r),Momentum=Vector(r),PlatformVelocity=Vector(r),DashDirection=Vector(r)};
            int f=r.ReadUInt16(); m.Grounded=(f&1)!=0; m.OnWall=(f&2)!=0; m.Dashing=(f&4)!=0; m.Dead=(f&8)!=0;
            m.FacingRight=(f&16)!=0; m.RecentlyJumped=(f&32)!=0; m.NormalJump=(f&64)!=0; m.JumpBuffered=(f&128)!=0; m.JumpCutBuffered=(f&256)!=0;
            m.DashBuffered=(f&512)!=0; m.OnMovingPlatform=(f&1024)!=0; m.FinalWallCling=(f&2048)!=0; m.PhysicsSimulated=(f&4096)!=0;
            m.Cutscene=r.ReadInt32(); m.AirJumps=r.ReadInt32(); m.AirDashes=r.ReadInt32(); m.DashBounceFrames=r.ReadInt32();
            m.DashFramesRemaining=r.ReadSingle(); m.FramesSinceDash=r.ReadSingle(); m.DashCooldown=r.ReadSingle(); m.WallJumpLock=r.ReadSingle(); m.WallJumpDirection=r.ReadSingle(); m.WallCoyoteFrames=r.ReadSingle();
            m.JumpBufferExpires=r.ReadDouble(); m.JumpCutExpires=r.ReadDouble(); m.DashBufferExpires=r.ReadDouble(); m.RecentJumpExpires=r.ReadDouble();
            if (!Movie.Finite(m.Position.X)||!Movie.Finite(m.Position.Y)||!Movie.Finite(m.Velocity.X)||!Movie.Finite(m.Velocity.Y)) throw new InvalidDataException("Invalid preview coordinates.");
            return m;
        }
    }
}
