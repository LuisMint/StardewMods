using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Pathoschild.Stardew.Automate.Framework.Patches;
using StardewValley;
using StardewValley.Extensions;
using StardewValley.Menus;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. A small, fully self-contained "hold a button to keep a wandering target inside your
/// moving bar" minigame, used by <see cref="CrankedPowerCoilPatches"/> for the Cranked Power Coil's
/// "crank" interaction. This is a from-scratch reimplementation of just the core mechanic vanilla's own
/// fishing minigame (<see cref="BobberBar"/>) uses — NOT a subclass or reuse of <see cref="BobberBar"/>
/// itself, and it never touches <see cref="StardewValley.Tools.FishingRod"/> at all.
///
/// This exists specifically because the previous implementation instantiated the real
/// <see cref="BobberBar"/> and Harmony-patched its draw call to reskin it — which meant any OTHER
/// installed mod that Harmony-patches <see cref="BobberBar"/>/<see cref="StardewValley.Tools.FishingRod"/>
/// (a common "fishing overhaul" mod category) also affected our reused instance, since Harmony patches
/// apply per-method across every instance, not just ours. Being fully independent makes this minigame
/// completely immune to that class of conflict.
///
/// Deliberately drops several things a real fishing minigame has that don't make sense for cranking a
/// machine: no treasure chest sub-minigame, no bobber/tackle item modifiers, no fish size/quality/motion
/// variety (only vanilla's default "wandering" behavior is ported), and no beginner's-rod leniency.
/// <see cref="Progress"/> instead decays unconditionally whenever the target isn't contained, rather
/// than vanilla's own "only decays once you've caught a real fish" gate (a real-fishing-specific
/// tutorial leniency that doesn't map onto this at all).
///
/// Positioned via <see cref="Reposition"/>, ported from vanilla <see cref="BobberBar.Reposition"/>
/// almost exactly — anchored near the player (not dead-center on screen, which read as noticeably off
/// compared to the original fishing-based implementation).
///
/// <see cref="CrankedPowerCoilPatches.TryStartCrankMinigame"/> constructs this and assigns it to
/// <see cref="Game1.activeClickableMenu"/> directly; <see cref="CrankedPowerCoilPatches.CheckMinigameCompletion"/>
/// detects it closing (by reference inequality, same as it did for <see cref="BobberBar"/> before) and
/// reads <see cref="Progress"/>/<see cref="IsTargetContained"/> to resolve the outcome. This class knows
/// nothing about the coil/farmer/mod-data plumbing at all — it's a generic, reusable widget.
/// </summary>
internal class CrankMinigame : IClickableMenu
{
    /*********
    ** Fields
    *********/
    /// <summary>The menu's fixed width — matches vanilla <see cref="BobberBar"/>'s own box size, so every offset below lines up with the existing skin assets without any rescaling.</summary>
    private const int BoxWidth = 96;

    /// <summary>The menu's fixed height — see <see cref="BoxWidth"/>'s own remarks.</summary>
    private const int BoxHeight = 636;

    /// <summary>The target's own vertical travel range (0 = top). Matches vanilla <see cref="BobberBar"/>'s own <c>bobberTrackHeight</c>.</summary>
    private const float TargetTrackHeight = 548f;

    /// <summary>The bar's own vertical travel range, including its own height — always bigger than <see cref="TargetTrackHeight"/> since the bar itself has real height. Matches vanilla's own <c>bobberBarTrackHeight</c>.</summary>
    private const float BarBottomLimit = 568f;

    /// <summary>The bar's fixed height — no fishing-level formula at all, unlike vanilla (which the old implementation had to override on top of anyway to decouple it from the real Fishing skill).</summary>
    private const int BarHeight = 192;

    /// <summary>
    /// MOD: added. The difficulty value driving the target's own wandering behavior — matches Catfish's
    /// own real <c>Data/Fish</c> difficulty (75), since the crank was originally built around Catfish's
    /// movement and should keep feeling the same now that it's independent of real fish data entirely.
    /// </summary>
    private const float Difficulty = 75f;

