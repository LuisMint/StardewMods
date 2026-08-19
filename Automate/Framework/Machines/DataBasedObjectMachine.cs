using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.GameData.Machines;
using StardewValley.Inventories;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Machines;

/// <summary>An object that accepts input and provides output based on the rules in <see cref="DataLoader.Machines"/>.</summary>
internal class DataBasedObjectMachine : GenericObjectMachine<SObject>
{
    /*********
    ** Fields
    *********/
    /// <summary>The minimum machine processing time in minutes for which to apply fairy dust.</summary>
    private readonly Func<int> MinMinutesForFairyDust;

    /// <summary>MOD: added. What percentage (0-100) of the experience this machine would normally grant on harvest is actually granted when Automate collects it automatically — see <see cref="Models.ModConfig.AutomationExperiencePercent"/>.</summary>
    private readonly Func<int> GetExperiencePercent;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="machine">The underlying machine.</param>
    /// <param name="location">The location containing the machine.</param>
    /// <param name="tile">The tile covered by the machine.</param>
    /// <param name="minMinutesForFairyDust">The minimum machine processing time in minutes for which to apply fairy dust.</param>
    /// <param name="getExperiencePercent">MOD: added. What percentage (0-100) of the experience this machine would normally grant on harvest is actually granted when Automate collects it automatically.</param>
    public DataBasedObjectMachine(SObject machine, GameLocation location, Vector2 tile, Func<int> minMinutesForFairyDust, Func<int> getExperiencePercent)
        : base(machine, location, tile, BaseMachine.GetDefaultMachineId(machine.Name))
    {
        this.MinMinutesForFairyDust = minMinutesForFairyDust;
        this.GetExperiencePercent = getExperiencePercent;
    }

    /// <inheritdoc />
    public override MachineState GetState()
    {
        MachineState state = this.GetGenericState();

        if (state == MachineState.Done && this.Machine.GetMachineData()?.IsIncubator is true)
            state = MachineState.Processing; // don't grab incubating egg before it hatches

        return state;
    }

    /// <inheritdoc />
    public override bool SetInput(IStorage input)
    {
        SObject machine = this.Machine;

        // skip if no input needed
        if (!machine.HasContextTag("machine_input"))
            return false;

        // add machine input
        // MOD: routed through the container's own IHasAttemptAutoLoad handling when it has one (see that
        // interface's own remarks) — e.g. ItemFilteredContainer's implementation, when a sign filter
        // applies, since vanilla's own ingredient consumption mutates the input item's Stack directly,
        // bypassing any count-checking method a plain container.Inventory call could otherwise clamp; or
        // ThrottledContainer's implementation, which fires this mod's own exit-effect animation for
        // whatever actually got consumed this way, since that path bypasses its usual per-item hooks too.
        bool addedInput = false;
        foreach (IContainer container in input.MachineOutputContainers)
        {
            // MOD: added — vanilla's own AttemptAutoLoad checks a recipe's required count against ONE
            // inventory slot's own Stack at a time; it never combines several slots holding the same
            // item (e.g. a chest with four 999-count Copper Ore stacks after hitting the 999 cap). Once
            // a stack drains below whatever a recipe needs, vanilla just skips it forever and moves to
            // the next full stack — leaving a trail of small "orphaned" stacks (4, 4, 4, 4...) that
            // individually never meet the requirement even though they total more than enough. Merging
            // same-item stacks together first (up to each item's own max stack size) before vanilla's
            // check runs means it always sees as much of that item as physically fits in one slot.
            DataBasedObjectMachine.ConsolidateFragmentedStacks(container.Inventory);

            bool loaded = container is IHasAttemptAutoLoad withAutoLoad
                ? withAutoLoad.AttemptAutoLoad(machine, Game1.player)
                : machine.AttemptAutoLoad(container.Inventory, Game1.player);

            if (loaded)
            {
                addedInput = true;
                break;
            }
        }

        // apply fairy dust
        if (addedInput)
            this.TryApplyFairyDust(input);

        return addedInput;
    }

