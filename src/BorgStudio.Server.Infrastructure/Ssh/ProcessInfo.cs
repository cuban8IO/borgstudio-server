using System.Diagnostics;

using BorgStudio.Server.Application.Abstractions.Access;

namespace BorgStudio.Server.Infrastructure.Ssh;

internal sealed class ProcessInfo : IProcessInfo
{
    public int CurrentProcessId => Environment.ProcessId;

    public bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
