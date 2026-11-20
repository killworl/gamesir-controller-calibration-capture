namespace ProCalReadHost;
internal static class Limits
{
    public const int MaxPayload = 4096, QueueCapacity = 1024, OutputBytes = 16 * 1024 * 1024, ReceiptReserveBytes = 256 * 1024;
}
internal enum Terminal
{
    OfflineReplayComplete, ActivationOnlyComplete, NoMatch, UnqualifiedReceiveCapture, CapabilityOnlyComplete, CapabilityUnavailable,
    AmbiguousIdentity, IdentityIncomplete, NativeFailure, MalformedCallback, Overflow,
    Disconnected, Interrupted, IoFailure, ReadReplyObservedUnqualified, ReadTimeout, ReadRejected, BindingNotReady
}
internal sealed class AuditLimitException : Exception { public AuditLimitException():base("Native audit capacity exceeded"){} }
internal sealed class NativeAudit
{
    private readonly object gate=new();
    private readonly List<NativeAttempt> entries=[];
    private int Begin(string api,string arguments)
    { lock(gate) { if(entries.Count>=256) throw new AuditLimitException(); entries.Add(new(api,null,DateTimeOffset.UtcNow.ToString("O"),arguments)); return entries.Count-1; } }
    private void End(int index,int hr) { lock(gate) entries[index]=entries[index] with {HResult=hr}; }
    public int Call(string api,string arguments,Func<int> action)
    {
        int i=Begin(api,arguments);
        try { int hr=action(); End(i,hr); return hr; } catch(Exception e) { End(i,e.HResult); throw; }
    }
    public T Value<T>(string api,string arguments,Func<T> action)
    {
        int i=Begin(api,arguments);
        try { T result=action(); End(i,0); return result; } catch(Exception e) { End(i,e.HResult); throw; }
    }
    public void Getter(string name,int hresult) { int i=Begin(name,"raw provider getter"); End(i,hresult); }
    public NativeAttempt[] Snapshot() { lock(gate) return entries.ToArray(); }
}
