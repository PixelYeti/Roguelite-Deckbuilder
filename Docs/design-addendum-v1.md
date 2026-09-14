# TALUNE — Design Addendum v1

Fixes for 15 gaps found in the master build prompt: 5 from an initial read, 1 from a
completeness check on the hybrid matrix, 7 from simulating a single combat and a full
run end-to-end, and 2 from a final "solid bones" pass reading all the persistent
mechanics as one system. Each entry says which master-prompt section it edits and,
where it wasn't obvious from reading alone, what simulated moment exposed the gap.

Drop each fix into the matching section of the master prompt (insert or replace, as
noted per fix).

---

## Fix 1 — Missing hybrid pairing
**Insert into: HYBRID SYNERGIES**

6 Kin → C(6,2) = 15 possible pairs. The original doc defines 10. These 5 close the
matrix; **Stormroot is required for the prototype** (Bubblo + Voltrix + Mossmaw needs
all 3 of its own pairs, and Voltrix+Mossmaw was the one missing).

**VOLTRIX + MOSSMAW — Stormroot** *(prototype-required)* — Type: Hybrid, Kin: Voltrix + Mossmaw
Growth and Thorn stacks also carry a charge: each stack of Thorns discharges as 1
Lightning damage to a second enemy when triggered. Playing a Lightning card grants +1
Thorns as a side effect.

**BUBBLO + HUSH — Reflection** *(full game)* — Type: Hybrid, Kin: Bubblo + Hush
Shield cards leave behind a duplicate illusion of the shielded amount; the illusion
detonates as damage if the shield is depleted.

**BUBBLO + RAZORWING — Steam Vent** *(full game)* — Type: Hybrid, Kin: Bubblo + Razorwing
Blocking while Burn is on the enemy converts a portion of Block into bonus Burn (water
flashing to steam).

**HUSH + MOSSMAW — Verdant Echo** *(full game)* — Type: Hybrid, Kin: Hush + Mossmaw
Illusion copies inherit any Thorns/Growth stacks on the caster at time of creation.

**VOLTRIX + IRONJAW — Overload** *(full game)* — Type: Hybrid, Kin: Voltrix + Ironjaw
Stun applies a delayed Lightning burst that triggers when the stun expires.

*Coverage check: 10 original pairs + these 5 = all 15 of C(6,2) — every Kin
combination is now defined.*

---

## Fix 2 — Kin: type vs. axis conflict
**Replace: CARD TYPES, and the relevant lines in KIN SYSTEM**

Resolve by making **Kin an alignment tag, not a type**. Every card has exactly one
Type; 0–2 Kin tags.

Every card has exactly one **Type**: Attack, Guard, Skill, Power, or Hybrid.

Every card additionally carries **0–2 Kin tags**: Neutral (basic starter cards, no
tag), single-Kin (one tag — e.g. Static Fang is Type: Attack, Kin: Voltrix), or Hybrid
(exactly two tags, always Type: Hybrid).

"Kin" is removed from the Type list — it was never a third axis, it's the alignment
every Attack/Guard/Skill/Power card can also carry.

---

## Fix 3 — Status effect framework
**Insert new section after: CARD TYPES**

### STATUS EFFECTS

**Burn** — numeric stack. Deals stack-count damage at the start of the afflicted
entity's turn, then reduces the stack by 1. Multiple Burn applications add to the
existing stack (additive, not refresh-to-max).

**Stun** — binary, single-turn. Afflicted entity skips its next action. Reapplying
Stun before it triggers has no additional effect (does not stack duration).

Both are removed from the target the moment their stack reaches 0 / their single use
resolves. *(Extended by Fix 14 below — Growth, Thorns, and Illusion tokens.)*

---

## Fix 4 — Kin rank metric
**Insert into: KIN VISUAL FEEDBACK, before the Rank 1–5 list**

