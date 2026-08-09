using StardewModdingAPI;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Wraps a real <see cref="IManifest"/>, overriding only <see cref="Name"/> — used so the
/// Generic Mod Config Menu page can show "Powered Automation" instead of "Automate" without touching
/// this mod's real manifest.json (its actual <see cref="UniqueID"/>, <see cref="EntryDll"/>, etc. all
/// stay exactly as-is; only what GMCM displays as the section title changes). Every other SMAPI-facing
/// use of the real manifest (the startup log banner, <c>IModRegistry</c> lookups, update checks) is
/// untouched, since nothing but the GMCM registration call is given this wrapper.
/// </summary>
internal class DisplayNameManifest : IManifest
{
    /*********
    ** Fields
    *********/
    /// <summary>The real manifest being wrapped.</summary>
    private readonly IManifest Inner;


    /*********
    ** Accessors
    *********/
    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Author => this.Inner.Author;

    /// <inheritdoc />
    public ISemanticVersion Version => this.Inner.Version;

    /// <inheritdoc />
    public string Description => this.Inner.Description;

    /// <inheritdoc />
    public string UniqueID => this.Inner.UniqueID;

    /// <inheritdoc />
    public string? EntryDll => this.Inner.EntryDll;

    /// <inheritdoc />
    public IManifestContentPackFor? ContentPackFor => this.Inner.ContentPackFor;

    /// <inheritdoc />
    public IManifestDependency[] Dependencies => this.Inner.Dependencies;

    /// <inheritdoc />
    public ISemanticVersion? MinimumApiVersion => this.Inner.MinimumApiVersion;

    /// <inheritdoc />
    public ISemanticVersion? MinimumGameVersion => this.Inner.MinimumGameVersion;

    /// <inheritdoc />
    public System.Collections.Generic.IDictionary<string, object> ExtraFields => this.Inner.ExtraFields;

    /// <inheritdoc />
    public string[] UpdateKeys => this.Inner.UpdateKeys;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="inner">The real manifest being wrapped.</param>
    /// <param name="displayName">The name to show instead of <paramref name="inner"/>'s own.</param>
    public DisplayNameManifest(IManifest inner, string displayName)
    {
        this.Inner = inner;
        this.Name = displayName;
    }
}
