using System;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Patches;
using StardewValley;
using StardewValley.Buildings;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Handles the Power Relay's feed/status interaction, registered via
/// <see cref="GameLocation.RegisterTileAction"/> against its <c>Data/Buildings</c> <c>ActionTiles</c>
/// entry — same mechanism <see cref="PowerSiloInteraction"/> uses, and deliberately mirrors its exact
/// shape: clicking the Relay while holding a matching item (Prismatic Shard for the delay-reduction
/// track, Radioactive Bar for the actions-per-window track) delivers it — consumed, one-way, same as a
/// Power Silo tier — and otherwise opens <see cref="PowerRelayMenu"/>. An earlier version of this class
/// let the player pull delivered items back out via the menu; per direct user request, delivery is now
/// one-way and the menu is purely a status display.
/// </summary>
internal class PowerRelayInteraction
{
    /*********
    ** Fields
    *********/
    /// <summary>The key this interaction is registered under — must match the <c>Action</c> value on the Power Relay's <c>Data/Buildings</c> <c>ActionTiles</c> entry.</summary>
    public const string ActionKey = "luisMint.PoweredAutomation_PowerRelayInteract";

    /// <summary>The power relay system, used to read/write a Relay's delivered counts.</summary>
    private readonly PowerRelaySystem PowerRelaySystem;

    /// <summary>Get the qualified/unqualified item ID delivered for the delay-reduction track, from level 1 onward.</summary>
    private readonly Func<string> GetShardItemId;

    /// <summary>MOD: added. Get the qualified/unqualified item ID delivered for the delay-reduction track's very first delivery only (level 0→1) — see <see cref="ModConfig.PowerRelayFirstShardItemId"/>.</summary>
    private readonly Func<string> GetFirstShardItemId;

    /// <summary>Get the qualified/unqualified item ID delivered for the actions-per-window bonus track, from level 1 onward.</summary>
    private readonly Func<string> GetBarItemId;

    /// <summary>MOD: added. Get the qualified/unqualified item ID delivered for the actions-per-window bonus track's very first delivery only (level 0→1) — see <see cref="ModConfig.PowerRelayFirstBarItemId"/>.</summary>
    private readonly Func<string> GetFirstBarItemId;

    /// <summary>Get the current base <see cref="ModConfig.ActionsPerDelayWindow"/>, before the Power Relay bonus is applied.</summary>
    private readonly Func<int> GetBaseActionsPerDelayWindow;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="powerRelaySystem">The power relay system, used to read/write a Relay's delivered counts.</param>
    /// <param name="getShardItemId">Get the qualified/unqualified item ID delivered for the delay-reduction track, from level 1 onward.</param>
    /// <param name="getFirstShardItemId">MOD: added. Get the qualified/unqualified item ID delivered for the delay-reduction track's very first delivery only (level 0→1).</param>
    /// <param name="getBarItemId">Get the qualified/unqualified item ID delivered for the actions-per-window bonus track, from level 1 onward.</param>
    /// <param name="getFirstBarItemId">MOD: added. Get the qualified/unqualified item ID delivered for the actions-per-window bonus track's very first delivery only (level 0→1).</param>
    /// <param name="getBaseActionsPerDelayWindow">Get the current base <see cref="ModConfig.ActionsPerDelayWindow"/>, before the Power Relay bonus is applied.</param>
    public PowerRelayInteraction(PowerRelaySystem powerRelaySystem, Func<string> getShardItemId, Func<string> getFirstShardItemId, Func<string> getBarItemId, Func<string> getFirstBarItemId, Func<int> getBaseActionsPerDelayWindow)
    {
        this.PowerRelaySystem = powerRelaySystem;
        this.GetShardItemId = getShardItemId;
        this.GetFirstShardItemId = getFirstShardItemId;
        this.GetBarItemId = getBarItemId;
        this.GetFirstBarItemId = getFirstBarItemId;
        this.GetBaseActionsPerDelayWindow = getBaseActionsPerDelayWindow;
    }

