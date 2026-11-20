using System.Runtime.InteropServices;
namespace SeReceiveHost;
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct VersionInfo(ushort Major, ushort Minor, ushort Build, ushort Revision);
internal sealed record GetterResult<T>(int HResult, T? Value) where T : struct;
internal sealed record ProviderMetadata(GetterResult<ushort> VendorId, GetterResult<ushort> ProductId,
    GetterResult<VersionInfo> Firmware, GetterResult<VersionInfo> Hardware, GetterResult<byte> Connected)
{
    public bool AllSucceeded => VendorId.HResult >= 0 && VendorId.Value.HasValue && ProductId.HResult >= 0 && ProductId.Value.HasValue &&
        Firmware.HResult >= 0 && Firmware.Value.HasValue && Hardware.HResult >= 0 && Hardware.Value.HasValue && Connected.HResult >= 0 && Connected.Value.HasValue && Connected.Value <= 1;
    public bool CompleteAndConnected => AllSucceeded && Connected.Value == 1;
}
internal sealed record CandidateDecision(bool Admitted,string? CandidateId,string Classification);
internal sealed class CandidateRegistry(CaptureSession session)
{
    private readonly object gate=new();
    private nint identity;
    private ProviderMetadata? metadata;
    public CaptureSession Session { get; }=session;
    public CandidateDecision Admit(nint canonicalIdentity,ProviderMetadata data)
    {
        lock(gate)
        {
            if(Session.Terminal!=null) return new(false,null,"session_closed");
            Session.Lifecycle("candidate_metadata",identity==0?null:"candidate-1",detail:System.Text.Json.JsonSerializer.Serialize(data,RunRecorder.Json));
            Terminal? reject = canonicalIdentity==0 || !data.AllSucceeded ? Terminal.IdentityIncomplete :
                data.VendorId.Value!=0x3537 || data.ProductId.Value!=0x1071 || (identity!=0 && identity!=canonicalIdentity) ? Terminal.AmbiguousIdentity :
                data.Connected.Value!=1 ? Terminal.Disconnected :
                identity!=0 && metadata!=data ? Terminal.IdentityIncomplete : null;
            if(reject!=null) { Session.Seal(reject.Value); return new(false,null,reject.ToString()!); }
            identity=canonicalIdentity; metadata=data;
            Session.BindCandidate("candidate-1");
            return new(Session.Terminal is null,"candidate-1","unqualified_candidate");
        }
    }
    public void Remove(string id)
    {
        lock(gate)
        {
            if(Session.Terminal!=null) { Session.Lifecycle("late_remove",id); return; }
            if(identity==0 || id!="candidate-1") { Session.Seal(Terminal.IdentityIncomplete); return; }
            Session.Lifecycle("candidate_removed",id); Session.Seal(Terminal.Disconnected);
        }
    }
}
