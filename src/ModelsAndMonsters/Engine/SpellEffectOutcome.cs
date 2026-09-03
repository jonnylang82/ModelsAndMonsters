namespace ModelsAndMonsters.Engine;

public sealed record SpellEffectOutcome : ActionOutcome
{
    public required string CasterId { get; init; }
    public required string CasterName { get; init; }
    public required string TargetId { get; init; }
    public required string TargetName { get; init; }
    public required string AbilityName { get; init; }
    public required string EffectSummary { get; init; }
    public override string OutcomeType => "spell-effect";
    public override string Summary => $"{CasterName} used {AbilityName}. {EffectSummary}";
}
