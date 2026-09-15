using System.Diagnostics;
using System.Runtime.Loader;
using VRCFaceTracking.Core.Sandboxing;
using VRCFaceTracking.Core.Sandboxing.IPC;

namespace VRCFaceTracking.Core.Library;

public class ModuleRuntimeInfo
{
    // @NOTE: The following 4 properties should all be removed by the time sandboxing is done

#if true

    public ExtTrackingModule Module;
    public AssemblyLoadContext AssemblyLoadContext;
    public CancellationTokenSource UpdateCancellationToken;
    public Thread UpdateThread;

#endif

    /// <summary>
    /// Whether the module is active and will receive update events.
    /// </summary>
    public bool IsActive;
    /// <summary>
    /// The UDP port the sandbox process associated with this application is on
    /// </summary>
    public int SandboxProcessPort;
    /// <summary>
    /// The PID of a sandbox process
    /// </summary>
    public int SandboxProcessPID;
    /// <summary>
    /// The path to the module the sandbox shall load
    /// </summary>
    public string SandboxModulePath;
    /// <summary>
    /// The process hosting the sandboxed module
    /// </summary>
    public Process Process;
    /// <summary>
    /// The module's retreived metadata
    /// </summary>
    public ModuleMetadataInternal ModuleInformation;
    /// <summary>
    /// Module status
    /// </summary>
    public ModuleState Status = ModuleState.Uninitialized;
    /// <summary>
    /// The class name of the module. Retrieved through a metadata packet.
    /// </summary>
    public string ModuleClassName;

    /// <summary>
    /// Whether this module supports eye tracking or not.
    /// </summary>
    public bool SupportsEyeTracking;
    /// <summary>
    /// Whether this module supports expression tracking or not.
    /// </summary>
    public bool SupportsExpressionTracking;

    // The two flags above start out false and are only ever AND-ed, so they never say
    // anything. These record the handshake as it actually happened, for deciding when a
    // module that got no slot is worth starting again.

    /// <summary>What the module said it can provide, from its ReplyGetSupported.</summary>
    public bool CanProvideEye, CanProvideExpression;

    /// <summary>Which slots were still free when it was asked to initialize.</summary>
    public bool OfferedEye, OfferedExpression;

    /// <summary>
    /// Queue of packets to send
    /// </summary>
    public Queue<QueuedPacket> EventBus;

    /// <summary>
    /// Rolling tail of the module process's own stdout/stderr. Native tracking SDKs tend to
    /// report failures here rather than through .NET, so this is the most useful evidence
    /// when a module dies. Bounded so a chatty module can't grow it without limit.
    /// </summary>
    private readonly Queue<string> _outputTail = new();
    private const int MaxOutputLines = 60;

    public void RecordOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_outputTail)
        {
            _outputTail.Enqueue(line);
            while (_outputTail.Count > MaxOutputLines)
            {
                _outputTail.Dequeue();
            }
        }
    }

    public string GetOutputTail()
    {
        lock (_outputTail)
        {
            return string.Join(Environment.NewLine, _outputTail);
        }
    }
}

public struct QueuedPacket
{
    public IpcPacket packet;
    public int destinationPort;
}