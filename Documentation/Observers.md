# Observers

Observers are low-impact, long-lived objects that perform specialied monitoring and reporting activities. Observers monitor and report, but they aren't designed to take action. Observers generally monitor appliations through their side effects on the node, like resource usage, but do not actually communicate with the applications. Observers report to SF Event Store (viewable through SFX) in warning and error states, and can use built-in AppInsights support to report there as well.  

### Note: All of the observers that collect resource usage data can also emit telemetry: [EventSource ETW](ETW.md) and either LogAnalytics or ApplicationInsights diagnostic service calls. 

> AppInsights or LogAnalytics telemetry can be configured in `Settings.xml` by providing your related authorization/identity information (keys). You must enable ObserverManagerEnableTelemetryProvider app parameter in AppplicationManifest.xml, which you can also enable/disable with versionless
> parameter-only application upgrades.

### Logging

Each Observer instance logs to a directory of the same name. You can configure the base directory of the output and log verbosity level (verbose or not). If you enable telemetry and provide ApplicationInsights/LogAnalytics settings, then you will also see the output in your Azure analytics queries. Each observer has configuration settings in FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml. AppObserver and NetworkObserver house their runtime config settings (error/warning thresholds) in json files located in FabricObserver\PackageRoot\Config folder.  

### Emiting Errors

