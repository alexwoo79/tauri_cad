param(
    [Parameter(Mandatory = $true)]
    [string]$PrivateKeyXmlPath,

    [Parameter(Mandatory = $true)]
    [string]$MachineCode,

    [Parameter(Mandatory = $true)]
    [string]$Customer,

    [Parameter(Mandatory = $false)]
    [string]$ExpiryDate = "",

    [Parameter(Mandatory = $false)]
    [string]$Product = "SunlightPlugin"
)

if (-not (Test-Path $PrivateKeyXmlPath)) {
    throw "Private key file not found: $PrivateKeyXmlPath"
}

$privateXml = [System.IO.File]::ReadAllText((Resolve-Path $PrivateKeyXmlPath), [System.Text.Encoding]::UTF8)
$normalizedMachine = ($MachineCode -replace '-', '').Trim().ToUpperInvariant()

$payload = "version=v1;product=$Product;machine=$normalizedMachine;customer=$Customer"
if (-not [string]::IsNullOrWhiteSpace($ExpiryDate)) {
    $payload += ";expiry=$ExpiryDate"
}

$payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)

$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.FromXmlString($privateXml)
    $signature = $rsa.SignData($payloadBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $licenseCode = [Convert]::ToBase64String($payloadBytes) + "." + [Convert]::ToBase64String($signature)
    Write-Output $licenseCode
}
finally {
    $rsa.Dispose()
}