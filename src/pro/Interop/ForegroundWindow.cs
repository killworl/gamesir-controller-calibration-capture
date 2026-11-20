using System.ComponentModel;
using System.Runtime.InteropServices;
namespace ProCalReadHost.Interop;

internal readonly record struct WindowStatus(bool Open,bool Foreground,uint ForegroundProcessId);
internal interface IReceiveWindow : IDisposable { WindowStatus Pump(); void Activate(); }

// A real HWND belonging to this process, not a console owned by conhost.
// Uses Windows' built-in STATIC class and its default close handling.
// No controller APIs, synthetic input, injected focus messages, or focus loops.
internal sealed class ForegroundWindow : IReceiveWindow
{
    private nint handle, label;
    private readonly uint thread=GetCurrentThreadId();
    public ForegroundWindow()
    {
        handle=CreateWindowExW(0x00040000,"STATIC",$"GameSir Controller Calibration Capture | Read only — PID {Environment.ProcessId}",
            0x00CF0000,unchecked((int)0x80000000),unchecked((int)0x80000000),560,175,0,0,GetModuleHandleW(null),0);
        if(handle==0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            label=CreateWindowExW(0,"STATIC",
                "Waiting for the controller. Capture will start automatically when ready.",
                0x50000000,14,16,510,100,handle,0,GetModuleHandleW(null),0);
            if(label==0) throw new Win32Exception(Marshal.GetLastWin32Error());
            ShowWindow(handle,4); // SW_SHOWNOACTIVATE: wait until the sink is bound/Added.
        } catch { Dispose(); throw; }
    }
    public void Activate()
    {
        if(GetCurrentThreadId()!=thread) throw new InvalidOperationException("Window must be activated on its owning thread");
        if(handle==0 || !IsWindow(handle)) return;
        SetForegroundWindow(handle);
        SetWindowTextW(label, GetForegroundWindow()==handle
            ? "Reader active. Leave this window selected while the capture completes."
            : "Windows kept another window active. Click here once to let the reader continue.");
    }
    public WindowStatus Pump()
    {
        if(GetCurrentThreadId()!=thread) throw new InvalidOperationException("Window must be pumped on its owning thread");
        for(int count=0;count<256 && PeekMessageW(out var message,0,0,0,1);count++)
        {
            if(message.Message==0x12) { Dispose(); break; }
            TranslateMessage(ref message); DispatchMessageW(ref message);
        }
        uint owner=0; nint foreground=GetForegroundWindow();
        if(foreground!=0) GetWindowThreadProcessId(foreground,out owner);
        bool open=handle!=0 && IsWindow(handle);
        return new(open,open && owner==(uint)Environment.ProcessId,owner);
    }
    public void Dispose()
    {
        if(handle==0) return;
        if(GetCurrentThreadId()!=thread) throw new InvalidOperationException("Window must be closed on its owning thread");
        if(IsWindow(handle)) DestroyWindow(handle);
        handle=0;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage
    { public nint Window; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X,Y; public uint Private; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] private static extern bool SetWindowTextW(nint window, string text);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] private static extern nint CreateWindowExW(uint ex,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll",ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessageW(out NativeMessage message,nint window,uint min,uint max,uint remove);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll",ExactSpelling=true)] private static extern nint DispatchMessageW(ref NativeMessage message);
}
