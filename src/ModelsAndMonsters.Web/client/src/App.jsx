import React, { useCallback, useEffect, useRef, useState } from 'react'

const API = 'http://localhost:5170'

// How each streamed event renders in the transcript. Structured events (state/turnStarted) are handled
// separately and drive the cards, not the log.
const LOG_KINDS = new Set([
  'runHeader', 'notice', 'round', 'narration', 'asks', 'acts', 'speaks',
  'passes', 'refused', 'privateObservation', 'attack', 'completed', 'ending'
])

export default function App() {
  const [runId, setRunId] = useState(null)
  const [status, setStatus] = useState('idle') // idle | running | done
  const [characters, setCharacters] = useState([])
  const [objects, setObjects] = useState([])
  const [turn, setTurn] = useState(null)
  const [round, setRound] = useState(null)
  const [log, setLog] = useState([])
  const esRef = useRef(null)
  const bottomRef = useRef(null)

  useEffect(() => { bottomRef.current?.scrollIntoView({ behavior: 'smooth' }) }, [log])
  useEffect(() => () => esRef.current?.close(), [])

  const handle = useCallback((evt) => {
    const p = evt.payload || {}
    if (evt.type === 'state') { setCharacters(p.characters || []); setObjects(p.objects || []); return }
    if (evt.type === 'turnStarted') { setTurn(p.character); return }
    if (evt.type === 'round') setRound(p.round)
    if (evt.type === 'completed') setStatus('done')
    if (LOG_KINDS.has(evt.type)) setLog((l) => [...l, evt])
  }, [])

  const start = useCallback(async () => {
    esRef.current?.close()
    setCharacters([]); setObjects([]); setTurn(null); setRound(null); setLog([]); setStatus('running')
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
        <h1>🎲 Models &amp; Monsters 👺</h1>
        <div className="controls">
          {round != null && <span className="round">Round {round}</span>}
          <span className={`status ${status}`}>{status}</span>
          <button onClick={start} disabled={status === 'running'}>Start run</button>
          <button onClick={cancel} disabled={status !== 'running'} className="ghost">Cancel</button>
        </div>
      </header>

      <section className="cards">
        {characters.length === 0 && <div className="empty">Press “Start run” to begin an encounter.</div>}
        {characters.map((c) => <CharacterCard key={c.id} c={c} active={c.name === turn} />)}
      </section>

      {objects.length > 0 && (
        <section className="objects">
          {objects.map((o) => (
            <div key={o.id} className={`obj ${o.isContainer && o.isOpen ? 'open' : ''}`}>
              <span className="obj-name">{o.name}</span>
              {o.isContainer && <span className="obj-state">{o.isOpen ? 'open' : 'closed'}</span>}
            </div>
          ))}
        </section>
      )}

      <section className="stream">
        {log.map((e, i) => <LogLine key={i} evt={e} />)}
        <div ref={bottomRef} />
      </section>
    </div>
  )
}

function CharacterCard({ c, active }) {
  const pct = c.maxHealth > 0 ? Math.max(0, Math.round((c.health / c.maxHealth) * 100)) : 0
  const cls = ['card', c.team === 'Heroes' ? 'heroes' : 'monsters', c.alive ? '' : 'dead', active ? 'active' : ''].join(' ')
  return (
    <div className={cls}>
      <div className="card-head">
        <span className="name">{c.name}</span>
        <span className="team">{c.team}</span>
      </div>
      <div className="hpbar"><div className="hpfill" style={{ width: `${pct}%` }} /></div>
      <div className="hptext">{c.alive ? `${c.health} / ${c.maxHealth}` : 'fallen'}</div>
      <div className="meta">{c.weapon || 'unarmed'} · armour {c.armour}</div>
      {c.inventory.length > 0 && <div className="inv">carrying: {c.inventory.join(', ')}</div>}
      {c.injuries.length > 0 && <div className="injuries">{c.injuries.join('; ')}</div>}
    </div>
  )
}

function LogLine({ evt }) {
  const p = evt.payload || {}
  switch (evt.type) {
    case 'round': return <div className="line divider">── Round {p.round} ──</div>
    case 'runHeader': return <div className="line sys">{p.scenario} · run {p.runId}</div>
    case 'notice': return <div className="line sys">{p.text}</div>
    case 'narration': return <Line label="DM" cls="dm" text={p.text} />
    case 'speaks': return <Line label={`${p.character} says`} cls="speech" text={`“${p.text}”`} />
    case 'asks': return <Line label={`${p.character} asks`} cls="ask" text={`“${p.text}”`} />
    case 'acts': return <Line label={p.character} cls="act" text={`“${p.text}”`} />
    case 'passes': return <Line label={`${p.character} holds back`} cls="pass" text={p.text} />
    case 'refused': return <Line label={`DM → ${p.character}`} cls="refused" text={p.text} />
    case 'privateObservation': return <Line label={`DM → ${p.character} (private)`} cls="private" text={p.text} />
    case 'attack': return <div className="line combat">{attackText(p)}</div>
    case 'completed': return <div className="line divider end">── {p.terminalCondition} ──</div>
    case 'ending': {
      // SummariseEnding leads with the terminal condition, which the `completed` divider already shows —
      // render only the per-character survivor lines so the condition is not printed twice.
      const lines = (p.text || '').split('\n').filter(Boolean).slice(1)
      return lines.length ? <div className="line sys">{lines.map((l, i) => <div key={i}>{l}</div>)}</div> : null
    }
    default: return null
  }
}

function Line({ label, cls, text }) {
  return <div className={`line ${cls}`}><span className="who">{label}:</span> {text}</div>
}

function attackText(p) {
  if (!p.hit) return `${p.attacker} attacks ${p.target} — misses.`
  const kind = p.glancing ? 'a glancing blow' : 'a solid hit'
  const dead = p.died ? ` ${p.target} falls.` : ` (${p.target} ${p.targetHealth}/${p.targetMaxHealth})`
  return `${p.attacker} hits ${p.target} — ${kind}, ${p.damage} damage.${dead}`
}