    /// <summary>How much <see cref="Progress"/> gains per tick while the target is contained.</summary>
    private const float ProgressGainPerTickContained = 0.002f;

    /// <summary>
    /// MOD: added. How much slower <see cref="Progress"/> drains than vanilla's raw per-tick rate would
    /// otherwise give (0.15 = about 7x slower) — matches the old <c>BobberBar</c>-based implementation's
    /// own <c>distanceFromCatchPenaltyModifier</c> override, brought back per request. Vanilla itself only
    /// ever uses this same field for its "Blessing of Waters" buff (at 0.5, i.e. half speed); this is a
    /// much bigger slowdown, deliberate for the crank specifically.
    /// </summary>
    private const float DecayPenaltyModifier = 0.15f;

    /// <summary>How much <see cref="Progress"/> drains per tick while the target isn't contained — applied unconditionally, unlike vanilla's own real-fishing-only decay gate (see this class's own remarks), and slowed by <see cref="DecayPenaltyModifier"/>.</summary>
    private const float ProgressDecayPerTickUncontained = 0.003f * CrankMinigame.DecayPenaltyModifier;

    /// <summary>The <see cref="Progress"/> value a fresh crank starts at — a generous cushion, since there's no beginner-leniency mechanic to fall back on here at all.</summary>
    private const float StartingProgress = 0.35f;

    /// <summary>How much <see cref="Scale"/> changes per tick while fading in/out.</summary>
    private const float FadeStep = 0.05f;

    /// <summary>How long the closing shake/sparkle plays before the menu actually finishes closing, in real milliseconds.</summary>
    private const float ShakeDurationMs = 500f;

    /// <summary>How much of the bar's velocity survives a bounce off the top/bottom of its track.</summary>
    private const float BarBounceRestitution = 2f / 3f;

    /// <summary>How much the bar's gravity is dampened while it's currently containing the target — makes it easier to hold steady once you're actually on target.</summary>
    private const float BarFrictionWhenContained = 0.6f;

    /// <summary>The bar's gravity magnitude — pulls down while the button's released, pulls up (as a negative value) while it's held.</summary>
    private const float GravityMagnitude = 0.25f;

    /// <summary>The target's own current vertical position (0-548, matching <see cref="TargetTrackHeight"/>). Matches vanilla's own <c>bobberPosition</c>.</summary>
    private float TargetPosition;

    /// <summary>The target's own current vertical velocity.</summary>
    private float TargetVelocity;

    /// <summary>The target's current wander destination, or <c>-1</c> if it isn't currently heading anywhere.</summary>
    private float TargetDestination = -1f;

    /// <summary>The player-controlled bar's own current vertical position.</summary>
    private float BarPosition;

    /// <summary>The player-controlled bar's own current vertical velocity.</summary>
    private float BarVelocity;

    /// <summary>Whether the crank button is currently held (left mouse, the "use tool" key, or a gamepad face button).</summary>
    private bool IsButtonHeld;

    /// <summary>
    /// MOD: added. Whether the target is currently inside the bar right now — internal (not private) so
    /// <see cref="CrankedPowerCoilPatches.GetCrankShakeSpeed"/> can read it for the cranking coil's own
    /// on-object shake speed, the same way it used to read vanilla <see cref="BobberBar.bobberInBar"/>.
    /// </summary>
    internal bool IsTargetContained;

    /// <summary>
    /// MOD: added. The crank's own progress toward success, from 0 (an immediate loss) to 1 (a win) —
    /// internal (not private) so <see cref="CrankedPowerCoilPatches.CheckMinigameCompletion"/> can read
    /// it once this menu closes, the same way it used to read vanilla <see cref="BobberBar.distanceFromCatching"/>.
    /// </summary>
    internal float Progress = CrankMinigame.StartingProgress;

    /// <summary>The reel icon's current rotation.</summary>
    private float ReelRotation;

    /// <summary>The target icon's own small jitter offset, while contained.</summary>
    private Vector2 TargetShake;

