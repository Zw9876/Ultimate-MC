# Minecraft Portable Launcher - With Image Background Support
# Seamless transition between GUI and loading screen

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName Microsoft.VisualBasic

# Get script/exe directory FIRST
$scriptDir = $null

# Try multiple methods to get the directory
try {
    # Method 1: PSScriptRoot (works for scripts)
    if ($PSScriptRoot) {
        $scriptDir = $PSScriptRoot
    }
    # Method 2: MyInvocation (works for scripts)
    elseif ($MyInvocation.MyCommand.Path) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    # Method 3: Assembly location (works for EXE)
    elseif ([System.Reflection.Assembly]::GetExecutingAssembly().Location) {
        $scriptDir = [System.IO.Path]::GetDirectoryName([System.Reflection.Assembly]::GetExecutingAssembly().Location)
    }
    # Method 4: Current process (works for EXE)
    elseif ($Host.UI.RawUI) {
        $scriptDir = [System.IO.Path]::GetDirectoryName([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName)
    }
    # Method 5: Fallback to current directory
    else {
        $scriptDir = Get-Location | Select-Object -ExpandProperty Path
    }
}
catch {
    # Final fallback
    $scriptDir = [System.Environment]::CurrentDirectory
}

# Ensure we have a valid directory
if ([string]::IsNullOrEmpty($scriptDir) -or -not (Test-Path $scriptDir)) {
    $scriptDir = [System.Environment]::CurrentDirectory
}

Set-Location $scriptDir

# WebView2 — load DLLs now that $scriptDir is confirmed (reliable in both script and EXE)
$script:webView2Loaded = $false
$script:webView2Error  = ""
try {
    $wv2Dir  = Join-Path $scriptDir "runtime\webview2"
    $wv2Core = Join-Path $wv2Dir "Microsoft.Web.WebView2.Core.dll"
    $wv2Wpf  = Join-Path $wv2Dir "Microsoft.Web.WebView2.Wpf.dll"

    if (-not (Test-Path $wv2Dir)) {
        $script:webView2Error = "Folder not found: $wv2Dir"
    } elseif (-not (Test-Path $wv2Core)) {
        $files = (Get-ChildItem $wv2Dir -File | Select-Object -ExpandProperty Name) -join ", "
        $script:webView2Error = "Core.dll not found.`nFiles in folder: $files"
    } elseif (-not (Test-Path $wv2Wpf)) {
        $files = (Get-ChildItem $wv2Dir -File | Select-Object -ExpandProperty Name) -join ", "
        $script:webView2Error = "Wpf.dll not found.`nFiles in folder: $files"
    } else {
        # Add the webview2 folder to PATH so Windows finds WebView2Loader.dll (native dependency)
        $env:PATH = "$wv2Dir;$env:PATH"
        # UnsafeLoadFrom bypasses the Zone.Identifier internet-download security block
        # without modifying files or needing any permissions — correct for bundled DLLs
        [void][System.Reflection.Assembly]::UnsafeLoadFrom($wv2Core)
        [void][System.Reflection.Assembly]::UnsafeLoadFrom($wv2Wpf)
        $script:webView2Loaded = $true
    }
} catch {
    $files = if (Test-Path (Join-Path $scriptDir "runtime\webview2")) {
        (Get-ChildItem (Join-Path $scriptDir "runtime\webview2") -File | Select-Object -ExpandProperty Name) -join ", "
    } else { "folder missing" }
    $script:webView2Error  = "Load failed: $($_.Exception.Message)`nFiles: $files"
    $script:webView2Loaded = $false
}

# Server config file path
$serverConfigPath = Join-Path $scriptDir "server_config.json"

function Load-ServerConfig {
    if (Test-Path $serverConfigPath) {
        try {
            $config = Get-Content -Path $serverConfigPath -Raw | ConvertFrom-Json
            return $config
        } catch {
            Write-Host "Failed to load server config: $_"
            return $null
        }
    }
    return $null
}

function Save-ServerConfig {
    param(
        [string]$Version,
        [string]$ServerType,
        [int]$Memory
    )
    
    $config = @{
        LastVersion = $Version
        LastServerType = $ServerType
        LastMemory = $Memory
        LastUpdated = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    }
    
    try {
        $config | ConvertTo-Json | Set-Content -Path $serverConfigPath
        Write-Host "Server config saved: $Version, $ServerType, ${Memory}GB"
    } catch {
        Write-Host "Failed to save server config: $_"
    }
}

# Error logging (now that we have scriptDir)
$ErrorActionPreference = "Continue"
$errorLogFile = Join-Path $scriptDir "launcher_errors.txt"

try {

#region UUID Management
function Get-ComputerUUID {
    $uuidFile = Join-Path $scriptDir "computer_uuid.dat"
    
    if (Test-Path $uuidFile) {
        $uuid = Get-Content $uuidFile -Raw
        return $uuid.Trim()
    }
    
    $computerName = $env:COMPUTERNAME
    $tempFile = Join-Path $scriptDir "temp_uuid.txt"
    $hashFile = Join-Path $scriptDir "temp_hash.txt"
    
    try {
        $computerName | Out-File -FilePath $tempFile -Encoding ASCII -NoNewline
        $null = certutil -hashfile $tempFile MD5 > $hashFile 2>&1
        
        $hashContent = Get-Content $hashFile
        if ($hashContent.Count -gt 1) {
            $hash = $hashContent[1].Trim()
            $uuid = "$($hash.Substring(0,8))-$($hash.Substring(8,4))-$($hash.Substring(12,4))-$($hash.Substring(16,4))-$($hash.Substring(20,12))"
        } else {
            $uuid = [guid]::NewGuid().ToString()
        }
        
        $uuid | Out-File -FilePath $uuidFile -Encoding ASCII -NoNewline
        
        Remove-Item $tempFile -ErrorAction SilentlyContinue
        Remove-Item $hashFile -ErrorAction SilentlyContinue
        
        return $uuid
    }
    catch {
        $uuid = [guid]::NewGuid().ToString()
        $uuid | Out-File -FilePath $uuidFile -Encoding ASCII -NoNewline
        return $uuid
    }
}

function Reset-ComputerUUID {
    $uuidFile = Join-Path $scriptDir "computer_uuid.dat"
    if (Test-Path $uuidFile) {
        Remove-Item $uuidFile
    }
}
#endregion

#region Skin Management
function Get-SkinPath {
    param([string]$Version)
    
    # OfflineSkins mod looks for skins in: versions/[version]/config/offlineskins/
    if ($Version) {
        $versionDir = Join-Path $scriptDir "versions\$Version"
        $offlineSkinsDir = Join-Path $versionDir "config\offlineskins"
    } else {
        # Fallback to general skins folder
        $offlineSkinsDir = Join-Path $scriptDir "skins"
    }
    
    if (-not (Test-Path $offlineSkinsDir)) {
        New-Item -Path $offlineSkinsDir -ItemType Directory -Force | Out-Null
    }
    
    return $offlineSkinsDir
}

function Get-CurrentSkin {
    param([string]$Username, [string]$Version)
    
    $skinsDir = Get-SkinPath -Version $Version
    $currentSkinFile = Join-Path $skinsDir "$Username.png"
    
    if (Test-Path $currentSkinFile) {
        return $currentSkinFile
    }
    
    return $null
}

function Set-MinecraftSkin {
    param(
        [string]$SkinFilePath,
        [string]$Username,
        [string]$Version
    )
    
    if (-not (Test-Path $SkinFilePath)) {
        throw "Skin file not found: $SkinFilePath"
    }
    
    # Validate it's a PNG
    $extension = [System.IO.Path]::GetExtension($SkinFilePath)
    if ($extension -ne ".png") {
        throw "Skin file must be a PNG image"
    }
    
    # Save to OfflineSkins mod location: versions/[version]/config/offlineskins/[username].png
    $skinsDir = Get-SkinPath -Version $Version
    $userSkinFile = Join-Path $skinsDir "$Username.png"
    
    # If file exists and is locked, try to copy anyway with force and retry
    $maxRetries = 3
    $retryCount = 0
    $copied = $false
    
    while (-not $copied -and $retryCount -lt $maxRetries) {
        try {
            # Force copy even if file exists
            Copy-Item -Path $SkinFilePath -Destination $userSkinFile -Force -ErrorAction Stop
            $copied = $true
        }
        catch {
            $retryCount++
            if ($retryCount -lt $maxRetries) {
                Start-Sleep -Milliseconds 200
            }
            else {
                throw "Failed to save skin after $maxRetries attempts: $($_.Exception.Message)"
            }
        }
    }
    
    # Update config.json to tell OfflineSkins mod which skin to use
    $configFile = Join-Path $skinsDir "config.json"
    $config = @{
        selectedSkinName = $Username
        defaultModel = "steve"
    }
    
    # Convert to JSON and save
    $configJson = $config | ConvertTo-Json
    $configJson | Out-File -FilePath $configFile -Encoding UTF8 -Force
    
    return $userSkinFile
}

function Show-SkinPicker {
    param(
        [string]$Username,
        [string]$Version
    )
    
    $skinForm = New-Object System.Windows.Forms.Form
    $skinForm.Text = "Skin Picker - OfflineSkins Integration"
    $skinForm.Size = New-Object System.Drawing.Size(600, 520)
    $skinForm.StartPosition = "CenterScreen"
    $skinForm.FormBorderStyle = "FixedDialog"
    $skinForm.MaximizeBox = $false
    $skinForm.BackColor = [System.Drawing.Color]::FromArgb(45, 45, 48)
    
    # Title
    $titleLabel = New-Object System.Windows.Forms.Label
    $titleLabel.Location = New-Object System.Drawing.Point(20, 20)
    $titleLabel.Size = New-Object System.Drawing.Size(560, 30)
    $titleLabel.Text = "Choose Your Minecraft Skin"
    $titleLabel.Font = New-Object System.Drawing.Font("Segoe UI", 14, [System.Drawing.FontStyle]::Bold)
    $titleLabel.ForeColor = [System.Drawing.Color]::White
    $skinForm.Controls.Add($titleLabel)
    
    # Preview
    $previewLabel = New-Object System.Windows.Forms.Label
    $previewLabel.Location = New-Object System.Drawing.Point(20, 60)
    $previewLabel.Size = New-Object System.Drawing.Size(200, 200)
    $previewLabel.Text = "Skin Preview"
    $previewLabel.TextAlign = "MiddleCenter"
    $previewLabel.BackColor = [System.Drawing.Color]::FromArgb(60, 60, 60)
    $previewLabel.ForeColor = [System.Drawing.Color]::White
    $previewLabel.BorderStyle = "FixedSingle"
    $skinForm.Controls.Add($previewLabel)
    
    # Load current skin
    $currentSkin = Get-CurrentSkin -Username $Username -Version $Version
    if ($currentSkin -and (Test-Path $currentSkin)) {
        try {
            # Load image into memory without locking the file
            $bytes = [System.IO.File]::ReadAllBytes($currentSkin)
            $ms = New-Object System.IO.MemoryStream($bytes, 0, $bytes.Length)
            $skinImage = [System.Drawing.Image]::FromStream($ms)
            $previewLabel.Image = $skinImage
            $previewLabel.Text = ""
            # Keep the MemoryStream in a variable so it doesn't get disposed
            $previewLabel.Tag = $ms
        } catch { }
    }
    
    # Upload Button
    $uploadButton = New-Object System.Windows.Forms.Button
    $uploadButton.Location = New-Object System.Drawing.Point(240, 60)
    $uploadButton.Size = New-Object System.Drawing.Size(340, 40)
    $uploadButton.Text = "Upload Custom Skin (.png)"
    $uploadButton.Font = New-Object System.Drawing.Font("Segoe UI", 10)
    $uploadButton.ForeColor = [System.Drawing.Color]::White
    $uploadButton.BackColor = [System.Drawing.Color]::FromArgb(0, 120, 215)
    $uploadButton.FlatStyle = "Flat"
    $uploadButton.Add_Click({
        $openFileDialog = New-Object System.Windows.Forms.OpenFileDialog
        $openFileDialog.Filter = "PNG Images (*.png)|*.png"
        $openFileDialog.Title = "Select Minecraft Skin"
        
        if ($openFileDialog.ShowDialog() -eq "OK") {
            try {
                $newSkinPath = Set-MinecraftSkin -SkinFilePath $openFileDialog.FileName -Username $Username -Version $Version
                
                # Dispose old image and stream
                if ($previewLabel.Image) {
                    $previewLabel.Image.Dispose()
                }
                if ($previewLabel.Tag -is [System.IO.MemoryStream]) {
                    $previewLabel.Tag.Dispose()
                }
                
                # Load new image without locking the file
                $bytes = [System.IO.File]::ReadAllBytes($newSkinPath)
                $ms = New-Object System.IO.MemoryStream($bytes, 0, $bytes.Length)
                $skinImage = [System.Drawing.Image]::FromStream($ms)
                $previewLabel.Image = $skinImage
                $previewLabel.Text = ""
                $previewLabel.Tag = $ms
                
                [System.Windows.Forms.MessageBox]::Show("Skin saved as: $Username.png`n`nIt will automatically load with OfflineSkins mod!", "Success", "OK", "Information")
            } catch {
                [System.Windows.Forms.MessageBox]::Show("Failed to apply skin: $($_.Exception.Message)", "Error", "OK", "Error")
            }
        }
    })
    $skinForm.Controls.Add($uploadButton)
    
    # Info
    $infoLabel = New-Object System.Windows.Forms.Label
    $infoLabel.Location = New-Object System.Drawing.Point(240, 120)
    $infoLabel.Size = New-Object System.Drawing.Size(340, 120)
    $infoLabel.Text = "✅ Integrated with OfflineSkins mod!`n`nYour skin is saved to:`nconfig/offlineskins/$Username.png`n`nMake sure you have the OfflineSkins mod installed for Fabric!`n`nSupports 64x32 or 64x64 format skins.`nWorks with 3D Skin Layers and other cosmetic mods!"
    $infoLabel.Font = New-Object System.Drawing.Font("Segoe UI", 8)
    $infoLabel.ForeColor = [System.Drawing.Color]::LightGray
    $skinForm.Controls.Add($infoLabel)
    
    # Skin location label
    $locationLabel = New-Object System.Windows.Forms.Label
    $locationLabel.Location = New-Object System.Drawing.Point(20, 280)
    $locationLabel.Size = New-Object System.Drawing.Size(560, 60)
    $skinsDir = Get-SkinPath -Version $Version
    $locationLabel.Text = "Skin saved to:`n$skinsDir\$Username.png"
    $locationLabel.Font = New-Object System.Drawing.Font("Consolas", 8)
    $locationLabel.ForeColor = [System.Drawing.Color]::Yellow
    $skinForm.Controls.Add($locationLabel)
    
    # Instructions
    $instructionsLabel = New-Object System.Windows.Forms.Label
    $instructionsLabel.Location = New-Object System.Drawing.Point(20, 350)
    $instructionsLabel.Size = New-Object System.Drawing.Size(560, 60)
    $instructionsLabel.Text = "After uploading, your skin will automatically load when you start the game!`n`nNo commands needed - just launch Minecraft with OfflineSkins mod installed."
    $instructionsLabel.Font = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
    $instructionsLabel.ForeColor = [System.Drawing.Color]::LightGreen
    $skinForm.Controls.Add($instructionsLabel)
    
    # Close Button
    $closeButton = New-Object System.Windows.Forms.Button
    $closeButton.Location = New-Object System.Drawing.Point(240, 430)
    $closeButton.Size = New-Object System.Drawing.Size(340, 40)
    $closeButton.Text = "Close"
    $closeButton.Font = New-Object System.Drawing.Font("Segoe UI", 10)
    $closeButton.ForeColor = [System.Drawing.Color]::White
    $closeButton.BackColor = [System.Drawing.Color]::FromArgb(70, 70, 70)
    $closeButton.FlatStyle = "Flat"
    $closeButton.Add_Click({
        if ($previewLabel.Image) {
            $previewLabel.Image.Dispose()
        }
        if ($previewLabel.Tag -is [System.IO.MemoryStream]) {
            $previewLabel.Tag.Dispose()
        }
        $skinForm.Close()
    })
    $skinForm.Controls.Add($closeButton)
    
    $skinForm.Add_FormClosing({
        if ($previewLabel.Image) {
            $previewLabel.Image.Dispose()
        }
        if ($previewLabel.Tag -is [System.IO.MemoryStream]) {
            $previewLabel.Tag.Dispose()
        }
    })
    
    [void]$skinForm.ShowDialog()
}
#endregion

#region Game Launcher Core
function Start-MinecraftGame {
    param(
        [string]$Version,
        [string]$LoaderType,
        [string]$Username,
        [int]$Memory
    )
    
    $versionsDir = Join-Path $scriptDir "versions"
    $versionDir = Join-Path $versionsDir $Version
    $libsDir = Join-Path $versionDir "libraries"
    $natives = Join-Path $versionDir "natives"
    $gameDir = $versionDir
    $runtimeDir = Join-Path $scriptDir "runtime"
    
    $uuid = Get-ComputerUUID
    
    $versionJson = $null
    
    if ($LoaderType -eq "FABRIC") {
        $fabricJsons = Get-ChildItem (Join-Path $versionDir "versions") -Filter "fabric-loader*.json" -ErrorAction SilentlyContinue
        if ($fabricJsons) {
            $versionJson = $fabricJsons[0].FullName
        }
    }
    elseif ($LoaderType -eq "FORGE") {
        $forgeJsons = Get-ChildItem (Join-Path $versionDir "versions") -Filter "forge*.json" -ErrorAction SilentlyContinue
        if ($forgeJsons) {
            $versionJson = $forgeJsons[0].FullName
        }
    }
    
    # Default to vanilla if no mod loader JSON found
    if (-not $versionJson -or -not (Test-Path $versionJson)) {
        $versionJson = Join-Path $versionDir "versions\$Version.json"
        $LoaderType = "VANILLA"
    }
    
    if (-not $versionJson -or -not (Test-Path $versionJson)) {
        $versionJson = Join-Path $versionDir "versions\$Version.json"
        $LoaderType = "VANILLA"
    }
    
    if (-not (Test-Path $versionJson)) {
        throw "Version JSON not found: $versionJson"
    }
    
    $jsonContent = Get-Content $versionJson -Raw | ConvertFrom-Json
    
    $javaVersion = 25
    if ($jsonContent.javaVersion.majorVersion) {
        $javaVersion = $jsonContent.javaVersion.majorVersion
    }
    
    $javaExe = Join-Path $runtimeDir "$javaVersion\bin\java.exe"
    if (-not (Test-Path $javaExe)) {
        throw "Java $javaVersion not found at: $javaExe"
    }
    
    $mainClass = $jsonContent.mainClass
    
    $assetIndex = $Version
    $assetIndexFiles = Get-ChildItem (Join-Path $versionDir "assets\indexes") -Filter "*.json" -ErrorAction SilentlyContinue
    if ($assetIndexFiles) {
        $assetIndex = $assetIndexFiles[0].BaseName
    }
    
    $libraries = @()
    
    # Match the batch file logic EXACTLY: create temp files for debugging
    $tempMerged = Join-Path $scriptDir "~temp_libs_merged.txt"
    $tempFiltered = Join-Path $scriptDir "~temp_libs_filtered.txt"
    
    if ($LoaderType -eq "FABRIC" -or $LoaderType -eq "FORGE") {
        # Step 1: Merge mod loader + vanilla libraries
        $allLibs = @()
        
        # Add mod loader libraries first
        if ($jsonContent.libraries) {
            foreach ($lib in $jsonContent.libraries) {
                if ($lib.name) {
                    $allLibs += $lib.name
                }
            }
        }
        
        # Then add vanilla libraries
        $vanillaJsonPath = Join-Path $versionDir "versions\$Version.json"
        if (Test-Path $vanillaJsonPath) {
            $vanillaJson = Get-Content $vanillaJsonPath -Raw | ConvertFrom-Json
            if ($vanillaJson.libraries) {
                foreach ($lib in $vanillaJson.libraries) {
                    if ($lib.name) {
                        $allLibs += $lib.name
                    }
                }
            }
        }
        
        # Remove duplicates (keep order - mod loader first)
        $allLibs = $allLibs | Select-Object -Unique
        
        # Write merged libraries to temp file (for debugging)
        $allLibs | Out-File -FilePath $tempMerged -Encoding ASCII
        
        # Step 2: Filter to keep only latest version of each library
        $latest = @{}
        foreach ($lib in $allLibs) {
            $parts = $lib -split ':'
            if ($parts.Length -ge 3) {
                $key = "$($parts[0]):$($parts[1])"
                # If there's a classifier (4th part), add it to the key
                if ($parts.Length -eq 4) {
                    $key += ":$($parts[3])"
                }
                $ver = $parts[2]
                
                if (-not $latest.ContainsKey($key)) {
                    $latest[$key] = $ver
                } else {
                    # Keep newer version
                    if ($ver -gt $latest[$key]) {
                        $latest[$key] = $ver
                    }
                }
            }
        }
        
        # Step 3: Rebuild library list from filtered versions
        $libraries = @()
        foreach ($key in $latest.Keys) {
            $keyParts = $key -split ':'
            $group = $keyParts[0]
            $artifact = $keyParts[1]
            $classifier = if ($keyParts.Length -eq 3) { $keyParts[2] } else { $null }
            $ver = $latest[$key]
            
            if ($classifier) {
                $libraries += "$group`:$artifact`:$ver`:$classifier"
            } else {
                $libraries += "$group`:$artifact`:$ver"
            }
        }
        
        # Write filtered libraries to temp file (for debugging)
        $libraries | Out-File -FilePath $tempFiltered -Encoding ASCII
    }
    else {
        # Vanilla: just get libraries from JSON and filter
        $allLibs = @()
        if ($jsonContent.libraries) {
            foreach ($lib in $jsonContent.libraries) {
                if ($lib.name) {
                    $allLibs += $lib.name
                }
            }
        }
        
        # Write merged libraries (same as all libs for vanilla)
        $allLibs | Out-File -FilePath $tempMerged -Encoding ASCII
        
        # Filter to keep only latest version of each library
        $latest = @{}
        foreach ($lib in $allLibs) {
            $parts = $lib -split ':'
            if ($parts.Length -eq 3) {
                $key = "$($parts[0]):$($parts[1])"
                $ver = $parts[2]
                
                if (-not $latest.ContainsKey($key)) {
                    $latest[$key] = $ver
                } else {
                    if ($ver -gt $latest[$key]) {
                        $latest[$key] = $ver
                    }
                }
            }
        }
        
        # Rebuild library list
        $libraries = @()
        foreach ($key in $latest.Keys) {
            $group, $artifact = $key -split ':'
            $ver = $latest[$key]
            $libraries += "$group`:$artifact`:$ver"
        }
        
        # Write filtered libraries to temp file (for debugging)
        $libraries | Out-File -FilePath $tempFiltered -Encoding ASCII
    }
    
    $classpathEntries = @()
    
    # Build classpath with RELATIVE paths
    foreach ($lib in $libraries) {
        $parts = $lib -split ':'
        if ($parts.Length -ge 3) {
            $group = $parts[0]
            $artifact = $parts[1]
            $libVersion = $parts[2]
            $classifier = if ($parts.Length -eq 4) { $parts[3] } else { $null }
            
            $groupPath = $group -replace '\.', '\'
            
            # Build JAR name with classifier if present
            if ($classifier) {
                $jarName = "$artifact-$libVersion-$classifier.jar"
            } else {
                $jarName = "$artifact-$libVersion.jar"
            }
            
            # Use FULL path to check if file exists
            $fullPath = Join-Path $libsDir "$groupPath\$artifact\$libVersion\$jarName"
            
            # But use RELATIVE path in classpath
            $relPath = "versions\$Version\libraries\$groupPath\$artifact\$libVersion\$jarName"
            
            if (Test-Path $fullPath) {
                $classpathEntries += $relPath
            }
        }
    }
    
    # Add client JAR with RELATIVE path (EXCEPT for Forge)
    if ($LoaderType -ne "FORGE") {
        $clientJar = Join-Path $versionDir "versions\$Version-client.jar"
        
        if (-not (Test-Path $clientJar)) {
            $clientJar = Join-Path $versionDir "versions\$Version.jar"
        }
        
        if (-not (Test-Path $clientJar)) {
            $versionsFolder = Join-Path $versionDir "versions"
            $existingFiles = ""
            if (Test-Path $versionsFolder) {
                $files = Get-ChildItem $versionsFolder -Filter "*.jar"
                $existingFiles = ($files | ForEach-Object { $_.Name }) -join ", "
            }
            
            throw "Client JAR not found!`n`nLooking for: $Version-client.jar or $Version.jar`nIn folder: $versionsFolder`nFound JARs: $existingFiles"
        }
        
        # Add as relative path
        if (Test-Path (Join-Path $versionDir "versions\$Version-client.jar")) {
            $classpathEntries += "versions\$Version\versions\$Version-client.jar"
        } else {
            $classpathEntries += "versions\$Version\versions\$Version.jar"
        }
    }
    
    $classpath = $classpathEntries -join ';'
    
    $javaArgs = @(
        "-Djava.library.path=`"$natives`""
        "-Xmx$($Memory)G"
        "-Xmn128M"
        "-Dorg.lwjgl.librarypath=`"$natives`""
    )

    # authlib-injector — patches the client to trust our skin server
    # (texture domain whitelist + signature key + API redirection)
    $aliJar = Join-Path $scriptDir "runtime\authlib-injector\authlib-injector.jar"
    if (Test-Path $aliJar) {
        $skinServerAddr = $null

        # 1) Manual override via SKIN_SERVER= in config.txt (optional)
        $cfgFile = Join-Path $scriptDir "config.txt"
        if (Test-Path $cfgFile) {
            $line = (Get-Content $cfgFile | Where-Object { $_ -match '^SKIN_SERVER=(.+)$' } | Select-Object -First 1)
            if ($line -match '^SKIN_SERVER=(.+)$') { $skinServerAddr = $matches[1].Trim() }
        }

        # 2) Zero-config: UDP broadcast discovery on the LAN (port 25568)
        if (-not $skinServerAddr) {
            try {
                $udp = New-Object System.Net.Sockets.UdpClient
                $udp.EnableBroadcast = $true
                $udp.Client.ReceiveTimeout = 1500
                $probe = [System.Text.Encoding]::ASCII.GetBytes("MCSKINSERVER_DISCOVER")
                $bcast = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Broadcast, 25568)
                [void]$udp.Send($probe, $probe.Length, $bcast)
                $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
                $reply  = $udp.Receive([ref]$remote)
                $msg    = [System.Text.Encoding]::ASCII.GetString($reply)
                if ($msg -match '^MCSKINSERVER:(\d+)$') {
                    $skinServerAddr = "$($remote.Address.IPAddressToString):$($matches[1])"
                }
                $udp.Close()
            } catch {
                try { $udp.Close() } catch {}
            }
        }

        if ($skinServerAddr) {
            $javaArgs = @("-javaagent:`"$aliJar`"=http://$skinServerAddr") + $javaArgs

            # Auto-sync: push this player's local skin to the host's skin server
            # so friends only need to add their skin in their OWN Skins tab
            try {
                $mySkin = Join-Path $scriptDir "skins\$Username.png"
                if (Test-Path $mySkin) {
                    $myModel = Get-SkinModel -Username $Username
                    $bytes   = [System.IO.File]::ReadAllBytes($mySkin)
                    Invoke-WebRequest -Uri "http://$skinServerAddr/upload/$Username`?model=$myModel" `
                        -Method Post -Body $bytes -ContentType "image/png" `
                        -UseBasicParsing -TimeoutSec 5 | Out-Null
                }
            } catch { }   # upload is best-effort; game launches regardless
        }
        # No skin server found → launch without the agent (vanilla behavior, no skins)
    }
    
    # Add JVM arguments from JSON (ONLY for Forge!)
    if ($LoaderType -eq "FORGE" -and $jsonContent.arguments -and $jsonContent.arguments.jvm) {
        foreach ($arg in $jsonContent.arguments.jvm) {
            if ($arg -is [string]) {
                # Replace variables in JVM arguments
                $processedArg = $arg
                $processedArg = $processedArg -replace '\$\{library_directory\}', (Join-Path $versionDir "libraries")
                $processedArg = $processedArg -replace '\$\{classpath_separator\}', ';'
                $processedArg = $processedArg -replace '\$\{version_name\}', $Version
                
                $javaArgs += $processedArg
            }
        }
    }
    
    # Add classpath and main class
    $javaArgs += "-cp"
    $javaArgs += "`"$classpath`""
    $javaArgs += $mainClass
    
    $gameArgs = @(
        "--username", $Username
        "--version", $Version
        "--gameDir", "`"$gameDir`""
        "--assetsDir", "`"$(Join-Path $versionDir 'assets')`""
        "--assetIndex", $assetIndex
        "--uuid", $uuid
        "--accessToken", "0"
        "--userType", "legacy"
    )
    
    # Add game arguments from JSON (ONLY for Forge!)
    if ($LoaderType -eq "FORGE" -and $jsonContent.arguments -and $jsonContent.arguments.game) {
        foreach ($arg in $jsonContent.arguments.game) {
            if ($arg -is [string]) {
                $gameArgs += $arg
            }
        }
    }
    
    $allArgs = $javaArgs + $gameArgs
    
    # Build the command line
    $argsString = $allArgs -join ' '
    
    # Create a temporary batch file to run the command
    $tempBatchFile = Join-Path $scriptDir "~temp_launch.bat"
    $batchContent = @"
@echo off
cd /d "$scriptDir"
"$javaExe" $argsString
"@
    
    $batchContent | Out-File -FilePath $tempBatchFile -Encoding ASCII
    
    # Run the batch file hidden
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $tempBatchFile
    $psi.WorkingDirectory = $scriptDir
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    
    $process = [System.Diagnostics.Process]::Start($psi)
    
    # Clean up temp files after a short delay
    Start-Sleep -Milliseconds 500
    if (Test-Path $tempBatchFile) { Remove-Item $tempBatchFile -Force }
    $tempMerged = Join-Path $scriptDir "~temp_libs_merged.txt"
    $tempFiltered = Join-Path $scriptDir "~temp_libs_filtered.txt"
    if (Test-Path $tempMerged) { Remove-Item $tempMerged -Force }
    if (Test-Path $tempFiltered) { Remove-Item $tempFiltered -Force }
    
    # Clean up temp files
    $tempMerged = Join-Path $scriptDir "~temp_libs_merged.txt"
    $tempFiltered = Join-Path $scriptDir "~temp_libs_filtered.txt"
    if (Test-Path $tempMerged) { Remove-Item $tempMerged -Force }
    if (Test-Path $tempFiltered) { Remove-Item $tempFiltered -Force }
    
    return 0
}
#endregion

#region Configuration
function Get-Config {
    $configFile = Join-Path $scriptDir "config.txt"
    $config = @{
        Username = ""
        Memory = 2
        LastVersion = ""
        LastLoader = "VANILLA"
    }
    
    if (Test-Path $configFile) {
        Get-Content $configFile | ForEach-Object {
            if ($_ -match "NICK=(.+)") { $config.Username = $matches[1] }
            if ($_ -match "max_MEM=(.+)") { $config.Memory = [int]$matches[1] }
            if ($_ -match "LAST_VERSION=(.+)") { $config.LastVersion = $matches[1] }
            if ($_ -match "LAST_LOADER=(.+)") { $config.LastLoader = $matches[1] }
        }
    }
    
    return $config
}

function Save-Config {
    param($Username, $Memory, $LastVersion, $LastLoader)
    
    $configFile = Join-Path $scriptDir "config.txt"
    @"
NICK=$Username
max_MEM=$Memory
LAST_VERSION=$LastVersion
LAST_LOADER=$LastLoader
"@ | Out-File -FilePath $configFile -Encoding ASCII
}
#endregion

#region Server Management
function Get-ServerPath {
    param([string]$Version, [string]$LoaderType)
    
    $serverName = "$LoaderType-$Version".ToLower()
    $serverDir = Join-Path $scriptDir "servers\$serverName"
    
    if (-not (Test-Path $serverDir)) {
        New-Item -Path $serverDir -ItemType Directory -Force | Out-Null
    }
    
    return $serverDir
}

function Download-ServerJar {
    param(
        [string]$Version,
        [string]$LoaderType,
        [string]$ServerDir
    )
    
    $serverJar = Join-Path $ServerDir "server.jar"
    
    # If server.jar already exists, skip download
    if (Test-Path $serverJar) {
        return $true
    }
    
    Write-Host "Downloading $LoaderType server JAR for version $Version..."
    
    try {
        switch ($LoaderType.ToLower()) {
            "vanilla" {
                # Download Vanilla server from Mojang
                $manifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest.json"
                $manifest = Invoke-RestMethod -Uri $manifestUrl
                
                $versionData = $manifest.versions | Where-Object { $_.id -eq $Version } | Select-Object -First 1
                if (-not $versionData) {
                    throw "Version $Version not found in manifest"
                }
                
                $versionJson = Invoke-RestMethod -Uri $versionData.url
                $serverUrl = $versionJson.downloads.server.url
                
                if (-not $serverUrl) {
                    throw "No server download available for version $Version"
                }
                
                Invoke-WebRequest -Uri $serverUrl -OutFile $serverJar
                Write-Host "Vanilla server downloaded successfully!"
                return $true
            }
            
            "fabric" {
                # Download Fabric server launcher
                $fabricVersion = "0.19.2" # Latest stable Fabric loader
                $fabricUrl = "https://meta.fabricmc.net/v2/versions/loader/$Version/$fabricVersion/1.0.1/server/jar"
                
                Invoke-WebRequest -Uri $fabricUrl -OutFile $serverJar
                Write-Host "Fabric server downloaded successfully!"
                return $true
            }
            
            "forge" {
                # Download Forge installer
                Write-Host "Downloading Forge installer for $Version..."
                
                try {
                    # Get Forge promotions (recommended versions)
                    $promotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json"
                    $promotions = Invoke-RestMethod -Uri $promotionsUrl
                    
                    # Try to find recommended version for this Minecraft version
                    $promoKey = "$Version-recommended"
                    $latestKey = "$Version-latest"
                    
                    $forgeVersion = $null
                    if ($promotions.promos.$promoKey) {
                        $forgeVersion = $promotions.promos.$promoKey
                        Write-Host "Found recommended Forge version: $forgeVersion"
                    } elseif ($promotions.promos.$latestKey) {
                        $forgeVersion = $promotions.promos.$latestKey
                        Write-Host "Found latest Forge version: $forgeVersion"
                    }
                    
                    if (-not $forgeVersion) {
                        # If no promotion found, try to get from version list
                        Write-Host "No promoted version found, checking available versions..."
                        $versionListUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml"
                        $versionXml = Invoke-RestMethod -Uri $versionListUrl
                        
                        # Find versions matching this Minecraft version
                        $matchingVersions = $versionXml.metadata.versioning.versions.version | Where-Object { $_ -like "$Version-*" }
                        
                        if ($matchingVersions) {
                            # Get the latest matching version
                            $forgeVersion = ($matchingVersions | Select-Object -Last 1) -replace "$Version-", ""
                            Write-Host "Found Forge version: $forgeVersion"
                        } else {
                            throw "No Forge version found for Minecraft $Version"
                        }
                    }
                } catch {
                    throw "Failed to fetch Forge version info: $_`n`nPlease download Forge installer manually from: https://files.minecraftforge.net/"
                }
                
                $installerUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/$Version-$forgeVersion/forge-$Version-$forgeVersion-installer.jar"
                $installerPath = Join-Path $ServerDir "forge-installer.jar"
                
                Write-Host "Downloading from: $installerUrl"
                
                try {
                    Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath
                } catch {
                    throw "Failed to download Forge installer: $_`n`nPlease download manually from: https://files.minecraftforge.net/`nPlace installer in: $ServerDir"
                }
                
                # Run Forge installer
                Write-Host "Running Forge installer (this may take a few minutes)..."
                
                # Get Java path
                $javaExe = $null
                foreach ($javaVer in @("25", "23", "21", "17", "8")) {
                    $testJava = Join-Path $scriptDir "runtime\$javaVer\bin\java.exe"
                    if (Test-Path $testJava) {
                        $javaExe = $testJava
                        break
                    }
                }
                
                if (-not $javaExe) {
                    throw "Java runtime not found - cannot install Forge"
                }
                
                # Run installer with --installServer flag
                $installProcess = Start-Process -FilePath $javaExe -ArgumentList "-jar","`"$installerPath`"","--installServer" -WorkingDirectory $ServerDir -Wait -PassThru -NoNewWindow
                
                if ($installProcess.ExitCode -eq 0) {
                    # Find the generated server JAR (usually forge-VERSION-shim.jar or similar)
                    $forgeJars = Get-ChildItem -Path $ServerDir -Filter "forge-*.jar" | Where-Object { $_.Name -notlike "*installer*" }
                    
                    if ($forgeJars) {
                        # Rename to server.jar
                        Move-Item -Path $forgeJars[0].FullName -Destination $serverJar -Force
                        
                        # Also check for run.bat/run.sh that Forge creates
                        $forgeBat = Join-Path $ServerDir "run.bat"
                        if (Test-Path $forgeBat) {
                            # Forge creates its own run script, we'll use that
                            Write-Host "Forge installed with run.bat - server will use Forge's launcher"
                        }
                        
                        Write-Host "Forge server installed successfully!"
                        return $true
                    } else {
                        throw "Forge installation completed but server JAR not found"
                    }
                } else {
                    throw "Forge installer failed with exit code: $($installProcess.ExitCode)"
                }
            }
            
            "paper" {
                # Download Paper server using API v2
                Write-Host "Downloading Paper server for $Version..."
                
                try {
                    # Get version builds info
                    $buildsUrl = "https://api.papermc.io/v2/projects/paper/versions/$Version/builds"
                    $buildsData = Invoke-RestMethod -Uri $buildsUrl
                    
                    # Find latest build
                    $latestBuild = $buildsData.builds | Select-Object -Last 1
                    
                    if (-not $latestBuild) {
                        throw "No Paper builds found for version $Version"
                    }
                    
                    $buildNumber = $latestBuild.build
                    $jarName = $latestBuild.downloads.application.name
                    
                    # Construct download URL
                    $paperUrl = "https://api.papermc.io/v2/projects/paper/versions/$Version/builds/$buildNumber/downloads/$jarName"
                    
                    Write-Host "Downloading Paper build #$buildNumber..."
                    Invoke-WebRequest -Uri $paperUrl -OutFile $serverJar
                    
                    Write-Host "Paper server downloaded successfully!"
                    return $true
                } catch {
                    throw "Failed to download Paper server: $_`n`nPlease download manually from: https://papermc.io/downloads/paper"
                }
            }
            
            "purpur" {
                # Download Purpur server using API v2
                Write-Host "Downloading Purpur server for $Version..."
                
                try {
                    # Purpur has a simple latest download endpoint
                    $purpurUrl = "https://api.purpurmc.org/v2/purpur/$Version/latest/download"
                    
                    Write-Host "Downloading latest Purpur build..."
                    Invoke-WebRequest -Uri $purpurUrl -OutFile $serverJar
                    
                    Write-Host "Purpur server downloaded successfully!"
                    return $true
                } catch {
                    throw "Failed to download Purpur server: $_`n`nPlease download manually from: https://purpurmc.org/downloads/purpur"
                }
            }
        }
    } catch {
        Write-Host "ERROR: Failed to download server JAR: $_"
        return $false
    }
}

function Initialize-MinecraftServer {
    param(
        [string]$Version,
        [string]$LoaderType,
        [string]$Port = "25565",
        [string]$Gamemode = "survival",
        [string]$Difficulty = "normal",
        [int]$MaxPlayers = 20,
        [bool]$PVP = $true,
        [int]$Memory = 2
    )
    
    $serverDir = Get-ServerPath -Version $Version -LoaderType $LoaderType
    
    # Create eula.txt (auto-accept)
    $eulaFile = Join-Path $serverDir "eula.txt"
    "eula=true" | Out-File -FilePath $eulaFile -Encoding ASCII -Force
    
    # Create server.properties
    $propsFile = Join-Path $serverDir "server.properties"
    
    # Handle hardcore mode (hardcore overrides gamemode)
    $isHardcore = ($Gamemode -eq "Hardcore")
    $actualGamemode = if ($isHardcore) { "survival" } else { $Gamemode.ToLower() }
    
    $properties = @"
server-port=$Port
gamemode=$actualGamemode
difficulty=$($Difficulty.ToLower())
hardcore=$($isHardcore.ToString().ToLower())
max-players=$MaxPlayers
pvp=$($PVP.ToString().ToLower())
view-distance=10
online-mode=false
white-list=false
enable-command-block=true
spawn-protection=0
motd=Minecraft Portable Server - $Version
"@
    
    $properties | Out-File -FilePath $propsFile -Encoding ASCII -Force
    
    return $serverDir
}

function Start-MinecraftServer {
    param(
        [string]$Version,
        [string]$LoaderType,
        [int]$Memory,
        [hashtable]$Config
    )
    
    # Initialize server with config
    $serverDir = Initialize-MinecraftServer -Version $Version -LoaderType $LoaderType `
        -Port $Config.Port -Gamemode $Config.Gamemode -Difficulty $Config.Difficulty `
        -MaxPlayers $Config.MaxPlayers -PVP $Config.PVP -Memory $Memory
    
    # Find or download server JAR
    $serverJar = Join-Path $serverDir "server.jar"
    
    if (-not (Test-Path $serverJar)) {
        Write-Host "Server JAR not found, attempting to download..."
        
        $downloadSuccess = Download-ServerJar -Version $Version -LoaderType $LoaderType -ServerDir $serverDir
        
        if (-not $downloadSuccess -or -not (Test-Path $serverJar)) {
            throw "Failed to download server JAR for $LoaderType $Version`n`nPlease manually place server.jar in:`n$serverDir"
        }
    }
    
    # Get Java path
    $javaExe = $null
    
    # Try different Java runtime locations
    foreach ($javaVer in @("25", "23", "21", "17", "8")) {
        $testJava = Join-Path $scriptDir "runtime\$javaVer\bin\java.exe"
        if (Test-Path $testJava) {
            $javaExe = $testJava
            break
        }
    }
    
    if (-not $javaExe) {
        throw "Java runtime not found"
    }
    
    # Build server start command
    $serverArgs = @(
        "-Xmx$($Memory)G"
        "-Xms$($Memory)G"
        "-jar"
        "`"$serverJar`""
        "nogui"
    )
    
    # Create batch file to run server
    $batchFile = Join-Path $serverDir "start_server.bat"
    $batchContent = @"
@echo off
title Minecraft Server - $LoaderType $Version
cd /d "$serverDir"
echo Starting Minecraft Server...
echo Version: $Version
echo Loader: $LoaderType
echo Port: $($Config.Port)
echo Memory: $($Memory)GB
echo.
"$javaExe" $($serverArgs -join ' ')
echo.
echo Server stopped. Press any key to close...
pause >nul
"@
    
    $batchContent | Out-File -FilePath $batchFile -Encoding ASCII -Force
    
    # Start server in new window — capture process so we can monitor when it exits
    $proc = Start-Process -FilePath $batchFile -WorkingDirectory $serverDir -PassThru
    
    return @{
        ServerDir = $serverDir
        Port      = $Config.Port
        Running   = $true
        Process   = $proc
    }
}
#endregion

#region Mod Management
function Get-ModFolderPath {
    param(
        [string]$Version,
        [string]$Mode,       # "Client" or "Server"
        [string]$LoaderType  # Only used when Mode = "Server": Fabric/Forge/Paper/Purpur
    )

    if ($Mode -eq "Server") {
        $serverDir = Get-ServerPath -Version $Version -LoaderType $LoaderType

        if ($LoaderType -in @("Paper", "Purpur")) {
            # Paper/Purpur use a Bukkit-style plugins folder, not a mods folder
            $folder = Join-Path $serverDir "plugins"
        } else {
            $folder = Join-Path $serverDir "mods"
        }
    }
    else {
        $folder = Join-Path $scriptDir "versions\$Version\mods"
    }

    if (-not (Test-Path $folder)) {
        New-Item -Path $folder -ItemType Directory -Force | Out-Null
    }

    return $folder
}

function Get-InstalledMods {
    param([string]$FolderPath)

    $mods = @()

    if ([string]::IsNullOrWhiteSpace($FolderPath) -or -not (Test-Path $FolderPath)) {
        return $mods
    }

    $files = Get-ChildItem -Path $FolderPath -File -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -like "*.jar" -or $_.Name -like "*.jar.disabled"
    }

    foreach ($file in $files) {
        $isDisabled = $file.Name -like "*.jar.disabled"
        $displayName = if ($isDisabled) {
            $file.Name.Substring(0, $file.Name.Length - ".disabled".Length)
        } else {
            $file.Name
        }

        $mods += [PSCustomObject]@{
            FileName    = $file.Name
            DisplayName = $displayName
            Enabled     = -not $isDisabled
            SizeKB      = [Math]::Round($file.Length / 1KB, 1)
            FullPath    = $file.FullName
        }
    }

    return $mods | Sort-Object -Property DisplayName
}

function Add-ModFiles {
    param(
        [string]$FolderPath,
        [string[]]$SourcePaths
    )

    $added = 0
    $skipped = @()

    foreach ($sourcePath in $SourcePaths) {
        if (-not (Test-Path $sourcePath)) {
            continue
        }

        $fileName = [System.IO.Path]::GetFileName($sourcePath)
        $destPath = Join-Path $FolderPath $fileName

        if (Test-Path $destPath) {
            $skipped += $fileName
            continue
        }

        try {
            Copy-Item -Path $sourcePath -Destination $destPath -Force -ErrorAction Stop
            $added++
        }
        catch {
            $skipped += $fileName
        }
    }

    return @{
        Added   = $added
        Skipped = $skipped
    }
}

function Remove-ModFile {
    param(
        [string]$FolderPath,
        [string]$FileName
    )

    $filePath = Join-Path $FolderPath $FileName

    if (Test-Path $filePath) {
        Remove-Item -Path $filePath -Force
        return $true
    }

    return $false
}

function Set-ModEnabled {
    param(
        [string]$FolderPath,
        [string]$FileName,
        [bool]$Enabled
    )

    $currentPath = Join-Path $FolderPath $FileName

    if (-not (Test-Path $currentPath)) {
        return $false
    }

    if ($Enabled -and $FileName -like "*.jar.disabled") {
        $newName = $FileName.Substring(0, $FileName.Length - ".disabled".Length)
        Rename-Item -Path $currentPath -NewName $newName -Force
        return $true
    }
    elseif ((-not $Enabled) -and $FileName -like "*.jar" -and $FileName -notlike "*.jar.disabled") {
        $newName = "$FileName.disabled"
        Rename-Item -Path $currentPath -NewName $newName -Force
        return $true
    }

    return $false
}

function Open-ModsFolder {
    param([string]$FolderPath)

    Start-Process -FilePath "explorer.exe" -ArgumentList "`"$FolderPath`""
}
#endregion

function Get-OfflineUUID {
    param(
        [string]$Username
    )
    
    # Calculate offline UUID from username (same as Minecraft server does)
    $input = "OfflinePlayer:$Username"
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($input))
    
    # Set version to 3 (name-based UUID) and variant bits
    $hash[6] = ($hash[6] -band 0x0F) -bor 0x30
    $hash[8] = ($hash[8] -band 0x3F) -bor 0x80
    
    # Format as UUID
    $uuid = "{0:x2}{1:x2}{2:x2}{3:x2}-{4:x2}{5:x2}-{6:x2}{7:x2}-{8:x2}{9:x2}-{10:x2}{11:x2}{12:x2}{13:x2}{14:x2}{15:x2}" -f `
        $hash[0], $hash[1], $hash[2], $hash[3], `
        $hash[4], $hash[5], `
        $hash[6], $hash[7], `
        $hash[8], $hash[9], `
        $hash[10], $hash[11], $hash[12], $hash[13], $hash[14], $hash[15]
    
    return $uuid
}

function Get-UsernameFromDatFile {
    param(
        [string]$FilePath
    )
    
    try {
        # Read file as bytes
        $bytes = [System.IO.File]::ReadAllBytes($FilePath)
        
        # Decompress GZIP (NBT files are GZIP compressed)
        $memStream = New-Object System.IO.MemoryStream
        $memStream.Write($bytes, 0, $bytes.Length)
        $memStream.Position = 0
        
        $gzipStream = New-Object System.IO.Compression.GZipStream($memStream, [System.IO.Compression.CompressionMode]::Decompress)
        $decompressed = New-Object System.IO.MemoryStream
        $gzipStream.CopyTo($decompressed)
        $gzipStream.Close()
        $memStream.Close()
        
        $data = $decompressed.ToArray()
        $decompressed.Close()
        
        # Convert to string and search for username patterns
        $text = [System.Text.Encoding]::UTF8.GetString($data)
        
        # Look for Minecraft username patterns (3-16 chars, alphanumeric + underscore)
        # Common NBT tags: "lastKnownName", or just the username itself
        $patterns = @(
            'lastKnownName.{1,5}([A-Za-z0-9_]{3,16})',  # Bukkit/Spigot/Paper
            '([A-Za-z0-9_]{3,16})\x00',                  # Username followed by null byte
            'playerName.{1,5}([A-Za-z0-9_]{3,16})'       # Some mods
        )
        
        foreach ($pattern in $patterns) {
            if ($text -match $pattern) {
                $username = $matches[1]
                # Validate it looks like a real username (not random data)
                if ($username -match '^[A-Za-z0-9_]{3,16}$' -and $username -notmatch '^\d+$') {
                    return $username
                }
            }
        }
        
        # Fallback: Search for any valid username pattern in the data
        $allMatches = [regex]::Matches($text, '[A-Za-z][A-Za-z0-9_]{2,15}')
        $candidates = @{}
        
        foreach ($match in $allMatches) {
            $candidate = $match.Value
            # Count occurrences (username usually appears multiple times)
            if (-not $candidates.ContainsKey($candidate)) {
                $candidates[$candidate] = 0
            }
            $candidates[$candidate]++
        }
        
        # Return most common candidate (likely the username)
        $mostCommon = $candidates.GetEnumerator() | Sort-Object -Property Value -Descending | Select-Object -First 1
        if ($mostCommon -and $mostCommon.Value -ge 2) {
            return $mostCommon.Key
        }
        
        return $null
        
    } catch {
        Write-Host "Could not extract username from $FilePath : $_"
        return $null
    }
}

function Show-WorldImportDialog {
    # Create import dialog
    $importForm = New-Object System.Windows.Forms.Form
    $importForm.Text = "Import LAN/Singleplayer World"
    $importForm.Size = New-Object System.Drawing.Size(600, 500)
    $importForm.StartPosition = "CenterScreen"
    $importForm.BackColor = [System.Drawing.Color]::FromArgb(40, 40, 40)
    $importForm.FormBorderStyle = "FixedDialog"
    $importForm.MaximizeBox = $false
    
    # Instructions
    $instructionsLabel = New-Object System.Windows.Forms.Label
    $instructionsLabel.Location = New-Object System.Drawing.Point(20, 20)
    $instructionsLabel.Size = New-Object System.Drawing.Size(550, 90)
    $instructionsLabel.Text = "⚠️ Converting LAN/Singleplayer World to Server`n`nLAN worlds use hardware-based UUIDs for players.`nServers use username-based UUIDs.`n`nThis tool will automatically detect usernames from cache and convert player data!"
    $instructionsLabel.ForeColor = [System.Drawing.Color]::White
    $instructionsLabel.Font = New-Object System.Drawing.Font("Segoe UI", 9)
    $importForm.Controls.Add($instructionsLabel)
    
    # World folder selection
    $worldLabel = New-Object System.Windows.Forms.Label
    $worldLabel.Location = New-Object System.Drawing.Point(20, 120)
    $worldLabel.Size = New-Object System.Drawing.Size(100, 20)
    $worldLabel.Text = "World Folder:"
    $worldLabel.ForeColor = [System.Drawing.Color]::White
    $worldLabel.Font = New-Object System.Drawing.Font("Segoe UI", 10)
    $importForm.Controls.Add($worldLabel)
    
    $worldPathBox = New-Object System.Windows.Forms.TextBox
    $worldPathBox.Location = New-Object System.Drawing.Point(20, 145)
    $worldPathBox.Size = New-Object System.Drawing.Size(450, 25)
    $worldPathBox.Font = New-Object System.Drawing.Font("Segoe UI", 9)
    $importForm.Controls.Add($worldPathBox)
    
    $browseButton = New-Object System.Windows.Forms.Button
    $browseButton.Location = New-Object System.Drawing.Point(480, 143)
    $browseButton.Size = New-Object System.Drawing.Size(80, 28)
    $browseButton.Text = "Browse..."
    $browseButton.BackColor = [System.Drawing.Color]::FromArgb(70, 70, 70)
    $browseButton.ForeColor = [System.Drawing.Color]::White
    $browseButton.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $browseButton.Add_Click({
        $folderBrowser = New-Object System.Windows.Forms.FolderBrowserDialog
        $folderBrowser.Description = "Select your Minecraft world folder"
        $folderBrowser.RootFolder = "MyComputer"
        
        if ($folderBrowser.ShowDialog() -eq "OK") {
            $worldPathBox.Text = $folderBrowser.SelectedPath
        }
    })
    $importForm.Controls.Add($browseButton)
    
    # Scan button
    $scanButton = New-Object System.Windows.Forms.Button
    $scanButton.Location = New-Object System.Drawing.Point(20, 185)
    $scanButton.Size = New-Object System.Drawing.Size(150, 35)
    $scanButton.Text = "Scan for Players"
    $scanButton.BackColor = [System.Drawing.Color]::FromArgb(50, 100, 150)
    $scanButton.ForeColor = [System.Drawing.Color]::White
    $scanButton.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $scanButton.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
    $importForm.Controls.Add($scanButton)
    
    # Results area
    $resultsLabel = New-Object System.Windows.Forms.Label
    $resultsLabel.Location = New-Object System.Drawing.Point(20, 230)
    $resultsLabel.Size = New-Object System.Drawing.Size(550, 20)
    $resultsLabel.Text = "Found Players:"
    $resultsLabel.ForeColor = [System.Drawing.Color]::White
    $resultsLabel.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
    $resultsLabel.Visible = $false
    $importForm.Controls.Add($resultsLabel)
    
    $resultsBox = New-Object System.Windows.Forms.TextBox
    $resultsBox.Location = New-Object System.Drawing.Point(20, 255)
    $resultsBox.Size = New-Object System.Drawing.Size(550, 100)
    $resultsBox.Multiline = $true
    $resultsBox.ScrollBars = "Vertical"
    $resultsBox.ReadOnly = $true
    $resultsBox.Font = New-Object System.Drawing.Font("Consolas", 9)
    $resultsBox.Visible = $false
    $importForm.Controls.Add($resultsBox)
    
    # Username input
    $usernameLabel = New-Object System.Windows.Forms.Label
    $usernameLabel.Location = New-Object System.Drawing.Point(20, 365)
    $usernameLabel.Size = New-Object System.Drawing.Size(550, 20)
    $usernameLabel.Text = "Usernames (auto-detected, edit if needed):"
    $usernameLabel.ForeColor = [System.Drawing.Color]::White
    $usernameLabel.Font = New-Object System.Drawing.Font("Segoe UI", 9)
    $usernameLabel.Visible = $false
    $importForm.Controls.Add($usernameLabel)
    
    $usernameBox = New-Object System.Windows.Forms.TextBox
    $usernameBox.Location = New-Object System.Drawing.Point(20, 380)
    $usernameBox.Size = New-Object System.Drawing.Size(550, 25)
    $usernameBox.Font = New-Object System.Drawing.Font("Segoe UI", 10)
    $usernameBox.Visible = $false
    $importForm.Controls.Add($usernameBox)
    
    # Convert button
    $convertButton = New-Object System.Windows.Forms.Button
    $convertButton.Location = New-Object System.Drawing.Point(200, 415)
    $convertButton.Size = New-Object System.Drawing.Size(200, 40)
    $convertButton.Text = "Convert & Import"
    $convertButton.BackColor = [System.Drawing.Color]::FromArgb(50, 150, 50)
    $convertButton.ForeColor = [System.Drawing.Color]::White
    $convertButton.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $convertButton.Font = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
    $convertButton.Visible = $false
    $importForm.Controls.Add($convertButton)
    
    # Store found player files
    $script:foundPlayers = @()
    
    # Scan button click
    $scanButton.Add_Click({
        $worldPath = $worldPathBox.Text
        
        if (-not $worldPath -or -not (Test-Path $worldPath)) {
            [System.Windows.Forms.MessageBox]::Show("Please select a valid world folder!", "Error", "OK", "Error")
            return
        }
        
        $playerdataPath = Join-Path $worldPath "playerdata"
        
        if (-not (Test-Path $playerdataPath)) {
            [System.Windows.Forms.MessageBox]::Show("No playerdata folder found!`n`nMake sure you selected the world folder (contains level.dat)", "Error", "OK", "Error")
            return
        }
        
        # Scan for .dat files
        $datFiles = Get-ChildItem -Path $playerdataPath -Filter "*.dat" | Where-Object { $_.Name -ne "player.dat" }
        
        if ($datFiles.Count -eq 0) {
            [System.Windows.Forms.MessageBox]::Show("No player data files found in world!", "Info", "OK", "Information")
            return
        }
        
        # Try to find usercache.json to map UUIDs to usernames
        $uuidToUsername = @{}
        
        # Look for usercache.json in multiple locations
        $cacheLocations = @(
            (Join-Path $worldPath "usercache.json"),                    # In world folder (LAN)
            (Join-Path (Split-Path $worldPath -Parent) "usercache.json"), # In saves folder
            (Join-Path (Split-Path (Split-Path $worldPath -Parent) -Parent) "usercache.json") # In .minecraft folder
        )
        
        foreach ($cachePath in $cacheLocations) {
            if (Test-Path $cachePath) {
                try {
                    $cacheContent = Get-Content -Path $cachePath -Raw | ConvertFrom-Json
                    
                    foreach ($entry in $cacheContent) {
                        # Store mapping (remove hyphens from UUID for matching)
                        $cleanUUID = $entry.uuid -replace '-', ''
                        $uuidToUsername[$cleanUUID] = $entry.name
                    }
                    
                    Write-Host "Found usercache.json with $($uuidToUsername.Count) entries"
                    break
                } catch {
                    Write-Host "Failed to parse usercache.json: $_"
                }
            }
        }
        
        # Store found players with username info
        $script:foundPlayers = @()
        $script:playerMappings = @()
        
        # Display results with usernames if available
        $resultsText = ""
        $autoUsernames = @()
        
        foreach ($file in $datFiles) {
            $script:foundPlayers += $file
            
            # Extract UUID from filename (remove .dat extension)
            $fileUUID = $file.BaseName -replace '-', ''
            
            # Check if we have a username for this UUID
            if ($uuidToUsername.ContainsKey($fileUUID)) {
                $username = $uuidToUsername[$fileUUID]
                $resultsText += "✓ $username → $($file.Name)`n"
                $autoUsernames += $username
                $script:playerMappings += @{ UUID = $file.BaseName; Username = $username }
            } else {
                $resultsText += "? Unknown → $($file.Name)`n"
                $script:playerMappings += @{ UUID = $file.BaseName; Username = $null }
            }
        }
        
        $resultsText += "`nTotal: $($datFiles.Count) player(s)"
        
        if ($uuidToUsername.Count -gt 0) {
            $resultsText += "`n`n✓ = Username detected from cache"
            $resultsText += "`n? = Username unknown (enter manually)"
            
            # Auto-fill detected usernames
            $usernameBox.Text = ($autoUsernames -join ', ')
        } else {
            $resultsText += "`n`n⚠️ No usercache.json found"
            $resultsText += "`nPlease enter usernames manually"
        }
        
        $resultsBox.Text = $resultsText
        $resultsLabel.Visible = $true
        $resultsBox.Visible = $true
        $usernameLabel.Visible = $true
        $usernameBox.Visible = $true
        $convertButton.Visible = $true
    })
    
    # Convert button click
    $convertButton.Add_Click({
        $worldPath = $worldPathBox.Text
        $usernames = $usernameBox.Text -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
        
        if ($usernames.Count -eq 0) {
            [System.Windows.Forms.MessageBox]::Show("Please enter at least one username!", "Error", "OK", "Error")
            return
        }
        
        if ($usernames.Count -ne $script:foundPlayers.Count) {
            $result = [System.Windows.Forms.MessageBox]::Show(
                "Number of usernames ($($usernames.Count)) doesn't match found players ($($script:foundPlayers.Count)).`n`nContinue anyway?`n`n- If MORE usernames: extras ignored`n- If FEWER usernames: remaining players won't be converted",
                "Mismatch Warning",
                "YesNo",
                "Warning"
            )
            
            if ($result -ne "Yes") {
                return
            }
        }
        
        try {
            # Get selected server version and type
            $version = $serverVersionDropdown.SelectedItem
            $loaderType = "vanilla"
            if ($serverFabricRadio.IsChecked) { $loaderType = "fabric" }
            elseif ($serverForgeRadio.IsChecked) { $loaderType = "forge" }
            elseif ($serverPaperRadio.IsChecked) { $loaderType = "paper" }
            elseif ($serverPurpurRadio.IsChecked) { $loaderType = "purpur" }
            
            $serverDir = Join-Path $scriptDir "servers\$loaderType-$version"
            $newWorldPath = Join-Path $serverDir "world"
            
            # Check if world already exists
            if (Test-Path $newWorldPath) {
                $result = [System.Windows.Forms.MessageBox]::Show(
                    "A world already exists in this server!`n`nOverwrite?",
                    "Overwrite Warning",
                    "YesNo",
                    "Warning"
                )
                
                if ($result -ne "Yes") {
                    return
                }
                
                Remove-Item -Path $newWorldPath -Recurse -Force
            }
            
            # Create server directory if needed
            if (-not (Test-Path $serverDir)) {
                New-Item -ItemType Directory -Path $serverDir -Force | Out-Null
            }
            
            # Copy world
            Write-Host "Copying world to server directory..."
            Copy-Item -Path $worldPath -Destination $newWorldPath -Recurse -Force
            
            # Convert player UUIDs
            $playerdataPath = Join-Path $newWorldPath "playerdata"
            $converted = 0
            
            for ($i = 0; $i -lt [Math]::Min($usernames.Count, $script:foundPlayers.Count); $i++) {
                $username = $usernames[$i]
                $oldFile = $script:foundPlayers[$i]
                
                # Calculate new UUID
                $newUUID = Get-OfflineUUID -Username $username
                
                # Rename file
                $oldPath = Join-Path $playerdataPath $oldFile.Name
                $newPath = Join-Path $playerdataPath "$newUUID.dat"
                
                if (Test-Path $oldPath) {
                    Move-Item -Path $oldPath -Destination $newPath -Force
                    Write-Host "Converted: $username -> $newUUID"
                    $converted++
                }
            }
            
            $importForm.Close()
            
            [System.Windows.Forms.MessageBox]::Show(
                "World imported successfully!`n`n- Converted $converted player(s)`n- Location: $newWorldPath`n`nYou can now start your server!",
                "Success",
                "OK",
                "Information"
            )
            
        } catch {
            [System.Windows.Forms.MessageBox]::Show("Failed to import world:`n`n$($_.Exception.Message)", "Error", "OK", "Error")
        }
    })
    
    [void]$importForm.ShowDialog()
}

#region Skin Manager
# Central skins folder — shared across all versions and servers
function Get-SkinsDir {
    $dir = Join-Path $scriptDir "skins"
    if (-not (Test-Path $dir)) { New-Item -Path $dir -ItemType Directory -Force | Out-Null }
    return $dir
}

# Persistent metadata file — maps username → model (steve/alex)
function Get-SkinsMetadata {
    $metaFile = Join-Path (Get-SkinsDir) "metadata.json"
    if (Test-Path $metaFile) {
        try { return (Get-Content $metaFile -Raw | ConvertFrom-Json) } catch {}
    }
    return [PSCustomObject]@{}
}

function Save-SkinsMetadata {
    param($Metadata)
    $metaFile = Join-Path (Get-SkinsDir) "metadata.json"
    $Metadata | ConvertTo-Json | Out-File -FilePath $metaFile -Encoding UTF8 -Force
}

function Get-SkinModel {
    param([string]$Username)
    $meta = Get-SkinsMetadata
    $prop = $meta.PSObject.Properties[$Username]
    if ($prop) { return $prop.Value }
    return "steve"
}

function Set-SkinModel {
    param([string]$Username, [string]$Model)
    $meta = Get-SkinsMetadata
    $meta | Add-Member -MemberType NoteProperty -Name $Username -Value $Model -Force
    Save-SkinsMetadata -Metadata $meta
}

function Get-AllSkins {
    $dir  = Get-SkinsDir
    $meta = Get-SkinsMetadata
    $skins = @()
    Get-ChildItem -Path $dir -Filter "*.png" -File -ErrorAction SilentlyContinue | ForEach-Object {
        $username = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
        $model    = if ($meta.PSObject.Properties[$username]) { $meta.PSObject.Properties[$username].Value } else { "steve" }
        $skins += [PSCustomObject]@{
            Username = $username
            Model    = $model
            FileName = $_.Name
            FullPath = $_.FullName
        }
    }
    return $skins | Sort-Object Username
}

function Add-SkinFile {
    param([string]$SourcePath, [string]$Username, [string]$Model = "steve")
    $dir      = Get-SkinsDir
    $destPath = Join-Path $dir "$Username.png"
    Copy-Item -Path $SourcePath -Destination $destPath -Force
    Set-SkinModel -Username $Username -Model $Model
    return $destPath
}

function Remove-SkinFile {
    param([string]$Username)
    $dir  = Get-SkinsDir
    $file = Join-Path $dir "$Username.png"
    if (Test-Path $file) { Remove-Item $file -Force }
    $meta = Get-SkinsMetadata
    if ($meta.PSObject.Properties[$Username]) {
        $meta.PSObject.Properties.Remove($Username)
        Save-SkinsMetadata -Metadata $meta
    }
}
#endregion

#region HTTP Skin Server

$script:skinHttpPort      = 25567
$script:skinHttpRunspace  = $null
$script:skinHttpPs        = $null
$script:skinMonitorTimer  = $null

function Get-SkinServerIPs {
    [System.Net.Dns]::GetHostAddresses([System.Net.Dns]::GetHostName()) |
        Where-Object { $_.AddressFamily -eq 'InterNetwork' } |
        Select-Object -ExpandProperty IPAddressToString
}

function Write-SkinRestorer-Config {
    param([string]$ServerDir, [string]$LoaderType, [int]$Port)

    $configDir  = Join-Path $ServerDir "config\skinrestorer"
    $configFile = Join-Path $configDir "config.json"
    if (-not (Test-Path $configDir)) {
        New-Item -Path $configDir -ItemType Directory -Force | Out-Null
    }
    if (Test-Path $configFile) { Remove-Item $configFile -Force }

    $hostIP  = (Get-SkinServerIPs | Where-Object { $_ -ne '127.0.0.1' } | Select-Object -First 1)
    if (-not $hostIP) { $hostIP = "127.0.0.1" }

    $json = @"
{
  "language": "en_us",
  "storage": { "location": "world" },
  "join": {
    "refreshSkin": true,
    "skipRefreshProviders": [],
    "applyDelay": 0,
    "autoFetch": {
      "enabled": true,
      "overrideExisting": true,
      "providers": [ "launcher-skins" ]
    }
  },
  "request": { "proxy": "", "timeout": 10, "userAgent": "" },
  "providers": {
    "mojang":     { "enabled": false, "name": "mojang",     "cache": { "enabled": true, "duration": 60   } },
    "ely_by":     { "enabled": false, "name": "ely.by",     "cache": { "enabled": true, "duration": 60   } },
    "mineskin":   { "apiKey": "", "proxyUrlUpload": false, "enabled": false, "name": "web", "cache": { "enabled": true, "duration": 300 } },
    "collection": { "sources": [], "enabled": false, "name": "collection", "cache": { "enabled": true, "duration": 604800 } },
    "custom": [
      {
        "type": "yggdrasil",
        "enabled": true,
        "name": "launcher-skins",
        "baseUrl": "http://127.0.0.1:$Port",
        "servicesUrl": "http://127.0.0.1:$Port",
        "sessionUrl": "http://127.0.0.1:$Port",
        "useProviderSignature": true,
        "cache": { "enabled": false, "duration": 1 }
      }
    ]
  },
  "version": 3
}
"@
    [System.IO.File]::WriteAllText($configFile, $json, [System.Text.Encoding]::UTF8)

    # Log confirmation so we can verify the config was written
    "[$(Get-Date -Format 'HH:mm:ss')] SkinRestorer config written to: $configFile" |
        Out-File "$env:TEMP\skinserver_debug.txt" -Append -Encoding UTF8 -Force

    return $hostIP
}

function Start-SkinHttpServer {
    param([int]$Port)

    Stop-SkinHttpServer

    $skinsDir = Get-SkinsDir
    $hostIP   = (Get-SkinServerIPs | Where-Object { $_ -ne '127.0.0.1' } | Select-Object -First 1)
    if (-not $hostIP) { $hostIP = "127.0.0.1" }

    # Build offline UUID map and write as JSON for the standalone process
    $uuidMap = @{}
    Get-AllSkins | ForEach-Object {
        $u   = $_.Username
        $b   = [System.Text.Encoding]::UTF8.GetBytes("OfflinePlayer:$u")
        $md5 = [System.Security.Cryptography.MD5]::Create()
        $h   = $md5.ComputeHash($b)
        $h[6] = ($h[6] -band 0x0f) -bor 0x30
        $h[8] = ($h[8] -band 0x3f) -bor 0x80
        $uuid = "$([BitConverter]::ToString($h[0..3]).Replace('-',''))-$([BitConverter]::ToString($h[4..5]).Replace('-',''))-$([BitConverter]::ToString($h[6..7]).Replace('-',''))-$([BitConverter]::ToString($h[8..9]).Replace('-',''))-$([BitConverter]::ToString($h[10..15]).Replace('-',''))".ToLower()
        $uuidMap[$uuid]        = @{ username = $u; model = $_.Model }
        $uuidMap[$u.ToLower()] = $uuid
    }

    $mapFile    = Join-Path $env:TEMP "mc_skin_map.json"
    $scriptPath = Join-Path $env:TEMP "mc_skin_server.ps1"
    $uuidMap | ConvertTo-Json -Depth 3 | Out-File -FilePath $mapFile -Encoding UTF8 -Force

    # Log how many skins are in the map — if 0, no skins will ever match Yggdrasil lookups
    "[$(Get-Date -Format 'HH:mm:ss')] Skin map: $($uuidMap.Count / 2) skins loaded (map has $($uuidMap.Count) entries)" |
        Add-Content "$env:TEMP\skinserver_debug.txt"

    $serverScript = @'
param([string]$MapFile,[string]$SkinsDir,[string]$HostIP,[int]$Port,[string]$KeyFile)
$dbg = "$env:TEMP\skinserver_debug.txt"
"[$((Get-Date).ToString('HH:mm:ss'))] Standalone skin server starting Port=$Port McPort=$McPort" | Out-File $dbg -Force

$uuidMap = @{}
try {
    $json = Get-Content $MapFile -Raw | ConvertFrom-Json
    $json.PSObject.Properties | ForEach-Object {
        $v = $_.Value
        $uuidMap[$_.Name.ToLower()] = if ($v -is [string]) { $v } else { @{ username = $v.username; model = $v.model } }
    }
    "[$((Get-Date).ToString('HH:mm:ss'))] Loaded $($uuidMap.Count) UUID entries" | Add-Content $dbg
} catch { "[$((Get-Date).ToString('HH:mm:ss'))] Map error: $_" | Add-Content $dbg }

# RSA for signing texture values — PERSISTED so the public key stays stable
# across restarts (authlib-injector clients verify signatures against it)
$rsa = [System.Security.Cryptography.RSA]::Create(2048)
if ($KeyFile -and (Test-Path $KeyFile)) {
    try {
        $rsa.FromXmlString((Get-Content $KeyFile -Raw))
        "[$((Get-Date).ToString('HH:mm:ss'))] Loaded RSA key" | Add-Content $dbg
    } catch {
        "[$((Get-Date).ToString('HH:mm:ss'))] Key load failed, generating new: $_" | Add-Content $dbg
        try { $rsa.ToXmlString($true) | Out-File $KeyFile -Encoding UTF8 -Force } catch {}
    }
} elseif ($KeyFile) {
    try {
        $rsa.ToXmlString($true) | Out-File $KeyFile -Encoding UTF8 -Force
        "[$((Get-Date).ToString('HH:mm:ss'))] Generated new RSA key" | Add-Content $dbg
    } catch {}
}

# ── Build SubjectPublicKeyInfo PEM (needed for authlib-injector metadata) ──
function EncLen([int]$len) {
    if ($len -lt 0x80) { return ,([byte]$len) }
    $bl = @(); $l = $len
    while ($l -gt 0) { $bl = ,([byte]($l -band 0xFF)) + $bl; $l = $l -shr 8 }
    return ,([byte](0x80 -bor $bl.Count)) + $bl
}
function EncTlv([byte]$tag, [byte[]]$content) {
    return ,([byte]$tag) + (EncLen $content.Count) + $content
}
function EncInt([byte[]]$b) {
    if ($b[0] -band 0x80) { $b = ,([byte]0) + $b }
    return EncTlv 0x02 $b
}

$rp     = $rsa.ExportParameters($false)
$rsaSeq = EncTlv 0x30 ((EncInt $rp.Modulus) + (EncInt $rp.Exponent))
$bitStr = EncTlv 0x03 (,([byte]0) + $rsaSeq)
$algOid = [byte[]](0x30,0x0D,0x06,0x09,0x2A,0x86,0x48,0x86,0xF7,0x0D,0x01,0x01,0x01,0x05,0x00)
$spki   = EncTlv 0x30 ($algOid + $bitStr)
$b64pub = [Convert]::ToBase64String($spki)
$pubPem = "-----BEGIN PUBLIC KEY-----\n"
for ($i = 0; $i -lt $b64pub.Length; $i += 64) {
    $pubPem += $b64pub.Substring($i, [Math]::Min(64, $b64pub.Length - $i)) + "\n"
}
$pubPem += "-----END PUBLIC KEY-----\n"

# authlib-injector metadata served at GET /
$aliMeta = "{`"meta`":{`"serverName`":`"Launcher Skins`",`"implementationName`":`"launcher-skin-server`",`"implementationVersion`":`"1.0`"},`"skinDomains`":[`"$HostIP`",`"127.0.0.1`",`"localhost`"],`"signaturePublickey`":`"$pubPem`"}"

function MakeTexJson($Uuid,$Username,$Model) {
    $url  = "http://$HostIP`:$Port/skins/$Username.png"
    $slim = if ($Model -eq "alex") { ",`"metadata`":{`"model`":`"slim`"}" } else { "" }
    return "{`"timestamp`":$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()),`"profileId`":`"$($Uuid.Replace('-',''))`",`"profileName`":`"$Username`",`"signatureRequired`":true,`"textures`":{`"SKIN`":{`"url`":`"$url`"$slim}}}"
}
function SendHttp($Stream,$Status,$CT,$Body) {
    $st  = if ($Status -eq 200) { "OK" } else { "Not Found" }
    $hdr = "HTTP/1.1 $Status $st`r`nContent-Type: $CT`r`nContent-Length: $($Body.Length)`r`nConnection: close`r`n`r`n"
    $hb  = [System.Text.Encoding]::ASCII.GetBytes($hdr)
    $Stream.Write($hb,0,$hb.Length)
    if ($Body.Length -gt 0) { $Stream.Write($Body,0,$Body.Length) }
    $Stream.Flush()
}
function ToBytes($s) { [System.Text.Encoding]::UTF8.GetBytes($s) }

$tcp = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Any, $Port)
try { $tcp.Start() } catch {
    "[$((Get-Date).ToString('HH:mm:ss'))] Port bind failed: $_" | Add-Content $dbg; exit 1
}
"[$((Get-Date).ToString('HH:mm:ss'))] Listening on $Port" | Add-Content $dbg

# UDP discovery socket — client launchers broadcast to find us (zero-config setup)
$udpPort = $Port + 1
$udp = New-Object System.Net.Sockets.Socket(
    [System.Net.Sockets.AddressFamily]::InterNetwork,
    [System.Net.Sockets.SocketType]::Dgram,
    [System.Net.Sockets.ProtocolType]::Udp)
try {
    $udp.Bind((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, $udpPort)))
    "[$((Get-Date).ToString('HH:mm:ss'))] Discovery listening on UDP $udpPort" | Add-Content $dbg
} catch {
    "[$((Get-Date).ToString('HH:mm:ss'))] UDP bind failed (discovery disabled): $_" | Add-Content $dbg
    $udp = $null
}

# Serving loop on MAIN thread — PowerShell can't run scriptblocks on raw .NET threads
"[$((Get-Date).ToString('HH:mm:ss'))] Serving on main thread" | Add-Content $dbg

while ($true) {
    try {
        $chk = New-Object System.Collections.ArrayList
        [void]$chk.Add($tcp.Server)
        if ($udp) { [void]$chk.Add($udp) }
        [System.Net.Sockets.Socket]::Select($chk, $null, $null, 500000)
        if ($chk.Count -eq 0) { continue }

        # ── UDP discovery request ─────────────────────────────
        if ($udp -and $chk.Contains($udp)) {
            try {
                $dbuf   = New-Object byte[] 256
                $sender = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
                $epRef  = [System.Net.EndPoint]$sender
                $dlen   = $udp.ReceiveFrom($dbuf, [ref]$epRef)
                $msg    = [System.Text.Encoding]::ASCII.GetString($dbuf, 0, $dlen)
                if ($msg -eq "MCSKINSERVER_DISCOVER") {
                    $reply = [System.Text.Encoding]::ASCII.GetBytes("MCSKINSERVER:$Port")
                    [void]$udp.SendTo($reply, $epRef)
                    "[$((Get-Date).ToString('HH:mm:ss'))] Discovery reply sent to $epRef" | Add-Content $dbg
                }
            } catch {}
        }

        # ── TCP HTTP request ──────────────────────────────────
        if (-not $chk.Contains($tcp.Server)) { continue }
        $client = $tcp.AcceptTcpClient()
        "[$((Get-Date).ToString('HH:mm:ss'))] Connection" | Add-Content $dbg
        try {
            $stream = $client.GetStream()
            $ms  = New-Object System.IO.MemoryStream
            $buf = New-Object byte[] 8192

            # Read until the header terminator (\r\n\r\n) is found — keeps body as raw bytes
            $headerEnd = -1
            while ($headerEnd -lt 0 -and $ms.Length -lt 65536) {
                $read = $stream.Read($buf, 0, $buf.Length)
                if ($read -le 0) { break }
                $ms.Write($buf, 0, $read)
                $data = $ms.ToArray()
                for ($i = 3; $i -lt $data.Length; $i++) {
                    if ($data[$i-3] -eq 13 -and $data[$i-2] -eq 10 -and
                        $data[$i-1] -eq 13 -and $data[$i]   -eq 10) { $headerEnd = $i + 1; break }
                }
            }

            if ($headerEnd -gt 0) {
                $data       = $ms.ToArray()
                $headerText = [System.Text.Encoding]::ASCII.GetString($data, 0, $headerEnd)
                $first      = ($headerText -split "`r`n")[0]
                "[$((Get-Date).ToString('HH:mm:ss'))] $first" | Add-Content "$env:TEMP\skinserver_requests.txt"

                # For POST bodies: honour Content-Length, keep reading raw bytes
                $clen = 0
                if ($headerText -match 'Content-Length:\s*(\d+)') { $clen = [int]$matches[1] }
                if ($clen -gt 2097152) { $clen = 0 }   # 2MB cap
                while (($ms.Length - $headerEnd) -lt $clen) {
                    $read = $stream.Read($buf, 0, $buf.Length)
                    if ($read -le 0) { break }
                    $ms.Write($buf, 0, $read)
                }
                $data = $ms.ToArray()
                $bodyBytes = [byte[]]@()
                if ($clen -gt 0 -and ($data.Length - $headerEnd) -ge $clen) {
                    $bodyBytes = New-Object byte[] $clen
                    [System.Array]::Copy($data, $headerEnd, $bodyBytes, 0, $clen)
                }
                $req = $headerText   # routes below only look at headers/first line

                if ($first -match 'GET / HTTP') {
                    # authlib-injector metadata (root endpoint)
                    SendHttp $stream 200 "application/json" (ToBytes $aliMeta)
                } elseif ($first -match 'POST /upload/([^/?# ]+)') {
                    # Skin upload from a client launcher: body is the raw PNG
                    $un    = $matches[1]
                    $model = if ($first -match 'model=alex') { "alex" } else { "steve" }
                    if ($bodyBytes.Length -gt 0) {
                        $dest = Join-Path $SkinsDir "$un.png"
                        [System.IO.File]::WriteAllBytes($dest, $bodyBytes)

                        # Live-update the in-memory UUID map so it works without restart
                        $ub  = [System.Text.Encoding]::UTF8.GetBytes("OfflinePlayer:$un")
                        $md5 = [System.Security.Cryptography.MD5]::Create()
                        $h   = $md5.ComputeHash($ub)
                        $h[6] = ($h[6] -band 0x0f) -bor 0x30
                        $h[8] = ($h[8] -band 0x3f) -bor 0x80
                        $uuid = "$([BitConverter]::ToString($h[0..3]).Replace('-',''))-$([BitConverter]::ToString($h[4..5]).Replace('-',''))-$([BitConverter]::ToString($h[6..7]).Replace('-',''))-$([BitConverter]::ToString($h[8..9]).Replace('-',''))-$([BitConverter]::ToString($h[10..15]).Replace('-',''))".ToLower()
                        $uuidMap[$uuid]        = @{ username = $un; model = $model }
                        $uuidMap[$un.ToLower()] = $uuid

                        # Persist model choice into the host's metadata.json
                        try {
                            $metaFile = Join-Path $SkinsDir "metadata.json"
                            $meta = if (Test-Path $metaFile) { Get-Content $metaFile -Raw | ConvertFrom-Json } else { New-Object PSObject }
                            $meta | Add-Member -MemberType NoteProperty -Name $un -Value $model -Force
                            $meta | ConvertTo-Json | Out-File $metaFile -Encoding UTF8 -Force
                        } catch {}

                        "[$((Get-Date).ToString('HH:mm:ss'))] Skin uploaded: $un ($model, $($bodyBytes.Length) bytes)" | Add-Content $dbg
                        SendHttp $stream 200 "application/json" (ToBytes '{"ok":true}')
                    } else {
                        SendHttp $stream 400 "application/json" (ToBytes '{"error":"empty body"}')
                    }
                } elseif ($first -match 'GET (?:/minecraftservices)?/minecraft/profile/lookup/name/([^/?# ]+)') {
                    # Modern Minecraft Services API — what SkinRestorer actually calls
                    $un = $matches[1].ToLower()
                    if ($uuidMap.ContainsKey($un)) {
                        $id = $uuidMap[$un]; $e = $uuidMap[$id]
                        SendHttp $stream 200 "application/json" (ToBytes "{`"id`":`"$($id.Replace('-',''))`",`"name`":`"$($e.username)`"}")
                    } else { SendHttp $stream 404 "application/json" (ToBytes '{"path":"/minecraft/profile/lookup/name","error":"NOT_FOUND"}') }
                } elseif ($first -match 'GET (?:/api)?/users/profiles/minecraft/([^/?# ]+)') {
                    $un = $matches[1].ToLower()
                    if ($uuidMap.ContainsKey($un)) {
                        $id = $uuidMap[$un]; $e = $uuidMap[$id]
                        SendHttp $stream 200 "application/json" (ToBytes "{`"id`":`"$($id.Replace('-',''))`",`"name`":`"$($e.username)`"}")
                    } else { SendHttp $stream 404 "application/json" (ToBytes '{"error":"Not Found"}') }
                } elseif ($first -match 'POST .*profiles/minecraft') {
                    # Bulk username→UUID lookup: request body is a JSON array of usernames
                    # Response is a JSON array of {id, name} objects
                    $bodyStart = $req.IndexOf("`r`n`r`n")
                    $body = if ($bodyStart -ge 0) { $req.Substring($bodyStart + 4) } else { "" }
                    "[$((Get-Date).ToString('HH:mm:ss'))] POST body: $body" | Add-Content $dbg
                    $names = @()
                    try { $names = $body | ConvertFrom-Json } catch {}
                    $results = @()
                    foreach ($n in $names) {
                        $key = "$n".ToLower()
                        if ($uuidMap.ContainsKey($key)) {
                            $id = $uuidMap[$key]; $e = $uuidMap[$id]
                            $results += "{`"id`":`"$($id.Replace('-',''))`",`"name`":`"$($e.username)`"}"
                        }
                    }
                    SendHttp $stream 200 "application/json" (ToBytes "[$($results -join ',')]")
                } elseif ($first -match 'GET (?:/sessionserver)?/session/minecraft/profile/([^/?# ]+)') {
                    $raw = ($matches[1] -replace '\?.*','').Replace("-","").ToLower()
                    $id  = if ($raw.Length -eq 32) {"$($raw.Substring(0,8))-$($raw.Substring(8,4))-$($raw.Substring(12,4))-$($raw.Substring(16,4))-$($raw.Substring(20))"} else {$raw}
                    if ($uuidMap.ContainsKey($id)) {
                        $e   = $uuidMap[$id]
                        $tx  = MakeTexJson $id $e.username $e.model
                        $val = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($tx))
                        $sig = [System.Convert]::ToBase64String($rsa.SignData([System.Text.Encoding]::UTF8.GetBytes($val),[System.Security.Cryptography.HashAlgorithmName]::SHA1,[System.Security.Cryptography.RSASignaturePadding]::Pkcs1))
                        SendHttp $stream 200 "application/json" (ToBytes "{`"id`":`"$($id.Replace('-',''))`",`"name`":`"$($e.username)`",`"properties`":[{`"name`":`"textures`",`"value`":`"$val`",`"signature`":`"$sig`"}]}")
                    } else { SendHttp $stream 404 "application/json" (ToBytes '{"error":"Not Found"}') }
                } elseif ($first -match 'GET /skins/([^/?# ]+)\.png') {
                    $f = Join-Path $SkinsDir "$($matches[1]).png"
                    if (Test-Path $f) { SendHttp $stream 200 "image/png" ([System.IO.File]::ReadAllBytes($f)) }
                    else { SendHttp $stream 404 "text/plain" ([byte[]]@()) }
                } else {
                    "[$((Get-Date).ToString('HH:mm:ss'))] UNMATCHED: $first" | Add-Content $dbg
                    SendHttp $stream 400 "text/plain" ([byte[]]@())
                }
            }
        } catch { "[$((Get-Date).ToString('HH:mm:ss'))] Err: $_" | Add-Content $dbg }
        finally { try { $client.Close() } catch {} }
    } catch {
        "[$((Get-Date).ToString('HH:mm:ss'))] Accept err: $_" | Add-Content $dbg
        Start-Sleep -Milliseconds 500
    }
}
'@

    $serverScript | Out-File -FilePath $scriptPath -Encoding UTF8 -Force

    # Use full path to powershell.exe
    $pwshPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    if (-not (Test-Path $pwshPath)) { $pwshPath = "powershell.exe" }

    # Launch via WMI (Win32_Process.Create) — WMI-spawned processes are created
    # by the WMI service OUTSIDE the launcher's job object, so they survive the
    # launcher closing. Process.Start children inherit ps2exe's job and get
    # killed when the launcher exits (that was the bug).
    $keyFile = Join-Path $skinsDir "skinserver_rsa.xml"
    $cmdLine = "`"$pwshPath`" -WindowStyle Hidden -NonInteractive -ExecutionPolicy Bypass -File `"$scriptPath`" -MapFile `"$mapFile`" -SkinsDir `"$skinsDir`" -HostIP $hostIP -Port $Port -KeyFile `"$keyFile`""
    try {
        # Win32_ProcessStartup with ShowWindow=0 (SW_HIDE) hides the console window
        # at creation time — the -WindowStyle Hidden arg alone can't prevent the
        # window because Windows creates it before PowerShell parses arguments
        $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }
        $procInfo = Invoke-CimMethod -ClassName Win32_Process -MethodName Create `
                        -Arguments @{ CommandLine = $cmdLine; ProcessStartupInformation = $startup } -ErrorAction Stop
        if ($procInfo.ReturnValue -eq 0) {
            $script:skinServerPid = $procInfo.ProcessId
            "[$(Get-Date -Format 'HH:mm:ss')] Skin server started via WMI (PID $($procInfo.ProcessId))" |
                Add-Content "$env:TEMP\skinserver_debug.txt"
        } else {
            "[$(Get-Date -Format 'HH:mm:ss')] WMI Create failed with code $($procInfo.ReturnValue)" |
                Add-Content "$env:TEMP\skinserver_debug.txt"
        }
    } catch {
        "[$(Get-Date -Format 'HH:mm:ss')] WMI launch FAILED: $_" |
            Add-Content "$env:TEMP\skinserver_debug.txt"
    }
}

function Stop-SkinHttpServer {
    if ($script:skinServerPid) {
        try { Stop-Process -Id $script:skinServerPid -Force -ErrorAction SilentlyContinue } catch {}
    }
    $script:skinServerPid = $null
    # Also kill any orphaned skin server from a previous launcher session
    try {
        Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" |
            Where-Object { $_.CommandLine -like "*mc_skin_server.ps1*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    } catch {}
}


#region GUI
$config = Get-Config
$currentUUID = Get-ComputerUUID

#region XAML Window Definition
[xml]$xamlDef = @'
<Window
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="Minecraft Portable Launcher"
    Width="1344" Height="756"
    MinWidth="1100" MinHeight="620"
    WindowStartupLocation="CenterScreen"
    Background="#141414">

    <Window.Resources>

        <!-- ── Nav button: inactive ── -->
        <Style x:Key="NavItem" TargetType="Button">
            <Setter Property="Background"               Value="Transparent"/>
            <Setter Property="Foreground"               Value="#888888"/>
            <Setter Property="BorderThickness"          Value="0"/>
            <Setter Property="Height"                   Value="44"/>
            <Setter Property="FontSize"                 Value="13"/>
            <Setter Property="FontWeight"               Value="Normal"/>
            <Setter Property="HorizontalContentAlignment" Value="Left"/>
            <Setter Property="Cursor"                   Value="Hand"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="bg"
                                Background="{TemplateBinding Background}"
                                BorderThickness="3,0,0,0"
                                BorderBrush="Transparent">
                            <ContentPresenter HorizontalAlignment="Left"
                                              VerticalAlignment="Center"
                                              Margin="14,0,0,0"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="bg" Property="Background" Value="#1e1e1e"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- ── Nav button: active ── -->
        <Style x:Key="NavItemActive" TargetType="Button">
            <Setter Property="Background"               Value="#252525"/>
            <Setter Property="Foreground"               Value="White"/>
            <Setter Property="BorderThickness"          Value="0"/>
            <Setter Property="Height"                   Value="44"/>
            <Setter Property="FontSize"                 Value="13"/>
            <Setter Property="FontWeight"               Value="SemiBold"/>
            <Setter Property="HorizontalContentAlignment" Value="Left"/>
            <Setter Property="Cursor"                   Value="Hand"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="bg"
                                Background="{TemplateBinding Background}"
                                BorderThickness="3,0,0,0"
                                BorderBrush="#3fd982">
                            <ContentPresenter HorizontalAlignment="Left"
                                              VerticalAlignment="Center"
                                              Margin="14,0,0,0"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="bg" Property="Background" Value="#2d2d2d"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- ── Card surface ── -->
        <Style x:Key="Card" TargetType="Border">
            <Setter Property="Background"      Value="#202020"/>
            <Setter Property="BorderBrush"     Value="#2d2d2d"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="CornerRadius"    Value="8"/>
            <Setter Property="Padding"         Value="16"/>
        </Style>

        <!-- ── Section header (ACCOUNT / DEVICES etc.) ── -->
        <Style x:Key="SectionLabel" TargetType="TextBlock">
            <Setter Property="Foreground"  Value="#555555"/>
            <Setter Property="FontSize"    Value="10"/>
            <Setter Property="FontWeight"  Value="SemiBold"/>
            <Setter Property="Margin"      Value="0,0,0,8"/>
        </Style>

        <!-- ── Field sub-label inside cards ── -->
        <Style x:Key="FieldLabel" TargetType="TextBlock">
            <Setter Property="Foreground" Value="#888888"/>
            <Setter Property="FontSize"   Value="11"/>
            <Setter Property="Margin"     Value="0,0,0,6"/>
        </Style>

        <!-- ── Primary action button (coloured) ── -->
        <Style x:Key="PrimaryButton" TargetType="Button">
            <Setter Property="Foreground"      Value="White"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="FontSize"        Value="12"/>
            <Setter Property="FontWeight"      Value="SemiBold"/>
            <Setter Property="Height"          Value="38"/>
            <Setter Property="Cursor"          Value="Hand"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="bg"
                                Background="{TemplateBinding Background}"
                                CornerRadius="8">
                            <ContentPresenter HorizontalAlignment="Center"
                                              VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="bg" Property="Opacity" Value="0.82"/>
                            </Trigger>
                            <Trigger Property="IsPressed" Value="True">
                                <Setter TargetName="bg" Property="Opacity" Value="0.65"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- ── Secondary / muted button ── -->
        <Style x:Key="SecondaryButton" TargetType="Button">
            <Setter Property="Background"      Value="#2a2a2a"/>
            <Setter Property="Foreground"      Value="#cccccc"/>
            <Setter Property="BorderBrush"     Value="#3a3a3a"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="FontSize"        Value="12"/>
            <Setter Property="Height"          Value="38"/>
            <Setter Property="Cursor"          Value="Hand"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="bg"
                                Background="{TemplateBinding Background}"
                                CornerRadius="8"
                                BorderBrush="{TemplateBinding BorderBrush}"
                                BorderThickness="{TemplateBinding BorderThickness}">
                            <ContentPresenter HorizontalAlignment="Center"
                                              VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="bg" Property="Background" Value="#333333"/>
                            </Trigger>
                            <Trigger Property="IsPressed" Value="True">
                                <Setter TargetName="bg" Property="Background" Value="#1e1e1e"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- ── Global TextBox ── -->
        <Style TargetType="TextBox">
            <Setter Property="Background"             Value="#2a2a2a"/>
            <Setter Property="Foreground"             Value="White"/>
            <Setter Property="BorderBrush"            Value="#3a3a3a"/>
            <Setter Property="BorderThickness"        Value="1"/>
            <Setter Property="Height"                 Value="32"/>
            <Setter Property="FontSize"               Value="12"/>
            <Setter Property="CaretBrush"             Value="White"/>
            <Setter Property="Padding"                Value="8,0"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
        </Style>

        <!-- ── Global ComboBox ── -->
        <Style TargetType="ComboBox">
            <Setter Property="Background"      Value="#2a2a2a"/>
            <Setter Property="Foreground"      Value="#111111"/>
            <Setter Property="BorderBrush"     Value="#3a3a3a"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Height"          Value="32"/>
            <Setter Property="FontSize"        Value="12"/>
        </Style>

        <!-- ── ComboBox dropdown items — black text on the light popup background ── -->
        <Style TargetType="ComboBoxItem">
            <Setter Property="Foreground"  Value="#111111"/>
            <Setter Property="Background"  Value="White"/>
            <Setter Property="FontSize"    Value="12"/>
            <Setter Property="Padding"     Value="8,4"/>
        </Style>

        <!-- ── Global RadioButton ── -->
        <Style TargetType="RadioButton">
            <Setter Property="Foreground"       Value="#cccccc"/>
            <Setter Property="FontSize"         Value="12"/>
            <Setter Property="Margin"           Value="0,0,16,0"/>
            <Setter Property="VerticalAlignment" Value="Center"/>
        </Style>

        <!-- ── Global CheckBox ── -->
        <Style TargetType="CheckBox">
            <Setter Property="Foreground" Value="#cccccc"/>
            <Setter Property="FontSize"   Value="12"/>
        </Style>

        <!-- ── ListViewItem — grays out disabled mods ── -->
        <Style TargetType="ListViewItem">
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="Padding"    Value="4,3"/>
            <Style.Triggers>
                <DataTrigger Binding="{Binding Enabled}" Value="False">
                    <Setter Property="Foreground" Value="#4a4a4a"/>
                </DataTrigger>
                <Trigger Property="IsMouseOver" Value="True">
                    <Setter Property="Background" Value="#2a2a2a"/>
                </Trigger>
                <Trigger Property="IsSelected" Value="True">
                    <Setter Property="Background" Value="#1a3a5a"/>
                    <Setter Property="BorderBrush" Value="#2a5a8a"/>
                </Trigger>
            </Style.Triggers>
        </Style>

    </Window.Resources>

    <!-- ════════════════════ ROOT GRID ════════════════════ -->
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="220"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>

        <!-- ═══════════════ SIDEBAR ═══════════════ -->
        <Border Grid.Column="0"
                Background="#1a1a1a"
                BorderBrush="#2a2a2a"
                BorderThickness="0,0,1,0">
            <DockPanel LastChildFill="True">

                <!-- App header -->
                <Border DockPanel.Dock="Top"
                        Padding="18,20,18,18"
                        BorderBrush="#2a2a2a"
                        BorderThickness="0,0,0,1">
                    <StackPanel>
                        <TextBlock Text="Launcher"
                                   Foreground="White"
                                   FontSize="18"
                                   FontWeight="Bold"/>
                        <TextBlock Text="Portable Minecraft"
                                   Foreground="#555555"
                                   FontSize="10"
                                   Margin="0,3,0,0"/>
                    </StackPanel>
                </Border>

                <!-- Computer ID pinned to bottom -->
                <Border DockPanel.Dock="Bottom"
                        Padding="16,12,16,16"
                        BorderBrush="#2a2a2a"
                        BorderThickness="0,1,0,0">
                    <StackPanel>
                        <TextBlock Text="COMPUTER ID"
                                   Foreground="#444444"
                                   FontSize="9"
                                   FontWeight="SemiBold"
                                   Margin="0,0,0,5"/>
                        <TextBlock x:Name="uuidLabel"
                                   Text=""
                                   Foreground="#666666"
                                   FontSize="9"
                                   TextWrapping="Wrap"/>
                        <Button x:Name="resetUUIDButton"
                                Content="Reset ID"
                                Style="{StaticResource SecondaryButton}"
                                Height="28"
                                FontSize="10"
                                Margin="0,8,0,0"/>
                    </StackPanel>
                </Border>

                <!-- Nav section -->
                <StackPanel DockPanel.Dock="Top">
                    <TextBlock Text="NAVIGATION"
                               Foreground="#444444"
                               FontSize="9"
                               FontWeight="SemiBold"
                               Margin="18,18,0,8"/>
                    <Button x:Name="navClient" Content="Client" Style="{StaticResource NavItemActive}"/>
                    <Button x:Name="navServer" Content="Server" Style="{StaticResource NavItem}"/>
                    <Button x:Name="navMods"   Content="Mods"   Style="{StaticResource NavItem}"/>
                    <Button x:Name="navSetup"  Content="Setup"  Style="{StaticResource NavItem}"/>
                    <Button x:Name="navSkins"  Content="Skins"  Style="{StaticResource NavItem}"/>
                </StackPanel>

                <!-- Remaining space filler -->
                <Grid/>
            </DockPanel>
        </Border>

        <!-- ═══════════════ CONTENT AREA ═══════════════ -->
        <Grid Grid.Column="1" Margin="28,22,28,22">

            <!-- ─────────── CLIENT PANEL ─────────── -->
            <ScrollViewer x:Name="clientPanel"
                          VerticalScrollBarVisibility="Auto"
                          HorizontalScrollBarVisibility="Disabled">
                <StackPanel Margin="0,160,0,0">

                    <TextBlock Text="CLIENT" Style="{StaticResource SectionLabel}"/>

                    <!-- Game Settings card -->
                    <Border Style="{StaticResource Card}" Margin="0,0,0,14">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="20"/>
                                <ColumnDefinition Width="*"/>
                            </Grid.ColumnDefinitions>

                            <StackPanel Grid.Column="0">
                                <TextBlock Text="VERSION" Style="{StaticResource FieldLabel}"/>
                                <ComboBox x:Name="versionDropdown"/>
                            </StackPanel>

                            <StackPanel Grid.Column="2">
                                <TextBlock Text="MOD LOADER" Style="{StaticResource FieldLabel}"/>
                                <StackPanel Orientation="Horizontal" Margin="0,6,0,0">
                                    <RadioButton x:Name="vanillaRadio" Content="Vanilla" GroupName="ClientLoaderGroup"/>
                                    <RadioButton x:Name="fabricRadio"  Content="Fabric"  GroupName="ClientLoaderGroup"/>
                                    <RadioButton x:Name="forgeRadio"   Content="Forge"   GroupName="ClientLoaderGroup"/>
                                </StackPanel>
                            </StackPanel>
                        </Grid>
                    </Border>

                    <!-- Player card -->
                    <TextBlock Text="PLAYER" Style="{StaticResource SectionLabel}"/>
                    <Border Style="{StaticResource Card}" Margin="0,0,0,14">
                        <StackPanel>
                            <TextBlock Text="USERNAME" Style="{StaticResource FieldLabel}"/>
                            <TextBox x:Name="usernameTextBox"/>
                        </StackPanel>
                    </Border>

                    <!-- Performance card -->
                    <TextBlock Text="PERFORMANCE" Style="{StaticResource SectionLabel}"/>
                    <Border Style="{StaticResource Card}" Margin="0,0,0,20">
                        <StackPanel>
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>
                                <TextBlock Grid.Column="0"
                                           Text="MEMORY ALLOCATION"
                                           Style="{StaticResource FieldLabel}"
                                           Margin="0"/>
                                <TextBlock x:Name="memoryValueLabel"
                                           Grid.Column="1"
                                           Text="2 GB"
                                           Foreground="#3fd982"
                                           FontSize="11"
                                           FontWeight="SemiBold"
                                           VerticalAlignment="Center"/>
                            </Grid>
                            <Slider x:Name="memorySlider"
                                    Minimum="1" Maximum="16" Value="2"
                                    TickFrequency="1"
                                    SmallChange="1"
                                    LargeChange="2"
                                    IsSnapToTickEnabled="True"
                                    Margin="0,10,0,2"/>
                        </StackPanel>
                    </Border>

                    <!-- Action buttons -->
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="12"/>
                            <ColumnDefinition Width="160"/>
                        </Grid.ColumnDefinitions>

                        <!-- PLAY -->
                        <Button x:Name="playButton"
                                Grid.Column="0"
                                Content="PLAY"
                                Height="52"
                                FontSize="18"
                                FontWeight="Bold"
                                Foreground="White"
                                BorderThickness="0"
                                Cursor="Hand">
                            <Button.Background>
                                <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                                    <GradientStop Color="#3fd982" Offset="0"/>
                                    <GradientStop Color="#1f9e63" Offset="1"/>
                                </LinearGradientBrush>
                            </Button.Background>
                            <Button.Template>
                                <ControlTemplate TargetType="Button">
                                    <Border x:Name="bg"
                                            Background="{TemplateBinding Background}"
                                            CornerRadius="8">
                                        <ContentPresenter HorizontalAlignment="Center"
                                                          VerticalAlignment="Center"/>
                                    </Border>
                                    <ControlTemplate.Triggers>
                                        <Trigger Property="IsMouseOver" Value="True">
                                            <Setter TargetName="bg" Property="Opacity" Value="0.82"/>
                                        </Trigger>
                                        <Trigger Property="IsPressed" Value="True">
                                            <Setter TargetName="bg" Property="Opacity" Value="0.65"/>
                                        </Trigger>
                                    </ControlTemplate.Triggers>
                                </ControlTemplate>
                            </Button.Template>
                        </Button>

                        <Button x:Name="skinButton"
                                Grid.Column="2"
                                Content="Change Skin"
                                Style="{StaticResource SecondaryButton}"
                                Height="52"/>
                    </Grid>

                </StackPanel>
            </ScrollViewer>

            <!-- ─────────── SERVER PANEL ─────────── -->
            <ScrollViewer x:Name="serverPanel"
                          Visibility="Collapsed"
                          VerticalScrollBarVisibility="Auto"
                          HorizontalScrollBarVisibility="Disabled">
                <StackPanel Margin="0,160,0,0">

                    <TextBlock Text="SERVER" Style="{StaticResource SectionLabel}"/>

                    <!-- Configuration card -->
                    <Border Style="{StaticResource Card}" Margin="0,0,0,14">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="20"/>
                                <ColumnDefinition Width="*"/>
                            </Grid.ColumnDefinitions>

                            <StackPanel Grid.Column="0">
                                <TextBlock Text="VERSION" Style="{StaticResource FieldLabel}"/>
                                <ComboBox x:Name="serverVersionDropdown"/>
                            </StackPanel>

                            <StackPanel Grid.Column="2">
                                <TextBlock Text="SERVER TYPE" Style="{StaticResource FieldLabel}"/>
                                <WrapPanel Margin="0,6,0,0">
                                    <RadioButton x:Name="serverVanillaRadio" Content="Vanilla" GroupName="ServerLoaderGroup"/>
                                    <RadioButton x:Name="serverFabricRadio"  Content="Fabric"  GroupName="ServerLoaderGroup"/>
                                    <RadioButton x:Name="serverForgeRadio"   Content="Forge"   GroupName="ServerLoaderGroup"/>
                                    <RadioButton x:Name="serverPaperRadio"   Content="Paper"   GroupName="ServerLoaderGroup"/>
                                    <RadioButton x:Name="serverPurpurRadio"  Content="Purpur"  GroupName="ServerLoaderGroup"/>
                                </WrapPanel>
                            </StackPanel>
                        </Grid>
                    </Border>

                    <!-- World settings card -->
                    <TextBlock Text="WORLD SETTINGS" Style="{StaticResource SectionLabel}"/>
                    <Border Style="{StaticResource Card}" Margin="0,0,0,14">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="14"/>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="14"/>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="14"/>
                                <ColumnDefinition Width="*"/>
                            </Grid.ColumnDefinitions>

                            <StackPanel Grid.Column="0">
                                <TextBlock Text="PORT" Style="{StaticResource FieldLabel}"/>
                                <TextBox x:Name="portTextBox" Text="25565"/>
                            </StackPanel>
                            <StackPanel Grid.Column="2">
                                <TextBlock Text="GAMEMODE" Style="{StaticResource FieldLabel}"/>
                                <ComboBox x:Name="gamemodeDropdown"/>
                            </StackPanel>
                            <StackPanel Grid.Column="4">
                                <TextBlock Text="DIFFICULTY" Style="{StaticResource FieldLabel}"/>
                                <ComboBox x:Name="difficultyDropdown"/>
                            </StackPanel>
                            <StackPanel Grid.Column="6">
                                <TextBlock Text="MAX PLAYERS" Style="{StaticResource FieldLabel}"/>
                                <TextBox x:Name="maxPlayersTextBox" Text="20"/>
                            </StackPanel>
                        </Grid>
                    </Border>

                    <!-- Options card -->
                    <TextBlock Text="OPTIONS" Style="{StaticResource SectionLabel}"/>
                    <Border Style="{StaticResource Card}" Margin="0,0,0,20">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="20"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>

                            <StackPanel Grid.Column="0">
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="Auto"/>
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Grid.Column="0"
                                               Text="MEMORY ALLOCATION"
                                               Style="{StaticResource FieldLabel}"
                                               Margin="0"/>
                                    <TextBlock x:Name="serverMemoryValueLabel"
                                               Grid.Column="1"
                                               Text="2 GB"
                                               Foreground="#3fd982"
                                               FontSize="11"
                                               FontWeight="SemiBold"
                                               VerticalAlignment="Center"/>
                                </Grid>
                                <Slider x:Name="serverMemorySlider"
                                        Minimum="1" Maximum="16" Value="2"
                                        TickFrequency="1"
                                        SmallChange="1"
                                        LargeChange="2"
                                        IsSnapToTickEnabled="True"
                                        Margin="0,10,0,2"/>
                            </StackPanel>

                            <StackPanel Grid.Column="2" VerticalAlignment="Center" Margin="0,10,0,0">
                                <CheckBox x:Name="pvpCheckbox" Content="Enable PVP" IsChecked="True"/>
                            </StackPanel>
                        </Grid>
                    </Border>

                    <!-- Action buttons -->
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="12"/>
                            <ColumnDefinition Width="180"/>
                        </Grid.ColumnDefinitions>
                        <Button x:Name="startServerButton"
                                Grid.Column="0"
                                Content="Start Server"
                                Style="{StaticResource PrimaryButton}"
                                Background="#1a7a3a"
                                Height="46"/>
                        <Button x:Name="importWorldButton"
                                Grid.Column="2"
                                Content="Import LAN World"
                                Style="{StaticResource SecondaryButton}"
                                Height="46"/>
                    </Grid>

                    <TextBlock Text="Server JAR must be in: servers\[loader]-[version]\server.jar  —  Server opens in a new window."
                               Foreground="#444444"
                               FontSize="10"
                               Margin="0,12,0,0"/>
                </StackPanel>
            </ScrollViewer>

            <!-- ─────────── MODS PANEL ─────────── -->
            <Grid x:Name="modsPanel" Visibility="Collapsed">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="220"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>

                <TextBlock Grid.Row="0" Text="MODS" Style="{StaticResource SectionLabel}" Margin="0,160,0,8"/>

                <!-- Controls card -->
                <Border Grid.Row="1" Style="{StaticResource Card}" Margin="0,0,0,12">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="200"/>
                            <ColumnDefinition Width="20"/>
                            <ColumnDefinition Width="Auto"/>
                            <ColumnDefinition Width="16"/>
                            <ColumnDefinition Width="Auto"/>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>

                        <StackPanel Grid.Column="0">
                            <TextBlock Text="VERSION" Style="{StaticResource FieldLabel}"/>
                            <ComboBox x:Name="modsVersionDropdown"/>
                        </StackPanel>

                        <StackPanel Grid.Column="2" VerticalAlignment="Bottom" Margin="0,0,0,3">
                            <RadioButton x:Name="modModeClientRadio"
                                         Content="Client Mods"
                                         GroupName="ModModeGroup"
                                         IsChecked="True"/>
                            <RadioButton x:Name="modModeServerRadio"
                                         Content="Server Mods"
                                         GroupName="ModModeGroup"
                                         Margin="0,6,0,0"/>
                        </StackPanel>

                        <!-- Loader radios — hidden until Server Mods selected -->
                        <StackPanel x:Name="modLoaderPanel"
                                    Grid.Column="4"
                                    VerticalAlignment="Bottom"
                                    Visibility="Collapsed"
                                    Margin="0,0,0,3">
                            <WrapPanel>
                                <RadioButton x:Name="modLoaderFabricRadio"  Content="Fabric"  GroupName="ModLoaderGroup" IsChecked="True"/>
                                <RadioButton x:Name="modLoaderForgeRadio"   Content="Forge"   GroupName="ModLoaderGroup"/>
                                <RadioButton x:Name="modLoaderPaperRadio"   Content="Paper"   GroupName="ModLoaderGroup"/>
                                <RadioButton x:Name="modLoaderPurpurRadio"  Content="Purpur"  GroupName="ModLoaderGroup"/>
                            </WrapPanel>
                        </StackPanel>

                        <StackPanel Grid.Column="6" VerticalAlignment="Center" HorizontalAlignment="Right">
                            <TextBlock x:Name="modFolderTypeLabel"
                                       Text=""
                                       Foreground="#555555"
                                       FontSize="10"
                                       HorizontalAlignment="Right"/>
                            <TextBlock x:Name="modCountLabel"
                                       Text=""
                                       Foreground="#777777"
                                       FontSize="10"
                                       HorizontalAlignment="Right"
                                       Margin="0,2,0,0"/>
                        </StackPanel>
                    </Grid>
                </Border>

                <!-- Mod list card -->
                <Border Grid.Row="2" Style="{StaticResource Card}" Padding="0" Margin="0,0,0,12">
                    <ListView x:Name="modsListView"
                              Background="Transparent"
                              Foreground="White"
                              FontSize="12"
                              BorderThickness="0"
                              SelectionMode="Extended">
                        <ListView.View>
                            <GridView>
                                <GridViewColumn Header="File"      Width="460" DisplayMemberBinding="{Binding DisplayName}"/>
                                <GridViewColumn Header="Status"    Width="110" DisplayMemberBinding="{Binding StatusText}"/>
                                <GridViewColumn Header="Size (KB)" Width="90"  DisplayMemberBinding="{Binding SizeKB}"/>
                            </GridView>
                        </ListView.View>
                    </ListView>
                </Border>

                <!-- Mod action buttons -->
                <Grid Grid.Row="3">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="8"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="8"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="8"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <Button x:Name="addModButton"        Grid.Column="0" Content="Add Mod(s)..."     Style="{StaticResource PrimaryButton}"   Background="#0078d4"/>
                    <Button x:Name="removeModButton"     Grid.Column="2" Content="Remove Selected"   Style="{StaticResource PrimaryButton}"   Background="#b43232"/>
                    <Button x:Name="toggleModButton"     Grid.Column="4" Content="Enable / Disable"  Style="{StaticResource PrimaryButton}"   Background="#7a6800"/>
                    <Button x:Name="openModsFolderButton" Grid.Column="6" Content="Open Folder"      Style="{StaticResource SecondaryButton}"/>
                </Grid>

            </Grid>

            <!-- ─────────── SETUP PANEL ─────────── -->
            <Grid x:Name="setupPanel" Visibility="Collapsed">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="160"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>

                <TextBlock Grid.Row="0" Text="SETUP" Style="{StaticResource SectionLabel}" Margin="0,30,0,8"/>

                <!-- Filter card -->
                <Border Grid.Row="1" Style="{StaticResource Card}" Margin="0,0,0,12">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="180"/>
                            <ColumnDefinition Width="16"/>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="16"/>
                            <ColumnDefinition Width="140"/>
                        </Grid.ColumnDefinitions>
                        <StackPanel Grid.Column="0">
                            <TextBlock Text="SHOW" Style="{StaticResource FieldLabel}"/>
                            <ComboBox x:Name="setupVersionTypeFilter"/>
                        </StackPanel>
                        <StackPanel Grid.Column="2">
                            <TextBlock Text="SEARCH" Style="{StaticResource FieldLabel}"/>
                            <TextBox x:Name="setupVersionSearch"/>
                        </StackPanel>
                        <StackPanel Grid.Column="4" VerticalAlignment="Bottom">
                            <Button x:Name="setupRefreshButton" Content="Refresh List" Style="{StaticResource SecondaryButton}"/>
                        </StackPanel>
                    </Grid>
                </Border>

                <!-- Version list -->
                <Border Grid.Row="2" Style="{StaticResource Card}" Padding="0" Margin="0,0,0,12">
                    <ListView x:Name="setupVersionList"
                              Background="Transparent"
                              Foreground="White"
                              FontSize="12"
                              BorderThickness="0"
                              SelectionMode="Single">
                        <ListView.View>
                            <GridView>
                                <GridViewColumn Header="Version"      Width="200" DisplayMemberBinding="{Binding Id}"/>
                                <GridViewColumn Header="Type"         Width="120" DisplayMemberBinding="{Binding Type}"/>
                                <GridViewColumn Header="Release Date" Width="120" DisplayMemberBinding="{Binding Date}"/>
                            </GridView>
                        </ListView.View>
                    </ListView>
                </Border>

                <!-- Loader card -->
                <Border Grid.Row="3" Style="{StaticResource Card}" Margin="0,0,0,12">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto"/>
                            <ColumnDefinition Width="24"/>
                            <ColumnDefinition Width="Auto"/>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>
                        <StackPanel Grid.Column="0">
                            <TextBlock Text="MOD LOADER" Style="{StaticResource FieldLabel}"/>
                            <StackPanel Orientation="Horizontal" Margin="0,6,0,0">
                                <RadioButton x:Name="setupLoaderNoneRadio"   Content="Vanilla Only" GroupName="SetupLoaderGroup" IsChecked="True"/>
                                <RadioButton x:Name="setupLoaderFabricRadio" Content="+ Fabric"     GroupName="SetupLoaderGroup"/>
                            </StackPanel>
                        </StackPanel>
                        <StackPanel x:Name="setupFabricVersionPanel" Grid.Column="2" Visibility="Collapsed">
                            <TextBlock Text="FABRIC LOADER VERSION" Style="{StaticResource FieldLabel}"/>
                            <ComboBox x:Name="setupFabricLoaderDropdown" Width="220"/>
                        </StackPanel>
                        <StackPanel Grid.Column="4" VerticalAlignment="Center" HorizontalAlignment="Right">
                            <TextBlock x:Name="setupStatusLabel" Text="" Foreground="#888888" FontSize="10" HorizontalAlignment="Right"/>
                        </StackPanel>
                    </Grid>
                </Border>

                <!-- Log output -->
                <Border Grid.Row="4" Style="{StaticResource Card}" Padding="0" Margin="0,0,0,12">
                    <TextBox x:Name="setupLogBox"
                             Background="Transparent"
                             Foreground="#aaaaaa"
                             FontSize="10"
                             FontFamily="Consolas"
                             BorderThickness="0"
                             IsReadOnly="True"
                             TextWrapping="Wrap"
                             VerticalScrollBarVisibility="Auto"
                             HorizontalScrollBarVisibility="Disabled"
                             Padding="12,8"
                             Height="Auto"
                             VerticalAlignment="Stretch"
                             VerticalContentAlignment="Top"/>
                </Border>

                <!-- Action buttons -->
                <Grid Grid.Row="5">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="12"/>
                        <ColumnDefinition Width="140"/>
                    </Grid.ColumnDefinitions>
                    <Button x:Name="setupDownloadButton" Grid.Column="0" Content="Download Selected Version" Style="{StaticResource PrimaryButton}" Background="#0078d4"/>
                    <Button x:Name="setupClearLogButton" Grid.Column="2" Content="Clear Log" Style="{StaticResource SecondaryButton}"/>
                </Grid>

            </Grid>

            <!-- ─────────── SKINS PANEL ─────────── -->
            <Grid x:Name="skinsPanel" Visibility="Collapsed">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>

                <TextBlock Grid.Row="0" Text="SKINS" Style="{StaticResource SectionLabel}" Margin="0,30,0,8"/>

                <!-- Skin table card -->
                <Border Grid.Row="1" Style="{StaticResource Card}" Padding="0" Margin="0,0,0,12">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="1"/>
                            <ColumnDefinition Width="220"/>
                        </Grid.ColumnDefinitions>

                        <!-- ListView -->
                        <ListView x:Name="skinsListView"
                                  Grid.Column="0"
                                  Background="Transparent"
                                  Foreground="White"
                                  FontSize="12"
                                  BorderThickness="0"
                                  SelectionMode="Single">
                            <ListView.View>
                                <GridView>
                                    <GridViewColumn Header="Username" Width="200" DisplayMemberBinding="{Binding Username}"/>
                                    <GridViewColumn Header="Model"    Width="80"  DisplayMemberBinding="{Binding Model}"/>
                                    <GridViewColumn Header="File"     Width="300" DisplayMemberBinding="{Binding FileName}"/>
                                </GridView>
                            </ListView.View>
                        </ListView>

                        <!-- Divider -->
                        <Border Grid.Column="1" Background="#2d2d2d" Width="1"/>

                        <!-- Preview panel -->
                        <Grid Grid.Column="2" Background="#1a1a1a">
                            <Grid.RowDefinitions>
                                <RowDefinition Height="*"/>
                                <RowDefinition Height="Auto"/>
                            </Grid.RowDefinitions>

                            <!-- No-skin placeholder -->
                            <TextBlock x:Name="skinPreviewPlaceholder"
                                       Text="Select a skin&#x0a;to preview"
                                       Foreground="#444444"
                                       FontSize="10"
                                       TextAlignment="Center"
                                       TextWrapping="Wrap"
                                       Padding="12,0"
                                       VerticalAlignment="Center"
                                       HorizontalAlignment="Center"/>

                            <!-- WebView2 host — filled at runtime when DLLs are present -->
                            <ContentControl x:Name="skinViewerHost" Visibility="Collapsed"/>

                            <!-- "Open full" button at bottom -->
                            <Button x:Name="skin3DPreviewButton"
                                    Grid.Row="1"
                                    Content="Open in Browser (Full Screen)"
                                    Style="{StaticResource SecondaryButton}"
                                    Margin="16,0,16,16"
                                    IsEnabled="False"/>
                        </Grid>
                    </Grid>
                </Border>

                <!-- Action buttons row -->
                <Grid Grid.Row="2">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="8"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="8"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="8"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <Button x:Name="skinAddButton"    Grid.Column="0" Content="Add Skin..."     Style="{StaticResource PrimaryButton}"   Background="#0078d4"/>
                    <Button x:Name="skinRemoveButton" Grid.Column="2" Content="Remove Selected" Style="{StaticResource PrimaryButton}"   Background="#b43232"/>
                    <Button x:Name="skinToggleButton" Grid.Column="4" Content="Toggle Steve / Alex" Style="{StaticResource PrimaryButton}" Background="#7a6800"/>
                    <Button x:Name="skinOpenFolderButton" Grid.Column="6" Content="Open Skins Folder" Style="{StaticResource SecondaryButton}"/>
                </Grid>
            </Grid>

        </Grid>
    </Grid>
</Window>
'@
#endregion

# Parse XAML into a live WPF Window object
$reader = New-Object System.Xml.XmlNodeReader($xamlDef)
$window = [System.Windows.Markup.XamlReader]::Load($reader)

# ── Pull every named element by name ──────────────────────────
$navClient             = $window.FindName("navClient")
$navServer             = $window.FindName("navServer")
$navMods               = $window.FindName("navMods")
$navSetup              = $window.FindName("navSetup")
$navSkins              = $window.FindName("navSkins")
$uuidLabel             = $window.FindName("uuidLabel")
$resetUUIDButton       = $window.FindName("resetUUIDButton")

$clientPanel           = $window.FindName("clientPanel")
$serverPanel           = $window.FindName("serverPanel")
$modsPanel             = $window.FindName("modsPanel")
$setupPanel            = $window.FindName("setupPanel")
$skinsPanel            = $window.FindName("skinsPanel")

$skinsListView         = $window.FindName("skinsListView")
$skinPreviewPlaceholder= $window.FindName("skinPreviewPlaceholder")
$skinViewerHost        = $window.FindName("skinViewerHost")
$skin3DPreviewButton   = $window.FindName("skin3DPreviewButton")
$skinAddButton         = $window.FindName("skinAddButton")
$skinRemoveButton      = $window.FindName("skinRemoveButton")
$skinToggleButton      = $window.FindName("skinToggleButton")
$skinOpenFolderButton  = $window.FindName("skinOpenFolderButton")

$versionDropdown       = $window.FindName("versionDropdown")
$vanillaRadio          = $window.FindName("vanillaRadio")
$fabricRadio           = $window.FindName("fabricRadio")
$forgeRadio            = $window.FindName("forgeRadio")
$usernameTextBox       = $window.FindName("usernameTextBox")
$memorySlider          = $window.FindName("memorySlider")
$memoryValueLabel      = $window.FindName("memoryValueLabel")
$playButton            = $window.FindName("playButton")
$skinButton            = $window.FindName("skinButton")

$serverVersionDropdown  = $window.FindName("serverVersionDropdown")
$serverVanillaRadio     = $window.FindName("serverVanillaRadio")
$serverFabricRadio      = $window.FindName("serverFabricRadio")
$serverForgeRadio       = $window.FindName("serverForgeRadio")
$serverPaperRadio       = $window.FindName("serverPaperRadio")
$serverPurpurRadio      = $window.FindName("serverPurpurRadio")
$portTextBox            = $window.FindName("portTextBox")
$gamemodeDropdown       = $window.FindName("gamemodeDropdown")
$difficultyDropdown     = $window.FindName("difficultyDropdown")
$maxPlayersTextBox      = $window.FindName("maxPlayersTextBox")
$pvpCheckbox            = $window.FindName("pvpCheckbox")
$serverMemorySlider     = $window.FindName("serverMemorySlider")
$serverMemoryValueLabel = $window.FindName("serverMemoryValueLabel")
$startServerButton      = $window.FindName("startServerButton")
$importWorldButton      = $window.FindName("importWorldButton")

$modsVersionDropdown    = $window.FindName("modsVersionDropdown")
$modModeClientRadio     = $window.FindName("modModeClientRadio")
$modModeServerRadio     = $window.FindName("modModeServerRadio")
$modLoaderPanel         = $window.FindName("modLoaderPanel")
$modLoaderFabricRadio   = $window.FindName("modLoaderFabricRadio")
$modLoaderForgeRadio    = $window.FindName("modLoaderForgeRadio")
$modLoaderPaperRadio    = $window.FindName("modLoaderPaperRadio")
$modLoaderPurpurRadio   = $window.FindName("modLoaderPurpurRadio")
$modFolderTypeLabel     = $window.FindName("modFolderTypeLabel")
$modCountLabel          = $window.FindName("modCountLabel")
$modsListView           = $window.FindName("modsListView")
$addModButton           = $window.FindName("addModButton")
$removeModButton        = $window.FindName("removeModButton")
$toggleModButton        = $window.FindName("toggleModButton")
$openModsFolderButton   = $window.FindName("openModsFolderButton")

$setupVersionTypeFilter    = $window.FindName("setupVersionTypeFilter")
$setupVersionSearch        = $window.FindName("setupVersionSearch")
$setupRefreshButton        = $window.FindName("setupRefreshButton")
$setupVersionList          = $window.FindName("setupVersionList")
$setupLoaderNoneRadio      = $window.FindName("setupLoaderNoneRadio")
$setupLoaderFabricRadio    = $window.FindName("setupLoaderFabricRadio")
$setupFabricVersionPanel   = $window.FindName("setupFabricVersionPanel")
$setupFabricLoaderDropdown = $window.FindName("setupFabricLoaderDropdown")
$setupStatusLabel          = $window.FindName("setupStatusLabel")
$setupLogBox               = $window.FindName("setupLogBox")
$setupDownloadButton       = $window.FindName("setupDownloadButton")
$setupClearLogButton       = $window.FindName("setupClearLogButton")

# ── Style references for active/inactive nav swapping ─────────
$navActiveStyle   = $window.Resources["NavItemActive"]
$navInactiveStyle = $window.Resources["NavItem"]

# ── Background image ──────────────────────────────────────────
$bgPath = Join-Path $scriptDir "background.png"
if (-not (Test-Path $bgPath)) { $bgPath = Join-Path $scriptDir "background.jpg" }
if (Test-Path $bgPath) {
    try {
        $bmp = New-Object System.Windows.Media.Imaging.BitmapImage
        $bmp.BeginInit()
        $bmp.UriSource   = New-Object System.Uri($bgPath, [System.UriKind]::Absolute)
        $bmp.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
        $bmp.EndInit()
        $imgBrush         = New-Object System.Windows.Media.ImageBrush($bmp)
        $imgBrush.Stretch = [System.Windows.Media.Stretch]::Fill
        $window.Background = $imgBrush
    } catch { }
}

# ── Populate static dropdowns ─────────────────────────────────
@("Survival","Creative","Adventure","Hardcore") | ForEach-Object { [void]$gamemodeDropdown.Items.Add($_) }
$gamemodeDropdown.SelectedIndex = 0

@("Peaceful","Easy","Normal","Hard") | ForEach-Object { [void]$difficultyDropdown.Items.Add($_) }
$difficultyDropdown.SelectedIndex = 2

# ── Populate version dropdowns from disk ──────────────────────
$versionsDir = Join-Path $scriptDir "versions"
if (Test-Path $versionsDir) {
    Get-ChildItem $versionsDir -Directory | ForEach-Object {
        [void]$versionDropdown.Items.Add($_.Name)
        [void]$serverVersionDropdown.Items.Add($_.Name)
        [void]$modsVersionDropdown.Items.Add($_.Name)
    }
}

# ── Restore client config ──────────────────────────────────────
$uuidLabel.Text       = $currentUUID
$usernameTextBox.Text = $config.Username
$memorySlider.Value   = $config.Memory
$memoryValueLabel.Text = "$($config.Memory) GB"

if ($config.LastVersion -and $versionDropdown.Items.Contains($config.LastVersion)) {
    $versionDropdown.SelectedItem = $config.LastVersion
} elseif ($versionDropdown.Items.Count -gt 0) {
    $versionDropdown.SelectedIndex = 0
}

switch ($config.LastLoader) {
    "FABRIC" { $fabricRadio.IsChecked  = $true }
    "FORGE"  { $forgeRadio.IsChecked   = $true }
    default  { $vanillaRadio.IsChecked = $true }
}

# ── Restore server config ──────────────────────────────────────
$savedConfig = Load-ServerConfig
if ($savedConfig) {
    if ($savedConfig.LastMemory) {
        $serverMemorySlider.Value = [Math]::Max(1, [Math]::Min(16, $savedConfig.LastMemory))
        $serverMemoryValueLabel.Text = "$([int]$serverMemorySlider.Value) GB"
    }
    if ($savedConfig.LastVersion) {
        $idx = $serverVersionDropdown.Items.IndexOf($savedConfig.LastVersion)
        if ($idx -ge 0) { $serverVersionDropdown.SelectedIndex = $idx }
    }
    if ($savedConfig.LastServerType) {
        switch ($savedConfig.LastServerType.ToUpper()) {
            "FABRIC"  { $serverFabricRadio.IsChecked  = $true }
            "FORGE"   { $serverForgeRadio.IsChecked   = $true }
            "PAPER"   { $serverPaperRadio.IsChecked   = $true }
            "PURPUR"  { $serverPurpurRadio.IsChecked  = $true }
            default   { $serverVanillaRadio.IsChecked = $true }
        }
    }
} else {
    $serverVanillaRadio.IsChecked = $true
}

if ($serverVersionDropdown.Items.Count -gt 0 -and $serverVersionDropdown.SelectedIndex -lt 0) {
    $serverVersionDropdown.SelectedIndex = 0
}
if ($modsVersionDropdown.Items.Count -gt 0) {
    $modsVersionDropdown.SelectedIndex = 0
}

# ════════════════════════════════════════════════════════════
#  EVENT HANDLERS
# ════════════════════════════════════════════════════════════

# ── Nav: helper to switch panels ──────────────────────────────
function Switch-Tab {
    param([string]$Tab)
    $clientPanel.Visibility = [System.Windows.Visibility]::Collapsed
    $serverPanel.Visibility = [System.Windows.Visibility]::Collapsed
    $modsPanel.Visibility   = [System.Windows.Visibility]::Collapsed
    $setupPanel.Visibility  = [System.Windows.Visibility]::Collapsed
    $skinsPanel.Visibility  = [System.Windows.Visibility]::Collapsed

    $navClient.Style = $navInactiveStyle
    $navServer.Style = $navInactiveStyle
    $navMods.Style   = $navInactiveStyle
    $navSetup.Style  = $navInactiveStyle
    $navSkins.Style  = $navInactiveStyle

    switch ($Tab) {
        "Client" {
            $clientPanel.Visibility = [System.Windows.Visibility]::Visible
            $navClient.Style = $navActiveStyle
        }
        "Server" {
            $serverPanel.Visibility = [System.Windows.Visibility]::Visible
            $navServer.Style = $navActiveStyle
        }
        "Mods" {
            $modsPanel.Visibility = [System.Windows.Visibility]::Visible
            $navMods.Style = $navActiveStyle
            Refresh-ModsListView
        }
        "Setup" {
            $setupPanel.Visibility = [System.Windows.Visibility]::Visible
            $navSetup.Style = $navActiveStyle
        }
        "Skins" {
            $skinsPanel.Visibility = [System.Windows.Visibility]::Visible
            $navSkins.Style = $navActiveStyle
            Refresh-SkinsListView
        }
    }
}

$navClient.Add_Click({ Switch-Tab "Client" })
$navServer.Add_Click({ Switch-Tab "Server" })
$navMods.Add_Click({   Switch-Tab "Mods"   })
$navSetup.Add_Click({  Switch-Tab "Setup"  })
$navSkins.Add_Click({  Switch-Tab "Skins"  })

# Stop the HTTP skin server cleanly when the launcher window closes
# Note: the skin server is a standalone process and intentionally survives
# launcher close — it's killed when a new server is started (Stop-SkinHttpServer)

# ── Reset UUID ─────────────────────────────────────────────────
$resetUUIDButton.Add_Click({
    $result = [System.Windows.Forms.MessageBox]::Show(
        "Reset your Computer ID?`nThis gives you a new player identity on LAN worlds.",
        "Reset ID", "YesNo", "Warning"
    )
    if ($result -eq "Yes") {
        Reset-ComputerUUID
        $newUUID = Get-ComputerUUID
        $uuidLabel.Text = $newUUID
        [System.Windows.Forms.MessageBox]::Show("Computer ID reset successfully!", "Reset ID", "OK", "Information")
    }
})

