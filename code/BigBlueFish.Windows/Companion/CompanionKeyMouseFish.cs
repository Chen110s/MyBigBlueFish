using System;
using System.Collections.Generic;

namespace BigBlueFish.Windows;

// 键鼠陪伴: her body, eyes, hands and tail (KuroCat's DaFeiYuRenderer, MIT): springs for the lean and tilt, the gaze,
// the blush that follows the typing speed, the hands and the whale tail.
internal sealed partial class CompanionKeyMouseScene
{
    private sealed class Hand
    {
        public double X, Y, Tx, Ty, LastAt = -10000, Squash;
        public readonly HashSet<int> Held = new();
        public Hand(double x, double y) { X = Tx = x; Y = Ty = y; }
    }

    private sealed class TailState
    {
        public double RelX = 84, RelY = -36, ActiveWag, WagPhaseX, WagPhaseY, SlapAt = -10000, ReleaseAt = -10000;
        public double SlapTargetX = 340, SlapTargetY = 355, Squash, Phase;
        public readonly HashSet<int> Held = new();
    }

    private sealed class Ripple { public double X, Y, R, Life; }
    private sealed class Particle { public double X, Y, Vx, Vy, Life; public bool Heart; }

    private readonly Hand leftHand = new(155, 312), rightHand = new(235, 312);
    private readonly TailState tail = new();
    private readonly List<Ripple> ripples = new();
    private readonly List<Particle> particles = new();

    private double lastKeyGazeAt = -10000, lastKeyPtX = 214, lastKeyPtY = 353, fishMouseClickAt = -10000;
    private readonly List<double> keyTimes = new();
    private double typingHeat, blushLevel, moodChangedAt = -10000, nextBlinkAt = 3600, blinkUntil;
    private int moodTier;
    private double eyeGazeX, eyeGazeY;
    private double bodyShiftX, bodyShiftVel, bodyShiftTarget, bodyTilt, bodyTiltVel, bodyTiltTarget, bodyDiveY, bodyBob;
    private bool started;

    private bool Happy => !Privacy && petEnergy > .25;
    private bool MouseMode(double now) => !Privacy && !Happy && (mouseLeftDown || mouseRightDown || now - lastMouseMoveAt < 900);

    // The pressed mouse button, where her left hand rests on the mouse.
    private (double X, double Y) MouseButtonPoint
    {
        get
        {
            double mx = mouseBaseX + mouseOffsetX, my = mouseBaseY + mouseOffsetY;
            double lx = mouseLeftDown ? -6.5 : mouseRightDown ? 6.5 : 0, ly = -10.5;
            double ang = kbTilt + Math.PI, c = Math.Cos(ang), s = Math.Sin(ang);
            return (mx + lx * c - ly * s, my + lx * s + ly * c);
        }
    }

    private void FishKeyDown(int vk, (double X, double Y) pt, bool mouseBusy, bool useTail, double now)
    {
        lastKeyGazeAt = now;
        lastKeyPtX = pt.X; lastKeyPtY = pt.Y;
        keyTimes.Add(now);
        bodyBob = Math.Min(1.1, bodyBob + .72);
        double charX = CharX;
        double normX = Km.Clamp((pt.X - kbCenterX) / (kbWidth * .46), -1, 1);
        bool leftSide = pt.X < kbCenterX - 2;
        if (useTail)
        {
            // the whale tail slaps the key
            tail.SlapAt = now;
            tail.ReleaseAt = -10000;
            tail.Held.Add(vk);
            tail.SlapTargetX = pt.X; tail.SlapTargetY = pt.Y;
            tail.Squash = 1;
            if (!mouseBusy)
            {
                bodyTiltTarget = normX >= 0 ? -.075 : .055;
                bodyShiftTarget = normX >= 0 ? -7 : 8;
            }
            ripples.Add(new Ripple { X = pt.X, Y = pt.Y, R = 6, Life = 1 });
            ripples.Add(new Ripple { X = pt.X, Y = pt.Y, R = 2, Life = .85 });
            SpawnParticles(pt.X, pt.Y - 8, 3, true);
        }
        else if (mouseBusy)
        {
            // mouse and keyboard at once: the left hand stays on the mouse, the right one types
            double cur = charX + (Is108 ? -88 : -38);
            rightHand.Tx = Km.Clamp(pt.X, cur - 10, cur + 92);
            rightHand.Ty = Math.Min(kbCenterY + 6, pt.Y);
            rightHand.LastAt = now; rightHand.Squash = 1;
            rightHand.Held.Add(vk);
        }
        else
        {
            // both hands, with a little sway towards the key
            bodyTiltTarget = normX * .095;
            bodyShiftTarget = normX * (Is108 ? 22 : 13);
            var hand = leftSide ? leftHand : rightHand;
            hand.Tx = charX + bodyShiftTarget + Km.Clamp((pt.X - charX) * .78, -76, 76);
            hand.Ty = Math.Min(kbCenterY + 10, pt.Y);
            hand.LastAt = now; hand.Squash = 1;
            hand.Held.Add(vk);
        }
    }

