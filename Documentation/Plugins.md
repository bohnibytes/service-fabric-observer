## How to implement an observer plugin using our extensibility model*

Please see the [SampleObserver project](/SampleObserverPlugin) for a complete sample observer plugin implementation with code comments and readme. This document is a simple 
overview of how to get started with building an observer plugin.

#### Steps 

- Install [.Net Core 3.1](https://dotnet.microsoft.com/download/dotnet-core/3.1).
- Navigate to top level directory (where FabricObserver.sln lives, for example) then,

For now, you can use the related nugets (choose target OS, framework-dependent or self-contained nupkg file) available [here](https://github.com/microsoft/service-fabric-observer/releases). 

Download the appropriate nupkg to your local machine and update your local nuget.config to include the location of the file on disk.

OR

You can build them yourself by simply running these scripts, in this order: 

- ./Build-FabricObserver.ps1
- ./Build-NugetPackages.ps1


Create a new .NET Core 3.1 library project, install the nupkg you need for your target OS (Linux (Ubuntu) or Windows):  

	Framework-dependent = Requires that .NET Core 3.1 is already installed on target machine.

	Self-contained = Includes all the binaries necessary for running .NET Core 3.1 applications on target machine.

- Write your observer plugin!

- Build your observer project, drop the output dll into the Data/Plugins folder in FabricObserver/PackageRoot.

- Add a new config section for your observer in FabricObserver/PackageRoot/Config/Settings.xml (see example at bottom of that file)
   Update ApplicationManifest.xml with Parameters if you want to support Application Parameter Updates for your plugin.
   (Look at both FabricObserver/PackageRoot/Config/Settings.xml and FabricObserverApp/ApplicationPackageRoot/ApplicationManifest.xml for several examples of how to do this.)

- Deploy FabricObserver to your cluster. Your new observer will be managed and run just like any other observer.

#### Note: Due to the complexity of unloading plugins at runtime, in order to add or update a plugin, you must redeploy FabricObserver. The problem is easier to solve for new plugins, as this could be done via a Data configuration update, but we have not added support for this yet.