# ── Memory slider labels ───────────────────────────────────────
$memorySlider.Add_ValueChanged({
    $memoryValueLabel.Text = "$([int]$memorySlider.Value) GB"
})

$serverMemorySlider.Add_ValueChanged({
    $serverMemoryValueLabel.Text = "$([int]$serverMemorySlider.Value) GB"
})

# ── PLAY ───────────────────────────────────────────────────────
$playButton.Add_Click({
    if ([string]::IsNullOrWhiteSpace($usernameTextBox.Text)) {
        [System.Windows.Forms.MessageBox]::Show("Please enter a username.", "Error", "OK", "Error")
        return
    }
    if ($versionDropdown.SelectedIndex -eq -1) {
        [System.Windows.Forms.MessageBox]::Show("Please select a version.", "Error", "OK", "Error")
        return
    }

    $username   = $usernameTextBox.Text
    $memory     = [int]$memorySlider.Value
    $version    = $versionDropdown.SelectedItem
    $loaderType = "VANILLA"
    if ($fabricRadio.IsChecked) { $loaderType = "FABRIC" }
    elseif ($forgeRadio.IsChecked) { $loaderType = "FORGE" }

    Save-Config -Username $username -Memory $memory -LastVersion $version -LastLoader $loaderType

    try {
        Start-MinecraftGame -Version $version -LoaderType $loaderType -Username $username -Memory $memory | Out-Null
        $window.Close()
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Failed to launch:`n$($_.Exception.Message)", "Launch Error", "OK", "Error")
    }
})

# ── Change Skin ────────────────────────────────────────────────
$skinButton.Add_Click({
    $username = $usernameTextBox.Text
    if ([string]::IsNullOrWhiteSpace($username)) { $username = "Player" }

    $version = $versionDropdown.SelectedItem
    if ([string]::IsNullOrWhiteSpace($version)) {
        [System.Windows.Forms.MessageBox]::Show("Please select a version first.", "No Version Selected", "OK", "Warning")
        return
    }
    Show-SkinPicker -Username $username -Version $version
})

# ── Start Server ───────────────────────────────────────────────
$startServerButton.Add_Click({
    $version = $serverVersionDropdown.SelectedItem
    if ([string]::IsNullOrWhiteSpace($version)) {
        [System.Windows.Forms.MessageBox]::Show("Please select a version.", "No Version", "OK", "Warning")
        return
    }

    $memory     = [int]$serverMemorySlider.Value
    $loaderType = "VANILLA"
    if ($serverFabricRadio.IsChecked)  { $loaderType = "FABRIC" }
    elseif ($serverForgeRadio.IsChecked)  { $loaderType = "FORGE"  }
    elseif ($serverPaperRadio.IsChecked)  { $loaderType = "PAPER"  }
    elseif ($serverPurpurRadio.IsChecked) { $loaderType = "PURPUR" }

    $svrConfig = @{
        Port       = $portTextBox.Text
        Gamemode   = $gamemodeDropdown.SelectedItem
        Difficulty = $difficultyDropdown.SelectedItem
        MaxPlayers = [int]$maxPlayersTextBox.Text
        PVP        = [bool]$pvpCheckbox.IsChecked
    }

    try {
        # Compute server directory — same formula used inside Start-MinecraftServer
        $serverName    = "$loaderType-$version".ToLower()
        $serverDirPath = Join-Path $scriptDir "servers\$serverName"

        # Write Skin Restorer config BEFORE starting the server so it loads
        # our Yggdrasil provider config at startup rather than the default
        $hostIP = Write-SkinRestorer-Config -ServerDir $serverDirPath `
                      -LoaderType $loaderType -Port $script:skinHttpPort

        # Now start the Minecraft server
        $result = Start-MinecraftServer -Version $version -LoaderType $loaderType -Memory $memory -Config $svrConfig
        Save-ServerConfig -Version $version -ServerType $loaderType -Memory $memory

        # Start the HTTP skin server inside this process (TcpListener, no admin needed)
        Start-SkinHttpServer -Port $script:skinHttpPort


        $lanIPs = (Get-SkinServerIPs | Where-Object { $_ -ne '127.0.0.1' }) -join ", "
        $ipLine = if ($lanIPs) { "`nSkin server LAN IP: $lanIPs" } else { "" }

        [System.Windows.Forms.MessageBox]::Show(
            "Server started!`n`nPort: $($svrConfig.Port)`nConnect via: localhost:$($svrConfig.Port)`n`nSkin server running on port $script:skinHttpPort$ipLine`nSkin Restorer config written automatically.",
            "Server Started", "OK", "Information"
        )
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Failed to start server:`n$($_.Exception.Message)", "Error", "OK", "Error")
    }
})

# ── Import LAN World ───────────────────────────────────────────
$importWorldButton.Add_Click({
    Show-WorldImportDialog
})

# ════════════════════════════════════════════════════════════
#  MOD TAB FUNCTIONS & EVENTS
# ════════════════════════════════════════════════════════════

function Get-SelectedModMode {
    if ($modModeServerRadio.IsChecked) { return "Server" }
    return "Client"
}

function Get-SelectedModLoaderType {
    if ($modLoaderForgeRadio.IsChecked)  { return "Forge"  }
    elseif ($modLoaderPaperRadio.IsChecked)  { return "Paper"  }
    elseif ($modLoaderPurpurRadio.IsChecked) { return "Purpur" }
    return "Fabric"
}

function Update-ModLoaderRowVisibility {
    $vis = if ($modModeServerRadio.IsChecked) {
        [System.Windows.Visibility]::Visible
    } else {
        [System.Windows.Visibility]::Collapsed
    }
    $modLoaderPanel.Visibility = $vis
}

function Get-ModOrPluginNoun {
    if ((Get-SelectedModMode) -eq "Server") {
        $lt = Get-SelectedModLoaderType
        if ($lt -in @("Paper","Purpur")) { return "Plugin" }
    }
    return "Mod"
}

function Update-ModTabLabels {
    $noun = Get-ModOrPluginNoun
    $addModButton.Content = "Add $noun(s)..."
}

function Refresh-ModsListView {
    $modsListView.Items.Clear()
    Update-ModTabLabels
    $noun = Get-ModOrPluginNoun

    $version = $modsVersionDropdown.SelectedItem
    if ([string]::IsNullOrWhiteSpace($version)) {
        $modCountLabel.Text      = ""
        $modFolderTypeLabel.Text = ""
        return
    }

    $mode       = Get-SelectedModMode
    $loaderType = Get-SelectedModLoaderType
    $folder     = Get-ModFolderPath -Version $version -Mode $mode -LoaderType $loaderType
    $modFolderTypeLabel.Text = "→ $(Split-Path $folder -Leaf)"

    $mods = Get-InstalledMods -FolderPath $folder
    foreach ($mod in $mods) {
        [void]$modsListView.Items.Add([PSCustomObject]@{
            DisplayName = $mod.DisplayName
            StatusText  = if ($mod.Enabled) { "Enabled" } else { "Disabled" }
            SizeKB      = $mod.SizeKB
            FileName    = $mod.FileName
            Enabled     = $mod.Enabled
        })
    }

    $enabledCount = ($mods | Where-Object { $_.Enabled }).Count
    $modCountLabel.Text = "$($mods.Count) $($noun.ToLower())(s)  ·  $enabledCount enabled"
}

$modsVersionDropdown.Add_SelectionChanged({ Refresh-ModsListView })
$modModeClientRadio.Add_Click({ Update-ModLoaderRowVisibility; Refresh-ModsListView })
$modModeServerRadio.Add_Click({ Update-ModLoaderRowVisibility; Refresh-ModsListView })
$modLoaderFabricRadio.Add_Click({  Refresh-ModsListView })
$modLoaderForgeRadio.Add_Click({   Refresh-ModsListView })
$modLoaderPaperRadio.Add_Click({   Refresh-ModsListView })
$modLoaderPurpurRadio.Add_Click({  Refresh-ModsListView })

$addModButton.Add_Click({
    $version = $modsVersionDropdown.SelectedItem
    if ([string]::IsNullOrWhiteSpace($version)) {
        [System.Windows.Forms.MessageBox]::Show("Please select a version first.", "No Version Selected", "OK", "Warning")
        return
    }
    $folder = Get-ModFolderPath -Version $version -Mode (Get-SelectedModMode) -LoaderType (Get-SelectedModLoaderType)

    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Filter      = "Mod/Plugin Files (*.jar)|*.jar"
    $dlg.Title       = "Select File(s) to Add"
    $dlg.Multiselect = $true

    if ($dlg.ShowDialog() -eq "OK") {
        $result = Add-ModFiles -FolderPath $folder -SourcePaths $dlg.FileNames
        Refresh-ModsListView
        $msg = "Added $($result.Added) file(s)."
        if ($result.Skipped.Count -gt 0) {
            $msg += "`n`nSkipped (already exists):`n$($result.Skipped -join "`n")"
        }
        [System.Windows.Forms.MessageBox]::Show($msg, "Add Files", "OK", "Information")
    }
})

