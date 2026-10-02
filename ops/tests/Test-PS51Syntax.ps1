# Parses every ops script with the PowerShell parser and flags syntax or parameters that Windows PowerShell 5.1 lacks.
# Run with PowerShell 7 (pwsh) on any OS:  pwsh -NoProfile -File ops/tests/Test-PS51Syntax.ps1
# Exit code = number of problems.
param([string[]]$Files = @())

if ($Files.Count -eq 0) {
    $Files = @(Get-ChildItem -Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts') -Filter '*.ps1' | ForEach-Object { $_.FullName })
    $Files += @(Get-ChildItem -Path $PSScriptRoot -Filter '*.ps1' | ForEach-Object { $_.FullName })
}
# Parameters that exist only in PowerShell 6+ on cmdlets these scripts use.
$ps7Params = @{
    'ConvertFrom-Json' = @('AsHashtable', 'Depth', 'NoEnumerate')
    'Invoke-WebRequest' = @('SkipCertificateCheck', 'SkipHttpErrorCheck', 'StatusCodeVariable', 'Resume', 'Authentication', 'Token', 'SslProtocol')
    'Invoke-RestMethod' = @('SkipCertificateCheck', 'SkipHttpErrorCheck', 'StatusCodeVariable', 'Resume', 'Authentication', 'Token', 'SslProtocol', 'ResponseHeadersVariable')
    'Get-Content' = @('AsByteStream')
    'Set-Content' = @('AsByteStream')
    'Join-Path' = @('AdditionalChildPath')
    'Test-Connection' = @('TcpPort', 'TargetName')
    'ForEach-Object' = @('Parallel')
    'Select-Object' = @('SkipLast')
    'Import-Csv' = @('SkipHeader')
}
$bad = 0
foreach ($f in $Files) {
    $t = $null; $e = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $f), [ref]$t, [ref]$e)
    $issues = New-Object System.Collections.Generic.List[string]
    foreach ($x in $e) { $issues.Add('parse error: ' + $x.Message + ' line ' + $x.Extent.StartLineNumber) }
    $ps7 = @($ast.FindAll({ param($n)
        $n -is [System.Management.Automation.Language.TernaryExpressionAst] -or
        $n -is [System.Management.Automation.Language.PipelineChainAst] -or
        ($n -is [System.Management.Automation.Language.BinaryExpressionAst] -and $n.Operator -eq 'QuestionQuestion') -or
        ($n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Operator -eq 'QuestionQuestionEquals') -or
        ($n -is [System.Management.Automation.Language.MemberExpressionAst] -and $n.NullConditional) }, $true))
    foreach ($x in $ps7) { $issues.Add('PS7-only syntax line ' + $x.Extent.StartLineNumber + ': ' + $x.Extent.Text) }
    foreach ($tok in @($t | Where-Object { $_.Kind -in 'QuestionDot', 'QuestionLBracket', 'QuestionQuestion', 'QuestionQuestionEquals', 'AndAnd', 'OrOr' })) {
        $issues.Add('PS7-only token line ' + $tok.Extent.StartLineNumber + ': ' + $tok.Text)
    }
    foreach ($cmd in @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))) {
        $name = $cmd.GetCommandName()
        if (-not $name) { continue }
        if ($ps7Params.ContainsKey($name)) {
            foreach ($p in @($cmd.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.CommandParameterAst] })) {
                foreach ($bp in $ps7Params[$name]) {
                    if ([string]::Equals($bp, $p.ParameterName, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $issues.Add("PS6+ parameter -$($p.ParameterName) on $name line $($cmd.Extent.StartLineNumber)")
                    }
                }
            }
        }
        if ($name -eq 'Join-Path') {
            $positional = @($cmd.CommandElements | Select-Object -Skip 1 | Where-Object { -not ($_ -is [System.Management.Automation.Language.CommandParameterAst]) })
            $named = @($cmd.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.CommandParameterAst] })
            if ($positional.Count -gt 2 -and $named.Count -eq 0) { $issues.Add("Join-Path with $($positional.Count) positional parts (5.1 takes 2) line $($cmd.Extent.StartLineNumber)") }
        }
        foreach ($p in @($cmd.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.CommandParameterAst] -and $_.ParameterName -eq 'Encoding' })) {
            $idx = [array]::IndexOf($cmd.CommandElements, $p)
            if ($idx -ge 0 -and $idx + 1 -lt $cmd.CommandElements.Count) {
                $v = $cmd.CommandElements[$idx + 1].Extent.Text.Trim("'", '"')
                if ($v -match '^(utf8NoBOM|utf8BOM)$') { $issues.Add("-Encoding $v is PS6+ line $($cmd.Extent.StartLineNumber)") }
            }
        }
    }
    # Static .NET calls that do not exist in .NET Framework 4.5.
    foreach ($m in @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and $n.Static }, $true))) {
        $txt = $m.Extent.Text
        if ($txt -match '^\[(System\.)?IO\.Path\]::(GetRelativePath|Join)\b' -or $txt -match '^\[(System\.)?Text\.Json') { $issues.Add("API missing on .NET Framework line $($m.Extent.StartLineNumber): $txt") }
    }
    $short = Split-Path -Leaf $f
    if ($issues.Count -eq 0) { "{0}: OK (0 parse errors, 0 PS5.1 problems)" -f $short }
    else { "{0}: {1} problem(s)" -f $short, $issues.Count; foreach ($i in $issues) { "  $i" } }
    $bad += $issues.Count
}
exit $bad
