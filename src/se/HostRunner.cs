using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SeReceiveHost.Interop;
namespace SeReceiveHost;
internal interface IObservationWindow { bool WaitNext(CancellationToken stop); }
internal sealed class ObservationWindow(int seconds) : IObservationWindow
{
    private readonly Stopwatch elapsed=Stopwatch.StartNew();
    public bool WaitNext(CancellationToken stop) { if(stop.IsCancellationRequested || elapsed.Elapsed.TotalSeconds>=seconds) return false; stop.WaitHandle.WaitOne(10); return !stop.IsCancellationRequested && elapsed.Elapsed.TotalSeconds<seconds; }
}
internal sealed class ReadObservationWindow : IObservationWindow
{
    private readonly Func<double> now;
    private readonly int responseSeconds;
    private double deadline=30;
    private bool started;
    public ReadObservationWindow(int responseSeconds,Func<double>? elapsedSeconds=null)
    {
        this.responseSeconds=responseSeconds;
        var elapsed=Stopwatch.StartNew();
        now=elapsedSeconds??(()=>elapsed.Elapsed.TotalSeconds);
    }
    public bool BeginRead()
    {
        if(started || now()>=deadline) return false;
        started=true; deadline=now()+responseSeconds; return true;
    }
    public bool WaitNext(CancellationToken stop)
    {
        if(stop.IsCancellationRequested || now()>=deadline) return false;
        stop.WaitHandle.WaitOne(10);
        return !stop.IsCancellationRequested && now()<deadline;
    }
}
internal sealed class RegistrationLifetime(Factory factory,OwnedComReference exported,OwnedComReference manager) : IDisposable
{
    public void Dispose() { factory.Dispose(); exported.Dispose(); manager.Dispose(); }
}
internal static class HostRunner
{
    public static int Run(Options options,IHostRuntime runtime,IReceiptStorage storage,CancellationToken stop,IObservationWindow? window=null,Func<IReceiveWindow>? receiveWindowFactory=null)
    {
        try { Validate(options); using var recorder=RunRecorder.CreateNew(options.Output!,storage); return RunPrepared(options,runtime,recorder,stop,window,receiveWindowFactory); }
        catch(Exception e) { Console.Error.WriteLine($"Incomplete run: {e.Message}"); return 3; }
        finally { runtime.Dispose(); }
    }
    private static void Validate(Options options)
    { if(options.Mode is not (Mode.Observe or Mode.ActivationOnly or Mode.CapabilityOnly or Mode.ReadOnce) || options.Seconds is <1 or >60 || string.IsNullOrWhiteSpace(options.Output) || (options.Mode==Mode.ReadOnce && options.Side is not (ReadSide.Left or ReadSide.Right)) || (options.Foreground && options.Mode is not (Mode.Observe or Mode.ReadOnce))) throw new ArgumentException("Invalid native mode options"); }
    internal static int RunPrepared(Options options,IHostRuntime runtime,RunRecorder recorder,CancellationToken stop,IObservationWindow? window=null,Func<IReceiveWindow>? receiveWindowFactory=null)
    {
        Validate(options);
        using var session=new CaptureSession(new(Guid.NewGuid().ToString("N"),runtime.EvidenceSource,Limits.QueueCapacity,new HostClock(),options.Mode==Mode.ReadOnce));
        var audit=new NativeAudit(); OwnedComReference? manager=null,exported=null; Factory? factory=null;
        IReceiveWindow? receiveWindow=null; WindowStatus? previousWindow=null;
        bool PumpWindow()
        {
            if(receiveWindow==null) return true;
            var state=receiveWindow.Pump();
            if(previousWindow!=state) session.Lifecycle("window_state",detail:JsonSerializer.Serialize(new {state.Open,state.Foreground,state.ForegroundProcessId,HostProcessId=Environment.ProcessId},RunRecorder.Json));
            bool lost=previousWindow?.Foreground==true && !state.Foreground;
            previousWindow=state;
            if(!state.Open || lost) { session.Lifecycle(lost?"foreground_lost":"window_closed"); session.Seal(Terminal.Interrupted); return false; }
            return true;
        }
        using var cancellation=stop.Register(()=>session.Seal(Terminal.Interrupted));
        try
        {
            if(stop.IsCancellationRequested) session.Seal(Terminal.Interrupted);
            else
            {
                if(options.Foreground) receiveWindow=(receiveWindowFactory??(()=>new ForegroundWindow()))();
                PumpWindow();
            }
            if(session.Terminal==null)
            {
                int hr=audit.Call("RoInitialize","MTA",runtime.Initialize); if(hr<0) throw new COMException("Runtime initialization failed",hr);
                if(!stop.IsCancellationRequested) manager=audit.Value("RoGetActivationFactory","OS GameControllerFactoryManager",runtime.ActivateManager);
                if(session.Terminal==null && options.Mode==Mode.ActivationOnly) session.Seal(Terminal.ActivationOnlyComplete);
                else if(session.Terminal==null)
                {
                    var retainedManager=manager!;
                    OwnedComReference? factoryInterface=null;
                    factory=new Factory(session,controller=>
                    {
                        using var resolved=audit.Value("TryGetFactoryControllerFromGameController","same registered factory",()=>runtime.ResolveController(retainedManager.Pointer,factoryInterface!.Pointer,controller));
                        return resolved?.CanonicalIdentity;
                    },audit);
                    using(var unknown=ComExports.Export(factory)) factoryInterface=unknown.Query(Abi.FactoryIid);
                    exported=factoryInterface;
                    runtime.KeepUntilExit(new RegistrationLifetime(factory,exported,retainedManager));
                    manager=null; exported=null; // ownership transferred before a possibly partial registration
                    hr=audit.Call("RegisterCustomFactoryForHardwareId","3537:1071",()=>runtime.RegisterHardware(retainedManager.Pointer,factoryInterface.Pointer,0x3537,0x1071));
                    if(hr<0) session.Seal(Terminal.NativeFailure);
                    IObservationWindow clock=window??(options.Mode==Mode.ReadOnce && options.Foreground ? new ReadObservationWindow(options.Seconds) : new ObservationWindow(options.Seconds));
                    bool readAttempted=false,activationRequested=false;
                    while(session.Terminal==null)
                    {
                        if(!PumpWindow()) break;
                        if(receiveWindow!=null && !activationRequested && factory.InitializedAndAdded)
                        {
                            activationRequested=true;
                            session.Lifecycle("window_activation_requested");
                            receiveWindow.Activate();
                            if(!PumpWindow()) break;
                        }
                        Drain(session,recorder);
                        if(options.Mode==Mode.CapabilityOnly && factory.HasCandidate) { factory.ProbeCapability(audit); break; }
                        if(options.Mode==Mode.ReadOnce && factory.ReceiveLifecycleReady && !readAttempted && (receiveWindow==null || previousWindow?.Foreground==true))
                        { if(clock is ReadObservationWindow phased && !phased.BeginRead()) break; readAttempted=true; factory.ReadOnce(audit,options.Side!.Value,foregroundEpoch:receiveWindow!=null); }
                        if(readAttempted && (!PumpWindow() || session.CompleteReadIfReady())) break;
                        if(!clock.WaitNext(stop)) break;
                    }
                    if(stop.IsCancellationRequested) session.Seal(Terminal.Interrupted);
                    if(session.Terminal==null)
                    {
                        if(options.Mode==Mode.ReadOnce) { if(readAttempted) session.CompleteRead(); else session.Seal(factory.HasCandidate?Terminal.BindingNotReady:Terminal.NoMatch); }
                        else {
                            factory.RefreshIdentity();
                            if(options.Mode==Mode.CapabilityOnly && factory.HasCandidate) factory.ProbeCapability(audit);
                            else session.Seal(factory.HasCandidate?Terminal.UnqualifiedReceiveCapture:Terminal.NoMatch);
                        }
                    }
                    factory=null; // retained by runtime through its registration lifetime
                }
            }
        }
        catch(AuditLimitException) { session.Seal(Terminal.Overflow); }
        catch(ReceiptLimitException) { session.Seal(Terminal.Overflow); throw; }
        catch(IOException) { session.Seal(Terminal.IoFailure); throw; }
        catch(Exception e) { session.Lifecycle("native_error",detail:$"HRESULT={e.HResult}: {e.GetType().Name}"); session.Seal(Terminal.NativeFailure); }
        finally
        {
            // A transferred factory must NOT be disposed here, including failed registration.
            if(manager!=null) factory?.Dispose();
            exported?.Dispose(); manager?.Dispose();
            receiveWindow?.Dispose();
        }
        Drain(session,recorder); recorder.Complete(session.Snapshot(audit.Snapshot()));
        return session.Terminal is Terminal.ActivationOnlyComplete or Terminal.UnqualifiedReceiveCapture or Terminal.CapabilityOnlyComplete or Terminal.ReadReplyObservedUnqualified ? 0 : 1;
    }
    internal static void Drain(CaptureSession session,RunRecorder recorder) { while(session.TryDequeue(out var e)) recorder.Append(e!); }
}
