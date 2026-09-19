using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ZatCollectibles;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct MemoryInfo
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint count, out nuint read);
    [DllImport("kernel32.dll")] internal static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryInfo info, nuint size);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int w, int h, uint flags);
}
