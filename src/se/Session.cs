using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using SeReceiveHost.Interop;

namespace SeReceiveHost;
internal interface IHostClock { DateTimeOffset UtcNow { get; } long MonotonicTicks { get; } long Frequency { get; } }
internal sealed class HostClock : IHostClock { public DateTimeOffset UtcNow=>DateTimeOffset.UtcNow; public long MonotonicTicks=>Stopwatch.GetTimestamp(); public long Frequency=>Stopwatch.Frequency; }
internal sealed record SessionOptions(string SessionId,string Source,int QueueCapacity,IHostClock Clock,bool CalibrationRead=false);
internal sealed record EventEnvelope(string Schema,string SessionId,string Source,string HostOrdinal,string HostUtc,
    string HostMonotonicTicks,string HostMonotonicFrequency,string Kind,string? CandidateId,string? ProviderTimestampRaw,
    int? MessageClass,byte? MessageId,byte? Sequence,uint? DeclaredLength,string PayloadHex,string PayloadSha256,string? DetailJson);
internal sealed record NativeAttempt(string Api,int? HResult,string HostUtc,string Arguments);
internal sealed record SessionSummary(string SessionId,string Source,Terminal? Terminal,long AdmittedEvents,long RejectedEvents,
    long LateCallbacks,IReadOnlyList<NativeAttempt> NativeAudit);
