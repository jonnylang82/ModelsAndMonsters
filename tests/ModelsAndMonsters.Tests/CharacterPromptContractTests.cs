using ModelsAndMonsters.Prompts;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.10 additions to the character-facing action contract: exactly one mechanical deed per turn, that a
/// demand is only speech, that there is no distance to exploit in this room, and that a looted weapon is a
/// trophy rather than something to fight with. These pin the rendered prompt text down so the guidance cannot
/// silently drift or be deleted, the way it drifted out of scope for the largest two refusal buckets in
/// <c>reports/action-set-gaps.md</c> (compound intents and repositioning).
/// </summary>
public sealed class CharacterPromptContractTests
{
    private static readonly PromptLibrary Prompts =
        PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));

    private static string RowanSystemPrompt()
    {
        var scenario = TestWorld.V07Scenario();
        var factory = new CharacterPromptFactory(Prompts);
        var rowan = scenario.Characters.First(c => c.Id == TestWorld.RowanId);
        return factory.CreateSystemPrompt(rowan, scenario.Characters);
    }

    [Fact]
    public void The_prompt_states_one_mechanical_deed_per_turn_with_allowed_and_not_allowed_examples()
    {
        var prompt = RowanSystemPrompt();

        Assert.Contains("One deed, not several", prompt, StringComparison.Ordinal);
        Assert.Contains("I attack Vark while shouting a warning", prompt, StringComparison.Ordinal);
        Assert.Contains("I attack Vark and steal his purse", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_states_a_demand_is_only_speech_and_binds_nobody()
    {
        var prompt = RowanSystemPrompt();

        Assert.Contains("Demanding an enemy yield", prompt, StringComparison.Ordinal);
        Assert.Contains("it binds nobody", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_prompt_states_everything_in_the_room_is_already_within_reach()
    {
        var prompt = RowanSystemPrompt();

        Assert.Contains("Nothing here is out of reach", prompt, StringComparison.Ordinal);
        Assert.Contains("already within reach", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("end_turn", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_states_a_looted_weapon_is_a_trophy_never_something_to_fight_with()
    {
        var prompt = RowanSystemPrompt();

        Assert.Contains("trophy", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gain nothing from it in a fight", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_prompt_states_a_weapon_only_surrender_offer_is_a_complete_offer()
    {
        var prompt = RowanSystemPrompt();

        Assert.Contains("I hold my sabre out by the flat", prompt, StringComparison.Ordinal);
    }

    // A live run had Rowan spend seven of eleven turns guarding a companion at full health while he himself
    // sat at 2 health, scared, with an unused healing flask — his persona names guarding as his defining
    // trait and the ability never runs dry (MaxUsesPerEncounter: null), with nothing pushing back against
    // spending it on someone who is not actually in danger. Bracing already carries this caution
    // ("If you find yourself bracing turn after turn..."); guarding an ally had none.
    [Fact]
    public void The_prompt_states_protecting_a_companion_who_is_not_in_danger_protects_nobody()
    {
        var prompt = RowanSystemPrompt();

        Assert.Contains("protects nobody", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Your own wounds do not close because you spent it on someone else", prompt, StringComparison.Ordinal);
    }
}
