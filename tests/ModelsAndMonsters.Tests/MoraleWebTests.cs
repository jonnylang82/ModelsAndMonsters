using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;
using ModelsAndMonsters.Tracing;
using ModelsAndMonsters.Web;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// What the observer UI is handed for morale. The React rendering is out of scope for a C# test, but the
/// snapshot and the event stream it renders from are exactly this.
/// </summary>
public sealed class MoraleWebTests
{
    [Fact]
    public void The_state_snapshot_carries_each_characters_exact_fear_and_visible_scared_state()
    {
        // The UI is an experiment artefact, so it is allowed the figure no opponent in the fiction sees.
        var state = TestWorld.V07State(exitOpen: false,
            TestWorld.RowanV07() with { Fear = 4 }, TestWorld.ElaraV07() with { Fear = 1 },
            TestWorld.VarkV07(), TestWorld.SkritV07());
        var engine = new GameEngine(state, new SeededRng(1), CombatRules.Default);

        var dto = StateDto.From(engine.State);

        var rowan = dto.Characters.Single(c => c.Name == "Rowan");
        Assert.Equal(4, rowan.Fear);
        Assert.Equal(FearRules.Maximum, rowan.MaxFear);
        Assert.True(rowan.Scared);

        var elara = dto.Characters.Single(c => c.Name == "Elara");
        Assert.Equal(1, elara.Fear);
        Assert.False(elara.Scared);

        // The Scared status rides the same snapshot as a badge, so the card can show both.
        Assert.Contains(rowan.Statuses, s => s.Kind == nameof(StatusEffectKind.Scared));
        Assert.DoesNotContain(elara.Statuses, s => s.Kind == nameof(StatusEffectKind.Scared));
    }

    [Fact]
    public void A_character_who_leaves_the_fight_keeps_their_final_fear_on_the_card()
    {
        var state = TestWorld.V07State(exitOpen: false,
            TestWorld.RowanV07(), TestWorld.ElaraV07(),
            TestWorld.VarkV07() with { Disposition = CharacterDisposition.Surrendered, Fear = 5 },
            TestWorld.SkritV07() with { Disposition = CharacterDisposition.Dead, Health = 0, Fear = 3 });

        var dto = StateDto.From(state);

        Assert.Equal(5, dto.Characters.Single(c => c.Name == "Vark").Fear);
        Assert.Equal(3, dto.Characters.Single(c => c.Name == "Skrit").Fear);

        // The number is kept; the visible state is not. Somebody who has yielded or fallen is out of the
        // fight, the engine has swept their Scared status away, and the derived flag agrees with that rather
        // than leaving the card claiming a badge that is no longer there.
        Assert.False(dto.Characters.Single(c => c.Name == "Vark").Scared);
        Assert.False(dto.Characters.Single(c => c.Name == "Skrit").Scared);
    }

    [Fact]
    public void The_snapshot_carries_the_outnumbered_latch_so_the_card_can_explain_the_odds()
    {
        var engine = new GameEngine(
            TestWorld.V07State(exitOpen: false, TestWorld.RowanV07(), TestWorld.VarkV07(), TestWorld.SkritV07()),
            new SeededRng(1), CombatRules.Default);

        var dto = StateDto.From(engine.State);

        Assert.True(dto.Characters.Single(c => c.Name == "Rowan").Outnumbered);
        Assert.False(dto.Characters.Single(c => c.Name == "Vark").Outnumbered);
    }