Service Fabric Error Health Events can block upgrades and other important Fabric runtime operations. Error thresholds should be set such that putting the cluster in an emergency state incurs less cost than allowing the state to continue. For this reason, Fabric Observer by default ***treats Errors as Warnings***.  However if your cluster health policy is to [ConsiderWarningAsError](https://docs.microsoft.com/en-us/azure/service-fabric/service-fabric-health-introduction#cluster-health-policy), FabricObserver has a ***high risk of putting your cluster in an error state***. Proceed with caution.  

## ObserverManager Configuration

ObserverManager is the monitoring entry point for FabricObserver. That is, it manages all enabled observers, processes error information (when an observer fails for some reason), and much more. You can configure ObserverManager with settings housed in both Settings.xml and ApplicationManifest.xml. The latter enables versionless, parameter-only application updgrades of key settings for ObserverManager. 
See [Settings.xml](https://github.com/microsoft/service-fabric-observer/blob/main/FabricObserver/PackageRoot/Config/Settings.xml) for the definitions (with detailed comments) of the following Application Parameters housed in ApplicationManifest.xml:  

```XML
    <!-- ObserverManager Configuration -->
    <Parameter Name="ObserverManagerObserverLoopSleepTimeSeconds" DefaultValue="30" />
    <Parameter Name="ObserverManagerObserverExecutionTimeout" DefaultValue="3600" />
    <Parameter Name="ObserverManagerEnableVerboseLogging" DefaultValue="false" />
    <Parameter Name="ObserverManagerEnableETWProvider" DefaultValue="true" />
    <Parameter Name="ObserverManagerEnableTelemetryProvider" DefaultValue="true" />
    <Parameter Name="ObserverManagerEnableOperationalFOTelemetry" DefaultValue="true" />
    <Parameter Name="ObserverManagerObserverFailureHealthStateLevel" DefaultValue="Warning" />
    <Parameter Name="ObserverLogPath" DefaultValue="fabric_observer_logs" />

    ...

    <Section Name="ObserverManagerConfiguration">
        <Parameter Name="ObserverLoopSleepTimeSeconds" Value="[ObserverManagerObserverLoopSleepTimeSeconds]" />
        <Parameter Name="ObserverExecutionTimeout" Value="[ObserverManagerObserverExecutionTimeout]" />
        <Parameter Name="EnableVerboseLogging" Value="[ObserverManagerEnableVerboseLogging]" />
        <Parameter Name="EnableETWProvider" Value="[ObserverManagerEnableETWProvider]" />
        <Parameter Name="EnableTelemetryProvider" Value="[ObserverManagerEnableTelemetryProvider]" />
        <Parameter Name="EnableFabricObserverOperationalTelemetry" Value="[ObserverManagerEnableOperationalFOTelemetry]" />
        <Parameter Name="ObserverFailureHealthStateLevel" Value="[ObserverManagerObserverFailureHealthStateLevel]" />
        <Parameter Name="ObserverLogPath" Value="[ObserverLogPath]" />
    </Section>
```

The top section above is the list of Application Parameters that you can modify while FabricObserver is deployed and running. This pattern is supported by all observers, minus the threshold settings for AppObserver, ContainerObserver, and NetworkObserver as these settings are held in JSON files, not XML. 

**Note:** By default, if an observer fails for some reason like AppObserver not being able to access process information for some service target because the service is running at a higher user level than FabricObserver itself, FabricObserver will be put into Warning by default. You can change this behavior by setting ```ObserverManagerObserverFailureHealthStateLevel``` to your preference (Error, Warning, Ok, None). This can be very useful if, for example, you have configured AppObserver to monitor all deployed application services on Windows and one or more of these services are running as Admin or System user. You will immediately see that you forgot to account for this and then mitigate the problem by re-deploying FabricObserver to run as LocalSystem (System user) on Windows. That is one simple example. There are plenty more scenarios where this feature can be helpful.

## [How to implement a new Observer](#writing-a-custom-observer)
## Currently Implemented Observers  

| Observer | Description |
| :--- | :--- |
| [AppObserver](#appobserver) | Monitors service process CPU, Memory, TCP Port, Thread, and Handle usage. Alerts when user-supplied thresholds are breached. |
| [AzureStorageUploadObserver](#azurestorageuploadobserver) | Runs periodically (do set its RunInterval setting) and will upload dmp files that AppObserver creates when you set dumpProcessOnError to true. It will clean up files after successful upload. |
| [CertificateObserver](#certificateobserver) | Monitors the expiration date of the cluster certificate and any other certificates provided by the user. Warns when close to expiration. |
| [ContainerObserver](#containerobserver) | Monitors container CPU and Memory use. Alerts when user-supplied thresholds are breached. |
| [DiskObserver](#diskobserver) | Monitors logical disk space conusumption and IO queue wait time. Alerts when user-supplied thresholds are breached. |
| [FabricSystemObserver](#fabricsystemobserver) | Monitors CPU, Memory, TCP Port, Thread, and Handle usage for Service Fabric System service processes. Alerts when user-supplied thresholds are breached. |
| [NetworkObserver](#networkobserver) | Monitors outbound connection state for user-supplied endpoints (hostname/port pairs). This observer checks that the node can reach specific endpoints (over both http (e.g., REST) and direct tcp socket connections). |
| [NodeObserver](#nodeobserver) | Monitors VM level resource usage across CPU, Memory, firewall rules, static and dynamic ports (aka ephemeral ports), File Handles (Linux). |
| [OSObserver](#osobserver) | Records basic OS properties across OS version, OS health status, physical/virtual memory use, number of running processes, number of active TCP ports (active/ephemeral), number of enabled firewall rules (Windows), list of recent patches/hotfixes (with hyper-links to related KB articles). |

# Observers - What they do and how to configure them  

You can quickly get started by reading [this](/Documentation/Using.md).  

***Note: All observers that monitor various resources output serialized instances of TelemetryData type (JSON). This JSON string is set as the Description property of a Health Event. This is done for a few reasons: Telemetry support and for consuming services that need to deserialize the data to inform some related workflow. In later versions of SF, SFX will display only the textual pieces of this serialized object instance, making it easier to read in SFX's Details view.***

```
The vast majority of settings for any observer are provided in ApplicationManifest.xml 
as required overridden parameters. For AppObserver and NetworkObserver, 
all thresholds/settings are housed in json files, not XML.
For every other observer, it's XML as per usual.
```  

## AppObserver  
Observer that monitors CPU, Memory, TCP Port, Thread, and Handle usage for Service Fabric user service processes and the child processes they spawn, if enabled. If a service process creates child processes, then these processes will be monitored and their summed resource usage for some metric you are observing will be applied to the parent process (added) and a threshold breach will be determined based on the sum of children and parent resource usage.
This observer will alert (SF Health event) when user-supplied thresholds are reached. **Please note that this observer should not be used to monitor docker container applications. It is not designed for this task. Instead, please use [ContainerObserver](#containerobserver), which is designed specifically for container monitoring**. 

***Important: By default, FabricObserver runs as an unprivileged user (NetworkUser on Windows and sfappsuser on Linux). If you want to monitor services that are running as System user (or Admin user) on Windows, you must run FabricObserver as System user.***  

***For Linux, there is no need to run as root, so do not do that.***    

You configure FO's user account type in ApplicationManifest.xml (only Windows would need this. FO's build script automatically inserts this setting for Linux target, and for running Setup scripts only (not Code package binaries), to support FO's Linux Capabilities implementation):  

```XML
    </ConfigOverrides>
    <!-- Uncomment below to run FO as System user. Also uncomment the Principals node below. -->
    <!--<Policies>
      <RunAsPolicy CodePackageRef="Code" UserRef="SystemUser" />
    </Policies>-->
  </ServiceManifestImport>
  <DefaultServices>
    <!-- The section below creates instances of service types, when an instance of this 
         application type is created. You can also create one or more instances of service type using the 
         ServiceFabric PowerShell module.
         The attribute ServiceTypeName below must match the name defined in the imported ServiceManifest.xml file. -->
    <Service Name="FabricObserver" ServicePackageActivationMode="ExclusiveProcess">
      <StatelessService ServiceTypeName="FabricObserverType" InstanceCount="[FabricObserver_InstanceCount]">
        <SingletonPartition />
      </StatelessService>
    </Service>
  </DefaultServices>
  <!-- Uncomment below to run FO as System user. Also uncomment the Policies node above. -->
  <!--<Principals>
    <Users>
      <User Name="SystemUser" AccountType="LocalSystem" />
    </Users>
  </Principals>-->
```

### A note on child process monitoring

AppObserver (FO version >= 3.1.15) will automatically monitor up to 50 child processes spawned by your primary service process (50 is extreme. You should not design services that own that many descendant processes..). If your services launch child processes, then AppObserver will automatically monitor them for the same metrics and thresholds you supply for the containing Application. 
Their culmative impact on some monitored metric will be added to that of the parent process (your service process) and this combined (sum) value will be used to determine health state based on supplied threshold for the related metric.  

You can disable this feature (you shouldn't if you **do** launch child processes from your service and they run for a while or for the lifetime of your service and compute (use resources)) by setting AppObserverEnableChildProcessMonitoring to false.
For telemetry, you can control how many offspring are present in the event data by setting AppObserverMaxChildProcTelemetryDataCount (default is 5). Both of these settings are located in ApplicationManifest.xml.
The AppObserverMaxChildProcTelemetryDataCount setting determines the size of the list used in family tree process data telemetry transmission, which corresponds to the size of the telemetry data event. You should keep this below 10. AppObserver will order the list of ChildProcessInfo (a member of ChildProcessTelemetryData) by resoure usage value, from highest to lowest. 

In the vast majority of cases, your services are not going to launch 50 descendant processes, but FO is designed to support such an extreme edge case scenario, which frankly should not be in your service design playbook. Also note that if you do spawn a lot of child processes and 
you have AppObserverMonitorDuration set to, say, 10 seconds, then you will be running AppObserver for (n + 1) * 10 seconds, where n is total number of processes related to a service instance (n = child procs, +1 to account for the parent..) for every service that launches children.
If your Service A spawns 20 descendants, then that would be 21 * 10 = 210 seconds of monitoring time. If Service B launches 10 descendants, then add 110 seconds to that. Etc... Please keep this in mind as you design your configuration. And, please don't design services that launch 50 descendant processes. Why do that?

Finally, ***if you do not launch child processes from your services please disable this feature*** by setting ```AppObserverEnableChildProcessMonitoring``` to false in ApplicationManifest.xml. This is important because AppObserver will run code that checks to see if some process has children. If you know this is not the case, then save electrons and disable the feature.

### A note on concurrent process monitoring

AppObserver, by default, will monitor and report on services using concurrent Tasks if FO is running on capable CPU(s). 

You can turn this feature on/off by setting ```AppObserverEnableConcurrentMonitoring``` in ApplicationManifest.xml. Further, you can control "how much" parallelism you can handle (which means, really, how many threads can be used to host tasks).
You set this with ```AppObserverMaxConcurrentTasks``` in ApplicationManifest.xml. The default value for ```AppObserverMaxConcurrentTasks``` is 25.
You can set this to -1 (unlimited), or some integer value that makes sense based on how many services AppObserver is monitoring. **The impact on CPU usage with default parallelization settings is minimal**.
Please test and choose a value that suits your needs or simply leave AppObserverMaxConcurrentTasks unset and go with the default (25). 

### Input
JSON config file supplied by user, stored in PackageRoot\Config folder. This configuration is composed of JSON array
objects which constitute Service Fabric Apps (identified by service URI's). Users supply Error/Warning thresholds for CPU use, Memory use and Disk
IO, ports. Memory values are supplied as number of megabytes or percentage use. CPU and Disk Space values are provided as percentages (integers: so, 80 = 80%) 
**Please note that you can omit any of these properties. You can also supply 0 as the value, which means that threshold
will be ignored (they are not omitted below so you can see what a fully specified object looks like). 
We recommend you omit all Error thresholds until you become more comfortable with the behavior of your services and the side effects they have on machine resources**.

Default AppObserver JSON config file located in **PackageRoot\\Config** folder (AppObserver.config.json). This configuration applies
to all Service Fabric user (non-System) service processes running on the machine.
```JSON
[
  {
    "targetApp": "*",
    "cpuWarningLimitPercent": 85,
    "memoryWarningLimitMb": 1024,
    "networkWarningEphemeralPorts": 7000,
    "networkWarningEphemeralPortsPercent": 30,
    "warningHandleCount": 10000,
    "warningThreadCount": 500,
    "warningPrivateBytesMb": 1280
  }
]
```
Settings descriptions: 

All settings are optional, ***except target OR targetType***, and can be omitted if you don't want to track. For process memory use, you can supply either MB values (a la 1024 for 1GB) for Working Set (Private) or percentage of total memory in use by process (as an integer, 1 - 100).

| Setting | Description |
| :--- | :--- |
| **targetApp** | App name to observe (either SF URI format or just the name. E.g., fabric:/FooApp or FooApp). Optional (Required if targetType not specified). | 
| **targetAppType** | ApplicationType name. FO will observe **all** app services belonging to it. Optional (Required if target not specified). | 
| **appExcludeList** | This setting is only useful when targetApp is set to "*" or "All". A comma-separated list of app names to ***exclude from observation***. Just omit the object or set value to "" to mean ***include all***. (excluding all does not make sense) | 
| **appIncludeList** | This setting is only useful when targetApp is set to "*" or "All". A comma-separated list of app names to ***include in observation***. Just omit the object or set value to "" to mean ***include all***.  | 
| **serviceExcludeList** | A comma-separated list of service names (***not URI format***, just the service name as we already know the app name URI) to ***exclude from observation***. Just omit the object or set value to "" to mean ***include all***. (excluding all does not make sense) |
| **serviceIncludeList** | A comma-separated list of service names (***not URI format***, just the service name as we already know the app name URI) to ***include in observation***. Just omit the object or set value to "" to mean ***include all***. |  
| **memoryErrorLimitMb** | Maximum service process Working Set (RAM, physical memory) in Megabytes that should generate an Error. Default type is Private Working Set. You can change this in ApplicationManifest.xml if you want full Working Set (private + shared physical memory). |  
| **memoryWarningLimitMb**| Minimum service process Working Set (RAM, physical memory) in Megabytes that should generate a Warning. Default type is Private Working Set. You can change this in ApplicationManifest.xml if you want full Working Set (private + shared physical memory). | 
| **memoryErrorLimitPercent** | Maximum percentage of total physical memory (RAM) used by a service process that should generate an Error. |  
| **memoryWarningLimitPercent** | Minimum percentage of total physical memory (RAM) used by a service process that should generate a Warning. | 
| **cpuErrorLimitPercent** | Maximum CPU usage by a service process as a percentage of total CPU that should generate an Error. |
| **cpuWarningLimitPercent** | Minimum CPU usage by a service process as a percentage of total CPU that should generate a Warning. |
| **dumpProcessOnError** | Instructs FabricObserver to dump a user service process when any related ***Error*** threshold has been reached. This is only supported on Windows today. |  
| **dumpProcessOnWarning** | Instructs FabricObserver to dump a user service process when any related ***Warning*** threshold has been reached. This is only supported on Windows today. |  
| **networkErrorActivePorts** | Maximum number of established TCP ports in use by service process that will generate an Error. |
| **networkWarningActivePorts** | Minimum number of established TCP ports in use by service process that will generate a Warning. |
| **networkErrorEphemeralPorts** | Maximum number of ephemeral TCP ports (within a dynamic port range) in use by service process that will generate an Error. |
| **networkWarningEphemeralPorts** | Minimum number of established TCP ports (within a dynamic port range) in use by service process that will generate a Warning. | 
| **networkErrorEphemeralPortsPercent** | Maximum percentage of ephemeral TCP ports (within a dynamic port range) in use by service process that will generate an Error. |
| **networkWarningEphemeralPortsPercent** | Minimum percentage of established TCP ports (within a dynamic port range) in use by service process that will generate a Warning. |   
| **errorOpenFileHandles** | **Legacy (supported)**. Maximum number of handles opened by a service process that will generate an Error. This is a legacy metric name and is equivalent to errorHandleCount, a more accurate metric name. |  
| **warningOpenFileHandles** | **Legacy (supported)**. Minimum number of handles opened by a service process that will generate a Warning. This is a legacy metric name and is equivalent to warningHandleCount, a more accurate metric name. |  
| **errorHandleCount** | Maximum number of handles opened by a service process that will generate an Error. |  
| **warningHandleCount** | Minimum number of handles opened by service process that will generate a Warning. |  
| **errorThreadCount** | Maximum number of threads in use by an service process that will generate an Error. |  
| **warningThreadCount** | Minimum number of threads in use by service process that will generate a Warning. |  
| **errorPrivateBytesMb** | Windows-only. Maximum amount of Private Bytes (or Commit Charge) for a service process in megabytes that will generate an Error. Think of this as the total amount of private memory that the memory manager has committed for your service. |  
| **warningPrivateBytesMb** | Windows-only. Minimum amount of Private Bytes (or Commit Charge) for a service process in megabytes that will generate a Warning. Think of this as the total amount of private memory that the memory manager has committed for your service. | 
| **errorPrivateBytesPercent** | Windows-only. Maximum percentage of Private Bytes (or Commit Charge) for a service process that will generate an Error. Think of this as the total amount of private memory that the memory manager has committed for your service as a percentage of available commit. |  
| **warningPrivateBytesPercent** | Windows-only. Minimum percentage of Private Bytes (or Commit Charge) for a service process that will generate a Warning. Think of this as the total amount of private memory that the memory manager has committed for your service as a percentage of available commit. | 
| **warningRGMemoryLimitPercent** | Windows-only. Percentage of Memory Resource Governance 'MemoryInMB/Limit' value currently in use that will trigger a Warning for a service. You can override the default value (90%) for specific or all services like any other AppObserver threshold. Note: if you supply a decimal value (e.g., 0.70) it is converted to a double (so, it is taken to mean 70.0, so 70%). |  
| **warningRGCpuLimitPercent** | Windows-only. Percentage of CPU Resource Governance: Percentage of CPU usage for specified core limit. This will trigger a Warning for a service. You can override the default value (90%) for specific or all services like any other AppObserver threshold. Note: if you supply a decimal value (e.g., 0.70) it is converted to a double (so, it is taken to mean 70.0, so 70%). |  

**A note on Private Working Set monitoring on Windows** This measurement employs a PerformanceCounter object created for each process (instance) being monitored. PerformanceCounter is an expensive and inefficent object. It is recommended that you monitor **Private Bytes** to track memory usage of your service processes as this employs an much more efficient mechanism that has little impact on memory and CPU usage.
If you do want to track Working Set in addition to Private Bytes, then set ```AppObserverMonitorPrivateWorkingSet``` to false and this will use a very efficient way to compute Shared + Private Working Set.

**Output** Local log text(Error/Warning/Info), Service entity Health Reports (Error/Warning/Ok), ETW (EventSource), Telemetry (AppInsights/LogAnalytics).

AppObserver also supports non-JSON parameters for configuration unrelated to thresholds. Like all observers these settings are located in ApplicationManifest.xml to support versionless configuration updates via application upgrade. 

#### Non-json settings set in ApplicationManifest.xml (Default Configuration)

```XML
    <!-- AppObserver -->
    <Parameter Name="AppObserverClusterOperationTimeoutSeconds" DefaultValue="120" />
    <!-- Note: CircularBufferCollection is not thread safe for writes. If you enable this AND enable concurrent monitoring, a ConcurrentQueue will be used. -->
    <Parameter Name="AppObserverUseCircularBuffer" DefaultValue="false" />
    <!-- Optional-If UseCircularBuffer = true -->
    <Parameter Name="AppObserverResourceUsageDataCapacity" DefaultValue="" />
    <!-- Configuration file name. -->
    <Parameter Name="AppObserverConfigurationFile" DefaultValue="AppObserver.config.json" />
    <!-- Process family tree monitoring: AppObserver monitors the resource usage by child processes of target services.
         NOTE: If you already know that your target services do *not* spawn child processes, then you should not enable this feature. -->
    <Parameter Name="AppObserverEnableChildProcessMonitoring" DefaultValue="false" />
    <!-- The maximum number of child process data items to include in a sorted list of top n consumers for some metric, where n is the value of this setting. -->
    <Parameter Name="AppObserverMaxChildProcTelemetryDataCount" DefaultValue="5" />
    <!-- Service process dumps (dumpProcessOnError/dumpProcessOnWarning setting).
         You need to set AppObserverEnableProcessDumps setting to true here AND set dumpProcessOnError or dumpProcessOnWarning to true in AppObserver.config.json
         if you want AppObserver to dump service processes when an Error or Warning threshold has been breached for some observed metric (e.g., memoryErrorLimitPercent/memoryWarningLimitPercent). -->
    <Parameter Name="AppObserverEnableProcessDumps" DefaultValue="false" />
    <!-- Supported values are: Mini, MiniPlus, Full. -->
    <Parameter Name="AppObserverProcessDumpType" DefaultValue="MiniPlus" />
    <!-- Max number of dumps to generate per service, per observed metric, within a supplied TimeSpan window. See AppObserverMaxDumpsTimeWindow. -->
    <Parameter Name="AppObserverMaxProcessDumps" DefaultValue="3" />
    <!-- Time window in which max dumps per process, per observed metric can occur. See AppObserverMaxProcessDumps. -->
    <Parameter Name="AppObserverMaxDumpsTimeWindow" DefaultValue="04:00:00" />
    <!-- Concurrency/Parallelism Support.
         Note: This will only add real value (substantial) on machines with capable hardware (>= 4 logical processors). FO will not attempt to do anything concurrently if
         the number of logical processors is less than 4, even if this is set to true.
         You can control the level of concurrency by setting the AppObserverMaxConcurrentTasks setting. -->
    <Parameter Name="AppObserverEnableConcurrentMonitoring" DefaultValue="true" />
    <!-- Set this to -1 or some positive integer that roughly maps to max number of threads to use. -1 means that the .net runtime will use as many managed threadpool threads as possible. 
         Default value is 25. Please read the related documentation for more information on MaxDegreeOfParallelism. This setting is provided here so you can dial thread usage up and down based on your needs, if you need to.
         See https://learn.microsoft.com/dotnet/api/system.threading.tasks.paralleloptions.maxdegreeofparallelism?view=net-6.0 -->
    <Parameter Name="AppObserverMaxConcurrentTasks" DefaultValue="25" />
    <!-- *Windows-only*: KVS LVID Usage Monitoring.
         NOTE: If you monitor stateful Actor services, then you should set this to true. -->
    <Parameter Name="AppObserverEnableKvsLvidMonitoring" DefaultValue="false" />
    <!-- *Windows-only*: Process Working Set type for AppObserver to monitor.
         Note: You should consider monitoring process commit (warningPrivateBytesMb threshold setting) instead of Private Working Set for tracking potential leaks on Windows. 
         The Private Bytes measurement code is extremely fast and CPU-efficient on Windows. Monitoring Private Working Set on Windows requires PerformanceCounter, which is an expensive .net object.
		 *Windows* Recommendation: Disable this, employ warningPrivateBytesMb/errorPrivateBytesMb in AppObserver.config.json file for tracking process memory usage on Windows. -->
    <Parameter Name="AppObserverMonitorPrivateWorkingSet" DefaultValue="false" />
    <!-- *Windows-only*. AppObserver can monitor services with Service Fabric Resource Governance limits set (CPU and Memory) and put related services into Warning if they reach 90% of the specified limit. 
         If you do set Memory or CPU Resource Governance limits for your service code packages, then you should enable this feature. -->
    <Parameter Name="AppObserverMonitorResourceGovernanceLimits" DefaultValue="false" />
```

Example AppObserver Output (Warning - Ephemeral Ports Usage):  

![alt text](/Documentation/Images/AppObsWarn.png "AppObserver Warning output example.")  

AppObserver also optionally outputs CSV files for each app containing all resource usage data across iterations for use in analysis. Included are Average and Peak measurements. You can turn this on/off in ApplicationManifest.xml. See Settings.xml where there are comments explaining the feature further.  
  
AppObserver error/warning thresholds are user-supplied-only and bound to specific service instances (processes) as dictated by the user,
as explained above. Like FabricSystemObserver, all data is stored in in-memory data structures for the lifetime of the run (for example, 60 seconds at 5 second intervals). Like all observers, the last thing this observer does is call its *ReportAsync*, which will then determine the health state based on accumulated data across resources, send a Warning if necessary (clear an existing warning if necessary), then clean out the in-memory data structures to limit impact on the system over time. So, each iteration of this observer accumulates *temporary* data for use in health determination.
  
This observer can also monitor the FabricObserver service itself across CPU/Mem/FileHandles/Ports/Threads.  

## AzureStorageUploadObserver 
Runs periodically (you can set its RunInterval setting, just like any observer) and will upload dmp files of user services that AppObserver creates when you set dumpProcessOnError to true and supply Error thresholds in AppObserver configuration. The files are compressed and uploaded to a specified Azure Storage Account (blob storage only) and blob container name (default is fodumps, but you can configure this). It will delete dmp files from local storage after each successful upload. 
For authentication to Azure Storage, Storage Connection String and Account Name/Account Key pair are supported today. Since there is currently only support for Windows process dumps (by AppObserver only), there is no need to run this Observer on Linux (today..).
The dumps created are *not* crash dumps, they are live dumps of a process's memory, handles, threads. The target process will not be killed or blow up in memory size. The offending service will keep on doing what it's doing wrong.
By default, the dmp files are MiniPlus mini dumps, so they will be roughly as large as the target process's private working set and stack. You can set to Mini (similar size) or 
Full, which is much larger. You probably do not need to create Full dumps in most cases. 

Note that this feature does not apply to the FabricObserver process, even if specifying a configuration setting to do so. FabricObserver will not dump itself.  

#### Compression  

All dmp files are compressed to zip files before uploading to your storage account over the Internet. By default, the compression level is set to Optimal, which means the files will be compressed to the *smallest size possible*. You can change this in configuration to Fastest or NoCompression. We do not recommend NoCompression. The choice is yours to own.

**Optimal**: Best compression, uses more CPU for a short duration (this should not be an issue nor a deciding factor).  

**Fastest**: Fastest compression, uses less CPU than Optimal, produces non-optimally compressed files.  

**NoCompression**: Don't compress. This is NOT recommended. You should reduce the size of these files before uploading them to your cloud storage (blob) container.

A note on resource usage: This feature is intended for the exceptional case - when your app service is truly doing something really wrong (like leaking memory, ports, handles). Make sure that you set your Error thresholds to meaningfully high values. Internally, FabricObserver will only dump a configured amount of times in a specified time window per service, per observed metric. The idea
is to not eat your local disk space and use up too much CPU for too long. Please be mindful of how you utilize this **debugging** feature. It is best to enable it in Test and Staging clusters to find the egregious bugs in your service code *before* you ship your services to production clusters. 

#### Encrypting your secrets  (Optional, but recommended)

It is very important that you generate an encrypted Connection String or Account Key string in a supported way: Use Service Fabric's Invoke-ServiceFabricEncryptText PowerShell cmdlet with your Cluster thumbprint or cert name/location. 
Please see the [related documentation with samples](https://docs.microsoft.com/en-us/powershell/module/servicefabric/invoke-servicefabricencrypttext?view=azureservicefabricps). It is really easy to do! Non-encrypted strings are supported, but we do not recommend using them. The decision is yours to own.

Also, since FO runs as NetworkUser by default, you will need to supply a SecretsCertificate setting in ApplicationManifest.xml which will enable FO to run as unprivileged user and access your private key for the cert installed on the local machine.
This section is already present in ApplicationManifest.xml. Just add the thumbprint you used to create your encrypted connection string or account key and a friendly name for the cert.
If you do not do this, then you will need to run FO as System in order for decryption of your connection string to work and for blob uploads to succeed. 

***As always, if you want to monitor user services on Windows that are running as System user (or Admin user), you must run FabricObserver as System user on Windows.*** In the FO-as-System-user-on-Windows case, you do not need to set SecretsCertificate.  

SecretsCertificate configuration in ApplicationManifest.xml:

```XML
...
  <!-- This is important to set so that FO can run as NetworkUser if you want to upload AppObserver user service process dumps to your Azure Storage account. 
       Just supply the same thumbprint you used to encrypt your ConnectionString. Of course, the certificate's private key will also need to be installed on the VM.
       You should already understand this. -->
  <Certificates>
    <SecretsCertificate X509FindValue="[your thumbprint]" Name="[cert friendly name]" />
  </Certificates>
</ApplicationManifest>
```

Example AzureStorageUploadObserver configuration in ApplicationManifest.xml:  

```XML
    <!-- AzureStorageUploadObserver -->
    <Parameter Name="AzureStorageUploadObserverBlobContainerName" DefaultValue="fodumps" />
    <!-- This should be an encrypted string. It is not a good idea to place an unencrypted ConnectionString in an XML file.. FO will ignore unencrypted strings. 
         Use Invoke-ServiceFabricEncryptText PowerShell API to generate the encrypted string to use here. 
         The encrypted string must NOT contain any line breaks or spaces. This is very important. If you copy the the string incorrectly, then FO will not upload dumps.
         See https://docs.microsoft.com/en-us/powershell/module/servicefabric/invoke-servicefabricencrypttext?view=azureservicefabricps  
         for details. Follow the directions in the sample for creating an encryted secret. Note the use of thumbprint in the cmd. -->
    <Parameter Name="AzureStorageUploadObserverStorageConnectionString" DefaultValue="" />
    <!-- OR -->
    <Parameter Name="AzureStorageUploadObserverStorageAccountName" DefaultValue="mystorageacctname" />
    <!-- This should be an encrypted string. Make sure there are no line breaks and no blank spaces between any characters. This is very important. -->
    <Parameter Name="AzureStorageUploadObserverStorageAccountKey" DefaultValue="XIICjAYJKoZIhvcNAQcDoXXCfTCCAnkCAQAxggGCMIIBfgIRESBmME8xCzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xIDAeBgNVBAMTF01pY3Jvc29mdCBSU0EgVExTIENBIDAxAhNrAABpknf9Fp92KoLyCCCCGmSMA0GCSqGSIb3DQEBBzAABIIBAB5/q1AKccctjPq/dM/jTz1eZfAsyhkJFLfs12X2aNYHqJSVPm02A8XUjS0RjKQ5NKd40AEiEGjYHWWQCXy/qnTOF5ViwyxSZmizlpthhVzEU/rPtgfJy/KXCfFsOwjuIy2npnLpcVcjK2u010tcp+BWMBSC2M3adrgMNDzxINJZbmul/0oxC16O5O4grIbqhktq3FG/iCiBHOo3irkwZos4gPslcg7SFYXYpZjbTxbippzNRpPDXIqD2KspIfp7RGjtmJYU/d5mlB/ratW+NxVGXz8B9CQs7SaDyxwcSK/97gZAn9JsnOYr8pxrM2EA5dt3apT9oy779MImQ61PGPMwge0GCSqGSIb3DQEHATAdBglghkgBZQMEASoEEJSJgAwPRYXujvanWupBZpSAgcAwDfo7qv7n1cWvnq1EEZt5btgRr8QDm0bvrgg2gRxOnxwGAyPmYuLGDna4M0JAcdmQ3V1t7x0sd0AJRLDfYd8tH0uXVD7jdPFkAnN7EvdpVG/u/HEwEkyDAUWbC//mW+waCUpiHvOcIkxlV7mRAVNYowHeOVSKlQcnfjKaNorMMWS8AoCAhDsvBuUUgPlBcnR7zBXjPe7KblcS5l5xxSs4FKi8JkP1uQh18/9QQOn4Xy41TtNe5RP2ExUBiz7d5fg=" />
    <!-- Zip file compression level to use: Optimal, Fastest or NoCompression. Default is Optimal if this is not set. -->
    <Parameter Name="AzureStorageUploadObserverZipFileCompressionLevel" DefaultValue="Optimal" />
```
  
You do not need to encrypt your keys, but that is up to you to decide. We recommend that you do. If you do not want to, then:

In Settings.xml you must change IsEncryted to false:

```Xml
 <Section Name="AzureStorageUploadObserverConfiguration">
    <!-- For Authenticating to your Storage Account, you can either provide a Connection String OR an Account Name and Account Key. 
         NOTE: If you do not plan on encrypting your account secrets, then set IsEncrypted to false both here and in 
         the AzureStorageUploadObserverConfiguration Section in ApplicationManifest.xml. -->
    <Parameter Name="AzureStorageConnectionString" Value="" IsEncrypted="false" MustOverride="true" />
    ... 
    <Parameter Name="AzureStorageAccountKey" Value="" IsEncrypted="false" MustOverride="true" />
 </Section>
```

In ApplicationManifest.xml you must change IsEncrypted to false:  

```XML
 <Section Name="AzureStorageUploadObserverConfiguration">
    ...
    <Parameter Name="AzureStorageConnectionString" Value="[AzureStorageUploadObserverStorageConnectionString]" IsEncrypted="false" />
    <!-- OR use Account Name/Account Key pair if NOT using Connection String.. -->
    <Parameter Name="AzureStorageAccountName" Value="[AzureStorageUploadObserverStorageAccountName]" />
    <Parameter Name="AzureStorageAccountKey" Value="[AzureStorageUploadObserverStorageAccountKey]" IsEncrypted="false" />
    ...
 </Section>
```

## CertificateObserver
Monitors the expiration date of the cluster certificate and any other certificates provided by the user. 

### Notes
- If the cluster is unsecured or uses Windows security, the observer will not monitor the cluster security but will still monitor user-provided certificates. 
- The user can provide a list of thumbprints and/or common names of certificates they use on the VM, and the Observer will emit warnings if they pass the expiration Warning threshold. 
- In the case of common-name, CertificateObserver will monitor the ***newest*** valid certificate with that common name, whether it is the cluster certificate or another certificate.
- A user should provide either the thumbprint or the commonname of any certifcate they want monitored, not both, as this will lead to redudant alerts.

### Configuration
```xml
  <Section Name="CertificateObserverConfiguration">
    <Parameter Name="Enabled" Value="" MustOverride="true" />
    <Parameter Name="EnableTelemetry" Value="" MustOverride="true" />
    <Parameter Name="EnableVerboseLogging" Value="" MustOverride="true" />
    <!-- Optional: How often does the observer run? For example, CertificateObserver's RunInterval is set to 1 day 
         below, which means it won't run more than once a day (where day = 24 hours.). All observers support a RunInterval parameter.
         Just add a Parameter like below to any of the observers' config sections when you want this level of run control.
         Format: Day(s).Hours:Minutes:Seconds e.g., 1.00:00:00 = 1 day. -->
    <Parameter Name="RunInterval" Value="" MustOverride="true" />
    <!-- Cluster and App Certs Warning Start (Days) -> DefaultValue is 42 -->
    <Parameter Name="DaysUntilClusterExpiryWarningThreshold" Value="" MustOverride="true" />
    <Parameter Name="DaysUntilAppExpiryWarningThreshold" Value="" MustOverride="true" />
    <!-- Required: These are JSON-style lists of strings, empty should be "[]", full should be "['thumb1', 'thumb2']" -->
    <Parameter Name="AppCertThumbprintsToObserve" Value="" MustOverride="true" />
```

**Output**: Log text(Error/Warning), Node Level Service Fabric Health Reports (Ok/Warning), structured telemetry (ApplicationInsights, LogAnalytics), ETW, optional HTML output for FO Web API service. 


## ContainerObserver 
Monitors CPU and Memory use of Service Fabric containerized (docker) services.  

**In order for ContainerObserver to function properly on Windows, FabricObserver must be configured to run as Admin or System user.** This is not the case for Linux deployments.

**Version 3.1.18 introduced support for concurrent docker stats data parsing and reporting by ContainerObserver**. You can enable/disable this feature by setting the boolean value for ContainerObserverEnableConcurrentMonitoring. Note that this is disabled by default.
If your compute configuration includes multiple CPUs (logical processors >= 4) and you monitor several containerized services, then you should consider enabling this capability as it will significantly decrease the time it takes ContainerObserver to complete monitoring/reporting.
If you do not have a capable CPU configuration, then enabling concurrent monitoring will not do anything.


### Configuration 

XML:  

Settings.xml  

```XML
  <!-- NOTE: FabricObserver must run as System or Admin on *Windows* in order to run ContainerObserver successfully. This is not the case for Linux. -->
  <Section Name="ContainerObserverConfiguration">
     <Parameter Name="Enabled" Value="" MustOverride="true" />
     <!-- Optional: Whether or not ContainerObserver should try to monitor service processes concurrently.
          This can significantly decrease the amount of time it takes ContainerObserver to monitor several containerized applications. 
          Note that this feature is only useful on capable CPU configurations (>= 4 logical processors). -->
     <Parameter Name="EnableConcurrentMonitoring" Value="" MustOverride="true" />
     <Parameter Name="MaxConcurrentTasks" Value="" MustOverride="true" />
     <Parameter Name="ClusterOperationTimeoutSeconds" Value="120" />
     <Parameter Name="EnableCSVDataLogging" Value="" MustOverride="true" />
     <Parameter Name="EnableEtw" Value="" MustOverride="true"/>
     <Parameter Name="EnableTelemetry" Value="" MustOverride="true" />
     <Parameter Name="EnableVerboseLogging" Value="" MustOverride="true" />
     <Parameter Name="RunInterval" Value="" MustOverride="true" />
     <Parameter Name="ConfigFileName" Value="" MustOverride="true" />
  </Section>
```
Overridable XML settings are locatated in ApplicationManifest.xml, as always.

JSON: 

Configuration file supplied by user, stored in PackageRoot\\Config folder. 

Example JSON config file located in **PackageRoot\\Config** folder (ContainerObserver.config.json). This is an example of a configuration that applies
to all Service Fabric containerized services running on the virtual machine.
```JSON
[
  {
    "targetApp": "*",
    "cpuWarningLimitPercent": 60,
    "memoryWarningLimitMb": 1048
  }
]
```
Settings descriptions: 

All settings are optional, ***except targetApp***, and can be omitted if you don't want to track. For memory use thresholds, you must supply MB values (a la 1024 for 1GB).

| Setting | Description |
| :--- | :--- |
| **targetApp** | App URI string to observe. Required. | 
| **memoryErrorLimitMb** | Maximum container memory use set in Megabytes that should generate a Fabric Error. |  
| **memoryWarningLimitMb**| Minimum container memory set in Megabytes that should generate a Fabric Warning. |  
| **cpuErrorLimitPercent** | Maximum CPU percentage that should generate a Fabric Error. |
| **cpuWarningLimitPercent** | Minimum CPU percentage that should generate a Fabric Warning. |

**Output**: Log text(Error/Warning), Application Level Service Fabric Health Reports (Ok/Warning/Error), structured telemetry (ApplicationInsights, LogAnalytics), ETW, optional HTML output for FO Web API service. 

### Notes

**In order for ContainerObserver to function properly on Windows, FabricObserver must be configured to run as Admin or System user.** This is not the case for Linux deployments.

## DiskObserver
This observer monitors, records and analyzes storage disk information, including folders.
Depending upon configuration settings, it signals disk health status warnings (or OK state) for all logical disks it detects.

After DiskObserver logs basic disk information, it performs measurements on all logical disks across space usage (Consumption) and IO (Average Queue Length) and, optionally, any specified folder paths you supply in configuration. The data collected are used in ReportAsync to determine if a Warning shot should be fired based on user-supplied threshold settings housed in ApplicationManifest.xml. Note that you do not need to specify a threshold parameter that you don't plan you using. You can either omit the XML node or leave the value blank (or set to 0).

```xml
<Section Name="DiskObserverConfiguration">
    <Parameter Name="Enabled" Value="" MustOverride="true" />
    <Parameter Name="EnableTelemetry" Value="" MustOverride="true" />
    <Parameter Name="EnableEtw" Value="" MustOverride="true" />
    <Parameter Name="EnableVerboseLogging" Value="" MustOverride="true" />
    <Parameter Name="RunInterval" Value="" MustOverride="true" />
    <Parameter Name="DiskSpacePercentUsageWarningThreshold" Value="" MustOverride="true" />
    <Parameter Name="DiskSpacePercentUsageErrorThreshold" Value="" MustOverride="true" />
    <Parameter Name="AverageQueueLengthErrorThreshold" Value="" MustOverride="true" />
    <Parameter Name="AverageQueueLengthWarningThreshold" Value="" MustOverride="true" />
    <Parameter Name="EnableFolderSizeMonitoring" Value="" MustOverride="true" />
    <Parameter Name="FolderPathsErrorThresholdsMb" Value="" MustOverride="true" />
    <Parameter Name="FolderPathsWarningThresholdsMb" Value="" MustOverride="true" />
</Section>
```

For folder size monitoring (available in FO versions 3.1.24 and above), enable the feature and supply full path/threshold size (in MB) pairs in the following format: 

```"fullpath, threshold | fullpath1 threshold1 ..."``` 

in ApplicationManifest.xml:  

``` XML 
<Parameter Name="DiskObserverEnableFolderSizeMonitoring" DefaultValue="true" />
<Parameter Name="DiskObserverFolderPathsWarningThresholdsMb" DefaultValue="E:\SvcFab\Log\Traces, 15000 | C:\somefolder\foo, 500" />
```

**Output**: Log text(Error/Warning), Node Level Service Fabric Health Reports (Ok/Warning/Error), structured telemetry (ApplicationInsights, LogAnalytics), ETW, optional HTML output for FO Web API service.  

Example SFX Output (Warning - Disk Space Consumption):  

![alt text](/Documentation/Images/DiskObsWarn.png "DiskObserver output example.")  


## FabricSystemObserver 
This observer monitors Fabric system service processes e.g., Fabric, FabricApplicationGateway, FabricCAS,
FabricDCA, FabricDnsService, FabricFAS, FabricGateway, FabricHost, etc...

**NOTE:**
Only enable FabricSystemObserver ***after*** you get a sense of what impact your services have on the SF runtime, in particular Fabric.exe. 
This is very important because there is no "one threshold fits all" across warning/error thresholds for any of the SF system services. 
By default, FabricObserver runs as NetworkUser on Windows and sfappsuser on Linux. These are non-privileged accounts and therefore for any service
running as System or root, default FabricObserver can't monitor process behavior (this is always true on Windows). That said, there are only a few system
services you would care about: Fabric.exe and FabricGateway.exe. Fabric.exe is generally the system service that your code can directly impact with respect to machine resource usage.

**Input - Settings.xml**: Only ClusterOperationTimeoutSeconds is set in Settings.xml.

```xml
  <Section Name="FabricSystemObserverConfiguration">
    ...
    <Parameter Name="ClusterOperationTimeoutSeconds" Value="120" />
  </Section>
```

**Input - ApplicationManifest.xml**: Threshold settings are defined (overridden) in ApplicationManifest.xml.

```xml
<!-- FabricSystemObserver -->
<Section Name="FabricSystemObserverConfiguration">
    <Parameter Name="Enabled" Value="" MustOverride="true" />

    <!-- Optional: Whether or not AppObserver should monitor the percentage of maximum LVIDs in use by a stateful System services that employs KVS (Fabric, FabricRM).
         Enabling this will put fabric:/System into Warning when either Fabric or FabricRM have consumed 75% of Maximum number of LVIDs (which is int.MaxValue per process). -->
    <Parameter Name="EnableKvsLvidMonitoring" Value="" MustOverride="true" />
    <Parameter Name="EnableTelemetry" Value="" MustOverride="true" />
    <Parameter Name="EnableEtw" Value="" MustOverride="true" />
    <Parameter Name="EnableCSVDataLogging" Value="" MustOverride="true" />
    <Parameter Name="EnableVerboseLogging" Value="" MustOverride="true" />
    <Parameter Name="MonitorDuration" Value="" MustOverride="true" />
    <Parameter Name="RunInterval" Value="" MustOverride="true" />

    <!-- Optional: monitor private working set only for target service processes (versus full working set, which is private + shared). The default setting in ApplicationManifest.xml is true.  -->
    <Parameter Name="MonitorPrivateWorkingSet" Value="" MustOverride="true" />
    
    <!-- Optional: You can choose between of List<T> or a CircularBufferCollection<T> for observer data storage.
         It just depends upon how much data you are collecting per observer run and if you only care about
         the most recent data (where number of most recent items in collection 
         type equals the ResourceUsageDataCapacity you specify). -->
    <Parameter Name="UseCircularBuffer" Value="" MustOverride="true" />
    
    <!-- Required-If UseCircularBuffer = True -->
    <Parameter Name="ResourceUsageDataCapacity" Value="" MustOverride="true"/>
    
    <!-- OBSOLETE. Windows Event Log monitoring is no longer supported. -->
    <Parameter Name="MonitorWindowsEventLog" Value="" MustOverride="true" />
    <Parameter Name="CpuErrorLimitPercent" Value="" MustOverride="true" />
    <Parameter Name="CpuWarningLimitPercent" Value="" MustOverride="true" />
    <Parameter Name="MemoryErrorLimitMb" Value="" MustOverride="true" />
    <Parameter Name="MemoryWarningLimitMb" Value="" MustOverride="true" />
    <Parameter Name="NetworkErrorActivePorts" Value="" MustOverride="true"  />
    <Parameter Name="NetworkWarningActivePorts" Value="" MustOverride="true"  />
    <Parameter Name="NetworkErrorEphemeralPorts" Value="" MustOverride="true" />
    <Parameter Name="NetworkWarningEphemeralPorts" Value="" MustOverride="true" />
    <Parameter Name="AllocatedHandlesErrorLimit" Value="" MustOverride="true" />
    <Parameter Name="AllocatedHandlesWarningLimit" Value="" MustOverride="true" />
    <Parameter Name="ThreadCountErrorLimit" Value="" MustOverride="true" />
    <Parameter Name="ThreadCountWarningLimit" Value="" MustOverride="true" />
    <Parameter Name="ClusterOperationTimeoutSeconds" Value="120" />
  </Section>
```

**Output**: Log text(Error/Warning), System (App) Level Service Fabric Health Report (Error/Warning/Ok), ETW, Telemetry

Example SFX output (Informational):  

![alt text](/Documentation/Images/FOFSOInfo.png "FabricSystemObserver output example.")  

**This observer also optionally outputs a CSV file containing all resource usage
data across iterations for use in analysis. Included are Average and
Peak measurements. See Settings.xml's EnableCSVDataLogging setting.**

This observer runs for either a specified configuration setting of time
or default of 30 seconds. Each fabric system process is monitored for
CPU, memory, ports, and allocated handle usage, with related values stored in instances of
FabricResourceUsageData object.

## NetworkObserver

This observer checks outbound connection state for user-supplied endpoints (hostname/port pairs).

**Input**: NetworkObserver.config.json in PackageRoot\\Config.
Users must supply target application Uri (targetApp), endpoint hostname/port pairs, and protocol. 

The point of this observer is to simply test reachability of an external endpoint. There is no support for verifying successful authentication with remote endpoints. That is, the tests will not authenticate with credentials, but will pass if the server responds since the response means the server/service is up and running (reachable). The implementation allows for TCP/IP-based tests (HTTP or direct TCP), which is the most common protocol for use in service to service communication (which will undoubtedly involve passing data back and forth).

Each endpoint test result is stored in a simple data type
(ConnectionState) that lives for either the lifetime of the run or until
the next run assuming failure, in which case if the endpoint is
reachable, SFX will be cleared with an OK health state report and the
ConnectionState object will be removed from the containing
List\<ConnectionState\>. Only Warning data is persisted across
iterations. If targetApp entries share hostname/port pairs, only one test will
be conducted to limit network traffic. However, each app that is configured with the same
hostname/port pairs will go into warning if the reachability test fails.

| Setting | Description |
| :--- | :--- | 
| **targetApp**|SF application name (Uri format) to target. | 
| **endpoints** | Array of hostname/port pairs and protocol to be used for the reachability tests. | 
| **hostname**| The remote hostname to connect to. You don't have to supply a prefix like http or https if you are using port 443. However, if the endpoint requires a secure SSL/TLS channel using a custom port (so, *not* 443, for example), then you must provide the https prefix (like for a CosmosDB REST endpoint) in the hostname setting string. If you want to test local endpoints (same local network/cluster, some port number), then use the local IP and *not* the domain name for hostname. | 
| **port**| The remote port to connect to. |
| **protocol** | These are "direct" protocols. So, http means the connection should be over HTTP (like REST calls), and a WebRequest will be made. tcp is for database endpoints like SQL Azure, which require a direct TCP connection (TcpClient socket will be used). Note: the default is http, so you can omit this setting for your REST endpoints, etc. You must use tcp when you want to monitor database server endpoints like SQL Azure.|  


Example NetworkObserver.config.json configuration:  

```javascript
[
  {
    "targetApp": "fabric:/MyApp0",
    "endpoints": [
      {
        "hostname": "https://myazuresrvice42.westus2.cloudapp.azure.com",
        "port": 443,
        "protocol": "http"
      },
      {
        "hostname": "somesqlservername.database.windows.net",
        "port": 1433,
        "protocol": "tcp"
      }
    ]
  },
  {
    "targetApp": "fabric:/MyApp1",
    "endpoints": [
      {
        "hostname": "somesqlservername.database.windows.net",
        "port": 1433,
        "protocol": "tcp"
      }
    ]
  }
]
```

**Output**: Log text(Error/Warning), Application Level Service Fabric Health Report (Error/Warning/Ok), structured telemetry.  

This observer runs 4 checks per supplied hostname with a 3 second delay between tests. This is done to help ensure we don't report transient
network failures which will result in Fabric Health warnings that live until the observer runs again.  


## NodeObserver
 This observer monitors VM level resource usage across CPU, Memory, firewall rules, static and dynamic ports (aka ephemeral ports).
 Thresholds for Erorr and Warning signals are user-supplied and set in ApplicationManifest.xml as application paramaters.  

**Input - ApplicationManifest.xml**:
```xml
<!-- NodeObserver -->
<Parameter Name="NodeObserverUseCircularBuffer" DefaultValue="false" />
<!-- Required-If UseCircularBuffer = True -->
<Parameter Name="NodeObserverResourceUsageDataCapacity" DefaultValue="" />
<Parameter Name="NodeObserverCpuErrorLimitPercent" DefaultValue="" />
<Parameter Name="NodeObserverCpuWarningLimitPercent" DefaultValue="95" />
<Parameter Name="NodeObserverMemoryErrorLimitMb" DefaultValue="" />
<Parameter Name="NodeObserverMemoryWarningLimitMb" DefaultValue="" />
<Parameter Name="NodeObserverMemoryErrorLimitPercent" DefaultValue="" />
<Parameter Name="NodeObserverMemoryWarningLimitPercent" DefaultValue="90" />
<Parameter Name="NodeObserverNetworkErrorActivePorts" DefaultValue="" />
<Parameter Name="NodeObserverNetworkWarningActivePorts" DefaultValue="50000" />
<Parameter Name="NodeObserverNetworkErrorFirewallRules" DefaultValue="" />
<Parameter Name="NodeObserverNetworkWarningFirewallRules" DefaultValue="2500" />
<Parameter Name="NodeObserverNetworkErrorEphemeralPorts" DefaultValue="" />
<Parameter Name="NodeObserverNetworkWarningEphemeralPorts" DefaultValue="20000" />
<Parameter Name="NodeObserverNetworkErrorEphemeralPortsPercentage" DefaultValue="" />
<Parameter Name="NodeObserverNetworkWarningEphemeralPortsPercentage" DefaultValue="90" />
<Parameter Name="NodeObserverEnableNodeSnapshot" DefaultValue="false" />
<!-- The below settings only make sense for Linux. -->
<Parameter Name="NodeObserverLinuxFileHandlesErrorLimitPercent" DefaultValue="" />
<Parameter Name="NodeObserverLinuxFileHandlesWarningLimitPercent" DefaultValue="90" />
<Parameter Name="NodeObserverLinuxFileHandlesErrorLimitTotal" DefaultValue="" />
<Parameter Name="NodeObserverLinuxFileHandlesWarningLimitTotal" DefaultValue="" />
```  

| Setting | Description |
| :--- | :--- | 
| **NodeObserverCpuErrorLimitPercent** | Maximum CPU percentage that should generate an Error |  
| **NodeObserverCpuWarningLimitPercent** | Minimum CPU percentage that should generate a Warning | 
| **NodeObserverEnableTelemetry** | Whether or not to capture and emit NodeObserver monitoring Telemetry/ETW. |  
| **NodeObserverEnableNodeSnapshot** | Whether or not to capture and emit Fabric node state/configuration each time NodeObserver runs. This only makes sense if you also enable Telemetry or ETW for NodeObserver. |  
| **NodeObserverMemoryErrorLimitMb** | Maximum amount of committed memory on machine that will generate an Error. | 
| **NodeObserverMemoryWarningLimitMb** | Minimum amount of committed memory that will generate a Warning. |  
| **NodeObserverMemoryErrorLimitPercent** | Maximum percentage of memory in use on machine that will generate an Error. | 
| **NodeObserverMemoryWarningLimitPercent** | Minimum percentage of memory in use on machine that will generate a Warning. |  
| **NodeObserverMonitorDuration** | The amount of time NodeObserver conducts resource usage probing. | 
| **NodeObserverNetworkErrorFirewallRules** | Number of established Firewall Rules that will generate a Health Warning. |  
| **NodeObserverNetworkWarningFirewallRules** |  Number of established Firewall Rules that will generate a Health Error. |  
| **NodeObserverNetworkErrorActivePorts** | Maximum number of established ports in use by all processes on node that will generate a Fabric Error. |
| **NodeObserverNetworkWarningActivePorts** | Minimum number of established TCP ports in use by all processes on machine that will generate a Fabric Warning. |
| **NodeObserverNetworkErrorEphemeralPorts** | Maximum number of established ephemeral TCP ports in use all processes on machine that will generate a Fabric Error. |
| **NodeObserverNetworkWarningEphemeralPorts** | Minimum number of established ephemeral TCP ports in use by all processes on machine that will generate a Fabric warning. |
| **NodeObserverNetworkErrorEphemeralPortsPercentage** | Maximum percentage of configured ephemeral TCP ports in use by all processes on machine that will generate a Fabric Error. |
| **NodeObserverNetworkWarningEphemeralPortsPercentage** | Minimum percentage of configured ephemeral TCP ports in use by all processes on machine that will generate a Fabric warning. |
| **NodeObserverUseCircularBuffer** | You can choose between of `List<T>` or a `CircularBufferCollection<T>` for observer data storage. | 
| **NodeObserverResourceUsageDataCapacity** | Required-If UseCircularBuffer = True: This represents the number of items to hold in the data collection instance for the observer. | 
| **NodeObserverLinuxFileHandlesErrorLimitPercent** | Maximum percentage of allocated file handles (as a percentage of maximum FDs configured) in use on Linux machine that will generate an Error. | 
| **NodeObserverLinuxFileHandlesWarningLimitPercent** | Minumum percentage of allocated file handles (as a percentage of maximum FDs configured) in use on Linux machine that will generate a Warning. |
| **NodeObserverLinuxFileHandlesErrorLimitTotal** | Total number of allocated file handles in use on Linux machine that will generate an Error. | 
| **NodeObserverLinuxFileHandlesWarningLimitTotal** | Total number of allocated file handles in use on Linux machine that will generate a Warning. |

**Output**: Log text(Error/Warning), Node Level Service Fabric Health Reports (Ok/Warning/Error), structured telemetry (ApplicationInsights, LogAnalytics), ETW. 


Example SFX Output (Warning - Memory Consumption):  

![alt text](/Documentation/Images/FODiskNodeObs.png "NodeObserver output example.")  


## OSObserver
This observer records basic OS properties across OS version, OS health status, physical/virtual memory use, number of running processes, number of active TCP ports (active/ephemeral), number of enabled firewall rules, list of recent patches/hotfixes. It creates an OK Health State SF Health Report that is visible in SFX at the node level (Details tab) and by calling http://localhost:5000/api/ObserverManager if you have deployed the FabricObserver Web Api App. It's best to enable this observer in all deployments of FO. OSObserver will check the VM's Windows Update AutoUpdate settings and Warn if Windows AutoUpdate Downloads setting is enabled. It is critical to not install Windows Updates in an unregulated (non-rolling) manner is this can take down multiple VMs concurrently, which can lead to seed node quorum loss in your cluster. Please do not enable Automatic Windows Update downloads. **It is highly recommended that you enable [Azure virtual machine scale set automatic OS image upgrades](https://docs.microsoft.com/azure/virtual-machine-scale-sets/virtual-machine-scale-sets-automatic-upgrade).**

**Input**: For Windows, you can set OSObserverEnableWindowsAutoUpdateCheck setting to true of false. This will let you know if your OS is misconfigured with respect to how Windows Update manages update downloads and installation. In general, you should not configure Windows to automatically download Windows Update binaries. Instead, use VMSS Automatic Image Upgrade service.  
**Output**: Log text(Error/Warning), Node Level Service Fabric Health Reports (Ok/Warning/Error), structured telemetry (ApplicationInsights, LogAnalytics), ETW, optional HTML output for FO Web API service. 

The only Fabric health reports generated by this observer is an Error when OS Status is not "OK" (which means something is wrong at the OS level and this means trouble), a Warning if Windows Update Automatic Update service is configured to automatically download updates, and long-lived Ok Health Report that contains the information it collected about the VM it's running on.  

Example SFX output (Informational): 

![alt text](/Documentation/Images/FONodeDetails.png "OSObserver output example.")  

## Writing a Custom Observer
Please see the [SampleObserver project](/SampleObserverPlugin) for a complete sample observer plugin implementation with code comments and readme.
Also, see [How to implement an observer plugin using our extensibility model](/Documentation/Plugins.md)
