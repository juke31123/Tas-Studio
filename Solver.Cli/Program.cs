using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using Igtap.Tas.Core.Search;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Usage: TasStudio.Solver <job.json>"); return 2; }
        string path = Path.GetFullPath(args[0]), directory = Path.GetDirectoryName(path);
        SearchJob job = null;
        try
        {
            job = SearchFiles.Read<SearchJob>(path); job.Validate();
            if(job.Options.Mode=="wave") throw new NotSupportedException("Wave search requires the prepared game-code host.");
            long nextCancelPoll = 0; int cancelled = 0;
            bool Cancel()
            {
                long now = Stopwatch.GetTimestamp(), due = Interlocked.Read(ref nextCancelPoll);
                if (now >= due && Interlocked.CompareExchange(ref nextCancelPoll, now + Stopwatch.Frequency / 10, due) == due)
                    if (File.Exists(Path.Combine(directory, "cancel"))) Interlocked.Exchange(ref cancelled, 1);
                return Volatile.Read(ref cancelled) != 0;
            }
            void Progress(SearchResult report) => SearchFiles.Write(Path.Combine(directory, "progress.json"), report);
            var result = job.Options.Mode == "chain" ? new ChainSearch().Run(job, Cancel, Progress) : new SectionSearch().Run(job, Cancel, Progress);
            SearchFiles.Write(Path.Combine(directory, "result.json"), result);
            return 0;
        }
        catch (Exception e)
        {
            try { SearchFiles.Write(Path.Combine(directory, "result.json"), new SearchResult { JobId = job?.Id ?? "", MovieHash = job?.MovieHash ?? "", Status = "Error", Message = e.Message, Finished = true }); } catch { }
            Console.Error.WriteLine(e); return 1;
        }
    }
}
