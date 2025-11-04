using System;
using System.Collections.Generic;

namespace TiYf.Engine.Core;

public sealed class GvrsShadowAlertManager
{
    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);

    public bool TryRegister(string decisionId, bool shouldAlert)
    {
        if (!shouldAlert)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(decisionId))
        {
            return false;
        }
        return _emitted.Add(decisionId);
    }

    public void Clear(string decisionId)
    {
        if (string.IsNullOrWhiteSpace(decisionId))
        {
            return;
        }
        _emitted.Remove(decisionId);
    }
}
