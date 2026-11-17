namespace BumpGoPrep.Core;

/// <summary>What the service does to each tag placed on the reader.</summary>
public enum TagMode
{
    /// <summary>
    /// Write the BumpGo password and PACK only. The charger then treats the
    /// tag as a new, empty BumpGo tag and you save a profile to it there.
    /// </summary>
    Prep,

    /// <summary>
    /// Read the whole tag and report it through
    /// <see cref="BumpGoTagPrepperService.TagRead"/>. Nothing is written.
    /// </summary>
    Dump,

    /// <summary>
    /// Prep the tag, then copy the profile pages from
    /// <see cref="BumpGoTagPrepperService.Template"/> onto it and read them
    /// back to verify.
    /// </summary>
    Program,
}
