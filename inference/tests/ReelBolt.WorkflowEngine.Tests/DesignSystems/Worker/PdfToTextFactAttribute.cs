using System;
using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Worker;

/// <summary>
/// A <see cref="FactAttribute"/> for tests that run the REAL <c>pdftotext</c>. Following the repo convention
/// (<c>FfmpegToolsFactAttribute</c>), a host without poppler gets a visible SKIP that names the binary; the
/// test never returns early and reports green without having asserted anything.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PdfToTextFactAttribute : FactAttribute
{
    public PdfToTextFactAttribute()
    {
        if (!Available())
            Skip = "'pdftotext' (poppler-utils) is not on PATH; this test runs the real binary.";
    }

    public static bool Available()
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "pdftotext",
                Arguments = "-v",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            if (process is null)
                return false;

            process.WaitForExit(5000);
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>A <see cref="FactAttribute"/> for tests that drive a POSIX shell script as a fake executable.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class UnixShellFactAttribute : FactAttribute
{
    public UnixShellFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Skip = "Needs a POSIX /bin/sh to run a fake pdftotext script.";
    }
}