    /// <summary>The bar's own small jitter offset, while missing the target.</summary>
    private Vector2 BarShake;

    /// <summary>A shared jitter offset applied to the whole menu, while <see cref="EverythingShakeTimerMs"/> is running (the win/loss/escape shake).</summary>
    private Vector2 EverythingShake;

    /// <summary>How much longer <see cref="EverythingShake"/> keeps jittering, in real milliseconds.</summary>
    private float EverythingShakeTimerMs;

    /// <summary>The menu's own fade-in/fade-out scale, from 0 to 1.</summary>
    private float Scale;

    /// <summary>Whether the menu is currently fading in.</summary>
    private bool IsFadingIn = true;

    /// <summary>Whether the menu is currently fading out (about to close).</summary>
    private bool IsFadingOut;

    /// <summary>Whether the win/loss outcome has already been resolved (a sound played, <see cref="IsFadingOut"/> set) — guards against resolving it twice between the natural loss/win branch and <see cref="emergencyShutDown"/>.</summary>
    private bool HasHandledResult;

    /// <summary>The currently-playing "target contained" reel sound, if any.</summary>
    private ICue? ReelSound;

    /// <summary>The currently-playing "target missed" reel-back sound, if any.</summary>
    private ICue? UnreelSound;

    /// <summary>Whether this menu is currently anchored to the right of the player instead of the left — see <see cref="Reposition"/>'s own remarks. Flips the bubble backdrop's own draw the same way vanilla's own <c>flipBubble</c> does.</summary>
    private bool IsFlipped;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    public CrankMinigame()
        : base(0, 0, CrankMinigame.BoxWidth, CrankMinigame.BoxHeight)
    {
        // MOD: fixed — matches vanilla BobberBar's own constructor exactly (bobberPosition = 508f;
        // bobberTargetPosition = (100f - difficulty) / 100f * 548f;) rather than an invented "start
        // centered, no destination yet" state — the target's very first movement now reads the same as
        // vanilla's instead of a noticeably different opening beat.
        this.TargetPosition = 508f;
        this.TargetDestination = (100f - CrankMinigame.Difficulty) / 100f * CrankMinigame.TargetTrackHeight;
        this.BarPosition = CrankMinigame.BarBottomLimit - CrankMinigame.BarHeight;

        this.Reposition();
        Game1.player.Halt();
    }

    /// <inheritdoc />
    public override void update(GameTime time)
    {
        base.update(time);

        this.Reposition();

        if (this.EverythingShakeTimerMs > 0f)
        {
            this.EverythingShakeTimerMs -= time.ElapsedGameTime.Milliseconds;
            this.EverythingShake = new Vector2(Game1.random.Next(-10, 11) / 10f, Game1.random.Next(-10, 11) / 10f);
            if (this.EverythingShakeTimerMs <= 0f)
                this.EverythingShake = Vector2.Zero;
        }

        if (this.IsFadingIn)
        {
            this.Scale += CrankMinigame.FadeStep;
            if (this.Scale >= 1f)
            {
                this.Scale = 1f;
                this.IsFadingIn = false;
            }
            return; // no physics while still fading in — matches vanilla's own fadeIn branch being exclusive of the main loop
        }

        if (this.IsFadingOut)
        {
            if (this.EverythingShakeTimerMs > 0f)
                return; // let the shake finish playing first, same as vanilla

            this.Scale -= CrankMinigame.FadeStep;
            if (this.Scale <= 0f)
            {
                this.Scale = 0f;
                this.IsFadingOut = false;
                Game1.exitActiveMenu();
            }
            return;
        }

        this.UpdatePhysics(time);
    }

