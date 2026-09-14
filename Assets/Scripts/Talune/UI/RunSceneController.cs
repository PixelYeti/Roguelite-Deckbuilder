using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
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
        private RewardNodeType _pendingRewardType;
        private readonly List<string> _logLines = new();
        private readonly Dictionary<EnemyCombatant, (Image panelImage, Image spriteImage, Text text, Image hpFill)> _enemyUI = new();

        // Click-a-card-then-click-a-target flow: a card needing an enemy target waits
        // here until the player picks one (or cancels), rather than requiring a target
        // to be pre-selected before a card can be played.
        private CardData _pendingCard;
        private RectTransform _pendingCardVisual;
        private bool _awaitingTarget;

        // --- Map progression: a real branching board (RunMap.Rows), not just "the next
        // row" - every node is visible so the player can plan ahead, only the ones the
        // current node actually connects to are clickable, and a token sprite tracks
        // where Rook physically is on the path. ---
        private ScrollRect _mapScrollRect;
        private RectTransform _mapContent;
        private RectTransform _playerTokenRT;
        private readonly Dictionary<int, Vector2> _mapNodePositions = new(); // nodeId -> content-local position; -1 = pre-run start marker.
        private readonly HashSet<(int from, int to)> _traveledEdges = new(); // Which specific edges were actually walked, for path-line highlighting.
        private bool _mapTravelInProgress;

        private const float MapRowSpacing = 150f;
        private const float MapColumnSpacing = 230f;
        private const float MapBottomStartY = 110f; // Row 0's Y within the scroll content.
        private const float MapStartMarkerY = 20f;  // Pre-run token position, below row 0.
        private const float MapTopPadding = 90f;    // Headroom above the boss row.

        // --- Shared chrome ---
        private Text _hudText;
        private GameObject _screenContainer;
        private readonly Dictionary<string, GameObject> _screens = new();

        // --- Combat screen refs ---
        private Text _playerStatsText;
        private Image _playerHpFill;
        private Text _logText;
        private Button _endTurnButton;
        private Text _instructionsText;
        private Transform _handContainer;
        private Transform _enemyRow;
        private GameObject _resultOverlay;
        private Text _resultText;
        private Button _resultOverlayButton;
        private Text _drawPileText;
        private Text _discardPileText;

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
        private Sprite _combatBackgroundSprite;
        private Sprite _panelFrameSprite;
        private Sprite _buttonFrameSprite;
        private Sprite _mapNodeFrameSprite;
        private Sprite _playerTokenSprite;
        private readonly Dictionary<string, Sprite> _enemySpriteCache = new();
        private readonly Dictionary<CardType, Sprite> _cardIconCache = new();
        private readonly Dictionary<MapNodeType, Sprite> _mapIconCache = new();

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

        /// <summary>Combat reuses the card-type "Attack" icon (crossed swords) rather than
        /// a separate generated asset - every other node type has its own Art/MapIcons sprite.</summary>
        private Sprite GetMapIcon(MapNodeType type)
        {
            if (_mapIconCache.TryGetValue(type, out var cached)) return cached;
            var sprite = type == MapNodeType.Combat
                ? Resources.Load<Sprite>("Art/CardIcons/Attack")
                : Resources.Load<Sprite>($"Art/MapIcons/{type}");
            _mapIconCache[type] = sprite;
            return sprite;
        }

        private void Awake()
        {
            _cardFrameSprite = Resources.Load<Sprite>("Art/Cards/CardFrame");
            _combatBackgroundSprite = Resources.Load<Sprite>("Art/Backgrounds/CombatBackground");
            _panelFrameSprite = Resources.Load<Sprite>("Art/UI/PanelFrame");
            _buttonFrameSprite = Resources.Load<Sprite>("Art/UI/ButtonFrame");
            _mapNodeFrameSprite = Resources.Load<Sprite>("Art/UI/MapNodeFrame");
            _playerTokenSprite = Resources.Load<Sprite>("Art/MapIcons/PlayerToken");
            _sfxSource = gameObject.AddComponent<AudioSource>();
            _sfxSource.playOnAwake = false;
            BuildUI();
            StartNewRun();
        }

        /// <summary>Hotkeys: 1-9 play the corresponding hand card (or, if it needs a
        /// target, arm it exactly like clicking it would), Space/Enter ends the turn,
        /// Escape cancels a pending target. Only live during an ongoing combat.</summary>
        private void Update()
        {
            if (_combat == null || _combat.Outcome != CombatOutcome.Ongoing) return;
            if (!_screens.TryGetValue("Combat", out var combatScreen) || !combatScreen.activeSelf) return;
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.escapeKey.wasPressedThisFrame && _pendingCard != null)
            {
                CancelPendingTarget();
                return;
            }

            if ((kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame) && _pendingCard == null)
            {
                OnEndTurnClicked();
                return;
            }

            for (int digit = 1; digit <= 9; digit++)
            {
                if (DigitPressed(kb, digit))
                {
                    PlayHandCardAtIndex(digit - 1);
                    break;
                }
            }
        }

        private static bool DigitPressed(Keyboard kb, int digit) => digit switch
        {
            1 => kb.digit1Key.wasPressedThisFrame,
            2 => kb.digit2Key.wasPressedThisFrame,
            3 => kb.digit3Key.wasPressedThisFrame,
            4 => kb.digit4Key.wasPressedThisFrame,
            5 => kb.digit5Key.wasPressedThisFrame,
            6 => kb.digit6Key.wasPressedThisFrame,
            7 => kb.digit7Key.wasPressedThisFrame,
            8 => kb.digit8Key.wasPressedThisFrame,
            9 => kb.digit9Key.wasPressedThisFrame,
            _ => false,
        };

        /// <summary>Mirrors clicking the card at this hand slot - same targeting rules apply.</summary>
        private void PlayHandCardAtIndex(int index)
        {
            if (_cardActionInProgress) return;
            var hand = _combat.Deck.Hand;
            if (index < 0 || index >= hand.Count || index >= _handContainer.childCount) return;
            var card = hand[index];
            if (!_combat.Player.CanAfford(card.EnergyCost)) return;
            var visual = _handContainer.GetChild(index).Find("Visual") as RectTransform;
            if (visual != null) OnCardClicked(card, visual);
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
            _traveledEdges.Clear();

            ShowMapScreen();
        }

        private void RefreshHUD()
        {
            _hudText.text = $"Rook  HP {_player.CurrentHP}/{_player.MaxHP}    Fragments {_runState.Fragments}    Deck {_runState.Deck.Count}    Relics {_runState.Relics.Count}";
        }

        // ============================================================
        // Map screen
        // ============================================================

        /// <summary>Renders the WHOLE board (every row of _map.Rows), not just the next
        /// step - Fix 6's node-visibility rule means every node's type is already meant
        /// to be visible before committing to a path, so showing the full graph (with
        /// non-reachable nodes dimmed and non-interactive) is more honest to that intent
        /// than only ever showing one row at a time.</summary>
        private void ShowMapScreen()
        {
            ShowScreen("Map");
            RefreshHUD();
            _mapTravelInProgress = false;

            var titleText = _screens["Map"].transform.Find("Title").GetComponent<Text>();
            titleText.text = _map.CurrentNodeId == -1
                ? "Choose your first step into Talune:"
                : "Choose your next step:";

            for (int i = _mapContent.childCount - 1; i >= 0; i--) DestroyImmediate(_mapContent.GetChild(i).gameObject);
            _mapNodePositions.Clear();

            int rowCount = _map.Rows.Count;
            float contentHeight = MapBottomStartY + (rowCount - 1) * MapRowSpacing + MapTopPadding;
            _mapContent.sizeDelta = new Vector2(_mapContent.sizeDelta.x, contentHeight);

            // Pass 1: every node's position, before drawing anything - connectors need
            // both endpoints' positions up front. A small deterministic per-node jitter
            // (seeded off the node's own Id, so it's stable across rebuilds) keeps the
            // three lanes from looking like a rigid grid.
            _mapNodePositions[-1] = new Vector2(0f, MapStartMarkerY);
            foreach (var row in _map.Rows)
            {
                for (int i = 0; i < row.Count; i++)
                {
                    var node = row[i];
                    var rand = new System.Random(node.Id * 7919 + 13);
                    float jitter = ((float)rand.NextDouble() - 0.5f) * 46f;
                    float x = MapNodeX(i, row.Count) + jitter;
                    float y = MapBottomStartY + node.RowIndex * MapRowSpacing;
                    _mapNodePositions[node.Id] = new Vector2(x, y);
                }
            }

            // Pass 2: connector lines, drawn before node icons so icons sit on top.
            foreach (var row in _map.Rows)
            {
                foreach (var node in row)
                {
                    var from = _mapNodePositions[node.Id];
                    foreach (var targetId in node.ConnectedNodeIds)
                    {
                        var to = _mapNodePositions[targetId];
                        bool walked = _traveledEdges.Contains((node.Id, targetId));
                        Color lineColor = walked ? new Color(0.85f, 0.72f, 0.35f, 0.9f) : new Color(0.4f, 0.36f, 0.3f, 0.55f);
                        CreateMapConnector(_mapContent, from, to, lineColor, walked ? 7f : 5f);
                    }
                }
            }

            // Pass 3: node icons - available (clickable), completed/current (visited,
            // full brightness), or locked (visible per Fix 6, but dim and unclickable).
            var available = new HashSet<int>(_map.AvailableNextNodes().Select(n => n.Id));
            foreach (var row in _map.Rows)
            {
                foreach (var node in row)
                {
                    bool isAvailable = available.Contains(node.Id);
                    bool visited = node.Completed || node.Id == _map.CurrentNodeId;
                    float alpha = isAvailable || visited ? 1f : 0.4f;
                    var btn = CreateMapNodeButton(_mapContent, node, _mapNodePositions[node.Id], isAvailable, alpha);
                    var capturedNode = node;
                    btn.onClick.AddListener(() => OnMapNodeClicked(capturedNode));
                }
            }

            // Rook's token: sits at the current node, or the pre-run start marker below row 0.
            var tokenRT = CreateUIObject("PlayerToken", _mapContent);
            tokenRT.anchorMin = tokenRT.anchorMax = new Vector2(0.5f, 0f);
            tokenRT.pivot = new Vector2(0.5f, 0.5f);
            tokenRT.sizeDelta = new Vector2(56, 56);
            tokenRT.anchoredPosition = _mapNodePositions[_map.CurrentNodeId];
            var tokenImg = tokenRT.gameObject.AddComponent<Image>();
            tokenImg.sprite = _playerTokenSprite;
            tokenImg.preserveAspect = true;
            tokenImg.raycastTarget = false;
            _playerTokenRT = tokenRT;

            // Auto-scroll so Rook's current position is centered in view.
            Canvas.ForceUpdateCanvases();
            float viewportHeight = ((RectTransform)_mapScrollRect.viewport).rect.height;
            if (contentHeight > viewportHeight)
            {
                float targetY = _mapNodePositions[_map.CurrentNodeId].y;
                float maxScroll = contentHeight - viewportHeight;
                _mapScrollRect.verticalNormalizedPosition = Mathf.Clamp01((targetY - viewportHeight / 2f) / maxScroll);
            }
        }

        private Sprite _roundFillSprite;

        /// <summary>A plain white circle, generated once and cached - Unity's classic
        /// "UI/Skin/Knob.psd" builtin resource (the usual shortcut for a round fill)
        /// doesn't exist in this project's Unity version, so this stands in for it.</summary>
        private Sprite GetRoundFillSprite()
        {
            if (_roundFillSprite != null) return _roundFillSprite;
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var center = new Vector2(size / 2f, size / 2f);
            float radius = size / 2f - 1f;
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(radius - dist + 1f));
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            _roundFillSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            return _roundFillSprite;
        }

        private static float MapNodeX(int index, int count)
        {
            if (count <= 1) return 0f;
            float span = MapColumnSpacing * (count - 1);
            return -span / 2f + index * MapColumnSpacing;
        }

        private void CreateMapConnector(Transform parent, Vector2 a, Vector2 b, Color color, float thickness)
        {
            var rt = CreateUIObject("Connector", parent);
            Vector2 delta = b - a;
            float length = delta.magnitude;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(length, thickness);
            rt.anchoredPosition = a;
            rt.localEulerAngles = new Vector3(0f, 0f, angle);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
        }

        private Button CreateMapNodeButton(Transform parent, MapNode node, Vector2 pos, bool interactable, float alpha)
        {
            var rt = CreateUIObject($"Node_{node.Id}", parent);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(84, 84);
            rt.anchoredPosition = pos;

            var baseColor = NodeTypeColor.GetValueOrDefault(node.NodeType, new Color(0.3f, 0.3f, 0.3f));
            baseColor.a = alpha;
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = GetRoundFillSprite(); // Round fill, so color doesn't peek past the round frame's corners.
            img.color = baseColor;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.interactable = interactable;

            AddDecorativeFrame(rt, _mapNodeFrameSprite);

            var icon = GetMapIcon(node.NodeType);
            if (icon != null)
            {
                var iconRT = CreateUIObject("Icon", rt);
                iconRT.anchorMin = iconRT.anchorMax = new Vector2(0.5f, 0.5f);
                iconRT.sizeDelta = new Vector2(52, 52);
                iconRT.anchoredPosition = Vector2.zero;
                var iconImg = iconRT.gameObject.AddComponent<Image>();
                iconImg.sprite = icon;
                iconImg.preserveAspect = true;
                iconImg.raycastTarget = false;
                iconImg.color = new Color(1f, 1f, 1f, alpha);
            }
            return btn;
        }

        private void OnMapNodeClicked(MapNode node)
        {
            if (_mapTravelInProgress) return;
            _mapTravelInProgress = true;
            StartCoroutine(TravelThenResolve(node));
        }

        /// <summary>Slides Rook's token from wherever it currently sits to the chosen
        /// node before actually resolving it, so picking a path reads as walking the
        /// board rather than an instant menu-swap. Has to finish before ResolveMapNode
        /// navigates away, since ShowMapScreen fully rebuilds (destroying the token and
        /// every position it was tracking) on its next call.</summary>
        private IEnumerator TravelThenResolve(MapNode node)
        {
            PlaySfx("CardPlay"); // Reused as a travel "whoosh" - a distinct SFX can follow later.
            if (_map.CurrentNodeId != -1) _traveledEdges.Add((_map.CurrentNodeId, node.Id));
            if (_playerTokenRT != null && _mapNodePositions.TryGetValue(node.Id, out var targetPos))
                yield return SlideToken(_playerTokenRT, targetPos);
            _map.TryMoveTo(node.Id);
            ResolveMapNode(node);
        }

        private static IEnumerator SlideToken(RectTransform rt, Vector2 targetPos)
        {
            const float duration = 0.45f;
            Vector2 startPos = rt.anchoredPosition;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / duration);
                p = p * p * (3f - 2f * p); // Smoothstep - an eased step reads better than a linear slide.
                if (rt == null) yield break;
                rt.anchoredPosition = Vector2.Lerp(startPos, targetPos, p);
                yield return null;
            }
            if (rt != null) rt.anchoredPosition = targetPos;
        }

        private void ResolveMapNode(MapNode node)
        {
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
            _pendingCard = null;
            _pendingCardVisual = null;
            _awaitingTarget = false;

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

        /// <summary>Click handler for a hand card. If the card needs an enemy target
        /// (a Single/SecondEnemy-targeted effect) and there's more than one living
        /// enemy to choose between, it ARMS instead of playing immediately - the player
        /// then clicks the enemy they want to hit. Cards that don't need a choice (Guard/
        /// Skill/Power, or only one enemy is alive) just play straight away.</summary>
        private void OnCardClicked(CardData card, RectTransform visual)
        {
            if (_cardActionInProgress) return;

            // Clicking the already-armed card again cancels it, matching Escape's behavior.
            if (_pendingCard == card && _pendingCardVisual == visual) { CancelPendingTarget(); return; }
            if (_pendingCard != null) CancelPendingTarget();

            var livingEnemies = _combat.Enemies.Where(e => !e.IsDead).ToList();
            bool needsChoice = livingEnemies.Count > 1 && card.Effects.Any(e => e.Target == TargetType.SingleEnemy || e.Target == TargetType.SecondEnemy);

            if (needsChoice)
            {
                _pendingCard = card;
                _pendingCardVisual = visual;
                _awaitingTarget = true;
                StopAndStartTween(visual, TweenCard(visual, new Vector2(0, 24), new Vector3(1.15f, 1.15f, 1f))); // Lift and hold, so it's visibly "armed".
                _instructionsText.text = $"Choose a target for {card.CardName} - click an enemy (or press Escape / click the card again to cancel).";
                // NOT RefreshCombatUI() - that calls RebuildHand(), which destroys every
                // hand card (including the one we just armed) and creates fresh ones,
                // leaving _pendingCardVisual pointing at a destroyed object. Arming only
                // changes enemy highlighting, not hand contents, so only refresh that.
                RefreshEnemyPanels();
                return;
            }

            var target = livingEnemies.FirstOrDefault();
            StartCoroutine(PlayCardSequence(card, visual, target));
        }

        private void CancelPendingTarget()
        {
            if (_pendingCardVisual != null) StopAndStartTween(_pendingCardVisual, TweenCard(_pendingCardVisual, Vector2.zero, Vector3.one));
            _pendingCard = null;
            _pendingCardVisual = null;
            _awaitingTarget = false;
            ResetInstructionsText();
            RefreshEnemyPanels(); // Same reasoning as above - don't touch the hand here.
        }

        private void ResetInstructionsText()
        {
            _instructionsText.text = "HOW TO PLAY:  Click a card to play it (or press 1-9).  If it needs a target, click the enemy to hit.  Press SPACE/ENTER or click END TURN when done.";
        }

        private IEnumerator PlayCardSequence(CardData card, RectTransform visual, EnemyCombatant target)
        {
            _cardActionInProgress = true;
            PlaySfx("CardPlay");
            yield return PlayCardCastAnimation(visual);

            var hpBefore = _combat.Enemies.ToDictionary(e => e, e => e.CurrentHP);
            bool hasBlockEffect = card.Effects.Any(e => e.Kind == CardEffectKind.Block);
            _combat.TryPlayCard(card, target);
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

        /// <summary>Clicking an enemy only matters while a card is armed and waiting
        /// for a target (see OnCardClicked) - it confirms and plays that card on this
        /// enemy. Otherwise a click is a no-op (there's nothing to "pre-select" anymore).</summary>
        private void OnEnemyClicked(EnemyCombatant enemy)
        {
            if (enemy.IsDead || _cardActionInProgress) return;
            if (_pendingCard == null) return;

            var card = _pendingCard;
            var visual = _pendingCardVisual;
            _pendingCard = null;
            _pendingCardVisual = null;
            _awaitingTarget = false;
            ResetInstructionsText();
            StartCoroutine(PlayCardSequence(card, visual, enemy));
        }

        private void OnEndTurnClicked()
        {
            if (_cardActionInProgress) return;
            if (_pendingCard != null) CancelPendingTarget(); // Ending turn abandons an unconfirmed target.
            int hpBefore = _combat.Player.CurrentHP;
            // Snapshot BEFORE resolving - by the time EndPlayerTurn() returns, each enemy's
            // NextIntent has already been overwritten with what they'll do NEXT turn, so
            // this is the only chance to know who actually attacked just now.
            var attackers = _combat.Enemies.Where(e => !e.IsDead && e.NextIntent.Category == IntentCategory.Attack).ToList();

            _combat.EndPlayerTurn();
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

        /// <summary>Updates enemy panels only (color, text, health bar) - deliberately
        /// separate from RefreshCombatUI/RebuildHand so arming or cancelling a target
        /// doesn't tear down and recreate the hand out from under itself.</summary>
        private void RefreshEnemyPanels()
        {
            foreach (var enemy in _combat.Enemies)
            {
                if (!_enemyUI.TryGetValue(enemy, out var ui)) continue;
                // While a card is armed and waiting for a target, every living enemy is a
                // valid click target - highlight all of them, not just one "selected" one.
                bool targetable = _awaitingTarget && !enemy.IsDead;
                ui.panelImage.color = enemy.IsDead ? new Color(0.08f, 0.08f, 0.08f) : targetable ? TargetSelectedBg : TargetBg;
                if (ui.spriteImage != null) ui.spriteImage.color = enemy.IsDead ? new Color(1, 1, 1, 0.25f) : Color.white;
                ui.text.text = enemy.IsDead
                    ? $"{enemy.DisplayName}\n(defeated)"
                    : (targetable ? "◆ CLICK TO TARGET ◆\n" : "") +
                      $"{enemy.DisplayName}\nHP {enemy.CurrentHP}/{enemy.MaxHP}   Block {enemy.Block}\nWill do: {DescribeIntent(enemy.NextIntent)}";
                if (ui.hpFill != null) SetHealthBarFill(ui.hpFill, enemy.CurrentHP, enemy.MaxHP);
            }
        }

        private void RefreshCombatUI()
        {
            var p = _combat.Player;
            _playerStatsText.text = $"Rook   HP {p.CurrentHP}/{p.MaxHP}   Block {p.Block}   Energy {p.Energy}/{p.MaxEnergy}   Turn {_combat.TurnCount}" +
                (p.GetStacks(StatusEffectType.Growth) > 0 ? $"   Growth {p.GetStacks(StatusEffectType.Growth)}" : "") +
                (p.GetStacks(StatusEffectType.Thorns) > 0 ? $"   Thorns {p.GetStacks(StatusEffectType.Thorns)}" : "") +
                (p.GetStacks(StatusEffectType.Burn) > 0 ? $"   Burn {p.GetStacks(StatusEffectType.Burn)}" : "");
            SetHealthBarFill(_playerHpFill, p.CurrentHP, p.MaxHP);
            RefreshEnemyPanels();
            RebuildHand();
            _logText.text = string.Join("\n", _logLines.TakeLast(6));
            _drawPileText.text = _combat.Deck.DrawPileCount.ToString();
            _discardPileText.text = _combat.Deck.DiscardPileCount.ToString();

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

        /// <summary>Green at full HP, sliding to red as it drops - color plus the fill
        /// bar itself, so health reads at a glance instead of requiring reading numbers.</summary>
        private static void SetHealthBarFill(Image fillImg, int current, int max)
        {
            if (fillImg == null) return;
            float pct = max > 0 ? Mathf.Clamp01((float)current / max) : 0f;
            fillImg.fillAmount = pct;
            fillImg.color = Color.Lerp(new Color(0.75f, 0.15f, 0.15f), new Color(0.25f, 0.75f, 0.25f), pct);
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
                CreateCardButton(_handContainer, card, affordable, (visual) => OnCardClicked(card, visual), entranceDelay: index * 0.05f, hotkeyNumber: index < 9 ? index + 1 : null);
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

                // Health bar - sits in the band between the sprite and the stats text.
                var barBgRT = CreateUIObject("HealthBarBg", panelRT);
                barBgRT.anchorMin = new Vector2(0.10f, 0.30f);
                barBgRT.anchorMax = new Vector2(0.90f, 0.37f);
                barBgRT.offsetMin = Vector2.zero;
                barBgRT.offsetMax = Vector2.zero;
                var barBgImg = barBgRT.gameObject.AddComponent<Image>();
                barBgImg.color = new Color(0.08f, 0.08f, 0.08f, 0.9f);
                barBgImg.raycastTarget = false;
                var barFillRT = CreateUIObject("HealthBarFill", barBgRT);
                barFillRT.anchorMin = Vector2.zero;
                barFillRT.anchorMax = Vector2.one;
                barFillRT.offsetMin = new Vector2(2, 2);
                barFillRT.offsetMax = new Vector2(-2, -2);
                var hpFillImg = barFillRT.gameObject.AddComponent<Image>();
                hpFillImg.type = Image.Type.Filled;
                hpFillImg.fillMethod = Image.FillMethod.Horizontal;
                hpFillImg.fillOrigin = (int)Image.OriginHorizontal.Left;
                hpFillImg.fillAmount = 1f;
                hpFillImg.color = Color.green;
                hpFillImg.raycastTarget = false;

                var textRT = CreateUIObject("Text", panelRT);
                textRT.anchorMin = new Vector2(0, 0);
                textRT.anchorMax = new Vector2(1, sprite != null ? 0.29f : 1f);
                textRT.offsetMin = new Vector2(0, 10); // clears the panel frame's bottom border.
                textRT.offsetMax = Vector2.zero;
                var text = textRT.gameObject.AddComponent<Text>();
                text.font = BuiltinFont();
                text.fontSize = 14;
                text.alignment = TextAnchor.MiddleCenter;
                text.color = Color.white;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.raycastTarget = false;

                AddDecorativeFrame(panelRT, _panelFrameSprite); // last, so the ornate border sits on top of sprite/bar/text.

                _enemyUI[enemy] = (img, spriteImg, text, hpFillImg);
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

            if (_combatBackgroundSprite != null)
            {
                var bgRT = CreateUIObject("Background", canvasGO.transform);
                StretchFull(bgRT);
                var bgImg = bgRT.gameObject.AddComponent<Image>();
                bgImg.sprite = _combatBackgroundSprite;
                bgImg.type = Image.Type.Simple;
                bgImg.preserveAspect = false; // Cover the full canvas regardless of aspect ratio.
                bgImg.raycastTarget = false;
            }

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
            AddDecorativeFrame(hudRT, _panelFrameSprite);
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
            layout.spacing = 10;
            layout.padding = new RectOffset(20, 20, 16, 10);
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var title = CreateText(screen, "Choose your next step:", 20, TextAnchor.MiddleCenter, new Color(0.92f, 0.88f, 0.7f));
            title.name = "Title";
            AddLayoutElement(title.rectTransform, preferredHeight: 32);

            // A real scrollable board, not just "the next row": Content holds every row
            // of the run's node graph (connectors + icons + Rook's token), rebuilt fresh
            // each ShowMapScreen() call and sized/scrolled to fit however many rows the
            // Act has.
            var scrollAreaRT = CreateUIObject("MapScrollArea", screen);
            AddLayoutElement(scrollAreaRT, flexibleHeight: 1);
            var scrollRect = scrollAreaRT.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 25f;

            var viewportRT = CreateUIObject("Viewport", scrollAreaRT);
            StretchFull(viewportRT);
            var viewportImg = viewportRT.gameObject.AddComponent<Image>();
            viewportImg.color = new Color(0f, 0f, 0f, 0.02f); // Needs SOME alpha to be a drag/scroll raycast target over empty gaps between nodes.
            viewportRT.gameObject.AddComponent<RectMask2D>();
            scrollRect.viewport = viewportRT;

            var contentRT = CreateUIObject("Content", viewportRT);
            contentRT.anchorMin = new Vector2(0f, 0f);
            contentRT.anchorMax = new Vector2(1f, 0f);
            contentRT.pivot = new Vector2(0.5f, 0f);
            contentRT.anchoredPosition = Vector2.zero;
            contentRT.sizeDelta = new Vector2(0f, 400f); // Height recomputed every ShowMapScreen().
            scrollRect.content = contentRT;
            _mapContent = contentRT;
            _mapScrollRect = scrollRect;

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
            AddDecorativeFrame(instructionsRT, _panelFrameSprite);
            _instructionsText = CreateText(instructionsRT, "", 14, TextAnchor.MiddleCenter, new Color(0.85f, 0.9f, 0.85f));
            StretchFull(_instructionsText.rectTransform);
            ResetInstructionsText();

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
            AddDecorativeFrame(logPanelRT, _panelFrameSprite);
            _logText = CreateText(logPanelRT, "", 15, TextAnchor.UpperLeft);
            StretchFull(_logText.rectTransform);
            _logText.rectTransform.offsetMin += new Vector2(10, 6);
            _logText.rectTransform.offsetMax += new Vector2(-10, -6);

            var bottomBarRT = CreateUIObject("BottomBar", screen);
            AddLayoutElement(bottomBarRT, preferredHeight: 260);
            var bottomBarImg = bottomBarRT.gameObject.AddComponent<Image>();
            bottomBarImg.color = PanelBg;
            AddDecorativeFrame(bottomBarRT, _panelFrameSprite);
            var bottomLayout = bottomBarRT.gameObject.AddComponent<VerticalLayoutGroup>();
            bottomLayout.padding = new RectOffset(18, 18, 12, 10); // clears the panel frame's ~24px border.
            bottomLayout.spacing = 8;
            bottomLayout.childForceExpandWidth = true;
            bottomLayout.childForceExpandHeight = false;

            var statsRowRT = CreateUIObject("StatsRow", bottomBarRT);
            AddLayoutElement(statsRowRT, preferredHeight: 30);
            var statsLayout = statsRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            statsLayout.childForceExpandWidth = false;
            _playerStatsText = CreateText(statsRowRT, "", 18, TextAnchor.MiddleLeft);
            AddLayoutElement(_playerStatsText.rectTransform, flexibleWidth: 1, preferredHeight: 30);
            _endTurnButton = CreateButton(statsRowRT, "END TURN (Space)", OnEndTurnClicked, new Color(0.25f, 0.2f, 0.1f));
            AddLayoutElement(_endTurnButton.GetComponent<RectTransform>(), preferredWidth: 180, preferredHeight: 30);

            // Player health bar - own row, just under the stats line.
            var playerBarBgRT = CreateUIObject("PlayerHealthBarBg", bottomBarRT);
            AddLayoutElement(playerBarBgRT, preferredHeight: 10);
            var playerBarBgImg = playerBarBgRT.gameObject.AddComponent<Image>();
            playerBarBgImg.color = new Color(0.08f, 0.08f, 0.08f, 0.9f);
            var playerBarFillRT = CreateUIObject("Fill", playerBarBgRT);
            playerBarFillRT.anchorMin = Vector2.zero;
            playerBarFillRT.anchorMax = Vector2.one;
            playerBarFillRT.offsetMin = new Vector2(2, 2);
            playerBarFillRT.offsetMax = new Vector2(-2, -2);
            _playerHpFill = playerBarFillRT.gameObject.AddComponent<Image>();
            _playerHpFill.type = Image.Type.Filled;
            _playerHpFill.fillMethod = Image.FillMethod.Horizontal;
            _playerHpFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _playerHpFill.fillAmount = 1f;
            _playerHpFill.color = Color.green;

            // Hand area: draw pile | hand (fanned) | discard pile - a real deck of cards,
            // not just a floating row.
            var handAreaRT = CreateUIObject("HandArea", bottomBarRT);
            AddLayoutElement(handAreaRT, flexibleHeight: 1);
            var handAreaLayout = handAreaRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            handAreaLayout.spacing = 12;
            handAreaLayout.childAlignment = TextAnchor.MiddleCenter;
            handAreaLayout.childForceExpandWidth = false;
            handAreaLayout.childForceExpandHeight = false;

            CreatePileWidget(handAreaRT, "DRAW", out _drawPileText);

            var handRowRT = CreateUIObject("HandRow", handAreaRT);
            AddLayoutElement(handRowRT, flexibleWidth: 1, preferredHeight: 210);
            var handLayout = handRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            handLayout.spacing = -35;
            handLayout.childAlignment = TextAnchor.LowerCenter;
            handLayout.childForceExpandWidth = false;
            handLayout.childForceExpandHeight = false;
            _handContainer = handRowRT;

            CreatePileWidget(handAreaRT, "DISCARD", out _discardPileText);

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

        /// <summary>A small card-back-styled stack with a count, for the draw/discard piles -
        /// makes the hand read as drawn FROM and discarded TO somewhere physical.</summary>
        private RectTransform CreatePileWidget(Transform parent, string label, out Text countText)
        {
            var rt = CreateUIObject($"{label}Pile", parent);
            AddLayoutElement(rt, preferredWidth: 66, preferredHeight: 92);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = _cardFrameSprite;
            img.color = new Color(0.55f, 0.55f, 0.6f, 0.9f);

            var labelText = CreateText(rt, label, 10, TextAnchor.UpperCenter, new Color(0.2f, 0.15f, 0.1f));
            labelText.rectTransform.anchorMin = new Vector2(0.05f, 0.68f);
            labelText.rectTransform.anchorMax = new Vector2(0.95f, 0.95f);
            labelText.rectTransform.offsetMin = Vector2.zero;
            labelText.rectTransform.offsetMax = Vector2.zero;

            countText = CreateText(rt, "0", 22, TextAnchor.MiddleCenter, new Color(0.15f, 0.1f, 0.05f));
            countText.rectTransform.anchorMin = new Vector2(0.05f, 0.15f);
            countText.rectTransform.anchorMax = new Vector2(0.95f, 0.65f);
            countText.rectTransform.offsetMin = Vector2.zero;
            countText.rectTransform.offsetMax = Vector2.zero;
            return rt;
        }

        // --- Card button (shared by hand, rewards, and shop) ---

        private Button CreateCardButton(Transform parent, CardData card, bool affordable, UnityAction<RectTransform> onClick, float entranceDelay = 0f, int? hotkeyNumber = null)
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

            if (hotkeyNumber.HasValue)
            {
                // Bottom-left, not top-right: cards overlap by ~35px on their RIGHT edge
                // (later siblings draw over earlier ones there), so anything placed in
                // that band is only ever visible on the last card in the fan. The left
                // side is always clear.
                var hotkeyRT = CreateUIObject("HotkeyBadge", visual);
                hotkeyRT.anchorMin = new Vector2(0f, 0.08f);
                hotkeyRT.anchorMax = new Vector2(0f, 0.08f);
                hotkeyRT.pivot = new Vector2(0.5f, 0.5f);
                hotkeyRT.sizeDelta = new Vector2(26, 26);
                hotkeyRT.anchoredPosition = new Vector2(18, 14);
                var hotkeyImg = hotkeyRT.gameObject.AddComponent<Image>();
                hotkeyImg.color = new Color(0.1f, 0.1f, 0.1f, 0.85f);
                hotkeyImg.raycastTarget = false;
                var hotkeyText = CreateText(hotkeyRT, hotkeyNumber.Value.ToString(), 14, TextAnchor.MiddleCenter, new Color(0.9f, 0.85f, 0.5f));
                hotkeyText.raycastTarget = false;
                StretchFull(hotkeyText.rectTransform);
            }

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

        /// <summary>Purely decorative ornate border overlay (transparent center, 9-sliced) laid on top
        /// of an existing flat-color Image - mirrors how CardFrame overlays card art, so the underlying
        /// color-coding (button semantics, enemy target state, etc.) keeps working untouched.</summary>
        private static void AddDecorativeFrame(Transform parent, Sprite frameSprite)
        {
            if (frameSprite == null) return;
            var overlayRT = CreateUIObject("FrameOverlay", parent);
            StretchFull(overlayRT);
            var img = overlayRT.gameObject.AddComponent<Image>();
            img.sprite = frameSprite;
            img.type = Image.Type.Sliced;
            img.color = Color.white;
            img.raycastTarget = false;
        }

        private Button CreateButton(Transform parent, string label, UnityAction onClick, Color bg, int fontSize = 16)
        {
            var rt = CreateUIObject("Button", parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = bg;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            AddDecorativeFrame(rt, _buttonFrameSprite);
            var txt = CreateText(rt, label, fontSize, TextAnchor.MiddleCenter);
            StretchFull(txt.rectTransform);
            if (onClick != null) btn.onClick.AddListener(onClick);
            return btn;
        }
    }
}
