# Copilot Instructions

## Project Guidelines
- In the ChessBot engine, hot-path search code (Searcher, MoveOrdering, MoveGenerator) should use fixed-size Move[] buffers with an out/parameter count instead of List<Move>, to keep move generation and ordering allocation-free. Public List<Move> APIs should be kept only as thin wrappers for external callers/tests.