    /// <inheritdoc />
    public override void draw(SpriteBatch b)
    {
        // MOD: fixed — vanilla BobberBar never dims the background at all (you can see the world clearly
        // while fishing); an earlier version of this code borrowed a screen-darkening overlay from
        // PowerSiloMenu/PowerRelayMenu's own convention, which doesn't apply here since it isn't
        // something the class it's modeled on actually does.

        // vanilla's own translucent bubble backdrop — flips to match Reposition's own IsFlipped side, same as vanilla's flipBubble.
        b.Draw(Game1.mouseCursors, new Vector2(this.xPositionOnScreen - (this.IsFlipped ? 44 : 20) + 104, this.yPositionOnScreen - 16 + 314) + this.EverythingShake,
            new Rectangle(652, 1685, 52, 157), Color.White * 0.6f * this.Scale, 0f, new Vector2(26f, 78.5f) * this.Scale, 4f * this.Scale, this.IsFlipped ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0.001f);

        // MOD: added — the crank minigame's own themed track/pole backdrop, drawn in place of vanilla's
        // fishing-pole sprite — see CrankedPowerCoilPatches.CrankingUiAssetName's own remarks.
        Texture2D crankingUiTexture = Game1.content.Load<Texture2D>(CrankedPowerCoilPatches.CrankingUiAssetName);
        b.Draw(crankingUiTexture, new Vector2(this.xPositionOnScreen + 70, this.yPositionOnScreen + 296) + this.EverythingShake,
            new Rectangle(0, 0, crankingUiTexture.Width, crankingUiTexture.Height), Color.White * this.Scale, 0f,
            new Vector2(crankingUiTexture.Width / 2f, crankingUiTexture.Height / 2f) * this.Scale, 4f * this.Scale, SpriteEffects.None, 0.01f);

        if (this.Scale == 1f) // only draw bar/target detail once fully faded in — matches vanilla's own gating
        {
            Color barTint = this.IsTargetContained
                ? Color.White
                : Color.White * 0.25f * ((float)Math.Round(Math.Sin(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 100.0), 2) + 2f);

            // bar track/end-caps — same vanilla mouseCursors chrome BobberBar itself uses (generic UI
            // pieces, not fishing-specific, so there's no reason to reskin these too).
            b.Draw(Game1.mouseCursors, new Vector2(this.xPositionOnScreen + 64, this.yPositionOnScreen + 12 + (int)this.BarPosition) + this.BarShake + this.EverythingShake,
                new Rectangle(682, 2078, 9, 2), barTint, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.89f);
            b.Draw(Game1.mouseCursors, new Vector2(this.xPositionOnScreen + 64, this.yPositionOnScreen + 12 + (int)this.BarPosition + 8) + this.BarShake + this.EverythingShake,
                new Rectangle(682, 2081, 9, 1), barTint, 0f, Vector2.Zero, new Vector2(4f, CrankMinigame.BarHeight - 16), SpriteEffects.None, 0.89f);
            b.Draw(Game1.mouseCursors, new Vector2(this.xPositionOnScreen + 64, this.yPositionOnScreen + 12 + (int)this.BarPosition + CrankMinigame.BarHeight - 8) + this.BarShake + this.EverythingShake,
                new Rectangle(682, 2085, 9, 2), barTint, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.89f);

            // progress fill.
            b.Draw(Game1.staminaRect, new Rectangle(this.xPositionOnScreen + 124, this.yPositionOnScreen + 4 + (int)(580f * (1f - this.Progress)), 16, (int)(580f * this.Progress)),
                Utility.getRedToGreenLerpColor(this.Progress));

            // reel icon.
            b.Draw(Game1.mouseCursors, new Vector2(this.xPositionOnScreen + 18, this.yPositionOnScreen + 514) + this.EverythingShake,
                new Rectangle(257, 1990, 5, 10), Color.White, this.ReelRotation, new Vector2(2f, 10f), 4f, SpriteEffects.None, 0.9f);

            // MOD: added — the crank minigame's own themed target icon, drawn in place of vanilla's
            // moving fish sprite — see CrankedPowerCoilPatches.CrankingTargetAssetName's own remarks.
            Texture2D crankingTargetTexture = Game1.content.Load<Texture2D>(CrankedPowerCoilPatches.CrankingTargetAssetName);
            b.Draw(crankingTargetTexture, new Vector2(this.xPositionOnScreen + 64 + 18, this.yPositionOnScreen + 12 + 24 + this.TargetPosition) + this.TargetShake + this.EverythingShake,
                new Rectangle(0, 0, crankingTargetTexture.Width, crankingTargetTexture.Height), Color.White, 0f,
                new Vector2(crankingTargetTexture.Width / 2f, crankingTargetTexture.Height / 2f), 2f, SpriteEffects.None, 0.88f);
        }

        this.drawMouse(b);
    }