    private void SpawnParticles(double x, double y, int count, bool onlyDrop)
    {
        for (int i = 0; i < count; i++)
            particles.Add(new Particle
            {
                X = x + (random.NextDouble() - .5) * (onlyDrop ? 28 : 48),
                Y = y + (random.NextDouble() - .5) * 16,
                Vx = (random.NextDouble() - .5) * (onlyDrop ? 2.2 : .8),
                Vy = -1.1 - random.NextDouble() * 1.2,
                Life = 1,
                Heart = !onlyDrop && random.NextDouble() > .45,
            });
    }

    // Where a point of the sprite (0..1 across, 0..1 down) is in the scene, with her lean and tilt.
    private sealed class Geom
    {
        public double CharCenterX, CharBaseY, DrawW, DrawH, PivotOff = 48, PivotY, Tilt;
        public (double X, double Y) SpritePoint(double nx, double ny)
        {
            double lx = (nx - .5) * DrawW, ly = ny * DrawH - DrawH + PivotOff, c = Math.Cos(Tilt), s = Math.Sin(Tilt);
            return (CharCenterX + lx * c - ly * s, PivotY + lx * s + ly * c);
        }
    }

    private Geom ComputeGeom(double now)
    {
        double baseCharY = Is108 ? 366 : 364;
        double breath = Math.Sin(now * .0034) * 2.2 + bodyBob * 3.8 + bodyDiveY;
        double drawW = Is108 ? 268 : 262;
        var g = new Geom { CharCenterX = CharX + bodyShiftX, CharBaseY = baseCharY + breath, DrawW = drawW, DrawH = drawW * 278 / 224, Tilt = bodyTilt };
        g.PivotY = g.CharBaseY - g.PivotOff;
        return g;
    }

