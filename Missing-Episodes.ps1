# --- Configuration ---
# Server URL + API key live in config.local.ps1 (gitignored). Create it with:
#   $EmbyUrl = "http://host:8096"
#   $ApiKey  = "your-api-key"
$ConfigPath = Join-Path $PSScriptRoot "config.local.ps1"
if (-not (Test-Path $ConfigPath)) { Write-Error "Missing $ConfigPath — see comment above."; return }
. $ConfigPath

# We need a UserID for this endpoint. 
# The script will attempt to find the first admin user automatically.
$Headers = @{ "X-Emby-Token" = $ApiKey; "Accept" = "application/json" }
$User = (Invoke-RestMethod -Uri "$EmbyUrl/emby/Users" -Headers $Headers)[0]
$UserId = $User.Id

Write-Host "Querying Metadata Manager's missing episodes list..." -ForegroundColor Cyan

# This is the dedicated endpoint used by Metadata Manager -> Views -> Missing Episodes
$MissingUrl = "$EmbyUrl/emby/Shows/Missing?UserId=$UserId&Recursive=true&Fields=SeriesName,ParentIndexNumber,IndexNumber,PremiereDate&SortBy=SeriesSortName,ParentIndexNumber,IndexNumber"

try {
    $Data = Invoke-RestMethod -Uri $MissingUrl -Headers $Headers -Method Get
    $MissingItems = $Data.Items
} catch {
    Write-Error "Request failed. Ensure 'Display missing episodes within seasons' is enabled in your Library settings."
    return
}

if ($MissingItems.Count -gt 0) {
    $Report = foreach ($Item in $MissingItems) {
        [PSCustomObject]@{
            Series   = $Item.SeriesName
            Season   = $Item.ParentIndexNumber
            Episode  = $Item.IndexNumber
            Title    = $Item.Name
            AirDate  = if ($Item.PremiereDate) { ([DateTime]$Item.PremiereDate).ToShortDateString() } else { "N/A" }
        }
    }

    Write-Host "Found $($MissingItems.Count) missing episodes:`n" -ForegroundColor Green
    
    # Strictly using Format-Table -AutoSize as requested
    $Report | Format-Table -AutoSize
    $Report | Export-Csv -Path "c:\MissingEpisodes.csv" -NoTypeInformation
} else {
    Write-Host "No missing episodes found. If you expect results, check your 'Display missing episodes' library setting." -ForegroundColor Yellow
}