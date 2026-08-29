namespace ModelsAndMonsters.Tracing;

/// <summary>
/// The vocabulary of trace events. Serialised as strings so trace files stay readable.
/// </summary>
public enum TraceEventType
{
    RunStarted,
    ScenarioSeeded,
    RoundStarted,
    TurnStarted,
    TurnEnded,

    /// <summary>An actor's turn was skipped because it was not alive; no model call was made.</summary>
    TurnSkipped,

    /// <summary>Everything sent into our <c>IChatClient</c> abstraction for one logical call.</summary>
    ModelRequest,

    /// <summary>Everything the model returned for that call.</summary>
    ModelResponse,

    /// <summary>A model call threw.</summary>
    ModelError,

    /// <summary>
    /// The model stopped because it ran out of output budget rather than because it had finished.
    /// Anything it was part-way through — most importantly a tool call — may be missing or malformed.
    /// </summary>
    ModelResponseTruncated,

    /// <summary>
    /// A request came close enough to the agent's context window that the provider may have discarded
    /// the oldest messages. Ollama does this silently, so it has to be inferred and reported.
    /// </summary>
    ContextWindowSaturated,

    /// <summary>
    /// A character's request filled its context window, so the harness shed history to make room before
    /// asking again — rather than appending another nudge to a request that had none.
    /// </summary>
    ContextRoomReclaimed,

    /// <summary>The application inspected a requested tool call and decided how to dispatch it.</summary>
    ToolCallDispatched,

    /// <summary>The result the application handed back to the model for a tool call.</summary>
    ToolCallResult,

    /// <summary>A tool call could not be dispatched (unknown tool, malformed arguments, handler threw).</summary>
    ToolCallError,

    /// <summary>A tool call the model wrote as prose was parsed and dispatched as if it had been called.</summary>
    ToolCallRecovered,

    /// <summary>
    /// A character wrote a spoken line in prose (a quoted utterance) instead of calling <c>say</c>. Recorded
    /// so behavioural analysis can see the character tried to communicate; the words are never delivered —
    /// the character is nudged to call <c>say</c> properly instead.
    /// </summary>
    UnstructuredSpeechAttempt,

    /// <summary>
    /// A character had already used its voice this turn, so a further line was not delivered. This is a
    /// RULE OF THE WORLD — a character gets so many breaths in a turn — and not a guard against a
    /// misbehaving model, which is why it is not a <see cref="HarnessLimitReached"/>. Recording it as one
    /// made a well-behaved run look badly behaved: three of a live run's four "harness limits" were this,
    /// working exactly as designed.
    /// </summary>
    SpeechNotHeard,

    /// <summary>
    /// A prose reply that carried no tool call was read by the stateless intent parser into the say / ask /
    /// take_action calls it implied. Recorded with the raw prose so the parser's reading stays auditable
    /// against what the character actually wrote.
    /// </summary>
    IntentParsed,

    /// <summary>
    /// A character's older turns were folded into a running summary to keep their history within budget.
    /// Recorded with the token estimate before and after and the summary text, so the compression is
    /// auditable and its effect on context size is visible.
    /// </summary>
    HistorySummarised,

    CharacterQuestion,
    DungeonMasterAnswer,

    /// <summary>
    /// A character spoke aloud to the room. Records the verbatim message and every living recipient, so
    /// the delivery of public speech can be verified independently of the narration channel.
    /// </summary>
    CharacterSpeech,

    /// <summary>A character deliberately chose to do nothing with its turn.</summary>
    CharacterPassed,

    /// <summary>
    /// An attempt to open a container or take an item from one, accepted or rejected. Complements the
    /// generic <see cref="EngineAction"/> row with the object-specific ids and version transition.
    /// </summary>
    ObjectInteraction,

    /// <summary>A character examined an object closely, with what (if anything) it discovered.</summary>
    ObjectInspected,

    /// <summary>
    /// A discoverable fact was minted in the knowledge ledger for the first time, with its stable id and
    /// the world version it describes. Emitted once per fact, however many characters later learn it.
    /// </summary>
    KnowledgeFactCreated,

