param([Parameter(Mandatory=$true)][string]$Executable,[Parameter(Mandatory=$true)][string]$Thumbprint,[Parameter(Mandatory=$true)][string]$SignTool)
$ErrorActionPreference='Stop'
# The private key remains in the certificate store/HSM. Never add it to the repository.
& $SignTool sign /sha1 $Thumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $Executable
if($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
$taskSignature=Get-AuthenticodeSignature -LiteralPath $Executable
if($taskSignature.Status -ne 'Valid') { throw "Signature verification failed: $($taskSignature.Status)" }
Write-Output 'Signature verified. Regenerate the package manifest and ZIP hash after signing.'
