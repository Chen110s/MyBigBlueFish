using System;
using System.Collections.Generic;
using System.Linq;

namespace BigBlueFish.Windows;

// 键鼠陪伴: the 大肥鱼 of KuroCat (MIT, Copyright (c) 2026 KuroCat Contributors, github.com/qianyiai/KuroCat —
// dafeiyuRenderer.ts and the 大肥鱼 parts of catEngine.ts) ported to C#. She sits behind a small mechanical keyboard
// and a mouse: her eyes follow the pointer and the keys you press, a hand presses each key you press, the left hand
// holds the mouse while you use it, the whale tail slaps Enter after a burst (or any key when both hands are busy),
// her cheeks blush with the typing speed, and in a password field she closes her eyes, covers them with her hands
// and the keycaps are hidden. Only key codes are seen (no text), nothing is stored or sent.
//
// Logical scene units as on KuroCat: 420 x 440 with the 65% keyboard, 540 x 456 with the 108-key board.
//
// KuroCat's licence, for the code ported into CompanionKeyMouseScene.cs, CompanionKeyMouseFish.cs,
// CompanionKeyMouseDraw.cs and CompanionKeyMouseLayout.cs:
//
//   MIT License
//
//   Copyright (c) 2026 KuroCat Contributors
//
//   Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
//   documentation files (the "Software"), to deal in the Software without restriction, including without limitation
//   the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
//   to permit persons to whom the Software is furnished to do so, subject to the following conditions:
//
//   The above copyright notice and this permission notice shall be included in all copies or substantial portions
//   of the Software.
//
//   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
//   THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
//   CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
//   IN THE SOFTWARE.
internal sealed partial class CompanionKeyMouseScene
{
    // ---------------------------------------------------------------- layout
    public CompanionKeyboardLayout Board { get; private set; } = CompanionKeyboardLayout.Compact;
    public double LogicalW { get; private set; }
    public double LogicalH { get; private set; }
    private double kbCenterX, kbCenterY, kbWidth, kbHeight, kbTilt, mouseBaseX, mouseBaseY;
    private bool Is108 => Board.Full;
    private double CharX => Is108 ? 268 : 192;
    public bool ShowBubbles = true;
    public bool UseTail = true;

    public CompanionKeyMouseScene() => SetBoard("65");

    public void SetBoard(string id)
    {
        var next = CompanionKeyboardLayout.For(id);
        bool changed = next != Board || LogicalW == 0;
        Board = next;
        if (Is108)
        {
            LogicalW = 540; LogicalH = 456;
            kbCenterX = 290; kbCenterY = 356; kbWidth = 398; kbHeight = 96; kbTilt = 2.4 * Math.PI / 180;
            mouseBaseX = 54; mouseBaseY = 332;
        }
        else
        {
            LogicalW = 420; LogicalH = 440;
            kbCenterX = 234; kbCenterY = 353; kbWidth = 266; kbHeight = 74; kbTilt = 3.4 * Math.PI / 180;
            mouseBaseX = 72; mouseBaseY = 322;
        }
        if (!changed) return;
        keyPress.Clear(); keyHeld.Clear(); bubbles.Clear();
        leftHand.Held.Clear(); rightHand.Held.Clear(); tail.Held.Clear();
        keyboardCache = null;
    }

    // Where the drawn sprite lies (logical units), so the host can lay her over the pet's own body.
    public (double Left, double Top, double Width, double Height) SpriteRect
    {
        get
        {
            double drawW = Is108 ? 268 : 262, drawH = drawW * 278 / 224, baseY = Is108 ? 366 : 364;
            return (CharX - drawW / 2, baseY - drawH, drawW, drawH);
        }
    }

    // The lowest point anything is drawn at (the keyboard's shadow).
    public double ContentBottom => kbCenterY + kbHeight / 2 + 8;
    public double ContentLeft => mouseBaseX - 26;

    // ---------------------------------------------------------------- input state
    private readonly Random random = new();
    private readonly Dictionary<string, double> keyPress = new();   // key id -> 0..1 (pressed down)
    private readonly Dictionary<string, int> keyHeld = new();       // key id -> keys held that light it
    private readonly HashSet<int> vksDown = new();
    private double lastAnyKeyAt = -10000;
    private readonly List<double> recentKeyTimes = new();
    private double typingEnergy, kbBounce;
    private bool mouseLeftDown, mouseRightDown, mouseMiddleDown;
    private double mouseLeftPress, mouseRightPress, mouseMiddlePress;
    private double mouseOffsetX, mouseOffsetY, mouseTargetOffsetX, mouseTargetOffsetY;
    private double lastMouseNX = .5, lastMouseNY = .5, lastMouseMoveAt = -10000, lastMouseClickAt = -10000;
    private (double X, double Y)? cursor; // the pointer in logical units (anywhere on the screen)
    private double petEnergy;
    public bool Privacy { get; private set; }
    private bool keysHidden; // not yet known whether the caret is in a password field: keys are not shown
    private double privacyClose, privacyTypingAt = -10000;