**Kin Rank** = the number of cards carrying that Kin's tag currently in the player's
deck (draw + discard + hand; not exhaust/removed). Recalculated immediately on any
deck change (card added, removed, or upgraded — upgrades don't change the count).

Thresholds: Rank 1 = 1–2 cards, Rank 2 = 3–4, Rank 3 = 5–6, Rank 4 = 7–8, Rank 5 = 9+.

Hybrid-card eligibility (see HYBRID SYNERGIES) requires both relevant Kins at Rank 2
or higher.

*Sanity check: a 20-card, two-Kin deck split 10/10 puts both Kins at Rank 5 —
eligibility is reachable mid-run, not gated to a "finished" deck. A three-Kin split
(~6–7 each) lands around Rank 3–4, still eligible. Holds at both the 15 and 30-card
ends of the target deck-size range.*

---

## Fix 5 — Hybrid rarity/type collision
**Edit: CARD RARITY**

Rarity tiers: Common, Uncommon, Rare. **Hybrid is not a rarity** — it's a Type (see
Fix 2), unlocked by Kin Rank thresholds rather than drop-weighted like the other
three. Reward pools roll Common/Uncommon/Rare normally; Hybrid cards are offered as
bonus options only when the player's current Kin Ranks make them eligible.

---

## Fix 6 — Node visibility / route legibility
**Insert into: RUN STRUCTURE, after the node type list**

Every node's type icon is visible on the map before the player commits to a path —
Combat, Elite, Shrine, Shop, Treasure, Healing, Fracture, Bramble, and Boss are all
shown upfront. Only **Mystery Event** nodes are hidden (shown as a "?"), since their
entire appeal is not knowing what's inside. "Safer vs. riskier" routing works because
the player can see, e.g., an Elite-heavy path vs. a Shop-heavy path before choosing —
risk has to be visible to be a real decision.

---

## Fix 7 — Multi-enemy combat structure
**Insert new subsection under: ENEMY STRUCTURE**

*Found by simulating a turn: Static Fang's "chain lightning once," Firestorm,
Wildfire, Molten Impact, and Stormroot (Fix 1) all assume a second enemy can exist —
nothing established that combats ever field more than one.*

Combats field 1–3 enemies at once. Basic Combat: 1–2 enemies. Elite Combat: 1 elite,
occasionally with a lesser add. Boss: 1 boss, which may summon adds per its own kit
(see the "boss that summons smaller enemies" example). Any card that targets "a
second enemy" or "chains"/"spreads" simply deals its base effect to the single enemy
present with no bonus when only one target exists — it never fizzles.

---

## Fix 8 — Block decay rule
**Insert into: TURN STRUCTURE, near the Energy refill line**

*Found by simulating a multi-turn exchange: Tidal Fortress's "large block totals" is
unbalanceable until it's known whether Block persists or resets.*

Block resets to 0 at the start of the player's own turn — it does not carry over.
"Large block totals" (Tidal Fortress) means block generated within a single turn, not
a stockpile built across several.

---

## Fix 9 — Enemy intent telegraphing
**Insert new subsection under: ENEMY STRUCTURE**

