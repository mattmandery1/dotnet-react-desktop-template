import type { Card } from '../playingCards'

interface PlayingCardProps {
  card: Card
}

export function PlayingCard({ card }: PlayingCardProps) {
  const isRed = card.suit === 'Hearts' || card.suit === 'Diamonds'

  return (
    <article
      className={`playing-card${isRed ? ' playing-card--red' : ''}`}
      aria-label={`${card.rank} of ${card.suit}`}
    >
      <span className="playing-card__corner">
        <span>{card.rank}</span>
        <span>{card.symbol}</span>
      </span>
      <span className="playing-card__suit" aria-hidden="true">
        {card.symbol}
      </span>
    </article>
  )
}
