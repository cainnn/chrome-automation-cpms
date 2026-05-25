param([int]$DurationSec = 600)

Add-Type -AssemblyName UIAutomationClient
$deadline = (Get-Date).AddSeconds($DurationSec)
$clicked = 0
$keepLabel = [char]0x4FDD + [char]0x7559

while ((Get-Date) -lt $deadline) {
    try {
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $nameCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $keepLabel)
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
        if ($el) {
            $invoke = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            if ($invoke) {
                $invoke.Invoke()
                $clicked++
                Write-Host "[keep-clicker] clicked Keep ($clicked)"
            }
        }
    }
    catch {
    }

    Start-Sleep -Seconds 2
}

Write-Host "[keep-clicker] done, clicks=$clicked"
