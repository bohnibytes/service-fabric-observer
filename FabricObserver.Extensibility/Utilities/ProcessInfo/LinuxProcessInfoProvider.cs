﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;

namespace FabricObserver.Observers.Utilities
{
    public class LinuxProcessInfoProvider : ProcessInfoProvider
    {
        public override float GetProcessPrivateWorkingSetInMB(int processId)
        {
            if (LinuxProcFS.TryParseStatusFile(processId, out ParsedStatus status))
            {
                return (status.VmRSS - status.RsSFile) / 1048576f;
            }

            // Could not read from /proc/[pid]/status - it is possible that process already exited.
            return 0f;
        }

        public override float GetProcessAllocatedHandles(int processId, StatelessServiceContext context)
        {
            if (processId < 0)
            {
                return -1f;
            }

            // We need the full path to the currently deployed FO CodePackage, which is where our 
            // proxy binary lives.
            string path = context.CodePackageActivationContext.GetCodePackageObject("Code").Path;
            string arg = processId.ToString();
            string bin = $"{path}/elevated_proc_fd";
            float result;

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                Arguments = arg,
                FileName = bin,
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            using (Process process = Process.Start(startInfo))
            {
                var stdOut = process.StandardOutput;
                string output = stdOut.ReadToEnd();

                process.WaitForExit();

                result = float.TryParse(output, out float ret) ? ret : -42f;

                if (process.ExitCode != 0)
                {
                    Logger.LogWarning($"elevated_proc_fd exited with: {process.ExitCode}");
                    return -1f;
                }
            }

            return result;
        }

        public override List<(string ProcName, int Pid)> GetChildProcessInfo(int processId)
        {
            string pidCmdResult = $"ps -o pid= --ppid {processId}".Bash();
            string procNameCmdResult = $"ps -o comm= --ppid {processId}".Bash();
            List<(string procName, int Pid)> childProcesses = new List<(string procName, int Pid)>();

            if (!string.IsNullOrWhiteSpace(pidCmdResult) && !string.IsNullOrWhiteSpace(procNameCmdResult))
            {
                var sPids = pidCmdResult.Trim().Split('\n');
                var sProcNames = procNameCmdResult.Trim().Split('\n');

                if (sPids?.Length > 0 && sProcNames.Length > 0)
                {
                    for (int i = 0; i < sPids.Length; ++i)
                    {
                        if (int.TryParse(sPids[i], out int childProcId))
                        {
                            childProcesses.Add((sProcNames[i], childProcId)); 
                        }
                    }
                }
            }

            return childProcesses;
        }

        protected override void Dispose(bool disposing)
        {
            // nothing to do here.
        }
    }
}