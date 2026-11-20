using SeReceiveHost.Interop;
namespace SeReceiveHost;
internal sealed class Factory(CaptureSession session,Func<nint,nint?>? resolver=null,NativeAudit? audit=null) : ICallbackOwner,IDisposable
{
    private readonly object gate=new();
    private readonly CandidateRegistry registry=new(session);
    private OwnedComReference? provider, exportedSink;
    private InputSink? sink;
    private bool disposed,capabilityAttempted;
    public CaptureSession Session { get; }=session;
    public bool HasCandidate { get { lock(gate) return exportedSink!=null; } }
    public bool InitializedAndAdded { get { lock(gate) return !disposed && sink?.InitializedAndAdded==true; } }
    public bool ReceiveLifecycleReady { get { lock(gate) return !disposed && sink?.ReceiveLifecycleReady==true; } }
    public nint Create(nint borrowedProvider)
    {
        if(Session.Terminal!=null) { Session.Lifecycle("late_create"); throw new System.Runtime.InteropServices.COMException("Closed session",Abi.Closed); }
        OwnedComReference? incoming=OwnedComReference.Query(borrowedProvider,Abi.GenericProviderIid);
        try
        {
            var metadata=Abi.ReadIdentity(incoming.Pointer,audit is null?null:audit.Getter);
            lock(gate)
            {
                if(disposed || Session.Terminal!=null) throw new System.Runtime.InteropServices.COMException("Closed session",Abi.Closed);
                var decision=registry.Admit(incoming.CanonicalIdentity,metadata);
                if(!decision.Admitted) throw new System.Runtime.InteropServices.COMException("Candidate rejected",Abi.Failure);
                if(exportedSink==null)
                {
                    sink=new InputSink(Session,decision.CandidateId!);
                    exportedSink=ControllerExport.Export(sink,incoming.CanonicalIdentity);
                    provider=incoming; incoming=null;
                }
                return Abi.Query(exportedSink.Pointer,Abi.InspectableIid); // caller owns returned reference
            }
        }
        finally { incoming?.Dispose(); }
    }
    public int Lifecycle(nint controller,bool added)
    {
        if(Session.Terminal!=null) return Session.Lifecycle(added?"late_added":"late_removed");
        using var incoming=OwnedComReference.Query(controller,Abi.UnknownIid);
        nint expected;
        lock(gate) expected=sink?.OuterIdentity is { } outer && outer!=0?outer:exportedSink?.CanonicalIdentity??0;
        nint actual=incoming.CanonicalIdentity;
        if(actual!=expected) actual=resolver?.Invoke(controller)??0;
        if(actual==0 || actual!=expected) { Session.Seal(Terminal.IdentityIncomplete); return Abi.Failure; }
        if(added) { RefreshIdentity(); return sink?.MarkAdded()??Abi.Closed; }
        registry.Remove("candidate-1"); return 0;
    }
    public void RefreshIdentity()
    {
        OwnedComReference? current;
        lock(gate) { if(disposed || provider==null || Session.Terminal!=null) return; current=provider.Query(Abi.GenericProviderIid); }
        using(current) registry.Admit(current.CanonicalIdentity,Abi.ReadIdentity(current.Pointer,audit is null?null:audit.Getter));
    }
    public void Dispose()
    {
        OwnedComReference? oldProvider,oldSink; InputSink? oldTarget;
        lock(gate)
        {
            if(disposed) return; disposed=true; Session.Seal(Terminal.Interrupted);
            oldProvider=provider; provider=null; oldSink=exportedSink; exportedSink=null; oldTarget=sink; sink=null;
        }
        oldTarget?.Dispose(); oldSink?.Dispose(); oldProvider?.Dispose();
    }
    public void ProbeCapability(NativeAudit journal)
    {
        OwnedComReference current;
        lock(gate)
        {
            if(disposed || Session.Terminal!=null) return;
            if(provider==null || capabilityAttempted) { Session.Seal(Terminal.IdentityIncomplete); return; }
            capabilityAttempted=true;
            current=provider.Query(Abi.GenericProviderIid);
        }
        using(current)
        {
            RefreshIdentity();
            if(Session.Terminal!=null) return;
            var result=GipCapabilityProbe.Check(current,journal);
            // A disconnect/cancel during QI must not become a completed preflight.
            RefreshIdentity();
            if(Session.Terminal!=null) return;
            Session.Lifecycle("gip_capability","candidate-1",detail:System.Text.Json.JsonSerializer.Serialize(result,RunRecorder.Json));
            Session.Seal(!result.Supported?Terminal.CapabilityUnavailable:
                result.SameIdentity==true?Terminal.CapabilityOnlyComplete:Terminal.IdentityIncomplete);
        }
    }
    public void ReadOnce(NativeAudit journal,ReadSide side,bool foregroundEpoch=false)
    {
        OwnedComReference current;
        lock(gate)
        {
            if(disposed || Session.Terminal!=null) return;
            if(provider==null) {Session.Seal(Terminal.IdentityIncomplete);return;}
            if(sink?.ReceiveLifecycleReady!=true) {Session.Seal(Terminal.BindingNotReady);return;}
            if(foregroundEpoch && !sink.OpenForegroundReadEpoch()) return;
            current=provider.Query(Abi.GenericProviderIid);
        }
        using(current) FixedStockRead.Dispatch(current,Session,journal,side);
    }
}
