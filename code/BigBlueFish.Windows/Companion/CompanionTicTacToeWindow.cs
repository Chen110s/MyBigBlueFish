using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ttt = BigBlueFish.Windows.CompanionTicTacToe;

namespace BigBlueFish.Windows;

// 井字棋 in a small panel beside her. Rock-paper-scissors decides who starts; the owner plays ×, she plays ○ at the level
// picked on the panel (kept in the settings), thinks a moment before each move, now and then comments (a line of yours she
// blocks, a fork of hers, a line of hers you block) and reacts to the result. While the panel is open she stays where she
// is and keeps her chatter to herself. Opened from the play panel, the context menu, the chat (「下井字棋」) or her invitation.
public partial class MainWindow
{
    private const int TttCost = 3;

    private sealed class TttGame
    {
        public readonly int[] Board = new int[9];
        public bool Started, Over, HerTurn;
        public int Moves;
        public DispatcherTimer? Timer;
    }

    private TttGame? tttGame;

    // 原地不动, or a game on the board: no wandering, crawling or running off.
    private bool StayStill => preferences.StayPut || tttGame != null;

    private void ShowTicTacToe()
    {
        if (tttGame != null && petPanel is { IsVisible: true } open) { open.Activate(); return; }
        if (!PlayReady(TttCost)) return;
        chatWindow?.Close(); // the board takes the chat box's place beside her
        var muted = Brushes.SlateGray;
        var blue = new SolidColorBrush(global::BigBlueFish.Windows.CompanionBubble.HairBlue);
        var ink = new SolidColorBrush(Color.FromRgb(38, 51, 76));
        var lineFill = new SolidColorBrush(Color.FromRgb(255, 236, 179));
        var body = new StackPanel();

        var score = new TextBlock { Foreground = muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        void Score()
        {
            bond.RollDay(DateTime.Now);
            score.Text = $"今天：鱼鱼赢 {bond.TttWins} · 主人赢 {bond.TttLosses} · 和棋 {bond.TttDraws}";
            score.ToolTip = $"一共：鱼鱼赢 {bond.TttWinsAll} · 主人赢 {bond.TttLossesAll} · 和棋 {bond.TttDrawsAll}";
        }
        Score();
        body.Children.Add(score);

        var levelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        levelRow.Children.Add(new TextBlock { Text = "难度", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var level = new ComboBox { MinWidth = 100, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "让着你：常常走漏 · 认真：偶尔失误，能赢她 · 绝不输：最多和棋（换了马上生效）" };
        System.Windows.Automation.AutomationProperties.SetName(level, "井字棋难度");
        foreach (var name in Ttt.Levels) level.Items.Add(name);
        level.SelectedIndex = preferences.TicTacToeLevel;
        level.SelectionChanged += (_, _) => { if (level.SelectedIndex >= 0) { preferences.TicTacToeLevel = level.SelectedIndex; SaveCompanion(); } };
        levelRow.Children.Add(level);
        body.Children.Add(levelRow);

        var status = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
        body.Children.Add(status);
        var rps = new WrapPanel();
        body.Children.Add(rps);

        var board = new UniformGrid { Rows = 3, Columns = 3, Width = 222, Height = 222, Margin = new Thickness(0, 6, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Center };
        var cells = new Button[9];
        body.Children.Add(board);

        Window? panel = null;
        TttGame game = null!;
        var end = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        var again = BigBlueFish.Deployment.ModernUI.Button("再来一局", () => NewRound(), true);
        var quit = BigBlueFish.Deployment.ModernUI.Button("不玩了", () => panel?.Close());
        again.Margin = new Thickness(0, 0, 8, 0);
        quit.Margin = new Thickness(0);
        end.Children.Add(again);
        end.Children.Add(quit);
        body.Children.Add(end);
        body.Children.Add(new TextBlock { Text = "主人是 ×，鱼鱼是 ○。也可以按数字键：1–9 从左上数起，小键盘按位置。", Foreground = muted, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        UIElement Mark(int who) => who == Ttt.Owner
            ? new System.Windows.Shapes.Path { Data = Geometry.Parse("M 7,7 L 33,33 M 33,7 L 7,33"), Stroke = ink, StrokeThickness = 5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 40, Height = 40 }
            : new System.Windows.Shapes.Ellipse { Stroke = blue, StrokeThickness = 5, Width = 32, Height = 32 };

        void Render(int[]? line = null)
        {
            for (int i = 0; i < 9; i++)
            {
                int who = game.Board[i];
                bool open = game.Started && !game.Over && !game.HerTurn && who == Ttt.Empty;
                cells[i].Content = who == Ttt.Empty ? null : Mark(who);
                cells[i].Cursor = open ? Cursors.Hand : Cursors.Arrow;
                cells[i].Background = line != null && line.Contains(i) ? lineFill : Brushes.White;
                cells[i].Opacity = game.Started ? 1 : .5;
            }
        }

        void NewRound(bool first = false)
        {
            if (!first && !PlayReady(TttCost)) { status.Text = "鱼鱼累了，先休息一下吧～"; return; }
            game?.Timer?.Stop();
            game = tttGame = new TttGame();
            status.Text = "先猜拳，赢的先下！";
            rps.Visibility = Visibility.Visible;
            end.Visibility = Visibility.Collapsed;
            Render();
        }

        void Rps(int mine)
        {
            if (game.Started) return;
            int hers = companionRandom.Next(3);
            if (hers == mine)
            {
                status.Text = $"都出了{Hands[mine]}！再来～";
                Say($"都出了{Hands[mine]}！心有灵犀～再来！");
                return;
            }
            bool herFirst = (hers + 1) % 3 == mine; // 0 石头 beats 1 剪刀 beats 2 布 beats 0 石头
            game.Started = true;
            rps.Visibility = Visibility.Collapsed;
            SpendPlay(TttCost, counted: false);
            PlayClip("下五子棋", "Ttt"); // she sits down at the board
            Say($"你出{Hands[mine]}，鱼鱼出{Hands[hers]}——" + (herFirst ? "鱼鱼先下！" : "主人先下～"));
            if (herFirst) HerTurnSoon();
            else { status.Text = "轮到主人（×）"; Render(); }
        }

        void HerTurnSoon()
        {
            game.HerTurn = true;
            Render();
            status.Text = "鱼鱼在想……";
            var current = game;
            var timer = game.Timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550 + companionRandom.Next(750)) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (current == game && tttGame == current && !current.Over) HerMove();
            };
            timer.Start();
        }

        void HerMove()
        {
            var cellsNow = game.Board;
            var threats = Ttt.Wins(cellsNow, Ttt.Owner);
            int cell = Ttt.Choose(cellsNow, preferences.TicTacToeLevel, companionRandom);
            game.HerTurn = false;
            if (cell < 0) { Finish(); return; }
            cellsNow[cell] = Ttt.Her;
            game.Moves++;
            if (Finish()) return;
            status.Text = "轮到主人（×）";
            Render();
            var ownerNext = Ttt.Wins(cellsNow, Ttt.Owner);
            string? remark = null;
            if (threats.Count >= 2 && ownerNext.Count > 0) remark = Pick(new[] { "糟糕……两边都要连上了，堵不过来！", "呜，被主人做了个套……" });
            else if (ownerNext.Count == 0 && Ttt.Wins(cellsNow, Ttt.Her).Count >= 2) remark = Pick(new[] { "嘿嘿，鱼鱼有两条路可以赢啦～", "看好咯，这下主人堵不过来了吧！" });
            else if (threats.Contains(cell)) remark = Pick(new[] { "想连成一条线？被鱼鱼堵住啦～", "嘿！这里鱼鱼早就看到了！" });
            else if (game.Moves == 1 && cell == 4) remark = "鱼鱼先占中间！";
            if (remark != null && companionRandom.NextDouble() < .75) Say(remark);
        }

        void OwnerMove(int cell)
        {
            if (!game.Started || game.Over || game.HerTurn || game.Board[cell] != Ttt.Empty) return;
            var hers = Ttt.Wins(game.Board, Ttt.Her);
            game.Board[cell] = Ttt.Owner;
            game.Moves++;
            NoteInteraction();
            if (Finish()) return;
            if (hers.Contains(cell) && companionRandom.NextDouble() < .6) Say(Pick(new[] { "啊！被主人看穿了……", "哼，堵得真准！" }));
            HerTurnSoon();
        }

        // The game is over when someone has a line or the board is full: count it and let her react.
        bool Finish()
        {
            var (winner, line) = Ttt.Outcome(game.Board);
            if (winner == Ttt.Empty && !Ttt.Full(game.Board)) return false;
            game.Over = true;
            game.HerTurn = false;
            Render(line);
            bond.RollDay(DateTime.Now);
            bool firstToday = bond.TttWins + bond.TttLosses + bond.TttDraws == 0;
            string said, clip, subject;
            if (winner == Ttt.Her)
            {
                bond.TttWins++; bond.TttWinsAll++;
                said = Pick(new[] { "耶！鱼鱼连成一条线啦～", "嘿嘿，鱼鱼赢啦！主人要不要再来一局？", "鱼鱼的井字棋可是练过的！" });
                clip = "点击回应-开心跃动"; subject = "井字棋输了"; status.Text = "鱼鱼赢了！";
            }
            else if (winner == Ttt.Owner)
            {
                bond.TttLosses++; bond.TttLossesAll++;
                said = Pick(new[] { "呜……被主人赢了！不服，再来！", "主人好厉害……鱼鱼下次一定赢回来！", "哼！这局不算，鱼鱼刚刚走神了！" });
                clip = companionRandom.NextDouble() < .5 ? "点击回应-傲娇生气" : "玩游戏气急败坏"; subject = "井字棋赢了"; status.Text = "主人赢了！";
            }
            else
            {
                bond.TttDraws++; bond.TttDrawsAll++;
                said = preferences.TicTacToeLevel == 2 ? Pick(new[] { "和棋～鱼鱼认真起来可是不会输的哦！", "平局！想赢鱼鱼可没那么容易～" })
                    : Pick(new[] { "和棋！势均力敌～", "平局！主人和鱼鱼一样聪明～" });
                clip = "东张西望"; subject = "井字棋和棋"; status.Text = "和棋！";
            }
            if (firstToday) { bond.Plays++; AddAffection(1); }
            NoteInteraction();
            SaveBondSoon();
            Score();
            end.Visibility = Visibility.Visible;
            Say(said, clip);
            PetEvent("play", subject, game.Moves, said: said, ownReaction: true);
            return true;
        }

        for (int i = 0; i < Hands.Length; i++)
        {
            int mine = i;
            var button = BigBlueFish.Deployment.ModernUI.Button(Hands[i], () => Rps(mine), i == 0);
            button.Margin = new Thickness(0, 0, 8, 0);
            button.MinWidth = 64;
            rps.Children.Add(button);
        }
        for (int i = 0; i < 9; i++)
        {
            int cell = i;
            var button = new Button { Margin = new Thickness(3), Padding = new Thickness(0), MinHeight = 0, MinWidth = 0,
                Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(200, 210, 232)) };
            System.Windows.Automation.AutomationProperties.SetName(button, $"第 {cell / 3 + 1} 行第 {cell % 3 + 1} 列");
            button.Click += (_, _) => OwnerMove(cell);
            cells[i] = button;
            board.Children.Add(button);
        }

        NewRound(true);
        ShowPetPanel("和鱼鱼下井字棋", body, 280, false);
        panel = petPanel;
        if (panel == null) { tttGame = null; return; }
        panel.PreviewKeyDown += (_, e) =>
        {
            if (level.IsKeyboardFocusWithin) return;
            int cell = e.Key switch
            {
                >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
                Key.NumPad7 => 0, Key.NumPad8 => 1, Key.NumPad9 => 2,
                Key.NumPad4 => 3, Key.NumPad5 => 4, Key.NumPad6 => 5,
                Key.NumPad1 => 6, Key.NumPad2 => 7, Key.NumPad3 => 8,
                _ => -1
            };
            if (cell < 0 || !game.Started) return;
            e.Handled = true;
            OwnerMove(cell);
        };
        panel.Closed += (_, _) =>
        {
            game.Timer?.Stop();
            bool unfinished = game.Started && !game.Over && game.Moves > 0;
            if (tttGame == game) tttGame = null;
            if (unfinished && !companionClosed && !exitAnimation) Say("这盘先不下啦？好吧～下次再来！");
        };
    }

    // 聊天：「来下井字棋」「陪我玩井字棋吧」 opens the board; questions about the game (怎么下、规则…) go to the model as usual.
    private static readonly Regex TttAsk = new(@"井字棋|三子棋|圈圈叉叉|圈叉棋|[Oo][Xx]棋|tic.?tac.?toe", RegexOptions.IgnoreCase);
    private static readonly Regex TttNotNow = new(@"(不|别|没)(想|要|用|能)?(下|玩)|怎么|如何|规则|是什么|技巧|必胜|攻略|为什么|输入|打开|搜索|网站");

    private bool TryTicTacToeChat(string message)
    {
        if (message.Length > 40 || !TttAsk.IsMatch(message) || TttNotNow.IsMatch(message)) return false;
        if (Energy() < TttCost + 5) { ChatReply(message, "鱼鱼玩累了……歇一会儿再陪主人下好不好～", "哈欠连天", remember: false); return true; }
        ChatReply(message, Pick(new[] { "好呀！先猜拳，赢的先下～", "来！鱼鱼可不会手下留情哦～先猜拳！" }), remember: false);
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(ShowTicTacToe));
        return true;
    }
}