$removeModButton.Add_Click({
    $version = $modsVersionDropdown.SelectedItem
    if ($modsListView.SelectedItems.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("Select at least one item to remove.", "No Selection", "OK", "Warning")
        return
    }
    $folder = Get-ModFolderPath -Version $version -Mode (Get-SelectedModMode) -LoaderType (Get-SelectedModLoaderType)
    $confirm = [System.Windows.Forms.MessageBox]::Show(
        "Remove $($modsListView.SelectedItems.Count) item(s)? This cannot be undone.",
        "Confirm Removal", "YesNo", "Warning"
    )
    if ($confirm -eq "Yes") {
        @($modsListView.SelectedItems) | ForEach-Object {
            Remove-ModFile -FolderPath $folder -FileName $_.FileName | Out-Null
        }
        Refresh-ModsListView
    }
})

$toggleModButton.Add_Click({
    $version = $modsVersionDropdown.SelectedItem
    if ($modsListView.SelectedItems.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("Select at least one item to toggle.", "No Selection", "OK", "Warning")
        return
    }
    $folder = Get-ModFolderPath -Version $version -Mode (Get-SelectedModMode) -LoaderType (Get-SelectedModLoaderType)
    @($modsListView.SelectedItems) | ForEach-Object {
        $fn      = $_.FileName
        $enabled = ($fn -like "*.jar") -and ($fn -notlike "*.jar.disabled")
        Set-ModEnabled -FolderPath $folder -FileName $fn -Enabled (-not $enabled) | Out-Null
    }
    Refresh-ModsListView
})

