﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using FabricObserver.Interfaces;
using Microsoft.Win32.SafeHandles;

namespace FabricObserver.Observers.Utilities
{
    // Cross plaform impl.
    public class CpuUsageProcess : ICpuUsage
    {
        private DateTime prevTime = DateTime.MinValue;
        private DateTime currentTimeTime = DateTime.MinValue;
        private TimeSpan prevTotalProcessorTime;
        private TimeSpan currentTotalProcessorTime;

        /// <summary>
        /// This function computes process CPU time as a percentage of all processors. 
        /// </summary>
        /// <param name="procId">Target Process object</param>
        /// <param name="procName">Optional process name.</param>
        /// <param name="procHandle">Optional (Windows only) safe process handle.</param>
        /// <returns>CPU Time percentage for supplied procId. If the process is no longer running, then -1 will be returned.</returns>
        public double GetCurrentCpuUsagePercentage(int procId, string procName = null, SafeProcessHandle procHandle = null)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    procHandle ??= NativeMethods.GetSafeProcessHandle(procId);

                    // Validate that the supplied process is still the droid we're looking for.
                    if (procHandle.IsInvalid || string.Compare(NativeMethods.GetProcessNameFromId(procHandle), procName, StringComparison.OrdinalIgnoreCase) != 0)
                    {
                        // Not the droid we're looking for. Caller should ignore this result..
                        return -1;
                    }
                }

                using Process p = Process.GetProcessById(procId);

                // First run.
                if (prevTime == DateTime.MinValue)
                {
                    prevTime = DateTime.Now;
                    prevTotalProcessorTime = p.TotalProcessorTime;
                    Thread.Sleep(50);
                }

                currentTimeTime = DateTime.Now;
                currentTotalProcessorTime = p.TotalProcessorTime;
                double currentUsage = (currentTotalProcessorTime.TotalMilliseconds - prevTotalProcessorTime.TotalMilliseconds) / currentTimeTime.Subtract(prevTime).TotalMilliseconds;
                double cpuUsage = currentUsage / Environment.ProcessorCount;
                prevTime = currentTimeTime;
                prevTotalProcessorTime = currentTotalProcessorTime;

                return cpuUsage * 100.0;
            }
            catch (Exception e) when (e is ArgumentException or Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Caller should ignore this result. Don't want to use an Exception here.
                return -1;
            }
            catch (Exception e)
            {
                ProcessInfoProvider.ProcessInfoLogger.LogWarning($"GetCurrentCpuUsagePercentage(NET8 Process impl) failure (pid = {procId}): {e.Message}");
                throw;
            }
        }
    }
}