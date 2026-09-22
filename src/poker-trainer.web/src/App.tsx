import './App.css'
import { PlayingCard } from './components/PlayingCard'
import { deck } from './playingCards'

function App() {
  return (
    <main className="poker-screen">
      <section className="table-section" aria-label="Texas Hold'em poker table">
        <div className="poker-table" role="img" aria-label="Empty green felt poker table">
          <div className="poker-table__felt" />
        </div>
      </section>

      <section className="deck-section" aria-label="Standard 52-card deck">
        <div className="deck-grid">
          {deck.map((card) => (
            <PlayingCard key={`${card.rank}-${card.suit}`} card={card} />
          ))}
        </div>
      </section>
    </main>
  )
}

export default App
