using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace ProCalReadHost;
internal static class OfflineReplay
{
    public static int Run(string input,RunRecorder recorder,CancellationToken stop)
    {
        using var file=new FileStream(input,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length>Limits.OutputBytes) throw new InvalidDataException("Replay input exceeds 16 MiB");
        using var session=new CaptureSession(new(Guid.NewGuid().ToString("N"),"replay",Limits.QueueCapacity,new HostClock()));
        session.BindCandidate("candidate-1"); long expected=1,total=0;
        string? originalSession=null,originalSource=null,originalCandidate=null;
        ulong? frequency=null,lastTicks=null;
        var line=new List<byte>(); var utf8=new UTF8Encoding(false,true);
        void Consume()
        {
            if(line.Count==0) throw new InvalidDataException("Empty replay record");
            using var doc=JsonDocument.Parse(utf8.GetString(line.ToArray()),new JsonDocumentOptions{MaxDepth=16});
            JsonElement root=doc.RootElement; var names=new HashSet<string>();
            foreach(var prop in root.EnumerateObject()) if(!names.Add(prop.Name)) throw new InvalidDataException("Duplicate JSON field");
            var e=root.Deserialize<EventEnvelope>(RunRecorder.Json)??throw new InvalidDataException("Missing envelope");
            if(e.Schema!="pro_cal_read_event_v1" || e.Source is not ("fixture" or "replay" or "windows_callback") || e.HostOrdinal!=expected.ToString(CultureInfo.InvariantCulture) ||
                string.IsNullOrWhiteSpace(e.Kind) || e.Kind.Length>64 || string.IsNullOrWhiteSpace(e.SessionId) || e.SessionId.Length>128) throw new InvalidDataException("Invalid provenance/schema/ordinal");
            if(originalSession!=null && (e.SessionId!=originalSession || e.Source!=originalSource)) throw new InvalidDataException("Mixed source sessions");
            originalSession=e.SessionId; originalSource=e.Source;
            ulong ticks=ExactDecimal(e.HostMonotonicTicks,long.MaxValue),hz=ExactDecimal(e.HostMonotonicFrequency,long.MaxValue);
            if(hz==0 || (frequency.HasValue && hz!=frequency.Value) || (lastTicks.HasValue && ticks<lastTicks.Value) ||
                e.HostUtc==null || !(e.HostUtc.EndsWith("Z",StringComparison.Ordinal) || e.HostUtc.EndsWith("+00:00",StringComparison.Ordinal)) ||
                !DateTimeOffset.TryParse(e.HostUtc,CultureInfo.InvariantCulture,DateTimeStyles.None,out var utc) || utc.Offset!=TimeSpan.Zero)
                throw new InvalidDataException("Invalid arrival clocks");
            frequency=hz; lastTicks=ticks;
            if(e.CandidateId!=null)
            {
                if(string.IsNullOrWhiteSpace(e.CandidateId) || e.CandidateId.Length>128 || (originalCandidate!=null && e.CandidateId!=originalCandidate)) throw new InvalidDataException("Mixed candidates");
                originalCandidate=e.CandidateId;
            }
            ulong? providerTime=e.ProviderTimestampRaw==null?null:ExactDecimal(e.ProviderTimestampRaw,ulong.MaxValue);
            expected++;
            string hex=e.PayloadHex??throw new InvalidDataException("Missing payload");
            if(hex.Length>8192 || (hex.Length%2)!=0 || hex.Any(c=>!Uri.IsHexDigit(c))) throw new InvalidDataException("Malformed payload hex");
            byte[] body=Convert.FromHexString(hex);
            if(Convert.ToHexString(SHA256.HashData(body))!=e.PayloadSha256) throw new InvalidDataException("Payload hash mismatch");
            if(e.Kind=="message")
            {
                if(e.DeclaredLength!=body.Length || !e.MessageClass.HasValue || !e.MessageId.HasValue || !e.Sequence.HasValue ||
                    providerTime==null || e.CandidateId==null) throw new InvalidDataException("Malformed message metadata");
                string origin=JsonSerializer.Serialize(new {e.Source,e.SessionId,e.HostOrdinal,e.HostUtc,e.HostMonotonicTicks,e.HostMonotonicFrequency,e.CandidateId},RunRecorder.Json);
                if(session.Lifecycle("replay_origin","candidate-1",detail:origin)<0) throw new InvalidDataException("Replay admission failed");
                unsafe { fixed(byte* p=body) if(session.TryMessage("candidate-1",providerTime.Value,e.MessageClass.Value,e.MessageId.Value,e.Sequence.Value,(uint)body.Length,(nint)p)<0) throw new InvalidDataException("Replay message rejected"); }
            }
            else
            {
                if(body.Length!=0 || e.MessageClass!=null || e.MessageId!=null || e.Sequence!=null || e.DeclaredLength!=null) throw new InvalidDataException("Non-message contains message fields");
                string original=JsonSerializer.Serialize(e,RunRecorder.Json);
                if(session.Lifecycle("replayed_lifecycle","candidate-1",detail:original)<0) throw new InvalidDataException("Replay lifecycle rejected");
            }
            HostRunner.Drain(session,recorder); line.Clear();
        }
        while(true)
        {
            if(stop.IsCancellationRequested) { session.Seal(Terminal.Interrupted); break; }
            int b=file.ReadByte(); if(b<0) { if(line.Count!=0) Consume(); break; }
            if(++total>Limits.OutputBytes) throw new InvalidDataException("Replay input grew beyond limit");
            if(b=='\n') { Consume(); continue; }
            if(line.Count>=65536) throw new InvalidDataException("Replay line exceeds 64 KiB");
            line.Add((byte)b);
        }
        if(expected==1 && !stop.IsCancellationRequested) throw new InvalidDataException("Empty replay input");
        session.Seal(stop.IsCancellationRequested?Terminal.Interrupted:Terminal.OfflineReplayComplete);
        HostRunner.Drain(session,recorder); recorder.Complete(session.Snapshot());
        return session.Terminal==Terminal.OfflineReplayComplete?0:1;
    }
    private static ulong ExactDecimal(string? text,ulong maximum)
    {
        if(!ulong.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out ulong value) || value>maximum || value.ToString(CultureInfo.InvariantCulture)!=text)
            throw new InvalidDataException("Invalid exact integer string");
        return value;
    }
}