    /// <inheritdoc />
    /// <remarks>This implementation is based on <see cref="SObject.CheckForActionOnMachine"/>.</remarks>
    public override ITrackedStack? GetOutput()
    {
        SObject machine = this.Machine;
        MachineData? machineData = machine.GetMachineData();

        if (machineData?.IsIncubator is true)
            return null; // don't grab incubating egg before it hatches

        // recalculate output if needed (e.g. bee house honey)
        if (machine.lastOutputRuleId.Value != null && machineData != null)
        {
            MachineOutputRule? outputRule = machineData.OutputRules?.FirstOrDefault(p => p.Id == machine.lastOutputRuleId.Value);
            if (outputRule?.RecalculateOnCollect == true)
            {
                var prevOutput = machine.heldObject.Value;
                machine.heldObject.Value = null;

                machine.OutputMachine(machineData, outputRule, machine.lastInputItem.Value, null, machine.Location, false);

                machine.heldObject.Value ??= prevOutput;
            }
        }

        // get output
        return this.GetTracked(this.Machine.heldObject.Value, onReduced: this.OnOutputCollected);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Reset the machine, so it's ready to accept a new input.</summary>
    /// <param name="trackedStack">The tracked item stack which was reduced.</param>
    /// <param name="item">The item that was taken.</param>
    /// <remarks>This implementation is based on <see cref="SObject.CheckForActionOnMachine"/>.</remarks>
    protected void OnOutputCollected(TrackedItem trackedStack, Item item)
    {
        SObject machine = this.Machine;
        MachineData? machineData = machine.GetMachineData();

        // update stats
        int numberCollected = Math.Max(1, trackedStack.LastCount - trackedStack.Count);
        bool empty = trackedStack.Count < 1;
        MachineDataUtility.UpdateStats(machineData?.StatsToIncrementWhenHarvested, item, numberCollected);

        // reset when empty
        if (empty)
        {
            // reset machine data
            // This needs to happen before the OutputCollected check, which may start producing a new output.
            machine.heldObject.Value = null;
            machine.readyForHarvest.Value = false;
            machine.showNextIndex.Value = false;
            machine.ResetParentSheetIndex();

            // apply OutputCollected rule
            if (MachineDataUtility.TryGetMachineOutputRule(machine, machineData, MachineOutputTrigger.OutputCollected, item.getOne(), null, machine.Location, out MachineOutputRule outputCollectedRule, out _, out _, out _))
                machine.OutputMachine(machineData, outputCollectedRule, machine.lastInputItem.Value, null, machine.Location, false);

            // update tapper
            if (machine.IsTapper())
            {
                if (machine.Location.terrainFeatures.TryGetValue(machine.TileLocation, out TerrainFeature terrainFeature) && terrainFeature is Tree tree)
                    tree.UpdateTapperProduct(machine, item as SObject);
            }

            // grant any experience
            // MOD: changed — scaled by GetExperiencePercent, since automated collection defaults to 0%
            // (no experience) instead of the full amount a manual harvest would grant — see
            // Models.ModConfig.AutomationExperiencePercent's own remarks.
            if (machineData?.ExperienceGainOnHarvest != null)
            {
                string[] expSplit = machineData.ExperienceGainOnHarvest.Split(' ');
                for (int i = 0; i < expSplit.Length - 1; i += 2)
                {
                    int skill = Farmer.getSkillNumberFromName(expSplit[i]);
                    if (skill != -1 && expSplit.Length > i + 1)
                    {
                        if (int.TryParse(expSplit[i + 1], out int amount))
                        {
                            int scaledAmount = (int)Math.Round(amount * (this.GetExperiencePercent() / 100.0));
                            if (scaledAmount > 0)
                                Game1.player.gainExperience(skill, scaledAmount);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// MOD: added. Merge fragmented stacks of the same item together (up to each item's own max stack
    /// size), so a single slot ends up holding as much of that item as physically fits — see this
    /// class's own remarks on <see cref="SetInput"/> for why this matters ahead of vanilla's own
    /// <c>AttemptAutoLoad</c> check. Merges toward the EARLIEST matching slot(s) first, nulling out any
    /// slot that's fully drained into another.
    /// </summary>
    /// <param name="inventory">The inventory to consolidate.</param>
    private static void ConsolidateFragmentedStacks(IInventory inventory)
    {
        Dictionary<string, List<int>> slotIndexesByItemId = [];
        for (int i = 0; i < inventory.Count; i++)
        {
            Item? item = inventory[i];
            if (item == null || item.maximumStackSize() <= 1)
                continue; // not a stackable item — nothing to consolidate

            if (!slotIndexesByItemId.TryGetValue(item.QualifiedItemId, out List<int>? slotIndexes))
                slotIndexesByItemId[item.QualifiedItemId] = slotIndexes = [];
            slotIndexes.Add(i);
        }

        foreach (List<int> slotIndexes in slotIndexesByItemId.Values)
        {
            if (slotIndexes.Count < 2)
                continue; // only one stack of this item — nothing to merge it with

            for (int a = 0; a < slotIndexes.Count; a++)
            {
                Item? target = inventory[slotIndexes[a]];
                if (target == null)
                    continue;

                int maxStack = target.maximumStackSize();

                for (int b = slotIndexes.Count - 1; b > a; b--)
                {
                    Item? source = inventory[slotIndexes[b]];
                    if (source == null || source.Stack <= 0)
                        continue;

                    int room = maxStack - target.Stack;
                    if (room <= 0)
                        break; // target is full — move on to the next target slot

                    // MOD: fixed — matching QualifiedItemId alone doesn't mean two stacks are actually
                    // the same item: a flavored ColoredObject (wine, juice, pickles, jelly, etc.) shares
                    // its QualifiedItemId across every flavor, so without this check two DIFFERENT
                    // flavors (e.g. Ancient Fruit Wine and Melon Wine) would get merged together here —
                    // discarding whichever slot got merged away and keeping only the OTHER flavor's
                    // identity, silently turning cheap wine into expensive wine. canStackWith is
                    // vanilla's own authoritative check for whether two item instances are really
                    // interchangeable (also covers quality and orderData, not just color/name).
                    if (!target.canStackWith(source))
                        continue;

                    int move = Math.Min(room, source.Stack);
                    target.Stack += move;
                    source.Stack -= move;

                    if (source.Stack <= 0)
                        inventory[slotIndexes[b]] = null!;
                }
            }
        }
    }

    /// <summary>Apply fairy dust from the given containers if needed.</summary>
    /// <param name="input">The input to search for containers.</param>
    private void TryApplyFairyDust(IStorage input)
    {
        SObject machine = this.Machine;
        int minMinutes = Math.Max(10, this.MinMinutesForFairyDust());

        if (machine.MinutesUntilReady < minMinutes || !machine.TryApplyFairyDust(probe: true))
            return;

        int maxToApply = 3;
        foreach (IContainer container in input.MachineOutputContainers)
        {
            while (maxToApply > 0 && container.Inventory.ContainsId("(O)872"))
            {
                if (!machine.TryApplyFairyDust())
                    return;

                container.Inventory.ReduceId("(O)872", 1);
                maxToApply--;

                if (machine.MinutesUntilReady < minMinutes || !machine.TryApplyFairyDust(probe: true))
                    return;
            }
        }
    }
}
