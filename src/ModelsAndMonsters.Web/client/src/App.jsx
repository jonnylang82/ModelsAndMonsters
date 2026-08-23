import React, { useCallback, useEffect, useRef, useState } from 'react'

const API = 'http://localhost:5170'

// How each streamed event renders in the transcript. Structured events (state/turnStarted) are handled
// separately and drive the cards, not the log.
const LOG_KINDS = new Set([
  'runHeader', 'notice', 'round', 'roundSummary', 'narration', 'asks', 'acts', 'speaks',
  'passes', 'refused', 'privateObservation', 'attack',
  'surrendered', 'escaped', 'exitOpened', 'gave', 'dropped', 'stole', 'completed', 'ending',
  // v0.7: negotiation, abilities, statuses. Deliberately distinct kinds — an offer and an accepted
  // surrender must never read alike, because only one of them ends a fight.
  'surrenderOffered', 'surrenderOfferSettled', 'surrenderAccepted',
  'abilityUsed', 'statusChanged', 'attackRedirected', 'defendReduced',
  // v0.8: morale. A point of fear moving, a nerve breaking, a threat and a steadying word are four
  // different things and read as four different lines.
  'fearChanged', 'intimidation', 'allySteadied',
  // v0.9: environmental cover. Taking or leaving cover, cover taking a blow, and a deliberate strike on an
  // object are three different things — none of them reads like an attack on a person.
  'coverOccupancyChanged', 'coverDamaged', 'environmentalObjectDamaged'
])

