param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"

$rootPath = [System.IO.Path]::GetFullPath($Root)
$reportFile = if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    Join-Path $rootPath "artifacts\v6-gate\huaray-sdk-contract.json"
}
else {
    [System.IO.Path]::GetFullPath($ReportPath)
}
$checks = [System.Collections.Generic.List[object]]::new()
$blockingReasons = [System.Collections.Generic.List[string]]::new()

function Add-Check([string]$Name, [string]$Status, [string]$Reason, [object]$Details = $null) {
    $record = [ordered]@{
        name = $Name
        status = $Status
        reason = $Reason
    }
    if ($null -ne $Details) {
        $record.details = $Details
    }
    [void]$checks.Add($record)
    if ($Status -eq "BLOCKED") {
        [void]$blockingReasons.Add("$Name`: $Reason")
    }
}

function Get-TypeOrThrow([System.Reflection.Assembly]$Assembly, [string]$Name) {
    $type = $Assembly.GetType($Name, $false)
    if ($null -eq $type) {
        throw "Missing public type '$Name'."
    }
    return $type
}

function Assert-Method(
    [type]$Type,
    [string]$Name,
    [string]$ReturnType,
    [string[]]$ParameterTypes) {

    $methods = @($Type.GetMethods([System.Reflection.BindingFlags]"Public,Instance,Static,DeclaredOnly") |
        Where-Object { $_.Name -eq $Name })
    foreach ($method in $methods) {
        $actualParameters = @($method.GetParameters() | ForEach-Object { $_.ParameterType.FullName })
        if ($method.ReturnType.FullName -eq $ReturnType -and
            ($actualParameters -join "|") -eq ($ParameterTypes -join "|")) {
            return [ordered]@{
                method = $method.Name
                isStatic = $method.IsStatic
                returnType = $method.ReturnType.FullName
                parameterTypes = $actualParameters
            }
        }
    }

    $actual = @($methods | ForEach-Object {
        "$($_.ReturnType.FullName) $Name($((@($_.GetParameters() | ForEach-Object { $_.ParameterType.FullName }) -join ', ')))"
    })
    throw "Signature mismatch for $Name. Expected $ReturnType $Name($($ParameterTypes -join ', ')); actual: $($actual -join ' | ')"
}

function Assert-Field([type]$Type, [string]$Name, [string]$ExpectedType) {
    $field = $Type.GetField($Name, [System.Reflection.BindingFlags]"Public,Instance")
    if ($null -eq $field -or $field.FieldType.FullName -ne $ExpectedType) {
        $actual = if ($null -eq $field) { "missing" } else { $field.FieldType.FullName }
        throw "Field mismatch for $($Type.FullName).$Name. Expected $ExpectedType; actual: $actual"
    }

    return [ordered]@{
        type = $Type.FullName
        field = $Name
        fieldType = $field.FieldType.FullName
    }
}

function Assert-EnumValue([type]$Type, [string]$Name, [long]$ExpectedValue) {
    if (-not $Type.IsEnum) {
        throw "$($Type.FullName) is not an enum."
    }

    $field = $Type.GetField($Name, [System.Reflection.BindingFlags]"Public,Static")
    if ($null -eq $field) {
        throw "Missing enum value $($Type.FullName).$Name."
    }

    $actualValue = [Convert]::ToInt64($field.GetValue($null))
    if ($actualValue -ne $ExpectedValue) {
        throw "Enum value mismatch for $($Type.FullName).$Name. Expected $ExpectedValue; actual: $actualValue"
    }

    return [ordered]@{
        enum = $Type.FullName
        name = $Name
        value = $actualValue
    }
}

function Write-ReportAndExit([object]$Report, [int]$ExitCode) {
    $directory = Split-Path -Parent $reportFile
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    [System.IO.File]::WriteAllText(
        $reportFile,
        ($Report | ConvertTo-Json -Depth 20),
        [System.Text.UTF8Encoding]::new($false))
    Write-Output ($Report | ConvertTo-Json -Depth 20)
    exit $ExitCode
}

$sdkInput = [string]$env:CLEARFROST_HUARAY_SDK_PATH
$baseReport = [ordered]@{
    schemaVersion = "v6-huaray-sdk-contract-1.0"
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    root = $rootPath
    inputEnvironmentVariable = "CLEARFROST_HUARAY_SDK_PATH"
    sdk = [ordered]@{
        supplied = -not [string]::IsNullOrWhiteSpace($sdkInput)
        path = ""
        sha256 = ""
        assembly = ""
    }
    checks = @()
    cameraConnectionAttempted = $false
    realCameraStatus = "NOT_VERIFIED"
    blockingReasons = @()
    status = "NOT_VERIFIED"
}

