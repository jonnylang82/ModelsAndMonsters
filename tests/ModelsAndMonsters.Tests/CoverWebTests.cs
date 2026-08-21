using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;
using ModelsAndMonsters.Tracing;
using ModelsAndMonsters.Web;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The observer UI re-renders environmental cover (v0.9) from the authoritative <see cref="StateDto"/>
/// snapshot and from the <see cref="UiEvent"/>/<see cref="WebTraceSink"/> pair that turns trace events into
/// the structured events the live cards and transcript consume. Verifying the projection and the sink's
/// dispatch verifies the data the React rendering (out of scope for a C# test, per the same convention
/// <c>DispositionWebTests</c> and <c>MoraleWebTests</c> already establish) actually receives.
/// </summary>
public sealed class CoverWebTests
{
    [Fact]
    public void The_state_snapshot_carries_the_cover_object_intact_and_unoccupied_before_anything_happens()
    {
        var dto = StateDto.From(TestWorld.TwoVsTwoStateWithCover());

        var cover = Assert.Single(dto.Cover);
        Assert.Equal(TestWorld.WorkbenchId, cover.Id);
        Assert.Equal("Overturned Mill Workbench", cover.Name);
        Assert.Equal("Intact", cover.State);
        Assert.Equal(1, cover.Capacity);
        Assert.Null(cover.Occupant);
        Assert.Equal(2, cover.MaximumDurability);
        Assert.Equal(2, cover.CurrentDurability);
        Assert.Equal(-20, cover.HitChanceModifier);
        Assert.Equal(2, cover.Armour);

        // The cover object never rides the ordinary Objects list (that would double-render it as a container).
        Assert.DoesNotContain(dto.Objects, o => o.Id == TestWorld.WorkbenchId);
    }

    [Fact]
    public void The_state_snapshot_names_the_occupant_by_name_once_cover_is_taken()
    {
        var engine = TestWorld.EngineWithCover(characters: [TestWorld.Rowan(), TestWorld.Vark()]);
        Assert.True(engine.Execute(new TakeCoverAction("Rowan", TestWorld.WorkbenchId)).Accepted);

        var dto = StateDto.From(engine.State);

        var cover = Assert.Single(dto.Cover);
        Assert.Equal("Rowan", cover.Occupant);
        Assert.Equal("Intact", cover.State);
    }

    [Fact]
    public void The_state_snapshot_reads_damaged_and_then_destroyed_as_durability_falls()
    {
        var engine = TestWorld.EngineWithCover(characters: [TestWorld.Rowan(), TestWorld.Vark()]);

        Assert.True(engine.Execute(new DamageEnvironmentalObjectAction("Rowan", TestWorld.WorkbenchId)).Accepted);

        var destroyed = Assert.Single(StateDto.From(engine.State).Cover);
        Assert.Equal("Destroyed", destroyed.State);
        Assert.Equal(0, destroyed.CurrentDurability);
    }

    [Fact]
    public void The_web_trace_sink_publishes_a_cover_state_alongside_an_attack_carrying_the_interception()
    {
        var published = new List<UiEvent>();
        var sink = new WebTraceSink(published.Add);

        var target = TestWorld.Elara(hitChance: 100);
        var engine = TestWorld.EngineWithCover(
            rng: new ScriptedRng(60), rules: CombatRules.NoGlancing,
            characters: [TestWorld.Rowan(hitChance: 70), target]);
        Assert.True(engine.Execute(new TakeCoverAction("Elara", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Elara", "Longsword"));
        Assert.True(result.Accepted);
        var attack = (AttackOutcome)result.Outcome!;
        Assert.True(attack.InterceptedByCover);

        sink.Write(new TraceEvent
        {
            Sequence = 1,
            Timestamp = DateTimeOffset.UnixEpoch,
            RunId = "cover-web-test",
            Round = 1,
            Turn = 1,
            Actor = "Rowan",
            EventType = TraceEventType.EngineAction,
            Data = new EngineActionPayload
            {
                ActionType = "attack_character",
                Action = new AttackCharacterAction("Rowan", "Elara", "Longsword"),
                Accepted = true,
                Outcome = attack,
                OutcomeSummary = attack.Summary,
                StateBefore = result.StateBefore,
                StateAfter = result.StateAfter
            }
        });

        var attackEvent = Assert.Single(published, e => e.Type == "attack");
        var attackDto = Assert.IsType<AttackDto>(attackEvent.Payload);
        Assert.True(attackDto.Intercepted);
        Assert.Equal(TestWorld.WorkbenchId, attackDto.CoverId);
        Assert.Equal("Overturned Mill Workbench", attackDto.CoverName);
        Assert.False(attackDto.Hit);

        // The interception also gets its own transcript-distinct line — a cover interception must never read
        // like an ordinary miss, so it is a different published kind, not folded into the attack line alone.
        Assert.Single(published, e => e.Type == "coverDamaged");
    }
}
