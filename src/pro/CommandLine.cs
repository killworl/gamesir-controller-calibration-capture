using System.Globalization;

namespace ProCalReadHost;
internal enum Mode { Help, OfflineReplay, ActivationOnly, Observe, CapabilityOnly, ReadOnce, Profile1ReadOnce }
internal enum ReadSide { Left, Right }
internal sealed record Options(Mode Mode, string? Input, string? Output, int Seconds, ReadSide? Side=null, bool Foreground=false);
internal static class CommandLine
{
    public const string Help = "G7 Pro fixed calibration-record and Profile1 chunk readers. No arguments = help.\n" +
        "offline-replay --input FILE --output NEW_DIR\nactivation-only --output NEW_DIR\nobserve --output NEW_DIR [--seconds 1..60]\ncapability-only --output NEW_DIR [--seconds 1..60]\n" +
        "read-once --side left|right --output NEW_DIR [--seconds 1..60]\n" +
        "profile1-read-once --output NEW_DIR --window foreground (30s readiness / 5s reply; one fixed chunk)\n" +
        "observe/read-once may add --window foreground for a visible process-owned window.\n" +
        "Native modes require operator authorization. Each read mode sends one fixed stock read; no update or calibration writes. Profile bytes are not an RC readback.";
    public static Options Parse(string[] args)
    {
        if (args.Length == 0 || (args.Length == 1 && args[0] is "help" or "--help")) return new(Mode.Help, null, null, 30);
        Mode mode = args[0] switch { "offline-replay" => Mode.OfflineReplay, "activation-only" => Mode.ActivationOnly, "observe" => Mode.Observe, "capability-only" => Mode.CapabilityOnly, "read-once" => Mode.ReadOnce, "profile1-read-once" => Mode.Profile1ReadOnce, _ => throw new ArgumentException("Unknown mode") };
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i += 2)
        {
            string k = args[i];
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i+1]) || args[i+1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Missing option value");
            if (k != "--output" && !(k == "--input" && mode == Mode.OfflineReplay) && !(k == "--seconds" && mode is Mode.Observe or Mode.CapabilityOnly or Mode.ReadOnce) && !(k == "--side" && mode == Mode.ReadOnce) && !(k == "--window" && mode is Mode.Observe or Mode.ReadOnce or Mode.Profile1ReadOnce)) throw new ArgumentException("Unsupported option");
            if (!flags.TryAdd(k, args[i+1])) throw new ArgumentException("Duplicate option");
        }
        if (!flags.TryGetValue("--output", out string? output)) throw new ArgumentException("Output required");
        flags.TryGetValue("--input", out string? input);
        if (mode == Mode.OfflineReplay && input is null) throw new ArgumentException("Input required");
        ReadSide? side=null;
        if(mode==Mode.ReadOnce) side=flags.GetValueOrDefault("--side") switch {"left"=>ReadSide.Left,"right"=>ReadSide.Right,_=>throw new ArgumentException("One explicit side required")};
        int seconds = mode==Mode.Profile1ReadOnce?5:mode==Mode.ReadOnce?10:30;
        if (flags.TryGetValue("--seconds", out string? s) && (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds is < 1 or > 60)) throw new ArgumentException("Seconds must be 1..60");
        bool foreground=flags.TryGetValue("--window",out var policy);
        if(foreground && policy!="foreground") throw new ArgumentException("Window policy must be foreground");
        if(mode==Mode.Profile1ReadOnce && !foreground) throw new ArgumentException("Profile1 read requires foreground window");
        return new(mode, input, output, seconds,side,foreground);
    }
}
