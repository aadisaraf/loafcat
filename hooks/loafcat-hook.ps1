# loafcat — the hook Claude Code runs on Windows, so the cat can react to what it
# is doing. The PowerShell counterpart of loafcat-hook.sh; same contract, same five
# fields, same endpoint.
#
# THE CONTRACT: this script must never slow down, block or fail a Claude Code
# session. It is the only part of loafcat that runs inside somebody else's process
# tree, and a hook that misbehaves shows up to the user as "Claude got slow", which
# they will never trace back to a desktop pet.
#
# So, in order of importance:
#
#   * exit 0, always, whatever happened. Exit 2 blocks Claude outright; other
#     non-zero codes surface as errors the user has to read. Neither is ever worth
#     it for a cosmetic animation. The trap at the bottom makes that true even for a
#     failure this script did not anticipate.
#   * no unbounded wait, anywhere. curl carries both a connect and a total timeout,
#     and both are sub-second.
#   * silent no-op when loafcat is not running. The app deletes the handshake file
#     when it quits, so there is nothing to read, and we are gone before we ever
#     open a socket.
#   * nothing on stdout. A hook's stdout is Claude's to interpret.
#
# PRIVACY: hook payloads carry the user's prompt text and the full command line of
# every tool call. Five scalar fields are extracted and the rest is dropped on the
# floor here, before anything leaves the process — the app never sees it and there is
# nothing to leak from a log.
#
# Usage:  loafcat-hook.ps1 <EventName> [state]      hook JSON on stdin

param(
    [string]$EventName = "",
    [string]$State = ""
)

# Never let a terminating error become a non-zero exit. The app registers this with
# "async": true and a short timeout, but a hook that returns 1 is still noise in
# somebody's session for no benefit.
$ErrorActionPreference = "SilentlyContinue"
$ProgressPreference = "SilentlyContinue"

trap { exit 0 }

try {
    $endpointPath = $env:LOAFCAT_ENDPOINT
    if (-not $endpointPath) {
        $endpointPath = Join-Path $env:USERPROFILE ".loafcat\endpoint.json"
    }

    # Not running, never installed, or mid-restart. Nothing to do, and nothing to
    # report — this is the expected state most of the time.
    if (-not (Test-Path -LiteralPath $endpointPath)) { exit 0 }

    $handshake = Get-Content -LiteralPath $endpointPath -Raw | ConvertFrom-Json
    $port = $handshake.port
    $token = $handshake.token
    if (-not $port -or -not $token) { exit 0 }

    # --- stdin ---------------------------------------------------------------
    # Read only when something is actually piped in. `[Console]::In.ReadToEnd()`
    # blocks forever on an interactive console, which is exactly the unbounded wait
    # this script may not have.
    $payload = ""
    if ([Console]::IsInputRedirected) {
        $payload = [Console]::In.ReadToEnd()
        if ($payload.Length -gt 65536) { $payload = $payload.Substring(0, 65536) }
    }

    $parsed = $null
    if ($payload) { $parsed = $payload | ConvertFrom-Json }

    # Reduced to a character set that cannot break out of a JSON string, so the body
    # below is well-formed by construction with no escaping to get wrong. A path with
    # spaces loses them; cwd is only ever displayed, never used as an identity, so
    # that costs nothing.
    function Format-Safe([string]$value) {
        if (-not $value) { return "" }
        $clean = ($value -replace '[^A-Za-z0-9._:/@+\-]', '')
        if ($clean.Length -gt 200) { $clean = $clean.Substring(0, 200) }
        return $clean
    }

    if (-not $EventName -and $parsed) { $EventName = $parsed.hook_event_name }
    $EventName = Format-Safe $EventName
    if (-not $EventName) { exit 0 }

    $state = Format-Safe $State
    $session = Format-Safe $(if ($parsed) { $parsed.session_id } else { "" })
    $cwd = Format-Safe $(if ($parsed) { $parsed.cwd } else { "" })

    $agent = $env:CLAUDE_AGENT_ID
    if (-not $agent -and $parsed) { $agent = $parsed.agent_id }
    $agent = Format-Safe $agent
    if (-not $agent) { $agent = "main" }

    $body = "{""agentId"":""$agent"",""event"":""$EventName"",""state"":""$state""," +
            """sessionId"":""$session"",""cwd"":""$cwd""}"

    # curl.exe ships with Windows 10 1803 and later. Preferred over
    # Invoke-RestMethod because its timeouts are sub-second and separate for connect
    # and total; Invoke-RestMethod's -TimeoutSec is whole seconds and covers neither
    # case well.
    #
    # --noproxy: a configured proxy would otherwise send this — token and all — out
    # to a proxy host instead of to the loopback listener two inches away.
    # Content-Type is not decoration: the app requires application/json precisely
    # because it forces a CORS preflight that a hostile web page cannot satisfy.
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source --silent --output NUL `
            --noproxy '*' `
            --connect-timeout 0.2 --max-time 0.5 `
            --request POST `
            --header "Authorization: Bearer $token" `
            --header 'Content-Type: application/json' `
            --data-binary $body `
            "http://127.0.0.1:$port/agent-state" 2>$null
    } else {
        # Windows 10 before 1803. One second, and still fire-and-forget.
        Invoke-RestMethod -Method Post -TimeoutSec 1 `
            -Uri "http://127.0.0.1:$port/agent-state" `
            -Headers @{ Authorization = "Bearer $token" } `
            -ContentType 'application/json' `
            -Body $body | Out-Null
    }
} catch {
    # Deliberately swallowed. Whatever went wrong, it is not worth a line in the
    # user's session.
}

exit 0
