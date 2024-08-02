﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;

namespace FabricObserver.Observers.Utilities
{
    [SupportedOSPlatform("linux")]
    public class LinuxProcessInfoProvider : ProcessInfoProvider
    {
        private const int MaxDescendants = 50;

        public override float GetProcessWorkingSetMb(int processId, string procName, CancellationToken token, bool getPrivateWorkingSet = false)
        {
            if (LinuxProcFS.TryParseStatusFile(processId, out ParsedStatus status))
            {
                return (status.VmRSS - status.RsSFile) / 1048576f;
            }

            // Could not read from /proc/[pid]/status - it is possible that process already exited.
            return 0f;
        }

        // TODO.. cgroups on file system.. 
        public override float GetProcessPrivateBytesMb(int processId)
        {
            return GetProcessWorkingSetMb(processId, null, CancellationToken.None);
        }

        public override float GetProcessAllocatedHandles(int processId, string configPath)
        {
            if (processId < 0 || string.IsNullOrWhiteSpace(configPath))
            {
                return -1f;
            }

            // We need the full path to the currently deployed FO CodePackage, which is where our 
            // proxy binary lives.
            string arg = processId.ToString();
            string bin = $"{configPath}/elevated_proc_fd";
            float result;

            ProcessStartInfo startInfo = new()
            {
                Arguments = arg,
                FileName = bin,
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            string error = string.Empty;
            string output = string.Empty;

            using (Process process = new())
            {
                process.ErrorDataReceived += (sender, e) => { error += e.Data; };
                process.OutputDataReceived += (sender, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { output += e.Data; } };
                process.StartInfo = startInfo;
                
                if (!process.Start())
                {
                    return -1f;
                }

                // Start async reads.
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                process.WaitForExit();

                result = float.TryParse(output, out float ret) ? ret : -1f;

                if (process?.ExitCode != 0)
                {
                    ProcessInfoLogger.LogWarning($"elevated_proc_fd exited with: {process.ExitCode}");
                    
                    // Try and work around the unsetting of caps issues when SF runs a cluster upgrade.
                    if (error.Contains("permission denied", StringComparison.CurrentCultureIgnoreCase))
                    {
                        // Throwing LinuxPermissionException here will eventually take down FO (by design). The failure will be logged and telemetry will be emitted, then
                        // the exception will be re-thrown by ObserverManager and the FO process will fail fast exit. Then, SF will create a new instance of FO on the offending node which
                        // will run the setup bash script that ensures the elevated_proc_fd binary has the correct caps in place.
                        throw new LinuxPermissionException($"Capabilities have been removed from elevated_proc_fd{Environment.NewLine}{error}");
                    }
                    return -1f;
                }
            }

            return result;
        }

        public override List<(string ProcName, int Pid, DateTime ProcessStartTime)> GetChildProcessInfo(int parentPid, NativeMethods.SafeObjectHandle handleToSnapshot = null)
        {
            if (parentPid < 1)
            {
                return null;
            }

            // Get child procs.
            List<(string ProcName, int Pid, DateTime ProcessStartTime)> childProcesses = TupleGetChildProcessInfo(parentPid);

            if (childProcesses == null || childProcesses.Count == 0)
            {
                return null;
            }

            if (childProcesses.Count >= MaxDescendants)
            {
                return childProcesses.Take(MaxDescendants).ToList();
            }

            // Get descendant proc at max depth = 5 and max number of descendants = 50. 
            for (int i = 0; i < childProcesses.Count; ++i)
            {
                List<(string ProcName, int Pid, DateTime ProcessStartTime)> c1 = TupleGetChildProcessInfo(childProcesses[i].Pid);

                if (c1 != null && c1.Count > 0)
                {
                    childProcesses.AddRange(c1);

                    if (childProcesses.Count >= MaxDescendants)
                    {
                        return childProcesses.Take(MaxDescendants).ToList();
                    }

                    for (int j = 0; j < c1.Count; ++j)
                    {
                        List<(string ProcName, int Pid, DateTime ProcessStartTime)> c2 = TupleGetChildProcessInfo(c1[j].Pid);

                        if (c2 != null && c2.Count > 0)
                        {
                            childProcesses.AddRange(c2);

                            if (childProcesses.Count >= MaxDescendants)
                            {
                                return childProcesses.Take(MaxDescendants).ToList();
                            }

                            for (int k = 0; k < c2.Count; ++k)
                            {
                                List<(string ProcName, int Pid, DateTime ProcessStartTime)> c3 = TupleGetChildProcessInfo(c2[k].Pid);

                                if (c3 != null && c3.Count > 0)
                                {
                                    childProcesses.AddRange(c3);

                                    if (childProcesses.Count >= MaxDescendants)
                                    {
                                        return childProcesses.Take(MaxDescendants).ToList();
                                    }

                                    for (int l = 0; l < c3.Count; ++l)
                                    {
                                        List<(string ProcName, int Pid, DateTime ProcessStartTime)> c4 = TupleGetChildProcessInfo(c3[l].Pid);

                                        if (c4 != null && c4.Count > 0)
                                        {
                                            childProcesses.AddRange(c4);

                                            if (childProcesses.Count >= MaxDescendants)
                                            {
                                                return childProcesses.Take(MaxDescendants).ToList();
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return childProcesses;
        }

        public override double GetProcessKvsLvidsUsagePercentage(string procName, CancellationToken token, int procId = 0)
        {
            // Not supported on Linux.
            return -1;
        }

        private static List<(string ProcName, int Pid, DateTime ProcessStartTime)> TupleGetChildProcessInfo(int processId)
        {
            string pidCmdResult = $"ps -o pid= --ppid {processId}".Bash();
            string procNameCmdResult = $"ps -o comm= --ppid {processId}".Bash();
            List<(string ProcName, int Pid, DateTime ProcessStartTime)> childProcesses = null;

            if (!string.IsNullOrWhiteSpace(pidCmdResult) && !string.IsNullOrWhiteSpace(procNameCmdResult))
            {
                var sPids = pidCmdResult.Trim().Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var sProcNames = procNameCmdResult.Trim().Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

                if (sPids?.Length > 0 && sProcNames?.Length > 0)
                {
                    childProcesses = new List<(string ProcName, int Pid, DateTime ProcessStartTime)>();

                    for (int i = 0; i < sPids.Length; ++i)
                    {
                        if (sProcNames[i] is "ps" or "bash")
                        {
                            continue;
                        }

                        if (int.TryParse(sPids[i], out int childProcId))
                        {
                            childProcesses.Add((sProcNames[i], childProcId, GetProcessStartTime(childProcId)));
                        }
                    }
                }
            }

            return childProcesses;
        }

        private static DateTime GetProcessStartTime(int ProcId) 
        {
            try
            {
                using Process p = Process.GetProcessById(ProcId);
                return p.StartTime;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {

            }

            return DateTime.MinValue;
        }
    }
}