    /// <inheritdoc />
    public override bool readyToClose()
    {
        // matches vanilla BobberBar — can't be closed via the generic upper-right X/menu-close routing,
        // only via receiveKeyPress's own explicit Escape handling below, or natural completion.
        return false;
    }

    /// <inheritdoc />
    public override void receiveKeyPress(Keys key)
    {
        if (Game1.options.menuButton.Contains(new InputButton(key)))
            this.emergencyShutDown();
    }

    /// <inheritdoc />
    public override void emergencyShutDown()
    {
        base.emergencyShutDown();

        this.UnreelSound?.Stop(AudioStopOptions.Immediate);
        this.ReelSound?.Stop(AudioStopOptions.Immediate);

        // MOD: fixed — deliberately does NOT match vanilla here. Vanilla's own emergencyShutDown resets
        // everythingShakeTimer back up to its full duration on EVERY call, and update()'s fade-out branch
        // won't start counting the scale down until that timer reaches 0 — so spamming Escape/the menu
        // button keeps re-triggering this and re-resetting the timer, stalling the close indefinitely.
        // The sound is still allowed to play every time (so mashing the key still gives feedback), but
        // the actual close sequence only ever gets armed once.
        Game1.playSound("fishEscape"); // matches vanilla's own emergencyShutDown cue

        if (this.HasHandledResult)
            return;

        this.IsFadingOut = true;
        this.EverythingShakeTimerMs = CrankMinigame.ShakeDurationMs;
        this.Progress = -1f; // forces CheckMinigameCompletion's ">= 1f" read to be false — treated as a failed crank, matching vanilla's own emergencyShutDown trick
        this.HasHandledResult = true;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>
    /// MOD: fixed — anchors this menu near the player instead of dead-center on screen, matching vanilla
    /// <see cref="BobberBar.Reposition"/> almost exactly (adapted to this class's own field names): a
    /// fixed offset up-and-to-the-side of the player, on whichever side they're facing away from (so it
    /// doesn't cover them), then clamped to stay fully on screen. Called once from the constructor and
    /// again every tick from <see cref="update"/>, same as vanilla, so it tracks the player if they
    /// happen to shift position while the menu is open.
    /// </summary>
    private void Reposition()
    {
        this.IsFlipped = Game1.player.FacingDirection == 3; // facing right — anchor to the right instead of the left, and flip the bubble backdrop to match

        if (this.IsFlipped)
        {
            this.xPositionOnScreen = (int)Game1.player.Position.X + 128;
            this.yPositionOnScreen = (int)Game1.player.Position.Y - 274;
        }
        else
        {
            this.xPositionOnScreen = (int)Game1.player.Position.X - 64 - 132;
            this.yPositionOnScreen = (int)Game1.player.Position.Y - 274;
        }

        this.xPositionOnScreen -= Game1.viewport.X;
        this.yPositionOnScreen -= Game1.viewport.Y + 64;

        if (this.xPositionOnScreen + CrankMinigame.BoxWidth > Game1.viewport.Width)
            this.xPositionOnScreen = Game1.viewport.Width - CrankMinigame.BoxWidth;
        else if (this.xPositionOnScreen < 0)
            this.xPositionOnScreen = 0;

        if (this.yPositionOnScreen < 0)
            this.yPositionOnScreen = 0;
        else if (this.yPositionOnScreen + CrankMinigame.BoxHeight > Game1.viewport.Height)
            this.yPositionOnScreen = Game1.viewport.Height - CrankMinigame.BoxHeight;
    }

    /// <summary>Advance the target/bar physics and progress by one tick — only ever called while neither fading in nor out.</summary>
    /// <param name="time">The current game time.</param>
    private void UpdatePhysics(GameTime time)
    {
        // 1. target wandering — vanilla's default "mixed" motion type only (see this class's own remarks).
        // MOD: fixed — vanilla's real condition here is "motionType != 2 || bobberTargetPosition == -1f",
        // which for the "mixed" type (motionType 0, always != 2) is unconditionally true — meaning
        // vanilla can re-roll a BRAND NEW destination on this same roll EVEN WHILE the target is already
        // heading somewhere, not just once it's arrived. An earlier version of this code incorrectly
        // gated this on TargetDestination == -1f, which made the target noticeably more predictable/less
        // erratic than real Catfish movement — that gate is intentionally NOT here anymore.
        if (Game1.random.NextDouble() < CrankMinigame.Difficulty / 4000.0)
        {
            float spaceBelow = CrankMinigame.TargetTrackHeight - this.TargetPosition;
            float spaceAbove = this.TargetPosition;
            float percent = Math.Min(99f, CrankMinigame.Difficulty + Game1.random.Next(10, 45)) / 100f;
            this.TargetDestination = this.TargetPosition + Game1.random.Next((int)Math.Min(-spaceAbove, spaceBelow), (int)spaceBelow) * percent;
        }

        if (Math.Abs(this.TargetPosition - this.TargetDestination) > 3f && this.TargetDestination != -1f)
        {
            float acceleration = (this.TargetDestination - this.TargetPosition) / (Game1.random.Next(10, 30) + (100f - Math.Min(100f, CrankMinigame.Difficulty)));
            this.TargetVelocity += (acceleration - this.TargetVelocity) / 5f;
        }
        else if (Game1.random.NextDouble() < CrankMinigame.Difficulty / 2000.0)
        {
            this.TargetDestination = this.TargetPosition + (Game1.random.NextBool() ? Game1.random.Next(-100, -51) : Game1.random.Next(50, 101));
        }
        else
        {
            this.TargetDestination = -1f;
        }

        this.TargetDestination = Math.Max(-1f, Math.Min(this.TargetDestination, CrankMinigame.TargetTrackHeight));
        this.TargetPosition += this.TargetVelocity;
        this.TargetPosition = Math.Clamp(this.TargetPosition, 0f, 532f);

        // 2. containment check, including vanilla's own near-bottom edge-case guard.
        this.IsTargetContained = this.TargetPosition + 12f <= this.BarPosition - 32f + CrankMinigame.BarHeight
            && this.TargetPosition - 16f >= this.BarPosition - 32f;
        if (this.TargetPosition >= CrankMinigame.TargetTrackHeight - CrankMinigame.BarHeight && this.BarPosition >= CrankMinigame.BarBottomLimit - CrankMinigame.BarHeight - 4f)
            this.IsTargetContained = true;

        // 3. input — polled directly, matching vanilla BobberBar.update()'s own approach (its receiveLeftClick etc. are no-ops too).
        bool wasHeld = this.IsButtonHeld;
        this.IsButtonHeld = Game1.oldMouseState.LeftButton == ButtonState.Pressed
            || Game1.isOneOfTheseKeysDown(Game1.oldKBState, Game1.options.useToolButton)
            || (Game1.options.gamepadControls && (Game1.oldPadState.IsButtonDown(Buttons.X) || Game1.oldPadState.IsButtonDown(Buttons.A)));
        if (!wasHeld && this.IsButtonHeld)
            Game1.playSound("fishingRodBend");

        // 4. bar physics.
        float gravity = this.IsButtonHeld ? -CrankMinigame.GravityMagnitude : CrankMinigame.GravityMagnitude;
        if (this.IsButtonHeld && gravity < 0f && (this.BarPosition == 0f || this.BarPosition == CrankMinigame.BarBottomLimit - CrankMinigame.BarHeight))
            this.BarVelocity = 0f;
        if (this.IsTargetContained)
            gravity *= CrankMinigame.BarFrictionWhenContained;

        float oldBarPosition = this.BarPosition;
        this.BarVelocity += gravity;
        this.BarPosition += this.BarVelocity;

        if (this.BarPosition + CrankMinigame.BarHeight > CrankMinigame.BarBottomLimit)
        {
            this.BarPosition = CrankMinigame.BarBottomLimit - CrankMinigame.BarHeight;
            this.BarVelocity = -this.BarVelocity * CrankMinigame.BarBounceRestitution;
            if (oldBarPosition + CrankMinigame.BarHeight < CrankMinigame.BarBottomLimit)
                Game1.playSound("shiny4");
        }
        else if (this.BarPosition < 0f)
        {
            this.BarPosition = 0f;
            this.BarVelocity = -this.BarVelocity * CrankMinigame.BarBounceRestitution;
            if (oldBarPosition > 0f)
                Game1.playSound("shiny4");
        }

        // 5. progress + cosmetics.
        if (this.IsTargetContained)
        {
            this.Progress += CrankMinigame.ProgressGainPerTickContained;
            this.ReelRotation += MathF.PI / 8f;
            this.TargetShake = new Vector2(Game1.random.Next(-10, 11) / 10f, Game1.random.Next(-10, 11) / 10f);
            this.BarShake = Vector2.Zero;
            Rumble.rumble(0.1f, 1000f);
            this.UnreelSound?.Stop(AudioStopOptions.Immediate);
            if (this.ReelSound == null || this.ReelSound.IsStopped || this.ReelSound.IsStopping || !this.ReelSound.IsPlaying)
                Game1.playSound("fastReel", out this.ReelSound);
        }
        else
        {
            // MOD: fixed — matches vanilla's own "tinyWhip" cue, played exactly once at the moment the
            // target leaves the bar (vanilla detects this via its fishShake field still being nonzero
            // from the previous "contained" tick; TargetShake's own reset already gives us the same
            // one-tick-late signal, so checking it here before it's cleared below is equivalent).
            if (this.TargetShake != Vector2.Zero)
                Game1.playSound("tinyWhip");

            this.Progress -= CrankMinigame.ProgressDecayPerTickUncontained; // MOD: unconditional — see this class's own remarks on why vanilla's real-fishing-only leniency gate is dropped
            float distanceAway = Math.Abs(this.TargetPosition - (this.BarPosition + CrankMinigame.BarHeight / 2f));
            this.ReelRotation -= MathF.PI / Math.Max(10f, 200f - distanceAway);
            this.BarShake = new Vector2(Game1.random.Next(-10, 11) / 10f, Game1.random.Next(-10, 11) / 10f);
            this.TargetShake = Vector2.Zero;
            this.ReelSound?.Stop(AudioStopOptions.Immediate);
            if (this.UnreelSound == null || this.UnreelSound.IsStopped)
                Game1.playSound("slowReel", 600, out this.UnreelSound);
        }

        this.Progress = Math.Clamp(this.Progress, 0f, 1f);

        // 6. win/loss detection.
        if (this.Progress <= 0f)
        {
            this.IsFadingOut = true;
            this.EverythingShakeTimerMs = CrankMinigame.ShakeDurationMs;
            Game1.playSound("fishEscape"); // MOD: fixed — matches vanilla's own loss cue exactly (just a cue name, no coupling to FishingRod/BobberBar)
            this.HasHandledResult = true;
            this.UnreelSound?.Stop(AudioStopOptions.Immediate);
            this.ReelSound?.Stop(AudioStopOptions.Immediate);
        }
        else if (this.Progress >= 1f)
        {
            this.EverythingShakeTimerMs = CrankMinigame.ShakeDurationMs;
            Game1.playSound("jingle1");
            this.IsFadingOut = true;
            this.HasHandledResult = true;
            this.UnreelSound?.Stop(AudioStopOptions.Immediate);
            this.ReelSound?.Stop(AudioStopOptions.Immediate);
        }
    }
}