    private sealed class Bubble
    {
        public string Label = "";
        public bool IsSpace;
        public double X, Y, TargetX, TargetY, Rot, CreatedAt, Lifetime;
    }
    private readonly List<Bubble> bubbles = new();
    private int nextBubbleId;

    private sealed class Badge
    {
        public string Label = "";
        public KmColor Color;
        public double X, Y, CreatedAt, Lifetime;
    }
    private readonly List<Badge> badges = new();

    public double LastInputAt => Math.Max(lastAnyKeyAt, Math.Max(lastMouseMoveAt, lastMouseClickAt));

    // ---------------------------------------------------------------- input
    public void KeyDown(int vk, double now)
    {
        if (!vksDown.Add(vk)) return; // still held: no auto-repeat
        if (Privacy || keysHidden)
        {
            privacyTypingAt = now;
            bodyBob = Math.Min(.65, bodyBob + .42);
            return;
        }
        var spec = Board.Find(vk);
        if (spec == null) return;
        keyPress[spec.Id] = 1;
        keyHeld[spec.Id] = (keyHeld.TryGetValue(spec.Id, out int held) ? held : 0) + 1;

        double prevAnyKeyAt = lastAnyKeyAt;
        lastAnyKeyAt = now;
        recentKeyTimes.Add(now);
        if (recentKeyTimes.Count > 26) recentKeyTimes.RemoveAt(0);
        kbBounce = Math.Min(1, kbBounce + .45);

        var raw = KeyWorldCenter(spec);
        bool mouseBusy = mouseLeftDown || mouseRightDown || now - lastMouseMoveAt < 900;
        bool useTail = UseTail && ShouldUseTail(spec, mouseBusy, prevAnyKeyAt, now);
        var pt = useTail ? TailTargetPoint(spec) : raw;
        FishKeyDown(vk, pt, mouseBusy, useTail, now);
        if (ShowBubbles && spec.Bubble.Length > 0) SpawnBubble(spec.Bubble, spec.Id == "space", pt.X, now);
    }

    public void KeyUp(int vk, double now)
    {
        if (!vksDown.Remove(vk)) return;
        leftHand.Held.Remove(vk);
        rightHand.Held.Remove(vk);
        if (tail.Held.Remove(vk) && tail.Held.Count == 0) tail.ReleaseAt = now;
        var spec = Board.Find(vk);
        if (spec != null && keyHeld.TryGetValue(spec.Id, out int held))
        {
            if (held <= 1) keyHeld.Remove(spec.Id); else keyHeld[spec.Id] = held - 1;
        }
    }

    // The pointer: nx, ny = 0..1 across its monitor; at = the pointer in logical units.
    public void MouseMove(double nx, double ny, (double X, double Y)? at, double now)
    {
        cursor = at;
        double moved = Km.Hypot(nx - lastMouseNX, ny - lastMouseNY);
        if (moved < .0028) return; // sensor jitter
        bool typingNow = now - lastAnyKeyAt < 500 || vksDown.Count > 0;
        lastMouseNX = nx; lastMouseNY = ny;
        if (typingNow && moved < .015) return; // a desk bump while typing
        mouseTargetOffsetX = (nx - .5) * 14;
        mouseTargetOffsetY = (ny - .5) * 11;
        lastMouseMoveAt = now;
    }

    public void MouseButton(int button, bool down, double now) // 0 left, 1 right, 2 middle
    {
        bool wasDown = button == 0 ? mouseLeftDown : button == 1 ? mouseRightDown : mouseMiddleDown;
        if (wasDown == down) return;
        if (button == 0) mouseLeftDown = down; else if (button == 1) mouseRightDown = down; else mouseMiddleDown = down;
        if (!down) return;
        if (button == 0) mouseLeftPress = 1; else if (button == 1) mouseRightPress = 1; else mouseMiddlePress = 1;
        SpawnBadge(button, now);
        lastMouseMoveAt = lastMouseClickAt = now;
        leftHand.LastAt = -10000; // the left hand goes back to the mouse
        fishMouseClickAt = now;
    }

