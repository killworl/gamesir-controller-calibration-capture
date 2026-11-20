using System.Runtime.InteropServices;
namespace ProCalReadHost.Interop;
internal sealed record GipCapabilityResult(string InterfaceId,int HResult,bool Supported,bool? SameIdentity);
// Deliberately owns no provider-method wrapper. Only QI and reference bookkeeping.
internal static unsafe class GipCapabilityProbe
{
    private static readonly Guid InterfaceId=new("DBCF1E19-1AF5-45A8-BF02-A0EE50C823FC");
    public static GipCapabilityResult Check(OwnedComReference provider,NativeAudit audit)
    {
        nint owned=0;
        try
        {
            int hr=audit.Call("QueryInterface.GipProvider",InterfaceId.ToString(),()=>
            {
                nint value=0; Guid iid=InterfaceId; nint p=provider.Pointer;
                int result=((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)(*(nint**)p)[0])(p,&iid,&value);
                owned=value; return result;
            });
            if(hr==Abi.NoInterface) return new(InterfaceId.ToString(),hr,false,null);
            if(hr<0) throw new COMException("GIP interface query failed",hr);
            if(owned==0) throw new COMException("GIP interface query returned null",Abi.PointerError);
            nint transfer=owned; owned=0;
            using var gip=OwnedComReference.Adopt(transfer);
            return new(InterfaceId.ToString(),hr,true,gip.CanonicalIdentity==provider.CanonicalIdentity);
        }
        finally { if(owned!=0) Abi.Release(owned); }
    }
}