$openModsFolderButton.Add_Click({
    $version = $modsVersionDropdown.SelectedItem
    if ([string]::IsNullOrWhiteSpace($version)) {
        [System.Windows.Forms.MessageBox]::Show("Please select a version first.", "No Version Selected", "OK", "Warning")
        return
    }
    $folder = Get-ModFolderPath -Version $version -Mode (Get-SelectedModMode) -LoaderType (Get-SelectedModLoaderType)
    Open-ModsFolder -FolderPath $folder
})

# ── Initial state ─────────────────────────────────────────────
Update-ModLoaderRowVisibility
Refresh-ModsListView

# ════════════════════════════════════════════════════════════
#  SETUP TAB — VERSION LIST + DOWNLOAD LOGIC
# ════════════════════════════════════════════════════════════

# Type filter items
@("Releases Only", "Snapshots Only", "All Versions") | ForEach-Object {
    [void]$setupVersionTypeFilter.Items.Add($_)
}
$setupVersionTypeFilter.SelectedIndex = 0

$script:cachedManifest  = $null
$script:setupLogQueue   = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()

# ── DispatcherTimer drains the log queue on the UI thread ──────
# Avoids all cross-runspace variable scope issues entirely
$setupLogTimer = New-Object System.Windows.Threading.DispatcherTimer
$setupLogTimer.Interval = [System.TimeSpan]::FromMilliseconds(150)
$setupLogTimer.Add_Tick({
    $line = [string]::Empty
    $hasNew = $false
    while ($script:setupLogQueue.TryDequeue([ref]$line)) {
        switch ($line) {
            "__DONE_OK__" {
                $sel = $setupVersionList.SelectedItem
                $id  = if ($sel) { $sel.Id } else { "" }
                $setupStatusLabel.Text         = "Done — $id installed."
                $setupDownloadButton.IsEnabled = $true
                $setupLogTimer.Stop()
                try { $script:setupPs.Dispose() } catch {}
                try { $script:setupRs.Close()   } catch {}
                try { $script:setupRs.Dispose() } catch {}
            }
            "__DONE_FAIL__" {
                $setupStatusLabel.Text         = "Installation failed — check log."
                $setupDownloadButton.IsEnabled = $true
                $setupLogTimer.Stop()
                try { $script:setupPs.Dispose() } catch {}
                try { $script:setupRs.Close()   } catch {}
                try { $script:setupRs.Dispose() } catch {}
            }
            default {
                $setupLogBox.AppendText($line)
                $hasNew = $true
            }
        }
    }
    if ($hasNew) { $setupLogBox.ScrollToEnd() }
})