    // Stroked on the head: happy face and cheering hands for a couple of seconds.
    public void Pet() => petEnergy = Math.Max(petEnergy, 1);

    public void SetPrivacy(bool on, bool pending)
    {
        keysHidden = pending;
        if (on == Privacy) return;
        Privacy = on;
        if (!on) return;
        leftHand.Held.Clear(); rightHand.Held.Clear(); tail.Held.Clear();
        ripples.Clear(); bubbles.Clear();
        foreach (var id in keyPress.Keys.ToList()) keyPress[id] = 0;
        keyHeld.Clear();
    }

    // Lets go of everything (the mode was switched off, or the keys could not be read for a while).
    public void ReleaseAll(double now)
    {
        foreach (int vk in vksDown.ToList()) KeyUp(vk, now);
        mouseLeftDown = mouseRightDown = mouseMiddleDown = false;
    }

    // ---------------------------------------------------------------- keyboard geometry
    private (double X, double Y) ProjectKeyboardPoint(double uCol, double rowPos)
    {
        // Cat-facing board (KuroCat's default): the space bar is on her side, the legends upside down for us.
        double screenU = Board.TotalUnits - uCol, screenRow = Board.TotalRows - rowPos;
        double innerW = kbWidth - 14, innerH = kbHeight - 12;
        double localX = -innerW / 2 + screenU / Board.TotalUnits * innerW;
        double localY = -innerH / 2 + screenRow / Board.TotalRows * innerH;
        double depth = .94 + screenRow / Board.TotalRows * .09;
        double px = localX * depth, py = localY, c = Math.Cos(kbTilt), s = Math.Sin(kbTilt);
        return (kbCenterX + px * c - py * s, kbCenterY + px * s + py * c);
    }

    private (double X, double Y) KeyWorldCenter(CompanionKey spec)
    {
        double u = Km.Clamp(spec.UStart + spec.UWidth * .5, .65, Board.TotalUnits - .65);
        double rowPos = spec.Row <= .1 ? spec.Row + .68 : spec.Row + spec.RowSpan * .5;
        return ProjectKeyboardPoint(u, rowPos);
    }

    private (double X, double Y) ClampToKeyboard((double X, double Y) pt)
    {
        double dx = pt.X - kbCenterX, dy = pt.Y - kbCenterY, c = Math.Cos(-kbTilt), s = Math.Sin(-kbTilt);
        double lx = dx * c - dy * s, ly = dx * s + dy * c;
        double maxX = kbWidth * .5 - 15;
        double cx = Km.Clamp(lx, -maxX, maxX), cy = Km.Clamp(ly, -kbHeight * .5 + 8, kbHeight * .5 - 14);
        double rc = Math.Cos(kbTilt), rs = Math.Sin(kbTilt);
        return (kbCenterX + cx * rc - cy * rs, kbCenterY + cx * rs + cy * rc);
    }

    private (double X, double Y) TailTargetPoint(CompanionKey spec)
        => ClampToKeyboard(spec.Id == "space" ? ProjectKeyboardPoint(spec.UStart + spec.UWidth * .24, spec.Row + .5) : KeyWorldCenter(spec));

    // KuroCat's 'smart' tail: a third hand when both hands are busy, and a flourish on Enter after a burst of typing.
    private bool ShouldUseTail(CompanionKey spec, bool activelyMousing, double prevAnyKeyAt, double now)
    {
        int leftHeld = leftHand.Held.Count, rightHeld = rightHand.Held.Count;
        bool rightBusy = rightHeld > 0 || now - rightHand.LastAt < 135;
        if (activelyMousing && rightBusy || leftHeld > 0 && rightHeld > 0 || leftHeld + rightHeld >= 2) return true;
        return (spec.Id == "enter" || spec.Id == "np_ent")
            && (typingEnergy > .24 || now - prevAnyKeyAt < 450 && recentKeyTimes.Count >= 2);
    }

    private void SpawnBubble(string label, bool isSpace, double keyX, double now)
    {
        if (bubbles.Count >= 6) bubbles.RemoveAt(0);
        double minX = Is108 ? 145 : 125, maxX = LogicalW - 105;
        double baseX = Km.Clamp(keyX * .45 + kbCenterX * .55, minX, maxX);
        double staggerY = 258 + (nextBubbleId % 3 - 1) * 10 + (random.NextDouble() - .5) * 6;
        nextBubbleId++;
        bubbles.Add(new Bubble { Label = label, IsSpace = isSpace, X = baseX, TargetX = baseX, Y = 292, TargetY = staggerY,
            Rot = (random.NextDouble() - .5) * 4.5 * Math.PI / 180, CreatedAt = now, Lifetime = 940 });
        LayoutBubbles();
    }

