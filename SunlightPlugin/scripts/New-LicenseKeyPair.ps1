param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$resolvedDir = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedDir -Force | Out-Null

$rsa = [System.Security.Cryptography.RSA]::Create(2048)
try {
    $privateXml = $rsa.ToXmlString($true)
    $publicXml = $rsa.ToXmlString($false)

    $privatePath = Join-Path $resolvedDir "sunlight-private-key.xml"
    $publicPath = Join-Path $resolvedDir "sunlight-public-key.xml"

    [System.IO.File]::WriteAllText($privatePath, $privateXml, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($publicPath, $publicXml, [System.Text.Encoding]::UTF8)

    Write-Host "Private key: $privatePath"
    Write-Host "Public key : $publicPath"
    Write-Host "Keep the private key offline and only copy the public key into LicenseManager.cs"
}
finally {
    $rsa.Dispose()
}