    /// <summary>MOD: added. Get the qualified/unqualified item ID currently accepted for a track, given its current level — the track's normal item from level 1 onward, or its special first-delivery item while still at level 0.</summary>
    /// <param name="level">The track's current level.</param>
    /// <param name="getNormalItemId">Get the track's normal item ID (level 1 onward).</param>
    /// <param name="getFirstItemId">Get the track's special first-delivery item ID (level 0 only).</param>
    private static string GetCurrentItemId(int level, Func<string> getNormalItemId, Func<string> getFirstItemId)
    {
        return level == 0 ? getFirstItemId() : getNormalItemId();
    }

    /// <summary>Register this interaction with the game, so clicking a Power Relay's action tile invokes <see cref="Handle"/>.</summary>
    public void Register()
    {
        GameLocation.RegisterTileAction(PowerRelayInteraction.ActionKey, this.Handle);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Handle a click on a Power Relay's action tile.</summary>
    /// <param name="location">The location containing the Power Relay.</param>
    /// <param name="args">The action's arguments — unused, since this action takes none.</param>
    /// <param name="who">The player who triggered the action.</param>
    /// <param name="tile">The tile that was clicked.</param>
    /// <returns>Returns whether the action was handled.</returns>
    private bool Handle(GameLocation location, string[] args, Farmer who, Point tile)
    {
        Building? relay = location.getBuildingAt(new Vector2(tile.X, tile.Y));
        if (relay is null)
            return false;

        SObject? held = who.ActiveObject;
        if (held is not null)
        {
            // MOD: added — per direct user request, each track's very first delivery (level 0→1) asks
            // for a different item (see GetFirstShardItemId/GetFirstBarItemId's own remarks) before
            // reverting to the track's normal item from level 1 onward.
            string shardItemId = PowerRelayInteraction.GetCurrentItemId(this.PowerRelaySystem.GetShardLevel(relay), this.GetShardItemId, this.GetFirstShardItemId);
            string barItemId = PowerRelayInteraction.GetCurrentItemId(this.PowerRelaySystem.GetBarLevel(relay), this.GetBarItemId, this.GetFirstBarItemId);

            // MOD: added — once the effective delay has been pushed down to its global floor, per direct
            // user request no Relay's shard track accepts further deliveries at all (not just this one),
            // so a held shard is treated as no longer a valid delivery target and the click falls through
            // to opening the menu instead, same as an ordinary per-Relay-maxed track already does.
            bool shardTrackGloballyCapped = this.PowerRelaySystem.IsGlobalSpeedCapped();
            if (!shardTrackGloballyCapped && this.TryDeliver(relay, held, who, location, shardItemId,
                this.PowerRelaySystem.GetShardsDelivered, this.PowerRelaySystem.SetShardsDelivered,
                this.PowerRelaySystem.GetShardLevel, this.PowerRelaySystem.GetShardsNeededForNextLevel,
                PowerRelaySystem.MaxCumulativeShards, "speed"))
                return true;
            if (this.TryDeliver(relay, held, who, location, barItemId,
                this.PowerRelaySystem.GetBarsDelivered, this.PowerRelaySystem.SetBarsDelivered,
                this.PowerRelaySystem.GetBarLevel, this.PowerRelaySystem.GetBarsNeededForNextLevel,
                PowerRelaySystem.MaxCumulativeBars, "actions"))
                return true;
        }

        // otherwise, open the Power Relay's status menu — matches how a Power Silo opens
        // PowerSiloMenu instead of showing plain dialogue when you're not actively feeding it.
        Game1.playSound("bigSelect");
        Game1.activeClickableMenu = new PowerRelayMenu(relay, this.PowerRelaySystem, this.GetShardItemId, this.GetFirstShardItemId, this.GetBarItemId, this.GetFirstBarItemId, this.GetBaseActionsPerDelayWindow);
        return true;
    }

    /// <summary>Try to deliver the held item toward one of the Relay's two tracks.</summary>
    /// <param name="relay">The Power Relay building.</param>
    /// <param name="held">The item the player is currently holding.</param>
    /// <param name="who">The player delivering the item.</param>
    /// <param name="location">The location containing the Relay.</param>
    /// <param name="requiredItemId">The item ID this track accepts.</param>
    /// <param name="getDelivered">Get the track's current raw cumulative delivered count.</param>
    /// <param name="setDelivered">Set the track's raw cumulative delivered count.</param>
    /// <param name="getLevel">Get the track's current level, derived from the raw cumulative count.</param>
    /// <param name="getNeededForNextLevel">Get how many more raw items are needed to reach the track's next level.</param>
    /// <param name="cumulativeCap">The track's maximum raw cumulative delivered count (i.e. the cost of reaching its max level).</param>
    /// <param name="effectName">A short name for what the track boosts, used in the HUD message (e.g. "speed"/"actions").</param>
    /// <returns>
    /// Whether the held item matched this track AND it wasn't already maxed. A held item that matches
    /// but the track is already fully maxed returns <c>false</c> too — per direct user request, a maxed
    /// track is no longer an interactable delivery target at all, so <see cref="Handle"/> falls through
    /// to opening the status menu instead of showing an "already at max" callout.
    /// </returns>
    private bool TryDeliver(Building relay, SObject held, Farmer who, GameLocation location, string requiredItemId, Func<Building, int> getDelivered, Action<Building, int> setDelivered, Func<Building, int> getLevel, Func<Building, int> getNeededForNextLevel, int cumulativeCap, string effectName)
    {
        bool matches = held.QualifiedItemId == requiredItemId || held.ItemId == requiredItemId;
        if (!matches)
            return false;

        int delivered = getDelivered(relay);
        if (delivered >= cumulativeCap)
            return false;

        int oldLevel = getLevel(relay);

        // MOD: delivers as much of the held stack as it takes to complete the CURRENT level, same as
        // PowerSiloInteraction dumping a whole held stack in one click — but capped at the current
        // level's own remaining need (getNeededForNextLevel), never more, per direct user request: a
        // player holding e.g. 999 shards only ever completes ONE level per click, not several at once.
        int remainingForCurrentLevel = getNeededForNextLevel(relay);
        int delivering = Math.Min(held.Stack, remainingForCurrentLevel);
        held.Stack -= delivering;
        if (held.Stack <= 0)
            who.removeItemFromInventory(held);

        int newDelivered = delivered + delivering;
        setDelivered(relay, newDelivered);

        location.playSound("give_gift");

        int newLevel = getLevel(relay);
        if (newLevel > oldLevel)
        {
            // MOD: deliberately no distinct level-up sound cue here — per direct user request, just the
            // "give_gift" every delivery already plays above, unlike PowerSiloInteraction's own
            // tier-complete cue.
            PowerRelayEffectPatches.TriggerLevelUpShake(relay); // MOD: added — the same whole-building shake a Power Silo gets, per direct user request.
            Game1.addHUDMessage(new HUDMessage($"Automation Relay increased Power Grid's automation {effectName}!", HUDMessage.newQuest_type));
        }
        else
        {
            // MOD: matches PowerSiloInteraction's own delivery message format exactly — "x/y" here is
            // progress toward the CURRENT level's own cost (see PowerRelaySystem.GetLevelCost), not the
            // track's overall cumulative total.
            string itemName = ItemRegistry.GetDataOrErrorItem(requiredItemId).DisplayName;
            int levelCost = PowerRelaySystem.GetLevelCost(oldLevel + 1);
            int progressWithinLevel = levelCost - getNeededForNextLevel(relay);
            Game1.addHUDMessage(new HUDMessage($"Delivered {delivering}x {itemName} ({progressWithinLevel}/{levelCost})", HUDMessage.newQuest_type));
        }

        return true;
    }
}