    private void FishUpdate(double dt, double now)
    {
        if (!started) { started = true; nextBlinkAt = now + 3600 + random.NextDouble() * 2600; }
        bodyBob *= Math.Exp(-12 * dt);
        bool happy = Happy;
        bool typing = !Privacy && (now - lastAnyKeyAt < 550 || tail.Held.Count > 0 || leftHand.Held.Count > 0 || rightHand.Held.Count > 0);
        bool mouseMode = MouseMode(now);

        // 1) a calm 2.8 s typing-speed window and a blush mood that does not flicker
        while (keyTimes.Count > 0 && now - keyTimes[0] > 2800) keyTimes.RemoveAt(0);
        double cps = keyTimes.Count / 2.8;
        double heatTarget = Privacy ? 0 : Km.Clamp((cps - 2.4) / 5.4, 0, 1);
        typingHeat += (heatTarget - typingHeat) * (1 - Math.Exp((heatTarget > typingHeat ? -2.2 : -.65) * dt));
        double dwell = now - moodChangedAt;
        if (moodTier == 0) { if (typingHeat >= .40 && dwell >= 1400) { moodTier = 1; moodChangedAt = now; } }
        else if (moodTier == 1)
        {
            if (typingHeat >= .78 && dwell >= 1800) { moodTier = 2; moodChangedAt = now; }
            else if (typingHeat < .22 && dwell >= 2200) { moodTier = 0; moodChangedAt = now; }
        }
        else if (typingHeat < .55 && dwell >= 2200) { moodTier = 1; moodChangedAt = now; }
        double targetBlush = Math.Max(Math.Max(typingHeat * .92, happy ? .45 : 0), Math.Max(Privacy ? .46 : 0, moodTier == 1 ? .55 : moodTier == 2 ? .85 : 0));
        blushLevel += (targetBlush - blushLevel) * (1 - Math.Exp(-3.2 * dt));

        if (now >= nextBlinkAt) { blinkUntil = now + 135; nextBlinkAt = now + 4500 + random.NextDouble() * 2700; }
        if (happy && random.NextDouble() < .06) SpawnParticles(CharX + bodyShiftX, 120, 1, false);
        else if (!Privacy && moodTier == 2 && random.NextDouble() < .025) SpawnParticles(CharX + bodyShiftX, 124, 1, false);

        // 2) body springs
        if (Privacy)
        {
            bodyShiftTarget = 0;
            bodyTiltTarget = Math.Sin(now * .0042) * .032 + (now - privacyTypingAt < 240 ? Math.Sin(now * .04) * .024 : 0);
            bodyDiveY *= Math.Exp(-9 * dt);
        }
        else if (mouseMode)
        {
            bodyShiftTarget = (Is108 ? -125 : -38) + (lastMouseNX - .5) * 12;
            bodyTiltTarget = -.10;
            bodyDiveY += (8 - bodyDiveY) * (1 - Math.Exp(-10 * dt));
        }
        else if (!typing) { bodyShiftTarget = 0; bodyTiltTarget = 0; bodyDiveY *= Math.Exp(-9 * dt); }
        else bodyDiveY *= Math.Exp(-9 * dt);
        bodyShiftVel += ((bodyShiftTarget - bodyShiftX) * 160 - bodyShiftVel * 18) * dt;
        bodyShiftX += bodyShiftVel * dt;
        bodyTiltVel += ((bodyTiltTarget - bodyTilt) * 180 - bodyTiltVel * 17) * dt;
        bodyTilt += bodyTiltVel * dt;

        // 2a) eyes: the pointer, or the key just pressed, or the mouse while clicking
        double lastClick = Math.Max(fishMouseClickAt, lastMouseClickAt);
        bool activeClick = !Privacy && !happy && (mouseLeftDown || mouseRightDown || now - lastClick < 950);
        bool activeKeyGaze = !Privacy && !happy && (now - lastKeyGazeAt < 950 || leftHand.Held.Count > 0 || rightHand.Held.Count > 0 || tail.Held.Count > 0);
        (double X, double Y)? gaze = !Privacy && !happy ? cursor : null;
        if (activeKeyGaze && (!activeClick || lastKeyGazeAt > lastClick)) gaze = (lastKeyPtX, lastKeyPtY);
        else if (gaze == null && (activeClick || mouseMode)) gaze = MouseButtonPoint;
        double gazeTx = 0, gazeTy = 0;
        if (gaze is { } target)
        {
            var eye = ComputeGeom(now).SpritePoint(.49, .495);
            double wdx = target.X - eye.X, wdy = target.Y - eye.Y, c = Math.Cos(-bodyTilt), s = Math.Sin(-bodyTilt);
            gazeTx = Km.Clamp((wdx * c - wdy * s) / 21, -4.6, 4.6);
            gazeTy = Km.Clamp((wdx * s + wdy * c) / 29, -4.3, 4.3);
        }
        double gazeLerp = 1 - Math.Exp(-(gaze != null ? 24 : 8.5) * dt);
        eyeGazeX += (gazeTx - eyeGazeX) * gazeLerp;
        eyeGazeY += (gazeTy - eyeGazeY) * gazeLerp;

        // 2b) the whale tail, anchored at her waist
        var tailRoot = ComputeGeom(now).SpritePoint(.56, .785);
        double wagSpeed = happy ? .015 : typingHeat > .45 ? .014 : typing ? .0082 : .0042;
        tail.WagPhaseX = (tail.WagPhaseX + dt * wagSpeed * 1000) % (Math.PI * 2);
        tail.WagPhaseY = (tail.WagPhaseY + dt * wagSpeed * 1000 * 1.15) % (Math.PI * 2);
        double idleWag = Math.Sin(tail.WagPhaseX) * (happy ? .34 : .16 + typingHeat * .16);
        double maxIdleOffX = Km.Clamp(LogicalW - 26 - tailRoot.X, 54, 86);
        double idleOffX = maxIdleOffX + Math.Sin(tail.WagPhaseX) * 9, idleOffY = -36 + Math.Cos(tail.WagPhaseY) * 7;
        double tailAge = Math.Max(0, now - tail.SlapAt);
        bool holding = !Privacy && (tail.Held.Count > 0 || tail.SlapAt > 0 && tailAge < 260);
        double targetPhase = 0;
        if (holding) targetPhase = Math.Min(1, tailAge / 65);
        else if (!Privacy && tail.SlapAt > 0)
        {
            double holdEnd = Math.Max(tail.SlapAt + 260, tail.ReleaseAt);
            targetPhase = Math.Max(0, 1 - Math.Max(0, now - holdEnd) / 220);
        }
        tail.Phase += (targetPhase - tail.Phase) * (1 - Math.Exp(-34 * dt));
        if (tail.Phase < .002 && targetPhase == 0) tail.Phase = 0;
        double slapOffX = tail.SlapTargetX - tailRoot.X, slapOffY = tail.SlapTargetY - tailRoot.Y;
        double tLerp = 1 - Math.Exp(-36 * dt);
        tail.RelX += (idleOffX + (slapOffX - idleOffX) * tail.Phase - tail.RelX) * tLerp;
        tail.RelY += (idleOffY + (slapOffY - idleOffY) * tail.Phase - tail.RelY) * tLerp;
        tail.Squash *= Math.Exp(-11 * dt);
        tail.ActiveWag = idleWag * (1 - tail.Phase) - .08 * tail.Phase;

        // 3) ripples and particles
        for (int i = ripples.Count - 1; i >= 0; i--)
        {
            ripples[i].R += 58 * dt;
            ripples[i].Life -= 2.2 * dt;
            if (ripples[i].Life <= 0) ripples.RemoveAt(i);
        }
        for (int i = particles.Count - 1; i >= 0; i--)
        {
            var p = particles[i];
            p.X += p.Vx * dt * 60; p.Y += p.Vy * dt * 60; // KuroCat moves them per 60 Hz frame
            p.Life -= .022 * dt * 60;
            if (p.Life <= 0) particles.RemoveAt(i);
        }

        // 4) hands (KuroCat moves them while drawing the foreground; here before drawing)
        UpdateHands(dt, now);
    }