    /// <summary>
    /// A character learned a fact — its full detail plus how, when and at what world version it was
    /// observed. This is the record from which per-character knowledge and the discovery timeline are
    /// reconstructed. Hearsay never produces one of these; only first-hand or public knowledge does.
    /// </summary>
    KnowledgeFactLearned,

    /// <summary>
    /// A private observation delivered to a single character — an inspection result, or the contents seen
    /// on opening — recording its recipient, related facts and world version. It is never delivered to
    /// anyone else.
    /// </summary>
    PrivateObservationDelivered,

    /// <summary>
    /// A publicly observable fact delivered to everyone alive in the room — a visible item being carried
    /// off — recording all recipients, related facts and world version.
    /// </summary>
    PublicFactDelivered,

    /// <summary>The harness corrected a tool argument the Dungeon Master got structurally wrong.</summary>
    AdjudicationCorrected,

    /// <summary>The DM's ruling on a character's natural-language intent.</summary>
    DmAdjudication,

    /// <summary>How a natural-language target reference was resolved to a specific character (or not).</summary>
    TargetResolved,

    /// <summary>A structured action submitted to the engine, with before/after state.</summary>
    EngineAction,

    /// <summary>One complete random draw the engine made, with full context for replay and comparison.</summary>
    RngDraw,

    /// <summary>
    /// A character's disposition changed — active to surrendered, escaped or dead. Records the full before
    /// and after, the cause, and the public recipients who learned of it, so a character leaving active
    /// combat is auditable independently of the action that caused it.
    /// </summary>
    DispositionChanged,

    /// <summary>
    /// An attempt to open an exit or escape through one, accepted or rejected. Complements the generic
    /// <see cref="EngineAction"/> row with the exit-specific id, the open/closed transition and the
    /// validation result.
    /// </summary>
    ExitInteraction,

    /// <summary>
    /// An inventory transfer attempt — give, drop or steal — accepted or rejected. Complements the generic
    /// <see cref="EngineAction"/> row with the requested and authoritative bindings, ownership before and
    /// after, turn consumption, and the RNG/rulebook linkage.
    /// </summary>
    InventoryInteraction,

    /// <summary>
    /// A successful item movement recorded as a provenance event: the item, its previous and new
    /// owner/location, the action, the acting character, round and turn, and any RNG or rulebook linkage.
    /// Provenance is an event history, not a second source of ownership; together these reconstruct an
    /// item's journey through the encounter.
    /// </summary>
    ItemProvenance,

    /// <summary>
    /// One automatic rulebook consultation for a <c>take_action</c> request: the deterministic rule
    /// retrieval, the stateless resolver call and its structured guidance, validation, cache result, sizes
    /// and the candidate engine tools the guidance narrowed the Dungeon Master down to.
    /// </summary>
    RulebookConsultation,

    /// <summary>A character surrendered and left active combat. A focused, semantic companion to <see cref="DispositionChanged"/>.</summary>
    CharacterSurrendered,

    /// <summary>
    /// A surrender offer was made: the offerer, the named recipient, the exact enforceable terms, and the
    /// public speech that accompanied the plea. Nothing has moved — this records the proposal, not a transfer.
    /// </summary>
    SurrenderOfferMade,

    /// <summary>
    /// A surrender offer left Pending — accepted, rejected by a hostile act, expired unanswered, or
    /// invalidated because it could no longer be enforced — with the cause and how long it stood.
    /// </summary>
    SurrenderOfferResolved,

    /// <summary>
    /// A surrender was DEMANDED: the demander, the target told to yield, and the public ultimatum. Termless
    /// and pressure-only — nothing moved, nobody is bound; it records the ultimatum, not a transfer.
    /// </summary>
    SurrenderDemandMade,

    /// <summary>
    /// A surrender demand left Pending — lapsed after the target completed a turn without yielding, or
    /// invalidated because a party left active play — with the cause.
    /// </summary>
    SurrenderDemandResolved,

    /// <summary>
    /// A surrender agreement was struck: exactly what transferred, whether a weapon was forfeited, and the
    /// speech that accompanied the offer. The durable evidence of a negotiated surrender.
    /// </summary>
    SurrenderAgreementRecorded,

