namespace Opc.Ua.Atlas.Profiling;

/// <summary>
/// Classified behavioral signal type of an OPC UA node determined through passive live profiling.
/// </summary>
public enum SignalClassification
{
    /// <summary>
    /// Value never changed during the observation window.
    /// </summary>
    Constant,

    /// <summary>
    /// Binary signal that only flips between true/false or 0/1.
    /// </summary>
    Boolean,

    /// <summary>
    /// Monotonically non-decreasing numeric values with discrete step increments (e.g. part counter).
    /// </summary>
    Counter,

    /// <summary>
    /// Target setpoint that changes infrequently and holds steady between operator/program adjustments.
    /// </summary>
    Setpoint,

    /// <summary>
    /// High-frequency analog process value with sensor noise, ripple, or kinematic fluctuations (e.g. speed, pressure, temp).
    /// </summary>
    AnalogProcessValue,

    /// <summary>
    /// Discrete multi-state status code or enum with a small finite set of integer/string states (e.g. Running, Stopped, Fault).
    /// </summary>
    StateEnum
}
