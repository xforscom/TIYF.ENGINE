namespace TiYf.Engine.Core;

public sealed record SentimentGuardConfig(bool Enabled, int Window, decimal VolGuardSigma, string Mode);
