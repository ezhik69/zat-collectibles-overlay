"""Read-only inspection helpers. Never injects or writes to the game."""
import ctypes as C
from ctypes import wintypes as W
import struct

k = C.WinDLL('kernel32', use_last_error=True)
k.OpenProcess.argtypes = [W.DWORD, W.BOOL, W.DWORD]
k.OpenProcess.restype = W.HANDLE
k.ReadProcessMemory.argtypes = [W.HANDLE, C.c_void_p, C.c_void_p, C.c_size_t, C.POINTER(C.c_size_t)]
k.ReadProcessMemory.restype = W.BOOL
k.CloseHandle.argtypes = [W.HANDLE]

class Region(C.Structure):
    _fields_ = [('base', C.c_void_p), ('allocation', C.c_void_p), ('allocation_protect', W.DWORD),
                ('partition', W.WORD), ('size', C.c_size_t), ('state', W.DWORD),
                ('protect', W.DWORD), ('type', W.DWORD)]

k.VirtualQueryEx.argtypes = [W.HANDLE, C.c_void_p, C.POINTER(Region), C.c_size_t]
k.VirtualQueryEx.restype = C.c_size_t

class Memory:
    def __init__(self, pid):
        self.handle = k.OpenProcess(0x410, False, pid)
        if not self.handle:
            raise C.WinError(C.get_last_error())

    def read(self, address, size):
        buffer = C.create_string_buffer(size)
        count = C.c_size_t()
        k.ReadProcessMemory(self.handle, address, buffer, size, C.byref(count))
        return buffer.raw[:count.value]

    def regions(self):
        address = 0
        while address < 0x80000000:
            r = Region()
            if not k.VirtualQueryEx(self.handle, address, C.byref(r), C.sizeof(r)):
                break
            if r.state == 0x1000 and not r.protect & 0x101:
                yield r
            address = (r.base or 0) + r.size

    def close(self):
        if self.handle:
            k.CloseHandle(self.handle)
            self.handle = None

    def u32(self, address):
        return struct.unpack('<I', self.read(address, 4))[0]

    def __enter__(self): return self
    def __exit__(self, *_): self.close()