    private void UpdateHands(double dt, double now)
    {
        var g = ComputeGeom(now);
        var shL = g.SpritePoint(.39, .653);
        var shR = g.SpritePoint(.61, .653);
        bool mouseMode = MouseMode(now);
        bool leftTyping = !Privacy && !mouseMode && (now - leftHand.LastAt < 380 || leftHand.Held.Count > 0);
        bool rightTyping = !Privacy && (now - rightHand.LastAt < 380 || rightHand.Held.Count > 0);
        double lx, ly, rx, ry;
        if (Privacy)
        {
            var eyeL = g.SpritePoint(.390, .476);
            var eyeR = g.SpritePoint(.598, .476);
            double shy = Math.Sin(now * .008) * 1.4;
            lx = eyeL.X; ly = eyeL.Y + shy; rx = eyeR.X; ry = eyeR.Y - shy;
        }
        else if (Happy)
        {
            double cheer = Math.Sin(now * .016) * 5;
            lx = shL.X - 14; ly = shL.Y + 4 + cheer; rx = shR.X + 14; ry = shR.Y + 4 - cheer;
        }
        else
        {
            if (mouseMode)
            {
                var btn = MouseButtonPoint;
                lx = btn.X; ly = btn.Y + (mouseLeftDown || mouseRightDown ? 2.5 : 0);
            }
            else if (leftTyping) { lx = leftHand.Tx; ly = leftHand.Ty; }
            else { lx = shL.X - 15; ly = shL.Y + 34; }
            if (rightTyping) { rx = rightHand.Tx; ry = rightHand.Ty; }
            else { rx = shR.X + 15; ry = shR.Y + 34; }
        }
        double h = 1 - Math.Exp(-26 * dt);
        leftHand.X += (lx - leftHand.X) * h; leftHand.Y += (ly - leftHand.Y) * h;
        rightHand.X += (rx - rightHand.X) * h; rightHand.Y += (ry - rightHand.Y) * h;
        leftHand.Squash *= Math.Exp(-14 * dt);
        rightHand.Squash *= Math.Exp(-14 * dt);
    }
}