function Get-MojangManifest {
    if ($script:cachedManifest) { return $script:cachedManifest }
    try {
        $setupStatusLabel.Text = "Fetching version list..."
        $raw = Invoke-WebRequest -Uri "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json" -UseBasicParsing
        $script:cachedManifest = $raw.Content | ConvertFrom-Json
        $setupStatusLabel.Text = ""
        return $script:cachedManifest
    } catch {
        $setupStatusLabel.Text = "Failed to fetch manifest."
        return $null
    }
}

function Populate-VersionList {
    $setupVersionList.Items.Clear()
    $manifest = Get-MojangManifest
    if (-not $manifest) { return }

    $filter     = $setupVersionTypeFilter.SelectedItem
    $searchTerm = $setupVersionSearch.Text.Trim()

    $versions = $manifest.versions | Where-Object {
        $v = $_    # capture before switch overwrites $_
        $typeOk = switch ($filter) {
            "Releases Only"  { $v.type -eq "release"  }
            "Snapshots Only" { $v.type -eq "snapshot" }
            default          { $true }
        }
        $searchOk = [string]::IsNullOrWhiteSpace($searchTerm) -or ($v.id -like "*$searchTerm*")
        $typeOk -and $searchOk
    }

    foreach ($v in $versions) {
        [void]$setupVersionList.Items.Add([PSCustomObject]@{
            Id   = $v.id
            Type = $v.type
            Date = $v.releaseTime.Substring(0, 10)
            Url  = $v.url
            Sha1 = $v.sha1
        })
    }
    $setupStatusLabel.Text = "$($setupVersionList.Items.Count) version(s) shown"
}

