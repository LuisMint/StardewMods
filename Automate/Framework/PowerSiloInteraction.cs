using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Models;
using Pathoschild.Stardew.Automate.Framework.Patches;
using StardewValley;
using StardewValley.Buildings;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Handles the Power Silo's feed/status interaction, registered via
/// <see cref="GameLocation.RegisterTileAction"/> against its <c>Data/Buildings</c> <c>ActionTiles</c>
/// entry — no Harmony patch needed for the interaction itself (unlike everything else custom about a
/// Power Silo, which lives in Harmony patches gated on its <c>buildingType</c> string; this hooks the
/// vanilla, fully data-driven <c>ActionTiles</c> mechanism instead). Clicking the Silo either delivers
/// its currently-requested item (if held in enough quantity) to advance its tier, or shows a status
/// readout of its current capacity and what it's asking for next.
/// </summary>
internal class PowerSiloInteraction
{
    /*********
    ** Fields
    *********/
    /// <summary>The key this interaction is registered under — must match the <c>Action</c> value on the Power Silo's <c>Data/Buildings</c> <c>ActionTiles</c> entry.</summary>
    public const string ActionKey = "luisMint.PoweredAutomation_PowerSiloInteract";

    /// <summary>The power silo capacity system, used to read/write a Silo's current tier and total capacity.</summary>
    private readonly PowerSiloSystem PowerSiloSystem;

    /// <summary>MOD: changed — now resolved per Power Silo building, since each Silo rolls its own independent tier requirements (see <see cref="PowerSiloTierRoller"/>'s own remarks).</summary>
    private readonly Func<Building, List<PowerSiloTierConfig>> GetTiers;

    /// <summary>MOD: added. Show a HUD toast locally and broadcast it to every other connected player — see <see cref="ModEntry.BroadcastHudMessage"/>. Power Silo capacity is a save-wide stat, not per-player, so every player should see a tier-up.</summary>
    private readonly Action<string> BroadcastHudMessage;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="powerSiloSystem">The power silo capacity system, used to read/write a Silo's current tier and total capacity.</param>
    /// <param name="getTiers">Get the ordered capacity tiers for a specific Power Silo building.</param>
    /// <param name="broadcastHudMessage">MOD: added. Show a HUD toast locally and broadcast it to every other connected player — see <see cref="BroadcastHudMessage"/>.</param>
    public PowerSiloInteraction(PowerSiloSystem powerSiloSystem, Func<Building, List<PowerSiloTierConfig>> getTiers, Action<string> broadcastHudMessage)
    {
        this.PowerSiloSystem = powerSiloSystem;
        this.GetTiers = getTiers;
        this.BroadcastHudMessage = broadcastHudMessage;
    }

