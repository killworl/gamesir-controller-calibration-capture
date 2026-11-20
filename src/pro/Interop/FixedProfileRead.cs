namespace ProCalReadHost.Interop;

// Only Profile1, offset 0, count 55. No caller-supplied wire parameters.
// Stock-route evidence: g7pro-calibration-readback-audit-v1; captured 04/05 family.
internal static unsafe class FixedProfileRead
{
    public static bool IsReply(int messageClass,byte messageId,ReadOnlySpan<byte> body)=>
        messageClass==0 && messageId==0x10 && body.Length==60 &&
        body[..5].SequenceEqual(new byte[]{0x05,0x01,0x00,0x00,0x37});

    public static void Dispatch(OwnedComReference generic,CaptureSession session,NativeAudit audit)
    {
        var before=Abi.ReadIdentity(generic.Pointer,audit.Getter);
        if(!before.CompleteAndConnected || before.VendorId.Value!=0x3537 || before.ProductId.Value!=0x1003 ||
           before.Firmware.Value!=new VersionInfo(1,2,561,2)) {session.Seal(Terminal.IdentityIncomplete);return;}
        using var gip=audit.Value("QueryInterface.GipProvider","dbcf1e19-1af5-45a8-bf02-a0ee50c823fc",
            ()=>generic.Query(new Guid("DBCF1E19-1AF5-45A8-BF02-A0EE50C823FC")));
        var after=Abi.ReadIdentity(generic.Pointer,audit.Getter);
        if(gip.CanonicalIdentity!=generic.CanonicalIdentity || before!=after)
        {session.Seal(Terminal.IdentityIncomplete);return;}
        byte[] body=[0x04,0x01,0x00,0x00,0x37];
        if(!session.TryBeginProfile1Read() || session.Terminal!=null) return;
        int hr=audit.Call("ProfileRead.FixedGipDispatch",Convert.ToHexString(body),()=>{
            fixed(byte* bytes=body)
                return ((delegate* unmanaged[Stdcall]<nint,int,byte,uint,byte*,int>)(*(nint**)gip.Pointer)[6])
                    (gip.Pointer,0,0x0F,5,bytes);
        });
        session.RecordReadResult(hr);
    }
}
