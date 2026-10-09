"""Scheduling priority of a server process on Windows: CPU class, and the class the GPU scheduler (WDDM) uses between processes.

Unity and the inference servers share one GPU and one CPU. The GPU is time-sliced between processes of the same class, so a server
whose work is a few hundred small kernels waits for its turn between every few of them while Unity draws; a class above Unity's
lets its kernels go first. Raising the class past 'normal' is a request the system may refuse for an ordinary user: the status is
logged either way.
"""
import ctypes
import sys

CPU_CLASS = {'idle': 0x40, 'below': 0x4000, 'normal': 0x20, 'above': 0x8000, 'high': 0x80}
GPU_CLASS = {'idle': 0, 'below': 1, 'normal': 2, 'above': 3, 'high': 4}   # D3DKMT_SCHEDULINGPRIORITYCLASS


def apply(cpu='normal', gpu='normal', log=print):
    if sys.platform != 'win32':
        return
    try:
        k32 = ctypes.windll.kernel32
        k32.GetCurrentProcess.restype = ctypes.c_void_p
        k32.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
        me = k32.GetCurrentProcess()
        if cpu != 'normal':
            ok = k32.SetPriorityClass(me, CPU_CLASS[cpu])
            log('[prio] CPU priority class %s: %s' % (cpu, 'set' if ok else 'refused (error %d)' % k32.GetLastError()))
        if gpu != 'normal':
            gdi = ctypes.windll.gdi32
            gdi.D3DKMTSetProcessSchedulingPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_int]
            gdi.D3DKMTSetProcessSchedulingPriorityClass.restype = ctypes.c_int32
            status = gdi.D3DKMTSetProcessSchedulingPriorityClass(me, GPU_CLASS[gpu])
            log('[prio] GPU scheduling class %s: %s (status 0x%08x)' % (gpu, 'set' if status == 0 else 'refused', status & 0xffffffff))
    except Exception as e:   # a priority is an optimisation, never a reason not to start
        log('[prio] could not set the priority: %s' % e)
