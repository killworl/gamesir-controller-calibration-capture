using System.Text.Json;
using ProCalReadHost.Interop;
namespace ProCalReadHost;
internal interface ICallbackOwner { CaptureSession Session { get; } }
internal sealed class InputSink(CaptureSession session,string candidateId) : ICallbackOwner,IDisposable
{
    private readonly object bindingGate=new();
    private nint outerIdentity;
    private bool added,resumed;
    public CaptureSession Session { get; }=session;
    public string CandidateId { get; }=candidateId;
    public GameControllerSurface Controller { get; }=new(session);
    public int Receive(ulong time,int cls,byte id,byte seq,uint count,nint data)=>Session.TryMessage(CandidateId,time,cls,id,seq,count,data);
    public nint OuterIdentity { get { lock(bindingGate) return outerIdentity; } }
    public bool InitializedAndAdded { get { lock(bindingGate) return outerIdentity!=0 && added && Session.Terminal==null; } }
    public bool ReceiveLifecycleReady { get { lock(bindingGate) return outerIdentity!=0 && added && resumed && Session.Terminal==null; } }
    public bool OpenForegroundReadEpoch()
    {
        lock(bindingGate)
        {
            if(outerIdentity==0 || !added || !resumed || Session.Terminal!=null)
            { Session.Seal(Terminal.BindingNotReady); return false; }
            return Session.OpenForegroundReadEpoch();
        }
    }
    public int Initialized(nint outer)
    { lock(bindingGate) { outerIdentity=outer; added=false; resumed=false; return Session.Lifecycle("binding_initialized",CandidateId); } }
    public int MarkAdded() { lock(bindingGate) { added=true; return Session.Lifecycle("candidate_added",CandidateId); } }
    public int InputState(bool value,ulong time)
    { lock(bindingGate) { resumed=value; return Session.Lifecycle(value?"input_resumed":"input_suspended",CandidateId,time); } }
    public int Key(ulong time,byte key,byte pressed)
    {
        if(pressed>1) { Session.Seal(Terminal.MalformedCallback); return Abi.InvalidArg; }
        return Session.Lifecycle("key",CandidateId,time,JsonSerializer.Serialize(new {KeyCode=key,IsPressedRaw=pressed},RunRecorder.Json));
    }
    public void Dispose()=>Controller.Dispose();
}
