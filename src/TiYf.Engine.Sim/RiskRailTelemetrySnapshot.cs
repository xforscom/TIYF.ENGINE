using System;
using System.Collections.Generic;

namespace TiYf.Engine.Sim;

public sealed record RiskRailTelemetrySnapshot(
    decimal? BrokerDailyLossCapCcy,
    decimal BrokerDailyLossUsedCcy,
    long BrokerDailyLossViolationsTotal,
    long? MaxPositionUnitsLimit,
    long MaxPositionUnitsUsed,
    long MaxPositionViolationsTotal,
    IReadOnlyDictionary<string, long>? SymbolUnitCaps,
    IReadOnlyDictionary<string, long> SymbolUnitUsage,
    IReadOnlyDictionary<string, long> SymbolUnitViolations,
    bool CooldownEnabled,
    bool CooldownActive,
    DateTime? CooldownActiveUntilUtc,
    long CooldownTriggersTotal,
    int? CooldownConsecutiveLosses,
    int? CooldownMinutes);
