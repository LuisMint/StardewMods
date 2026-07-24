using System;
using StardewValley;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Wraps a real <see cref="ITrackedStack"/> to report a smaller <see cref="Count"/> than
/// it actually has, without copying or otherwise touching the underlying item — used to enforce a
/// numeric whitelist/blacklist sign condition (see <see cref="SignFilter"/>) by only ever exposing the
/// currently-allowed portion of a stack to callers. <see cref="Reduce"/>/<see cref="Take"/> forward to
/// the real stack, clamped to whatever's still remaining of the allowed amount.
/// </summary>
internal class ClampedTrackedStack : ITrackedStack
{
    /*********
    ** Fields
    *********/
    /// <summary>The real underlying stack being wrapped.</summary>
    private readonly ITrackedStack Inner;

    /// <summary>
    /// MOD: fixed. The remaining allowed count. This MUST decrease as <see cref="Reduce"/>/<see cref="Take"/>
    /// are called — it used to be a value fixed at construction time, which meant callers like
    /// <c>ChestContainer.Store</c> (which loops "reduce, then check if Count &lt;= 0 to know it's
    /// done") never saw it reach zero even after fully consuming the allowed amount, so they kept
    /// going and drained further stacks than intended (duplicating items into extra destination
    /// slots each time, since the real underlying stack WAS being reduced correctly on every one of
    /// those extra iterations — only this wrapper's own reported <see cref="Count"/> was stuck).
    /// </summary>
    private int Remaining;


    /*********
    ** Accessors
    *********/
    /// <inheritdoc />
    public Item Sample => this.Inner.Sample;

    /// <inheritdoc />
    public string Type => this.Inner.Type;

    /// <inheritdoc />
    public int Count => this.Remaining;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="inner">The real underlying stack being wrapped.</param>
    /// <param name="count">The clamped count to report, which must not exceed <paramref name="inner"/>'s own count.</param>
    public ClampedTrackedStack(ITrackedStack inner, int count)
    {
        this.Inner = inner;
        this.Remaining = count;
    }

    /// <inheritdoc />
    public void Reduce(int count)
    {
        count = Math.Min(count, this.Remaining);
        if (count <= 0)
            return;

        this.Inner.Reduce(count);
        this.Remaining -= count;
    }

    /// <inheritdoc />
    public Item? Take(int count)
    {
        count = Math.Min(count, this.Remaining);
        if (count <= 0)
            return null;

        Item? result = this.Inner.Take(count);
        this.Remaining -= count;
        return result;
    }
}
