using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;

namespace RavensPort.Core.Filters;

/// <summary>A GPU as Windows lists it. <see cref="Index"/> is what DirectML is told to use.</summary>
public sealed record GpuAdapter(int Index, string Name, long DedicatedMemoryBytes)
{
    public string Label => $"{Name} ({DedicatedMemoryBytes / (1024 * 1024 * 1024.0):0.#} GB)";
    public override string ToString() => Label;
}

/// <summary>
/// Opens ONNX Runtime sessions on the device a model is set to: the CPU, or a GPU through DirectML.
/// A GPU that cannot start the model — no DirectML in this build, a driver problem, the adapter
/// gone — falls back to the CPU and says so in the activity log, rather than leaving every call
/// blocked behind a model that will not load.
/// </summary>
public static class OnnxSessions
{
    /// <summary>The GPUs DirectML can use, in DXGI order. Empty off Windows and on machines with none.</summary>
    public static IReadOnlyList<GpuAdapter> DetectGpus()
    {
        if (!OperatingSystem.IsWindows()) return [];

        try
        {
            return Dxgi.Adapters();
        }
        catch (Exception)
        {
            // Listing is a convenience for the picker; a failure just means no GPU is offered.
            return [];
        }
    }

    /// <summary>Whether this build can run models on a GPU at all (the Windows build ships DirectML).</summary>
    public static bool GpuSupported => OperatingSystem.IsWindows();

    /// <summary>A session for <paramref name="modelPath"/> on the chosen device, or on the CPU if that device fails.</summary>
    /// <param name="usedDevice">Where it actually runs, for the Test button to report.</param>
    public static InferenceSession Open(
        string modelPath, OnnxDevice device, int gpuIndex, string modelName, ActivityLog? log, out string usedDevice)
    {
        if (device == OnnxDevice.Gpu)
        {
            var gpu = DetectGpus().FirstOrDefault(g => g.Index == gpuIndex);
            var gpuName = gpu?.Name ?? $"GPU {gpuIndex}";

            try
            {
                if (!GpuSupported) throw new PlatformNotSupportedException("GPU models need the Windows build (DirectML).");
                if (gpu is null) throw new InvalidOperationException($"no GPU {gpuIndex} on this PC");

                using var options = new SessionOptions();
                // DirectML's documented requirements: no memory pattern, sequential execution.
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.AppendExecutionProvider_DML(gpuIndex);

                var session = new InferenceSession(modelPath, options);
                usedDevice = gpu.Label;
                return session;
            }
            catch (Exception ex)
            {
                log?.Log($"FILTERS model '{modelName}' could not start on {gpuName} — running on the CPU instead: {ex.Message}");
                log?.LogError($"Filter model '{modelName}' fell back from {gpuName} to the CPU", ex);
                usedDevice = $"CPU (fell back from {gpuName}: {ex.Message})";
                return new InferenceSession(modelPath);
            }
        }

        usedDevice = "CPU";
        return new InferenceSession(modelPath);
    }

    /// <summary>
    /// DXGI adapter enumeration through its COM vtable — the list DirectML's device id indexes
    /// into. Raw vtable calls rather than a COM interop layer: three methods are all this needs.
    /// </summary>
    private static class Dxgi
    {
        private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
        private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDesc1Fn(IntPtr self, out AdapterDesc1 desc);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDesc1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }

        private static T Method<T>(IntPtr comObject, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size));

        public static List<GpuAdapter> Adapters()
        {
            var adapters = new List<GpuAdapter>();
            if (CreateDXGIFactory1(IID_IDXGIFactory1, out var factory) != 0) return adapters;

            try
            {
                // IDXGIFactory1::EnumAdapters1 is slot 12; IDXGIAdapter1::GetDesc1 is slot 10.
                var enumAdapters = Method<EnumAdapters1Fn>(factory, 12);

                for (uint i = 0; ; i++)
                {
                    if (enumAdapters(factory, i, out var adapter) == DXGI_ERROR_NOT_FOUND) break;

                    try
                    {
                        if (Method<GetDesc1Fn>(adapter, 10)(adapter, out var desc) != 0) continue;

                        // The software renderer is listed too; it is no faster than the CPU path.
                        if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;

                        adapters.Add(new GpuAdapter((int)i, desc.Description.Trim(), (long)desc.DedicatedVideoMemory));
                    }
                    finally
                    {
                        Marshal.Release(adapter);
                    }
                }
            }
            finally
            {
                Marshal.Release(factory);
            }

            return adapters;
        }
    }
}