function Populate-FabricLoaderDropdown {
    param([string]$McVersion)
    $setupFabricLoaderDropdown.Items.Clear()
    $setupFabricLoaderDropdown.IsEnabled = $false
    if ([string]::IsNullOrWhiteSpace($McVersion)) { return }
    try {
        $raw  = Invoke-WebRequest -Uri "https://meta.fabricmc.net/v2/versions/loader/$McVersion" -UseBasicParsing
        $data = $raw.Content | ConvertFrom-Json
        foreach ($entry in $data) {
            [void]$setupFabricLoaderDropdown.Items.Add($entry.loader.version)
        }
        if ($setupFabricLoaderDropdown.Items.Count -gt 0) {
            $setupFabricLoaderDropdown.SelectedIndex = 0
            $setupFabricLoaderDropdown.IsEnabled = $true
        }
    } catch {
        $setupStatusLabel.Text = "Could not fetch Fabric loader versions."
    }
}

$setupVersionTypeFilter.Add_SelectionChanged({ Populate-VersionList })
$setupVersionSearch.Add_TextChanged({ Populate-VersionList })
$setupRefreshButton.Add_Click({
    $script:cachedManifest = $null
    Populate-VersionList
})

$setupVersionList.Add_SelectionChanged({
    $sel = $setupVersionList.SelectedItem
    if ($sel -and $setupLoaderFabricRadio.IsChecked) {
        Populate-FabricLoaderDropdown -McVersion $sel.Id
    }
})

