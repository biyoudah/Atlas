namespace Atlas.FakeCnc;

/// <summary>
/// Deterministic telemetry scenarios for reproducible testing and CI validation:
/// - NormalProduction: Steady cycle execution, 10,000 RPM spindle, good part count incrementing.
/// - NetworkInterruption: Stalled updates and simulated socket disconnects.
/// - CounterReset: Shift change counter reset from baseline to zero.
/// - QualityFault: Bad sensor status codes, missing properties, out-of-range sensor drift.
/// - ProductionDisruption: Tool wear alarm, feed hold, spindle stop, vibration spike.
/// - SchemaVariation: Non-standard namespaces and alternative register encodings.
/// </summary>
public enum SimulationScenario
{
    NormalProduction,
    NetworkInterruption,
    CounterReset,
    QualityFault,
    ProductionDisruption,
    SchemaVariation
}
