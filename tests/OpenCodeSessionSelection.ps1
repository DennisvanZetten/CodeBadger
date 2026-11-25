# Exercise the actual Razor selection methods without starting OpenCode or MAUI.
$ErrorActionPreference = 'Stop'
$source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Components\Shared\RepositoryWorkspace.razor')
$start = $source.IndexOf('    private string GetPreferredOpenCodeSessionId()')
$end = $source.IndexOf('    private async Task SelectOpenCodeSessionAsync', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Session selection methods were not found.' }
$methods = $source.Substring($start, $end - $start)
$harness = @'
using System;
using System.Collections.Generic;
using System.Linq;
public sealed class SessionSelectionTest
{
    private static class CodeBadgerRoles
    {
        public const string Planner = "Planner", Worker = "Worker";
    }
    private enum TerminalMode { Plan, Build }
    private sealed class State
    {
        public string ActiveAgentRole = "";
        public string SelectedOpenCodeSessionId = "";
        public Dictionary<string, string> AgentSessionIds = new Dictionary<string, string>();
    }
    private sealed class Session
    {
        public Guid Id = Guid.NewGuid();
        public State State = new State();
        public TerminalMode Mode = TerminalMode.Plan;
    }
    private List<Session> _terminalSessions = new List<Session>();
    private State _terminalState = new State();
    private Guid _selectedTerminalSessionId;
    private Dictionary<string, string> OpenCodeSessions = new Dictionary<string, string>();
    private bool singleAgent;
    private string GetRoleSessionKey(Session session, string role) => singleAgent ? "SingleAgent" : role;
    private static bool IsOpenCodeSessionKey(string key) => key == "SingleAgent" ||
        (!key.StartsWith("Model:", StringComparison.Ordinal) && !key.StartsWith("ModelSelector:", StringComparison.Ordinal));
    private void Expect(string expected)
    {
        SynchronizeOpenCodeSessionSelection();
        if (_terminalState.SelectedOpenCodeSessionId != expected)
            throw new Exception("Expected " + expected + ", got " + _terminalState.SelectedOpenCodeSessionId);
    }
    public static void Run()
    {
        var test = new SessionSelectionTest();
        var first = new Session();
        var second = new Session();
        test._terminalSessions.Add(first);
        test._terminalSessions.Add(second);
        test._selectedTerminalSessionId = first.Id;
        test.Expect("");
        first.State.AgentSessionIds["Planner"] = "ses_plan";
        test.OpenCodeSessions["ses_plan"] = "Plan";
        test.Expect("ses_plan"); // Newly created session replaces the empty selection.
        first.State.AgentSessionIds["Worker"] = "ses_build";
        test.OpenCodeSessions["ses_build"] = "Build";
        first.State.ActiveAgentRole = "Worker";
        test.Expect("ses_build"); // Role transition follows the worker.
        test._selectedTerminalSessionId = second.Id;
        test.Expect(""); // A new tab must not show another tab's session.
        second.State.AgentSessionIds["SingleAgent"] = "ses_second";
        test.OpenCodeSessions["ses_second"] = "Second";
        test.singleAgent = true;
        test.Expect("ses_second");
        test._selectedTerminalSessionId = first.Id;
        test.singleAgent = false;
        test.Expect("ses_build"); // Switching tabs restores its active role.
        first.State.AgentSessionIds["Worker"] = "ses_rotated";
        test.OpenCodeSessions["ses_rotated"] = "Rotated";
        test.Expect("ses_rotated"); // Handoff rotation follows the replacement.
        first.State.ActiveAgentRole = "";
        first.Mode = TerminalMode.Plan;
        test.Expect("ses_plan"); // Mode fallback when no role has run yet.
        first.State.AgentSessionIds.Clear();
        test.Expect(""); // Clearing a tab never falls back to another tab.
        test._terminalSessions.Clear();
        test.Expect("");
    }
}
'@
$harness = $harness.Replace('    private void Expect(string expected)', $methods + '    private void Expect(string expected)')
Add-Type -TypeDefinition $harness
[SessionSelectionTest]::Run()
Write-Output 'OpenCode session selection tests passed.'
