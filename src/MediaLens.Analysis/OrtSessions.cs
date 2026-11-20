using Microsoft.ML.OnnxRuntime;

namespace MediaLens.Analysis;

/// <summary>
/// Creates ONNX Runtime sessions on the best hardware of the computer and falls back to the CPU when the
/// accelerator does not start (old driver, unsupported operator). MEDIALENS_DEVICE=cpu forces the CPU.
/// </summary>
public static class OrtSessions
{
    static OrtSessions()
    {
        // Warnings of the device discovery (e.g. in a VM) are noise for the user.
        var options = new EnvironmentCreationOptions { logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        OrtEnv.CreateInstanceWithOptions(ref options);
    }

    public static (InferenceSession Session, string Device) Create(string modelPath, bool preferAccelerator)
    {
        var forceCpu = string.Equals(Environment.GetEnvironmentVariable("MEDIALENS_DEVICE"), "cpu", StringComparison.OrdinalIgnoreCase);
        if (preferAccelerator && !forceCpu)
        {
            foreach (var (device, configure) in Accelerators())
            {
                SessionOptions? options = null;
                try
                {
                    options = BaseOptions();
                    configure(options);
                    return (new InferenceSession(modelPath, options), device);
                }
                catch (Exception e) when (e is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException or NotSupportedException)
                {
                    // Next one, at the end the CPU.
                }
                finally
                {
                    options?.Dispose();
                }
            }
        }
        using var cpu = BaseOptions();
        return (new InferenceSession(modelPath, cpu), "CPU");
    }

    private static SessionOptions BaseOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        // Half of the cores: the analysis runs in the background while the user browses and plays videos.
        IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
    };

    private static IEnumerable<(string Device, Action<SessionOptions> Configure)> Accelerators()
    {
#if ORT_DIRECTML
        yield return ("DirectML", o =>
        {
            // Required by the DirectML provider.
            o.EnableMemoryPattern = false;
            o.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            o.AppendExecutionProvider_DML(0);
        });
#endif
#if ORT_CUDA
        yield return ("CUDA", o => o.AppendExecutionProvider_CUDA(0));
#endif
        if (OperatingSystem.IsMacOS())
            yield return ("CoreML", o => o.AppendExecutionProvider_CoreML(CoreMLFlags.COREML_FLAG_CREATE_MLPROGRAM));
    }
}
