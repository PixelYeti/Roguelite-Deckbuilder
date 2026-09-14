using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using Talune.Cards;
using Talune.Combat;
using Talune.Content;
using Talune.Core;
using Talune.Map;
using Talune.Relics;
using Talune.Run;

namespace Talune.UI
{
    /// <summary>
    /// Drives a full run end-to-end: map navigation, combat, rewards, Kip's shop,
    /// healing, and placeholder events, all as uGUI screens toggled under one Canvas.
    /// Combat itself reuses the same verified CombatManager/RelicManager/RunState
    /// engine as before - this controller is purely the screen flow wrapped around it.
    /// Drop on any empty GameObject and press Play.
    /// </summary>
    public class RunSceneController : MonoBehaviour
    {
        // --- Run-persistent state ---
        private RunState _runState;
        private RunMap _map;
        private PlayerCombatant _player;
        private System.Random _rng;
        private List<CardData> _cardPool;
        private List<RelicData> _relicPool;

        // --- Combat-scoped state ---
        private CombatManager _combat;
        private EnemyCombatant _selectedTarget;
        private RewardNodeType _pendingRewardType;
        private readonly List<string> _logLines = new();
        private readonly Dictionary<EnemyCombatant, (Image panelImage, Image spriteImage, Text text)> _enemyUI = new();

        // --- Shared chrome ---
        private Text _hudText;
        private GameObject _screenContainer;
        private readonly Dictionary<string, GameObject> _screens = new();

        // --- Combat screen refs ---
        private Text _playerStatsText;
        private Text _logText;
        private Button _endTurnButton;
        private Transform _handContainer;
        private Transform _enemyRow;
        private GameObject _resultOverlay;
        private Text _resultText;
        private Button _resultOverlayButton;

        private static readonly Color PanelBg = new(0.14f, 0.14f, 0.18f);
        private static readonly Color CardUnaffordableTint = new(0.42f, 0.42f, 0.42f, 1f);
        private static readonly Color TargetSelectedBg = new(0.45f, 0.14f, 0.14f);
        private static readonly Color TargetBg = new(0.16f, 0.15f, 0.17f);

        private static readonly Dictionary<CardType, Color> CardTypeColor = new()
        {
            { CardType.Attack, new Color(0.55f, 0.18f, 0.18f) },
            { CardType.Guard, new Color(0.18f, 0.32f, 0.55f) },
            { CardType.Skill, new Color(0.20f, 0.45f, 0.24f) },
            { CardType.Power, new Color(0.45f, 0.24f, 0.55f) },
            { CardType.Hybrid, new Color(0.55f, 0.45f, 0.14f) },
        };

        // Fix 6's node-visibility rule: every node type shown as its real label except
        // Mystery Event (MapNode.DisplayLabel already returns "?" for that one).
        private static readonly Dictionary<MapNodeType, Color> NodeTypeColor = new()
        {
            { MapNodeType.Combat, new Color(0.35f, 0.16f, 0.16f) },
            { MapNodeType.Elite, new Color(0.5f, 0.2f, 0.1f) },
            { MapNodeType.Boss, new Color(0.5f, 0.05f, 0.05f) },
            { MapNodeType.KinShrine, new Color(0.20f, 0.45f, 0.24f) },
            { MapNodeType.KipShop, new Color(0.45f, 0.36f, 0.1f) },
            { MapNodeType.Treasure, new Color(0.45f, 0.24f, 0.55f) },
            { MapNodeType.Healing, new Color(0.16f, 0.32f, 0.55f) },
            { MapNodeType.MysteryEvent, new Color(0.25f, 0.25f, 0.3f) },
            { MapNodeType.FractureEvent, new Color(0.3f, 0.15f, 0.35f) },
            { MapNodeType.BrambleEvent, new Color(0.2f, 0.3f, 0.15f) },
        };

        private Sprite _cardFrameSprite;
        private readonly Dictionary<string, Sprite> _enemySpriteCache = new();
        private readonly Dictionary<CardType, Sprite> _cardIconCache = new();

        private AudioSource _sfxSource;
        private readonly Dictionary<string, AudioClip> _sfxCache = new();
        private bool _cardActionInProgress; // Guards against double-clicks during the brief play animation.

        private void PlaySfx(string name)
        {
            if (!_sfxCache.TryGetValue(name, out var clip))
            {
                clip = Resources.Load<AudioClip>($"Audio/SFX/{name}");
                _sfxCache[name] = clip; // Cache the miss too (null) - no repeated failed loads.
            }
            if (clip != null) _sfxSource.PlayOneShot(clip);
        }

        private Sprite GetEnemySprite(string displayName)
        {
            if (_enemySpriteCache.TryGetValue(displayName, out var cached)) return cached;
            var sprite = Resources.Load<Sprite>($"Art/Enemies/{displayName}");
            _enemySpriteCache[displayName] = sprite;
            return sprite;
        }

        private Sprite GetCardIcon(CardType type)
        {
            if (_cardIconCache.TryGetValue(type, out var cached)) return cached;
            var sprite = Resources.Load<Sprite>($"Art/CardIcons/{type}");
            _cardIconCache[type] = sprite;
            return sprite;
        }

        private void Awake()
        {
            _cardFrameSprite = Resources.Load<Sprite>("Art/Cards/CardFrame");
            _sfxSource = gameObject.AddComponent<AudioSource>();
            _sfxSource.playOnAwake = false;
            BuildUI();
            StartNewRun();
        }

        // ============================================================
        // Run lifecycle
        // ============================================================

        private void StartNewRun()
        {
            _rng = new System.Random();
            _runState = new RunState();
            _runState.Deck.AddRange(DefaultContent.BuildStarterDeck());
            _cardPool = DefaultContent.BuildRewardPool();
            _relicPool = DefaultContent.BuildStarterRelicPool();
            _player = new PlayerCombatant(BaselineNumbers.RookMaxHP, BaselineNumbers.PlayerMaxEnergy);
            _map = MapGenerator.Generate(rowCount: 13, nodesPerRow: 3, rng: _rng);

            ShowMapScreen();
        }

        private void RefreshHUD()
        {
            _hudText.text = $"Rook  HP {_player.CurrentHP}/{_player.MaxHP}    Fragments {_runState.Fragments}    Deck {_runState.Deck.Count}    Relics {_runState.Relics.Count}";
        }

        // ============================================================
        // Map screen
        // ============================================================

        private void ShowMapScreen()
        {
            ShowScreen("Map");
            RefreshHUD();

            var container = _screens["Map"].transform.Find("NodeButtons");
            for (int i = container.childCount - 1; i >= 0; i--) DestroyImmediate(container.GetChild(i).gameObject);

            var titleText = _screens["Map"].transform.Find("Title").GetComponent<Text>();
            titleText.text = _map.CurrentNodeId == -1
                ? "Choose your first step into Talune:"
                : "Choose your next step:";

            foreach (var node in _map.AvailableNextNodes())
            {
                var btn = CreateButton(container, "", () => OnMapNodeClicked(node), NodeTypeColor.GetValueOrDefault(node.NodeType, new Color(0.3f, 0.3f, 0.3f)), fontSize: 16);
                AddLayoutElement(btn.GetComponent<RectTransform>(), preferredWidth: 220, preferredHeight: 100);
                var label = btn.GetComponentInChildren<Text>();
                label.text = $"{node.DisplayLabel}\n{NodeFlavor(node.NodeType)}";
            }
        }

