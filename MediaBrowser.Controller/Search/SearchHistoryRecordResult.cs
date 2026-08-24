namespace MediaBrowser.Controller.Search;

/// <summary>
/// Describes the outcome of recording a profile search-history term.
/// </summary>
public enum SearchHistoryRecordResult
{
    /// <summary>
    /// The term was recorded.
    /// </summary>
    Recorded,

    /// <summary>
    /// The term contained no searchable characters after normalization.
    /// </summary>
    EmptyTerm,

    /// <summary>
    /// The raw request exceeded the bounded input contract.
    /// </summary>
    RawTermTooLong,

    /// <summary>
    /// The normalized display term exceeded the persistence contract.
    /// </summary>
    TermTooLong
}