    private void LayoutBubbles()
    {
        if (bubbles.Count == 0) return;
        var widths = bubbles.Select(b => b.IsSpace ? 40 : Math.Max(25, b.Label.Length * 7.5 + 14)).ToList();
        const double gap = 3.5;
        double total = widths.Sum() + gap * (bubbles.Count - 1);
        double anchor = kbCenterX + 12, left = Is108 ? 140 : 126, right = LogicalW - 82;
        double x = Math.Max(left, Math.Min(right - total, anchor - total * .5));
        for (int i = 0; i < bubbles.Count; i++) { bubbles[i].TargetX = x + widths[i] * .5; x += widths[i] + gap; }
    }

    private static readonly KmColor MouseLeftColor = KmColor.Parse("#5cc8ff"), MouseRightColor = KmColor.Parse("#ff7b3b"),
        MouseMiddleColor = KmColor.Parse("#2563eb");

    private void SpawnBadge(int button, double now)
    {
        if (!ShowBubbles || Privacy) return;
        double mx = mouseBaseX + mouseOffsetX, my = mouseBaseY + mouseOffsetY;
        if (badges.Count >= 2) badges.RemoveAt(0);
        badges.Add(new Badge
        {
            Label = button == 0 ? "LMB" : button == 1 ? "RMB" : "MMB",
            Color = button == 0 ? MouseLeftColor : button == 1 ? MouseRightColor : MouseMiddleColor,
            X = Math.Max(24, mx + (button == 0 ? 10 : button == 1 ? -10 : 0)), Y = my - 30, CreatedAt = now, Lifetime = 560,
        });
    }

    // ---------------------------------------------------------------- update
    public void Update(double dt, double now)
    {
        double mLerp = 1 - Math.Exp(-28 * dt);
        mouseOffsetX += (mouseTargetOffsetX - mouseOffsetX) * mLerp;
        mouseOffsetY += (mouseTargetOffsetY - mouseOffsetY) * mLerp;
        if (!mouseLeftDown) mouseLeftPress = Math.Max(0, mouseLeftPress - dt * 5.8);
        if (!mouseRightDown) mouseRightPress = Math.Max(0, mouseRightPress - dt * 5.8);
        if (!mouseMiddleDown) mouseMiddlePress = Math.Max(0, mouseMiddlePress - dt * 5.8);
        badges.RemoveAll(b => now - b.CreatedAt >= b.Lifetime);

        while (recentKeyTimes.Count > 0 && now - recentKeyTimes[0] > 950) recentKeyTimes.RemoveAt(0);
        typingEnergy += (Math.Min(1, recentKeyTimes.Count / 7.0) - typingEnergy) * (1 - Math.Exp(-9 * dt));
        kbBounce = Math.Max(0, kbBounce - dt * 15);
        petEnergy = Math.Max(0, petEnergy - dt * .4);
        privacyClose += ((Privacy ? 1 : 0) - privacyClose) * (1 - Math.Exp(-14 * dt));

        foreach (var id in keyPress.Keys.ToList())
            if (!keyHeld.ContainsKey(id) && keyPress[id] > 0) keyPress[id] = Math.Max(0, keyPress[id] - dt * 5);

        int before = bubbles.Count;
        bubbles.RemoveAll(b => now - b.CreatedAt >= b.Lifetime);
        if (bubbles.Count != before) LayoutBubbles();
        foreach (var b in bubbles)
        {
            double age = (now - b.CreatedAt) / b.Lifetime;
            b.X += (b.TargetX - b.X) * (1 - Math.Exp(-18 * dt));
            b.Y += (b.TargetY - age * 10 - b.Y) * (1 - Math.Exp(-16 * dt));
        }
        FishUpdate(dt, now);
    }

    // Still moving (for the frame rate): anything that animates by itself beyond breathing and blinking.
    public bool Busy(double now) => now - LastInputAt < 2500 || bubbles.Count > 0 || badges.Count > 0 || ripples.Count > 0
        || particles.Count > 0 || petEnergy > 0 || Math.Abs(privacyClose - (Privacy ? 1 : 0)) > .01 || tail.Phase > 0;
}