export default function App() {
  const [runId, setRunId] = useState(null)
  const [status, setStatus] = useState('idle') // idle | running | done
  const [characters, setCharacters] = useState([])
  const [objects, setObjects] = useState([])
  const [exits, setExits] = useState([])
  const [ground, setGround] = useState([])
  const [cover, setCover] = useState([])
  const [pendingOffers, setPendingOffers] = useState([])
  const [settledOffers, setSettledOffers] = useState([])
  const [agreements, setAgreements] = useState([])
  const [turn, setTurn] = useState(null)
  const [round, setRound] = useState(null)
  const [log, setLog] = useState([])
  const [story, setStory] = useState(null)
  const [showStory, setShowStory] = useState(false)
  const [model, setModel] = useState(null)
  const [provider, setProvider] = useState(null)
  const [usage, setUsage] = useState(null)
  const esRef = useRef(null)
  const bottomRef = useRef(null)

  useEffect(() => { bottomRef.current?.scrollIntoView({ behavior: 'smooth' }) }, [log])
  useEffect(() => () => esRef.current?.close(), [])

  const handle = useCallback((evt) => {
    const p = evt.payload || {}
    if (evt.type === 'state') {
      setCharacters(p.characters || [])
      setObjects(p.objects || [])
      setExits(p.exits || [])
      setGround(p.ground || [])
      setCover(p.cover || [])
      setPendingOffers(p.pendingOffers || [])
      setSettledOffers(p.settledOffers || [])
      setAgreements(p.agreements || [])
      return
    }
    if (evt.type === 'turnStarted') { setTurn(p.character); return }
    if (evt.type === 'config') { setModel(p.model); setProvider(p.provider); return }
    if (evt.type === 'usage') { setUsage(p); return }
    // The full text lives in the popover, not the scrolling transcript — LOG_KINDS deliberately never
    // lists 'story' itself; a short marker line stands in for it (a first pass at this popped the story
    // straight into the log and it silently vanished, because handling it here never happened at all).
    if (evt.type === 'story') {
      setStory(p.text)
      setShowStory(true)
      setLog((l) => [...l, { type: 'storyReady', payload: {} }])
      return
    }
    if (evt.type === 'round') setRound(p.round)
    if (evt.type === 'completed') setStatus('done')
    if (LOG_KINDS.has(evt.type)) setLog((l) => [...l, evt])
  }, [])

  const start = useCallback(async () => {
    esRef.current?.close()
    setCharacters([]); setObjects([]); setExits([]); setGround([]); setCover([])
    setPendingOffers([]); setSettledOffers([]); setAgreements([])
    setTurn(null); setRound(null); setLog([]); setStatus('running')
    setStory(null); setShowStory(false); setUsage(null)
    const res = await fetch(`${API}/api/runs`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}'
    })
    const { runId } = await res.json()
    setRunId(runId)
    const es = new EventSource(`${API}/api/runs/${runId}/events`)
    esRef.current = es
    es.onmessage = (e) => handle(JSON.parse(e.data))
    es.onerror = () => { es.close(); setStatus((s) => (s === 'running' ? 'done' : s)) }
  }, [handle])

  const cancel = useCallback(() => {
    if (runId) fetch(`${API}/api/runs/${runId}/cancel`, { method: 'POST' })
  }, [runId])

  return (
    <div className="app">
      <header className="top">
        <h1>🎲 Models &amp; Monsters 🧌 <span className="version">v0.10</span></h1>
        <div className="controls">
          {round != null && <span className="round">Round {round}</span>}
          <span className={`status ${status}`}>{status}</span>
          {story && <button onClick={() => setShowStory(true)} className="ghost">📖 Read the tale</button>}
          <button onClick={start} disabled={status === 'running'}>Start run</button>
          <button onClick={cancel} disabled={status !== 'running'} className="ghost">Cancel</button>
        </div>
      </header>

      <section className="cards">
        {characters.length === 0 && <div className="empty">Press “Start run” to begin an encounter.</div>}
        {characters.map((c) => <CharacterCard key={c.id} c={c} active={c.name === turn} />)}
      </section>

      {(objects.length > 0 || exits.length > 0 || ground.length > 0 || cover.length > 0) && (
        <section className="objects">
          {objects.map((o) => (
            <div key={o.id} className={`obj ${o.isContainer && o.isOpen ? 'open' : ''}`}>
              <span className="obj-name">{o.name}</span>
              {o.isContainer && <span className="obj-state">{o.isOpen ? 'open' : 'closed'}</span>}
            </div>
          ))}
          {ground.length > 0 && (
            <div className="obj ground open">
              <span className="obj-name">🟫 On the floor</span>
              <span className="obj-state">{ground.join(', ')}</span>
            </div>
          )}
          {exits.map((e) => (
            <div key={e.id} className={`obj exit ${e.isOpen ? 'open' : ''}`}>
              <span className="obj-name">🚪 {e.name}</span>
              <span className="obj-state">{e.isOpen ? 'Open' : 'Closed'}</span>
            </div>
          ))}
          {cover.map((c) => (
            <div key={c.id} className={`obj cover ${c.state.toLowerCase()}`}>
              <span className="obj-name">🛖 {c.name}</span>
              <span className="obj-state">
                {c.state}{c.state !== 'Destroyed' ? ` (${c.currentDurability}/${c.maximumDurability})` : ''}
                {c.occupant ? ` · behind it: ${c.occupant}` : ' · empty'}
              </span>
            </div>
          ))}
        </section>
      )}

      {(pendingOffers.length > 0 || agreements.length > 0 || settledOffers.length > 0) && (
        <section className="negotiation">
          {pendingOffers.map((o) => (
            <div key={o.id} className="offer pending">
              <span className="offer-head">🏳️ {o.offerer} → {o.recipient}</span>
              <span className="offer-terms">offers: {o.terms}</span>
              <span className="offer-note">
                awaiting {o.recipient}’s answer · nothing transferred · {o.offerer} still a target · lapses at end of their turn
              </span>
            </div>
          ))}
          {agreements.map((a) => (
            <div key={a.id} className="offer accepted">
              <span className="offer-head">🤝 {a.offerer} yielded to {a.acceptedBy}</span>
              <span className="offer-terms">
                tribute: {a.transferredItems.length ? a.transferredItems.join(', ') : 'none'}
                {a.forfeitedWeapon ? ` · weapon forfeited: ${a.forfeitedWeapon}` : ' · no weapon promised'}
              </span>
              <span className="offer-note">binding · {a.offerer} disarmed and out of the fight (round {a.acceptedRound})</span>
            </div>
          ))}
          {settledOffers.map((o) => (
            <div key={o.id} className={`offer settled ${o.state.toLowerCase()}`}>
              <span className="offer-head">{o.offerer} → {o.recipient}</span>
              <span className="offer-terms">{o.terms}</span>
              <span className="offer-note">{o.state.toLowerCase()}{o.resolutionCause ? ` — ${o.resolutionCause}` : ''} · nothing transferred</span>
            </div>
          ))}
        </section>
      )}

      <section className="stream">
        {log.map((e, i) => <LogLine key={i} evt={e} />)}
        <div ref={bottomRef} />
      </section>

      {showStory && story && <StoryPopover text={story} onClose={() => setShowStory(false)} />}

      <footer className="statusbar">
        <span className="sb-model" title="Default agent model">
          🧠 {model || '—'}{provider ? <span className="sb-provider"> · {provider}</span> : null}
        </span>
        <span className="sb-tokens">
          {usage ? (
            <>
              <span className="sb-tok"><span className="sb-k">in</span> {fmtTokens(usage.input)}</span>
              <span className="sb-tok"><span className="sb-k">out</span> {fmtTokens(usage.output)}</span>
              <span className="sb-tok sb-total"><span className="sb-k">total</span> {fmtTokens(usage.total)}</span>
              <span className="sb-calls">{usage.calls} call{usage.calls === 1 ? '' : 's'}</span>
            </>
          ) : (
            <span className="sb-tok sb-idle">no tokens yet</span>
          )}
        </span>
      </footer>
    </div>
  )
}

