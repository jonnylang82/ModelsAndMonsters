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

    /// <summary>A character escaped through an exit and left the encounter. A focused, semantic companion to <see cref="DispositionChanged"/>.</summary>
    CharacterEscaped,

    /// <summary>The team terminal condition was evaluated after an accepted action or a turn.</summary>
    TeamOutcomeEvaluated,

    Narration,
    NarrationDelivered,

    /// <summary>A harness protection limit stopped a loop.</summary>
    HarnessLimitReached,

    RunCompleted,
    RunFailed
}
