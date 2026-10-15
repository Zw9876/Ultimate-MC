# Selecting a value in a WPF ComboBox over UI Automation.
#
# Dot-sourced by the Verify-* scripts. It exists because of two separate traps:
#
#   1. ValuePattern.SetValue does NOT update ComboBox.Text. A dialog was once
#      left unverifiable in both directions by exactly that: the test set the
#      value, the code read Text, got empty, and did nothing. So selection is
#      done by SELECTING AN ITEM, which is what SelectionItemPattern is for.
#
#   2. WPF virtualises ComboBox items. They are not in the automation tree at
#      all until the dropdown is expanded, so searching a collapsed ComboBox
#      finds nothing and looks exactly like "that value is not offered".
#      Hence Expand first, every time.
#
# Deliberately self-contained - no dependency on each script's own ById helper,
# so this can be dot-sourced into any of them without ordering games.

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes -ErrorAction SilentlyContinue

function Get-ComboItems($combo) {
    @($combo.FindAll([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::ListItem))))
}

function Expand-Combo($combo) {
    try {
        $combo.GetCurrentPattern(
            [Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 400
        return $true
    } catch { return $false }
}

function Collapse-Combo($combo) {
    try {
        $combo.GetCurrentPattern(
            [Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    } catch { }
}

function Find-Combo($window, $id) {
    $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
}

<#
.SYNOPSIS
Selects the LAST item in a ComboBox and returns its name, or $null.

.DESCRIPTION
The last one because HostableVersions() is sorted with MavenVersion.Comparer, so
the newest version is at the end - which is the answer a person would pick, and
the one a test should exercise rather than an arbitrary middle entry.
#>
function Select-ComboNewest($window, $id) {
    $combo = Find-Combo $window $id
    if (-not $combo) { return $null }
    if (-not (Expand-Combo $combo)) { return $null }

    $items = Get-ComboItems $combo
    if ($items.Count -eq 0) { Collapse-Combo $combo; return $null }

    $pick = $items[$items.Count - 1]
    $name = $pick.Current.Name
    try {
        $pick.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    } catch {
        Collapse-Combo $combo
        return $null
    }
    Start-Sleep -Milliseconds 300
    return $name
}

<#
.SYNOPSIS
Selects a named item in a ComboBox. Returns $true when it was found and selected.
#>
function Select-ComboValue($window, $id, $value) {
    $combo = Find-Combo $window $id
    if (-not $combo) { return $false }
    if (-not (Expand-Combo $combo)) { return $false }

    $item = $combo.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::NameProperty, $value)))

    if (-not $item) { Collapse-Combo $combo; return $false }

    try {
        $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    } catch {
        Collapse-Combo $combo
        return $false
    }
    Start-Sleep -Milliseconds 300
    return $true
}
