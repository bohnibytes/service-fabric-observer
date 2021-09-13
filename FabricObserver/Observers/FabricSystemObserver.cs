﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Fabric;
using System.Fabric.Health;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;

namespace FabricObserver.Observers
{
    // When enabled, FabricSystemObserver monitors all Fabric system service processes across various resource usage metrics (CPU Time, Private Workingset, Ephemeral and Total Active TCP ports, File Handles).
    // It will signal Warnings or Errors based on settings supplied in ApplicationManifest.xml (Like many observers, most of it's settings are overridable and can be reset with application parameter updates).
    // If the FabricObserverWebApi service is deployed: The output (a local file) is created for and used by the API service (http://localhost:5000/api/ObserverManager).
    // SF Health Report processor will also emit ETW telemetry if configured in ApplicationManifest.xml.
    // As with all observers, you should first determine the good (normal) states across resource usage before you set thresholds for the bad ones.
    public class FabricSystemObserver : ObserverBase
    {
        private string[] processWatchList;
        private Stopwatch stopwatch;

        // Health Report data container - For use in analysis to determine health state.
        private List<FabricResourceUsageData<int>> allCpuData;
        private List<FabricResourceUsageData<float>> allMemData;
        private List<FabricResourceUsageData<int>> allActiveTcpPortData;
        private List<FabricResourceUsageData<int>> allEphemeralTcpPortData;
        private List<FabricResourceUsageData<float>> allHandlesData;

        // Windows only. (EventLog).
        private List<EventRecord> evtRecordList = null;
        private bool monitorWinEventLog;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricSystemObserver"/> class.
        /// </summary>
        public FabricSystemObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)
        {
           
        }

        public int CpuErrorUsageThresholdPct
        {
            get; set;
        }

        public int MemErrorUsageThresholdMb
        {
            get; set;
        }

        public int TotalActivePortCountAllSystemServices
        {
            get; set;
        }

        public int TotalActiveEphemeralPortCountAllSystemServices
        {
            get; set;
        }

        public float TotalAllocatedHandlesAllSystemServices
        {
            get; set;
        }

        public int ActiveTcpPortCountError
        {
            get; set;
        }

        public int ActiveEphemeralPortCountError
        {
            get; set;
        }

        public int ActiveTcpPortCountWarning
        {
            get; set;
        }

        public int ActiveEphemeralPortCountWarning
        {
            get; set;
        }

        public int CpuWarnUsageThresholdPct
        {
            get; set;
        }

        public int MemWarnUsageThresholdMb
        {
            get; set;
        }

        public int AllocatedHandlesWarning
        {
            get; set;
        }

