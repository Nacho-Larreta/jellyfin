using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Tests.Logging;

internal sealed record CapturedLogEntry(
    LogLevel Level,
    EventId EventId,
    IReadOnlyDictionary<string, object?> Properties,
    Exception? Exception,
    string Message);
