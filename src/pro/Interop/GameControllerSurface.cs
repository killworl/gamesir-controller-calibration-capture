namespace ProCalReadHost.Interop;
internal sealed class GameControllerSurface(CaptureSession session) : IDisposable
{
    private readonly object gate=new();
    private readonly Dictionary<long,(int Kind,OwnedComReference Handler)> handlers=[];
    private long nextToken;
    private bool closed;
    public int Add(int kind,nint handler,out long token)
    {
        token=0;
        if(handler==0) return Abi.PointerError;
        using var owned=OwnedComReference.Query(handler,Abi.UnknownIid);
        lock(gate)
        {
            if(closed || session.Terminal!=null) return Abi.Closed;
            if(handlers.Count>=128) { session.Seal(Terminal.Overflow); return Abi.Failure; }
            var retained=owned.Query(Abi.UnknownIid);
            token=++nextToken; handlers.Add(token,(kind,retained)); return 0;
        }
    }
    public int Remove(int kind,long token)
    {
        OwnedComReference? release;
        lock(gate) { if(!handlers.TryGetValue(token,out var h) || h.Kind!=kind) return Abi.InvalidArg; release=h.Handler; handlers.Remove(token); }
        release.Dispose(); return 0;
    }
    public void Dispose()
    {
        OwnedComReference[] release;
        lock(gate) { if(closed) return; closed=true; release=handlers.Values.Select(h=>h.Handler).ToArray(); handlers.Clear(); }
        foreach(var h in release) h.Dispose();
    }
}