        public int AllocatedHandlesError
        {
            get; set;
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            if (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            Token = token;

            if (Token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                Initialize();

                for (int i = 0; i < processWatchList.Length; ++i)
                {
                    Token.ThrowIfCancellationRequested();

                    string procName = processWatchList[i];

                    try
                    {
                        string dotnet = string.Empty;

                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && procName.EndsWith(".dll"))
                        {
                            dotnet = "dotnet ";
                        }

                        await GetProcessInfoAsync($"{dotnet}{procName}").ConfigureAwait(true);
                    }
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
                    {

                    }
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                ObserverLogger.LogError( $"Unhandled exception in ObserveAsync:{Environment.NewLine}{e}");

                // Fix the bug..
                throw;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && IsObserverWebApiAppDeployed && monitorWinEventLog)
            {
                ReadServiceFabricWindowsEventLog();
            }

            await ReportAsync(token).ConfigureAwait(true);
            CleanUp();

            // The time it took to run this observer to completion.
            stopwatch.Stop();
            RunDuration = stopwatch.Elapsed;

            if (EnableVerboseLogging)
            {
                ObserverLogger.LogInfo($"Run Duration: {RunDuration}");
            }

            stopwatch.Reset();
            LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            try
            {
                Token.ThrowIfCancellationRequested();

                string memHandlesInfo = string.Empty;

                if (allMemData != null)
                {
                    memHandlesInfo += $"Fabric memory: {allMemData.FirstOrDefault(x => x.Id == "Fabric")?.AverageDataValue} MB{Environment.NewLine}" +
                                      $"FabricDCA memory: {allMemData.FirstOrDefault(x => x.Id.Contains("FabricDCA"))?.AverageDataValue} MB{Environment.NewLine}" +
                                      $"FabricGateway memory: {allMemData.FirstOrDefault(x => x.Id.Contains("FabricGateway"))?.AverageDataValue} MB{Environment.NewLine}" +

                                      // On Windows, FO runs as NetworkUser by default and therefore can't monitor FabricHost process, which runs as System.
                                      (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ?
                                          $"FabricHost memory: {allMemData.FirstOrDefault(x => x.Id == "FabricHost")?.AverageDataValue} MB{Environment.NewLine}" : string.Empty);
                }

                if (allHandlesData != null)
                {
                    memHandlesInfo += $"Fabric file handles: {allHandlesData.FirstOrDefault(x => x.Id == "Fabric")?.AverageDataValue}{Environment.NewLine}" +
                                      $"FabricDCA file handles: {allHandlesData.FirstOrDefault(x => x.Id.Contains("FabricDCA"))?.AverageDataValue}{Environment.NewLine}" +
                                      $"FabricGateway file handles: {allHandlesData.FirstOrDefault(x => x.Id.Contains("FabricGateway"))?.AverageDataValue}{Environment.NewLine}" +

                                      // On Windows, FO runs as NetworkUser by default and therefore can't monitor FabricHost process, which runs as System. 
                                      (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ?
                                            $"FabricHost file handles: {allHandlesData.FirstOrDefault(x => x.Id == "FabricHost")?.AverageDataValue}" : string.Empty);
                }

                // Informational report.
                TimeSpan timeToLiveWarning = GetHealthReportTimeToLive();
                var informationReport = new HealthReport
                {
                    Observer = ObserverName,
                    NodeName = NodeName,
                    HealthMessage = $"TCP ports in use by Fabric System services: {TotalActivePortCountAllSystemServices}{Environment.NewLine}" +
                                    $"Ephemeral TCP ports in use by Fabric System services: {TotalActiveEphemeralPortCountAllSystemServices}{Environment.NewLine}" +
                                    $"File handles in use by Fabric System services: {TotalAllocatedHandlesAllSystemServices}{Environment.NewLine}{memHandlesInfo}",

                    State = HealthState.Ok,
                    HealthReportTimeToLive = timeToLiveWarning,
                    ReportType = HealthReportType.Node
                };

                HealthReporter.ReportHealthToServiceFabric(informationReport);

                // Reset local tracking counters.
                TotalActivePortCountAllSystemServices = 0;
                TotalActiveEphemeralPortCountAllSystemServices = 0;
                TotalAllocatedHandlesAllSystemServices = 0;

                // CPU
                if (CpuErrorUsageThresholdPct > 0 || CpuWarnUsageThresholdPct > 0)
                {
                    ProcessResourceDataList(allCpuData, CpuErrorUsageThresholdPct, CpuWarnUsageThresholdPct);
                }

                // Memory
                if (MemErrorUsageThresholdMb > 0 || MemWarnUsageThresholdMb > 0)
                {
                    ProcessResourceDataList(allMemData, MemErrorUsageThresholdMb, MemWarnUsageThresholdMb);
                }

                // Ports - Active TCP
                if (ActiveTcpPortCountError > 0 || ActiveTcpPortCountWarning > 0)
                {
                    ProcessResourceDataList(allActiveTcpPortData, ActiveTcpPortCountError, ActiveTcpPortCountWarning);
                }

                // Ports - Ephemeral
                if (ActiveEphemeralPortCountError > 0 || ActiveEphemeralPortCountWarning > 0)
                {
                    ProcessResourceDataList(allEphemeralTcpPortData, ActiveEphemeralPortCountError, ActiveEphemeralPortCountWarning);
                }

                // Handles
                if (AllocatedHandlesError > 0 || AllocatedHandlesWarning > 0)
                {
                    ProcessResourceDataList(allHandlesData, AllocatedHandlesError, AllocatedHandlesWarning);
                }

                // Windows Event Log
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && IsObserverWebApiAppDeployed && monitorWinEventLog)
                {
                    // SF Eventlog Errors?
                    // Write this out to a new file, for use by the web front end log viewer.
                    // Format = HTML.
                    int count = evtRecordList.Count();
                    var logPath = Path.Combine(ObserverLogger.LogFolderBasePath, "EventVwrErrors.txt");

                    // Remove existing file.
                    if (File.Exists(logPath))
                    {
                        try
                        {
                            File.Delete(logPath);
                        }
                        catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
                        {

                        }
                    }

                    if (count >= 10)
                    {
                        var sb = new StringBuilder();

                        _ = sb.AppendLine("<br/><div><strong>" +
                                          "<a href='javascript:toggle(\"evtContainer\")'>" +
                                          "<div id=\"plus\" style=\"display: inline; font-size: 25px;\">+</div> " + count +
                                          " Error Events in ServiceFabric and System</a> " +
                                          "Event logs</strong>.<br/></div>");

                        _ = sb.AppendLine("<div id='evtContainer' style=\"display: none;\">");

                        foreach (var evt in evtRecordList.Distinct())
                        {
                            token.ThrowIfCancellationRequested();

                            try
                            {
                                // Access event properties:
                                _ = sb.AppendLine("<div>" + evt.LogName + "</div>");
                                _ = sb.AppendLine("<div>" + evt.LevelDisplayName + "</div>");
                                if (evt.TimeCreated.HasValue)
                                {
                                    _ = sb.AppendLine("<div>" + evt.TimeCreated.Value.ToShortDateString() + "</div>");
                                }

                                foreach (var prop in evt.Properties)
                                {
                                    if (prop.Value != null && Convert.ToString(prop.Value).Length > 0)
                                    {
                                        _ = sb.AppendLine("<div>" + prop.Value + "</div>");
                                    }
                                }
                            }
                            catch (EventLogException)
                            {
                            }
                        }

                        _ = sb.AppendLine("</div>");

                        _ = ObserverLogger.TryWriteLogFile(logPath, sb.ToString());
                        _ = sb.Clear();
                    }

                    // Clean up.
                    if (count > 0)
                    {
                        evtRecordList.Clear();
                    }
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
            {
                ObserverLogger.LogError($"Unhandled exception in ReportAsync:{Environment.NewLine}{e}");

                // Fix the bug..
                throw;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// ReadServiceFabricWindowsEventLog().
        /// </summary>
        private void ReadServiceFabricWindowsEventLog()
        {
            string sfOperationalLogSource = "Microsoft-ServiceFabric/Operational";
            string sfAdminLogSource = "Microsoft-ServiceFabric/Admin";
            string systemLogSource = "System";
            string sfLeaseAdminLogSource = "Microsoft-ServiceFabric-Lease/Admin";
            string sfLeaseOperationalLogSource = "Microsoft-ServiceFabric-Lease/Operational";

            var range2Days = DateTime.UtcNow.AddDays(-1);
            var format = range2Days.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00K", CultureInfo.InvariantCulture);
            var datexQuery = $"*[System/TimeCreated/@SystemTime >='{format}']";

            // Critical and Errors only.
            string xQuery = "*[System/Level <= 2] and " + datexQuery;

            // SF Admin Event Store.
            var evtLogQuery = new EventLogQuery(sfAdminLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent(); eventInstance != null; eventInstance = evtLogReader.ReadEvent())
                {
                    Token.ThrowIfCancellationRequested();
                    evtRecordList.Add(eventInstance);
                }
            }

            // SF Operational Event Store.
            evtLogQuery = new EventLogQuery(sfOperationalLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent(); eventInstance != null; eventInstance = evtLogReader.ReadEvent())
                {
                    Token.ThrowIfCancellationRequested();
                    evtRecordList.Add(eventInstance);
                }
            }

            // SF Lease Admin Event Store.
            evtLogQuery = new EventLogQuery(sfLeaseAdminLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent(); eventInstance != null; eventInstance = evtLogReader.ReadEvent())
                {
                    Token.ThrowIfCancellationRequested();
                    evtRecordList.Add(eventInstance);
                }
            }

            // SF Lease Operational Event Store.
            evtLogQuery = new EventLogQuery(sfLeaseOperationalLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent(); eventInstance != null; eventInstance = evtLogReader.ReadEvent())
                {
                    Token.ThrowIfCancellationRequested();
                    evtRecordList.Add(eventInstance);
                }
            }

            // System Event Store.
            evtLogQuery = new EventLogQuery(systemLogSource, PathType.LogName, xQuery);
            using (var evtLogReader = new EventLogReader(evtLogQuery))
            {
                for (var eventInstance = evtLogReader.ReadEvent(); eventInstance != null; eventInstance = evtLogReader.ReadEvent())
                {
                    Token.ThrowIfCancellationRequested();
                    evtRecordList.Add(eventInstance);
                }
            }
        }

