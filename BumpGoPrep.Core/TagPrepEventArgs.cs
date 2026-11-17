namespace BumpGoPrep.Core;

/// <summary>Raised whenever a new tag is placed on the reader.</summary>
public sealed class TagDetectedEventArgs : EventArgs
{
    public string? Uid { get; }
    public TagDetectedEventArgs(string? uid) => Uid = uid;
}

/// <summary>Raised when the tag that was on the reader is lifted off it.</summary>
public sealed class TagRemovedEventArgs : EventArgs
{
    public string? Uid { get; }
    public TagRemovedEventArgs(string? uid) => Uid = uid;
}

/// <summary>Raised after each individual write step completes (or fails).</summary>
public sealed class TagStepEventArgs : EventArgs
{
    public string StepLabel { get; }
    public bool Success { get; }
    public string Detail { get; }

    public TagStepEventArgs(string stepLabel, bool success, string detail)
    {
        StepLabel = stepLabel;
        Success = success;
        Detail = detail;
    }
}

/// <summary>Raised once a tag has been fully processed (all steps attempted).</summary>
public sealed class TagPreppedEventArgs : EventArgs
{
    public string? Uid { get; }
    public bool Success { get; }
    public int TotalPreppedCount { get; }

    public TagPreppedEventArgs(string? uid, bool success, int totalPreppedCount)
    {
        Uid = uid;
        Success = success;
        TotalPreppedCount = totalPreppedCount;
    }
}

/// <summary>General-purpose log/status message, for surfaces that just want text.</summary>
public sealed class LogEventArgs : EventArgs
{
    public string Message { get; }
    public LogEventArgs(string message) => Message = message;
}

/// <summary>Raised in <see cref="TagMode.Dump"/> once a tag's memory has been read.</summary>
public sealed class TagReadEventArgs : EventArgs
{
    public string? Uid { get; }
    public TagImage Image { get; }

    public TagReadEventArgs(string? uid, TagImage image)
    {
        Uid = uid;
        Image = image;
    }
}
