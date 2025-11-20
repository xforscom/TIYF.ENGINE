using System;
using System.Collections.Generic;

namespace TiYf.Engine.Sim;

/// <summary>
/// Telemetry snapshot for the risk rails to surface via metrics and /health (no enforcement).
/// </summary>
/// <param name="BrokerDailyLossCapCcy">Configured broker loss cap in account CCY (null if disabled).</param>
/// <param name="BrokerDailyLossUsedCcy">Current realized loss counted toward the broker cap.</param>
/// <param name="BrokerDailyLossViolationsTotal">Number of telemetry alerts triggered for the broker cap.</param>
/// <param name="MaxPositionUnitsLimit">Global position unit cap configured (null if disabled).</param>
/// <param name="MaxPositionUnitsUsed">Aggregated open units currently counted toward the global cap.</param>
/// <param name="MaxPositionViolationsTotal">Telemetry violations observed for the global unit cap.</param>
/// <param name="SymbolUnitCaps">Configured per-symbol unit caps (null if none).</param>
/// <param name="SymbolUnitUsage">Current open units per symbol.</param>
/// <param name="SymbolUnitViolations">Telemetry violation counters per symbol.</param>
/// <param name="BrokerCapBlocksTotal">Total broker guardrail evaluations that detected a breach.</param>
/// <param name="BrokerCapBlocksByGate">Per-gate broker guardrail counts (daily loss/global units/per-symbol).</param>
/// <param name="CooldownEnabled">Whether the cooldown guard is configured.</param>
/// <param name="CooldownActive">Whether a cooldown is currently active.</param>
/// <param name="CooldownActiveUntilUtc">UTC timestamp when the cooldown will expire (if active).</param>
/// <param name="CooldownTriggersTotal">Total number of cooldown triggers observed.</param>
/// <param name="CooldownConsecutiveLosses">Configured loss streak threshold for cooldown activation.</param>
/// <param name="CooldownMinutes">Configured cooldown window length in minutes.</param>
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
    long BrokerCapBlocksTotal,
    IReadOnlyDictionary<string, long> BrokerCapBlocksByGate,
    bool CooldownEnabled,
    bool CooldownActive,
    DateTime? CooldownActiveUntilUtc,
    long CooldownTriggersTotal,
    int? CooldownConsecutiveLosses,
    int? CooldownMinutes);