        private static string NodeFlavor(MapNodeType type) => type switch
        {
            MapNodeType.Combat => "A fight awaits",
            MapNodeType.Elite => "Tougher - better reward",
            MapNodeType.Boss => "The biome's guardian",
            MapNodeType.KinShrine => "Choose a card of your Kin",
            MapNodeType.KipShop => "Buy, upgrade, remove",
            MapNodeType.Treasure => "Fragments or a relic",
            MapNodeType.Healing => "Rest and recover",
            MapNodeType.MysteryEvent => "Unknown...",
            MapNodeType.FractureEvent => "The Fracture stirs",
            MapNodeType.BrambleEvent => "Bramble appears",
            _ => "",
        };

        private void OnMapNodeClicked(MapNode node)
        {
            _map.TryMoveTo(node.Id);
            switch (node.NodeType)
            {
                case MapNodeType.Combat:
                    var basicEnemies = _rng.Next(2) == 0
                        ? new List<EnemyCombatant> { DefaultContent.CreateMeleeEnemy() }
                        : new List<EnemyCombatant> { DefaultContent.CreateMeleeEnemy(), DefaultContent.CreateRangedEnemy() };
                    StartCombatForNode(RewardNodeType.Combat, basicEnemies);
                    break;
                case MapNodeType.Elite:
                    StartCombatForNode(RewardNodeType.Elite, new List<EnemyCombatant> { DefaultContent.CreateElite() });
                    break;
                case MapNodeType.Boss:
                    StartCombatForNode(RewardNodeType.Boss, new List<EnemyCombatant> { DefaultContent.CreateBoss() });
                    break;
                case MapNodeType.Treasure:
                    ShowTreasureReward();
                    break;
                case MapNodeType.Healing:
                    int healed = Mathf.Min(15, _player.MaxHP - _player.CurrentHP);
                    _player.Heal(15);
                    ShowMessage("A Healing Area", $"Rook rests a while.\n\nHealed {healed} HP.", 0, AfterNodeResolved);
                    break;
                case MapNodeType.KinShrine:
                    ShowKinShrineReward();
                    break;
                case MapNodeType.KipShop:
                    ShowShop();
                    break;
                case MapNodeType.MysteryEvent:
                case MapNodeType.FractureEvent:
                case MapNodeType.BrambleEvent:
                    ShowPlaceholderEvent(node.NodeType);
                    break;
            }
        }

        private void AfterNodeResolved()
        {
            _map.MarkCurrentNodeCompleted();
            if (_map.ActComplete) ShowRunEnd(true);
            else ShowMapScreen();
        }

        // ============================================================
        // Combat screen (mostly the same engine wiring as before)
        // ============================================================

        private void StartCombatForNode(RewardNodeType rewardType, List<EnemyCombatant> enemies)
        {
            _pendingRewardType = rewardType;
            _player.ClearCombatScopedStatuses(); // HP/relics persist; Growth/Thorns/Burn don't carry between fights.
            _logLines.Clear();
            _combat = new CombatManager(_player, enemies, _runState.Deck, _rng, _runState.Relics);
            _combat.OnLog += AppendLog;
            _selectedTarget = enemies.FirstOrDefault();

            BuildEnemyPanels(enemies);
            ShowScreen("Combat");
            _combat.StartCombat();
            RefreshCombatUI();
            RefreshHUD();
        }

        private void AppendLog(string line)
        {
            _logLines.Add(line);
            if (_logLines.Count > 200) _logLines.RemoveAt(0);
        }

        /// <summary>Click handler for a hand card: plays a brief "cast" animation on the
        /// clicked card BEFORE resolving it, so the card is actually seen being used
        /// rather than instantly vanishing into a rebuilt hand.</summary>
        private void OnCardClicked(CardData card, RectTransform visual)
        {
            if (_cardActionInProgress) return;
            StartCoroutine(PlayCardSequence(card, visual));
        }

        private IEnumerator PlayCardSequence(CardData card, RectTransform visual)
        {
            _cardActionInProgress = true;
            PlaySfx("CardPlay");
            yield return PlayCardCastAnimation(visual);

            var hpBefore = _combat.Enemies.ToDictionary(e => e, e => e.CurrentHP);
            bool hasBlockEffect = card.Effects.Any(e => e.Kind == CardEffectKind.Block);
            _combat.TryPlayCard(card, _selectedTarget);
            RefreshCombatUI(); // Destroys/rebuilds the hand - visual (now used) is gone after this.

            bool anyHit = false;
            foreach (var enemy in _combat.Enemies)
            {
                if (hpBefore.TryGetValue(enemy, out var before) && enemy.CurrentHP < before && _enemyUI.TryGetValue(enemy, out var ui))
                {
                    anyHit = true;
                    StopAndStartTween(ui.panelImage.rectTransform, PunchScale(ui.panelImage.rectTransform));
                    StartCoroutine(FlashColor(ui.panelImage, new Color(1f, 0.3f, 0.3f), ui.panelImage.color));
                }
            }
            if (anyHit) PlaySfx("Hit");
            else if (hasBlockEffect) PlaySfx("Block");

            _cardActionInProgress = false;
        }

