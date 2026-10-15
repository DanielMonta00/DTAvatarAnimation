# The capsule follower's logic, outside Unity: the same C# the Unity component runs (OperatorTrack, FvpFrameRateGovernor), compiled with the
# Roslyn and .NET that ship with the Unity editor.
#
#   .\run.ps1 -What scenarios              synthetic walkers, ghosts, duplicates, dropouts, crossings, a clock that goes back: pass / fail
#   .\run.ps1 -What governor               the frame-rate governor against a simulated GPU with the measured cliff: pass / fail
#   .\run.ps1 -What fit                    the capsule's size (FvpCapsuleFit): hand-made and random poses, is the body inside at every frame: pass / fail
#   .\run.ps1 -What replay -Csv x.csv      the REAL estimates of a recorded session through the tracker with a pipeline delay put in:
#                                          capsule error against the person's real torso point, for estimate rates x delays.
#                                          x.csv comes from:  python ..\capsule_eval.py --spawn --views 1,2,3,4 --frames 0:608:1 --csv x.csv
#                                          Options: -Noise (the tracker's measurement noise, m) -Accel (its maneuver acceleration, m/s^2)
param(
    [ValidateSet('scenarios', 'governor', 'fit', 'replay')][string]$What = 'scenarios',
    [string]$Csv = '',
    [double]$Noise = 0.06,
    [double]$Accel = 1.5,
    [string]$UnityData = "C:\Program Files\Unity\Hub\Editor\6000.3.11f1\Editor\Data"
)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$scripts = (Resolve-Path (Join-Path $here "..\..\..")).Path          # Assets/Scripts
$track = Join-Path $scripts "FasterVoxelPose\OperatorTrack.cs"
$governor = Join-Path $scripts "FasterVoxelPose\FvpFrameRateGovernor.cs"
$fit = Join-Path $scripts "FasterVoxelPose\FvpCapsuleFit.cs"
$runtime = Get-ChildItem "$UnityData\NetCoreRuntime\shared\Microsoft.NETCore.App" | Sort-Object Name | Select-Object -Last 1
$refs = Get-ChildItem "$($runtime.FullName)\*.dll" | Where-Object { $_.Name -match '^(System|Microsoft\.Win32|mscorlib|netstandard)' -and $_.Name -notmatch 'Native' } | ForEach-Object { '-r:"' + $_.FullName + '"' }
$core = "$UnityData\Managed\UnityEngine\UnityEngine.CoreModule.dll"          # Vector2 / Vector3 / Mathf: plain managed math
$refs += '-r:"' + $core + '"'
$out = Join-Path $env:TEMP "track_eval_$What"
New-Item -ItemType Directory -Force $out | Out-Null
Set-Content "$out\refs.rsp" $refs -Encoding ascii

switch ($What) {
    'scenarios' { $sources = @("$here\Scenarios.cs", $track); $args2 = @() }
    'governor'  { $sources = @("$here\GovernorSim.cs", $governor); $args2 = @() }
    'fit'       { $sources = @("$here\FitSim.cs", $fit); $args2 = @() }
    'replay' {
        if (-not $Csv) { throw "-Csv is required for -What replay (python ..\capsule_eval.py --spawn --csv x.csv)" }
        $sources = @("$here\Replay.cs", $track)
        $args2 = @((Resolve-Path $Csv).Path, "$Noise", "$Accel")
    }
}
if (Test-Path "$out\t.dll") { Remove-Item "$out\t.dll" -Force }
& "$UnityData\NetCoreRuntime\dotnet.exe" "$UnityData\DotNetSdkRoslyn\csc.dll" -nologo -target:exe -optimize+ -out:"$out\t.dll" -langversion:9 -nullable:disable "@$out\refs.rsp" @sources 2>&1 | Where-Object { $_ -notmatch 'CS8032' }
Set-Content "$out\t.runtimeconfig.json" '{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"}}}' -Encoding ascii
Copy-Item $core $out -Force                                                    # next to the exe, so the runtime finds it
& "$UnityData\NetCoreRuntime\dotnet.exe" "$out\t.dll" @args2
exit $LASTEXITCODE
