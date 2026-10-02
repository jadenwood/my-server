# Parse check for every PowerShell script in the repository (CI and local).
# Runs on PowerShell 7 (pwsh) on any OS:  pwsh -NoProfile -File tools/rok-sim/ci/ps-parse-check.ps1
#
# A file fails when the PowerShell parser reports an error, or when it uses syntax that exists only in
# PowerShell 7 (the server scripts must run in Windows PowerShell 5.1): ternary "a ? b : c", pipeline
# chains "&&" / "||", null-coalescing "??" / "??=", and null-conditional "?." / "?[]".
# Nothing is executed: the files are only parsed.
# Exit code = number of problems, capped at 255 (0 = all good).
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path,
    [string[]]$Exclude = @('node_modules', '.git', 'dist')
)

$files = @(Get-ChildItem -Path $Root -Recurse -File -Include '*.ps1', '*.psm1', '*.psd1' -ErrorAction SilentlyContinue |
    Where-Object {
        $rel = $_.FullName.Substring($Root.Length)
        -not ($Exclude | Where-Object { $rel -match ('[\\/]' + [regex]::Escape($_) + '[\\/]') })
    } | Sort-Object FullName)

$bad = 0
foreach ($f in $files) {
    $rel = $f.FullName.Substring($Root.Length).TrimStart('\', '/')
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errors)
    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($e in $errors) { $problems.Add(('parse error line {0}: {1}' -f $e.Extent.StartLineNumber, $e.Message)) }
    $ps7 = @($ast.FindAll({
        param($n)
        $n -is [System.Management.Automation.Language.TernaryExpressionAst] -or
        $n -is [System.Management.Automation.Language.PipelineChainAst] -or
        ($n -is [System.Management.Automation.Language.BinaryExpressionAst] -and $n.Operator -eq 'QuestionQuestion') -or
        ($n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Operator -eq 'QuestionQuestionEquals') -or
        ($n -is [System.Management.Automation.Language.MemberExpressionAst] -and $n.NullConditional) -or
        ($n -is [System.Management.Automation.Language.IndexExpressionAst] -and $n.NullConditional)
    }, $true))
    foreach ($n in $ps7) { $problems.Add(('PowerShell 7-only syntax line {0}: {1}' -f $n.Extent.StartLineNumber, $n.Extent.Text.Split("`n")[0])) }
    if ($problems.Count) {
        Write-Output ('FAIL {0}' -f $rel)
        foreach ($p in $problems) { Write-Output ('  ' + $p) }
        $bad += $problems.Count
    } else {
        Write-Output ('ok   {0}' -f $rel)
    }
}
Write-Output ('{0} PowerShell file(s) parsed, {1} problem(s).' -f $files.Count, $bad)
if ($files.Count -eq 0) { Write-Output 'No PowerShell files found: check -Root.'; exit 1 }
exit [Math]::Min($bad, 255)