        /// <summary>Quick punch-up-and-forward before the card actually resolves.</summary>
        private static IEnumerator PlayCardCastAnimation(RectTransform visual)
        {
            const float duration = 0.15f;
            Vector3 startScale = visual.localScale;
            Vector2 startPos = visual.anchoredPosition;
            Vector3 peakScale = startScale * 1.25f;
            Vector2 peakPos = startPos + new Vector2(0, 50);
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / duration);
                if (visual == null) yield break;
                visual.localScale = Vector3.Lerp(startScale, peakScale, p);
                visual.anchoredPosition = Vector2.Lerp(startPos, peakPos, p);
                yield return null;
            }
        }

        private void OnEnemyClicked(EnemyCombatant enemy)
        {
            if (enemy.IsDead || _cardActionInProgress) return;
            _selectedTarget = enemy;
            RefreshCombatUI();
        }

        private void OnEndTurnClicked()
        {
            if (_cardActionInProgress) return;
            int hpBefore = _combat.Player.CurrentHP;
            // Snapshot BEFORE resolving - by the time EndPlayerTurn() returns, each enemy's
            // NextIntent has already been overwritten with what they'll do NEXT turn, so
            // this is the only chance to know who actually attacked just now.
            var attackers = _combat.Enemies.Where(e => !e.IsDead && e.NextIntent.Category == IntentCategory.Attack).ToList();

            _combat.EndPlayerTurn();
            if (_combat.Outcome == CombatOutcome.Ongoing && (_selectedTarget == null || _selectedTarget.IsDead))
                _selectedTarget = _combat.Enemies.FirstOrDefault(e => !e.IsDead);
            RefreshCombatUI();

            if (_combat.Player.CurrentHP < hpBefore)
            {
                StopAndStartTween(_playerStatsText.rectTransform, PunchScale(_playerStatsText.rectTransform));
                StartCoroutine(FlashColor(_playerStatsText, new Color(1f, 0.35f, 0.35f), Color.white));
            }

            StartCoroutine(PlayEnemyAttackAnimations(attackers));
        }

        /// <summary>Staggers a lunge + Hit SFX per attacking enemy so multiple attacks in
        /// one turn read as a sequence of blows rather than one simultaneous jolt. The
        /// underlying numbers are already resolved (see the comment above) - this is
        /// purely the visual/audio follow-through.</summary>
        private IEnumerator PlayEnemyAttackAnimations(List<EnemyCombatant> attackers)
        {
            foreach (var enemy in attackers)
            {
                if (_enemyUI.TryGetValue(enemy, out var ui))
                {
                    var rt = ui.spriteImage != null ? ui.spriteImage.rectTransform : ui.panelImage.rectTransform;
                    PlaySfx("Hit");
                    StartCoroutine(EnemyLunge(rt));
                }
                yield return new WaitForSecondsRealtime(0.15f);
            }
        }

        /// <summary>Enemy lunges toward the player (down, since the player's HUD/hand sit
        /// below the enemy row) and back, with a scale punch for impact.</summary>
        private static IEnumerator EnemyLunge(RectTransform rt)
        {
            const float legDuration = 0.12f;
            Vector2 startPos = rt.anchoredPosition;
            Vector3 startScale = rt.localScale;
            Vector2 lungePos = startPos + new Vector2(0, -18);
            Vector3 lungeScale = startScale * 1.12f;

            float t = 0f;
            while (t < legDuration)
            {
                t += Time.unscaledDeltaTime;
                float p = t / legDuration;
                if (rt == null) yield break;
                rt.anchoredPosition = Vector2.Lerp(startPos, lungePos, p);
                rt.localScale = Vector3.Lerp(startScale, lungeScale, p);
                yield return null;
            }
            t = 0f;
            while (t < legDuration)
            {
                t += Time.unscaledDeltaTime;
                float p = t / legDuration;
                if (rt == null) yield break;
                rt.anchoredPosition = Vector2.Lerp(lungePos, startPos, p);
                rt.localScale = Vector3.Lerp(lungeScale, startScale, p);
                yield return null;
            }
            if (rt != null) { rt.anchoredPosition = startPos; rt.localScale = startScale; }
        }

        private void OnCombatResultContinue()
        {
            _resultOverlay.SetActive(false); // Otherwise it stays drawn over whatever screen we navigate to next.
            if (_combat.Outcome == CombatOutcome.Victory)
            {
                var reward = CombatReward.Generate(_pendingRewardType, _cardPool, _relicPool, _rng);
                ShowCombatReward(reward);
            }
            else
            {
                ShowRunEnd(false);
            }
        }

        private void RefreshCombatUI()
        {
            var p = _combat.Player;
            _playerStatsText.text = $"Rook   HP {p.CurrentHP}/{p.MaxHP}   Block {p.Block}   Energy {p.Energy}/{p.MaxEnergy}   Turn {_combat.TurnCount}" +
                (p.GetStacks(StatusEffectType.Growth) > 0 ? $"   Growth {p.GetStacks(StatusEffectType.Growth)}" : "") +
                (p.GetStacks(StatusEffectType.Thorns) > 0 ? $"   Thorns {p.GetStacks(StatusEffectType.Thorns)}" : "") +
                (p.GetStacks(StatusEffectType.Burn) > 0 ? $"   Burn {p.GetStacks(StatusEffectType.Burn)}" : "");

            foreach (var enemy in _combat.Enemies)
            {
                if (!_enemyUI.TryGetValue(enemy, out var ui)) continue;
                bool selected = enemy == _selectedTarget;
                ui.panelImage.color = enemy.IsDead ? new Color(0.08f, 0.08f, 0.08f) : selected ? TargetSelectedBg : TargetBg;
                if (ui.spriteImage != null) ui.spriteImage.color = enemy.IsDead ? new Color(1, 1, 1, 0.25f) : Color.white;
                ui.text.text = enemy.IsDead
                    ? $"{enemy.DisplayName}\n(defeated)"
                    : (selected ? "▶ TARGETED ◀\n" : "(click to target)\n") +
                      $"{enemy.DisplayName}\nHP {enemy.CurrentHP}/{enemy.MaxHP}   Block {enemy.Block}\nWill do: {DescribeIntent(enemy.NextIntent)}";
            }

            RebuildHand();
            _logText.text = string.Join("\n", _logLines.TakeLast(6));

            bool ongoing = _combat.Outcome == CombatOutcome.Ongoing;
            _endTurnButton.interactable = ongoing;
            bool wasAlreadyShown = _resultOverlay.activeSelf;
            _resultOverlay.SetActive(!ongoing);
            if (!ongoing)
            {
                _resultText.text = _combat.Outcome == CombatOutcome.Victory ? "VICTORY\n\n(click to continue)" : "DEFEATED\n\n(click to continue)";
                if (!wasAlreadyShown) PlaySfx(_combat.Outcome == CombatOutcome.Victory ? "Victory" : "Defeat"); // Only once, on the transition.
            }
            RefreshHUD();
        }

        private static string DescribeIntent(EnemyIntent intent) => intent.Category switch
        {
            IntentCategory.Attack => $"ATTACK {intent.Value}",
            IntentCategory.Block => $"BLOCK {intent.Value}",
            IntentCategory.Buff => $"BUFF ({intent.Description ?? "self"})",
            IntentCategory.Debuff => $"DEBUFF ({intent.Description ?? "?"})",
            IntentCategory.Special => $"SPECIAL: {intent.Description ?? "?"}",
            _ => "?",
        };

        private void RebuildHand()
        {
            for (int i = _handContainer.childCount - 1; i >= 0; i--) DestroyImmediate(_handContainer.GetChild(i).gameObject);
            bool ongoing = _combat.Outcome == CombatOutcome.Ongoing;

            int index = 0;
            foreach (var card in _combat.Deck.Hand)
            {
                bool affordable = ongoing && _combat.Player.CanAfford(card.EnergyCost);
                CreateCardButton(_handContainer, card, affordable, (visual) => OnCardClicked(card, visual), entranceDelay: index * 0.05f);
                index++;
            }
        }

        private void BuildEnemyPanels(List<EnemyCombatant> enemies)
        {
            for (int i = _enemyRow.childCount - 1; i >= 0; i--) DestroyImmediate(_enemyRow.GetChild(i).gameObject);
            _enemyUI.Clear();

            foreach (var enemy in enemies)
            {
                var panelRT = CreateUIObject(enemy.DisplayName, _enemyRow);
                AddLayoutElement(panelRT, preferredWidth: 260, preferredHeight: 220);
                var img = panelRT.gameObject.AddComponent<Image>();
                var btn = panelRT.gameObject.AddComponent<Button>();
                btn.targetGraphic = img;
                var capturedEnemy = enemy;
                btn.onClick.AddListener(() => OnEnemyClicked(capturedEnemy));

                Image spriteImg = null;
                var sprite = GetEnemySprite(enemy.DisplayName);
                if (sprite != null)
                {
                    var spriteRT = CreateUIObject("Sprite", panelRT);
                    spriteRT.anchorMin = new Vector2(0.5f, 1f);
                    spriteRT.anchorMax = new Vector2(0.5f, 1f);
                    spriteRT.pivot = new Vector2(0.5f, 1f);
                    spriteRT.sizeDelta = new Vector2(130, 130);
                    spriteRT.anchoredPosition = new Vector2(0, -8);
                    spriteImg = spriteRT.gameObject.AddComponent<Image>();
                    spriteImg.sprite = sprite;
                    spriteImg.preserveAspect = true;
                    spriteImg.raycastTarget = false;
                }

                var textRT = CreateUIObject("Text", panelRT);
                textRT.anchorMin = new Vector2(0, 0);
                textRT.anchorMax = new Vector2(1, sprite != null ? 0.42f : 1f);
                textRT.offsetMin = Vector2.zero;
                textRT.offsetMax = Vector2.zero;
                var text = textRT.gameObject.AddComponent<Text>();
                text.font = BuiltinFont();
                text.fontSize = 14;
                text.alignment = TextAnchor.MiddleCenter;
                text.color = Color.white;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.raycastTarget = false;

                _enemyUI[enemy] = (img, spriteImg, text);
            }
        }

        // ============================================================
        // Reward screen (Fix 11/12: auto-grant Fragments/guaranteed relics,
        // card choices always include Skip)
        // ============================================================

        private void ShowCombatReward(CombatReward reward)
        {
            _runState.AddFragments(reward.FragmentsAwarded);
            if (reward.RelicAwarded != null) _runState.AddRelic(reward.RelicAwarded);
            ShowRewardScreen($"You earned {reward.FragmentsAwarded} Fragments" + (reward.RelicAwarded != null ? $" and the relic '{reward.RelicAwarded.RelicName}'!" : "!"),
                reward.CardChoices, null, AfterNodeResolved);
        }

        private void ShowTreasureReward()
        {
            var reward = CombatReward.Generate(RewardNodeType.Treasure, _cardPool, _relicPool, _rng);
            // Fix 11: Treasure is a CHOICE between Fragments or a relic, not both automatically.
            ShowChoiceScreen("A Treasure!", "Take one:",
                ($"{reward.FragmentsAwarded} Fragments", () => { _runState.AddFragments(reward.FragmentsAwarded); AfterNodeResolved(); }),
                reward.RelicAwarded != null
                    ? ($"Relic: {reward.RelicAwarded.RelicName}\n{reward.RelicAwarded.Description}", (UnityAction)(() => { _runState.AddRelic(reward.RelicAwarded); AfterNodeResolved(); }))
                    : (null, null));
        }

        private void ShowKinShrineReward()
        {
            // No Kin-selection UI yet - defaults to whichever prototype Kin the player has
            // invested in most, so the shrine reinforces the build already underway.
            var kins = new[] { KinType.Bubblo, KinType.Voltrix, KinType.Mossmaw };
            var chosenKin = kins.OrderByDescending(k => _runState.CountOfKin(k)).First();
            var reward = CombatReward.GenerateKinShrineReward(chosenKin, _cardPool, _rng);
            ShowRewardScreen($"The shrine resonates with {chosenKin}.", reward.CardChoices, null, AfterNodeResolved);
        }

        /// <summary>Generic "up to 3 cards + Skip" reward screen used by Combat/Elite/Boss/KinShrine rewards.</summary>
        private void ShowRewardScreen(string message, List<CardData> cardChoices, RelicData relicChoice, UnityAction onDone)
        {
            ShowScreen("Reward");
            RefreshHUD();
            var screen = _screens["Reward"];
            screen.transform.Find("Message").GetComponent<Text>().text = message;

            var container = screen.transform.Find("CardButtons");
            for (int i = container.childCount - 1; i >= 0; i--) DestroyImmediate(container.GetChild(i).gameObject);

            int rewardIndex = 0;
            foreach (var card in cardChoices)
            {
                CreateCardButton(container, card, true, (_) => { _runState.AddCardToDeck(card); onDone(); }, entranceDelay: rewardIndex * 0.08f);
                rewardIndex++;
            }

            var skipBtn = screen.transform.Find("SkipButton").GetComponent<Button>();
            skipBtn.onClick.RemoveAllListeners();
            skipBtn.onClick.AddListener(() => onDone());
        }

        /// <summary>Up to 2 mutually-exclusive choices (used by Treasure) - plain text buttons, not cards.</summary>
        private void ShowChoiceScreen(string title, string message, (string label, UnityAction onClick) optionA, (string label, UnityAction onClick) optionB)
        {
            ShowScreen("Choice");
            RefreshHUD();
            var screen = _screens["Choice"];
            screen.transform.Find("Title").GetComponent<Text>().text = title;
            screen.transform.Find("Message").GetComponent<Text>().text = message;

            var container = screen.transform.Find("Options");
            for (int i = container.childCount - 1; i >= 0; i--) DestroyImmediate(container.GetChild(i).gameObject);

            foreach (var option in new[] { optionA, optionB })
            {
                if (option.label == null) continue;
                var btn = CreateButton(container, option.label, option.onClick, new Color(0.2f, 0.2f, 0.27f), fontSize: 15);
                AddLayoutElement(btn.GetComponent<RectTransform>(), preferredWidth: 320, preferredHeight: 120);
            }
        }

        // ============================================================
        // Kip's shop
        // ============================================================

        private void ShowShop()
        {
            ShowScreen("Shop");
            RefreshHUD();
            RefreshShopScreen();
        }

        private void RefreshShopScreen()
        {
            var screen = _screens["Shop"];
            var offer = KipShop.GenerateOffer(_cardPool, _relicPool, _rng);

            var cardsContainer = screen.transform.Find("CardsForSale");
            for (int i = cardsContainer.childCount - 1; i >= 0; i--) DestroyImmediate(cardsContainer.GetChild(i).gameObject);
            foreach (var (card, price) in offer.CardsForSale)
            {
                bool affordable = _runState.Fragments >= price;
                var cardBtn = CreateCardButton(cardsContainer, card, affordable, (_) =>
                {
                    if (KipShop.TryBuyCard(_runState, card, price)) RefreshShopScreen();
                });
                var priceTag = CreateText(cardBtn.transform, $"{price} Fragments", 14, TextAnchor.UpperCenter, Color.yellow);
                priceTag.rectTransform.anchorMin = new Vector2(0f, 0f);
                priceTag.rectTransform.anchorMax = new Vector2(1f, 0f);
                priceTag.rectTransform.pivot = new Vector2(0.5f, 1f);
                priceTag.rectTransform.sizeDelta = new Vector2(0, 22);
                priceTag.rectTransform.anchoredPosition = new Vector2(0, -6); // Clearly below the card, not overlapping its bottom border.
            }

            var relicSlot = screen.transform.Find("RelicForSale").GetComponent<Button>();
            var relicLabel = relicSlot.GetComponentInChildren<Text>();
            if (offer.RelicForSale != null)
            {
                relicLabel.text = $"Relic: {offer.RelicForSale.RelicName}\n{offer.RelicForSale.Description}\nPrice: {offer.RelicPrice}";
                relicSlot.interactable = _runState.Fragments >= offer.RelicPrice;
                relicSlot.onClick.RemoveAllListeners();
                relicSlot.onClick.AddListener(() => { if (KipShop.TryBuyRelic(_runState, offer.RelicForSale, offer.RelicPrice)) RefreshShopScreen(); });
            }
            else
            {
                relicLabel.text = "(no relic today)";
                relicSlot.interactable = false;
            }

            // Upgrade / remove: up to 8 deck cards, to keep the screen manageable.
            var deckContainer = screen.transform.Find("DeckActions");
            for (int i = deckContainer.childCount - 1; i >= 0; i--) DestroyImmediate(deckContainer.GetChild(i).gameObject);
            int removalPrice = KipShop.CurrentRemovalPrice(_runState);
            foreach (var deckCard in _runState.Deck.Take(8))
            {
                var row = CreateUIObject(deckCard.CardName, deckContainer);
                AddLayoutElement(row, preferredHeight: 34);
                var rowLayout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
                rowLayout.spacing = 6;
                CreateText(row, deckCard.CardName, 13, TextAnchor.MiddleLeft);

                if (deckCard.CanUpgrade)
                {
                    var upBtn = CreateButton(row, $"Upgrade ({KipShop.UpgradePrice}g)", () => { if (KipShop.TryBuyUpgrade(_runState, deckCard)) RefreshShopScreen(); }, new Color(0.2f, 0.3f, 0.2f), fontSize: 11);
                    upBtn.interactable = _runState.Fragments >= KipShop.UpgradePrice;
                    AddLayoutElement(upBtn.GetComponent<RectTransform>(), preferredWidth: 110, preferredHeight: 28);
                }
                var removeBtn = CreateButton(row, $"Remove ({removalPrice}g)", () => { if (KipShop.TryBuyRemoval(_runState, deckCard)) RefreshShopScreen(); }, new Color(0.3f, 0.2f, 0.2f), fontSize: 11);
                removeBtn.interactable = _runState.Fragments >= removalPrice;
                AddLayoutElement(removeBtn.GetComponent<RectTransform>(), preferredWidth: 110, preferredHeight: 28);
            }

            var leaveBtn = screen.transform.Find("LeaveButton").GetComponent<Button>();
            leaveBtn.onClick.RemoveAllListeners();
            leaveBtn.onClick.AddListener(AfterNodeResolved);
            RefreshHUD();
        }

        // ============================================================
        // Placeholder narrative events (no content authored yet)
        // ============================================================

        private void ShowPlaceholderEvent(MapNodeType type)
        {
            string title = type switch
            {
                MapNodeType.MysteryEvent => "A Mystery",
                MapNodeType.FractureEvent => "The Fracture Stirs",
                MapNodeType.BrambleEvent => "Bramble Appears",
                _ => "Something Happens",
            };
            int consolation = _rng.Next(5, 16);
            _runState.AddFragments(consolation);
            ShowMessage(title, "Talune holds its secrets a while longer.\n\n(No content authored for this event yet.)", consolation, AfterNodeResolved);
        }

        private void ShowMessage(string title, string body, int fragmentsGranted, UnityAction onContinue)
        {
            ShowScreen("Message");
            RefreshHUD();
            var screen = _screens["Message"];
            screen.transform.Find("Title").GetComponent<Text>().text = title;
            screen.transform.Find("Body").GetComponent<Text>().text = body + (fragmentsGranted > 0 ? $"\n\n+{fragmentsGranted} Fragments" : "");
            var btn = screen.transform.Find("ContinueButton").GetComponent<Button>();
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(onContinue);
        }

        // ============================================================
        // Run end
        // ============================================================

        private void ShowRunEnd(bool success)
        {
            ShowScreen("RunEnd");
            var screen = _screens["RunEnd"];
            screen.transform.Find("Title").GetComponent<Text>().text = success ? "ACT COMPLETE!" : "RUN FAILED";
            screen.transform.Find("Summary").GetComponent<Text>().text =
                $"Deck: {_runState.Deck.Count} cards\nRelics: {_runState.Relics.Count}\nFragments: {_runState.Fragments}";
        }

        // ============================================================
        // Screen management
        // ============================================================

        private void ShowScreen(string name)
        {
            foreach (var kvp in _screens) kvp.Value.SetActive(kvp.Key == name);
        }

        // ============================================================
        // UI construction
        // ============================================================

        private void BuildUI()
        {
            if (FindAnyObjectByType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

            var canvasGO = new GameObject("CombatCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGO.GetComponent<Canvas>();
            var cam = Camera.main;
            if (cam != null)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = cam;
                canvas.planeDistance = 1f;
            }
            else
            {
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            }
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900);

            var root = CreateUIObject("Root", canvasGO.transform);
            StretchFull(root);
            var rootLayout = root.gameObject.AddComponent<VerticalLayoutGroup>();
            rootLayout.padding = new RectOffset(16, 16, 16, 16);
            rootLayout.spacing = 10;
            rootLayout.childForceExpandWidth = true;
            rootLayout.childForceExpandHeight = false;
            rootLayout.childControlWidth = true;
            rootLayout.childControlHeight = true;

            // HUD - always visible regardless of screen.
            var hudRT = CreateUIObject("HUD", root);
            AddLayoutElement(hudRT, preferredHeight: 32);
            var hudImg = hudRT.gameObject.AddComponent<Image>();
            hudImg.color = new Color(0.08f, 0.08f, 0.1f);
            _hudText = CreateText(hudRT, "", 16, TextAnchor.MiddleLeft, new Color(0.9f, 0.85f, 0.6f));
            StretchFull(_hudText.rectTransform);
            _hudText.rectTransform.offsetMin += new Vector2(10, 0);

            // Screen container - exactly one child active at a time.
            var containerRT = CreateUIObject("ScreenContainer", root);
            AddLayoutElement(containerRT, flexibleHeight: 1);
            _screenContainer = containerRT.gameObject;

            BuildMapScreen(containerRT);
            BuildCombatScreen(containerRT);
            BuildRewardScreen(containerRT);
            BuildChoiceScreen(containerRT);
            BuildShopScreen(containerRT);
            BuildMessageScreen(containerRT);
            BuildRunEndScreen(containerRT);
        }

        private void BuildMapScreen(Transform parent)
        {
            var screen = CreateUIObject("MapScreen", parent);
            StretchFull(screen);
            var layout = screen.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 20;
            layout.padding = new RectOffset(20, 20, 30, 20);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var title = CreateText(screen, "Choose your next step:", 22, TextAnchor.MiddleCenter);
            title.name = "Title";
            AddLayoutElement(title.rectTransform, preferredHeight: 40);

            var buttonsRT = CreateUIObject("NodeButtons", screen);
            AddLayoutElement(buttonsRT, flexibleHeight: 1);
            var buttonsLayout = buttonsRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            buttonsLayout.spacing = 24;
            buttonsLayout.childAlignment = TextAnchor.MiddleCenter;
            buttonsLayout.childForceExpandWidth = false;
            buttonsLayout.childForceExpandHeight = false;

            RegisterScreen("Map", screen);
        }

        private void BuildCombatScreen(Transform parent)
        {
            var screen = CreateUIObject("CombatScreen", parent);
            StretchFull(screen);
            var layout = screen.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 10;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            var instructionsRT = CreateUIObject("Instructions", screen);
            AddLayoutElement(instructionsRT, preferredHeight: 40);
            var instructionsImg = instructionsRT.gameObject.AddComponent<Image>();
            instructionsImg.color = new Color(0.10f, 0.13f, 0.10f);
            var instructionsText = CreateText(instructionsRT, "HOW TO PLAY:  1) Click an enemy panel below to target it.   2) Click a card in your hand to play it (colored by type: red=Attack, blue=Guard, green=Skill).   3) Click END TURN when done.   \"Will do:\" on an enemy shows what it plays next.",
                14, TextAnchor.MiddleCenter, new Color(0.85f, 0.9f, 0.85f));
            StretchFull(instructionsText.rectTransform);

            var enemyRowRT = CreateUIObject("EnemyRow", screen);
            AddLayoutElement(enemyRowRT, preferredHeight: 230);
            var enemyRowLayout = enemyRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            enemyRowLayout.spacing = 16;
            enemyRowLayout.childAlignment = TextAnchor.MiddleCenter;
            _enemyRow = enemyRowRT;

            var logPanelRT = CreateUIObject("LogPanel", screen);
            AddLayoutElement(logPanelRT, flexibleHeight: 1);
            var bgImg = logPanelRT.gameObject.AddComponent<Image>();
            bgImg.color = PanelBg;
            _logText = CreateText(logPanelRT, "", 15, TextAnchor.UpperLeft);
            StretchFull(_logText.rectTransform);
            _logText.rectTransform.offsetMin += new Vector2(10, 6);
            _logText.rectTransform.offsetMax += new Vector2(-10, -6);

            var bottomBarRT = CreateUIObject("BottomBar", screen);
            AddLayoutElement(bottomBarRT, preferredHeight: 260);
            var bottomLayout = bottomBarRT.gameObject.AddComponent<VerticalLayoutGroup>();
            bottomLayout.spacing = 8;
            bottomLayout.childForceExpandWidth = true;
            bottomLayout.childForceExpandHeight = false;

            var statsRowRT = CreateUIObject("StatsRow", bottomBarRT);
            AddLayoutElement(statsRowRT, preferredHeight: 30);
            var statsLayout = statsRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            statsLayout.childForceExpandWidth = false;
            _playerStatsText = CreateText(statsRowRT, "", 18, TextAnchor.MiddleLeft);
            AddLayoutElement(_playerStatsText.rectTransform, flexibleWidth: 1, preferredHeight: 30);
            _endTurnButton = CreateButton(statsRowRT, "END TURN", OnEndTurnClicked, new Color(0.25f, 0.2f, 0.1f));
            AddLayoutElement(_endTurnButton.GetComponent<RectTransform>(), preferredWidth: 160, preferredHeight: 30);

            var handRowRT = CreateUIObject("HandRow", bottomBarRT);
            AddLayoutElement(handRowRT, flexibleHeight: 1);
            var handLayout = handRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            handLayout.spacing = -35;
            handLayout.childAlignment = TextAnchor.LowerCenter;
            handLayout.childForceExpandWidth = false;
            handLayout.childForceExpandHeight = false;
            _handContainer = handRowRT;

            var overlayRT = CreateUIObject("ResultOverlay", screen.transform.parent); // Overlay sits above ScreenContainer, not inside CombatScreen's own layout.
            StretchFull(overlayRT);
            var overlayImg = overlayRT.gameObject.AddComponent<Image>();
            overlayImg.color = new Color(0, 0, 0, 0.85f);
            _resultOverlayButton = overlayRT.gameObject.AddComponent<Button>();
            _resultOverlayButton.targetGraphic = overlayImg;
            _resultOverlayButton.onClick.AddListener(OnCombatResultContinue);
            _resultText = CreateText(overlayRT, "", 64, TextAnchor.MiddleCenter, Color.white);
            StretchFull(_resultText.rectTransform);
            _resultOverlay = overlayRT.gameObject;
            _resultOverlay.SetActive(false);

            RegisterScreen("Combat", screen);
        }

        private void BuildRewardScreen(Transform parent)
        {
            var screen = CreateUIObject("RewardScreen", parent);
            StretchFull(screen);
            var layout = screen.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 16;
            layout.padding = new RectOffset(20, 20, 30, 20);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var message = CreateText(screen, "", 20, TextAnchor.MiddleCenter, new Color(0.9f, 0.85f, 0.6f));
            message.name = "Message";
            AddLayoutElement(message.rectTransform, preferredHeight: 36);

            var subtitle = CreateText(screen, "Choose a card to add to your deck, or skip:", 16, TextAnchor.MiddleCenter);
            AddLayoutElement(subtitle.rectTransform, preferredHeight: 28);

            var cardsRT = CreateUIObject("CardButtons", screen);
            AddLayoutElement(cardsRT, flexibleHeight: 1);
            var cardsLayout = cardsRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            cardsLayout.spacing = 16;
            cardsLayout.childAlignment = TextAnchor.MiddleCenter;
            cardsLayout.childForceExpandWidth = false;
            cardsLayout.childForceExpandHeight = false;

            var skipBtn = CreateButton(screen, "SKIP", null, new Color(0.25f, 0.2f, 0.2f));
            skipBtn.name = "SkipButton";
            AddLayoutElement(skipBtn.GetComponent<RectTransform>(), preferredWidth: 180, preferredHeight: 40);

            RegisterScreen("Reward", screen);
        }

        private void BuildChoiceScreen(Transform parent)
        {
            var screen = CreateUIObject("ChoiceScreen", parent);
            StretchFull(screen);
            var layout = screen.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 16;
            layout.padding = new RectOffset(20, 20, 30, 20);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var title = CreateText(screen, "", 22, TextAnchor.MiddleCenter);
            title.name = "Title";
            AddLayoutElement(title.rectTransform, preferredHeight: 36);
            var message = CreateText(screen, "", 16, TextAnchor.MiddleCenter);
            message.name = "Message";
            AddLayoutElement(message.rectTransform, preferredHeight: 28);

            var optionsRT = CreateUIObject("Options", screen);
            AddLayoutElement(optionsRT, flexibleHeight: 1);
            var optionsLayout = optionsRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            optionsLayout.spacing = 24;
            optionsLayout.childAlignment = TextAnchor.MiddleCenter;
            optionsLayout.childForceExpandWidth = false;
            optionsLayout.childForceExpandHeight = false;

            RegisterScreen("Choice", screen);
        }

        private void BuildShopScreen(Transform parent)
        {
            var screen = CreateUIObject("ShopScreen", parent);
            StretchFull(screen);
            var layout = screen.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 14;
            layout.padding = new RectOffset(20, 20, 20, 20);
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var title = CreateText(screen, "Kip's Shop", 22, TextAnchor.MiddleCenter);
            AddLayoutElement(title.rectTransform, preferredHeight: 32);

            var cardsRT = CreateUIObject("CardsForSale", screen);
            AddLayoutElement(cardsRT, preferredHeight: 260);
            var cardsLayout = cardsRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            cardsLayout.spacing = 16;
            cardsLayout.childAlignment = TextAnchor.UpperCenter;
            cardsLayout.childForceExpandWidth = false;
            cardsLayout.childForceExpandHeight = false;

            var relicBtn = CreateButton(screen, "", null, new Color(0.3f, 0.25f, 0.1f), fontSize: 13);
            relicBtn.name = "RelicForSale";
            AddLayoutElement(relicBtn.GetComponent<RectTransform>(), preferredHeight: 70);

            var deckActionsLabel = CreateText(screen, "Your deck (upgrade/remove):", 15, TextAnchor.MiddleLeft);
            AddLayoutElement(deckActionsLabel.rectTransform, preferredHeight: 24);

            var deckRT = CreateUIObject("DeckActions", screen);
            AddLayoutElement(deckRT, flexibleHeight: 1);
            var deckLayout = deckRT.gameObject.AddComponent<VerticalLayoutGroup>();
            deckLayout.spacing = 4;
            deckLayout.childForceExpandWidth = true;
            deckLayout.childForceExpandHeight = false;

            var leaveBtn = CreateButton(screen, "LEAVE SHOP", null, new Color(0.2f, 0.2f, 0.2f));
            leaveBtn.name = "LeaveButton";
            AddLayoutElement(leaveBtn.GetComponent<RectTransform>(), preferredHeight: 36);

            RegisterScreen("Shop", screen);
        }

        private void BuildMessageScreen(Transform parent)
        {
            var screen = CreateUIObject("MessageScreen", parent);
            StretchFull(screen);
            var layout = screen.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 20;
            layout.padding = new RectOffset(40, 40, 60, 40);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var title = CreateText(screen, "", 26, TextAnchor.MiddleCenter, new Color(0.9f, 0.85f, 0.6f));
            title.name = "Title";
            AddLayoutElement(title.rectTransform, preferredHeight: 40);
            var body = CreateText(screen, "", 17, TextAnchor.UpperCenter);
            body.name = "Body";
            AddLayoutElement(body.rectTransform, flexibleHeight: 1);
            var continueBtn = CreateButton(screen, "CONTINUE", null, new Color(0.2f, 0.2f, 0.2f));
            continueBtn.name = "ContinueButton";
            AddLayoutElement(continueBtn.GetComponent<RectTransform>(), preferredHeight: 40);

            RegisterScreen("Message", screen);
        }

        private void BuildRunEndScreen(Transform parent)
        {
            var screen = CreateUIObject("RunEndScreen", parent);
            StretchFull(screen);
            var layout = screen.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 20;
            layout.padding = new RectOffset(40, 40, 80, 40);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var title = CreateText(screen, "", 48, TextAnchor.MiddleCenter, new Color(0.9f, 0.85f, 0.6f));
            title.name = "Title";
            AddLayoutElement(title.rectTransform, preferredHeight: 70);
            var summary = CreateText(screen, "", 18, TextAnchor.UpperCenter);
            summary.name = "Summary";
            AddLayoutElement(summary.rectTransform, flexibleHeight: 1);
            var restartBtn = CreateButton(screen, "START NEW RUN", StartNewRun, new Color(0.2f, 0.3f, 0.2f));
            AddLayoutElement(restartBtn.GetComponent<RectTransform>(), preferredHeight: 44);

            RegisterScreen("RunEnd", screen);
        }

        private void RegisterScreen(string key, RectTransform rt)
        {
            _screens[key] = rt.gameObject;
            rt.gameObject.SetActive(false);
        }

        // --- Card button (shared by hand, rewards, and shop) ---

        private Button CreateCardButton(Transform parent, CardData card, bool affordable, UnityAction<RectTransform> onClick, float entranceDelay = 0f)
        {
            var slot = CreateUIObject(card.CardName, parent);
            AddLayoutElement(slot, preferredWidth: 150, preferredHeight: 210);

            var hitArea = slot.gameObject.AddComponent<Image>();
            hitArea.color = new Color(0, 0, 0, 0);

            var btn = slot.gameObject.AddComponent<Button>();
            btn.targetGraphic = hitArea;
            btn.interactable = affordable;

            var visual = CreateUIObject("Visual", slot);
            StretchFull(visual);
            var sortingCanvas = visual.gameObject.AddComponent<Canvas>();
            sortingCanvas.overrideSorting = false;

            var frameImg = visual.gameObject.AddComponent<Image>();
            frameImg.sprite = _cardFrameSprite;
            frameImg.color = affordable ? Color.white : CardUnaffordableTint;
            frameImg.type = Image.Type.Simple;
            frameImg.raycastTarget = false;

            var icon = GetCardIcon(card.Type);
            if (icon != null)
            {
                var iconRT = CreateUIObject("Icon", visual);
                iconRT.anchorMin = new Vector2(0.20f, 0.50f);
                iconRT.anchorMax = new Vector2(0.80f, 0.86f);
                iconRT.offsetMin = Vector2.zero;
                iconRT.offsetMax = Vector2.zero;
                var iconImg = iconRT.gameObject.AddComponent<Image>();
                iconImg.sprite = icon;
                iconImg.preserveAspect = true;
                iconImg.raycastTarget = false;
                if (!affordable) iconImg.color = new Color(0.6f, 0.6f, 0.6f, 0.7f);
            }

            var tagRT = CreateUIObject("TypeTag", visual);
            tagRT.anchorMin = new Vector2(0.14f, 0.87f);
            tagRT.anchorMax = new Vector2(0.86f, 0.95f);
            tagRT.offsetMin = Vector2.zero;
            tagRT.offsetMax = Vector2.zero;
            var tagImg = tagRT.gameObject.AddComponent<Image>();
            tagImg.color = CardTypeColor[card.Type];
            tagImg.raycastTarget = false;
            var tagText = CreateText(tagRT, card.Type.ToString().ToUpperInvariant(), 11, TextAnchor.MiddleCenter, Color.white);
            tagText.raycastTarget = false;
            StretchFull(tagText.rectTransform);

            var gemRT = CreateUIObject("CostGem", visual);
            gemRT.anchorMin = new Vector2(0f, 0.90f);
            gemRT.anchorMax = new Vector2(0f, 0.90f);
            gemRT.pivot = new Vector2(0.5f, 0.5f);
            gemRT.sizeDelta = new Vector2(30, 30);
            gemRT.anchoredPosition = new Vector2(18, -4);
            var gemImg = gemRT.gameObject.AddComponent<Image>();
            gemImg.color = new Color(0.15f, 0.35f, 0.65f);
            gemImg.raycastTarget = false;
            var gemText = CreateText(gemRT, card.EnergyCost.ToString(), 15, TextAnchor.MiddleCenter, Color.white);
            gemText.raycastTarget = false;
            StretchFull(gemText.rectTransform);

            var bodyRT = CreateUIObject("Body", visual);
            bodyRT.anchorMin = new Vector2(0.14f, 0.08f);
            bodyRT.anchorMax = new Vector2(0.86f, 0.49f);
            bodyRT.offsetMin = Vector2.zero;
            bodyRT.offsetMax = Vector2.zero;
            var kinLabel = card.KinTags.Count > 0 ? $" [{string.Join("+", card.KinTags)}]" : "";
            var bodyText = CreateText(bodyRT, $"{card.CardName}{kinLabel}\n{card.Description}", 12, TextAnchor.UpperCenter, new Color(0.15f, 0.1f, 0.05f));
            bodyText.raycastTarget = false;
            StretchFull(bodyText.rectTransform);

            btn.onClick.AddListener(() => onClick(visual));
            AddHoverRaise(slot, visual, sortingCanvas);
            StartCoroutine(CardEntranceAnimation(visual, entranceDelay));
            return btn;
        }

        /// <summary>Cards pop in from slightly below and undersized instead of just
        /// appearing - reads like they're being dealt/drawn rather than materializing.</summary>
        private static IEnumerator CardEntranceAnimation(RectTransform visual, float delay)
        {
            if (delay > 0) yield return new WaitForSecondsRealtime(delay);
            const float duration = 0.18f;
            Vector3 fromScale = Vector3.one * 0.6f;
            Vector2 fromPos = new(0, -40);
            if (visual == null) yield break;
            visual.localScale = fromScale;
            visual.anchoredPosition = fromPos;

            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / duration);
                if (visual == null) yield break;
                visual.localScale = Vector3.Lerp(fromScale, Vector3.one, p);
                visual.anchoredPosition = Vector2.Lerp(fromPos, Vector2.zero, p);
                yield return null;
            }
            if (visual != null) { visual.localScale = Vector3.one; visual.anchoredPosition = Vector2.zero; }
        }

        private void AddHoverRaise(RectTransform slot, RectTransform visual, Canvas sortingCanvas)
        {
            var trigger = slot.gameObject.AddComponent<EventTrigger>();

            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(_ =>
            {
                sortingCanvas.overrideSorting = true;
                sortingCanvas.sortingOrder = 100;
                StopAndStartTween(visual, TweenCard(visual, new Vector2(0, 24), new Vector3(1.12f, 1.12f, 1f)));
            });
            trigger.triggers.Add(enter);

            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exit.callback.AddListener(_ => StopAndStartTween(visual, TweenCardThenReset(visual, sortingCanvas)));
            trigger.triggers.Add(exit);
        }

        private IEnumerator TweenCardThenReset(RectTransform visual, Canvas sortingCanvas)
        {
            yield return TweenCard(visual, Vector2.zero, Vector3.one);
            if (sortingCanvas != null) sortingCanvas.overrideSorting = false;
        }

        private readonly Dictionary<RectTransform, Coroutine> _activeTweens = new();

        private void StopAndStartTween(RectTransform rt, IEnumerator routine)
        {
            if (_activeTweens.TryGetValue(rt, out var existing) && existing != null) StopCoroutine(existing);
            _activeTweens[rt] = StartCoroutine(routine);
        }

        private static IEnumerator TweenCard(RectTransform rt, Vector2 targetPos, Vector3 targetScale)
        {
            const float duration = 0.12f;
            Vector2 startPos = rt.anchoredPosition;
            Vector3 startScale = rt.localScale;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / duration);
                if (rt == null) yield break;
                rt.anchoredPosition = Vector2.Lerp(startPos, targetPos, p);
                rt.localScale = Vector3.Lerp(startScale, targetScale, p);
                yield return null;
            }
            if (rt != null) { rt.anchoredPosition = targetPos; rt.localScale = targetScale; }
        }

        private IEnumerator PunchScale(RectTransform rt)
        {
            const float duration = 0.18f;
            Vector3 baseScale = Vector3.one;
            Vector3 peak = new(1.08f, 1.08f, 1f);
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float p = t / duration;
                if (rt == null) yield break;
                rt.localScale = p < 0.5f ? Vector3.Lerp(baseScale, peak, p / 0.5f) : Vector3.Lerp(peak, baseScale, (p - 0.5f) / 0.5f);
                yield return null;
            }
            if (rt != null) rt.localScale = baseScale;
        }

        private IEnumerator FlashColor(Graphic graphic, Color flashTo, Color returnTo)
        {
            const float duration = 0.25f;
            graphic.color = flashTo;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                if (graphic == null) yield break;
                graphic.color = Color.Lerp(flashTo, returnTo, t / duration);
                yield return null;
            }
            if (graphic != null) graphic.color = returnTo;
        }

        // --- Small UI helpers ---

        private static RectTransform CreateUIObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void AddLayoutElement(RectTransform rt, float preferredWidth = -1, float preferredHeight = -1, float flexibleWidth = -1, float flexibleHeight = -1)
        {
            var le = rt.gameObject.GetComponent<LayoutElement>() ?? rt.gameObject.AddComponent<LayoutElement>();
            if (preferredWidth >= 0) le.preferredWidth = preferredWidth;
            if (preferredHeight >= 0) le.preferredHeight = preferredHeight;
            if (flexibleWidth >= 0) le.flexibleWidth = flexibleWidth;
            if (flexibleHeight >= 0) le.flexibleHeight = flexibleHeight;
        }

        private static Font BuiltinFont()
        {
            var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return f != null ? f : Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        private static Text CreateText(Transform parent, string content, int fontSize, TextAnchor anchor, Color? color = null)
        {
            var rt = CreateUIObject("Text", parent);
            var text = rt.gameObject.AddComponent<Text>();
            text.font = BuiltinFont();
            text.text = content;
            text.fontSize = fontSize;
            text.alignment = anchor;
            text.color = color ?? Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        private static Button CreateButton(Transform parent, string label, UnityAction onClick, Color bg, int fontSize = 16)
        {
            var rt = CreateUIObject("Button", parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = bg;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            var txt = CreateText(rt, label, fontSize, TextAnchor.MiddleCenter);
            StretchFull(txt.rectTransform);
            if (onClick != null) btn.onClick.AddListener(onClick);
            return btn;
        }
    }
}
