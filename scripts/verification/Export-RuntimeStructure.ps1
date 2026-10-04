param([Parameter(Mandatory)][string]$InputFile,[Parameter(Mandatory)][string]$OutputFile)
$ErrorActionPreference='Stop'
if([IO.Path]::GetFullPath($InputFile) -eq [IO.Path]::GetFullPath($OutputFile)){throw 'Output must be separate from private runtime input.'}
try { $config=Get-Content -LiteralPath $InputFile -Raw | ConvertFrom-Json } catch { throw 'Runtime JSON could not be parsed; private values withheld.' }
# Allowlisted booleans/counts only. Never copy arbitrary tags, addresses, ports,
# credential hashes, usernames, passwords, auth files, or parser input/errors.
$explicit=$null; $owned=$null; $credentials=0
foreach($outbound in @($config.outbounds)) {
    if($outbound.password){$credentials++}
    if($outbound.tag -in @('openvpn','corp')){$explicit=[string]$outbound.password}
    if($outbound.tag -in @('openvpn-owned','corp-owned')){$owned=[string]$outbound.password}
}
foreach($inbound in @($config.inbounds)) {
    foreach($user in @($inbound.users)) {
        if($user.password){$credentials++}
        if($user.name -eq 'explicit'){$explicit=[string]$user.password}
        if($user.name -eq 'owned'){$owned=[string]$user.password}
    }
}
$explicitPresent=-not [string]::IsNullOrEmpty($explicit)
$ownedPresent=-not [string]::IsNullOrEmpty($owned)
[ordered]@{
    Schema=1
    InboundCount=@($config.inbounds).Count
    OutboundCount=@($config.outbounds).Count
    CredentialFieldCount=$credentials
    ExplicitCredentialPresent=$explicitPresent
    OwnedCredentialPresent=$ownedPresent
    CredentialsDistinct=($explicitPresent -and $ownedPresent -and $explicit -cne $owned)
    LiteralSecretsExported=$false
} | ConvertTo-Json | Set-Content -LiteralPath $OutputFile -Encoding UTF8
