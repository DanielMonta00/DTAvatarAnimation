# Accuracy of FvpMultiViewLifter on a recorded session, offline: the same C# the Unity component runs, compiled with the Roslyn and
# .NET that ship with the Unity editor.
#
#   1. in the `vitpose` environment:   python ..\vitpose_dump.py --out lift_input.json
#   2. here:                           .\run.ps1 -Json lift_input.json [-Scale 1.0] [-Options "--inlier 0.08 --conf 0.3"]
#
# Prints, for all the views and for every subset of three and two, the people found, the MPJPE in mm against the 3D ground truth,
# the reprojection error and the time per frame, then the error per joint. Options: --conf --gate --inlier --minjoints.
param(
    [Parameter(Mandatory = $true)][string]$Json,
    [double]$Scale = 1.0,
    [string]$Options = "",
    [string]$UnityData = "C:\Program Files\Unity\Hub\Editor\6000.3.11f1\Editor\Data"
)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$scripts = (Resolve-Path (Join-Path $here "..\..\..")).Path          # Assets/Scripts
$runtime = Get-ChildItem "$UnityData\NetCoreRuntime\shared\Microsoft.NETCore.App" | Sort-Object Name | Select-Object -Last 1
$refs = Get-ChildItem "$($runtime.FullName)\*.dll" | Where-Object { $_.Name -match '^(System|Microsoft\.Win32|mscorlib|netstandard)' -and $_.Name -notmatch 'Native' } | ForEach-Object { '-r:"' + $_.FullName + '"' }
$out = Join-Path $env:TEMP "lift_eval"
New-Item -ItemType Directory -Force $out | Out-Null
Set-Content "$out\refs.rsp" $refs -Encoding ascii
& "$UnityData\NetCoreRuntime\dotnet.exe" "$UnityData\DotNetSdkRoslyn\csc.dll" -nologo -target:exe -out:"$out\lift.dll" -langversion:9 -nullable:disable "@$out\refs.rsp" `
    "$here\Program.cs" "$scripts\FasterVoxelPose\FvpCameraModel.cs" "$scripts\FasterVoxelPose\FvpMultiViewLifter.cs" "$scripts\Calibration\MiniJson.cs" 2>&1 | Where-Object { $_ -notmatch 'CS8032' }
Set-Content "$out\lift.runtimeconfig.json" '{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"}}}' -Encoding ascii
& "$UnityData\NetCoreRuntime\dotnet.exe" "$out\lift.dll" (Resolve-Path $Json).Path $Scale @($Options -split ' ' | Where-Object { $_ })