        private Process[] GetDotnetLinuxProcessesByFirstArgument(string argument)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                throw new PlatformNotSupportedException("This function should only be called on Linux platforms.");
            }

            var result = new List<Process>();
            var processes = Process.GetProcessesByName("dotnet");

            for (int i = 0; i < processes.Length; ++i)
            {
                Token.ThrowIfCancellationRequested();

                Process process = processes[i];

                try
                {
                    string cmdline = File.ReadAllText($"/proc/{process.Id}/cmdline");

                    // dotnet /mnt/sfroot/_App/__FabricSystem_App4294967295/US.Code.Current/FabricUS.dll 
                    if (cmdline.Contains("/mnt/sfroot/_App/"))
                    {
                        string bin = cmdline[(cmdline.LastIndexOf("/", StringComparison.Ordinal) + 1)..];

                        if (string.Equals(argument, bin, StringComparison.InvariantCulture))
                        {
                            result.Add(process);
                        }
                    }
                    else if (cmdline.Contains("Fabric"))
                    {
                        // dotnet FabricDCA.dll
                        string[] parts = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);

                        if (parts.Length > 1 && string.Equals(argument, parts[1], StringComparison.Ordinal))
                        {
                            result.Add(process);
                        }
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    // It is possible that the process already exited.
                }
            }

            return result.ToArray();
        }

        private void Initialize()
        {
            Token.ThrowIfCancellationRequested();
            
            // Linux
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                processWatchList = new []
                {
                    "Fabric",
                    "FabricDCA.dll",
                    "FabricDnsService",
                    "FabricCAS.dll",
                    "FabricFAS.dll",
                    "FabricGateway.exe",
                    "FabricHost",
                    "FabricIS.dll",
                    "FabricRM.exe",
                    "FabricUS.dll"
                };
            }
            else
            {
                // Windows
                processWatchList = new []
                {
                    "Fabric",
                    "FabricApplicationGateway",
                    "FabricDCA",
                    "FabricDnsService",
                    "FabricFAS",
                    "FabricGateway",
                    "FabricHost",
                    "FabricIS",
                    "FabricRM"
                };
            }

            // fabric:/System
            MonitoredAppCount = 1;
            MonitoredServiceProcessCount = processWatchList.Length;
            int listcapacity = processWatchList.Length;
            int frudCapacity = 4;

            if (UseCircularBuffer)
            {
                frudCapacity = DataCapacity > 0 ? DataCapacity : 5;
            }
            else if (MonitorDuration > TimeSpan.MinValue)
            {
                frudCapacity = (int)MonitorDuration.TotalSeconds * 4;
            }

            stopwatch ??= new Stopwatch();
            stopwatch.Start();
            SetThresholdSFromConfiguration();

            // CPU data
            if (allCpuData == null && (CpuErrorUsageThresholdPct > 0 || CpuWarnUsageThresholdPct > 0))
            {
                allCpuData = new List<FabricResourceUsageData<int>>(listcapacity);

                foreach (var proc in processWatchList)
                {
                    allCpuData.Add(
                        new FabricResourceUsageData<int>(
                                ErrorWarningProperty.TotalCpuTime,
                                proc,
                                frudCapacity,
                                UseCircularBuffer));
                }
            }

            // Memory data
            if (allMemData == null && (MemErrorUsageThresholdMb > 0 || MemWarnUsageThresholdMb > 0))
            {
                allMemData = new List<FabricResourceUsageData<float>>(listcapacity);

                foreach (var proc in processWatchList)
                {
                    allMemData.Add(
                        new FabricResourceUsageData<float>(
                                ErrorWarningProperty.TotalMemoryConsumptionMb,
                                proc,
                                frudCapacity,
                                UseCircularBuffer));
                }
            }

            // Ports
            if (allActiveTcpPortData == null && (ActiveTcpPortCountError > 0 || ActiveTcpPortCountWarning > 0))
            {
                allActiveTcpPortData = new List<FabricResourceUsageData<int>>(listcapacity);

                foreach (var proc in processWatchList)
                {
                    allActiveTcpPortData.Add(
                        new FabricResourceUsageData<int>(
                                ErrorWarningProperty.TotalActivePorts,
                                proc,
                                frudCapacity,
                                UseCircularBuffer));
                }
            }

            if (allEphemeralTcpPortData == null && (ActiveEphemeralPortCountError > 0 || ActiveEphemeralPortCountWarning > 0))
            {
                allEphemeralTcpPortData = new List<FabricResourceUsageData<int>>(listcapacity);

                foreach (var proc in processWatchList)
                {
                    allEphemeralTcpPortData.Add(
                        new FabricResourceUsageData<int>(
                                ErrorWarningProperty.TotalEphemeralPorts,
                                proc,
                                frudCapacity,
                                UseCircularBuffer));
                }
            }

            // Handles
            if (allHandlesData == null && (AllocatedHandlesError > 0 || AllocatedHandlesWarning > 0))
            {
                allHandlesData = new List<FabricResourceUsageData<float>>(listcapacity);

                foreach (var proc in processWatchList)
                {
                    allHandlesData.Add(
                        new FabricResourceUsageData<float>(
                                ErrorWarningProperty.TotalFileHandles,
                                proc,
                                frudCapacity,
                                UseCircularBuffer));
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && monitorWinEventLog && evtRecordList == null)
            {
                evtRecordList = new List<EventRecord>();
            }
        }

        private void SetThresholdSFromConfiguration()
        {
            /* Error thresholds */
            
            Token.ThrowIfCancellationRequested();

            var cpuError = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverCpuErrorLimitPct);

            if (!string.IsNullOrEmpty(cpuError))
            {
                _ = int.TryParse(cpuError, out int threshold);

                if (threshold > 100 || threshold < 0)
                {
                    throw new ArgumentException($"{threshold}% is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverCpuErrorLimitPct}.");
                }

                CpuErrorUsageThresholdPct = threshold;
            }

            var memError = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverMemoryErrorLimitMb);

            if (!string.IsNullOrEmpty(memError))
            {
                _ = int.TryParse(memError, out int threshold);

                if (threshold < 0)
                {
                    throw new ArgumentException($"{threshold} is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverMemoryErrorLimitMb}.");
                }

                MemErrorUsageThresholdMb = threshold;
            }

            // Ports
            var activeTcpPortsError = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverNetworkErrorActivePorts);

            if (!string.IsNullOrEmpty(activeTcpPortsError))
            {
                _ = int.TryParse(activeTcpPortsError, out int threshold);
                ActiveTcpPortCountError = threshold;
            }

            var activeEphemeralPortsError = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverNetworkErrorEphemeralPorts);

            if (!string.IsNullOrEmpty(activeEphemeralPortsError))
            {
                _ = int.TryParse(activeEphemeralPortsError, out int threshold);
                ActiveEphemeralPortCountError = threshold;
            }

            // Handles
            var handlesError = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverErrorHandles);

            if (!string.IsNullOrEmpty(handlesError))
            {
                _ = int.TryParse(handlesError, out int threshold);
                AllocatedHandlesError = threshold;
            }

            /* Warning thresholds */

            Token.ThrowIfCancellationRequested();

            var cpuWarn = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverCpuWarningLimitPct);

            if (!string.IsNullOrEmpty(cpuWarn))
            {
                _ = int.TryParse(cpuWarn, out int threshold);

                if (threshold > 100 || threshold < 0)
                {
                    throw new ArgumentException($"{threshold}% is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverCpuWarningLimitPct}.");
                }

                CpuWarnUsageThresholdPct = threshold;
            }

            var memWarn = GetSettingParameterValue( ConfigurationSectionName, ObserverConstants.FabricSystemObserverMemoryWarningLimitMb);

            if (!string.IsNullOrEmpty(memWarn))
            {
                _ = int.TryParse(memWarn, out int threshold);

                if (threshold < 0)
                {
                    throw new ArgumentException($"{threshold} MB is not a meaningful threshold value for {ObserverConstants.FabricSystemObserverMemoryWarningLimitMb}.");
                }

                MemWarnUsageThresholdMb = threshold;
            }

            // Ports
            var activeTcpPortsWarning = GetSettingParameterValue( ConfigurationSectionName, ObserverConstants.FabricSystemObserverNetworkWarningActivePorts);

            if (!string.IsNullOrEmpty(activeTcpPortsWarning))
            {
                _ = int.TryParse(activeTcpPortsWarning, out int threshold);
                ActiveTcpPortCountWarning = threshold;
            }

            var activeEphemeralPortsWarning = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverNetworkWarningEphemeralPorts);

            if (!string.IsNullOrEmpty(activeEphemeralPortsWarning))
            {
                _ = int.TryParse(activeEphemeralPortsWarning, out int threshold);
                ActiveEphemeralPortCountWarning = threshold;
            }

            // Handles
            var handlesWarning = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverWarningHandles);

            if (!string.IsNullOrEmpty(handlesWarning))
            {
                _ = int.TryParse(handlesWarning, out int threshold);
                AllocatedHandlesWarning = threshold;
            }

            // Monitor Windows event log for SF and System Error/Critical events?
            // This can be noisy. Use wisely. Return if running on Linux.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var watchEvtLog = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.FabricSystemObserverMonitorWindowsEventLog);

            if (!string.IsNullOrEmpty(watchEvtLog) && bool.TryParse(watchEvtLog, out bool watchEl))
            {
                monitorWinEventLog = watchEl;
            }
        }

        private async Task GetProcessInfoAsync(string procName)
        {
            // This is to support differences between Linux and Windows dotnet process naming pattern.
            // Default value is what Windows expects for proc name. In linux, the procname is an argument (typically) of a dotnet command.
            string dotnetArg = procName;
            Process[] processes;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && procName.Contains("dotnet"))
            {
                dotnetArg = $"{procName.Replace("dotnet ", string.Empty)}";
                processes = GetDotnetLinuxProcessesByFirstArgument(dotnetArg);
            }
            else
            {
                processes = Process.GetProcessesByName(procName);
            }

            if (processes.Length == 0)
            {
                return;
            }

            Stopwatch timer = new Stopwatch();

            for (int i = 0; i < processes.Length; ++i)
            {
                Token.ThrowIfCancellationRequested();

                Process process = processes[i];

                try
                { 
                    // Ports - Active TCP All
                    int activePortCount = OSInfoProvider.Instance.GetActiveTcpPortCount(process.Id, FabricServiceContext);
                    
                    // This is used for info report.
                    TotalActivePortCountAllSystemServices += activePortCount;
                    
                    if (ActiveTcpPortCountError > 0 || ActiveTcpPortCountWarning > 0)
                    {
                        allActiveTcpPortData.FirstOrDefault(x => x.Id == dotnetArg).Data.Add(activePortCount);
                    }

                    // Ports - Active TCP Ephemeral
                    int activeEphemeralPortCount = OSInfoProvider.Instance.GetActiveEphemeralPortCount(process.Id, FabricServiceContext);

                    // This is used for info report.
                    TotalActiveEphemeralPortCountAllSystemServices += activeEphemeralPortCount;
                    
                    if (ActiveEphemeralPortCountError > 0 || ActiveEphemeralPortCountWarning > 0)
                    {
                        allEphemeralTcpPortData.FirstOrDefault(x => x.Id == dotnetArg).Data.Add(activeEphemeralPortCount);
                    }

                    // Allocated Handles
                    float handles = ProcessInfoProvider.Instance.GetProcessAllocatedHandles(process.Id, FabricServiceContext);

                    // This is used for info report.
                    TotalAllocatedHandlesAllSystemServices += handles;
                    
                    // No need to proceed further if there are no configuration settings for CPU, Memory, Handles thresholds.
                    // Returning here is correct as supplied thresholds apply to all system services.
                    if (CpuErrorUsageThresholdPct <= 0 && CpuWarnUsageThresholdPct <= 0 && MemErrorUsageThresholdMb <= 0 && MemWarnUsageThresholdMb <= 0
                        && AllocatedHandlesError <= 0 && AllocatedHandlesWarning <= 0)
                    {
                        return;
                    }

                    // Handles/FDs
                    if (AllocatedHandlesError > 0 || AllocatedHandlesWarning > 0)
                    {
                        allHandlesData.FirstOrDefault(x => x.Id == dotnetArg).Data.Add(handles);
                    }

                    CpuUsage cpuUsage = new CpuUsage();

                    // Mem
                    if (MemErrorUsageThresholdMb > 0 || MemWarnUsageThresholdMb > 0)
                    {
                        float mem = ProcessInfoProvider.Instance.GetProcessWorkingSetMb(process.Id, true);
                        allMemData.FirstOrDefault(x => x.Id == dotnetArg).Data.Add(mem);
                    }

                    TimeSpan duration = TimeSpan.FromSeconds(1);

                    if (MonitorDuration > TimeSpan.MinValue)
                    {
                        duration = MonitorDuration;
                    }

                    timer.Start();

                    while (!process.HasExited && timer.Elapsed <= duration)
                    {
                        Token.ThrowIfCancellationRequested();

                        try
                        {
                            // CPU Time for service process.
                            if (CpuErrorUsageThresholdPct > 0 || CpuWarnUsageThresholdPct > 0)
                            {
                                int cpu = (int)cpuUsage.GetCpuUsagePercentageProcess(process.Id);
                                allCpuData.FirstOrDefault(x => x.Id == dotnetArg).Data.Add(cpu);
                            }

                            await Task.Delay(250, Token).ConfigureAwait(true);
                        }
                        catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
                        {
                            ObserverLogger.LogWarning($"Unhandled Exception thrown in GetProcessInfoAsync:{Environment.NewLine}{e}");

                            // Fix the bug..
                            throw;
                        }
                    }
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
                {
                    // This will be a Win32Exception or InvalidOperationException if FabricObserver.exe is not running as Admin or LocalSystem on Windows.
                    // It's OK. Just means that the elevated process (like FabricHost.exe) won't be observed. 
                    // It is generally *not* worth running FO process as a Windows elevated user just for this scenario. On Linux, FO always should be run as normal user, not root.
#if DEBUG
                    ObserverLogger.LogWarning($"Can't observe {procName} due to it's privilege level. " +
                                              $"FabricObserver must be running as System or Admin on Windows for this specific task.");
#endif       
                    continue;
                }
                catch (Exception e) when (!(e is OperationCanceledException || e is TaskCanceledException))
                {
                    ObserverLogger.LogError("Unhandled exception in GetProcessInfoAsync:{Environment.NewLine}{e}");

                    // Fix the bug..
                    throw;
                }
                finally
                {
                    process?.Dispose();
                }

                timer.Stop();
                timer.Reset();

                await Task.Delay(250, Token).ConfigureAwait(false);
            }
        }

        private void ProcessResourceDataList<T>(
                            List<FabricResourceUsageData<T>> data,
                            T thresholdError,
                            T thresholdWarning)
                                where T : struct
        {
            string fileName = null;

            if (EnableCsvLogging)
            {
                fileName = $"FabricSystemServices{(CsvWriteFormat == CsvFileWriteFormat.MultipleFilesNoArchives ? "_" + DateTime.UtcNow.ToString("o") : string.Empty)}";
            }

            for (int i = 0; i < data.Count; ++i)
            {
                Token.ThrowIfCancellationRequested();

                var dataItem = data[i];

                if (dataItem.Data.Count == 0 || dataItem.AverageDataValue <= 0)
                {
                    continue;
                }

                if (EnableCsvLogging)
                {
                    var propertyName = data.First().Property;

                    /* Log average data value to long-running store (CSV).*/

                    var dataLogMonitorType = propertyName switch
                    {
                        ErrorWarningProperty.TotalCpuTime => "% CPU Time",
                        ErrorWarningProperty.TotalMemoryConsumptionMb => "Working Set %",
                        ErrorWarningProperty.TotalActivePorts => "Active TCP Ports",
                        ErrorWarningProperty.TotalEphemeralPorts => "Active Ephemeral Ports",
                        ErrorWarningProperty.TotalFileHandlesPct => "Allocated (in use) File Handles %",
                        _ => propertyName
                    };

                    // Log pid
                    try
                    {
                        int procId = -1;
                        Process[] p = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                            ? GetDotnetLinuxProcessesByFirstArgument(dataItem.Id)
                            : Process.GetProcessesByName(dataItem.Id);

                        if (p.Length > 0)
                        {
                            procId = p.First().Id;
                        }

                        if (procId > 0)
                        {
                            CsvFileLogger.LogData(fileName, dataItem.Id, "ProcessId", "", procId);
                        }
                    }
                    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                    {

                    }

                    CsvFileLogger.LogData(fileName, dataItem.Id, dataLogMonitorType, "Average", Math.Round(dataItem.AverageDataValue, 2));
                    CsvFileLogger.LogData(fileName, dataItem.Id, dataLogMonitorType, "Peak", Math.Round(Convert.ToDouble(dataItem.MaxDataValue)));
                }

                // This function will clear Data items in list (will call Clear() on the supplied FabricResourceUsageData instance's Data field..)
                ProcessResourceDataReportHealth(
                       dataItem,
                       thresholdError,
                       thresholdWarning,
                       GetHealthReportTimeToLive(),
                       HealthReportType.Application);
            }
        }

        private void CleanUp()
        {
            processWatchList = null;

            if (allCpuData != null && !allCpuData.Any(frud => frud.ActiveErrorOrWarning))
            {
                allCpuData?.Clear();
                allCpuData = null;
            }

            if (allEphemeralTcpPortData != null && !allEphemeralTcpPortData.Any(frud => frud.ActiveErrorOrWarning))
            {
                allEphemeralTcpPortData?.Clear();
                allEphemeralTcpPortData = null;
            }

            if (allHandlesData != null && !allHandlesData.Any(frud => frud.ActiveErrorOrWarning))
            {
                allHandlesData?.Clear();
                allHandlesData = null;
            }

            if (allMemData != null && !allMemData.Any(frud => frud.ActiveErrorOrWarning))
            {
                allMemData?.Clear();
                allMemData = null;
            }

            if (allActiveTcpPortData != null && !allActiveTcpPortData.Any(frud => frud.ActiveErrorOrWarning))
            {
                allActiveTcpPortData?.Clear();
                allActiveTcpPortData = null;
            }
        }
    }
}
