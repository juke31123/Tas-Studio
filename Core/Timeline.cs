using System;
using System.Collections.Generic;

namespace Igtap.Tas.Core
{
    public static class Timeline
    {
        public static void Replace(Movie movie, int start, int count, FrameInput input)
        {
            Range(movie, start, count);
            for (int i = start; i < start + count; i++) movie.Frames[i].Input = input;
            Invalidate(movie, start);
        }
        public static void Insert(Movie movie, int start, int count, FrameInput input)
        {
            Range(movie, start, 0);
            if (count < 1 || count > Movie.MaxFrames - movie.Frames.Count) throw new ArgumentOutOfRangeException(nameof(count));
            var frames = new List<MovieFrame>(count);
            for (int i = 0; i < count; i++) frames.Add(new MovieFrame { Input = input });
            movie.Frames.InsertRange(start, frames);
            Invalidate(movie, start);
        }
        public static void Delete(Movie movie, int start, int count)
        {
            Range(movie, start, count);
            movie.Frames.RemoveRange(start, count);
            Invalidate(movie, start);
        }
        public static void Branch(Movie movie, int nextFrame)
        {
            Range(movie, nextFrame, 0);
            movie.Frames.RemoveRange(nextFrame, movie.Frames.Count - nextFrame);
            movie.Rerecords++;
        }
        private static void Range(Movie movie, int start, int count)
        {
            if (start < 0 || count < 0 || start > movie.Frames.Count || count > movie.Frames.Count - start) throw new ArgumentOutOfRangeException(nameof(start));
        }
        internal static void Invalidate(Movie movie, int start)
        {
            // An edit can change every subsequent state, not only the edited frame.
            for (int i = start; i < movie.Frames.Count; i++)
            {
                var frame = movie.Frames[i];
                if (frame.FixedTicks >= 0) frame.PreviousFixedTicks = frame.FixedTicks;
                frame.Expected = null; frame.World = null; frame.FixedTicks = -1; frame.PuPhase = -1;
            }
        }
    }
}
