namespace ChessBot.Uci;

using ChessBot.Engine;

/// <summary>
/// Entry point for the UCI front end: wires the process's stdin/stdout to a
/// <see cref="UciSession"/>.
///
/// Exists so the engine can be run by any UCI host (cutechess-cli, Arena, lichess-bot) rather
/// than only by this repository's own match runner — which is what makes its strength
/// measurable against opponents the project does not control.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        // stdout is a protocol channel, not a log: a GUI that does not see "uciok" or
        // "bestmove" the moment it is written will time the engine out, so the writer is
        // unbuffered rather than flushed at exit.
        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };

        using var session = new UciSession(new ChessEngine(), output);
        session.Run(Console.In);

        return 0;
    }
}