    /// <summary>An ability use was attempted: the ability, its target, whether it was accepted, and the charges left.</summary>
    AbilityUsed,

    /// <summary>A status effect was put on a character, with its source, modifier, expiry rule and visibility.</summary>
    StatusApplied,

    /// <summary>A status effect did its one job and was removed, naming exactly what it did.</summary>
    StatusConsumed,

    /// <summary>A status effect reached its expiry rule unused and was removed.</summary>
    StatusExpired,

    /// <summary>A status effect was removed because it could no longer be sustained — its holder or source left the fight.</summary>
    StatusRemoved,

    /// <summary>
    /// A guard relationship moved an attack from its intended target onto the guardian. Recorded with both
    /// targets so it is provable that no second attack roll was made.
    /// </summary>
    AttackRedirected,

    /// <summary>
    /// One character's fear moved, with its cause, the value either side of the clamp, and any crossing of
    /// the public Scared threshold. Emitted for every change including a deterministic one, so morale is
    /// never a number that moves for reasons the record cannot show.
    /// </summary>
    FearChanged,

    /// <summary>
    /// One attempt to frighten an opponent: the whole modifier derivation, the raw roll, the outcome, and the
    /// fear it moved. Companion to the <see cref="RngDraw"/> the same attempt produced.
    /// </summary>
    IntimidationAttempted,

    /// <summary>One character spending their turn steadying an ally, with the fear it shed. No randomness.</summary>
    AllySteadied,

    /// <summary>
    /// The deterministic <c>AnswerFacts</c> projection built for one character's question: what the Dungeon
    /// Master was given to rephrase, and what was deliberately withheld from it.
    /// </summary>
    AnswerFactsProjected,

    /// <summary>
    /// Output a model produced after its turn had already been resolved — a further tool call or trailing
    /// action text — discarded without reaching the world, the transcript or the knowledge ledger.
    /// </summary>
    PostResolutionOutputDiscarded,

    /// <summary>A character escaped through an exit and left the encounter. A focused, semantic companion to <see cref="DispositionChanged"/>.</summary>
    CharacterEscaped,

    /// <summary>The team terminal condition was evaluated after an accepted action or a turn.</summary>
    TeamOutcomeEvaluated,

    /// <summary>
    /// An attempt to take or leave environmental cover, accepted or rejected (v0.9). Complements the generic
    /// <see cref="EngineAction"/> row with the cover-specific id, the occupancy transition and capacity state.
    /// </summary>
    CoverInteraction,

    /// <summary>
    /// An attack whose target was sheltering behind environmental cover, whatever the result (v0.9).
    /// Complements the attack's own <see cref="EngineAction"/> row with the pre-cover and covered effective
    /// hit chances, the classification (direct hit, cover interception, or ordinary miss), and any durability
    /// effect — so a covered attack is provably distinguishable from an ordinary one in the trace alone.
    /// </summary>
    AttackAgainstCover,

    /// <summary>
    /// An attempt to deliberately damage an environmental object, accepted or rejected (v0.9). Complements the
    /// generic <see cref="EngineAction"/> row with the object-specific weapon/armour arithmetic and durability
    /// transition.
    /// </summary>
    EnvironmentalObjectDamaged,

    /// <summary>
    /// An environmental object's durability reached zero, from a cover interception or deliberate damage
    /// (v0.9). A focused, semantic companion to whichever row caused it — the same relationship
    /// <see cref="CharacterSurrendered"/> and <see cref="CharacterEscaped"/> have to <see cref="DispositionChanged"/>.
    /// </summary>
    EnvironmentalObjectDestroyed,

    Narration,
    NarrationDelivered,

    /// <summary>
    /// A short dramatic story of the finished encounter was generated from its public transcript alone.
    /// Recorded with the transcript's size, how many public entries were included versus omitted for length,
    /// and the story text itself, so the retelling is auditable against the record it was built from.
    /// </summary>
    EncounterStoryGenerated,

    /// <summary>A harness protection limit stopped a loop.</summary>
    HarnessLimitReached,

    RunCompleted,
    RunFailed
}
