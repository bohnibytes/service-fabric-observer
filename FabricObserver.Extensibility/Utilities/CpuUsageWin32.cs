﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

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
        /// <param name="procName">The name of the process.</param>
        /// <returns>CPU Time percentage for the process as double value. If the supplied procName is no longer mapped to the supplied procId,
        /// then the result will be -1. Any Win32 failure will result in -1.</returns>
        public double GetCurrentCpuUsagePercentage(uint procId, string procName)
        {
            // Is procId still mapped to procName? If not, then this process is not the droid we're looking for.
            if (NativeMethods.GetProcessNameFromId(procId) != procName)
            {
                ProcessInfoProvider.ProcessInfoLogger.LogWarning($"GetCurrentCpuUsagePercentage(Win32 impl): {procId} is no longer mapped to {procName}");

                // Caller should ignore this result. Don't want to use an Exception here.
                return -1;
            }

            SafeProcessHandle sProcHandle = NativeMethods.GetSafeProcessHandle((uint)procId);

            if (sProcHandle.IsInvalid)
            {
                ProcessInfoProvider.ProcessInfoLogger.LogWarning("GetCurrentCpuUsagePercentage(Win32 impl) failure: Invalid handle.");

                // Caller should ignore this result. Don't want to use an Exception here.
                return -1;
            }

            try
            {
                if (!NativeMethods.GetProcessTimes(sProcHandle, out _, out _, out FILETIME processTimesRawKernelTime, out FILETIME processTimesRawUserTime))
                {
                    ProcessInfoProvider.ProcessInfoLogger.LogWarning($"GetProcessTimes failed with error code {Marshal.GetLastWin32Error()}.");

                    // Caller should ignore this result. Don't want to use an Exception here.
                    return -1;
                }

                if (!NativeMethods.GetSystemTimes(out _, out FILETIME systemTimesRawKernelTime, out FILETIME systemTimesRawUserTime))
                {
                    ProcessInfoProvider.ProcessInfoLogger.LogWarning($"GetSystemTimes failed with error code {Marshal.GetLastWin32Error()}.");

                    // Caller should ignore this result. Don't want to use an Exception here.
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
                    return GetCurrentCpuUsagePercentage(procId, procName);
                }
    
                return cpuUsage;
            }
            finally
            {
                sProcHandle?.Dispose();
                sProcHandle = null;
            }
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
