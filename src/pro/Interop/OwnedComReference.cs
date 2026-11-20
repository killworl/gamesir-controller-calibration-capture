namespace ProCalReadHost.Interop;
internal sealed class OwnedComReference : IDisposable
{
    private nint pointer, canonical;
    private OwnedComReference(nint owned)
    {
        if(owned==0) throw new ArgumentNullException(nameof(owned));
        try { canonical=Abi.Query(owned,Abi.UnknownIid); pointer=owned; }
        catch { Abi.Release(owned); throw; }
    }
    public static OwnedComReference Adopt(nint owned) => new(owned);
    public static OwnedComReference Query(nint borrowed,Guid iid) => Adopt(Abi.Query(borrowed,iid));
    public OwnedComReference Query(Guid iid) => Query(Pointer,iid);
    public nint Pointer => pointer!=0?pointer:throw new ObjectDisposedException(nameof(OwnedComReference));
    public nint CanonicalIdentity => pointer!=0?canonical:throw new ObjectDisposedException(nameof(OwnedComReference));
    public void Dispose()
    {
        nint p=Interlocked.Exchange(ref pointer,0);
        if(p==0) return;
        nint c=Interlocked.Exchange(ref canonical,0);
        Abi.Release(p); Abi.Release(c);
    }
}
