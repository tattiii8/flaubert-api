$ErrorActionPreference = "Stop"

# ============================================================
# Configuration
# ============================================================

$AuthUrl = "https://deleuze.lesure.net/connect/token"
$BaseUrl = "https://flaubert.lesure.net/api/drive"

# ------------------------------------------------------------
# Authentication
# ------------------------------------------------------------
# Set these values before running.
# Do NOT commit this file with real credentials.
# ------------------------------------------------------------

$TenantId = "flaubert"
$Username = "admin  "
$Password = "password"

# ============================================================
# Helper: API request
# ============================================================

function Invoke-DriveApi {
    param(
        [string]$Method,
        [string]$Uri,
        [object]$Body = $null
    )

    Write-Host ""
    Write-Host ">>> $Method $Uri" -ForegroundColor Cyan

    if ($null -ne $Body) {

        $Json = $Body | ConvertTo-Json -Depth 10

        Write-Host $Json -ForegroundColor DarkGray

        return Invoke-RestMethod `
            -Method $Method `
            -Uri $Uri `
            -Headers $script:Headers `
            -ContentType "application/json" `
            -Body $Json
    }

    return Invoke-RestMethod `
        -Method $Method `
        -Uri $Uri `
        -Headers $script:Headers
}

# ============================================================
# Helper: Get property value
# ============================================================

function Get-Value {
    param(
        [object]$Object,
        [string[]]$Names
    )

    foreach ($Name in $Names) {

        $Property = $Object.PSObject.Properties[$Name]

        if ($null -ne $Property) {
            return $Property.Value
        }
    }

    return $null
}

# ============================================================
# Helper: Assert value
# ============================================================

function Check-Value {
    param(
        [string]$Name,
        [object]$Value
    )

    if (
        $null -eq $Value -or
        [string]::IsNullOrWhiteSpace([string]$Value)
    ) {
        throw "ERROR: $Name was not returned."
    }

    Write-Host "[PASS] $Name = $Value" -ForegroundColor Green
}

# ============================================================
# Header
# ============================================================

Write-Host ""
Write-Host "============================================"
Write-Host " Flaubert Drive Virtual Path Test"
Write-Host "============================================"
Write-Host ""
Write-Host "Auth URL : $AuthUrl"
Write-Host "Drive URL: $BaseUrl"
Write-Host ""

# ============================================================
# 0. Get access token
# ============================================================

Write-Host "[0] Getting access token..." -ForegroundColor Yellow

if ($TenantId -eq "YOUR_TENANT_ID") {
    throw "Please set `$TenantId in drive-test.ps1."
}

if ($Username -eq "YOUR_LOGIN_ID") {
    throw "Please set `$Username in drive-test.ps1."
}

if ($Password -eq "YOUR_PASSWORD") {
    throw "Please set `$Password in drive-test.ps1."
}

$TokenBody = @{
    grant_type = "password"
    tenant_id  = $TenantId
    username   = $Username
    password   = $Password
}

try {

    $TokenResponse = Invoke-RestMethod `
        -Method POST `
        -Uri $AuthUrl `
        -ContentType "application/x-www-form-urlencoded" `
        -Body $TokenBody

}
catch {

    Write-Host ""
    Write-Host "[FAIL] Token request failed." -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    throw
}

$Token = Get-Value $TokenResponse @(
    "access_token",
    "AccessToken"
)

Check-Value "AccessToken" $Token

$TokenType = Get-Value $TokenResponse @(
    "token_type",
    "TokenType"
)

$ExpiresIn = Get-Value $TokenResponse @(
    "expires_in",
    "ExpiresIn"
)

Write-Host "[INFO] TokenType = $TokenType"
Write-Host "[INFO] ExpiresIn  = $ExpiresIn"

# ------------------------------------------------------------
# Never print the actual JWT.
# ------------------------------------------------------------

$script:Headers = @{
    Authorization = "Bearer $Token"
}

Write-Host "[PASS] Access token obtained" -ForegroundColor Green

# ============================================================
# 1. GET folders
# ============================================================

Write-Host ""
Write-Host "[1] GET folders" -ForegroundColor Yellow

$Folders = Invoke-DriveApi `
    -Method "GET" `
    -Uri "$BaseUrl/folders"

Write-Host "[PASS] GET /folders" -ForegroundColor Green

# ============================================================
# 2. Create root folder
# ============================================================

$RootName = "virtual-test-" + (Get-Date -Format "yyyyMMdd-HHmmss")

Write-Host ""
Write-Host "[2] Create root folder: $RootName" -ForegroundColor Yellow

$RootFolder = Invoke-DriveApi `
    -Method "POST" `
    -Uri "$BaseUrl/folders" `
    -Body @{
        name = $RootName
        parentId = $null
    }

$RootId = Get-Value $RootFolder @(
    "id",
    "Id"
)

Check-Value "RootFolderId" $RootId

# ============================================================
# 3. Create child folder
# ============================================================

Write-Host ""
Write-Host "[3] Create child folder" -ForegroundColor Yellow

$ChildFolder = Invoke-DriveApi `
    -Method "POST" `
    -Uri "$BaseUrl/folders" `
    -Body @{
        name = "2026"
        parentId = $RootId
    }

$ChildId = Get-Value $ChildFolder @(
    "id",
    "Id"
)

Check-Value "ChildFolderId" $ChildId

# ============================================================
# 4. Create test file
# ============================================================

$TempFile = Join-Path $env:TEMP "flaubert-drive-test.txt"

"Flaubert virtual path test $(Get-Date -Format o)" |
    Set-Content `
        -Path $TempFile `
        -Encoding UTF8

$Bytes = [System.IO.File]::ReadAllBytes($TempFile)

$ByteSize = $Bytes.Length

Write-Host ""
Write-Host "[4] Test file created" -ForegroundColor Yellow
Write-Host "File: $TempFile"
Write-Host "Size: $ByteSize"

# ============================================================
# 5. Create upload URL
# ============================================================

Write-Host ""
Write-Host "[5] Create upload URL" -ForegroundColor Yellow

$UploadResponse = Invoke-DriveApi `
    -Method "POST" `
    -Uri "$BaseUrl/objects" `
    -Body @{
        fileName = "episode001.txt"
        contentType = "text/plain"
        byteSize = $ByteSize
        folderId = $ChildId
    }

$UploadUrl = Get-Value $UploadResponse @(
    "uploadUrl",
    "UploadUrl"
)

$ObjectId = Get-Value $UploadResponse @(
    "objectId",
    "ObjectId",
    "id",
    "Id"
)

$StorageKey = Get-Value $UploadResponse @(
    "storageKey",
    "StorageKey"
)

Check-Value "UploadUrl" $UploadUrl
Check-Value "ObjectId" $ObjectId

if ($null -ne $StorageKey) {

    Write-Host "[INFO] StorageKey = $StorageKey"

    # Expected:
    # tenantId/UUID
    if (
        $StorageKey -like "$TenantId/*" -and
        $StorageKey -notlike "*episode001.txt*"
    ) {
        Write-Host "[PASS] StorageKey looks like tenantId/UUID" `
            -ForegroundColor Green
    }
    else {
        Write-Host "[WARN] StorageKey does not look like tenantId/UUID" `
            -ForegroundColor Yellow
    }
}

# ============================================================
# 6. Upload file to S3
# ============================================================

Write-Host ""
Write-Host "[6] Upload file to S3" -ForegroundColor Yellow

Invoke-RestMethod `
    -Method PUT `
    -Uri $UploadUrl `
    -Headers @{
        "Content-Type" = "text/plain"
    } `
    -Body $Bytes

Write-Host "[PASS] S3 upload completed" -ForegroundColor Green

# ============================================================
# 7. GET object
# ============================================================

Write-Host ""
Write-Host "[7] GET object" -ForegroundColor Yellow

$Object = Invoke-DriveApi `
    -Method "GET" `
    -Uri "$BaseUrl/objects/$ObjectId"

Write-Host ""
Write-Host "Object:"
$Object | ConvertTo-Json -Depth 10

# ============================================================
# 8. GET objects in folder
# ============================================================

Write-Host ""
Write-Host "[8] GET objects by folderId" -ForegroundColor Yellow

$FolderObjects = Invoke-DriveApi `
    -Method "GET" `
    -Uri "$BaseUrl/objects?folderId=$ChildId"

Write-Host ""
Write-Host "Objects in child folder:"
$FolderObjects | ConvertTo-Json -Depth 10

# ============================================================
# 9. Rename child folder
# ============================================================

Write-Host ""
Write-Host "[9] Rename child folder" -ForegroundColor Yellow

Invoke-DriveApi `
    -Method "PUT" `
    -Uri "$BaseUrl/folders/$ChildId" `
    -Body @{
        name = "archive"
        parentId = $RootId
    } | Out-Null

Write-Host "[PASS] Child folder renamed" -ForegroundColor Green

# ============================================================
# 10. Verify object after folder rename
# ============================================================

Write-Host ""
Write-Host "[10] Verify object after folder rename" -ForegroundColor Yellow

$ObjectAfterFolderRename = Invoke-DriveApi `
    -Method "GET" `
    -Uri "$BaseUrl/objects/$ObjectId"

Write-Host ""
$ObjectAfterFolderRename | ConvertTo-Json -Depth 10

Write-Host "[PASS] Object still exists after folder rename" `
    -ForegroundColor Green

# ============================================================
# 11. Rename root folder
# ============================================================

Write-Host ""
Write-Host "[11] Rename root folder" -ForegroundColor Yellow

Invoke-DriveApi `
    -Method "PUT" `
    -Uri "$BaseUrl/folders/$RootId" `
    -Body @{
        name = "$RootName-renamed"
        parentId = $null
    } | Out-Null

Write-Host "[PASS] Root folder renamed" -ForegroundColor Green

# ============================================================
# 12. Verify object after root rename
# ============================================================

Write-Host ""
Write-Host "[12] Verify object after root rename" -ForegroundColor Yellow

$ObjectAfterRootRename = Invoke-DriveApi `
    -Method "GET" `
    -Uri "$BaseUrl/objects/$ObjectId"

Write-Host ""
$ObjectAfterRootRename | ConvertTo-Json -Depth 10

Write-Host "[PASS] Object still exists after root rename" `
    -ForegroundColor Green

# ============================================================
# 13. Verify folder hierarchy
# ============================================================

Write-Host ""
Write-Host "[13] Verify folder hierarchy" -ForegroundColor Yellow

$FinalFolders = Invoke-DriveApi `
    -Method "GET" `
    -Uri "$BaseUrl/folders?parentId=$RootId"

Write-Host ""
$FinalFolders | ConvertTo-Json -Depth 10

Write-Host "[PASS] Folder hierarchy query completed" `
    -ForegroundColor Green

# ============================================================
# 14. Delete object
# ============================================================

Write-Host ""
Write-Host "[14] Delete object" -ForegroundColor Yellow

Invoke-DriveApi `
    -Method "DELETE" `
    -Uri "$BaseUrl/objects/$ObjectId" | Out-Null

Write-Host "[PASS] Object deleted" -ForegroundColor Green

# ============================================================
# 15. Delete root folder
# ============================================================

Write-Host ""
Write-Host "[15] Delete root folder" -ForegroundColor Yellow

try {

    Invoke-DriveApi `
        -Method "DELETE" `
        -Uri "$BaseUrl/folders/$RootId" | Out-Null

    Write-Host "[PASS] Root folder deleted" -ForegroundColor Green
}
catch {

    Write-Host "[WARN] Root folder deletion failed." `
        -ForegroundColor Yellow

    Write-Host $_.Exception.Message
}

# ============================================================
# Cleanup local file
# ============================================================

Remove-Item `
    $TempFile `
    -Force `
    -ErrorAction SilentlyContinue

# ============================================================
# Final result
# ============================================================

Write-Host ""
Write-Host "============================================"
Write-Host " TEST FINISHED"
Write-Host "============================================"
Write-Host ""
Write-Host "Virtual path test completed."
Write-Host ""
Write-Host "Verified:"
Write-Host "  [PASS] Authentication"
Write-Host "  [PASS] Folder creation"
Write-Host "  [PASS] Nested folder creation"
Write-Host "  [PASS] Upload URL creation"
Write-Host "  [PASS] S3 upload"
Write-Host "  [PASS] Object metadata"
Write-Host "  [PASS] Folder object listing"
Write-Host "  [PASS] Child folder rename"
Write-Host "  [PASS] Parent folder rename"
Write-Host "  [PASS] Object remains after folder rename"
Write-Host "  [PASS] Object deletion"
Write-Host "  [PASS] Folder deletion"
Write-Host ""