    /// <summary>Register this interaction with the game, so clicking a Power Silo's action tile invokes <see cref="Handle"/>.</summary>
    public void Register()
    {
        GameLocation.RegisterTileAction(PowerSiloInteraction.ActionKey, this.Handle);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Handle a click on a Power Silo's action tile.</summary>
    /// <param name="location">The location containing the Power Silo.</param>
    /// <param name="args">The action's arguments — unused, since this action takes none.</param>
    /// <param name="who">The player who triggered the action.</param>
    /// <param name="tile">The tile that was clicked.</param>
    /// <returns>Returns whether the action was handled.</returns>
    private bool Handle(GameLocation location, string[] args, Farmer who, Point tile)
    {
        Building? silo = location.getBuildingAt(new Vector2(tile.X, tile.Y));
        if (silo is null)
            return false;

        List<PowerSiloTierConfig> tiers = this.GetTiers(silo);
        if (tiers.Count == 0)
            return false;

        int tierIndex = Math.Clamp(this.PowerSiloSystem.GetTier(silo), 0, tiers.Count - 1);
        List<PowerSiloRequiredItem> requiredItems = tiers[tierIndex].RequiredItems ?? [];

        // try to deliver whatever's held toward one of the current tier's still-outstanding
        // requirements — each is tracked and delivered independently (see PowerSiloSystem's
        // GetDeliveredCount/SetDeliveredCount), so holding just one of possibly several required item
        // types still makes progress instead of needing to carry all of them in one trip.
        SObject? held = who.ActiveObject;
        if (held is not null)
        {
            for (int i = 0; i < requiredItems.Count; i++)
            {
                PowerSiloRequiredItem requirement = requiredItems[i];
                int delivered = this.PowerSiloSystem.GetDeliveredCount(silo, i);
                int remaining = requirement.Count - delivered;
                if (remaining <= 0)
                    continue; // already fully delivered — nothing left to accept for this one

                if (!requirement.Matches(held))
                    continue;

                int delivering = Math.Min(held.Stack, remaining);
                held.Stack -= delivering;
                if (held.Stack <= 0)
                    who.removeItemFromInventory(held);

                int newDelivered = delivered + delivering;
                this.PowerSiloSystem.SetDeliveredCount(silo, i, newDelivered, requiredItems.Count);

                bool tierComplete = true;
                for (int j = 0; j < requiredItems.Count; j++)
                {
                    int deliveredForJ = j == i ? newDelivered : this.PowerSiloSystem.GetDeliveredCount(silo, j);
                    if (deliveredForJ < requiredItems[j].Count)
                    {
                        tierComplete = false;
                        break;
                    }
                }

                location.playSound("give_gift");
                if (tierComplete)
                {
                    location.playSound("luisMint.PoweredAutomation_LowGrunt"); // MOD: added — a distinct cue for the moment a tier actually completes, layered on top of the "give_gift" every delivery already plays
                    int newTierIndex = tierIndex + 1;

                    // MOD: added — reaching the solar tier (delivering a Solar Panel here just consumes
                    // it as a crafting ingredient, same as any other required item — it doesn't need to
                    // be PLACED in the world) can jump total capacity by a lot in one step if the player
                    // already has Solar Panels placed elsewhere, or has multiple Silos all reaching this
                    // tier — exactly the kind of change the "Expanded Power Grid ±N" popup already
                    // covers for coil/panel/chest placement, so this reuses the exact same one instead
                    // of silently absorbing the jump into the generic message below.
                    bool unlockingSolarTier = newTierIndex < tiers.Count && tiers[newTierIndex].GrantsSolarBonus;
                    int before = this.PowerSiloSystem.GetTotalCapacity();

                    this.PowerSiloSystem.SetTier(silo, newTierIndex);
                    this.PowerSiloSystem.ResetDeliveryProgress(silo); // MOD: added — the next tier's requirements start fresh

                    if (unlockingSolarTier)
                        this.PowerSiloSystem.RefreshConnectedSolarPanelCount(); // MOD: added — this Silo's capacity depends on this count for the first time as of this exact tier change, so make sure it's current before measuring "after"

                    this.PowerSiloSystem.RefreshCoilAllowance(); // MOD: added — refresh immediately rather than waiting for the next tick's capacity check to notice the tier changed
                    int after = this.PowerSiloSystem.GetTotalCapacity();

                    if (unlockingSolarTier && before != after)
                        PowerSiloPatches.ShowCapacityPopup(this.PowerSiloSystem, before, after);
                    else
                        this.BroadcastHudMessage("Power Silo capacity increased!");
                }
                else
                {
                    string itemName = requirement.GetDisplayName();
                    Game1.addHUDMessage(new HUDMessage($"Delivered {delivering}x {itemName} ({newDelivered}/{requirement.Count})", HUDMessage.newQuest_type));
                }
                return true;
            }
        }

        // otherwise, open the Power Silo's status menu (see PowerSiloMenu) — matches how a Fish Pond
        // opens PondQueryMenu instead of showing plain dialogue when you're not actively feeding it,
        // including the same "bigSelect" opening cue FishPond plays right before constructing its own
        // menu (see FishPond.performObjectDropInAction/its interaction handling) — PowerSiloMenu itself
        // doesn't need to play it, since closing it already inherits IClickableMenu's own default
        // "bigDeSelect" close sound for the Escape/menu-button path, same as PondQueryMenu.
        Game1.playSound("bigSelect");
        Game1.activeClickableMenu = new PowerSiloMenu(silo, this.PowerSiloSystem, tiers);
        return true;
    }
}
