<#
.SYNOPSIS
    Interactive installer for SolidWorks add-ins (SolidWorks3DPrintDFMAddin and SolidWorksJoineryAddin).
    Shows a multi-select checkbox dialog by default so the user can choose which add-ins to build and register.
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Project = "",
    [string[]]$Projects = @(),
    [switch]$NoGui
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$catalog = @(
    @{
        Name = "SolidWorks3DPrintDFMAddin"
        Label = "3D Print DFM Add-in (Teardrops & Crisscross Bridges)"
        DefaultChecked = $true
    },
    @{
        Name = "SolidWorksJoineryAddin"
        Label = "Multi-Body Joinery Add-in (Tab & Slot, Finger Joint, Dado)"
        DefaultChecked = $true
    }
)

$requestedProjects = @()
if (-not [string]::IsNullOrWhiteSpace($Project)) {
    $requestedProjects += ($Project -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })
}
if ($Projects -and $Projects.Count -gt 0) {
    foreach ($p in $Projects) {
        if (-not [string]::IsNullOrWhiteSpace($p)) {
            $requestedProjects += ($p -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })
        }
    }
}

$selectedProjects = @()

if ($requestedProjects.Count -gt 0) {
    $validNames = @($catalog | ForEach-Object { $_.Name })
    $unknown = @($requestedProjects | Where-Object { $validNames -inotcontains $_ })
    if ($unknown.Count -gt 0) {
        Write-Error "Unknown add-in(s): $($unknown -join ', '). Valid values: $($validNames -join ', ')"
    }
    $selectedProjects = @($catalog | Where-Object { $requestedProjects -icontains $_.Name } | ForEach-Object { $_.Name })
} elseif ($NoGui) {
    $selectedProjects = @($catalog | ForEach-Object { $_.Name })
} else {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    [System.Windows.Forms.Application]::EnableVisualStyles()

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "Install SolidWorks Add-ins"
    $form.ClientSize = New-Object System.Drawing.Size(500, 230)
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
    $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.TopMost = $true
    $form.Font = New-Object System.Drawing.Font("Segoe UI", 9.5)

    $label = New-Object System.Windows.Forms.Label
    $label.Text = "Select which SolidWorks add-ins to build and register:"
    $label.Location = New-Object System.Drawing.Point(16, 14)
    $label.Size = New-Object System.Drawing.Size(468, 24)
    $form.Controls.Add($label)

    $checkedListBox = New-Object System.Windows.Forms.CheckedListBox
    $checkedListBox.Location = New-Object System.Drawing.Point(16, 44)
    $checkedListBox.Size = New-Object System.Drawing.Size(468, 120)
    $checkedListBox.CheckOnClick = $true
    $checkedListBox.IntegralHeight = $false
    $checkedListBox.Font = New-Object System.Drawing.Font("Segoe UI", 10)
    foreach ($entry in $catalog) {
        [void]$checkedListBox.Items.Add($entry.Label, $entry.DefaultChecked)
    }
    $form.Controls.Add($checkedListBox)

    $selectAllButton = New-Object System.Windows.Forms.Button
    $selectAllButton.Text = "Select All"
    $selectAllButton.Location = New-Object System.Drawing.Point(16, 180)
    $selectAllButton.Size = New-Object System.Drawing.Size(90, 32)
    $selectAllButton.Add_Click({
        for ($i = 0; $i -lt $checkedListBox.Items.Count; $i++) {
            $checkedListBox.SetItemChecked($i, $true)
        }
    })
    $form.Controls.Add($selectAllButton)

    $clearAllButton = New-Object System.Windows.Forms.Button
    $clearAllButton.Text = "Clear All"
    $clearAllButton.Location = New-Object System.Drawing.Point(114, 180)
    $clearAllButton.Size = New-Object System.Drawing.Size(90, 32)
    $clearAllButton.Add_Click({
        for ($i = 0; $i -lt $checkedListBox.Items.Count; $i++) {
            $checkedListBox.SetItemChecked($i, $false)
        }
    })
    $form.Controls.Add($clearAllButton)

    $okButton = New-Object System.Windows.Forms.Button
    $okButton.Text = "Build && Install"
    $okButton.Location = New-Object System.Drawing.Point(278, 180)
    $okButton.Size = New-Object System.Drawing.Size(116, 32)
    $okButton.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.Controls.Add($okButton)

    $cancelButton = New-Object System.Windows.Forms.Button
    $cancelButton.Text = "Cancel"
    $cancelButton.Location = New-Object System.Drawing.Point(402, 180)
    $cancelButton.Size = New-Object System.Drawing.Size(82, 32)
    $cancelButton.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.Controls.Add($cancelButton)

    $form.AcceptButton = $okButton
    $form.CancelButton = $cancelButton
    $form.Add_Shown({ $form.Activate() })

    $dialogResult = $form.ShowDialog()
    if ($dialogResult -eq [System.Windows.Forms.DialogResult]::OK) {
        foreach ($idx in $checkedListBox.CheckedIndices) {
            $selectedProjects += $catalog[$idx].Name
        }
    }
    $form.Dispose()

    if ($dialogResult -ne [System.Windows.Forms.DialogResult]::OK) {
        Write-Host "Installation cancelled by user." -ForegroundColor Yellow
        exit 0
    }

    if ($selectedProjects.Count -eq 0) {
        Write-Host "No add-ins selected. Exiting." -ForegroundColor Yellow
        exit 0
    }
}

Write-Host "Selected add-ins ($Configuration): $($selectedProjects -join ', ')" -ForegroundColor Cyan

$global:LASTEXITCODE = 0
& "$scriptDir\Build-Addin.ps1" -Configuration $Configuration -Projects $selectedProjects
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build-Addin.ps1 exited with code $LASTEXITCODE"
}

$global:LASTEXITCODE = 0
& "$scriptDir\Register-Addin.ps1" -Configuration $Configuration -Projects $selectedProjects
if ($LASTEXITCODE -ne 0) {
    Write-Error "Register-Addin.ps1 exited with code $LASTEXITCODE"
}

Write-Host ""
Write-Host "Successfully built and registered: $($selectedProjects -join ', ')" -ForegroundColor Green
