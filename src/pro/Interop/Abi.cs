using System.Runtime.InteropServices;
namespace ProCalReadHost.Interop;
internal static unsafe class Abi
{
    public const int InvalidArg=unchecked((int)0x80070057), PointerError=unchecked((int)0x80004003), NoInterface=unchecked((int)0x80004002),
        NotImplemented=unchecked((int)0x80004001), Closed=unchecked((int)0x80000013), Failure=unchecked((int)0x80004005);
    public static readonly Guid UnknownIid=new("00000000-0000-0000-C000-000000000046"), InspectableIid=new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90"),
        FactoryIid=new("69A0AE5E-758E-4CBE-ACE6-62155FE9126F"), ManagerIid=new("36CB66E3-D0A1-4986-A24C-40B137DEBA9E"),
        Manager2Iid=new("EACE5644-19DF-4115-B32A-2793E2AEA3BB"), GenericProviderIid=new("E6D73982-2996-4559-B16C-3E57D46E58D6"),
        InputSinkIid=new("1FF6F922-C640-4C78-A820-9A715C558BCB"), GipSinkIid=new("A2108ABF-09F1-43BC-A140-80F899EC36FB"),
        GameControllerIid=new("1BAF6522-5F64-42C5-8267-B9FE2215BFBD");
    public static nint Query(nint p, Guid iid)
    {
        if(p==0) throw new ArgumentNullException(nameof(p));
        nint value=0;
        int hr=((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)(*(nint**)p)[0])(p,&iid,&value);
        if(hr<0) { if(value!=0) Release(value); throw new COMException("QueryInterface failed",hr); }
        if(value==0) throw new COMException("QueryInterface returned null",PointerError);
        return value;
    }
    public static uint AddRef(nint p) => ((delegate* unmanaged[Stdcall]<nint,uint>)(*(nint**)p)[1])(p);
    public static uint Release(nint p) => ((delegate* unmanaged[Stdcall]<nint,uint>)(*(nint**)p)[2])(p);
    public static ProviderMetadata ReadIdentity(nint p,Action<string,int>? report=null)
    {
        if(p==0) throw new ArgumentNullException(nameof(p));
        var t=*(nint**)p;
        VersionInfo fw=default, hw=default; ushort pid=0,vid=0; byte connected=0;
        int a=((delegate* unmanaged[Stdcall]<nint,VersionInfo*,int>)t[6])(p,&fw);
        report?.Invoke("provider.firmware",a);
        int b=((delegate* unmanaged[Stdcall]<nint,ushort*,int>)t[7])(p,&pid);
        report?.Invoke("provider.product_id",b);
        int c=((delegate* unmanaged[Stdcall]<nint,ushort*,int>)t[8])(p,&vid);
        report?.Invoke("provider.vendor_id",c);
        int d=((delegate* unmanaged[Stdcall]<nint,VersionInfo*,int>)t[9])(p,&hw);
        report?.Invoke("provider.hardware",d);
        int e=((delegate* unmanaged[Stdcall]<nint,byte*,int>)t[10])(p,&connected);
        report?.Invoke("provider.connected",e);
        return new(new(c,c<0?null:vid),new(b,b<0?null:pid),new(a,a<0?null:fw),new(d,d<0?null:hw),new(e,e<0?null:connected));
    }
}
