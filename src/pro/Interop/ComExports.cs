using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProCalReadHost.Interop;
internal sealed unsafe class ComExports : ComWrappers
{
    private static readonly ComExports Instance=new();
    private static readonly ComInterfaceEntry* FactoryEntries=MakeEntries();
    public static OwnedComReference Export(Factory target)=>OwnedComReference.Adopt(Instance.GetOrCreateComInterfaceForObject(target,CreateComInterfaceFlags.None));
    protected override ComInterfaceEntry* ComputeVtables(object obj,CreateComInterfaceFlags flags,out int count)
    {
        if(obj is Factory) { count=2; return FactoryEntries; }
        throw new NotSupportedException("Controller exports require explicit inner/outer composition; only factories use ComWrappers");
    }
    protected override object CreateObject(nint externalComObject,CreateObjectFlags flags)=>throw new NotSupportedException("No imported object projection");
    protected override void ReleaseObjects(IEnumerable objects)=>throw new NotSupportedException("No imported objects");
    private static nint* Table(int size)
    {
        var p=(nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(ComExports),size*sizeof(nint));
        GetIUnknownImpl(out p[0],out p[1],out p[2]);
        p[3]=(nint)(delegate* unmanaged[Stdcall]<nint,uint*,Guid**,int>)&GetIids;
        p[4]=(nint)(delegate* unmanaged[Stdcall]<nint,nint*,int>)&GetName;
        p[5]=(nint)(delegate* unmanaged[Stdcall]<nint,int*,int>)&GetTrust;
        return p;
    }
    private static ComInterfaceEntry* MakeEntries()
    {
        var entries=(ComInterfaceEntry*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(ComExports),2*sizeof(ComInterfaceEntry));
        entries[0]=new(){IID=Abi.InspectableIid,Vtable=(nint)Table(6)};
        var f=Table(9); f[6]=(nint)(delegate* unmanaged[Stdcall]<nint,nint,nint*,int>)&Create;
        f[7]=(nint)(delegate* unmanaged[Stdcall]<nint,nint,int>)&Added;
        f[8]=(nint)(delegate* unmanaged[Stdcall]<nint,nint,int>)&Removed;
        entries[1]=new(){IID=Abi.FactoryIid,Vtable=(nint)f};
        return entries;
    }
    private static T Target<T>(nint self) where T:class=>ComInterfaceDispatch.GetInstance<T>((ComInterfaceDispatch*)self);
    private static int Failed(nint self,Exception e)
    {
        try { Target<ICallbackOwner>(self).Session.Seal(e is AuditLimitException?Terminal.Overflow:Terminal.NativeFailure); } catch { /* Never unwind through native ABI. */ }
        return Marshal.GetHRForException(e);
    }
    [DllImport("combase.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern int WindowsCreateString(string value,uint length,out nint handle);
    [DllImport("combase.dll",ExactSpelling=true,EntryPoint="WindowsDeleteString")] internal static extern int DeleteString(nint handle);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static int GetIids(nint self,uint* count,Guid** result)
    {
        if(count!=null) *count=0; if(result!=null) *result=null;
        if(count==null || result==null) return Abi.PointerError;
        try
        {
            Guid[] ids=[Abi.FactoryIid];
            var buffer=(Guid*)Marshal.AllocCoTaskMem(ids.Length*sizeof(Guid));
            ids.AsSpan().CopyTo(new Span<Guid>(buffer,ids.Length)); *result=buffer; *count=(uint)ids.Length; return 0;
        }
        catch(Exception e) { return Failed(self,e); }
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static int GetName(nint self,nint* value)
    {
        if(value==null) return Abi.PointerError; *value=0;
        try { const string name="Research.ProCalReadHost.Factory"; return WindowsCreateString(name,(uint)name.Length,out *value); }
        catch(Exception e) { return Failed(self,e); }
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int GetTrust(nint self,int* value)
    { if(value==null) return Abi.PointerError; *value=0; return 0; }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static int Create(nint self,nint provider,nint* value)
    {
        if(value==null) return Abi.PointerError; *value=0;
        try { *value=Target<Factory>(self).Create(provider); return 0; } catch(Exception e) { return Failed(self,e); }
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int Added(nint self,nint value)
    { try { return Target<Factory>(self).Lifecycle(value,true); } catch(Exception e) { return Failed(self,e); } }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int Removed(nint self,nint value)
    { try { return Target<Factory>(self).Lifecycle(value,false); } catch(Exception e) { return Failed(self,e); } }
}
