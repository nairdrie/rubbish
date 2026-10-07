using UnityEngine;

/// <summary>Neutral driveline drag, not a second service brake. Pure rules shared by tests and truck physics.</summary>
public static class TruckCoastRules
{
    public const float InputDeadZone = 0.08f;
    public const float RollingDeceleration = 0.7f;
    public const float SpeedDrag = 0.035f;
    public const float MaxDeceleration = 1.3f;

    /// <summary>Ground contact only; never reverse velocity in one tick or cancel airborne momentum.</summary>
    public static float SpeedLoss(float planarSpeed, float throttle, bool grounded, float deltaTime)
    {
        if (!grounded || Mathf.Abs(throttle) > InputDeadZone || planarSpeed <= 0f || deltaTime <= 0f ||
            float.IsNaN(planarSpeed) || float.IsInfinity(planarSpeed)) return 0f;
        float drag = Mathf.Min(MaxDeceleration, RollingDeceleration + planarSpeed * SpeedDrag);
        return Mathf.Min(planarSpeed, drag * deltaTime);
    }
}
