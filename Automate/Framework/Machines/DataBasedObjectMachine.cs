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


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="machine">The underlying machine.</param>
    /// <param name="location">The location containing the machine.</param>
    /// <param name="tile">The tile covered by the machine.</param>
    /// <param name="minMinutesForFairyDust">The minimum machine processing time in minutes for which to apply fairy dust.</param>
    public DataBasedObjectMachine(SObject machine, GameLocation location, Vector2 tile, Func<int> minMinutesForFairyDust)
        : base(machine, location, tile, BaseMachine.GetDefaultMachineId(machine.Name))
    {
        this.MinMinutesForFairyDust = minMinutesForFairyDust;
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
        // MOD: routed through ItemFilteredContainer's own AttemptAutoLoad when a sign filter applies,
        // since vanilla's own ingredient consumption mutates the input item's Stack directly — bypassing
        // any count-checking method a plain container.Inventory call could otherwise clamp — so a
        // numeric whitelist/blacklist condition needs that method's own stack-clamping workaround to be
        // enforced here at all. See ItemFilteredContainer.AttemptAutoLoad's own remarks for why.
        bool addedInput = false;
        foreach (IContainer container in input.OutputContainers)
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

            bool loaded = container is ItemFilteredContainer filtered
                ? filtered.AttemptAutoLoad(machine, Game1.player)
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
            if (machineData?.ExperienceGainOnHarvest != null)
            {
                string[] expSplit = machineData.ExperienceGainOnHarvest.Split(' ');
                for (int i = 0; i < expSplit.Length - 1; i += 2)
                {
                    int skill = Farmer.getSkillNumberFromName(expSplit[i]);
                    if (skill != -1 && expSplit.Length > i + 1)
                    {
                        if (int.TryParse(expSplit[i + 1], out int amount))
                            Game1.player.gainExperience(skill, amount);
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
        foreach (IContainer container in input.OutputContainers)
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
