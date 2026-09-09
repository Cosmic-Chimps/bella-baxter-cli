namespace BellaCli.Infrastructure;

/// <summary>
/// Whether there is an operator on the other end of this process who can answer a prompt.
///
/// <para>Pilot F8: this was written inline as
/// <c>Console.IsOutputRedirected || output is JsonOutputWriter</c> in thirty-odd places. Both
/// halves are real, but neither is the question: interactivity is a property of <b>stdin</b>. Run
/// from Ansible, cron, a CI step or <c>ssh host bella …</c>, stdout is often a terminal while stdin
/// is closed — so the CLI decided it could prompt, called
/// <c>AnsiConsole.Prompt</c>/<c>PromptAsync</c>, and either hung until the job timed out or threw.
/// <c>LoginCommand</c> already got this right with <see cref="Console.IsInputRedirected"/>.</para>
///
/// <para>This is a strict superset of the old condition, deliberately: a redirected stdout still
/// counts as non-interactive even when stdin is a terminal, so a prompt can never pollute a pipe
/// that a caller is parsing.</para>
/// </summary>
public static class Interactivity
{
    /// <summary>
    /// True only when stdin can carry an answer, stdout is not being captured, and the caller has
    /// not asked for machine-readable output.
    /// </summary>
    public static bool IsInteractive(IOutputWriter output) =>
        !Console.IsInputRedirected
        && !Console.IsOutputRedirected
        && output is not JsonOutputWriter;
}
