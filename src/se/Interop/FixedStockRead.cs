namespace SeReceiveHost.Interop;
// The only sending leaf: no caller-supplied command, address, size, ID or body.
internal static unsafe class FixedStockRead
{
    public static void Dispatch(OwnedComReference generic,CaptureSession session,NativeAudit audit,ReadSide side)
    {
        if(side is not (ReadSide.Left or ReadSide.Right)) throw new ArgumentException("Invalid side");
        var before=Abi.ReadIdentity(generic.Pointer,audit.Getter);
        if(!before.CompleteAndConnected || before.VendorId.Value!=0x3537 || before.ProductId.Value!=0x1071 ||
            before.Firmware.Value!=new VersionInfo(1,2,1636,2)) {session.Seal(Terminal.IdentityIncomplete);return;}
        using var gip=audit.Value("QueryInterface.GipProvider","dbcf1e19-1af5-45a8-bf02-a0ee50c823fc",
            ()=>generic.Query(new Guid("DBCF1E19-1AF5-45A8-BF02-A0EE50C823FC")));
        var after=Abi.ReadIdentity(generic.Pointer,audit.Getter);
        if(gip.CanonicalIdentity!=generic.CanonicalIdentity || before!=after)
        {session.Seal(Terminal.IdentityIncomplete);return;}
        byte[] body=new byte[60]; body[0]=0x14; body[1]=0x17; body[2]=(byte)(side==ReadSide.Left?0:0x20); body[3]=30;
        if(!session.TryBeginRead(side) || session.Terminal!=null) return;
        int hr=audit.Call("StockRead.FixedGipDispatch",Convert.ToHexString(body),()=>
        {
            fixed(byte* bytes=body)
                return ((delegate* unmanaged[Stdcall]<nint,int,byte,uint,byte*,int>)(*(nint**)gip.Pointer)[6])(gip.Pointer,0,0x0F,60,bytes);
        });
        session.RecordReadResult(hr);
    }
}