$setupLoaderFabricRadio.Add_Click({
    $setupFabricVersionPanel.Visibility = [System.Windows.Visibility]::Visible
    $sel = $setupVersionList.SelectedItem
    if ($sel) { Populate-FabricLoaderDropdown -McVersion $sel.Id }
})
$setupLoaderNoneRadio.Add_Click({
    $setupFabricVersionPanel.Visibility = [System.Windows.Visibility]::Collapsed
})

$setupClearLogButton.Add_Click({ $setupLogBox.Clear() })

$setupDownloadButton.Add_Click({
    $selectedItem = $setupVersionList.SelectedItem
    if (-not $selectedItem) {
        [System.Windows.Forms.MessageBox]::Show("Please select a version first.", "No Version Selected", "OK", "Warning")
        return
    }

    $withFabric     = [bool]$setupLoaderFabricRadio.IsChecked
    $fabricLoaderVer = [string]$setupFabricLoaderDropdown.SelectedItem

    if ($withFabric -and [string]::IsNullOrWhiteSpace($fabricLoaderVer)) {
        [System.Windows.Forms.MessageBox]::Show("Please select a Fabric loader version.", "No Loader Selected", "OK", "Warning")
        return
    }

    $setupDownloadButton.IsEnabled = $false
    $setupStatusLabel.Text         = "Downloading..."
    $setupLogBox.Clear()

    # Fresh queue for this download session
    $script:setupLogQueue = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()
    $setupLogTimer.Start()

    # Build background runspace — passes only primitive values + the queue
    $rs = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace()
    $rs.ApartmentState = "STA"
    $rs.Open()
    $rs.SessionStateProxy.SetVariable("logQueue",    $script:setupLogQueue)
    $rs.SessionStateProxy.SetVariable("mcVer",       [string]$selectedItem.Id)
    $rs.SessionStateProxy.SetVariable("verUrl",      [string]$selectedItem.Url)
    $rs.SessionStateProxy.SetVariable("withFabric",  $withFabric)
    $rs.SessionStateProxy.SetVariable("fabricVer",   $fabricLoaderVer)
    $rs.SessionStateProxy.SetVariable("installRoot", [string]$scriptDir)

    $ps = [System.Management.Automation.PowerShell]::Create()
    $ps.Runspace = $rs

    [void]$ps.AddScript({
        # Only enqueue strings — no Dispatcher, no WPF, no UI references
        function Add-Log {
            param([string]$m)
            $ts = (Get-Date).ToString("HH:mm:ss")
            $logQueue.Enqueue("[$ts] $m`n")
        }

        # Diagnostic — confirm variables were received correctly
        Add-Log "[DEBUG] mcVer=$mcVer  withFabric=$withFabric  fabricVer=$fabricVer"
        Add-Log "[DEBUG] installRoot=$installRoot"

        function Get-VerifiedFile {
            param([string]$Uri, [string]$OutFile, [string]$Sha1 = "")
            $dir = Split-Path $OutFile -Parent
            if (-not (Test-Path $dir)) { New-Item -Path $dir -ItemType Directory -Force | Out-Null }

            if (Test-Path $OutFile) {
                if ($Sha1) {
                    $hash = (Get-FileHash -Path $OutFile -Algorithm SHA1).Hash
                    if ($hash -ieq $Sha1) {
                        Add-Log "[SKIP] $(Split-Path $OutFile -Leaf)"
                        return $true
                    }
                    Add-Log "[WARN] Hash mismatch — re-downloading $(Split-Path $OutFile -Leaf)"
                    Remove-Item $OutFile -Force
                } else {
                    Add-Log "[SKIP] $(Split-Path $OutFile -Leaf)"
                    return $true
                }
            }

            try {
                Invoke-WebRequest -Uri $Uri -OutFile $OutFile -UseBasicParsing
                if ($Sha1) {
                    $hash = (Get-FileHash -Path $OutFile -Algorithm SHA1).Hash
                    if (-not ($hash -ieq $Sha1)) {
                        Add-Log "[ERROR] Hash mismatch after download: $(Split-Path $OutFile -Leaf)"
                        return $false
                    }
                }
                Add-Log "[OK] $(Split-Path $OutFile -Leaf)"
                return $true
            } catch {
                Add-Log "[ERROR] $(Split-Path $OutFile -Leaf): $($_.Exception.Message)"
                return $false
            }
        }

        function ConvertTo-MavenPath {
            param([string]$Coordinate)
            $parts      = $Coordinate -split ':'
            $groupPath  = $parts[0] -replace '\.', '/'
            $artifact   = $parts[1]
            $ver        = $parts[2]
            $classifier = if ($parts.Count -gt 3) { "-$($parts[3])" } else { "" }
            return "$groupPath/$artifact/$ver/$artifact-$ver$classifier.jar"
        }

        function Install-VanillaVersion {
            param([string]$McVersion, [string]$VersionUrl)

            $versionDir  = Join-Path $installRoot "versions\$McVersion"
            $versionsDir = Join-Path $versionDir  "versions"
            $libsDir     = Join-Path $versionDir  "libraries"
            $nativesDir  = Join-Path $versionDir  "natives"
            $assetsDir   = Join-Path $versionDir  "assets"

            foreach ($d in @($versionsDir, $libsDir, $nativesDir, $assetsDir)) {
                if (-not (Test-Path $d)) { New-Item -Path $d -ItemType Directory -Force | Out-Null }
            }

            Add-Log "=== Downloading version JSON ==="
            $versionJsonPath = Join-Path $versionsDir "$McVersion.json"
            if (-not (Get-VerifiedFile -Uri $VersionUrl -OutFile $versionJsonPath)) {
                throw "Failed to download version JSON."
            }
            $versionJson = Get-Content $versionJsonPath -Raw | ConvertFrom-Json

            Add-Log "=== Downloading client JAR ==="
            $clientUrl  = $versionJson.downloads.client.url
            $clientSha1 = $versionJson.downloads.client.sha1
            $clientPath = Join-Path $versionsDir "$McVersion-client.jar"
            if (-not (Get-VerifiedFile -Uri $clientUrl -OutFile $clientPath -Sha1 $clientSha1)) {
                throw "Failed to download client JAR."
            }

            Add-Log "=== Downloading libraries ==="
            $libCount = 0
            foreach ($lib in $versionJson.libraries) {
                $skip = $false
                if ($lib.rules) {
                    $allowed = $false
                    foreach ($rule in $lib.rules) {
                        $osMatch = (-not $rule.os) -or ($rule.os.name -eq "windows")
                        if ($rule.action -eq "allow"    -and $osMatch) { $allowed = $true }
                        if ($rule.action -eq "disallow" -and $osMatch) { $skip    = $true }
                    }
                    if (-not $allowed -and -not $skip) { $skip = $true }
                }
                if ($skip) { continue }

                if ($lib.downloads -and $lib.downloads.artifact) {
                    $art     = $lib.downloads.artifact
                    $outPath = Join-Path $libsDir ($art.path -replace '/', '\')
                    Get-VerifiedFile -Uri $art.url -OutFile $outPath -Sha1 $art.sha1 | Out-Null
                    $libCount++
                }

                if ($lib.natives -and $lib.natives.windows -and $lib.downloads.classifiers) {
                    $nativeKey = $lib.natives.windows -replace '\$\{arch\}', '64'
                    $native    = $lib.downloads.classifiers.$nativeKey
                    if ($native) {
                        $nativePath = Join-Path $libsDir ($native.path -replace '/', '\')
                        Get-VerifiedFile -Uri $native.url -OutFile $nativePath -Sha1 $native.sha1 | Out-Null
                        $libCount++
                    }
                }
            }
            Add-Log "[INFO] Processed $libCount library files."

            Add-Log "=== Downloading asset index ==="
            $assetIndex = $versionJson.assetIndex
            $indexesDir = Join-Path $assetsDir "indexes"
            if (-not (Test-Path $indexesDir)) { New-Item -Path $indexesDir -ItemType Directory -Force | Out-Null }
            $indexPath  = Join-Path $indexesDir "$($assetIndex.id).json"
            if (-not (Get-VerifiedFile -Uri $assetIndex.url -OutFile $indexPath -Sha1 $assetIndex.sha1)) {
                throw "Failed to download asset index."
            }

            Add-Log "=== Downloading assets (this may take a while) ==="
            $indexJson  = Get-Content $indexPath -Raw | ConvertFrom-Json
            $objectsDir = Join-Path $assetsDir "objects"
            $assetTotal = ($indexJson.objects.PSObject.Properties | Measure-Object).Count
            $assetDone  = 0
            $assetFail  = 0

            foreach ($prop in $indexJson.objects.PSObject.Properties) {
                $hash    = $prop.Value.hash
                $prefix  = $hash.Substring(0, 2)
                $objDir  = Join-Path $objectsDir $prefix
                $objPath = Join-Path $objDir $hash
                $objUrl  = "https://resources.download.minecraft.net/$prefix/$hash"

                if (-not (Test-Path $objDir)) { New-Item -Path $objDir -ItemType Directory -Force | Out-Null }

                if (Test-Path $objPath) { $assetDone++; continue }

                try {
                    Invoke-WebRequest -Uri $objUrl -OutFile $objPath -UseBasicParsing
                    $assetDone++
                } catch {
                    $assetFail++
                }

                if ($assetDone % 100 -eq 0) {
                    Add-Log "[INFO] Assets: $assetDone / $assetTotal"
                }
            }
            Add-Log "[INFO] Assets complete — $assetDone downloaded, $assetFail failed."
        }

        function Install-FabricLoader {
            param([string]$McVersion, [string]$LoaderVersion)

            $versionDir  = Join-Path $installRoot "versions\$McVersion"
            $versionsDir = Join-Path $versionDir  "versions"
            $libsDir     = Join-Path $versionDir  "libraries"

            Add-Log "=== Downloading Fabric loader profile ==="
            $profileUrl  = "https://meta.fabricmc.net/v2/versions/loader/$McVersion/$LoaderVersion/profile/json"
            $profilePath = Join-Path $versionsDir "fabric-loader-$LoaderVersion-$McVersion.json"

            if (-not (Get-VerifiedFile -Uri $profileUrl -OutFile $profilePath)) {
                throw "Failed to download Fabric loader profile."
            }

            $profileJson = Get-Content $profilePath -Raw | ConvertFrom-Json

            Add-Log "=== Downloading Fabric libraries (ASM, Mixin, etc.) ==="
            $libCount = 0
            foreach ($lib in $profileJson.libraries) {
                $mavenPath = ConvertTo-MavenPath -Coordinate $lib.name
                $outPath   = Join-Path $libsDir ($mavenPath -replace '/', '\')

                if ($lib.downloads -and $lib.downloads.artifact -and $lib.downloads.artifact.url) {
                    Get-VerifiedFile -Uri $lib.downloads.artifact.url -OutFile $outPath -Sha1 $lib.downloads.artifact.sha1 | Out-Null
                } else {
                    $baseUrl = if ($lib.url) { $lib.url.TrimEnd('/') } else { "https://libraries.minecraft.net" }
                    Get-VerifiedFile -Uri "$baseUrl/$mavenPath" -OutFile $outPath | Out-Null
                }
                $libCount++
            }
            Add-Log "[INFO] Processed $libCount Fabric library files."
        }

        # ── Main entry point ──────────────────────────────────────
        try {
            Install-VanillaVersion -McVersion $mcVer -VersionUrl $verUrl
            if ($withFabric) {
                Install-FabricLoader -McVersion $mcVer -LoaderVersion $fabricVer
            }
            Add-Log ""
            Add-Log "=== Installation complete! ==="
            Add-Log "Version '$mcVer' is ready in the Client tab."
            $logQueue.Enqueue("__DONE_OK__")
        } catch {
            Add-Log "[FATAL] $($_.Exception.Message)"
            $logQueue.Enqueue("__DONE_FAIL__")
        }
    })

    # Store for the timer to dispose safely on the UI thread — no cleanup thread needed
    $script:setupPs = $ps
    $script:setupRs = $rs
    $ps.BeginInvoke() | Out-Null
})
# ════════════════════════════════════════════════════════════
#  SKINS TAB
# ════════════════════════════════════════════════════════════

function Refresh-SkinsListView {
    $skinsListView.Items.Clear()
    Get-AllSkins | ForEach-Object {
        [void]$skinsListView.Items.Add($_)
    }
}

# Update the thumbnail preview when a skin is selected
# ── 3D skin model builder ────────────────────────────────────
# Creates a Model3DGroup of UV-mapped cuboids for each body part.
# All UV coordinates are normalised to 0-1 on a 64x64 skin texture.

# ── WebView2 initialisation ──────────────────────────────────
# Note: $script:webView2Loaded is already set by the DLL loading block near the top of the script
$script:webView2Ready    = $false
$script:pendingSkinPath  = $null
$script:pendingSkinModel = "steve"
$script:skinWebView      = $null

if ($script:webView2Loaded) {
    try {
        $script:skinWebView = New-Object Microsoft.Web.WebView2.Wpf.WebView2
        $skinViewerHost.Content    = $script:skinWebView
        $skinViewerHost.Visibility = [System.Windows.Visibility]::Collapsed

        # Locate the Fixed Version runtime — find msedgewebview2.exe inside runtime\webview2runtime\
        $fixedBase = Join-Path $scriptDir "runtime\webview2runtime"
        $fixedExe  = Get-ChildItem -Path $fixedBase -Recurse -Filter "msedgewebview2.exe" `
                        -ErrorAction SilentlyContinue | Select-Object -First 1
        $fixedPath = if ($fixedExe) { $fixedExe.DirectoryName } else { $fixedBase }

        # Apply required icacls permissions for Fixed Version on Windows 10 (harmless on Win 11)
        if (Test-Path $fixedPath) {
            try {
                Start-Process "icacls" `
                    -ArgumentList "`"$fixedPath`" /grant *S-1-15-2-2:(OI)(CI)(RX)" `
                    -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
                Start-Process "icacls" `
                    -ArgumentList "`"$fixedPath`" /grant *S-1-15-2-1:(OI)(CI)(RX)" `
                    -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
            } catch {}
        }

        # Wire up the completion event before starting init
        $script:skinWebView.add_CoreWebView2InitializationCompleted({
            $script:webView2Ready = $true
            if ($script:pendingSkinPath) {
                Show-SkinInWebView -SkinPath $script:pendingSkinPath -Model $script:pendingSkinModel
                $script:pendingSkinPath = $null
            }
        })

        # CreateAsync with Fixed Version path — returns a Task<CoreWebView2Environment>
        # Poll on the UI thread with a DispatcherTimer so we don't block the message pump
        $script:createEnvTask = [Microsoft.Web.WebView2.Core.CoreWebView2Environment]::CreateAsync(
            $fixedPath, $null, $null)

        $script:envPollTimer = New-Object System.Windows.Threading.DispatcherTimer
        $script:envPollTimer.Interval = [System.TimeSpan]::FromMilliseconds(100)
        $script:envPollTimer.Add_Tick({
            if ($script:createEnvTask.IsCompleted) {
                $script:envPollTimer.Stop()
                if (-not $script:createEnvTask.IsFaulted -and `
                    -not $script:createEnvTask.IsCanceled) {
                    try {
                        $wv2Env = $script:createEnvTask.Result
                        $script:skinWebView.EnsureCoreWebView2Async($wv2Env) | Out-Null
                    } catch {}
                }
            }
        })
        $script:envPollTimer.Start()

    } catch {
        $script:webView2Error  = "WebView2 control init failed: $($_.Exception.Message)"
        $script:webView2Loaded = $false
    }
}

# ── HTML skin viewer generator ───────────────────────────────
function Show-SkinInWebView {
    param([string]$SkinPath, [string]$Model = "steve")

    $bundlePath = Join-Path $scriptDir "runtime\skinview3d\skinview3d.bundle.js"
    if (-not (Test-Path $bundlePath)) {
        $skinPreviewPlaceholder.Text = "skinview3d.bundle.js not found.`nPlace it at:`nruntime\skinview3d\skinview3d.bundle.js"
        return
    }

    $skinBytes   = [System.IO.File]::ReadAllBytes($SkinPath)
    $skinBase64  = [System.Convert]::ToBase64String($skinBytes)
    $bundleJs    = [System.IO.File]::ReadAllText($bundlePath, [System.Text.Encoding]::UTF8)
    $modelType   = if ($Model -eq "alex") { "slim" } else { "default" }

    # Build HTML — split around $bundleJs so PS doesn't try to expand it
    $h1 = @'
<!DOCTYPE html><html><head><meta charset="UTF-8">
<style>
  *{margin:0;padding:0;box-sizing:border-box}
  body{background:#141414;overflow:hidden;display:flex;align-items:center;justify-content:center;height:100vh}
  canvas{display:block}
</style></head><body>
<canvas id="c"></canvas>
<script>
'@
    $h2 = @"
</script>
<script>
const viewer = new skinview3d.SkinViewer({
  canvas: document.getElementById('c'),
  width: window.innerWidth,
  height: window.innerHeight,
  skin: 'data:image/png;base64,$skinBase64'
});
viewer.controls = skinview3d.createOrbitControls(viewer);
viewer.controls.enableRotate = true;
viewer.controls.enableZoom   = true;
viewer.controls.enablePan    = false;
viewer.autoRotate      = true;
viewer.autoRotateSpeed = 0.5;
viewer.zoom            = 0.75;
viewer.animation = new skinview3d.WalkingAnimation();
viewer.playerObject.skin.modelType = '$modelType';
window.addEventListener('resize', () => viewer.setSize(window.innerWidth, window.innerHeight));
</script>
</body></html>
"@
    $html = $h1 + $bundleJs + $h2

    if ($script:webView2Ready) {
        $script:skinWebView.NavigateToString($html)
    } else {
        $script:pendingSkinPath  = $SkinPath
        $script:pendingSkinModel = $Model
    }
}

# ── Skin preview update ───────────────────────────────────────
function Update-SkinPreview {
    param($SkinEntry)

    if (-not $SkinEntry -or -not (Test-Path $SkinEntry.FullPath)) {
        $skinViewerHost.Visibility         = [System.Windows.Visibility]::Collapsed
        $skinPreviewPlaceholder.Visibility = [System.Windows.Visibility]::Visible
        $skinPreviewPlaceholder.Text       = "Select a skin`nto preview"
        $skin3DPreviewButton.IsEnabled     = $false
        return
    }

    if ($script:webView2Loaded) {
        $skinPreviewPlaceholder.Visibility = [System.Windows.Visibility]::Collapsed
        $skinViewerHost.Visibility         = [System.Windows.Visibility]::Visible
        $skin3DPreviewButton.IsEnabled     = $true
        Show-SkinInWebView -SkinPath $SkinEntry.FullPath -Model $SkinEntry.Model
    } else {
        # Show exactly what went wrong so we can diagnose
        $skinPreviewPlaceholder.Visibility = [System.Windows.Visibility]::Visible
        $skinViewerHost.Visibility         = [System.Windows.Visibility]::Collapsed
        $msg = if ($script:webView2Error) { $script:webView2Error } else { "WebView2 not available" }
        $skinPreviewPlaceholder.Text = "3D preview unavailable:`n$msg"
        $skin3DPreviewButton.IsEnabled = $true
    }
}

# ── Skin list selection → preview ───────────────────────────
$skinsListView.Add_SelectionChanged({
    $sel = $skinsListView.SelectedItem
    Update-SkinPreview -SkinEntry $sel
})

# ── Add Skin ─────────────────────────────────────────────────
$skinAddButton.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Filter      = "PNG Skin Files (*.png)|*.png"
    $dlg.Title       = "Select Minecraft Skin PNG"
    $dlg.Multiselect = $false

    if ($dlg.ShowDialog() -ne "OK") { return }

    $srcPath  = $dlg.FileName
    $basename = [System.IO.Path]::GetFileNameWithoutExtension($srcPath)

    $username = [Microsoft.VisualBasic.Interaction]::InputBox(
        "Enter the Minecraft username this skin belongs to:",
        "Username for Skin",
        $basename
    )
    if ([string]::IsNullOrWhiteSpace($username)) { return }
    $username = $username.Trim()

    try {
        Add-SkinFile -SourcePath $srcPath -Username $username -Model "steve"
        Refresh-SkinsListView
        $newItem = $skinsListView.Items | Where-Object { $_.Username -eq $username } | Select-Object -First 1
        if ($newItem) {
            $skinsListView.SelectedItem = $newItem
            $skinsListView.ScrollIntoView($newItem)
        }
    } catch {
        [System.Windows.Forms.MessageBox]::Show(
            "Failed to add skin: $($_.Exception.Message)", "Error", "OK", "Error")
    }
})

# ── Remove Skin ───────────────────────────────────────────────
$skinRemoveButton.Add_Click({
    $sel = $skinsListView.SelectedItem
    if (-not $sel) {
        [System.Windows.Forms.MessageBox]::Show(
            "Please select a skin to remove.", "No Selection", "OK", "Warning")
        return
    }
    $result = [System.Windows.Forms.MessageBox]::Show(
        "Remove skin for '$($sel.Username)'? The PNG file will be deleted.",
        "Confirm Removal", "YesNo", "Warning")
    if ($result -ne "Yes") { return }

    Remove-SkinFile -Username $sel.Username
    Update-SkinPreview -SkinEntry $null
    Refresh-SkinsListView
})

# ── Toggle Steve / Alex ───────────────────────────────────────
$skinToggleButton.Add_Click({
    $sel = $skinsListView.SelectedItem
    if (-not $sel) {
        [System.Windows.Forms.MessageBox]::Show(
            "Please select a skin to toggle.", "No Selection", "OK", "Warning")
        return
    }
    $newModel = if ($sel.Model -eq "steve") { "alex" } else { "steve" }
    Set-SkinModel -Username $sel.Username -Model $newModel
    Refresh-SkinsListView
    $updatedItem = $skinsListView.Items | Where-Object { $_.Username -eq $sel.Username } | Select-Object -First 1
    if ($updatedItem) {
        $skinsListView.SelectedItem = $updatedItem
        Update-SkinPreview -SkinEntry $updatedItem
    }
})

# ── Open Skins Folder ─────────────────────────────────────────
$skinOpenFolderButton.Add_Click({
    Start-Process -FilePath "explorer.exe" -ArgumentList "`"$(Get-SkinsDir)`""
})

# ── Open in Browser (Full Screen) ────────────────────────────
$skin3DPreviewButton.Add_Click({
    $sel = $skinsListView.SelectedItem
    if (-not $sel) { return }

    $bundlePath = Join-Path $scriptDir "runtime\skinview3d\skinview3d.bundle.js"
    if (-not (Test-Path $bundlePath)) {
        [System.Windows.Forms.MessageBox]::Show(
            "skinview3d.bundle.js not found.`n`nPlace it at:`nruntime\skinview3d\skinview3d.bundle.js",
            "3D Viewer Not Available", "OK", "Warning")
        return
    }

    $skinBytes  = [System.IO.File]::ReadAllBytes($sel.FullPath)
    $skinBase64 = [System.Convert]::ToBase64String($skinBytes)
    $bundleJs   = [System.IO.File]::ReadAllText($bundlePath, [System.Text.Encoding]::UTF8)
    $modelType  = if ($sel.Model -eq "alex") { "slim" } else { "default" }

    $h1 = @'
<!DOCTYPE html><html><head><meta charset="UTF-8">
<title>Skin Preview</title>
<style>*{margin:0;padding:0}body{background:#1a1a1a;display:flex;flex-direction:column;align-items:center;justify-content:center;height:100vh}canvas{display:block}.lbl{margin-top:10px;color:#888;font:12px Segoe UI}</style>
</head><body><canvas id="c" width="300" height="500"></canvas>
<div class="lbl">Drag to rotate · Scroll to zoom</div>
<script>
'@
    $h2 = @"
</script><script>
const v=new skinview3d.SkinViewer({canvas:document.getElementById('c'),width:300,height:500,skin:'data:image/png;base64,$skinBase64'});
v.controls=skinview3d.createOrbitControls(v);
v.controls.enableRotate=true;v.controls.enableZoom=true;v.controls.enablePan=false;
v.autoRotate=true;v.autoRotateSpeed=0.6;
v.animation=new skinview3d.WalkingAnimation();
v.playerObject.skin.modelType='$modelType';
</script></body></html>
"@
    $tmpHtml = Join-Path $env:TEMP "skinpreview_$($sel.Username).html"
    ($h1 + $bundleJs + $h2) | Out-File -FilePath $tmpHtml -Encoding UTF8 -Force
    Start-Process $tmpHtml
})

# Initial population
Refresh-SkinsListView

[void]$window.ShowDialog()
#endregion

} catch {
    $errorMessage = "Error: $($_.Exception.Message)`n`nStack Trace:`n$($_.ScriptStackTrace)"
    $errorMessage | Out-File -FilePath $errorLogFile -Encoding UTF8
    [System.Windows.Forms.MessageBox]::Show($errorMessage, "Launcher Error", "OK", "Error")
    exit 1
}