internal sealed class CaptureSession : IDisposable
{
    private readonly object gate=new();
    private readonly Queue<EventEnvelope> queue=[];
    private readonly SessionOptions options;
    private string? candidate;
    private Terminal? terminal;
    private long ordinal,rejected,late;
    private bool messageSeen,foregroundEpochOpened,readBegun,readInvalid,readReturned;
    private long suspensionGeneration,allowedStartupSuspensions;
    private ReadSide readSide;
    private int readReplies;
    public CaptureSession(SessionOptions options)
    {
        if(options.QueueCapacity is <1 or >Limits.QueueCapacity || string.IsNullOrWhiteSpace(options.SessionId) || options.SessionId.Length>128 || options.Clock.Frequency<=0 ||
            options.Source is not ("fixture" or "windows_callback" or "replay")) throw new ArgumentException("Invalid session options");
        this.options=options;
    }
    public Terminal? Terminal { get { lock(gate) return terminal; } }
    public long LateCallbacks { get { lock(gate) return late; } }
    public long RejectedEvents { get { lock(gate) return rejected; } }
    public int QueuedCount { get { lock(gate) return queue.Count; } }
    internal void BindCandidate(string id) { lock(gate) { if(terminal!=null) return; if(candidate!=null && candidate!=id) { SealLocked(SeReceiveHost.Terminal.AmbiguousIdentity); return; } candidate=id; } }
    public unsafe int TryMessage(string id,ulong timestamp,int messageClass,byte messageId,byte sequence,uint count,nint buffer)
    {
        lock(gate)
        {
            if(terminal!=null) { late++; return Abi.Closed; }
            if(id!=candidate) { rejected++; SealLocked(SeReceiveHost.Terminal.IdentityIncomplete); return Abi.InvalidArg; }
            if(count>Limits.MaxPayload || (count!=0 && buffer==0)) { rejected++; SealLocked(SeReceiveHost.Terminal.MalformedCallback); return Abi.InvalidArg; }
            if(queue.Count>=options.QueueCapacity) { rejected++; SealLocked(SeReceiveHost.Terminal.Overflow); return Abi.Failure; }
            byte[] body=new byte[(int)count];
            if(count!=0) new ReadOnlySpan<byte>((void*)buffer,(int)count).CopyTo(body);
            // Nexus interleaves 60-byte E0 status reports with the 15 17 calibration reply.
            // Retain every event in the log while filtering only this specific envelope.
            bool nexusStatus=options.CalibrationRead && messageClass==0 && messageId==0x10 && count==60 && body[0]==0xE0;
            if(!nexusStatus) messageSeen=true;
            if(readBegun && !nexusStatus)
            {
                bool valid=messageClass is >=0 and <=2 && messageId==0x10 && body.Length==60 &&
                    body[0]==0x15 && body[1]==0x17 && body[2]==(readSide==ReadSide.Left?0:0x20) && body[3]==30 &&
                    body.AsSpan(34).IndexOfAnyExcept((byte)0)<0;
                if(!valid || ++readReplies>1) readInvalid=true;
            }
            return Enqueue("message",id,timestamp,messageClass,messageId,sequence,count,body,null);
        }
    }
    public int Lifecycle(string kind,string? id=null,ulong? timestamp=null,string? detail=null)
    {
        lock(gate)
        {
            if(terminal!=null) { late++; return Abi.Closed; }
            if(kind.Length>64 || detail?.Length>8192) { rejected++; SealLocked(SeReceiveHost.Terminal.MalformedCallback); return Abi.InvalidArg; }
            if(queue.Count>=options.QueueCapacity) { rejected++; SealLocked(SeReceiveHost.Terminal.Overflow); return Abi.Failure; }
            if(kind=="input_suspended") suspensionGeneration++;
            if(readBegun && kind!="stock_read_call_result") readInvalid=true;
            return Enqueue(kind,id,timestamp,null,null,null,null,[],detail);
        }
    }
    // Called under InputSink's binding gate after Added and current Resumed.
    // Startup history stays in the journal; payloads and later suspensions never reset.
    public bool OpenForegroundReadEpoch()
    {
        lock(gate)
        {
            if(terminal!=null) return false;
            if(candidate==null || foregroundEpochOpened || readBegun || messageSeen)
            { SealLocked(SeReceiveHost.Terminal.ReadRejected); return false; }
            if(queue.Count>=options.QueueCapacity) { SealLocked(SeReceiveHost.Terminal.Overflow); return false; }
            foregroundEpochOpened=true; allowedStartupSuspensions=suspensionGeneration;
            Enqueue("stock_read_epoch",candidate,null,null,null,null,null,[],System.Text.Json.JsonSerializer.Serialize(new {
                Scope="foreground_activation_complete",StartupSuspensions=allowedStartupSuspensions},RunRecorder.Json));
            return true;
        }
    }
    public bool TryBeginRead(ReadSide side)
    {
        lock(gate)
        {
            if(terminal!=null) return false;
            if(side is not (ReadSide.Left or ReadSide.Right) || candidate==null || readBegun || messageSeen || suspensionGeneration!=allowedStartupSuspensions)
            { SealLocked(SeReceiveHost.Terminal.ReadRejected); return false; }
            if(queue.Count>=options.QueueCapacity) { SealLocked(SeReceiveHost.Terminal.Overflow); return false; }
            readBegun=true; readSide=side;
            Enqueue("stock_read_begin",candidate,null,null,null,null,null,[],System.Text.Json.JsonSerializer.Serialize(new {Model="G7SE",FirmwareVersion="6.6.4",Side=side==ReadSide.Left?"left":"right"},RunRecorder.Json));
            return true;
        }
    }
    public void RecordReadResult(int hr)
    {
        lock(gate)
        {
            readReturned=true;
            Lifecycle("stock_read_call_result",candidate,detail:System.Text.Json.JsonSerializer.Serialize(new {HResult=hr},RunRecorder.Json));
            if(hr<0) SealLocked(SeReceiveHost.Terminal.NativeFailure);
        }
    }
    public bool CompleteReadIfReady()
    {
        lock(gate)
        {
            if(terminal!=null) return true;
            if(!readReturned || (!readInvalid && readReplies==0)) return false;
            CompleteRead();
            return true;
        }
    }
    public void CompleteRead()
    {
        lock(gate)
        {
            if(terminal!=null) return;
            SealLocked(!readBegun || !readReturned || readInvalid ? SeReceiveHost.Terminal.ReadRejected :
                readReplies==1?SeReceiveHost.Terminal.ReadReplyObservedUnqualified:SeReceiveHost.Terminal.ReadTimeout);
        }
    }
    private int Enqueue(string kind,string? id,ulong? timestamp,int? cls,byte? msg,byte? seq,uint? count,byte[] payload,string? detail)
    {
        queue.Enqueue(new("se_receive_event_v1",options.SessionId,options.Source,(++ordinal).ToString(CultureInfo.InvariantCulture),options.Clock.UtcNow.ToString("O"),
            options.Clock.MonotonicTicks.ToString(CultureInfo.InvariantCulture),options.Clock.Frequency.ToString(CultureInfo.InvariantCulture),kind,id,
            timestamp?.ToString(CultureInfo.InvariantCulture),cls,msg,seq,count,Convert.ToHexString(payload),Convert.ToHexString(SHA256.HashData(payload)),detail));
        return 0;
    }
    private void SealLocked(Terminal reason)
    {
        if(terminal!=null) return;
        terminal=reason;
        if(queue.Count<options.QueueCapacity) Enqueue("stop",candidate,null,null,null,null,null,[],reason.ToString());
    }
    public void Seal(Terminal reason) { lock(gate) SealLocked(reason); }
    public bool TryDequeue(out EventEnvelope? value) { lock(gate) return queue.TryDequeue(out value); }
    public SessionSummary Snapshot(IReadOnlyList<NativeAttempt>? audit=null)
    { lock(gate) return new(options.SessionId,options.Source,terminal,ordinal,rejected,late,audit?.ToArray()??[]); }
    public void Dispose()=>Seal(SeReceiveHost.Terminal.Interrupted);
}
