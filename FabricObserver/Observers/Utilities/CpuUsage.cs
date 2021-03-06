﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics;

namespace FabricObserver.Observers.Utilities
{
    // .NET Standard Process-based impl (cross-platform)
    public class CpuUsage
    {
        private DateTime prevTime = DateTime.MinValue;
        private DateTime currentTimeTime = DateTime.MinValue;
        private TimeSpan prevTotalProcessorTime;
        private TimeSpan currentTotalProcessorTime;

        /// <summary>
        /// This function computes the total percentage of all cpus that the supplied process is currently using.
        /// </summary>
        /// <param name="p">Target Process object</param>
        /// <returns>CPU percentage in use as double value</returns>
        public double GetCpuUsagePercentageProcess(Process p)
        {
            if (p == null || p.HasExited)
            {
                return 0;
            }

            if (this.prevTime == DateTime.MinValue)
            {
                this.prevTime = DateTime.Now;
                this.prevTotalProcessorTime = p.TotalProcessorTime;
            }
            else
            {
                this.currentTimeTime = DateTime.Now;
                this.currentTotalProcessorTime = p.TotalProcessorTime;
                double currentUsage = (this.currentTotalProcessorTime.TotalMilliseconds - this.prevTotalProcessorTime.TotalMilliseconds) / this.currentTimeTime.Subtract(this.prevTime).TotalMilliseconds;
                double cpuUsage = currentUsage / Environment.ProcessorCount;
                this.prevTime = this.currentTimeTime;
                this.prevTotalProcessorTime = this.currentTotalProcessorTime;

                return cpuUsage * 100.0;
            }

            return 0.0;
        }
    }
}