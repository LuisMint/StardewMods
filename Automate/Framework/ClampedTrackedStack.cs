using StardewValley;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Wraps a real <see cref="ITrackedStack"/> to report a smaller <see cref="Count"/> than
/// it actually has, without copying or otherwise touching the underlying item — used to enforce a
/// numeric whitelist/blacklist sign condition (see <see cref="SignFilter"/>) by only ever exposing the
/// currently-allowed portion of a stack to callers. <see cref="Reduce"/>/<see cref="Take"/> forward
/// straight to the real stack, since callers only ever request up to this wrapper's own (clamped)
/// <see cref="Count"/>.
/// </summary>
internal class ClampedTrackedStack : ITrackedStack
{
    /*********
    ** Fields
    *********/
    /// <summary>The real underlying stack being wrapped.</summary>
    private readonly ITrackedStack Inner;


    /*********
    ** Accessors
    *********/
    /// <inheritdoc />
    public Item Sample => this.Inner.Sample;

    /// <inheritdoc />
    public string Type => this.Inner.Type;

    /// <inheritdoc />
    public int Count { get; }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="inner">The real underlying stack being wrapped.</param>
    /// <param name="count">The clamped count to report, which must not exceed <paramref name="inner"/>'s own count.</param>
    public ClampedTrackedStack(ITrackedStack inner, int count)
    {
        this.Inner = inner;
        this.Count = count;
    }

    /// <inheritdoc />
    public void Reduce(int count) => this.Inner.Reduce(count);

    /// <inheritdoc />
    public Item? Take(int count) => this.Inner.Take(count);
}