if ([string]::IsNullOrWhiteSpace($sdkInput)) {
    $baseReport.reason = "CLEARFROST_HUARAY_SDK_PATH was not supplied; external SDK contract was not verified."
    Write-ReportAndExit $baseReport 0
}

$sdkPath = [System.IO.Path]::GetFullPath($sdkInput.Trim())
$baseReport.sdk.path = $sdkPath
if (-not (Test-Path -LiteralPath $sdkPath -PathType Leaf)) {
    Add-Check "SDK input" "BLOCKED" "The supplied SDK path does not exist."
    $baseReport.checks = @($checks)
    $baseReport.blockingReasons = @($blockingReasons)
    $baseReport.status = "BLOCKED"
    Write-ReportAndExit $baseReport 1
}

try {
    $baseReport.sdk.sha256 = (Get-FileHash -LiteralPath $sdkPath -Algorithm SHA256).Hash.ToUpperInvariant()
    Add-Check "SDK SHA-256" "PASS" "Recorded the supplied MVSDK_Net.dll SHA-256." ([ordered]@{ sha256 = $baseReport.sdk.sha256 })

    $assembly = [System.Reflection.Assembly]::LoadFrom($sdkPath)
    $baseReport.sdk.assembly = $assembly.FullName
    Add-Check "Assembly load" "PASS" "Loaded the supplied managed SDK assembly without opening a camera."

    $defineType = Get-TypeOrThrow $assembly "MVSDK_Net.IMVDefine"
    $cameraType = Get-TypeOrThrow $assembly "MVSDK_Net.MyCamera"
    $deviceListType = Get-TypeOrThrow $assembly "MVSDK_Net.IMVDefine+IMV_DeviceList"
    $deviceInfoType = Get-TypeOrThrow $assembly "MVSDK_Net.IMVDefine+IMV_DeviceInfo"
    $frameType = Get-TypeOrThrow $assembly "MVSDK_Net.IMVDefine+IMV_Frame"
    $frameInfoType = Get-TypeOrThrow $assembly "MVSDK_Net.IMVDefine+IMV_FrameInfo"
    $pixelConvertType = Get-TypeOrThrow $assembly "MVSDK_Net.IMVDefine+IMV_PixelConvertParam"
    $stringType = Get-TypeOrThrow $assembly "MVSDK_Net.IMVDefine+IMV_String"
    $enumEntryInfoType = Get-TypeOrThrow $assembly "MVSDK_Net.IMVDefine+IMV_EnumEntryInfo"
    $enumEntryListType = Get-TypeOrThrow $assembly "MVSDK_Net.IMVDefine+IMV_EnumEntryList"
    $createHandleModeType = Get-TypeOrThrow $assembly "MVSDK_Net.IMVDefine+IMV_ECreateHandleMode"
    $cameraTypeEnum = Get-TypeOrThrow $assembly "MVSDK_Net.IMVDefine+IMV_ECameraType"
    $interfaceType = Get-TypeOrThrow $assembly "MVSDK_Net.IMVDefine+IMV_EInterfaceType"
    $pixelType = Get-TypeOrThrow $assembly "MVSDK_Net.IMVDefine+IMV_EPixelType"
    $bayerType = Get-TypeOrThrow $assembly "MVSDK_Net.IMVDefine+IMV_EBayerDemosaic"
    Add-Check "Types and nested types" "PASS" "Resolved the MyCamera type, IMVDefine, and all bridge structure/enums."

    $methodChecks = @(
        @("IMV_EnumDevices", "System.Int32", @("$($deviceListType.FullName)&", "System.UInt32")),
        @("IMV_CreateHandle", "System.Int32", @($createHandleModeType.FullName, "System.Int32", "System.String")),
        @("IMV_Open", "System.Int32", @()),
        @("IMV_Close", "System.Int32", @()),
        @("IMV_DestroyHandle", "System.Int32", @()),
        @("IMV_StartGrabbing", "System.Int32", @()),
        @("IMV_StopGrabbing", "System.Int32", @()),
        @("IMV_GetFrame", "System.Int32", @("$($frameType.FullName)&", "System.UInt32")),
        @("IMV_ReleaseFrame", "System.Int32", @("$($frameType.FullName)&")),
        @("IMV_FeatureIsReadable", "System.Boolean", @("System.String")),
        @("IMV_FeatureIsWriteable", "System.Boolean", @("System.String")),
        @("IMV_GetEnumFeatureSymbol", "System.Int32", @("System.String", "$($stringType.FullName)&")),
        @("IMV_SetEnumFeatureSymbol", "System.Int32", @("System.String", "System.String")),
        @("IMV_GetEnumFeatureEntryNum", "System.Int32", @("System.String", "System.UInt32&")),
        @("IMV_GetEnumFeatureEntrys", "System.Int32", @("System.String", "$($enumEntryListType.FullName)&")),
        @("IMV_GetDoubleFeatureValue", "System.Int32", @("System.String", "System.Double&")),
        @("IMV_SetDoubleFeatureValue", "System.Int32", @("System.String", "System.Double")),
        @("IMV_GetIntFeatureValue", "System.Int32", @("System.String", "System.Int64&")),
        @("IMV_SetIntFeatureValue", "System.Int32", @("System.String", "System.Int64")),
        @("IMV_GetBoolFeatureValue", "System.Int32", @("System.String", "System.Boolean&")),
        @("IMV_SetBoolFeatureValue", "System.Int32", @("System.String", "System.Boolean")),
        @("IMV_GetStringFeatureValue", "System.Int32", @("System.String", "$($stringType.FullName)&")),
        @("IMV_SetStringFeatureValue", "System.Int32", @("System.String", "System.String")),
        @("IMV_GetEnumFeatureValue", "System.Int32", @("System.String", "System.UInt64&")),
        @("IMV_SetEnumFeatureValue", "System.Int32", @("System.String", "System.UInt64")),
        @("IMV_PixelConvert", "System.Int32", @("$($pixelConvertType.FullName)&")),
        @("IMV_SetBufferCount", "System.Int32", @("System.UInt32")),
        @("IMV_ClearFrameBuffer", "System.Int32", @()),
        @("IMV_ExecuteCommandFeature", "System.Int32", @("System.String")),
        @("IMV_IsGrabbing", "System.Boolean", @())
    )
    $methodEvidence = [System.Collections.Generic.List[object]]::new()
    foreach ($methodCheck in $methodChecks) {
        [void]$methodEvidence.Add((Assert-Method $cameraType $methodCheck[0] $methodCheck[1] $methodCheck[2]))
    }
    $enumMethodEvidence = @($methodEvidence | Where-Object { $_.method -eq "IMV_EnumDevices" })
    if ($enumMethodEvidence.Count -ne 1 -or $enumMethodEvidence[0].isStatic -ne $true) {
        throw "IMV_EnumDevices must be a static method."
    }
    $instanceMethodEvidence = @($methodEvidence | Where-Object { $_.method -ne "IMV_EnumDevices" -and $_.isStatic -eq $true })
    if ($instanceMethodEvidence.Count -gt 0) {
        throw "Camera lifecycle, feature, frame, and PixelConvert methods must be instance methods."
    }
    Add-Check "Public method signatures" "PASS" "Validated enumeration, handle lifecycle, grabbing, frame, feature, and pixel conversion signatures." @($methodEvidence)

    $fieldEvidence = [System.Collections.Generic.List[object]]::new()
    foreach ($fieldCheck in @(
        @($deviceListType, "nDevNum", "System.UInt32"),
        @($deviceListType, "pDevInfo", "System.IntPtr"),
        @($deviceInfoType, "nCameraType", $cameraTypeEnum.FullName),
        @($deviceInfoType, "cameraKey", "System.String"),
        @($deviceInfoType, "cameraName", "System.String"),
        @($deviceInfoType, "serialNumber", "System.String"),
        @($deviceInfoType, "vendorName", "System.String"),
        @($deviceInfoType, "modelName", "System.String"),
        @($deviceInfoType, "nInterfaceType", $interfaceType.FullName),
        @($frameType, "frameHandle", "System.IntPtr"),
        @($frameType, "pData", "System.IntPtr"),
        @($frameType, "frameInfo", $frameInfoType.FullName),
        @($frameInfoType, "blockId", "System.UInt64"),
        @($frameInfoType, "status", "System.UInt32"),
        @($frameInfoType, "width", "System.UInt32"),
        @($frameInfoType, "height", "System.UInt32"),
        @($frameInfoType, "size", "System.UInt32"),
        @($frameInfoType, "pixelFormat", $pixelType.FullName),
        @($frameInfoType, "timeStamp", "System.UInt64"),
        @($frameInfoType, "paddingX", "System.UInt32"),
        @($frameInfoType, "paddingY", "System.UInt32"),
        @($pixelConvertType, "nWidth", "System.UInt32"),
        @($pixelConvertType, "nHeight", "System.UInt32"),
        @($pixelConvertType, "ePixelFormat", $pixelType.FullName),
        @($pixelConvertType, "pSrcData", "System.IntPtr"),
        @($pixelConvertType, "nSrcDataLen", "System.UInt32"),
        @($pixelConvertType, "nPaddingX", "System.UInt32"),
        @($pixelConvertType, "nPaddingY", "System.UInt32"),
        @($pixelConvertType, "eBayerDemosaic", $bayerType.FullName),
        @($pixelConvertType, "eDstPixelFormat", $pixelType.FullName),
        @($pixelConvertType, "pDstBuf", "System.IntPtr"),
        @($pixelConvertType, "nDstBufSize", "System.UInt32"),
        @($pixelConvertType, "nDstDataLen", "System.UInt32"),
        @($stringType, "str", "System.String"),
        @($enumEntryInfoType, "value", "System.UInt64"),
        @($enumEntryInfoType, "name", "System.String"),
        @($enumEntryListType, "nEnumEntryBufferSize", "System.UInt32"),
        @($enumEntryListType, "pEnumEntryInfo", "System.IntPtr")
    )) {
        [void]$fieldEvidence.Add((Assert-Field $fieldCheck[0] $fieldCheck[1] $fieldCheck[2]))
    }
    Add-Check "Structure fields" "PASS" "Validated the fields consumed by frame metadata, device enumeration, feature strings, enum entries, and PixelConvert." @($fieldEvidence)

    $enumEvidence = @(
        (Assert-EnumValue $createHandleModeType "modeByIndex" 0),
        (Assert-EnumValue $createHandleModeType "modeByCameraKey" 1),
        (Assert-EnumValue $createHandleModeType "modeByDeviceUserID" 2),
        (Assert-EnumValue $createHandleModeType "modeByIPAddress" 3),
        (Assert-EnumValue $interfaceType "interfaceTypeAll" 0),
        (Assert-EnumValue $pixelType "gvspPixelMono8" 0x01080001),
        (Assert-EnumValue $pixelType "gvspPixelBGR8" 0x02180015),
        (Assert-EnumValue $bayerType "demosaicEdgeSensing" 2)
    )
    Add-Check "Enum values" "PASS" "Validated the handle modes, interface mask, pixel formats, and Bayer conversion value." $enumEvidence

    $sdkObject = [Activator]::CreateInstance($cameraType)
    if ($null -eq $sdkObject) {
        throw "Activator returned null for MVSDK_Net.MyCamera."
    }
    Add-Check "SDK type instantiation" "PASS" "Instantiated MVSDK_Net.MyCamera without enumerating or opening a camera."

    $adapterCandidates = @(Get-ChildItem -LiteralPath (Join-Path $rootPath "ClearFrost\bin") -Filter "*.dll" -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne "MVSDK_Net.dll" } |
        Select-Object -ExpandProperty FullName)
    $adapterPath = ""
    foreach ($candidate in $adapterCandidates) {
        try {
            $candidateAssembly = [System.Reflection.Assembly]::LoadFrom($candidate)
            if ($null -ne $candidateAssembly.GetType("ClearFrost.Hardware.HuaraySdkCamera", $false)) {
                $adapterPath = $candidate
                break
            }
        }
        catch {
            # Dependency probing is best effort; the main adapter is selected by type.
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($adapterPath)) {
        $adapterAssembly = [System.Reflection.Assembly]::LoadFrom($adapterPath)
        $adapterType = $adapterAssembly.GetType("ClearFrost.Hardware.HuaraySdkCamera", $true)
        $adapter = [Activator]::CreateInstance($adapterType)
        $bridgeField = $adapterType.GetField("_bridge", [System.Reflection.BindingFlags]"NonPublic,Instance")
        if ($null -eq $bridgeField -or $null -eq $bridgeField.GetValue($adapter)) {
            throw "HuaraySdkCamera instantiated but its SDK bridge did not accept the supplied contract."
        }
        $adapter.Dispose()
        Add-Check "ClearFrost adapter instantiation" "PASS" "Instantiated HuaraySdkCamera and its validated bridge without camera I/O." @{
            assemblyPath = $adapterPath
            cameraConnectionAttempted = $false
        }
    }
    else {
        Add-Check "ClearFrost adapter instantiation" "NOT_VERIFIED" "ClearFrost output assembly was not supplied; SDK type instantiation and all public signatures were still checked."
    }
}
catch {
    Add-Check "SDK contract" "BLOCKED" $_.Exception.Message
}

$baseReport.checks = @($checks)
$baseReport.blockingReasons = @($blockingReasons)
    $baseReport.status = if ($blockingReasons.Count -gt 0) { "BLOCKED" } else { "PASS" }
$baseReport.reason = if ($baseReport.status -eq "PASS") {
    "Public MVSDK_Net contract and no-camera type checks passed; real camera behavior remains unverified."
}
elseif ($baseReport.status -eq "BLOCKED") {
    "The supplied MVSDK_Net.dll does not satisfy the ClearFrost Huaray reflection contract."
}
else {
    "The SDK contract was only partially verified."
}

Write-ReportAndExit $baseReport $(if ($baseReport.status -eq "BLOCKED") { 1 } else { 0 })
