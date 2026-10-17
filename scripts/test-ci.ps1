$ErrorActionPreference = 'Stop'

$arguments = @(
    'test', 'KaraokeFilmMaker.slnx',
    '-c', 'Release',
    '--no-build',
    '--no-restore',
    '--logger', 'trx;LogFilePrefix=test-results'
)

if ($env:GITHUB_ACTIONS -eq 'true') {
    Write-Output '::notice::Skipping WasapiPlaybackTests because GitHub-hosted runners have no default audio endpoint.'
    $arguments += @('--filter', 'FullyQualifiedName!~WasapiPlaybackTests')
}

& dotnet @arguments
$testExitCode = $LASTEXITCODE

if ($testExitCode -eq 0) { exit 0 }

function ConvertTo-GitHubCommandValue([string] $Value) {
    return $Value.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
}

$reports = Get-ChildItem -Path . -Recurse -Filter 'test-results*.trx' -File
foreach ($report in $reports) {
    [xml] $document = Get-Content -LiteralPath $report.FullName
    $failures = $document.SelectNodes("//*[local-name()='UnitTestResult' and @outcome='Failed']")
    foreach ($failure in $failures) {
        $name = ConvertTo-GitHubCommandValue ([string] $failure.testName)
        $messageNode = $failure.SelectSingleNode("./*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='Message']")
        $stackNode = $failure.SelectSingleNode("./*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='StackTrace']")
        $details = @($messageNode.InnerText, $stackNode.InnerText) -join "`n"
        $details = ConvertTo-GitHubCommandValue $details
        Write-Output "::error title=Test failed: $name::$details"
    }
}

exit $testExitCode
