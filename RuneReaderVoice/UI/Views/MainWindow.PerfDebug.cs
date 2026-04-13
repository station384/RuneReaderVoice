// SPDX-License-Identifier: GPL-3.0-only
// Temporary perf tracing. Delete this file to remove.
#define PERF_TRACE_UI
using System;
using System.Diagnostics;
namespace RuneReaderVoice.UI.Views;
public partial class MainWindow
{
    [Conditional("PERF_TRACE_UI")]
    private static void PerfLog(string area, string message)
    {
        try
        {
            var managedMb = GC.GetTotalMemory(false) / (1024 * 1024);
            var workingMb = Environment.WorkingSet / (1024 * 1024);
            Debug.WriteLine($"[PERF] {area} | {message} | managed={managedMb}MB working={workingMb}MB");
        }
        catch
        {
            Debug.WriteLine($"[PERF] {area} | {message}");
        }
    }
}
