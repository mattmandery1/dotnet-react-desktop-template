export const ranks = ['2', '3', '4', '5', '6', '7', '8', '9', '10', 'J', 'Q', 'K', 'A'] as const

export const suits = [
  { name: 'Spades', symbol: '♠' },
  { name: 'Hearts', symbol: '♥' },
  { name: 'Diamonds', symbol: '♦' },
  { name: 'Clubs', symbol: '♣' },
] as const

export type Rank = (typeof ranks)[number]
export type Suit = (typeof suits)[number]['name']

export interface Card {
  rank: Rank
  suit: Suit
  symbol: (typeof suits)[number]['symbol']
}

export const deck: Card[] = suits.flatMap(({ name, symbol }) =>
  ranks.map((rank) => ({ rank, suit: name, symbol })),
)
