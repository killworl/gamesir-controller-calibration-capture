namespace ProCalReadHost;
internal static class Program
{
    public static int Main(string[] args)
    {
        using var stop=new CancellationTokenSource();
        ConsoleCancelEventHandler handler=(_,e)=>{ e.Cancel=true; stop.Cancel(); };
        Console.CancelKeyPress+=handler;
        try { return Run(args,()=>new Interop.WindowsRuntime(),new PhysicalReceiptStorage(),stop.Token); }
        finally { Console.CancelKeyPress-=handler; }
    }
    internal static int Run(string[] args,Func<Interop.IHostRuntime> runtimeFactory,IReceiptStorage storage,CancellationToken stop)
    {
        try
        {
            Options options = CommandLine.Parse(args);
            if (options.Mode == Mode.Help) { Console.WriteLine(CommandLine.Help); return 0; }
            using var recorder=RunRecorder.CreateNew(options.Output!,storage);
            if(options.Mode==Mode.OfflineReplay) return OfflineReplay.Run(options.Input!,recorder,stop);
            using var runtime=runtimeFactory();
            return HostRunner.RunPrepared(options,runtime,recorder,stop);
        }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
        catch (Exception e) { Console.Error.WriteLine($"Incomplete run: {e.GetType().Name}: {e.Message}"); return 3; }
    }
}
