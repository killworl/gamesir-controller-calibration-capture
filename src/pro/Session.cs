using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using ProCalReadHost.Interop;

namespace ProCalReadHost;
internal interface IHostClock { DateTimeOffset UtcNow { get; } long MonotonicTicks { get; } long Frequency { get; } }
internal sealed class HostClock : IHostClock { public DateTimeOffset UtcNow=>DateTimeOffset.UtcNow; public long MonotonicTicks=>Stopwatch.GetTimestamp(); public long Frequency=>Stopwatch.Frequency; }
internal sealed record SessionOptions(string SessionId,string Source,int QueueCapacity,IHostClock Clock,bool CalibrationRecordRead=false);
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
    private bool profileRead;
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
    internal void BindCandidate(string id) { lock(gate) { if(terminal!=null) return; if(candidate!=null && candidate!=id) { SealLocked(ProCalReadHost.Terminal.AmbiguousIdentity); return; } candidate=id; } }
    public unsafe int TryMessage(string id,ulong timestamp,int messageClass,byte messageId,byte sequence,uint count,nint buffer)
    {
        lock(gate)
        {
            if(terminal!=null) { late++; return Abi.Closed; }
            if(id!=candidate) { rejected++; SealLocked(ProCalReadHost.Terminal.IdentityIncomplete); return Abi.InvalidArg; }
            if(count>Limits.MaxPayload || (count!=0 && buffer==0)) { rejected++; SealLocked(ProCalReadHost.Terminal.MalformedCallback); return Abi.InvalidArg; }
            if(queue.Count>=options.QueueCapacity) { rejected++; SealLocked(ProCalReadHost.Terminal.Overflow); return Abi.Failure; }
            byte[] body=new byte[(int)count];
            if(count!=0) new ReadOnlySpan<byte>((void*)buffer,(int)count).CopyTo(body);
            // Observed Pro stream envelope (native-left-20260920-03), disjoint
            // from calibration response ID0x10/length60. Preserve raw capture;
            // exempt only this exact envelope from transaction/stale-reply gates.
            bool unrelatedStream=messageClass==1 && messageId==0 && count==32;
            // Nexus 2.5.24 interleaves E0 reports with the distinct 15 17 calibration reply.
            if(options.CalibrationRecordRead && messageClass==0 && messageId==0x10 && count==60 && body[0]==0xE0)
                unrelatedStream=true;
            if(!unrelatedStream) messageSeen=true;
            if(readBegun && !unrelatedStream)
            {
                bool valid=profileRead?FixedProfileRead.IsReply(messageClass,messageId,body):
                    messageClass is >=0 and <=2 && messageId==0x10 && body.Length==60 &&
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
            if(kind.Length>64 || detail?.Length>8192) { rejected++; SealLocked(ProCalReadHost.Terminal.MalformedCallback); return Abi.InvalidArg; }
            if(queue.Count>=options.QueueCapacity) { rejected++; SealLocked(ProCalReadHost.Terminal.Overflow); return Abi.Failure; }
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
            { SealLocked(ProCalReadHost.Terminal.ReadRejected); return false; }
            if(queue.Count>=options.QueueCapacity) { SealLocked(ProCalReadHost.Terminal.Overflow); return false; }
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
            { SealLocked(ProCalReadHost.Terminal.ReadRejected); return false; }
            if(queue.Count>=options.QueueCapacity) { SealLocked(ProCalReadHost.Terminal.Overflow); return false; }
            readBegun=true; readSide=side;
            Enqueue("stock_read_begin",candidate,null,null,null,null,null,[],System.Text.Json.JsonSerializer.Serialize(new {Model="G7Pro",FirmwareVersion="REV0231; installed-image authenticity unproven",Side=side==ReadSide.Left?"left":"right"},RunRecorder.Json));
            return true;
        }
    }
    public void RecordReadResult(int hr)
    {
        lock(gate)
        {
            readReturned=true;
            Lifecycle("stock_read_call_result",candidate,detail:System.Text.Json.JsonSerializer.Serialize(new {HResult=hr},RunRecorder.Json));
            if(hr<0) SealLocked(ProCalReadHost.Terminal.NativeFailure);
        }
    }
    public bool TryBeginProfile1Read()
    {
        lock(gate)
        {
            if(terminal!=null) return false;
            if(candidate==null || !foregroundEpochOpened || readBegun || messageSeen || suspensionGeneration!=allowedStartupSuspensions)
            { SealLocked(ProCalReadHost.Terminal.ReadRejected); return false; }
            if(queue.Count>=options.QueueCapacity) { SealLocked(ProCalReadHost.Terminal.Overflow); return false; }
            readBegun=true; profileRead=true;
            Enqueue("stock_read_begin",candidate,null,null,null,null,null,[],System.Text.Json.JsonSerializer.Serialize(new {
                Model="G7Pro",FirmwareVersion="REV0231; installed-image authenticity unproven",
                Query="profile1_chunk0",MessageClass=0,MessageId=15,RequestHex="0401000037",ResponseLength=60,
                Profile=1,Offset=0,Count=55,Interpretation="opaque profile bytes; RC unresolved"},RunRecorder.Json));
            return true;
        }
    }
    // Stop at the first reply or invalidating event, after the send HRESULT is
    // known. Never let an early synchronous callback mask a failed send.
    public bool CompleteTransactionIfReady()
    {
        lock(gate)
        {
            if(terminal!=null) return true;
            if(!readReturned || (!readInvalid && readReplies==0)) return false;
            CompleteRead(); return true;
        }
    }
    public void CompleteRead()
    {
        lock(gate)
        {
            if(terminal!=null) return;
            SealLocked(!readBegun || !readReturned || readInvalid ? ProCalReadHost.Terminal.ReadRejected :
                readReplies==1?ProCalReadHost.Terminal.ReadReplyObservedUnqualified:ProCalReadHost.Terminal.ReadTimeout);
        }
    }
    private int Enqueue(string kind,string? id,ulong? timestamp,int? cls,byte? msg,byte? seq,uint? count,byte[] payload,string? detail)
    {
        queue.Enqueue(new("pro_cal_read_event_v1",options.SessionId,options.Source,(++ordinal).ToString(CultureInfo.InvariantCulture),options.Clock.UtcNow.ToString("O"),
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
    public void Dispose()=>Seal(ProCalReadHost.Terminal.Interrupted);
}
