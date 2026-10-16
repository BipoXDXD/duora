// Gerado por tools/visual/process.py a partir de tools/visual/catalog.json; não edite à mão.
import cardPair1x from './illustrations/card-pair.webp'
import cardPair2x from './illustrations/card-pair@2x.webp'
import compass1x from './illustrations/compass.webp'
import compass2x from './illustrations/compass@2x.webp'
import constellation1x from './illustrations/constellation.webp'
import constellation2x from './illustrations/constellation@2x.webp'
import envelope1x from './illustrations/envelope.webp'
import envelope2x from './illustrations/envelope@2x.webp'
import foldedMap1x from './illustrations/folded-map.webp'
import foldedMap2x from './illustrations/folded-map@2x.webp'
import lantern1x from './illustrations/lantern.webp'
import lantern2x from './illustrations/lantern@2x.webp'
import padlock1x from './illustrations/padlock.webp'
import padlock2x from './illustrations/padlock@2x.webp'
import placeholder1x from './illustrations/placeholder.webp'
import placeholder2x from './illustrations/placeholder@2x.webp'
import shield1x from './illustrations/shield.webp'
import shield2x from './illustrations/shield@2x.webp'
import speechBubbles1x from './illustrations/speech-bubbles.webp'
import speechBubbles2x from './illustrations/speech-bubbles@2x.webp'
import ticket1x from './illustrations/ticket.webp'
import ticket2x from './illustrations/ticket@2x.webp'

export const ILLUSTRATIONS = {
  'card-pair': { cssPx: 200, src1x: cardPair1x, src2x: cardPair2x },
  compass: { cssPx: 200, src1x: compass1x, src2x: compass2x },
  constellation: { cssPx: 200, src1x: constellation1x, src2x: constellation2x },
  envelope: { cssPx: 128, src1x: envelope1x, src2x: envelope2x },
  'folded-map': { cssPx: 200, src1x: foldedMap1x, src2x: foldedMap2x },
  lantern: { cssPx: 240, src1x: lantern1x, src2x: lantern2x },
  padlock: { cssPx: 128, src1x: padlock1x, src2x: padlock2x },
  placeholder: { cssPx: 160, src1x: placeholder1x, src2x: placeholder2x },
  shield: { cssPx: 128, src1x: shield1x, src2x: shield2x },
  'speech-bubbles': { cssPx: 160, src1x: speechBubbles1x, src2x: speechBubbles2x },
  ticket: { cssPx: 160, src1x: ticket1x, src2x: ticket2x },
} as const
