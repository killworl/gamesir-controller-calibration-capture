using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace ProCalReadHost.Interop;

// Nondelegating private implementation + public interfaces controlled by the
// Windows outer after Initialize. COM references, not the managed GC, own this
// allocation. The outer owns an inner reference; the reverse link is borrowed.
internal sealed unsafe class ControllerExport
{
    internal static readonly Guid ImplIid=new("06E58977-7684-4DC5-BAD1-CDA52A4AA06D");
    private static readonly nint PrivateTable=MakeTable(0), InputTable=MakeTable(1), GipTable=MakeTable(2), ControllerTable=MakeTable(3);
    private readonly object gate=new();
    private readonly InputSink sink;
    private readonly nint providerIdentity,block;
    private GCHandle handle;
    private int references=1,uncomposedPublicReferences;
    private int queriedSinkInterfaces;
    private nint outer;
    private nint At(int index)=>block+index*2*sizeof(nint);
    private ControllerExport(InputSink target,nint provider)
    {
        sink=target; providerIdentity=provider;
        block=(nint)NativeMemory.AllocZeroed(8,(nuint)sizeof(nint));
        if(block==0) throw new OutOfMemoryException();
        try {
            handle=GCHandle.Alloc(this);
            nint[] tables=[PrivateTable,InputTable,GipTable,ControllerTable];
            for(int i=0;i<4;i++) { ((nint*)At(i))[0]=tables[i]; ((nint*)At(i))[1]=GCHandle.ToIntPtr(handle); }
        } catch { NativeMemory.Free((void*)block); throw; }
    }
    public static OwnedComReference Export(InputSink target,nint provider)=>OwnedComReference.Adopt(new ControllerExport(target,provider).block);
    private static ControllerExport Get(nint p)=>(ControllerExport)GCHandle.FromIntPtr(((nint*)p)[1]).Target!;
    private uint AddInner()=>(uint)Interlocked.Increment(ref references);
    private uint ReleaseInner()
    {
        int left=Interlocked.Decrement(ref references);
        if(left==0) { sink.Dispose(); NativeMemory.Free((void*)block); handle.Free(); }
        return (uint)left;
    }
    private uint AddPublic()
    {
        nint owner;
        lock(gate) {
            owner=outer;
            if(owner==0) { uncomposedPublicReferences++; return AddInner(); }
        }
        return Abi.AddRef(owner);
    }
    private uint ReleasePublic()
    {
        nint owner;
        lock(gate) { owner=outer; if(owner==0) uncomposedPublicReferences--; }
        // Outer Release can destroy this inner. Do not access fields afterward.
        return owner!=0?Abi.Release(owner):ReleaseInner();
    }
    private int QueryInner(Guid iid,out nint value)
    {
        value=0;
        if(iid==Abi.UnknownIid || iid==Abi.InspectableIid || iid==ImplIid)
        { value=block; AddInner(); return 0; }
        nint owner;
        lock(gate) {
            owner=outer;
            if(iid==Abi.InputSinkIid) value=At(1);
            else if(iid==Abi.GipSinkIid) value=At(2);
            else if(iid==Abi.GameControllerIid) {
                if(owner==0) value=At(3); // Standalone surface only, before composition.
            }
            else return Abi.NoInterface;
            // Select the standalone surface and acquire its reference in the
            // same critical section: Initialize cannot switch owners between.
            if(owner==0) { uncomposedPublicReferences++; AddInner(); RecordSinkInterface(iid); return 0; }
        }
        if(iid==Abi.GameControllerIid) value=Abi.Query(owner,iid);
        else Abi.AddRef(owner);
        RecordSinkInterface(iid);
        return 0;
    }
    private void RecordSinkInterface(Guid iid)
    {
        int bit=iid==Abi.InputSinkIid?1:iid==Abi.GipSinkIid?2:0;
        if(bit!=0 && (Interlocked.Or(ref queriedSinkInterfaces,bit)&bit)==0)
            sink.Session.Lifecycle("binding_interface",sink.CandidateId,detail:
                System.Text.Json.JsonSerializer.Serialize(new {Interface=bit==1?"IGameControllerInputSink":"IGipGameControllerInputSink",HResult=0},RunRecorder.Json));
    }
    private int InitializeCore(nint owner,nint provider)
    {
        if(owner==0 || provider==0) return Fail(Abi.PointerError,"binding_invalid_null");
        using var supplied=OwnedComReference.Query(provider,Abi.GenericProviderIid);
        using var controller=OwnedComReference.Query(owner,Abi.GameControllerIid);
        lock(gate) {
            if(sink.Session.Terminal!=null) return Abi.Closed;
            if(outer!=0 || uncomposedPublicReferences!=0 || supplied.CanonicalIdentity!=providerIdentity || controller.CanonicalIdentity==block)
                return Fail(Abi.InvalidArg,"binding_invalid_identity_or_state");
            // Query references are released at exit: do NOT retain the outer.
            outer=controller.CanonicalIdentity;
            return sink.Initialized(outer);
        }
    }
    private int Fail(int hr,string detail)
    { sink.Session.Lifecycle(detail,sink.CandidateId); sink.Session.Seal(Terminal.NativeFailure); return hr; }
    private static int Error(nint self,Exception e)
    { try { Get(self).sink.Session.Seal(Terminal.NativeFailure); } catch { } return Marshal.GetHRForException(e); }
    private static nint MakeTable(int kind)
    {
        int size=kind==0?7:kind==3?15:8;
        var t=(nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(ControllerExport),size*sizeof(nint));
        t[0]=kind==0?(nint)(delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)&PrivateQuery:(nint)(delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)&PublicQuery;
        t[1]=kind==0?(nint)(delegate* unmanaged[Stdcall]<nint,uint>)&PrivateAdd:(nint)(delegate* unmanaged[Stdcall]<nint,uint>)&PublicAdd;
        t[2]=kind==0?(nint)(delegate* unmanaged[Stdcall]<nint,uint>)&PrivateRelease:(nint)(delegate* unmanaged[Stdcall]<nint,uint>)&PublicRelease;
        t[3]=(nint)(delegate* unmanaged[Stdcall]<nint,uint*,Guid**,int>)&GetIids;
        t[4]=(nint)(delegate* unmanaged[Stdcall]<nint,nint*,int>)&GetName;
        t[5]=(nint)(delegate* unmanaged[Stdcall]<nint,int*,int>)&GetTrust;
        if(kind==0) t[6]=(nint)(delegate* unmanaged[Stdcall]<nint,nint,nint,int>)&Initialize;
        if(kind==1) {
            t[6]=(nint)(delegate* unmanaged[Stdcall]<nint,ulong,int>)&Resumed;
            t[7]=(nint)(delegate* unmanaged[Stdcall]<nint,ulong,int>)&Suspended;
        }
        if(kind==2) {
            t[6]=(nint)(delegate* unmanaged[Stdcall]<nint,ulong,byte,byte,int>)&Key;
            t[7]=(nint)(delegate* unmanaged[Stdcall]<nint,ulong,int,byte,byte,uint,byte*,int>)&Message;
        }
        if(kind==3) {
            t[6]=(nint)(delegate* unmanaged[Stdcall]<nint,nint,long*,int>)&AddHeadset;
            t[7]=(nint)(delegate* unmanaged[Stdcall]<nint,long,int>)&RemoveHeadset;
            t[8]=(nint)(delegate* unmanaged[Stdcall]<nint,nint,long*,int>)&AddDisconnected;
            t[9]=(nint)(delegate* unmanaged[Stdcall]<nint,long,int>)&RemoveDisconnected;
            t[10]=(nint)(delegate* unmanaged[Stdcall]<nint,nint,long*,int>)&AddUser;
            t[11]=(nint)(delegate* unmanaged[Stdcall]<nint,long,int>)&RemoveUser;
            t[12]=(nint)(delegate* unmanaged[Stdcall]<nint,nint*,int>)&MissingObject;
            t[13]=(nint)(delegate* unmanaged[Stdcall]<nint,byte*,int>)&MissingBoolean;
            t[14]=(nint)(delegate* unmanaged[Stdcall]<nint,nint*,int>)&MissingObject;
        }
        return (nint)t;
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int PrivateQuery(nint p,Guid* iid,nint* result)
    {
        if(result==null) return Abi.PointerError; *result=0; if(iid==null) return Abi.PointerError;
        try { return Get(p).QueryInner(*iid,out *result); } catch(Exception e) { return Error(p,e); }
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int PublicQuery(nint p,Guid* iid,nint* result)
    {
        if(result==null) return Abi.PointerError; *result=0; if(iid==null) return Abi.PointerError;
        try {
            var c=Get(p); nint owner; lock(c.gate) owner=c.outer;
            if(owner==0) return c.QueryInner(*iid,out *result);
            // Never hold the inner monitor across external COM calls. The
            // caller's public reference keeps this immutable owner alive.
            return ((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)(*(nint**)owner)[0])(owner,iid,result);
        } catch(Exception e) { return Error(p,e); }
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static uint PrivateAdd(nint p)=>Get(p).AddInner();
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static uint PrivateRelease(nint p)=>Get(p).ReleaseInner();
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static uint PublicAdd(nint p)=>Get(p).AddPublic();
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static uint PublicRelease(nint p)=>Get(p).ReleasePublic();
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int Initialize(nint p,nint o,nint provider)
    { try { return Get(p).InitializeCore(o,provider); } catch(Exception e) { return Error(p,e); } }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int GetIids(nint p,uint* count,Guid** ids)
    {
        if(count!=null) *count=0; if(ids!=null) *ids=null; if(count==null || ids==null) return Abi.PointerError;
        try {
            Guid[] values=[Abi.InputSinkIid,Abi.GipSinkIid,Abi.GameControllerIid];
            var data=(Guid*)Marshal.AllocCoTaskMem(values.Length*sizeof(Guid));
            values.AsSpan().CopyTo(new Span<Guid>(data,values.Length)); *ids=data; *count=(uint)values.Length; return 0;
        } catch(Exception e) { return Error(p,e); }
    }
    [DllImport("combase.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern int WindowsCreateString(string v,uint n,out nint h);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int GetName(nint p,nint* name)
    { if(name==null) return Abi.PointerError; *name=0; try { const string v="Research.ProCalReadHost.Controller"; return WindowsCreateString(v,(uint)v.Length,out *name); } catch(Exception e) { return Error(p,e); } }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int GetTrust(nint p,int* trust)
    { if(trust==null) return Abi.PointerError; *trust=0; return 0; }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int Resumed(nint p,ulong t)
    { try { return Get(p).sink.InputState(true,t); } catch(Exception e) { return Error(p,e); } }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int Suspended(nint p,ulong t)
    { try { return Get(p).sink.InputState(false,t); } catch(Exception e) { return Error(p,e); } }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int Key(nint p,ulong t,byte key,byte pressed)
    { try { return Get(p).sink.Key(t,key,pressed); } catch(Exception e) { return Error(p,e); } }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int Message(nint p,ulong t,int cls,byte id,byte seq,uint n,byte* data)
    { try { return Get(p).sink.Receive(t,cls,id,seq,n,(nint)data); } catch(Exception e) { return Error(p,e); } }
    private static int AddEvent(nint p,int kind,nint handler,long* token)
    { if(token==null) return Abi.PointerError; *token=0; try { return Get(p).sink.Controller.Add(kind,handler,out *token); } catch(Exception e) { return Error(p,e); } }
    private static int RemoveEvent(nint p,int kind,long token)
    { try { return Get(p).sink.Controller.Remove(kind,token); } catch(Exception e) { return Error(p,e); } }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int AddHeadset(nint p,nint h,long* t)=>AddEvent(p,0,h,t);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int RemoveHeadset(nint p,long t)=>RemoveEvent(p,0,t);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int AddDisconnected(nint p,nint h,long* t)=>AddEvent(p,1,h,t);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int RemoveDisconnected(nint p,long t)=>RemoveEvent(p,1,t);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int AddUser(nint p,nint h,long* t)=>AddEvent(p,2,h,t);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int RemoveUser(nint p,long t)=>RemoveEvent(p,2,t);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int MissingObject(nint p,nint* v)
    { if(v==null) return Abi.PointerError; *v=0; return Abi.NotImplemented; }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static int MissingBoolean(nint p,byte* v)
    { if(v==null) return Abi.PointerError; *v=0; return Abi.NotImplemented; }
}
