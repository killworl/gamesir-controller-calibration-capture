using System.Runtime.InteropServices;
namespace ProCalReadHost.Interop;
internal interface IHostRuntime : IDisposable
{
    string EvidenceSource { get; }
    int Initialize();
    OwnedComReference ActivateManager();
    int RegisterHardware(nint manager,nint factory,ushort vid,ushort pid);
    OwnedComReference? ResolveController(nint manager,nint factory,nint controller);
    void KeepUntilExit(IDisposable roots);
}
internal sealed unsafe class WindowsRuntime : IHostRuntime
{
    private const string FactoryClass="Windows.Gaming.Input.Custom.GameControllerFactoryManager";
    private static readonly List<IDisposable> ProcessRoots=[];
    private static int registeredLifetime;
    private bool initialized,retained,disposed;
    private int ownerThread;
    public string EvidenceSource=>"windows_callback";
    [DllImport("combase.dll",ExactSpelling=true)] private static extern int RoInitialize(uint initType);
    [DllImport("combase.dll",ExactSpelling=true)] private static extern void RoUninitialize();
    [DllImport("combase.dll",ExactSpelling=true,CharSet=CharSet.Unicode)] private static extern int WindowsCreateString(string text,uint length,out nint handle);
    [DllImport("combase.dll",ExactSpelling=true)] private static extern int WindowsDeleteString(nint handle);
    [DllImport("combase.dll",ExactSpelling=true)] private static extern int RoGetActivationFactory(nint className,in Guid iid,out nint result);
    public int Initialize()
    {
        if(initialized || disposed) throw new InvalidOperationException("Runtime already used");
        if(!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("Windows x64 only");
        ownerThread=Environment.CurrentManagedThreadId;
        int hr=RoInitialize(1); initialized=hr>=0; return hr;
    }
    public OwnedComReference ActivateManager()
    {
        if(!initialized || disposed || ownerThread!=Environment.CurrentManagedThreadId) throw new InvalidOperationException("Wrong runtime thread/state");
        nint text=0,value=0;
        try
        {
            int hr=WindowsCreateString(FactoryClass,(uint)FactoryClass.Length,out text); Marshal.ThrowExceptionForHR(hr);
            hr=RoGetActivationFactory(text,Abi.ManagerIid,out value);
            if(hr<0) { if(value!=0) { Abi.Release(value); value=0; } throw new COMException("OS manager activation failed",hr); }
            nint transfer=value; value=0; return OwnedComReference.Adopt(transfer);
        }
        finally { if(value!=0) Abi.Release(value); if(text!=0) WindowsDeleteString(text); }
    }
    public int RegisterHardware(nint manager,nint factory,ushort vid,ushort pid)=>RegisterAt(manager,factory,vid,pid);
    internal static int RegisterAt(nint manager,nint factory,ushort vid,ushort pid)
    {
        if(manager==0 || factory==0 || vid!=0x3537 || pid!=0x1003) return Abi.InvalidArg;
        return ((delegate* unmanaged[Stdcall]<nint,nint,ushort,ushort,int>)(*(nint**)manager)[7])(manager,factory,vid,pid);
    }
    public OwnedComReference? ResolveController(nint manager,nint factory,nint controller)=>ResolveAt(manager,factory,controller);
    internal static OwnedComReference? ResolveAt(nint manager,nint factory,nint controller)
    {
        using var statics2=OwnedComReference.Query(manager,Abi.Manager2Iid);
        nint result=0;
        int hr=((delegate* unmanaged[Stdcall]<nint,nint,nint,nint*,int>)(*(nint**)statics2.Pointer)[6])(statics2.Pointer,factory,controller,&result);
        if(hr<0) { if(result!=0) Abi.Release(result); throw new COMException("Factory controller lookup failed",hr); }
        return result==0?null:OwnedComReference.Adopt(result);
    }
    public void KeepUntilExit(IDisposable roots)
    {
        if(Interlocked.CompareExchange(ref registeredLifetime,1,0)!=0) throw new InvalidOperationException("Only one registration lifetime per process");
        lock(ProcessRoots) ProcessRoots.Add(roots);
        retained=true;
    }
    public void Dispose()
    {
        if(disposed) return;
        if(initialized && ownerThread!=Environment.CurrentManagedThreadId) throw new InvalidOperationException("MTA must be released by its owning thread");
        disposed=true;
        // No unregister exists. Registered objects AND this MTA live until process exit.
        if(initialized && !retained) { RoUninitialize(); initialized=false; }
    }
}
