using System.Runtime.InteropServices;
using PoMode.Shared.Hardware;

namespace PoMode.API.Features.Hardware;

/// <summary>Thin NVML wrapper. Best-effort: any missing DLL/entry point or non-zero return yields null.</summary>
public static partial class NvmlInterop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [LibraryImport("nvml", EntryPoint = "nvmlInit_v2")]
    private static partial int NvmlInit();

    [LibraryImport("nvml", EntryPoint = "nvmlShutdown")]
    private static partial int NvmlShutdown();

    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static partial int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetMemoryInfo")]
    private static partial int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    public static GpuReport? TryProbe()
    {
        try
        {
            if (NvmlInit() != 0)
            {
                return null;
            }
            try
            {
                if (NvmlDeviceGetHandleByIndex(0, out var device) != 0
                    || NvmlDeviceGetMemoryInfo(device, out var memory) != 0)
                {
                    return null;
                }
                return new GpuReport(
                    Vendor: "NVIDIA",
                    TotalVramMb: (long)(memory.Total / (1024 * 1024)),
                    FreeVramMb: (long)(memory.Free / (1024 * 1024)),
                    CudaAvailable: true,
                    DmlAvailable: OperatingSystem.IsWindows());
            }
            finally
            {
                _ = NvmlShutdown();
            }
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }
}