// Thousands-separated token counts, so a six-figure total stays readable at a glance.
function fmtTokens(n) {
  return (n ?? 0).toLocaleString()
}

// The encounter's story, once it is written: a popover rather than another line in the scrolling
// transcript, since a short story deserves to be read as one thing rather than found buried in the log.
// Clicking the backdrop or pressing Escape closes it; clicking the card itself does not.
function StoryPopover({ text, onClose }) {
  useEffect(() => {
    const onKey = (e) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="story-backdrop" onClick={onClose}>
      <div className="story-card" onClick={(e) => e.stopPropagation()}>
        <button className="story-close" onClick={onClose} aria-label="Close">✕</button>
        <div className="story-heading">📖 The tale of it</div>
        <div className="story-body">{renderStoryMarkdown(text)}</div>
      </div>
    </div>
  )
}

// Minimal, dependency-free markdown for the story text: headings, paragraphs and **bold**/*italic*
// emphasis — exactly the subset the summariser's own prompt asks for, and nothing more. Deliberately
// never uses dangerouslySetInnerHTML: the story is unmoderated model output, so every element it becomes
// is a real React node built from parsed text, never raw HTML the model could have slipped something into.
function renderStoryMarkdown(text) {
  const blocks = []
  let paragraph = []
  const flushParagraph = () => {
    if (paragraph.length) {
      blocks.push({ type: 'p', text: paragraph.join(' ') })
      paragraph = []
    }
  }

  for (const rawLine of (text || '').replace(/\r\n/g, '\n').split('\n')) {
    const line = rawLine.trim()
    const heading = line.match(/^(#{1,6})\s+(.*)/)
    if (heading) {
      flushParagraph()
      blocks.push({ type: 'h', level: heading[1].length, text: heading[2] })
    } else if (line === '') {
      flushParagraph()
    } else {
      paragraph.push(line)
    }
  }
  flushParagraph()

  return blocks.map((block, i) => {
    if (block.type === 'h') {
      // Keeps headings modest inside a card that already has its own "The tale of it" title — a ## becomes
      // an h4, not a page-level h2.
      const Tag = `h${Math.min(block.level + 2, 6)}`
      return <Tag key={i} className="story-section-heading">{renderInline(block.text)}</Tag>
    }
    return <p key={i} className="story-paragraph">{renderInline(block.text)}</p>
  })
}

// **bold** and *italic*, applied left to right and not nested — the only inline emphasis a story ever uses.
function renderInline(text) {
  return text.split(/(\*\*[^*]+\*\*|\*[^*]+\*)/g).map((part, i) => {
    if (part.startsWith('**') && part.endsWith('**')) return <strong key={i}>{part.slice(2, -2)}</strong>
    if (part.startsWith('*') && part.endsWith('*')) return <em key={i}>{part.slice(1, -1)}</em>
    return part
  })
}

// Status badges. Each one is a mechanical fact the engine is enforcing, so it is shown as its own badge
// rather than folded into prose that a reader has to infer it from.
const STATUS_BADGE = {
  Guarding: (s) => `🛡 guarding${s.partnerName ? ` ${s.partnerName}` : ''}`,
  Guarded: (s) => `🛡 guarded by ${s.source}`,
  Rallied: (s) => `📣 rallied ${s.modifier >= 0 ? '+' : ''}${s.modifier}`,
  OffBalance: (s) => `💫 off balance ${s.modifier >= 0 ? '+' : ''}${s.modifier}`,
  Defending: () => '🪨 guard up (−1 next hit)',
  // Fear itself is shown by the meter below; this badge is the public fact — that everyone in the room
  // can see it — which is a different claim from the number.
  Scared: () => '😰 scared',
  InCover: () => '🛖 in cover',
  // Reeling from a stunning blow: the whole effect is the next turn, lost.
  Stunned: () => '💥 stunned (loses next turn)'
}

// Disposition drives how a card reads. Only the dead are "fallen"; a surrendered or escaped character is
// subdued and labelled but never shown as killed. An active card (still fighting) can also be highlighted
// as the one whose turn it is.
const DISPOSITION_LABEL = { Surrendered: 'surrendered', Escaped: 'escaped', Dead: 'fallen' }

function CharacterCard({ c, active }) {
  const pct = c.maxHealth > 0 ? Math.max(0, Math.round((c.health / c.maxHealth) * 100)) : 0
  const disposition = c.disposition || (c.alive ? 'Active' : 'Dead')
  const dispositionCls = disposition === 'Active' ? '' : disposition.toLowerCase()
  const highlight = active && disposition === 'Active'
  const cls = ['card', c.team === 'Heroes' ? 'heroes' : 'monsters', dispositionCls, highlight ? 'active' : ''].join(' ')
  const status = DISPOSITION_LABEL[disposition]
  return (
    <div className={cls}>
      <div className="card-head">
        <span className="name">{c.name}</span>
        <span className="team">{c.team}</span>
      </div>
      {status && <div className={`disposition ${dispositionCls}`}>{status}</div>}
      <div className="hpbar"><div className="hpfill" style={{ width: `${pct}%` }} /></div>
      <div className="hptext">{disposition === 'Dead' ? 'fallen' : `${c.health} / ${c.maxHealth}`}</div>
      <div className="meta">
        {c.disarmed ? <span className="disarmed">disarmed</span> : (c.weapon || 'unarmed')} · armour {c.armour}
      </div>
      <FearMeter fear={c.fear} max={c.maxFear} scared={c.scared} outnumbered={c.outnumbered} />
      {(c.statuses || []).length > 0 && (
        <div className="statuses">
          {c.statuses.map((s) => (
            <span key={s.id} className={`badge ${s.kind.toLowerCase()}`} title={s.description}>
              {(STATUS_BADGE[s.kind] || ((x) => x.kind))(s)}
            </span>
          ))}
        </div>
      )}
      {(c.abilities || []).length > 0 && (
        <div className="abilities">
          {c.abilities.map((a) => (
            <span key={a.id} className={`ability ${a.remainingUses === 0 ? 'spent' : ''}`}>
              {a.name} {a.maxUses == null ? '∞' : `${a.remainingUses}/${a.maxUses}`}
            </span>
          ))}
        </div>
      )}
      {c.inventory.length > 0 && <div className="inv">carrying: {c.inventory.join(', ')}</div>}
      {c.injuries.length > 0 && <div className="injuries">{c.injuries.join('; ')}</div>}
    </div>
  )
}

// A compact 0-5 nerve indicator. The observer UI is an experiment artefact, so it shows the exact
// authoritative figure that no opponent in the fiction ever sees. It stays on the card after a character
// dies, yields or flees, recording the nerve they left the fight with.
function FearMeter({ fear, max, scared, outnumbered }) {
  if (fear == null || max == null) return null // an older run recorded before morale existed
  const pips = Array.from({ length: max }, (_, i) => i < fear)
  return (
    <div className={`fear ${scared ? 'scared' : ''}`} title={`Fear ${fear} / ${max}${scared ? ' — Scared' : ''}`}>
      <span className="fear-label">nerve</span>
      <span className="pips">{pips.map((on, i) => <i key={i} className={on ? 'on' : ''} />)}</span>
      <span className="fear-count">{fear}/{max}</span>
      {outnumbered && <span className="fear-note" title="More active enemies than allies">outnumbered</span>}
    </div>
  )
}

function LogLine({ evt }) {
  const p = evt.payload || {}
  switch (evt.type) {
    case 'round': return <div className="line divider">── Round {p.round} ──</div>
    case 'roundSummary': return (
      <div className="line roundsummary">
        <span className="rs-tag">Round {p.round}</span>
        <span className="rs-text">{p.text}</span>
      </div>
    )
    case 'runHeader': return <div className="line sys">{p.scenario} · run {p.runId}</div>
    case 'notice': return <div className="line sys">{p.text}</div>
    case 'narration': return <Line label="DM" cls="dm" text={p.text} />
    case 'speaks': return <Line label={`${p.character} says`} cls="speech" text={`“${p.text}”`} />
    case 'asks': return <Line label={`${p.character} asks`} cls="ask" text={`“${p.text}”`} />
    case 'acts': return <Line label={p.character} cls="act" text={`“${p.text}”`} />
    case 'passes': return <Line label={`${p.character} holds back`} cls="pass" text={p.text} />
    case 'refused': return <Line label={`DM → ${p.character}`} cls="refused" text={p.text} />
    case 'privateObservation': return <Line label={`DM → ${p.character} (private)`} cls="private" text={p.text} />
    case 'attack': return (
      <div className={`line combat ${p.intercepted ? 'intercepted' : (p.quality || (p.glancing ? 'Glancing' : 'Solid')).toLowerCase()}`}>
        {attackText(p)}
      </div>
    )
    case 'exitOpened': return <div className="line outcome">🚪 {p.character} opens the {p.exit}.</div>
    case 'surrendered': return <div className="line outcome">🏳️ {p.character} surrenders and leaves the fight (still alive).</div>
    case 'escaped': return <div className="line outcome">🏃 {p.character} escapes through the {p.exit} and is gone (still alive).</div>
    case 'gave': return <div className="line outcome">🤝 {p.character} gives the {p.item} to {p.recipient}.</div>
    case 'dropped': return <div className="line outcome">🟫 {p.character} drops the {p.item} on the floor.</div>
    case 'stole': return <div className="line outcome">{p.succeeded ? `🖐️ ${p.character} snatches the ${p.item} from ${p.target}!` : `✋ ${p.character} lunges for ${p.target}'s ${p.item} — but fails.`}</div>
    // An offer is a proposal and must never read like a surrender: the line says so explicitly.
    case 'surrenderOffered': return (
      <div className="line offerline">
        🏳️ {p.character} offers {p.recipient} terms to end their fight — {p.terms}.
        <span className="sub"> Nothing has changed hands; {p.character} is still armed and still a target. Only {p.recipient} can accept.</span>
      </div>
    )
    case 'surrenderOfferSettled': return (
      <div className="line offerline settled">
        ⛔ {p.character}’s offer to {p.recipient} is {p.state.toLowerCase()} — {p.cause}. Nothing transferred.
      </div>
    )
    case 'surrenderAccepted': return (
      <div className="line outcome accepted">
        🤝 {p.character} accepts {p.offerer}’s surrender
        {p.tribute && p.tribute.length ? ` and takes ${p.tribute.join(', ')}` : ''}
        {p.weapon ? `; ${p.offerer}’s ${p.weapon} goes to ${p.weaponDisposition || 'the floor'}` : ''}.
        <span className="sub"> {p.offerer} is disarmed and out of the fight.</span>
      </div>
    )
    case 'abilityUsed': return (
      <div className="line ability">
        ✨ {p.character} uses {p.ability}{p.target ? ` on ${p.target}` : ''}
        {p.healing ? ` — ${p.healing} health restored` : ''}
        {p.remainingUses != null ? ` (${p.remainingUses} left)` : ''}.
      </div>
    )
    case 'statusChanged': return (
      <div className="line status">
        {statusIcon(p.transition)} {p.target}: {p.kind} {p.transition.toLowerCase()} — {p.cause}.
      </div>
    )
    case 'attackRedirected': return (
      <div className="line redirect">
        🛡 {p.attacker} struck at {p.intendedTarget}, but {p.guardian} took the blow instead (no extra roll).
      </div>
    )
    case 'defendReduced': return (
      <div className="line status">🪨 {p.target}’s guard turned aside {p.reduction} damage.</div>
    )
    case 'coverOccupancyChanged': {
      const verb = p.transition === 'entered' ? 'takes cover behind' : p.transition === 'left' ? 'steps out from behind' : 'is exposed from behind'
      return <div className="line cover">🛖 {p.character} {verb} {p.cover}.</div>
    }
    case 'coverDamaged': return (
      <div className="line cover">
        🛖 {p.cover} {p.destroyed ? 'is DESTROYED' : `takes damage (${p.durabilityAfter} durability left)`} — {p.cause}.
      </div>
    )
    case 'environmentalObjectDamaged': return (
      <div className="line cover">
        🪓 {p.character} strikes {p.objectName} with {p.weapon} — {p.damage} damage.
        {p.destroyed ? ` ${p.objectName} is DESTROYED.` : ` (${p.durabilityAfter} durability left)`}
      </div>
    )
    case 'fearChanged': {
      if (p.absorbed) {
        return (
          <div className="line status">
            😐 {p.character}: nerve unchanged at {p.after} — {p.causeDetail} (already at the limit).
          </div>
        )
      }
      const arrow = p.delta > 0 ? '▲' : '▼'
      const cls = p.transition === 'BecameScared' ? 'line fear broke'
        : p.transition === 'RecoveredFromScared' ? 'line fear steadied'
          : 'line fear'
      const crossing = p.transition === 'BecameScared'
        ? ' — their nerve goes, and everyone can see it'
        : p.transition === 'RecoveredFromScared' ? ' — their nerve holds again' : ''
      return (
        <div className={cls}>
          {arrow} {p.character} fear {p.before} → {p.after} ({p.causeDetail}){crossing}.
        </div>
      )
    }
    case 'intimidation': return (
      <div className={`line threat ${p.succeeded ? 'told' : 'failed'}`}>
        {p.succeeded ? '😨' : '😐'} {p.character} threatens {p.target} — {p.succeeded ? 'it tells' : 'no flinch'}.
        <span className="sub">
          {' '}rolled {p.roll} against {p.effectiveChance} (base {p.baseChance}
          {p.modifiers && p.modifiers.length ? `, ${p.modifiers.join(', ')}` : ''});
          {' '}{p.target} fear {p.targetFearBefore} → {p.targetFearAfter}. Nothing else changes.
        </span>
      </div>
    )
    case 'allySteadied': return (
      <div className="line steady">
        🫱 {p.character} steadies {p.target}
        {p.noEffect ? ' — but they had not lost their nerve, and the turn is spent' : ` — fear ${p.targetFearBefore} → ${p.targetFearAfter}`}.
      </div>
    )
    case 'completed': {
      const suffix = p.outcome && p.outcome !== 'Ongoing' ? ` — ${p.outcome}` : ''
      return <div className="line divider end">── {p.terminalCondition}{suffix} ──</div>
    }
    case 'ending': {
      // SummariseEnding leads with the terminal condition, which the `completed` divider already shows —
      // render only the per-character survivor lines so the condition is not printed twice.
      const lines = (p.text || '').split('\n').filter(Boolean).slice(1)
      return lines.length ? <div className="line sys">{lines.map((l, i) => <div key={i}>{l}</div>)}</div> : null
    }
    // The full text is handled outside the log entirely (see StoryPopover) — this is only the marker that
    // tells a reader scrolling back through the transcript when it arrived.
    case 'storyReady': return <div className="line sys">📖 The tale of the encounter is ready — see “Read the tale”.</div>
    default: return null
  }
}

function Line({ label, cls, text }) {
  return <div className={`line ${cls}`}><span className="who">{label}:</span> {text}</div>
}

function statusIcon(transition) {
  switch (transition) {
    case 'Applied': return '➕'
    case 'Consumed': return '➖'
    case 'Expired': return '⏱'
    default: return '✖'
  }
}

// `quality` is the authoritative word from the single quality draw; `glancing` is the older field, kept
// so a run recorded before critical hits existed still reads correctly instead of showing every blow solid.
const QUALITY_TEXT = {
  Glancing: 'a glancing blow',
  Solid: 'a solid hit',
  Critical: 'a CRITICAL hit'
}

function attackText(p) {
  if (p.intercepted) {
    return `${p.attacker} strikes at ${p.target} — turned aside by ${p.coverName}! ${p.target} takes no damage.`
  }
  if (!p.hit) {
    const coverNote = p.coverId ? ` (${p.target} was behind ${p.coverName}, but this was an ordinary miss)` : ''
    return `${p.attacker} attacks ${p.target} — misses.${coverNote}`
  }
  const quality = p.quality || (p.glancing ? 'Glancing' : 'Solid')
  const kind = QUALITY_TEXT[quality] || 'a solid hit'
  const dead = p.died ? ` ${p.target} falls.` : ` (${p.target} ${p.targetHealth}/${p.targetMaxHealth})`
  const bypassed = p.coverId ? ` — found them despite ${p.coverName}` : ''
  return `${p.attacker} hits ${p.target}${bypassed} — ${kind}, ${p.damage} damage.${dead}`
}
