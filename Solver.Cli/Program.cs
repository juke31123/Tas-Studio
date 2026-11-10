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
            void Progress(SearchResult report) => SearchFiles.WriteProgress(Path.Combine(directory, "progress.json"), report);
            SearchResult Algorithm(SearchJob j,Func<bool> stop,Action<SearchResult> report,Func<SearchJob,ISearchSimulation> create)=>
                j.Options.Mode=="chain"?new ChainSearch().Run(j,stop,report,create):new SectionSearch().Run(j,stop,report,create);
            var result=CheckpointSearch.Run(job,Cancel,Progress,j=>new StaticPlatformSimulation(j),Algorithm);
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
