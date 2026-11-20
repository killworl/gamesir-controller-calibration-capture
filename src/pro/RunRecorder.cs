using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProCalReadHost;
internal interface IReceiptStorage
{
    void CreateExclusiveDirectory(string path);
    Stream OpenNew(string path);
    Stream OpenRead(string path);
    void MoveNew(string source,string destination);
}
internal sealed class PhysicalReceiptStorage : IReceiptStorage
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateDirectoryW")]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectory(string path,nint security);
    public void CreateExclusiveDirectory(string path)
    { if(!CreateDirectory(Path.GetFullPath(path),0)) throw new IOException("Cannot exclusively create run directory",new Win32Exception(Marshal.GetLastWin32Error())); }
    public Stream OpenNew(string path)=>new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
    public Stream OpenRead(string path)=>new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
    public void MoveNew(string source,string destination)=>File.Move(source,destination,false);
}
internal sealed class ReceiptLimitException : IOException { public ReceiptLimitException():base("Receipt data budget exceeded"){} }
internal sealed class RunRecorder : IDisposable
{
    internal static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower};
    private readonly IReceiptStorage storage;
    private readonly string output;
    private readonly Stream events;
    private readonly int budget;
    private long bytes,written;
    private bool poisoned,completed,closed;
    private string? sessionId,source;
    private RunRecorder(string output,IReceiptStorage storage,int budget)
    {
        if(budget<=Limits.ReceiptReserveBytes || budget>Limits.OutputBytes) throw new ArgumentException("Invalid receipt budget");
        this.storage=storage; this.output=output; this.budget=budget;
        storage.CreateExclusiveDirectory(output);
        events=storage.OpenNew(Path.Combine(output,"events.jsonl.partial"));
    }
    public static RunRecorder CreateNew(string output)=>new(output,new PhysicalReceiptStorage(),Limits.OutputBytes);
    public static RunRecorder CreateNew(string output,IReceiptStorage storage,int budget=Limits.OutputBytes)=>new(output,storage,budget);
    private void Ready() { if(poisoned || completed || closed) throw new InvalidOperationException("Recorder is not writable"); }
    public void Append(EventEnvelope value)
    {
        Ready();
        try
        {
            if(value.HostOrdinal!=(written+1).ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                (sessionId!=null && (sessionId!=value.SessionId || source!=value.Source))) throw new InvalidDataException("Mixed session or non-contiguous ordinal");
            byte[] encoded=JsonSerializer.SerializeToUtf8Bytes(value,Json);
            if(bytes+encoded.Length+1>budget-Limits.ReceiptReserveBytes) throw new ReceiptLimitException();
            events.Write(encoded); events.WriteByte((byte)'\n'); bytes+=encoded.Length+1; written++;
            sessionId=value.SessionId; source=value.Source;
        }
        catch { poisoned=true; throw; }
    }
    public void Complete(SessionSummary summary)
    {
        Ready();
        try
        {
            if(summary.Terminal is null || summary.AdmittedEvents!=written || (sessionId!=null && (summary.SessionId!=sessionId || summary.Source!=source))) throw new InvalidDataException("Undrained or mismatched summary");
            events.Flush(); events.Dispose(); closed=true;
            storage.MoveNew(Path.Combine(output,"events.jsonl.partial"),Path.Combine(output,"events.jsonl"));
            var terminal=new {Schema="pro_cal_read_terminal_v1",summary.SessionId,summary.Source,Terminal=summary.Terminal.ToString(),EventCount=written,
                summary.AdmittedEvents,summary.RejectedEvents,summary.LateCallbacks,LateCallbacksBasis="as_of_receipt_seal",summary.NativeAudit,
                HostApiAttempts=summary.NativeAudit.Count,NativeAuditScope="activation,registration,resolution,provider_getters,GIP_query,fixed_read_if_selected; excludes COM reference and string bookkeeping",OsProviderTraffic="UNKNOWN",OsControllerHandles="UNKNOWN",
                EepromReadSuccess="unproven",Freshness="unproven",TransactionMatch="not_established",DeviceQualification="unqualified",FirmwareAuthenticity="unproven"};
            WriteFinal("terminal.json",JsonSerializer.SerializeToUtf8Bytes(terminal,Json));
            var files=new[]{Hash("events.jsonl"),Hash("terminal.json")};
            WriteFinal("manifest.json",JsonSerializer.SerializeToUtf8Bytes(new {Schema="pro_cal_read_manifest_v1",Files=files},Json));
            completed=true;
        }
        catch { poisoned=true; throw; }
    }
    private object Hash(string file)
    {
        using var s=storage.OpenRead(Path.Combine(output,file));
        long length=s.Length; string sha256=Convert.ToHexString(SHA256.HashData(s));
        return new {Path=file,Size=length,Sha256=sha256};
    }
    private void WriteFinal(string name,byte[] content)
    {
        if(bytes+content.Length>budget) throw new ReceiptLimitException();
        string partial=Path.Combine(output,name+".partial");
        using(var file=storage.OpenNew(partial)) { file.Write(content); file.Flush(); }
        storage.MoveNew(partial,Path.Combine(output,name)); bytes+=content.Length;
    }
    public void Dispose() { if(closed) return; closed=true; try { events.Dispose(); } catch(IOException) { poisoned=true; } }
}
