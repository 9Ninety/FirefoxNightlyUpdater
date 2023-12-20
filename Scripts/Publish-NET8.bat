@echo off

cd %~dp0\..\FirefoxNightlyMSIXUpdater

echo:
echo:
echo Publishing for .NET 8
echo:
dotnet publish FirefoxNightlyMSIXUpdater.csproj -f net8.0-windows10.0.17763.0^
 /p:DebugType=None^
 /p:DebugSymbols=false^
 /p:PublishProfile=Properties\PublishProfiles\NET8.pubxml

cd ..