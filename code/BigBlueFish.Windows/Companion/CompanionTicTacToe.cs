using System;
using System.Collections.Generic;
using System.Linq;

namespace BigBlueFish.Windows;

// 井字棋 with her: the board (cells 0-8, row by row), who has won, and how she picks a move at three levels:
// 让着你 misses wins and blocks and often plays anywhere, 认真 never misses a win but now and then misses a block or
// plays a weaker move (a perfect player beats her about one game in four), 绝不输 plays perfectly (a draw at best).
internal static class CompanionTicTacToe
{
    internal const int Empty = 0, Owner = 1, Her = 2;
    internal static readonly string[] Levels = ["让着你", "认真", "绝不输"];
    internal static readonly int[][] Lines =
        [[0, 1, 2], [3, 4, 5], [6, 7, 8], [0, 3, 6], [1, 4, 7], [2, 5, 8], [0, 4, 8], [2, 4, 6]];

    internal static (int Winner, int[]? Line) Outcome(int[] board)
    {
        foreach (var line in Lines)
        {
            int a = board[line[0]];
            if (a != Empty && a == board[line[1]] && a == board[line[2]]) return (a, line);
        }
        return (Empty, null);
    }

    internal static bool Full(int[] board) => Array.IndexOf(board, Empty) < 0;
    internal static bool Over(int[] board) => Outcome(board).Winner != Empty || Full(board);
    internal static int Other(int who) => who == Owner ? Her : Owner;
    internal static List<int> EmptyCells(int[] board) => Enumerable.Range(0, 9).Where(i => board[i] == Empty).ToList();

    // Cells where `who` would complete a line with the next move.
    internal static List<int> Wins(int[] board, int who)
    {
        var cells = new List<int>();
        for (int i = 0; i < 9; i++)
        {
            if (board[i] != Empty) continue;
            board[i] = who;
            if (Outcome(board).Winner == who) cells.Add(i);
            board[i] = Empty;
        }
        return cells;
    }

    // Value for the side to move with perfect play: above 0 a win, below 0 a loss, 0 a draw; each move it takes makes a win
    // (or a loss) count one less, so a quick win beats a slow one and a loss is put off. Positions are remembered (3^9 at most).
    private static readonly Dictionary<int, int> Known = new();

    private static int Negamax(int[] board, int toMove)
    {
        if (Outcome(board).Winner != Empty) return -10; // the side that has just moved has won
        if (Full(board)) return 0;
        int key = toMove;
        foreach (int cell in board) key = key * 3 + cell;
        lock (Known) if (Known.TryGetValue(key, out int known)) return known;
        int best = int.MinValue;
        for (int i = 0; i < 9; i++)
        {
            if (board[i] != Empty) continue;
            board[i] = toMove;
            int score = -Negamax(board, Other(toMove));
            board[i] = Empty;
            if (score > best) best = score;
        }
        best = best > 0 ? best - 1 : best < 0 ? best + 1 : 0;
        lock (Known) Known[key] = best;
        return best;
    }

    // Every move that is as good as any with perfect play (she picks one of them at random, so games differ).
    internal static List<int> BestMoves(int[] board, int who)
    {
        var best = new List<int>();
        int top = int.MinValue;
        for (int i = 0; i < 9; i++)
        {
            if (board[i] != Empty) continue;
            board[i] = who;
            int score = -Negamax(board, Other(who));
            board[i] = Empty;
            if (score > top) { top = score; best.Clear(); }
            if (score == top) best.Add(i);
        }
        return best;
    }

    // Her move at a level (0 让着你 · 1 认真 · 2 绝不输); -1 when the board is full.
    internal static int Choose(int[] board, int level, Random random)
    {
        var empty = EmptyCells(board);
        if (empty.Count == 0) return -1;
        int Pick(List<int> cells) => cells[random.Next(cells.Count)];
        var win = Wins(board, Her);
        var block = Wins(board, Owner);
        switch (Math.Clamp(level, 0, 2))
        {
            case 0:
                if (win.Count > 0 && random.NextDouble() < .5) return Pick(win);
                if (block.Count > 0 && random.NextDouble() < .35) return Pick(block);
                return Pick(empty);
            case 1:
                if (win.Count > 0) return Pick(win);
                if (block.Count > 0 && random.NextDouble() < .85) return Pick(block);
                return random.NextDouble() < .6 ? Pick(BestMoves(board, Her)) : Pick(empty);
            default:
                return Pick(BestMoves(board, Her));
        }
    }
}
