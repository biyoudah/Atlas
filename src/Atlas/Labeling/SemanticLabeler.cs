using System.Text.RegularExpressions;
using Atlas.Models;
using Atlas.Profiling;

namespace Atlas.Labeling;

/// <summary>
/// Infers standardized semantic labels (e.g. spindle_speed, parts_produced)
/// with explicit confidence scores and traceable evidence lists.
/// Combines Companion Specs, engineering units, EURanges, profiling telemetry,
/// and user-supplied mapping packs.
/// </summary>
public static class SemanticLabeler
{
    private static readonly Dictionary<string, (string Label, double BaseConfidence)> s_companionSpecKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ActualSpeed"] = ("spindle_speed", 0.70),
        ["SpindleSpeed"] = ("spindle_speed", 0.75),
        ["RotationalSpeed"] = ("spindle_speed", 0.70),
        ["SpindleLoad"] = ("spindle_load", 0.75),
        ["LoadTorque"] = ("spindle_load", 0.70),
        ["SpindleTemperature"] = ("spindle_temperature", 0.75),
        ["MotorTemperature"] = ("spindle_temperature", 0.70),
        ["BearingTemperature"] = ("spindle_temperature", 0.75),
        ["VibrationRMS"] = ("spindle_vibration", 0.75),
        ["Vibration"] = ("spindle_vibration", 0.70),
        ["PartsProduced"] = ("parts_produced", 0.75),
        ["PartCounter"] = ("parts_produced", 0.75),
        ["TotalParts"] = ("parts_produced", 0.70),
        ["GoodParts"] = ("parts_produced", 0.70),
        ["CycleTime"] = ("cycle_time", 0.75),
        ["CycleDuration"] = ("cycle_time", 0.70),
        ["CycleSec"] = ("cycle_time", 0.70),
        ["ActualFeedrate"] = ("feed_rate", 0.75),
        ["FeedRate"] = ("feed_rate", 0.70),
        ["Feed_Act"] = ("feed_rate", 0.70),
        ["CoolantPressure"] = ("coolant_pressure", 0.75),
        ["CoolantBar"] = ("coolant_pressure", 0.70),
        ["ExecutionState"] = ("machine_state", 0.75),
        ["OperatingMode"] = ("machine_state", 0.70),
        ["StateWord"] = ("machine_state", 0.65),
        ["EmergencyStop"] = ("emergency_stop", 0.85),
        ["EStop"] = ("emergency_stop", 0.80),
        ["DoorLocked"] = ("door_interlock", 0.75),
        ["DoorClosed"] = ("door_interlock", 0.70),
        ["ToolLifeRemaining"] = ("tool_wear", 0.75),
        ["ToolWearOffset"] = ("tool_wear", 0.75),
        ["OEE_Overall"] = ("oee_metric", 0.80),
        ["OEE_Availability"] = ("oee_metric", 0.75)
    };

    /// <summary>
    /// Labels each entry in the catalog in-place, adding InferredSemantics with confidence and evidence.
    /// </summary>
    public static void LabelCatalog(
        AtlasCatalog catalog,
        ProfileReport? profileReport = null,
        UserMappingPack? mappingPack = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        // Precompile user mapping regexes if provided
        List<(Regex Regex, UserMappingRule Rule)>? compiledRules = null;
        if (mappingPack?.Rules != null && mappingPack.Rules.Count > 0)
        {
            compiledRules = [];
            foreach (UserMappingRule r in mappingPack.Rules)
            {
                try
                {
                    compiledRules.Add((new Regex(r.Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), r));
                }
                catch
                {
                    // Ignore invalid regex patterns
                }
            }
        }

        foreach (CatalogEntry entry in catalog.Entries)
        {
            if (entry.NodeClass != "Variable")
            {
                continue;
            }

            // 1. User Mapping Pack (Always wins with 1.0 confidence)
            if (compiledRules != null)
            {
                UserMappingRule? matchedRule = null;
                foreach (var (regex, rule) in compiledRules)
                {
                    if (regex.IsMatch(entry.Id) || regex.IsMatch(entry.Path) || (entry.BrowseName != null && regex.IsMatch(entry.BrowseName)))
                    {
                        matchedRule = rule;
                        break;
                    }
                }

                if (matchedRule != null)
                {
                    if (!string.IsNullOrEmpty(matchedRule.Unit))
                    {
                        entry.Unit = matchedRule.Unit;
                    }

                    entry.Inferred = new InferredSemantics(
                        Label: matchedRule.Label,
                        Confidence: 1.0,
                        Evidence: [$"User mapping pack '{mappingPack!.Name}': pattern '{matchedRule.Pattern}'"]);

                    entry.Category = matchedRule.Category ?? DeduceCategory(entry, matchedRule.Label);
                    continue;
                }
            }

            // 2. Automated Multi-Factor Heuristic Inference
            string? candidateLabel = null;
            double confidence = 0.0;
            var evidence = new List<string>();

            // Factor A: Companion Spec Keyword Match
            string browseName = entry.BrowseName ?? string.Empty;
            if (s_companionSpecKeywords.TryGetValue(browseName, out var specMatch))
            {
                candidateLabel = specMatch.Label;
                confidence = specMatch.BaseConfidence;
                evidence.Add($"Companion spec / BrowseName match: '{browseName}'");
            }
            else
            {
                // Partial path segment match
                foreach (var (keyword, (label, baseConf)) in s_companionSpecKeywords)
                {
                    if (entry.Path.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        candidateLabel = label;
                        confidence = baseConf - 0.15;
                        evidence.Add($"Path segment match: '{keyword}'");
                        break;
                    }
                }
            }

            // Factor B: Engineering Units
            string unit = entry.Unit?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!string.IsNullOrEmpty(unit))
            {
                if (unit == "rpm")
                {
                    if (candidateLabel == "spindle_speed")
                    {
                        confidence += 0.15;
                        evidence.Add("unit matches rpm");
                    }
                    else if (candidateLabel == null)
                    {
                        candidateLabel = "spindle_speed";
                        confidence = 0.55;
                        evidence.Add("unit is rpm");
                    }
                }
                else if (unit is "bar" or "kpa" or "psi")
                {
                    if (candidateLabel == "coolant_pressure")
                    {
                        confidence += 0.15;
                        evidence.Add($"unit matches pressure ({unit})");
                    }
                    else if (candidateLabel == null)
                    {
                        candidateLabel = "pressure";
                        confidence = 0.50;
                        evidence.Add($"unit is {unit}");
                    }
                }
                else if (unit is "°c" or "deg c" or "k" or "°f")
                {
                    if (candidateLabel == "spindle_temperature")
                    {
                        confidence += 0.15;
                        evidence.Add($"unit matches temperature ({unit})");
                    }
                    else if (candidateLabel == null)
                    {
                        candidateLabel = "temperature";
                        confidence = 0.50;
                        evidence.Add($"unit is {unit}");
                    }
                }
                else if (unit is "mm/min" or "m/s" or "in/min")
                {
                    if (candidateLabel == "feed_rate")
                    {
                        confidence += 0.15;
                        evidence.Add($"unit matches feed rate ({unit})");
                    }
                }
                else if (unit is "s" or "ms")
                {
                    if (candidateLabel == "cycle_time")
                    {
                        confidence += 0.15;
                        evidence.Add($"unit matches cycle duration ({unit})");
                    }
                }
            }

            // Factor C: EURange Boundaries
            if (entry.Range != null)
            {
                if (candidateLabel == "spindle_speed" && entry.Range.High is >= 2000 and <= 40000)
                {
                    confidence += 0.10;
                    evidence.Add($"EURange {entry.Range.Low}-{entry.Range.High} matches industrial spindle speeds");
                }
                else if (candidateLabel == "coolant_pressure" && entry.Range.High is >= 5 and <= 150)
                {
                    confidence += 0.10;
                    evidence.Add($"EURange {entry.Range.Low}-{entry.Range.High} bar matches cutting coolant pump");
                }
            }

            // Factor D: Live Profiler Telemetry & Behavioral Correlations
            if (profileReport != null && profileReport.Profiles.TryGetValue(entry.Id, out NodeProfile? profile))
            {
                if (profile.Classification == SignalClassification.Counter)
                {
                    if (candidateLabel == "parts_produced")
                    {
                        confidence += 0.15;
                        evidence.Add("profiling: monotonic non-decreasing counter behavior");
                    }
                    else if (candidateLabel == null && (entry.BrowseName?.Contains("Counter", StringComparison.OrdinalIgnoreCase) == true ||
                                                        entry.BrowseName?.Contains("Part", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        candidateLabel = "parts_produced";
                        confidence = 0.65;
                        evidence.Add("profiling: counter telemetry on part register");
                    }
                }
                else if (profile.Classification == SignalClassification.AnalogProcessValue)
                {
                    if (candidateLabel is "spindle_speed" or "feed_rate" or "coolant_pressure" or "spindle_load")
                    {
                        confidence += 0.10;
                        evidence.Add("profiling: continuous fluctuating analog process value");
                    }
                }

                // Check cross-correlation evidence (e.g. tracks Running state)
                foreach (CorrelatedTransition corr in profileReport.Correlations)
                {
                    if (corr.TargetNodeId == entry.Id || corr.SourceNodeId == entry.Id)
                    {
                        if (corr.Relationship.Contains("Running", StringComparison.OrdinalIgnoreCase))
                        {
                            if (candidateLabel == null)
                            {
                                candidateLabel = "spindle_speed";
                                confidence = 0.60;
                            }
                            confidence += 0.20;
                            evidence.Add(corr.Relationship);
                            if (profile.ObservedMax is >= 1000)
                            {
                                evidence.Add($"range 0-{profile.ObservedMax:F0}");
                            }
                        }
                    }
                }
            }

            // Only assign if sufficient evidence and minimum confidence reached (e.g. >= 0.50)
            if (candidateLabel != null && confidence >= 0.45)
            {
                entry.Inferred = new InferredSemantics(
                    Label: candidateLabel,
                    Confidence: Math.Round(Math.Min(1.0, confidence), 2),
                    Evidence: evidence);
            }

            entry.Category = DeduceCategory(entry, candidateLabel);
        }
    }

    /// <summary>
    /// Categorizes a node into MTConnect tri-fold taxonomy (Sample, Event, Condition).
    /// </summary>
    public static SignalCategory DeduceCategory(CatalogEntry entry, string? label = null)
    {
        string effectiveLabel = label ?? entry.Inferred?.Label ?? string.Empty;

        // Condition indicators: safety, alarms, interlocks
        if (effectiveLabel is "emergency_stop" or "door_interlock" ||
            entry.Path.Contains("alarm", StringComparison.OrdinalIgnoreCase) ||
            entry.Path.Contains("fault", StringComparison.OrdinalIgnoreCase) ||
            entry.Path.Contains("estop", StringComparison.OrdinalIgnoreCase) ||
            entry.Path.Contains("interlock", StringComparison.OrdinalIgnoreCase) ||
            (entry.BrowseName != null && (
                entry.BrowseName.Contains("alarm", StringComparison.OrdinalIgnoreCase) ||
                entry.BrowseName.Contains("fault", StringComparison.OrdinalIgnoreCase) ||
                entry.BrowseName.Contains("interlock", StringComparison.OrdinalIgnoreCase))))
        {
            return SignalCategory.Condition;
        }

        // Event indicators: state transitions, counters, operational parameters
        if (effectiveLabel is "machine_state" or "parts_produced" or "operating_mode" or "active_program" or "tool_number" or "oee_metric" ||
            entry.DataType is "String" or "LocalizedText" ||
            (entry.EnumStrings != null && entry.EnumStrings.Count > 0) ||
            entry.DataType == "Boolean")
        {
            return SignalCategory.Event;
        }

        // Samples: analog fluctuating kinetics / measurements
        return SignalCategory.Sample;
    }
}
