namespace DevDriveCore.Models;

/// <summary>
/// A request for the elevated resize broker: the <see cref="ResizeMode"/> to run and the serialized
/// <see cref="ResizePlan"/> JSON to hand to the helper. The broker writes the plan to a temp file,
/// launches the elevated helper, and returns the raw result JSON it wrote back.
/// </summary>
/// <param name="Mode">Read-only feasibility (<see cref="ResizeMode.WhatIf"/>) or the real resize (<see cref="ResizeMode.Execute"/>).</param>
/// <param name="PlanJson">The serialized <see cref="ResizePlan"/> the helper should act on.</param>
public sealed record ResizeBrokerRequest(ResizeMode Mode, string PlanJson);
