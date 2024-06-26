﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices.ComTypes;
using FabricObserver.Interfaces;
using System.Threading;
using System.Runtime.InteropServices;

namespace FabricObserver.Observers.Utilities
{
    public class CpuUsageWin32 : ICpuUsage
    {
        private FILETIME processTimesLastUserTime, processTimesLastKernelTime, systemTimesLastUserTime, systemTimesLastKernelTime;
        bool hasRunOnce = false;

        /// <summary>
        /// This function computes the total percentage of all cpus that the supplied process is currently using.
        /// </summary>
        /// <param name="procId">The target process identifier.</param>
        /// <param name="procName">Optional process name.</param>
        /// <param name="procHandle">Optional (Windows only) safe process handle.</param>
        /// <returns>CPU Time percentage for the process as double value. If the supplied procName is no longer mapped to the supplied procId,
        /// then the result will be -1. Any Win32 failure will result in -1.</returns>
        public double GetCurrentCpuUsagePercentage(int procId, string procName = null, SafeProcessHandle procHandle = null)
        {
            procHandle ??= NativeMethods.GetSafeProcessHandle(procId);

            // Validate that the supplied process is still the droid we're looking for. \\

            if (procHandle.IsInvalid)
            {
                return -1;
            }

            // If the process went away, then came back with a new id, ignore it.
            if (string.Compare(NativeMethods.GetProcessNameFromId(procHandle), procName, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return -1;
            }

            if (!NativeMethods.GetProcessTimes(procHandle, out _, out _, out FILETIME processTimesRawKernelTime, out FILETIME processTimesRawUserTime))
            {
                ProcessInfoProvider.ProcessInfoLogger.LogWarning($"GetProcessTimes failed with error code {Marshal.GetLastWin32Error()}.");

                // Caller should ignore this result.
                return -1;
            }

            if (!NativeMethods.GetSystemTimes(out _, out FILETIME systemTimesRawKernelTime, out FILETIME systemTimesRawUserTime))
            {
                ProcessInfoProvider.ProcessInfoLogger.LogWarning($"GetSystemTimes failed with error code {Marshal.GetLastWin32Error()}.");

                // Caller should ignore this result.
                return -1;
            }

            ulong processTimesDelta =
                SubtractTimes(processTimesRawUserTime, processTimesLastUserTime) + SubtractTimes(processTimesRawKernelTime, processTimesLastKernelTime);
            ulong systemTimesDelta =
                SubtractTimes(systemTimesRawUserTime, systemTimesLastUserTime) + SubtractTimes(systemTimesRawKernelTime, systemTimesLastKernelTime);
            double cpuUsage = (double)systemTimesDelta == 0 ? 0 : processTimesDelta * 100 / (double)systemTimesDelta;
            UpdateTimes(processTimesRawUserTime, processTimesRawKernelTime, systemTimesRawUserTime, systemTimesRawKernelTime);

            if (!hasRunOnce)
            {
                Thread.Sleep(100);
                hasRunOnce = true;
                return GetCurrentCpuUsagePercentage(procId, procName, procHandle);
            }
    
            return cpuUsage;
        }

        private void UpdateTimes(FILETIME processTimesRawUserTime, FILETIME processTimesRawKernelTime, FILETIME systemTimesRawUserTime, FILETIME systemTimesRawKernelTime)
        {
            // Process times
            processTimesLastUserTime.dwHighDateTime = processTimesRawUserTime.dwHighDateTime;
            processTimesLastUserTime.dwLowDateTime = processTimesRawUserTime.dwLowDateTime;
            processTimesLastKernelTime.dwHighDateTime = processTimesRawKernelTime.dwHighDateTime;
            processTimesLastKernelTime.dwLowDateTime = processTimesRawKernelTime.dwLowDateTime;

            // System times
            systemTimesLastUserTime.dwHighDateTime = systemTimesRawUserTime.dwHighDateTime;
            systemTimesLastUserTime.dwLowDateTime = systemTimesRawUserTime.dwLowDateTime;
            systemTimesLastKernelTime.dwHighDateTime = systemTimesRawKernelTime.dwHighDateTime;
            systemTimesLastKernelTime.dwLowDateTime = systemTimesRawKernelTime.dwLowDateTime;
        }

        private static ulong SubtractTimes(FILETIME currentFileTime, FILETIME lastUpdateFileTime)
        {
            ulong currentTime = unchecked(((ulong)currentFileTime.dwHighDateTime << 32) | (uint)currentFileTime.dwLowDateTime);
            ulong lastUpdateTime = unchecked(((ulong)lastUpdateFileTime.dwHighDateTime << 32) | (uint)lastUpdateFileTime.dwLowDateTime);

            if ((currentTime - lastUpdateTime) < 0)
            {
                return 0;
            }

            return currentTime - lastUpdateTime;
        }
    }
}
