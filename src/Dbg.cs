using RedLoader;

namespace MasonLine;

/// <summary>Diagnostic logging gate. The wiring/patch internals were invaluable during development
/// but are noise for players; flip <see cref="Verbose"/> to true when hunting a bug report.
/// User-facing lines (init, kit economy, warnings) stay on RLog directly.</summary>
internal static class Dbg
{
    internal static readonly bool Verbose = false;  // inventory-crash SOLVED 2026-07-24 (listener-guard); silenced for release

    internal static void Msg(string m) { if (Verbose) RLog.Msg(m); }
    internal static void Msg(System.ConsoleColor c, string m) { if (Verbose) RLog.Msg(c, m); }
}