    [Fact]
    public void An_attack_event_carries_its_quality_so_a_critical_hit_reads_distinctly()
    {
        var published = new List<UiEvent>();
        var sink = new WebTraceSink(published.Add);

        sink.Write(new TraceEvent
        {
            RunId = "morale-web-test",
            Sequence = 1,
            Timestamp = DateTimeOffset.UnixEpoch,
            EventType = TraceEventType.EngineAction,
            Actor = "Rowan",
            Round = 1,
            Turn = 1,
            Data = new EngineActionPayload
            {
                ActionType = "attack_character",
                Action = new AttackCharacterAction("Rowan", "Vark", "Longsword"),
                Accepted = true,
                Outcome = new AttackOutcome
                {
                    AttackerId = TestWorld.RowanId,
                    AttackerName = "Rowan",
                    TargetId = TestWorld.VarkId,
                    TargetName = "Vark",
                    WeaponName = "Longsword",
                    WeaponDamage = 5,
                    TargetArmour = 2,
                    HitRoll = 10,
                    HitChance = 100,
                    Hit = true,
                    GlancingChance = 25,
                    CriticalChance = 25,
                    Quality = AttackQuality.Critical,
                    BaseDamage = 3,
                    DamageDealt = 6,
                    TargetHealthBefore = 12,
                    TargetHealthAfter = 6,
                    TargetMaxHealth = 12,
                    TargetDied = false
                },
                StateBefore = TestWorld.V07State(exitOpen: false),
                StateAfter = TestWorld.V07State(exitOpen: false)
            }
        });

        var attack = Assert.Single(published, e => e.Type == "attack");
        var dto = Assert.IsType<AttackDto>(attack.Payload);
        Assert.Equal("Critical", dto.Quality);
        Assert.True(dto.Critical);
        Assert.False(dto.Glancing);
    }

    [Fact]
    public void Fear_changes_threats_and_steadyings_each_reach_the_transcript_as_their_own_event()
    {
        var published = new List<UiEvent>();
        var sink = new WebTraceSink(published.Add);

        sink.Write(Event(TraceEventType.FearChanged, new FearChangedPayload
        {
            CharacterId = TestWorld.VarkId,
            CharacterName = "Vark",
            Team = "Goblins",
            Cause = "Intimidated",
            CauseDetail = "Rowan's open threat told",
            Delta = 1,
            FearBefore = 2,
            FearAfter = 3,
            Absorbed = false,
            ScaredTransition = "BecameScared",
            ScaredAfter = true,
            RngConsulted = true,
            Round = 2,
            Turn = 6,
            WorldVersion = 9
        }));

        sink.Write(Event(TraceEventType.IntimidationAttempted, new IntimidationAttemptedPayload
        {
            AttemptId = "intimidation-1",
            ActorId = TestWorld.RowanId,
            ActorName = "Rowan",
            TargetId = TestWorld.VarkId,
            TargetName = "Vark",
            BaseChance = 35,
            Modifiers = ["target-fear +20"],
            ModifierSources = [],
            EffectiveChance = 55,
            Roll = 12,
            Succeeded = true,
            TargetFearBefore = 2,
            TargetFearAfter = 3,
            ScaredTransition = "BecameScared",
            Round = 2,
            Turn = 6,
            WorldVersionBefore = 8,
            WorldVersionAfter = 9
        }));

        sink.Write(Event(TraceEventType.AllySteadied, new AllySteadiedPayload
        {
            ActorId = TestWorld.SkritId,
            ActorName = "Skrit",
            TargetId = TestWorld.VarkId,
            TargetName = "Vark",
            TargetFearBefore = 3,
            TargetFearAfter = 2,
            NoEffect = false,
            ScaredTransition = "RecoveredFromScared",
            Round = 3,
            Turn = 10,
            WorldVersionBefore = 12,
            WorldVersionAfter = 13
        }));

        // Three distinct kinds, because a point of fear moving, a threat landing and a companion steadying
        // somebody are three different things and must never read alike.
        Assert.Equal(["fearChanged", "intimidation", "allySteadied"], published.Select(e => e.Type));
    }

    private static TraceEvent Event(TraceEventType type, object payload) => new()
    {
        RunId = "morale-web-test",
        Sequence = 1,
        Timestamp = DateTimeOffset.UnixEpoch,
        EventType = type,
        Actor = "test",
        Round = 1,
        Turn = 1,
        Data = payload
    };
}