*Found by simulating a decision point: nothing tells the player what an enemy is
about to do, which makes Guard/counterattack/retaliation (Ironjaw's whole identity) a
guess rather than a choice — directly undercutting the doc's own "difficult to
master, easy to understand" and "visually readable" pillars.*

Every enemy shows an intent icon above its head each turn — action category (Attack +
damage number, Block, Buff, Debuff, Special) — visible before the player commits
cards. This is load-bearing for the Guard/counter/retaliation kit, not a cosmetic
nicety.

---

## Fix 10 — Baseline placeholder numbers for the vertical slice
**Insert new section: PROTOTYPE BASELINE NUMBERS**

*Found by trying to actually stage the first fight: there is no starting HP, enemy
HP, or card damage number anywhere in the doc — the prototype can't be played even
roughly without one.*

Rook: 70 HP. Basic enemy: 8–15 HP (weak/support), 25–40 HP (standard melee/ranged),
45–60 HP (special). Elite: 90–120 HP. Boss: 200–300 HP. Basic Attack card: 6 damage,
cost 1. Basic Guard card: 5 block, cost 1.

Explicitly placeholder, subject to playtest — but a number, not a blank.

---

## Fix 11 — Currency & reward structure
**Insert into: RUN STRUCTURE, after the node type list**

*Found by simulating a shop visit: Kip "sells" things, but no currency is ever named
or priced anywhere in the doc.*

Currency: **Fragments** — the same Fracture fragments Vorath collects, so the player
is spending the exact resource the antagonist is obsessed with (reinforces his motive
for free). Combat rewards: ~10–20 Fragments (basic), ~30–50 (Elite). Treasure nodes: a
Relic *or* a larger haul (~60–100), player's choice. Kip's prices: cards ~50–75,
upgrades ~50, card removal ~75–100 (increases with each purchase in a run, so it can't
be spammed), relics ~150–200.

Reward mapping by node: Combat → 1-of-3 card choice (or skip, Fix 12) + Fragments.
Elite → same + guaranteed Relic. Treasure → Relic or Fragments haul. Boss →
guaranteed Relic + large haul + Act transition.

---

## Fix 12 — Card reward skip option
**Insert into: CORE DECKBUILDING SYSTEM**

*Found by simulating the same shop visit forward to the next fight's reward screen:
without a way to decline a card, the deck grows every single Combat node.*

Every card-reward screen (Combat, Elite, Kin Shrine) includes a **Skip** option
alongside its 3 choices. This isn't optional polish — it's the only mechanism that
makes the doc's own stated goal ("deck should remain relatively small," 15–30 cards)
reachable. Without it, a ~40-node act forces 40 new cards into the deck regardless of
the target range.

---

## Fix 13 — Kin Shrine function
**New definition** — the name appears once in the node list and is never described
anywhere else in the doc.

*Found by simulating arrival at the node and realizing there's nothing to do there.*

Kin Shrine: the player picks one of the six Kins (their own aligned ones, or any if
none chosen yet) and is offered 3 cards of *that* Kin specifically (or Skip, per Fix
12). This is the deliberate, player-directed tool for steering toward a chosen Kin or
Hybrid build — distinct from the random-Kin 3-card offer after ordinary Combat.

---

## Fix 14 — Thorns/Growth and Illusion mechanical specs
**Extend: STATUS EFFECTS (Fix 3)**

*Found on a "solid bones" pass reading all five persistent sub-mechanics as a set:
Fix 3 only specified Burn and Stun because those are named directly in card text —
Thorns/Growth (Mossmaw) and Illusion tokens (Hush) are equally load-bearing across the
hybrid roster and had no spec at all.*

**Growth** — numeric stack, permanent for the combat (does not decay). Each stack: +1
damage on the caster's Attack cards. Mirrors "scaling strength."

**Thorns** — numeric stack, permanent for the combat. Deals stack-count damage back
to any enemy that melee-attacks the caster. Additive on reapplication, like Burn.

**Illusion token** — a temporary decoy created by certain Hush cards. Absorbs the
next single attack directed at the caster (as Block would), then is consumed. Does
not stack — a new Illusion token replaces an unused one rather than adding a second.

---

## Fix 15 — Kin unlock pacing across Acts
**Insert into: META PROGRESSION**, extending the existing "new Kin interactions"
unlock bullet.

*Found on the same pass: the prototype correctly limits itself to 3 Kins, but nothing
says the full 6-Kin game avoids dropping all 6 Kins + 15 hybrid pairs + 5 stacking
mechanics on a new player at once — which would contradict the doc's own
"understandable to younger players" pillar.*

Kin *availability* itself unlocks progressively across the first several full-game
runs, not just Kin *interactions*: a new player starts with 2 Kins active in
shops/rewards/shrines, unlocking 2 more after their first Act-1 clear and the final 2
after their first Act-2 clear. By full unlock, all players have already learned each
Kin's identity one pair at a time before facing the complete hybrid matrix.

---

## Smaller open items, resolved

- **Run length**: a run = 3 Acts; each Act = one biome, ~12–15 nodes, ending in that
  biome's Boss. Prototype = 1 Act only.
- **"Movement" cards are not a spatial/grid system** — there's no positioning
  mechanic described anywhere else in the doc, so Voltrix/Razorwing/Hush "movement,"
  "air currents," and "evasion" flavor resolve as ordinary buffs (Dodge-next-attack,
  bonus Energy, extra draw), not literal repositioning. Stated explicitly so a
  builder doesn't start implementing a grid combat system that was never intended.

---

## Solid-bones verdict

The core loop (Combat → Reward → Map routing → Meta wrapper) is fully closed once
Fixes 1–15 land — every node type, every card-text dependency, and every persistent
sub-mechanic now has a defined behavior, and the doc's own Prototype Priority section
already does the right thing by forcing a 3-Kin/1-biome slice before the full 6-Kin
vision. The two systemic risks worth carrying forward while building (Fix 14: two of
five stacking mechanics were previously undefined; Fix 15: the "kid-friendly" promise
needs explicit Kin-pacing in the full game, not just the prototype) are both addressed
above.
